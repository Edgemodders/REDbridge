using System.Globalization;
using System.Text.RegularExpressions;
using StreamJsonRpc;
using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// The game's on-screen texts (LocKeys), read offline from each language's <c>onscreens.json</c> in
/// the base game and Phantom Liberty, for REDcode's LocKey browser (C5 V4) and its MCP tools. A
/// project's own onscreens files win over the game's, as ArchiveXL makes them do in game: the ones
/// its <c>.xl</c> files name under <c>localization: onscreens:</c>, and any onscreens file under
/// <c>source/archive</c> in an onscreens folder below its language folder, as the game keeps them.
/// </summary>
public sealed partial class BridgeApi
{
    /// <summary>
    /// One entry: the LocKey number (as text, since it can pass 2^53), the secondary key, both texts, where
    /// it comes from, its file and its row in that file. The file is relative to the project for a project
    /// entry, and the game path of the onscreens file for a game one.
    /// </summary>
    public sealed record LocEntry(string Key, string? SecondaryKey, string Female, string Male, string Source, string? File = null, int? Index = null);
    public sealed record LocSearch(int Total, IReadOnlyList<LocEntry> Items);
    public sealed record LocLanguage(string Language, bool Base, bool Ep1);

    private sealed class LocIndex
    {
        public required List<LocEntry> Entries { get; init; }
        public required Dictionary<ulong, LocEntry> ByKey { get; init; }
        public required Dictionary<string, LocEntry> BySecondary { get; init; }
    }

    /// <summary>The languages the game ships text for (WolvenKit's list, as the folder names under localization).</summary>
    private static readonly string[] LocLanguages =
    [
        "en-us", "ar-ar", "cz-cz", "de-de", "es-es", "es-mx", "fr-fr", "hu-hu", "it-it", "jp-jp",
        "kr-kr", "pl-pl", "pt-br", "ru-ru", "th-th", "tr-tr", "ua-ua", "zh-cn", "zh-tw",
    ];

    private static readonly Dictionary<string, LocIndex> GameLoc = new();
    private static readonly Dictionary<string, (DateTime Written, List<LocEntry> Entries)> ProjectLocFiles = new(StringComparer.OrdinalIgnoreCase);

    // ---- methods --------------------------------------------------------------------------

    /// <summary>The languages with on-screen texts in the loaded archives, and which halves (base, Phantom Liberty) have them.</summary>
    [JsonRpcMethod("loc.languages")]
    public IReadOnlyList<LocLanguage> LocLanguagesList()
    {
        RequireGame();
        return LocLanguages
            .Select(l => new LocLanguage(l, _host.Archives.Lookup(OnscreensPath("base", l)).HasValue, _host.Archives.Lookup(OnscreensPath("ep1", l)).HasValue))
            .Where(l => l.Base || l.Ep1)
            .ToList();
    }

