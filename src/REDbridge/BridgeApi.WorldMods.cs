using System.Globalization;
using System.Text;
using WolvenKit.Common.FNV1A;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;
using YamlDotNet.RepresentationModel;

namespace REDbridge;

/// <summary>
/// Sectors that mods add to the world (issue #23, for REDcode's sector layers). ArchiveXL merges
/// the streaming blocks an <c>.xl</c> file lists under <c>streaming: blocks:</c> into the world's, so
/// every sector those blocks describe streams in beside the game's. sector.find's
/// <c>sources: ["mods"]</c> reads them from the game's archive\pc\mod, and <c>"project"</c> from a
/// project's own <c>.xl</c> files and loose files.
/// </summary>
public sealed partial class BridgeApi
{
    private sealed record SectorSource(List<SectorEntry> Sectors, Dictionary<string, List<SectorVariant>> Variants, List<string> Warnings);

    private (string Stamp, SectorSource Found)? _modSectors;

    /// <summary>
    /// The sectors installed mods add. Kept until a file in archive\pc\mod changes. Each mod
    /// archive is opened only to find a block or a sector, and closed again before the call ends.
    /// </summary>
    private SectorSource ModSectors()
    {
        var dir = Path.Combine(_host.GameDir!, "archive", "pc", "mod");
        if (!Directory.Exists(dir))
        {
            return new SectorSource(new(), new(), new());
        }
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xl", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".archive", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var stamp = string.Join(";", files.Select(f => { var i = new FileInfo(f); return $"{f}|{i.Length}|{i.LastWriteTimeUtc.Ticks}"; }));
        if (_modSectors is { } cached && cached.Stamp == stamp)
        {
            return cached.Found;
        }
        var archives = files.Where(f => f.EndsWith(".archive", StringComparison.OrdinalIgnoreCase)).ToList();
        using var opened = new OpenedArchives(_host);
        var found = new SectorSource(new(), new(), new());
        foreach (var xl in files.Where(f => f.EndsWith(".xl", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetRelativePath(dir, xl);
            // foo.archive.xl comes with foo.archive: look there first, then in every mod archive.
            var own = xl.EndsWith(".archive.xl", StringComparison.OrdinalIgnoreCase) ? xl[..^3] : null;
            var order = (own is not null && File.Exists(own) ? new[] { own } : Array.Empty<string>())
                .Concat(archives.Where(a => !string.Equals(a, own, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            AddBlocks(found, xl, name, "mod", path => opened.Read(path, order));
        }
        _modSectors = (stamp, found);
        return found;
    }

    /// <summary>
    /// The sectors a project adds: its <c>.xl</c> files under source\resources, with their blocks
    /// and sectors read from source\archive (loose files), as REDcode lays a project out.
    /// </summary>
    private SectorSource ProjectSectors(string project)
    {
        var folder = Path.GetFullPath(project);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"Project folder not found: {project}");
        }
        var resources = Path.Combine(folder, "source", "resources");
        var loose = Path.Combine(folder, "source", "archive");
        var found = new SectorSource(new(), new(), new());
        if (!Directory.Exists(resources))
        {
            return found;
        }
        using var mods = Directory.Exists(loose) ? ModFiles.Open(_host, null, loose) : null;
        foreach (var xl in Directory.EnumerateFiles(resources, "*.xl", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            AddBlocks(found, xl, Path.GetRelativePath(folder, xl), "project", path => mods?.Read(path) is { } ms ? (ms, "source\\archive") : null);
        }
        return found;
    }

    /// <summary>Reads an .xl's streaming blocks and adds every sector they list.</summary>
    private void AddBlocks(SectorSource found, string xl, string name, string source, Func<string, (MemoryStream Stream, string Where)?> read)
    {
        List<string> blocks;
        try
        {
            blocks = BlocksOf(File.ReadAllText(xl, Encoding.UTF8));
        }
        catch (Exception e)
        {
            found.Warnings.Add($"{name}: not read as YAML ({e.Message})");
            return;
        }
        foreach (var blockPath in blocks)
        {
            var got = read(blockPath);
            if (got is null)
            {
                found.Warnings.Add($"{name}: its block {blockPath} is in no mod archive");
                continue;
            }
            using var stream = got.Value.Stream;
            if (_host.Parser.ReadRed4File(stream)?.RootChunk is not worldStreamingBlock sb)
            {
                found.Warnings.Add($"{name}: {blockPath} is not a streaming block");
                continue;
            }
            var game = AllSectors();
            var gamePaths = new HashSet<string>(game.Select(g => g.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var d in sb.Descriptors)
            {
                var path = d.Data.DepotPath.GetResolvedText() ?? ((ulong)d.Data.DepotPath).ToString(CultureInfo.InvariantCulture);
                var m = GridName.Match(path);
                int[]? grid = m.Success ? [int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)] : null;
                var sector = read(path);
                string? archive = sector?.Where;
                sector?.Stream.Dispose();
                if (sector is null && _host.Archives.Lookup(path) is { HasValue: true } inGame)
                {
                    archive = inGame.Value.GetArchive().Name;
                }
                if (archive is null)
                {
                    found.Warnings.Add($"{name}: its sector {path} is in no archive");
                }
                var entry = new SectorEntry(path, KindOf(d.Category), grid, (byte)d.Level, BoundsOf(d.StreamingBox), blockPath, archive, gamePaths.Contains(path), d.Variants.Count, source, name);
                found.Sectors.Add(entry);
                found.Variants[path + "|" + source + "|" + name] = VariantsOf(d);
            }
        }
    }

    /// <summary>
    /// The block paths an .xl lists under <c>streaming: blocks:</c>: a list, or one path. Other
    /// parts of the file (sectors, resources, ...) are left alone.
    /// </summary>
    internal static List<string> BlocksOf(string yaml)
    {
        var result = new List<string>();
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is not YamlMappingNode root
                || !root.Children.TryGetValue(new YamlScalarNode("streaming"), out var streaming)
                || streaming is not YamlMappingNode sm
                || !sm.Children.TryGetValue(new YamlScalarNode("blocks"), out var blocks))
            {
                continue;
            }
            switch (blocks)
            {
                case YamlScalarNode one when !string.IsNullOrWhiteSpace(one.Value):
                    result.Add(one.Value!.Trim().Replace('/', '\\'));
                    break;
                case YamlSequenceNode many:
                    result.AddRange(many.Children.OfType<YamlScalarNode>().Where(n => !string.IsNullOrWhiteSpace(n.Value)).Select(n => n.Value!.Trim().Replace('/', '\\')));
                    break;
            }
        }
        return result;
    }

    /// <summary>Mod archives opened on demand, by path, for one call; all closed when it ends.</summary>
    private sealed class OpenedArchives(BridgeHost host) : IDisposable
    {
        private readonly Dictionary<string, WolvenKit.RED4.Archive.Archive?> _open = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A resource's bytes from the first of <paramref name="order"/> that has it, with that archive's name.</summary>
        public (MemoryStream Stream, string Where)? Read(string path, IReadOnlyList<string> order)
        {
            var hash = FNV1A64HashAlgorithm.HashString(path.Replace('/', '\\'));
            foreach (var file in order)
            {
                var ar = Get(file);
                if (ar is not null && ar.Files.TryGetValue(hash, out var entry))
                {
                    var ms = new MemoryStream();
                    entry.Extract(ms);
                    ms.Position = 0;
                    return (ms, Path.GetFileName(file));
                }
            }
            return null;
        }

        private WolvenKit.RED4.Archive.Archive? Get(string file)
        {
            if (!_open.TryGetValue(file, out var ar))
            {
                host.Hashes.Loaded.GetAwaiter().GetResult();
                try
                {
                    if (new ArchiveReader().ReadArchive(file, host.Hashes, out ar) != EFileReadErrorCodes.NoError)
                    {
                        ar?.Dispose();
                        ar = null;
                    }
                }
                catch
                {
                    ar = null;
                }
                _open[file] = ar;
            }
            return ar;
        }

        public void Dispose()
        {
            foreach (var ar in _open.Values)
            {
                ar?.Dispose();
            }
            _open.Clear();
        }
    }
}
