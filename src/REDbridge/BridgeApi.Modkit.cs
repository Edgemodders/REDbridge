using System.Text.Json;
using StreamJsonRpc;
using WolvenKit.Common;
using WolvenKit.Common.Model;
using WolvenKit.Common.Model.Arguments;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;
using static WolvenKit.RED4.Types.Enums;

namespace REDbridge;

/// <summary>
/// Export and import over WolvenKit's Modkit, for REDcode's C4 S3 and S4. Both work on a project's
/// layout: a file under source/archive exports to the same relative path under source/raw, and a
/// raw file imports back to source/archive. The results list what was written, because one export
/// can write several files (a mesh with its material textures, for instance).
/// </summary>
public sealed partial class BridgeApi
{
    /// <summary>The few export settings worth exposing; everything else keeps WolvenKit's defaults.</summary>
    public sealed record ExportOptions(
        string? TextureFormat = null,
        bool? MeshBinary = null,
        bool? MeshMaterials = null,
        string? MaterialTextureFormat = null,
        bool? MeshLodFilter = null);

    /// <summary>Mesh is set when an import wrote a .mesh (bridge 0.9.0); Texture when an image import wrote a .xbm, as read back from it.</summary>
    public sealed record ModkitResult(bool Ok, List<string> Written, MeshImport? Mesh = null, TextureSetup? Texture = null);

    /// <summary>A texture's setup, as an .xbm keeps it (the enum names WolvenKit and the game use).</summary>
    public sealed record TextureSetup(string Group, string Compression, string RawFormat, bool IsGamma, bool HasMipchain, bool IsStreamable);

