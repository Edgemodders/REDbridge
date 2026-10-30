using WolvenKit.Common.FNV1A;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;

namespace REDbridge;

/// <summary>
/// Where a resource is looked up before the game's archives: a mod's .archive anywhere on disk
/// (<c>archive</c>), or a folder of loose files laid out as in an archive, such as a REDcode
/// project's source\archive (<c>root</c>). mesh.info, mesh.infoMany and sector.build take them, so
/// an editor can read a mod's mesh or entity and bake a scene that places one. Asked for by
/// EDGEbuilder (issue #12).
///
/// The archive is opened for one call and closed after it, not kept between calls: an open archive
/// is memory mapped, which would stop REDcode or WolvenKit from packing the mod again. Reading a
/// mod archive's index takes a few milliseconds.
/// </summary>
public sealed partial class BridgeApi
{
    private sealed class ModFiles : IDisposable
    {
        private readonly WolvenKit.RED4.Archive.Archive? _archive;
        private readonly string? _root;

        private ModFiles(WolvenKit.RED4.Archive.Archive? archive, string? root)
        {
            _archive = archive;
            _root = root;
        }

        public bool IsEmpty => _archive is null && _root is null;

        /// <summary>Names what was searched, for error messages: "edgefab_chair.archive", "the folder ...".</summary>
        public string Where => string.Join(" or ", new[] { _archive?.Name, _root is null ? null : $"the folder {_root}" }.Where(s => s is not null));

        public static ModFiles Open(BridgeHost host, string? archive, string? root)
        {
            WolvenKit.RED4.Archive.Archive? ar = null;
            if (!string.IsNullOrEmpty(archive))
            {
                if (!File.Exists(archive))
                {
                    throw new FileNotFoundException($"Archive not found: {archive}", archive);
                }
                host.Hashes.Loaded.GetAwaiter().GetResult();
                var result = new ArchiveReader().ReadArchive(archive, host.Hashes, out ar);
                if (result != WolvenKit.RED4.Archive.IO.EFileReadErrorCodes.NoError)
                {
                    ar?.Dispose();
                    throw new InvalidDataException($"Could not read {archive}: {result}");
                }
            }
            string? folder = null;
            if (!string.IsNullOrEmpty(root))
            {
                folder = Path.GetFullPath(root);
                if (!Directory.Exists(folder))
                {
                    ar?.Dispose();
                    throw new DirectoryNotFoundException($"Folder not found: {root}");
                }
            }
            return new ModFiles(ar, folder);
        }

        /// <summary>The resource's bytes from the archive, then the folder; null when neither has it.</summary>
        public MemoryStream? Read(string pathOrHash)
        {
            var isHash = TryParseHash(pathOrHash, out var key);
            var path = pathOrHash.Replace('/', '\\');
            if (_archive is not null && _archive.Files.TryGetValue(isHash ? key : FNV1A64HashAlgorithm.HashString(path), out var file))
            {
                var ms = new MemoryStream();
                file.Extract(ms);
                ms.Position = 0;
                return ms;
            }
            if (_root is not null && !isHash)
            {
                var full = Path.GetFullPath(Path.Combine(_root, path));
                // A path with .. must not reach outside the folder.
                if (full.StartsWith(_root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    return new MemoryStream(File.ReadAllBytes(full));
                }
            }
            return null;
        }

        public bool Has(string pathOrHash)
        {
            using var ms = Read(pathOrHash);
            return ms is not null;
        }

        public void Dispose() => _archive?.Dispose();
    }

    /// <summary>
    /// Reads a resource as CR2W: from <paramref name="mods"/> first, then the game's archives. The
    /// name is the game's own name for a game file, else the path as given.
    /// </summary>
    private (string Name, CR2WFile File) ReadResource(string pathOrHash, ModFiles? mods)
    {
        if (mods is not null && !mods.IsEmpty)
        {
            using var ms = mods.Read(pathOrHash);
            if (ms is not null)
            {
                var name = pathOrHash.Replace('/', '\\');
                return (name, _host.Parser.ReadRed4File(ms) ?? throw new InvalidDataException($"'{name}' is not a CR2W file"));
            }
            if (_host.GameDir is null)
            {
                throw new FileNotFoundException($"'{pathOrHash}' is not in {mods.Where}, and no game is loaded");
            }
            if (!(TryParseHash(pathOrHash, out var k) ? _host.Archives.Lookup(k) : _host.Archives.Lookup(pathOrHash.Replace('/', '\\'))).HasValue)
            {
                throw new FileNotFoundException($"'{pathOrHash}' is not in {mods.Where}, nor in the game's archives");
            }
        }
        var file = Find(pathOrHash);
        return (file.Name, ReadCr2w(file));
    }
}
