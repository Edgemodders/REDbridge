using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamJsonRpc;

namespace REDbridge;

/// <summary>
/// The shared key store (bridge 0.18.0, K4a; design in REDcode's docs/design/keys.md): the player's
/// AI service keys in <c>%APPDATA%\Edgemodders\keys\&lt;service&gt;.txt</c>, one file per service,
/// as REDnode's <c>tools/keys/keys.mjs</c> keeps them (Windows DPAPI for the current user, written as
/// PowerShell's <c>ConvertFrom-SecureString</c> writes it: one line of hex). REDbridge decrypts them
/// in .NET, with no PowerShell. An environment variable wins when set, as in keys.mjs.
///
/// Off unless REDbridge was started with <c>--keys</c>: REDcode, the keys' keeper in VS Code, and
/// MCP servers that read keys start it so. Any other client (REDnode's host, which lends its bridge
/// to packages and in-game servers) cannot read a key through REDbridge. A key is never logged.
/// </summary>
public sealed partial class BridgeApi
{
    /// <summary>A known service: its name, the start every key has (when it has one), its variable.</summary>
    private sealed record KeyService(string Name, string? Prefix, string Env);

    private static readonly Dictionary<string, KeyService> KeyServices = new()
    {
        ["anthropic"] = new("Anthropic", "sk-ant-", "ANTHROPIC_API_KEY"),
        ["gemini"] = new("Google Gemini", null, "GEMINI_API_KEY"),
        ["openai"] = new("OpenAI", "sk-", "OPENAI_API_KEY"),
        ["elevenlabs"] = new("ElevenLabs", null, "ELEVENLABS_API_KEY"),
        ["tripo"] = new("Tripo", null, "TRIPO_API_KEY"),
        ["uthana"] = new("Uthana", null, "UTHANA_API_KEY"),
    };

    private static readonly Regex ServiceId = new("^[a-z0-9][a-z0-9-]{0,39}$");
    private static readonly Regex KeyText = new("^[\\x21-\\x7e]{1,4096}$");

    /// <summary>A tool that read a key, and when (ISO 8601).</summary>
    public sealed record KeyUse(string Tool, string At);

    /// <summary>
    /// What a Keys view shows for a service, never the key: whether it is set and where from
    /// ("env" or "file"), the variable that wins, its last 4 characters, whether its file cannot be
    /// decrypted by this Windows user, when the file was saved, and which tools have read it.
    /// </summary>
    public sealed record KeyInfo(string Service, string Name, bool Set, string? Source, string Env, string? Last4, bool Unreadable, string? Updated, IReadOnlyList<KeyUse> UsedBy);

    /// <summary>A key read: the key (null when unset or unreadable) and where it came from.</summary>
    public sealed record KeyRead(string Service, string? Key, string? Source, bool Unreadable);

    private readonly Dictionary<string, (string Stamp, string? Key)> _keyCache = new();

    private static string KeysDir() =>
        Environment.GetEnvironmentVariable("EDGEMODDERS_KEYS_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Edgemodders", "keys");

    private static string KeyFile(string service) => Path.Combine(KeysDir(), service + ".txt");