    /// <summary>The texture presets <c>project.import</c> takes, as WolvenKit's import sets up these texture groups.</summary>
    private static readonly Dictionary<string, GpuWrapApieTextureGroup> TexturePresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["color"] = GpuWrapApieTextureGroup.TEXG_Generic_Color,
        ["normal"] = GpuWrapApieTextureGroup.TEXG_Generic_Normal,
        ["grayscale"] = GpuWrapApieTextureGroup.TEXG_Generic_Grayscale,
    };

    /// <summary>A material the GLB gives its submeshes, by the submesh indices (render chunks) that use it.</summary>
    public sealed record MeshMaterialUse(string Name, IReadOnlyList<int> Submeshes);

    /// <summary>
    /// What a GLB import made of a .mesh. <see cref="Materials"/> are the GLB's material names per submesh,
    /// in the order WolvenKit writes the submeshes (by node name); <see cref="MeshMaterials"/> are the
    /// mesh's own material entries, which the import keeps; <see cref="Adjusted"/> says how appearances
    /// were sized to the new submesh count.
    /// </summary>
    public sealed record MeshImport(
        bool FromTemplate,
        string? Template,
        int Submeshes,
        IReadOnlyList<MeshMaterialUse> Materials,
        IReadOnlyList<string> MeshMaterials,
        IReadOnlyList<string> Appearances,
        IReadOnlyList<string> Adjusted);

    /// <summary>
    /// Exports a file under archiveDir (a project's source/archive) into rawDir (its source/raw), at
    /// the same relative path, the way WolvenKit's Export does: glTF for meshes, an image for
    /// textures, WAV for sounds, and so on. Mesh materials go into rawDir as well, as WolvenKit's
    /// command line puts them in its output folder. Written lists the files created or changed, relative to rawDir.
    /// </summary>
    [JsonRpcMethod("project.export")]
    public ModkitResult Export(string file, string archiveDir, string rawDir, ExportOptions? options = null)
    {
        var source = new FileInfo(file);
        if (!source.Exists)
        {
            throw new FileNotFoundException($"File not found: {file}", file);
        }
        var archive = new DirectoryInfo(archiveDir);
        if (!source.FullName.StartsWith(archive.FullName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{file} is not under {archiveDir}");
        }
        // WolvenKit dumps the raw buffers of any other type, which nobody wants in source/raw.
        var ext = source.Extension.TrimStart('.');
        if (!Enum.TryParse<ECookedFileFormat>(ext, true, out _))
        {
            throw new ArgumentException($".{ext} files have no export format; open them as JSON instead. Exportable: {string.Join(", ", Enum.GetNames<ECookedFileFormat>().Select(n => "." + n))}");
        }
        Directory.CreateDirectory(rawDir);
        // WolvenKit remembers what it exported in this process and skips it the next time.
        _host.ModTools.ClearFileLookup();
        var args = new GlobalExportArgs();
        var o = options ?? new ExportOptions();
        if (o.TextureFormat is { } tf)
        {
            args.Get<XbmExportArgs>().UncookExtension = ParseExtension(tf);
        }
        var mesh = args.Get<MeshExportArgs>();
        if (o.MeshBinary is { } bin)
        {
            mesh.isGLBinary = bin;
        }
        if (o.MeshMaterials is { } mats)
        {
            mesh.withMaterials = mats;
        }
        if (o.MaterialTextureFormat is { } mtf)
        {
            mesh.MaterialUncookExtension = ParseExtension(mtf);
        }
        if (o.MeshLodFilter is { } lod)
        {
            mesh.LodFilter = lod;
        }
        mesh.MaterialRepo = Path.GetFullPath(rawDir);
        args.Get<GeneralExportArgs>().MaterialRepositoryPath = Path.GetFullPath(rawDir);

        var before = Snapshot(rawDir);
        var ok = _host.ModTools.Export(source, args, archive, new DirectoryInfo(rawDir));
        return new ModkitResult(ok, Changed(rawDir, before));
    }

    /// <summary>
    /// Imports a raw file (under rawDir, a project's source/raw) back into archiveDir (its
    /// source/archive), the way WolvenKit's Import does. With keep (the default, as in WolvenKit),
    /// an existing game file at the target keeps its settings: a texture its group and compression,
    /// a mesh its materials. Written lists the files created or changed, relative to archiveDir.
    /// <para>
    /// <paramref name="texture"/> sets up the .xbm an image import writes, and wins over an existing
    /// texture's setup: a preset name ("color", "normal", "grayscale") or a texture group name, or an
    /// object <c>{ group, compression, rawFormat, isGamma, hasMipchain, isStreamable }</c> where every
    /// field is optional and the rest come from the group's defaults (issue #8). Without it, a new
    /// texture gets WolvenKit's plain defaults, as before.
    /// </para>
    /// </summary>
    [JsonRpcMethod("project.import")]
    public async Task<ModkitResult> Import(string file, string rawDir, string archiveDir, bool keep = true, string? template = null, JsonElement? texture = null)
    {
        var source = new FileInfo(file);
        if (!source.Exists)
        {
            throw new FileNotFoundException($"File not found: {file}", file);
        }
        var raw = new DirectoryInfo(rawDir);
        if (!source.FullName.StartsWith(raw.FullName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{file} is not under {rawDir}");
        }
        var ext = source.Extension.TrimStart('.');
        if (!Enum.TryParse<ERawFileFormat>(ext, true, out _))
        {
            throw new ArgumentException($".{ext} files cannot be imported. Importable: {string.Join(", ", Enum.GetNames<ERawFileFormat>().Select(n => "." + n))}");
        }
        Directory.CreateDirectory(archiveDir);
        var args = new GlobalImportArgs();
        args.Get<CommonImportArgs>().Keep = keep;
        args.Get<XbmImportArgs>().Keep = keep;
        args.Get<GltfImportArgs>().Keep = keep;

        var relative = Path.GetRelativePath(raw.FullName, source.FullName);

        // A GLB becomes a .mesh only over an existing one: WolvenKit's importer says "Direct mesh importing
        // is not implemented". For a new asset, the template (a game path or a .mesh on disk) is copied to
        // the target first. A mesh already at the target is imported over as it is, so later re-imports
        // keep what was done to it since, such as materials set by EDGEmat.
        var isModel = ext.Equals("glb", StringComparison.OrdinalIgnoreCase) || ext.Equals("gltf", StringComparison.OrdinalIgnoreCase);
        var meshTarget = Path.Combine(archiveDir, Path.ChangeExtension(relative, ".mesh"));
        var fromTemplate = false;
        if (isModel && template is not null && !File.Exists(meshTarget))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(meshTarget)!);
            if (File.Exists(template))
            {
                File.Copy(template, meshTarget);
            }
            else
            {
                using var fs = File.Create(meshTarget);
                Find(template).Extract(fs);
            }
            fromTemplate = true;
        }
        if (isModel)
        {
            if (!File.Exists(meshTarget))
            {
                throw new InvalidOperationException($"There is no mesh at {Path.ChangeExtension(relative, ".mesh")} to import {source.Name} over. For a new asset, pass a template: a game or project .mesh to start from.");
            }
            // The importer only writes over an existing mesh when Keep is on.
            args.Get<GltfImportArgs>().Keep = true;
        }

        // WolvenKit reads the existing texture's setup itself only when it runs as its command line;
        // its app fills the import dialog from that file first. Do what the dialog does.
        var existing = Path.Combine(archiveDir, Path.ChangeExtension(relative, ".xbm"));
        STextureGroupSetup? kept = null;
        var given = texture is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } t ? TextureArgs(t) : null;
        if (given is not null && !IsImage(ext))
        {
            throw new ArgumentException($"texture applies to an image import, not to .{ext}");
        }
        if (given is not null)
        {
            given.Keep = keep;
            args.Register(given);
        }
        else if (keep && IsImage(ext) && File.Exists(existing))
        {
            using var fs = File.OpenRead(existing);
            if (_host.Parser.ReadRed4File(fs)?.RootChunk is CBitmapTexture { Setup: { } setup })
            {
                kept = setup;
                args.Register(new XbmImportArgs(setup) { Keep = keep });
            }
        }

        var before = Snapshot(archiveDir);
        var ok = await _host.ModTools.Import(new RedRelativePath(raw, relative), args, new DirectoryInfo(archiveDir));
        if (ok && kept is not null)
        {
            RestoreTextureFlags(existing, kept);
        }
        var mesh = ok && isModel ? AfterMeshImport(source.FullName, meshTarget, fromTemplate, template) : null;
        var written = Changed(archiveDir, before);
        if (fromTemplate && !written.Contains(Path.ChangeExtension(relative, ".mesh"), StringComparer.OrdinalIgnoreCase))
        {
            written.Add(Path.ChangeExtension(relative, ".mesh"));
        }
        var textureSetup = ok && IsImage(ext) && written.Contains(Path.ChangeExtension(relative, ".xbm"), StringComparer.OrdinalIgnoreCase) ? ReadTextureSetup(existing) : null;
        return new ModkitResult(ok, written, mesh, textureSetup);
    }

    /// <summary>
    /// Import settings from the texture argument: a preset or group name, or an object whose fields
    /// override the defaults of its group (TEXG_Generic_Color when it names none), the defaults
    /// WolvenKit's import gives each group (CommonFunctions.TextureSetupFromTextureGroup).
    /// </summary>
    private static XbmImportArgs TextureArgs(JsonElement texture)
    {
        static GpuWrapApieTextureGroup Group(string name) =>
            TexturePresets.TryGetValue(name, out var preset) ? preset
            : ParseEnum<GpuWrapApieTextureGroup>(name, "texture group", $"or a preset: {string.Join(", ", TexturePresets.Keys)}");

        if (texture.ValueKind == JsonValueKind.String)
        {
            return CommonFunctions.TextureSetupFromTextureGroup(Group(texture.GetString()!));
        }
        if (texture.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("texture is a preset name (color, normal, grayscale) or an object { group, compression, rawFormat, isGamma, hasMipchain, isStreamable }");
        }
        var known = new[] { "group", "compression", "rawFormat", "isGamma", "hasMipchain", "isStreamable" };
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in texture.EnumerateObject())
        {
            if (!known.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"texture has no field '{p.Name}'; fields: {string.Join(", ", known)}");
            }
            fields[p.Name] = p.Value;
        }
        var args = CommonFunctions.TextureSetupFromTextureGroup(fields.TryGetValue("group", out var g) ? Group(g.GetString() ?? "") : GpuWrapApieTextureGroup.TEXG_Generic_Color);
        if (fields.TryGetValue("compression", out var c))
        {
            args.Compression = ParseEnum<ETextureCompression>(c.GetString() ?? "", "compression");
        }
        if (fields.TryGetValue("rawFormat", out var r))
        {
            args.RawFormat = ParseEnum<ETextureRawFormat>(r.GetString() ?? "", "raw format");
        }
        if (fields.TryGetValue("isGamma", out var gamma))
        {
            args.IsGamma = gamma.GetBoolean();
        }
        if (fields.TryGetValue("hasMipchain", out var mips))
        {
            args.GenerateMipMaps = mips.GetBoolean();
        }
        if (fields.TryGetValue("isStreamable", out var streamable))
        {
            args.IsStreamable = streamable.GetBoolean();
        }
        return args;
    }

    private static T ParseEnum<T>(string value, string what, string? more = null) where T : struct, Enum =>
        !int.TryParse(value, out _) && Enum.TryParse<T>(value, true, out var e)
            ? e
            : throw new ArgumentException($"Unknown {what} '{value}'; use one of {string.Join(", ", Enum.GetNames<T>())}{(more is null ? "" : $", {more}")}");

    private TextureSetup? ReadTextureSetup(string xbm)
    {
        using var fs = File.OpenRead(xbm);
        return _host.Parser.ReadRed4File(fs)?.RootChunk is CBitmapTexture { Setup: { } s }
            ? new TextureSetup(s.Group.ToString(), s.Compression.ToString(), s.RawFormat.ToString(), s.IsGamma, s.HasMipchain, s.IsStreamable)
            : null;
    }

    /// <summary>
    /// After a GLB import: the GLB's material names per submesh, and each appearance's chunkMaterials
    /// sized to the new submesh count, since WolvenKit's importer leaves appearances as they were. A
    /// submesh keeps the appearance's material at its index, or the appearance's last one. The GLB's
    /// names are reported, not applied: a GLB describes one look and a template can have many (the
    /// cargo crate has 18), so assigning materials is EDGEmat's (its Looks editor).
    /// </summary>
    private MeshImport? AfterMeshImport(string glb, string meshFile, bool fromTemplate, string? template)
    {
        CR2WFile? cr2w;
        using (var fs = File.OpenRead(meshFile))
        {
            cr2w = _host.Parser.ReadRed4File(fs);
        }
        if (cr2w?.RootChunk is not CMesh mesh || mesh.RenderResourceBlob?.Chunk is not rendRenderMeshBlob blob)
        {
            return null;
        }
        var submeshes = blob.Header.RenderChunkInfos.Count;

        // The importer takes each node with a mesh, sorted by node name, and its first primitive.
        var model = SharpGLTF.Schema2.ModelRoot.Load(glb, new SharpGLTF.Schema2.ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip });
        var perSubmesh = model.LogicalNodes.Where(n => n.Mesh is not null).Select(n => (n.Name ?? "", Material: n.Mesh.Primitives.FirstOrDefault()?.Material?.Name ?? ""))
            .OrderBy(x => x.Item1).Select(x => x.Material).ToList();
        var uses = perSubmesh.Select((name, i) => (name, i)).GroupBy(x => x.name).Select(g => new MeshMaterialUse(g.Key, g.Select(x => x.i).ToList())).ToList();

        var entries = mesh.MaterialEntries.Select(e => e.Name.GetResolvedText() ?? "").ToList();
        var appearances = new List<string>();
        var adjusted = new List<string>();
        var changed = false;
        foreach (var handle in mesh.Appearances)
        {
            if (handle.Chunk is not meshMeshAppearance app)
            {
                continue;
            }
            var name = app.Name.GetResolvedText() ?? "";
            appearances.Add(name);
            var was = app.ChunkMaterials.Select(m => m.GetResolvedText() ?? "").ToList();
            var now = new List<string>();
            for (var i = 0; i < submeshes; i++)
            {
                if (i < was.Count)
                {
                    now.Add(was[i]);
                }
                else
                {
                    now.Add(was.Count > 0 ? was[^1] : entries.FirstOrDefault() ?? "");
                }
            }
            if (!now.SequenceEqual(was))
            {
                app.ChunkMaterials = new CArray<CName>(now.Select(n => (CName)n).ToList());
                adjusted.Add($"{name}: {was.Count} chunk material(s) for {submeshes} submesh(es), now [{string.Join(", ", now)}]");
                changed = true;
            }
        }
        if (changed)
        {
            using var outFs = File.Create(meshFile);
            using var writer = new CR2WWriter(outFs) { LoggerService = _host.Log };
            writer.WriteFile(cr2w);
        }
        return new MeshImport(fromTemplate, fromTemplate ? template : null, submeshes, uses, entries, appearances, adjusted);
    }

    /// <summary>
    /// The setup fields WolvenKit's import settings have no place for, put back from the texture the
    /// import replaced. The rest (group, format, compression, gamma, mips) went in through XbmImportArgs.
    /// </summary>
    private void RestoreTextureFlags(string xbm, STextureGroupSetup was)
    {
        CR2WFile? cr2w;
        using (var fs = File.OpenRead(xbm))
        {
            cr2w = _host.Parser.ReadRed4File(fs);
        }
        if (cr2w?.RootChunk is not CBitmapTexture { Setup: { } now })
        {
            return;
        }
        now.IsStreamable = was.IsStreamable;
        now.AllowTextureDowngrade = was.AllowTextureDowngrade;
        now.PlatformMipBiasPC = was.PlatformMipBiasPC;
        now.PlatformMipBiasConsole = was.PlatformMipBiasConsole;
        using var outFs = File.Create(xbm);
        using var writer = new CR2WWriter(outFs) { LoggerService = _host.Log };
        writer.WriteFile(cr2w);
    }

    private static bool IsImage(string ext) => ext.ToLowerInvariant() is "png" or "tga" or "dds" or "bmp" or "jpg" or "tiff";

    private static EUncookExtension ParseExtension(string value) =>
        Enum.TryParse<EUncookExtension>(value.TrimStart('.'), true, out var e)
            ? e
            : throw new ArgumentException($"Unknown texture format {value}; use one of {string.Join(", ", Enum.GetNames<EUncookExtension>())}");

    /// <summary>Every file under dir with its write time and size, to tell afterwards what an export or import wrote.</summary>
    private static Dictionary<string, (DateTime, long)> Snapshot(string dir)
    {
        var map = new Dictionary<string, (DateTime, long)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir))
        {
            return map;
        }
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            map[f.FullName] = (f.LastWriteTimeUtc, f.Length);
        }
        return map;
    }

    private static List<string> Changed(string dir, Dictionary<string, (DateTime, long)> before)
    {
        var root = Path.GetFullPath(dir);
        return Snapshot(dir)
            .Where(kv => !before.TryGetValue(kv.Key, out var was) || was != kv.Value)
            .Select(kv => Path.GetRelativePath(root, kv.Key))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