    /// <summary>
    /// Entries matching the query. "LocKey#123" or a number finds that LocKey; "LocKey#name" a secondary
    /// key. Otherwise every word must appear, any case, in the secondary key or either text; exact
    /// secondary keys come first. With <paramref name="projectDir"/>, the project's entries replace the
    /// game's ones with the same key and are searched too.
    /// </summary>
    [JsonRpcMethod("loc.search")]
    public LocSearch LocSearchMethod(string query, string language = "en-us", int limit = 200, string? projectDir = null)
    {
        var all = Merged(language, projectDir);
        var q = query.Trim();
        List<LocEntry> hits;
        if (TryKey(q, out var key))
        {
            hits = all.Where(e => e.Key == key.ToString(CultureInfo.InvariantCulture)).ToList();
        }
        else
        {
            var bare = q.StartsWith("LocKey#", StringComparison.OrdinalIgnoreCase) ? q[7..] : q;
            var words = bare.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            hits = words.Length == 0 ? [] : all
                .Where(e => words.All(w =>
                    (e.SecondaryKey?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    e.Female.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                    e.Male.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(e => string.Equals(e.SecondaryKey, bare, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
        }
        return new LocSearch(hits.Count, hits.Take(Math.Clamp(limit, 1, 20000)).ToList());
    }

    /// <summary>Entries by key: "LocKey#123", "123" or a secondary key, with or without "LocKey#". null for a key with no text.</summary>
    [JsonRpcMethod("loc.get")]
    public IReadOnlyDictionary<string, LocEntry?> LocGet(IReadOnlyList<string> keys, string language = "en-us", string? projectDir = null)
    {
        var game = GameIndex(language);
        var project = projectDir is null ? [] : ProjectEntries(projectDir, language);
        var result = new Dictionary<string, LocEntry?>();
        foreach (var raw in keys)
        {
            var k = raw.Trim();
            var bare = k.StartsWith("LocKey#", StringComparison.OrdinalIgnoreCase) ? k[7..] : k;
            LocEntry? found;
            if (TryKey(k, out var number))
            {
                var text = number.ToString(CultureInfo.InvariantCulture);
                found = project.LastOrDefault(e => e.Key == text) ?? game.ByKey.GetValueOrDefault(number);
            }
            else
            {
                found = project.LastOrDefault(e => e.SecondaryKey == bare) ?? game.BySecondary.GetValueOrDefault(bare);
            }
            result[raw] = found;
        }
        return result;
    }

    /// <summary>A project's onscreens files for a language: relative to the project, whether it exists yet, and the .xl that names it (null for one found by its language folder).</summary>
    public sealed record LocProjectFile(string File, bool Exists, string? Xl);
    public sealed record LocNewFile(string File, bool Created, string? Xl, bool XlWritten);

    /// <summary>The project's onscreens files for a language: those its .xl files name (existing or not), then any .json under source/archive in an onscreens folder below the language's, as the game keeps them.</summary>
    [JsonRpcMethod("loc.projectFiles")]
    public IReadOnlyList<LocProjectFile> LocProjectFiles(string projectDir, string language = "en-us") => ProjectLocFilesOf(projectDir, language);

    /// <summary>
    /// Makes sure the project has an onscreens file for the language and returns it: the first
    /// existing one; else a file an .xl already names, written empty; else
    /// <c>source/archive/&lt;mod&gt;/localization/&lt;language&gt;.json</c>, written empty and added to
    /// an .xl under <c>localization: onscreens:</c> (a new <c>&lt;mod&gt;.archive.xl</c> in
    /// source/resources when the project has none).
    /// </summary>
    [JsonRpcMethod("loc.newFile")]
    public LocNewFile LocNewFileMethod(string projectDir, string modName, string language = "en-us")
    {
        var known = ProjectLocFilesOf(projectDir, language);
        if (known.FirstOrDefault(f => f.Exists) is { } existing)
        {
            return new LocNewFile(existing.File, false, existing.Xl, false);
        }
        if (known.FirstOrDefault(f => f.Xl is not null) is { } named)
        {
            WriteCr2w(Path.Combine(projectDir, named.File), EmptyOnscreens());
            return new LocNewFile(named.File, true, named.Xl, false);
        }
        var safe = SafeName(modName);
        if (safe.Length == 0)
        {
            safe = "mod";
        }
        var archivePath = $"{safe}\\localization\\{language}.json";
        var file = Path.Combine("source", "archive", safe, "localization", $"{language}.json");
        WriteCr2w(Path.Combine(projectDir, file), EmptyOnscreens());
        var resources = Path.Combine(projectDir, "source", "resources");
        var xl = Directory.Exists(resources) ? Directory.EnumerateFiles(resources, "*.xl", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
        xl ??= Path.Combine(resources, $"{safe}.archive.xl");
        var text = File.Exists(xl) ? File.ReadAllText(xl) : "";
        Directory.CreateDirectory(Path.GetDirectoryName(xl)!);
        File.WriteAllText(xl, XlWithOnscreens(text, language, archivePath));
        return new LocNewFile(file, true, Path.GetRelativePath(projectDir, xl), true);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static RedBaseClass EmptyOnscreens() =>
        new JsonResource { Root = new CHandle<ISerializable>(new localizationPersistenceOnScreenEntries()) };

    /// <summary>
    /// A project's localization files of one kind ("onscreens" or "subtitles") for a language: those its
    /// .xl files name under <c>localization: &lt;kind&gt;:</c>, then any .json under source/archive in a
    /// &lt;kind&gt; folder below the language's, as the game keeps them.
    /// </summary>
    private static List<LocProjectFile> ProjectLocFilesOf(string projectDir, string language, string kind = "onscreens")
    {
        var archive = Path.Combine(projectDir, "source", "archive");
        var resources = Path.Combine(projectDir, "source", "resources");
        var files = new List<LocProjectFile>();
        if (Directory.Exists(resources))
        {
            foreach (var xl in Directory.EnumerateFiles(resources, "*.xl", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var p in XlLocFiles(File.ReadAllText(xl), kind, language))
                {
                    var full = Path.GetFullPath(Path.Combine(archive, p.Replace('/', '\\').Replace('\\', Path.DirectorySeparatorChar)));
                    files.Add(new LocProjectFile(Path.GetRelativePath(projectDir, full), File.Exists(full), Path.GetRelativePath(projectDir, xl)));
                }
            }
        }
        if (Directory.Exists(archive))
        {
            // As the game keeps them: under <language>\<kind>\ (onscreens or subtitles). Other localization JSON beside them is not of this kind.
            files.AddRange(Directory.EnumerateFiles(archive, "*.json", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var dirs = Path.GetRelativePath(archive, f).Split(Path.DirectorySeparatorChar).SkipLast(1).ToList();
                    var at = dirs.FindIndex(s => s.Equals(language, StringComparison.OrdinalIgnoreCase));
                    return at >= 0 && dirs.Skip(at + 1).Any(s => s.Equals(kind, StringComparison.OrdinalIgnoreCase));
                })
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(f => new LocProjectFile(Path.GetRelativePath(projectDir, f), true, null)));
        }
        return files.DistinctBy(f => f.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The .xl text with "<c>language: path</c>" under <c>localization: onscreens:</c>, adding the
    /// blocks it lacks. Lines are inserted, never rewritten, so the rest of the file stays as it was.
    /// </summary>
    internal static string XlWithOnscreens(string xl, string language, string archivePath) => XlWithLoc(xl, "onscreens", language, archivePath);

    /// <summary>The .xl text with "<c>language: path</c>" under <c>localization: &lt;kind&gt;:</c> (onscreens or subtitles), lines inserted only.</summary>
    internal static string XlWithLoc(string xl, string kind, string language, string archivePath)
    {
        var nl = xl.Contains("\r\n") ? "\r\n" : "\n";
        var lines = xl.Length == 0 ? new List<string>() : xl.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        static int Indent(string l) => l.Length - l.TrimStart().Length;
        var loc = lines.FindIndex(l => Indent(l) == 0 && l.TrimEnd() == "localization:");
        var entry = $"{language}: {archivePath}";
        if (loc < 0)
        {
            lines.AddRange(["localization:", $"  {kind}:", $"    {entry}"]);
        }
        else
        {
            // The localization block: the lines after it that are indented or blank.
            var end = loc + 1;
            while (end < lines.Count && (lines[end].Trim().Length == 0 || Indent(lines[end]) > 0))
            {
                end++;
            }
            var on = lines.FindIndex(loc + 1, end - loc - 1, l => l.Trim() == $"{kind}:");
            if (on < 0)
            {
                lines.InsertRange(loc + 1, [$"  {kind}:", $"    {entry}"]);
            }
            else
            {
                lines.Insert(on + 1, $"{new string(' ', Indent(lines[on]) + 2)}{entry}");
            }
        }
        return string.Join(nl, lines) + nl;
    }

    private static string OnscreensPath(string root, string language) => $"{root}\\localization\\{language}\\onscreens\\onscreens.json";

    /// <summary>"LocKey#123" or "123": the number. A secondary key, even one of digits after "LocKey#", is matched as a number first.</summary>
    private static bool TryKey(string text, out ulong key)
    {
        var bare = text.StartsWith("LocKey#", StringComparison.OrdinalIgnoreCase) ? text[7..] : text;
        return ulong.TryParse(bare, NumberStyles.None, CultureInfo.InvariantCulture, out key);
    }

    /// <summary>The game's entries in a language, Phantom Liberty's after the base game's, loaded once.</summary>
    private LocIndex GameIndex(string language)
    {
        lock (GameLoc)
        {
            if (GameLoc.TryGetValue(language, out var known))
            {
                return known;
            }
            RequireGame();
            var entries = new List<LocEntry>();
            foreach (var root in new[] { "base", "ep1" })
            {
                var file = _host.Archives.Lookup(OnscreensPath(root, language));
                if (file.HasValue)
                {
                    entries.AddRange(EntriesOf(ReadCr2w(file.Value).RootChunk, root, OnscreensPath(root, language)));
                }
            }
            var byKey = new Dictionary<ulong, LocEntry>();
            var bySecondary = new Dictionary<string, LocEntry>();
            foreach (var e in entries)
            {
                var key = ulong.Parse(e.Key, CultureInfo.InvariantCulture);
                if (key != 0)
                {
                    byKey[key] = e;
                }
                if (!string.IsNullOrEmpty(e.SecondaryKey))
                {
                    bySecondary[e.SecondaryKey] = e;
                }
            }
            _host.Log.Info($"Localization {language}: {entries.Count:N0} entries");
            return GameLoc[language] = new LocIndex { Entries = entries, ByKey = byKey, BySecondary = bySecondary };
        }
    }

    private static IEnumerable<LocEntry> EntriesOf(RedBaseClass? root, string source, string file) =>
        root is JsonResource { Root.Chunk: localizationPersistenceOnScreenEntries list }
            ? list.Entries.Select((e, i) => new LocEntry(((ulong)e.PrimaryKey).ToString(CultureInfo.InvariantCulture), NullIfEmpty(e.SecondaryKey), (string?)e.FemaleVariant ?? "", (string?)e.MaleVariant ?? "", source, file, i))
            : [];

    private static string? NullIfEmpty(CString s) => string.IsNullOrEmpty((string?)s) ? null : (string)s!;

    /// <summary>The game's entries with the project's laid over them: a project entry replaces the game's with the same LocKey or secondary key.</summary>
    private List<LocEntry> Merged(string language, string? projectDir)
    {
        var game = GameIndex(language);
        if (projectDir is null)
        {
            return game.Entries;
        }
        var project = ProjectEntries(projectDir, language);
        if (project.Count == 0)
        {
            return game.Entries;
        }
        var keys = project.Where(e => e.Key != "0").Select(e => e.Key).ToHashSet();
        var secondary = project.Where(e => e.SecondaryKey is not null).Select(e => e.SecondaryKey!).ToHashSet();
        return project.Concat(game.Entries.Where(e => !keys.Contains(e.Key) && (e.SecondaryKey is null || !secondary.Contains(e.SecondaryKey)))).ToList();
    }

    /// <summary>
    /// The project's onscreens entries in a language: the files its .xl files name for that language,
    /// then onscreens files under source/archive in an onscreens folder below that language's. Each
    /// file is read again only when it changes.
    /// </summary>
    private List<LocEntry> ProjectEntries(string projectDir, string language)
    {
        var entries = new List<LocEntry>();
        foreach (var file in ProjectLocFilesOf(projectDir, language).Where(f => f.Exists).Select(f => Path.Combine(projectDir, f.File)))
        {
            var written = File.GetLastWriteTimeUtc(file);
            lock (ProjectLocFiles)
            {
                if (!ProjectLocFiles.TryGetValue(file, out var cached) || cached.Written != written)
                {
                    List<LocEntry> read;
                    try
                    {
                        using var fs = File.OpenRead(file);
                        read = EntriesOf(_host.Parser.ReadRed4File(fs)?.RootChunk, "project", Path.GetRelativePath(projectDir, file)).ToList();
                    }
                    catch
                    {
                        read = []; // not a CR2W onscreens file
                    }
                    cached = ProjectLocFiles[file] = (written, read);
                }
                entries.AddRange(cached.Entries);
            }
        }
        return entries;
    }

    /// <summary>
    /// The onscreens paths an ArchiveXL file names for a language: the "<c>language: path</c>" lines,
    /// or "<c>language:</c>" followed by "<c>- path</c>" items, under <c>localization: onscreens:</c>.
    /// A small reader for that one block, not a YAML parser.
    /// </summary>
    internal static IEnumerable<string> XlOnscreens(string xl, string language) => XlLocFiles(xl, "onscreens", language);

    /// <summary>The paths an ArchiveXL file names for a language under <c>localization: &lt;kind&gt;:</c> (onscreens or subtitles).</summary>
    internal static IEnumerable<string> XlLocFiles(string xl, string kind, string language)
    {
        var inOnscreens = false;
        var inLanguage = false;
        var onscreensIndent = -1;
        var blockStart = $"{kind}:";
        foreach (var raw in xl.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#'))
            {
                continue;
            }
            var indent = line.Length - line.TrimStart().Length;
            if (text.StartsWith(blockStart, StringComparison.Ordinal))
            {
                inOnscreens = true;
                inLanguage = false;
                onscreensIndent = indent;
                continue;
            }
            if (!inOnscreens)
            {
                continue;
            }
            if (indent <= onscreensIndent)
            {
                inOnscreens = false;
                continue;
            }
            var pair = Regex.Match(text, @"^([A-Za-z]{2}-[A-Za-z]{2})\s*:\s*(.*)$");
            if (pair.Success)
            {
                inLanguage = pair.Groups[1].Value.Equals(language, StringComparison.OrdinalIgnoreCase);
                var value = pair.Groups[2].Value.Trim();
                if (inLanguage)
                {
                    // "path", or a flow list "[a, b]".
                    var items = value.StartsWith('[') ? value.Trim('[', ']').Split(',') : [value];
                    foreach (var item in items.Select(i => i.Trim().Trim('"', '\'')).Where(i => i.Length > 0))
                    {
                        yield return item;
                    }
                }
                continue;
            }
            if (inLanguage && text.StartsWith('-'))
            {
                var value = text[1..].Trim().Trim('"', '\'');
                if (value.Length > 0)
                {
                    yield return value;
                }
            }
        }
    }
}