    private void RequireKeys()
    {
        if (!_host.KeysEnabled)
        {
            throw new InvalidOperationException("REDbridge's key methods are off: they answer only a REDbridge started with --keys, as REDcode starts it");
        }
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Shared keys are kept with Windows DPAPI: set the environment variable instead on this system");
        }
    }

    private static string CheckService(string? service) =>
        service is not null && ServiceId.IsMatch(service)
            ? service
            : throw new ArgumentException($"A key's service id is 1 to 40 lowercase letters, digits or dashes, not {JsonSerializer.Serialize(service)}");

    private static string EnvName(string service) =>
        KeyServices.TryGetValue(service, out var s) ? s.Env : service.ToUpperInvariant().Replace('-', '_') + "_API_KEY";

    private static string? FromEnv(string service) => Environment.GetEnvironmentVariable(EnvName(service))?.Trim() is { Length: > 0 } v ? v : null;

    private static string? Stamp(string file)
    {
        var f = new FileInfo(file);
        return f.Exists ? $"{f.LastWriteTimeUtc.Ticks}:{f.Length}" : null;
    }

    /// <summary>The file's key, cached until the file changes; null when unset or unreadable here.</summary>
    private (string? Key, bool OnDisk) FileKey(string service)
    {
        lock (_keyCache)
        {
            return FileKeyLocked(service);
        }
    }

    private (string? Key, bool OnDisk) FileKeyLocked(string service)
    {
        var file = KeyFile(service);
        var stamp = Stamp(file);
        if (stamp is null)
        {
            return (null, false);
        }
        if (_keyCache.TryGetValue(service, out var hit) && hit.Stamp == stamp)
        {
            return (hit.Key, true);
        }
        string? key = null;
        try
        {
            var hex = File.ReadAllText(file).Trim();
            var plain = ProtectedData.Unprotect(Convert.FromHexString(hex), null, DataProtectionScope.CurrentUser);
            // ConvertTo-SecureString holds UTF-16 text.
            key = Encoding.Unicode.GetString(plain).Trim() is { Length: > 0 } k ? k : null;
            CryptographicOperations.ZeroMemory(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or IOException)
        {
            // Made by another Windows user or PC, or not a key file: it counts as unset, so the tool asks again.
        }
        _keyCache[service] = (stamp, key);
        return (key, true);
    }

    private static Dictionary<string, Dictionary<string, string>> ReadUsed()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(Path.Combine(KeysDir(), "used-by.json"))) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void WriteAtomic(string file, string text)
    {
        Directory.CreateDirectory(KeysDir());
        var tmp = $"{file}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, file, true);
    }

    private static void RecordUse(string service, string? tool)
    {
        if (tool?.Trim() is not { Length: > 0 } t)
        {
            return;
        }
        try
        {
            var used = ReadUsed();
            if (!used.TryGetValue(service, out var tools))
            {
                used[service] = tools = [];
            }
            tools[t.Length > 64 ? t[..64] : t] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            WriteAtomic(Path.Combine(KeysDir(), "used-by.json"), JsonSerializer.Serialize(used, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // The record is for the Keys views only: a key read never fails for it.
        }
    }

    private KeyInfo InfoOf(string service)
    {
        var env = FromEnv(service);
        var (fileKey, onDisk) = FileKey(service);
        var key = env ?? fileKey;
        var file = new FileInfo(KeyFile(service));
        var used = ReadUsed().TryGetValue(service, out var u) ? u : [];
        return new KeyInfo(
            service,
            KeyServices.TryGetValue(service, out var s) ? s.Name : service,
            key is not null,
            env is not null ? "env" : key is not null ? "file" : null,
            EnvName(service),
            key is { Length: > 8 } ? key[^4..] : null,
            env is null && onDisk && fileKey is null,
            onDisk ? file.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") : null,
            used.Select(p => new KeyUse(p.Key, p.Value)).OrderByDescending(x => x.At, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// A service's key: the environment variable when set, else the shared file. Null when neither
    /// is set or the file cannot be decrypted by this Windows user. <paramref name="tool"/> names the
    /// tool reading it, for the used-by record.
    /// </summary>
    [JsonRpcMethod("keys.get")]
    public KeyRead KeysGet(string service, string? tool = null)
    {
        RequireKeys();
        CheckService(service);
        var env = FromEnv(service);
        var (fileKey, onDisk) = env is null ? FileKey(service) : (null, false);
        var key = env ?? fileKey;
        if (key is not null)
        {
            RecordUse(service, tool);
        }
        return new KeyRead(service, key, env is not null ? "env" : key is not null ? "file" : null, env is null && onDisk && fileKey is null);
    }

    /// <summary>Saves a key in the shared store for every tool, replacing any, after checking it looks like one for the service.</summary>
    [JsonRpcMethod("keys.set")]
    public KeyInfo KeysSet(string service, string key)
    {
        RequireKeys();
        CheckService(service);
        var k = key?.Trim() ?? "";
        if (!KeyText.IsMatch(k))
        {
            throw new ArgumentException("A key is one line of letters, digits and signs, with no spaces");
        }
        if (KeyServices.TryGetValue(service, out var s) && s.Prefix is { } prefix && !k.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{s.Name} keys start with {prefix}");
        }
        var plain = Encoding.Unicode.GetBytes(k);
        var hex = Convert.ToHexString(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(plain);
        var file = KeyFile(service);
        lock (_keyCache)
        {
            WriteAtomic(file, hex + "\r\n");
            _keyCache[service] = (Stamp(file)!, k);
        }
        _host.Log.Info($"Keys: {service} set");
        return InfoOf(service);
    }

    /// <summary>Removes a service's file (not its environment variable). True when there was one.</summary>
    [JsonRpcMethod("keys.delete")]
    public bool KeysDelete(string service)
    {
        RequireKeys();
        CheckService(service);
        lock (_keyCache)
        {
            _keyCache.Remove(service);
        }
        var file = KeyFile(service);
        if (!File.Exists(file))
        {
            return false;
        }
        File.Delete(file);
        _host.Log.Info($"Keys: {service} removed");
        return true;
    }

    /// <summary>What a Keys view shows for one service, never the key.</summary>
    [JsonRpcMethod("keys.info")]
    public KeyInfo KeysInfo(string service)
    {
        RequireKeys();
        return InfoOf(CheckService(service));
    }

    /// <summary>The services to show, the known ones and then any other with a file in the store, each as keys.info answers it.</summary>
    [JsonRpcMethod("keys.list")]
    public IReadOnlyList<KeyInfo> KeysList()
    {
        RequireKeys();
        var ids = new List<string>(KeyServices.Keys);
        if (Directory.Exists(KeysDir()))
        {
            foreach (var f in Directory.EnumerateFiles(KeysDir(), "*.txt").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (f is not null && ServiceId.IsMatch(f) && !ids.Contains(f))
                {
                    ids.Add(f);
                }
            }
        }
        return ids.Select(InfoOf).ToList();
    }
}
