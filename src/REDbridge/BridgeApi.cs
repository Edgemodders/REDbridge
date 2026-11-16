using System.Globalization;
using StreamJsonRpc;
using WolvenKit.Common;
using WolvenKit.Common.Conversion;
using WolvenKit.Common.FNV1A;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.CR2W.JSON;
using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// The JSON-RPC surface. Method names are dotted groups, as in the REDcode design.
/// Hashes travel as decimal strings, because JavaScript numbers cannot hold 64 bits.
/// </summary>
public sealed partial class BridgeApi
{
    private readonly BridgeHost _host;

    public BridgeApi(BridgeHost host) => _host = host;

    // ---- bridge ---------------------------------------------------------------------------

    public sealed record BridgeInfo(string Version, string WolvenKit, string? GameDir, bool GameLoaded, int Archives, long Files);

    [JsonRpcMethod("bridge.info")]
    public BridgeInfo Info()
    {
        var loaded = _host.GameDir is not null;
        var archives = loaded ? _host.Archives.GetGameArchives().ToList() : [];
        return new BridgeInfo(BridgeHost.Version, BridgeHost.WolvenKitVersion, _host.GameDir, loaded,
            archives.Count, archives.Sum(a => (long)a.Files.Count));
    }

    // ---- archive --------------------------------------------------------------------------

    public sealed record ArchiveInfo(string Name, string Source, int Files, string Path);
    public sealed record FileInfoDto(string Hash, string Name, uint Size, string Archive);
    public sealed record FileList(long Total, IReadOnlyList<FileInfoDto> Items);

    [JsonRpcMethod("archive.load")]
    public BridgeInfo Load(string gameDir)
    {
        _host.LoadGame(gameDir);
        return Info();
    }

    [JsonRpcMethod("archive.archives")]
    public IReadOnlyList<ArchiveInfo> Archives()
    {
        RequireGame();
        return _host.Archives.GetGameArchives()
            .Select(a => new ArchiveInfo(a.Name, a.Source.ToString(), a.Files.Count, a.ArchiveAbsolutePath))
            .OrderBy(a => a.Name)
            .ToList();
    }

    /// <summary>
    /// Pages through the files of one archive, or of every game archive, optionally filtered by a
    /// name prefix and by an ending (<paramref name="extension"/>, such as ".anims", any case; bridge
    /// 0.14.0, #17). <paramref name="archive"/> is a name or an absolute path; names repeat across
    /// the base game and Phantom Liberty, so the path is the unambiguous form.
    /// </summary>
    [JsonRpcMethod("archive.list")]
    public FileList List(string? archive = null, string? prefix = null, int offset = 0, int limit = 500, string? extension = null)
    {
        RequireGame();
        limit = Math.Clamp(limit, 1, 5000);
        var archives = _host.Archives.GetGameArchives()
            .Where(a => archive is null || a.Name == archive || string.Equals(a.ArchiveAbsolutePath, archive, StringComparison.OrdinalIgnoreCase));
        var query = archives.SelectMany(a => a.Files.Values.Select(f => (file: f, archive: a.Name)));
        if (!string.IsNullOrEmpty(prefix))
        {
            query = query.Where(x => x.file.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(extension))
        {
            var dot = extension.StartsWith('.') ? extension : "." + extension;
            query = query.Where(x => x.file.Name.EndsWith(dot, StringComparison.OrdinalIgnoreCase));
        }
        var all = query.OrderBy(x => x.file.Name, StringComparer.Ordinal).ToList();
        var page = all.Skip(offset).Take(limit).Select(x => ToDto(x.file, x.archive)).ToList();
        return new FileList(all.Count, page);
    }

    [JsonRpcMethod("archive.search")]
    public FileList Search(string query, int limit = 200)
    {
        RequireGame();
        limit = Math.Clamp(limit, 1, 5000);
        var hits = _host.Archives.Search(query, ArchiveManagerScope.Basegame);
        return new FileList(hits.Count, hits.Take(limit).Select(f => ToDto(f, f.GetArchive().Name)).ToList());
    }

    public sealed record FolderInfo(string Name, string Path, int Files);
    public sealed record TreeResult(string Path, IReadOnlyList<FolderInfo> Folders, IReadOnlyList<FileInfoDto> Files);

    /// <summary>
    /// One level of the archive folder tree: the subfolders of <paramref name="path"/> with their
    /// recursive file counts, and the files directly in it. An empty path is the root.
    /// </summary>
    [JsonRpcMethod("archive.tree")]
    public TreeResult Tree(string path = "")
    {
        RequireGame();
        var folder = _host.Tree.Find(path) ?? throw new DirectoryNotFoundException($"'{path}' is not a folder in the loaded archives");
        var normalized = path.Replace('/', '\\').Trim('\\');
        var folders = folder.Folders.Select(kv => new FolderInfo(kv.Key, normalized.Length == 0 ? kv.Key : $"{normalized}\\{kv.Key}", kv.Value.Count)).ToList();
        var files = folder.Files.OrderBy(x => x.file.Name, StringComparer.OrdinalIgnoreCase).Select(x => ToDto(x.file, x.archive)).ToList();
        return new TreeResult(normalized, folders, files);
    }

    public sealed record ExtractResult(string File, long Bytes);

    [JsonRpcMethod("archive.extract")]
    public ExtractResult Extract(string path, string outFile)
    {
        var file = Find(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        using var fs = File.Create(outFile);
        file.Extract(fs);
        return new ExtractResult(Path.GetFullPath(outFile), fs.Length);
    }

    public sealed record ExtractItem(string Path, string OutFile);
    public sealed record ExtractManyResult(int Extracted, long Bytes, IReadOnlyList<string> Failed);

    /// <summary>Extracts many files in one call. Files that fail are listed, the rest still succeed.</summary>
    [JsonRpcMethod("archive.extractMany")]
    public ExtractManyResult ExtractMany(IReadOnlyList<ExtractItem> items)
    {
        var failed = new List<string>();
        long bytes = 0;
        var count = 0;
        foreach (var item in items)
        {
            try
            {
                bytes += Extract(item.Path, item.OutFile).Bytes;
                count++;
            }
            catch (Exception e)
            {
                _host.Log.Warning($"extract {item.Path}: {e.Message}");
                failed.Add(item.Path);
            }
        }
        return new ExtractManyResult(count, bytes, failed);
    }

    public sealed record ModArchiveInfo(string Path, string Name, int Files, IReadOnlyList<FileInfoDto> Items);

    /// <summary>
    /// Reads one .archive file anywhere on disk, for example a mod in a mod manager's library, and
    /// lists the resources inside it. Works without a game loaded: only the hash tables are needed
    /// to resolve names. Two mods that both contain a resource with the same hash conflict in the game.
    /// </summary>
    [JsonRpcMethod("archive.openMod")]
    public ModArchiveInfo OpenMod(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Archive not found: {path}", path);
        }
        _host.Hashes.Loaded.GetAwaiter().GetResult();
        var reader = new ArchiveReader();
        var result = reader.ReadArchive(path, _host.Hashes, out var archive);
        if (result != WolvenKit.RED4.Archive.IO.EFileReadErrorCodes.NoError)
        {
            throw new InvalidDataException($"Could not read {path}: {result}");
        }
        try
        {
            var items = archive.Files.Values
                .Select(f => new FileInfoDto(f.Key.ToString(CultureInfo.InvariantCulture), f.Name, f.Size, archive.Name))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList();
            return new ModArchiveInfo(path, archive.Name, items.Count, items);
        }
        finally
        {
            (archive as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Extracts resources from one .archive file anywhere on disk, such as a mod in EDGEdeck's library,
    /// as archive.extractMany does for the game's archives. Each item names a resource by path or
    /// hash. No game needed. Resources that fail or are not in the archive are listed, the rest succeed.
    /// </summary>
    [JsonRpcMethod("archive.extractFromMod")]
    public ExtractManyResult ExtractFromMod(string archivePath, IReadOnlyList<ExtractItem> items)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"Archive not found: {archivePath}", archivePath);
        }
        _host.Hashes.Loaded.GetAwaiter().GetResult();
        var reader = new ArchiveReader();
        var result = reader.ReadArchive(archivePath, _host.Hashes, out var archive);
        if (result != WolvenKit.RED4.Archive.IO.EFileReadErrorCodes.NoError)
        {
            throw new InvalidDataException($"Could not read {archivePath}: {result}");
        }
        try
        {
            var failed = new List<string>();
            long bytes = 0;
            var count = 0;
            foreach (var item in items)
            {
                var key = TryParseHash(item.Path, out var h) ? h : FNV1A64HashAlgorithm.HashString(item.Path.Replace('/', '\\'));
                if (!archive.Files.TryGetValue(key, out var file))
                {
                    _host.Log.Warning($"extractFromMod {item.Path}: not in {Path.GetFileName(archivePath)}");
                    failed.Add(item.Path);
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(item.OutFile))!);
                    using var fs = File.Create(item.OutFile);
                    file.Extract(fs);
                    bytes += fs.Length;
                    count++;
                }
                catch (Exception e)
                {
                    _host.Log.Warning($"extractFromMod {item.Path}: {e.Message}");
                    failed.Add(item.Path);
                }
            }
            return new ExtractManyResult(count, bytes, failed);
        }
        finally
        {
            (archive as IDisposable)?.Dispose();
        }
    }

    // ---- cr2w -----------------------------------------------------------------------------

    public sealed record JsonResult(string Path, string Hash, string? File, long Bytes, string? Json);

    /// <summary>
    /// Converts a game file to WolvenKit's CR2W JSON. With <paramref name="outFile"/> the JSON is
    /// written there and only the path comes back; without it the JSON text is returned inline.
    /// </summary>
    [JsonRpcMethod("cr2w.toJson")]
    public JsonResult ToJson(string path, string? outFile = null)
    {
        var file = Find(path);
        var cr2w = ReadCr2w(file);
        var json = RedJsonSerializer.Serialize(new RedFileDto(cr2w));
        if (outFile is null)
        {
            return new JsonResult(file.Name, Hash(file.Key), null, json.Length, json);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, json);
        return new JsonResult(file.Name, Hash(file.Key), Path.GetFullPath(outFile), new FileInfo(outFile).Length, null);
    }

    /// <summary>Converts a CR2W file on disk (for example one in a project's source/archive) to JSON.</summary>
    [JsonRpcMethod("cr2w.fileToJson")]
    public JsonResult FileToJson(string file, string? outFile = null)
    {
        using var fs = File.OpenRead(file);
        var cr2w = _host.Parser.ReadRed4File(fs) ?? throw new InvalidDataException($"'{file}' is not a CR2W file");
        var json = RedJsonSerializer.Serialize(new RedFileDto(cr2w));
        if (outFile is null)
        {
            return new JsonResult(file, "", null, json.Length, json);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, json);
        return new JsonResult(file, "", Path.GetFullPath(outFile), new FileInfo(outFile).Length, null);
    }

    /// <summary>Writes a CR2W file from WolvenKit JSON, given inline or as a file path.</summary>
    [JsonRpcMethod("cr2w.fromJson")]
    public ExtractResult FromJson(string outFile, string? json = null, string? jsonFile = null)
    {
        json ??= jsonFile is not null ? File.ReadAllText(jsonFile) : throw new ArgumentException("json or jsonFile is required");
        var dto = RedJsonSerializer.Deserialize<RedFileDto>(json) ?? throw new InvalidDataException("JSON is not a WolvenKit file");
        var cr2w = dto.Data ?? throw new InvalidDataException("JSON has no Data");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        using var fs = File.Create(outFile);
        using var writer = new CR2WWriter(fs) { LoggerService = _host.Log };
        writer.WriteFile(cr2w);
        return new ExtractResult(Path.GetFullPath(outFile), fs.Length);
    }

    // ---- texture --------------------------------------------------------------------------

    [JsonRpcMethod("texture.toPng")]
    public ExtractResult ToPng(string path, string outFile)
    {
        var file = Find(path);
        return SavePng(ReadCr2w(file), outFile);
    }

    /// <summary>Converts a texture file on disk (for example one in a project's source/archive) to PNG.</summary>
    [JsonRpcMethod("texture.fileToPng")]
    public ExtractResult FileToPng(string file, string outFile)
    {
        using var fs = File.OpenRead(file);
        var cr2w = _host.Parser.ReadRed4File(fs) ?? throw new InvalidDataException($"'{file}' is not a CR2W file");
        return SavePng(cr2w, outFile);
    }

    /// <summary>
    /// Converts a plain image (TGA, DDS, TIFF, BMP, JPG, PNG), such as an exported texture in a
    /// project's source/raw, to PNG for viewing. No flip: these files are already the right way up,
    /// since the export flipped them.
    /// </summary>
    [JsonRpcMethod("texture.imageToPng")]
    public ExtractResult ImageToPng(string file, string outFile)
    {
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"File not found: {file}", file);
        }
        using var image = RedImage.LoadFromFile(file) ?? throw new InvalidDataException($"WolvenKit cannot read {Path.GetFileName(file)} as an image");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        image.SaveToPNG(outFile);
        return new ExtractResult(Path.GetFullPath(outFile), new FileInfo(outFile).Length);
    }

    /// <summary>
    /// Writes the texture as a PNG the right way up. Game textures are stored bottom-up, and
    /// WolvenKit's export and viewers flip them vertically (except LUTs); so does the game when
    /// it samples them, which is why atlas UV rectangles only line up on the flipped image.
    /// </summary>
    private static ExtractResult SavePng(CR2WFile cr2w, string outFile)
    {
        using var image = RedImage.FromRedFile(cr2w);
        var isLut = cr2w.RootChunk is CBitmapTexture { Setup: { } setup } && setup.Group == Enums.GpuWrapApieTextureGroup.TEXG_Generic_LUT;
        if (!isLut)
        {
            image.FlipV();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        image.SaveToPNG(outFile);
        return new ExtractResult(Path.GetFullPath(outFile), new FileInfo(outFile).Length);
    }

    // ---- project --------------------------------------------------------------------------

    public sealed record PackResult(string Archive, long Bytes, int Files, List<string> Warnings);

    /// <summary>
    /// Packs a folder (a project's source/archive) into outDir/modName.archive through WolvenKit's
    /// packer, the same archive WolvenKit's Pack writes. Warnings list the paths with capital letters
    /// or spaces, which WolvenKit warns about because the game may not resolve them.
    /// </summary>
    [JsonRpcMethod("project.pack")]
    public PackResult Pack(string dir, string outDir, string modName)
    {
        var input = new DirectoryInfo(dir);
        if (!input.Exists)
        {
            throw new DirectoryNotFoundException($"Folder not found: {dir}");
        }
        if (string.IsNullOrWhiteSpace(modName))
        {
            throw new ArgumentException("modName is required", nameof(modName));
        }
        Directory.CreateDirectory(outDir);
        var files = input.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var warnings = files
            .Select(f => Path.GetRelativePath(input.FullName, f.FullName))
            .Where(r => r.Any(char.IsUpper) || r.Any(char.IsWhiteSpace))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
        _host.Hashes.Loaded.GetAwaiter().GetResult();
        if (!_host.ModTools.Pack(input, new DirectoryInfo(outDir), modName))
        {
            throw new InvalidOperationException($"WolvenKit could not pack {dir}; see the bridge log");
        }
        var archive = Path.Combine(Path.GetFullPath(outDir), $"{modName}.archive");
        return new PackResult(archive, new FileInfo(archive).Length, files.Count, warnings);
    }

    // ---- hash -----------------------------------------------------------------------------

    public sealed record HashResult(string Value, string Hash, string Hex);

    [JsonRpcMethod("hash.fnv1a64")]
    public HashResult Fnv1a64(string value)
    {
        var h = FNV1A64HashAlgorithm.HashString(value);
        return new HashResult(value, Hash(h), $"0x{h:X16}");
    }

    public sealed record ResolveResult(string Hash, string? Path, bool Known);

    [JsonRpcMethod("hash.resolve")]
    public ResolveResult Resolve(string hash)
    {
        var key = ParseHash(hash);
        var path = _host.Hashes.Get(key);
        return new ResolveResult(Hash(key), path, path is not null);
    }

    // ---- helpers --------------------------------------------------------------------------

    private void RequireGame()
    {
        if (_host.GameDir is null)
        {
            throw new InvalidOperationException("No game loaded. Start with --game or call archive.load first.");
        }
    }

    /// <summary>Accepts a resource path (base\...) or a decimal or 0x hash.</summary>
    private IGameFile Find(string pathOrHash)
    {
        RequireGame();
        var byHash = TryParseHash(pathOrHash, out var key)
            ? _host.Archives.Lookup(key)
            : _host.Archives.Lookup(pathOrHash.Replace('/', '\\'));
        return byHash.HasValue ? byHash.Value : throw new FileNotFoundException($"'{pathOrHash}' is not in the loaded archives");
    }

    private CR2WFile ReadCr2w(IGameFile file)
    {
        using var ms = new MemoryStream();
        file.Extract(ms);
        ms.Position = 0;
        return _host.Parser.ReadRed4File(ms) ?? throw new InvalidDataException($"'{file.Name}' is not a CR2W file");
    }

    private static FileInfoDto ToDto(IGameFile f, string archive) => new(Hash(f.Key), f.Name, f.Size, archive);

    private static string Hash(ulong key) => key.ToString(CultureInfo.InvariantCulture);

    private static ulong ParseHash(string s) =>
        TryParseHash(s, out var v) ? v : throw new ArgumentException($"'{s}' is not a hash");

    private static bool TryParseHash(string s, out ulong value)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
        return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
