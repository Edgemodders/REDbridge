using System.Text.Json;
using StreamJsonRpc;
using WolvenKit.Common;
using WolvenKit.Common.Model;
using WolvenKit.Common.Model.Arguments;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;
using WolvenKit.Modkit.RED4.GeneralStructs;
using WolvenKit.Modkit.RED4.RigFile;
using WolvenKit.Modkit.RED4.Tools;
using WolvenKit.Modkit.RED4.Tools.Common;
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
        bool? MeshLodFilter = null,
        // A .mesh exported skinned on this rig (a game path, or a .rig file on disk or under archiveDir): bridge 0.13.0, #14.
        string? Rig = null,
        // Several rigs joined into one skeleton for a .mesh, such as man_base.rig and man_base_deformations.rig: bridge 0.13.0.
        IReadOnlyList<string>? Rigs = null,
        // A .ent exported as one GLB in this appearance ("default" for its first): bridge 0.13.0, #14.
        string? Appearance = null,
        // An .anims exported with its root motion on the root bone (WolvenKit leaves it off): bridge 0.13.0, #14.
        bool? RootMotion = null);

    /// <summary>Mesh is set when an import wrote a .mesh (bridge 0.9.0); Texture when an image import wrote a .xbm, as read back from it.</summary>
    public sealed record ModkitResult(bool Ok, List<string> Written, MeshImport? Mesh = null, TextureSetup? Texture = null, EntityExport? Entity = null);

    /// <summary>
    /// What an entity export put in its GLB (bridge 0.13.0, #14): the appearance, the rigs joined into
    /// its one skeleton, the skinned meshes on it (component and mesh path), and what it left out, each with why.
    /// </summary>
    public sealed record EntityExport(string Appearance, IReadOnlyList<string> Rigs, int Joints, IReadOnlyList<EntityMesh> Meshes, IReadOnlyList<EntityMesh> LeftOut);

    /// <summary>One mesh component of an exported entity: its name, its mesh, and (for one left out) why.</summary>
    public sealed record EntityMesh(string Component, string Mesh, string? Why = null, string? On = null);

    /// <summary>A rigid mesh component placed under a bone (through a slot) or another mesh component, for the entity GLB.</summary>
    private sealed record RigidPart(string Name, CR2WFile Mesh, string? Bone, string? ParentMesh, SharpGLTF.Transforms.AffineTransform Slot, SharpGLTF.Transforms.AffineTransform Local, ulong ChunkMask);

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
        var o = options ?? new ExportOptions();
        var ext = source.Extension.TrimStart('.');
        if (ext.Equals("ent", StringComparison.OrdinalIgnoreCase))
        {
            return ExportEntity(source, archive, rawDir, o);
        }
        // WolvenKit dumps the raw buffers of any other type, which nobody wants in source/raw.
        if (!Enum.TryParse<ECookedFileFormat>(ext, true, out _))
        {
            throw new ArgumentException($".{ext} files have no export format; open them as JSON instead. Exportable: {string.Join(", ", Enum.GetNames<ECookedFileFormat>().Select(n => "." + n))}");
        }
        Directory.CreateDirectory(rawDir);
        // WolvenKit remembers what it exported in this process and skips it the next time.
        _host.ModTools.ClearFileLookup();
        var args = new GlobalExportArgs();
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
        if (o.RootMotion is { } rm)
        {
            args.Get<AnimationExportArgs>().incRootMotion = rm;
        }

        var before = Snapshot(rawDir);
        bool ok;
        var rigPaths = (o.Rigs ?? []).Concat(o.Rig is { } one ? [one] : []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (rigPaths.Count > 0)
        {
            if (!ext.Equals("mesh", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"rig is for .mesh files; {source.Name} is a .{ext}");
            }
            // The mesh's bones matched by name to the rig's, so the GLB carries the rig's whole
            // skeleton with the mesh skinned on it; several rigs are joined into one skeleton.
            var cr2w = ReadProjectCr2w(source);
            var rigFiles = rigPaths.Select(p =>
            {
                using var s = RigStream(p, archive);
                return _host.Parser.ReadRed4File(s) is { RootChunk: animRig } f ? f : throw new InvalidDataException($"{p} is not a rig");
            }).ToList();
            var have = rigFiles.SelectMany(f => ((animRig)f.RootChunk).BoneNames).Select(n => n.GetResolvedText()).ToHashSet();
            var missing = cr2w.RootChunk is CMesh m ? m.BoneNames.Select(n => n.GetResolvedText()).Where(n => n is not null && !have.Contains(n)).ToList() : [];
            if (missing.Count > 0)
            {
                // WolvenKit would export anyway and the GLB would fail to write ("JOINTS_0 is out of bounds").
                throw new ArgumentException($"{source.Name} uses {missing.Count} bones the rig lacks, such as {string.Join(", ", missing.Take(5))}. A body or garment needs its deformations rig, such as base\\characters\\base_entities\\man_base\\deformations_rigs\\man_base_deformations.rig (with man_base.rig in rigs for the eyes and weapon slots)");
            }
            var outfile = OutFile(source, archive, rawDir);
            if (rigFiles.Count == 1)
            {
                using var rig = RigStream(rigPaths[0], archive);
                ok = Modkit.ExportMeshWithRig(cr2w, rig, outfile, mesh);
            }
            else
            {
                mesh.meshExportType = MeshExportType.Multimesh;
                ok = MultiMeshWithRig(new Dictionary<CR2WFile, string> { [cr2w] = Path.GetFileNameWithoutExtension(source.Name) }, rigFiles, outfile, mesh);
            }
        }
        else
        {
            ok = _host.ModTools.Export(source, args, archive, new DirectoryInfo(rawDir));
        }
        return new ModkitResult(ok, Changed(rawDir, before));
    }

    /// <summary>
    /// A .ent as one GLB in one appearance (bridge 0.13.0, #14): every rig the entity's animated
    /// components name (a character's body, deformations and face rigs) joined into one skeleton, and
    /// every skinned mesh of the entity, its appearance and the appearance's parts on it, through
    /// WolvenKit's multi-mesh export. A morph target's base mesh goes in unmorphed. Meshes on a bone
    /// slot (rigid ones, such as a weapon) are left out and listed. WolvenKit's own entity export puts
    /// meshes on the animation rig alone, which lacks a body's muscle bones, so the GLB failed to write.
    /// Written as &lt;name&gt;.ent.glb.
    /// </summary>
    private ModkitResult ExportEntity(FileInfo source, DirectoryInfo archive, string rawDir, ExportOptions o)
    {
        RequireGame();
        if (string.IsNullOrWhiteSpace(o.Appearance))
        {
            throw new ArgumentException(".ent files export as one GLB in one appearance: give options.appearance (its name, or \"default\" for the first)");
        }
        var cr2w = ReadProjectCr2w(source);
        if (cr2w.RootChunk is not entEntityTemplate ent)
        {
            throw new InvalidDataException($"{source.Name} is not an entity template");
        }
        var looks = ent.Appearances.Where(a => (a.Name.GetResolvedText() ?? "") is { Length: > 0 } n && n != "None").ToList();
        var look = o.Appearance == "default"
            ? looks.FirstOrDefault()
            : looks.FirstOrDefault(a => a.Name.GetResolvedText() == o.Appearance) ?? looks.FirstOrDefault(a => a.AppearanceName.GetResolvedText() == o.Appearance);
        if (look is null)
        {
            throw new ArgumentException(looks.Count == 0
                ? $"{source.Name} has no appearances"
                : $"{source.Name} has no appearance '{o.Appearance}'. It has: {string.Join(", ", looks.Select(a => a.Name.GetResolvedText()))}");
        }

        // The entity's own components, its appearance's, and those of the appearance's parts.
        var components = new List<entIComponent>(ComponentsOf(ent.CompiledData?.Buffer?.Data));
        var appPath = look.AppearanceResource.DepotPath;
        if ((ulong)appPath != 0)
        {
            var app = ReadCr2w(Find(PathText(appPath))).RootChunk as appearanceAppearanceResource
                ?? throw new InvalidDataException($"{PathText(appPath)} is not an appearance resource");
            var wanted = look.AppearanceName.GetResolvedText() is { Length: > 0 } an && an != "None" ? an : look.Name.GetResolvedText();
            var def = app.Appearances.Select(h => h.GetValue()).OfType<appearanceAppearanceDefinition>().FirstOrDefault(d => d.Name.GetResolvedText() == wanted)
                ?? throw new InvalidDataException($"{PathText(appPath)} has no appearance '{wanted}'");
            components.AddRange(ComponentsOf(def.CompiledData?.Buffer?.Data));
            foreach (var part in def.PartsValues)
            {
                var partPath = part.Resource.DepotPath;
                if ((ulong)partPath != 0 && TryRead(partPath) is { RootChunk: entEntityTemplate partEnt })
                {
                    components.AddRange(ComponentsOf(partEnt.CompiledData?.Buffer?.Data));
                }
            }
        }

        var rigs = new List<(string Path, CR2WFile File)>();
        var meshes = new Dictionary<CR2WFile, string>();
        var used = new List<EntityMesh>();
        var leftOut = new List<EntityMesh>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rigidComps = new List<IRedMeshComponent>();
        foreach (var c in components)
        {
            var name = c.Name.GetResolvedText() ?? "";
            if (c is entAnimatedComponent { Rig.DepotPath: var rp } && (ulong)rp != 0 && !rigs.Any(r => r.Path == PathText(rp)))
            {
                if (TryRead(rp) is { } rigFile)
                {
                    rigs.Add((PathText(rp), rigFile));
                }
                continue;
            }
            ResourcePath meshPath;
            string? why = null;
            if (c is IRedMeshComponent proxy && (c is entAppearanceProxyMeshComponent || name == "AppearanceProxyMesh"))
            {
                // The whole appearance as one low-poly mesh, drawn far away: it would cover the real meshes.
                leftOut.Add(new EntityMesh(name, PathText(proxy.Mesh.DepotPath), "the appearance's far proxy, which overlaps the real meshes"));
                continue;
            }
            var enabled = c switch
            {
                entMeshComponent mc => (bool)mc.IsEnabled,
                entSkinnedMeshComponent sc => (bool)sc.IsEnabled,
                entMorphTargetSkinnedMeshComponent tc => (bool)tc.IsEnabled,
                _ => true,
            };
            if (!enabled)
            {
                leftOut.Add(new EntityMesh(name, c is IRedMeshComponent off ? PathText(off.Mesh.DepotPath) : "", "switched off in this appearance"));
                continue;
            }
            switch (c)
            {
                case entSkinnedMeshComponent sk:
                    meshPath = sk.Mesh.DepotPath;
                    break;
                case entMorphTargetSkinnedMeshComponent mt:
                    meshPath = TryRead(mt.MorphResource.DepotPath)?.RootChunk is MorphTargetMesh morph ? morph.BaseMesh.DepotPath : default;
                    if ((ulong)meshPath == 0)
                    {
                        leftOut.Add(new EntityMesh(name, PathText(mt.MorphResource.DepotPath), "its morph target's base mesh is not in the loaded archives"));
                        continue;
                    }
                    break;
                case IRedMeshComponent rigidMesh:
                    rigidComps.Add(rigidMesh);
                    continue;
                default:
                    continue;
            }
            var path = PathText(meshPath);
            if ((ulong)meshPath == 0 || !seen.Add(name + "|" + path))
            {
                continue;
            }
            if (TryRead(meshPath) is not { RootChunk: CMesh } meshFile)
            {
                why = "not in the loaded archives";
                leftOut.Add(new EntityMesh(name, path, why));
                continue;
            }
            meshes[meshFile] = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(path) : name;
            used.Add(new EntityMesh(name, path, On: "skin"));
        }

        // Rigid meshes (a vehicle's body, doors and wheels, a character's weapon): placed under the bone
        // their slot names, so they follow it, as WolvenKit's entity export places them.
        var byName = components.GroupBy(c => c.Name.GetResolvedText() ?? "").ToDictionary(g => g.Key, g => g.First());
        var rigid = new List<RigidPart>();
        foreach (var rc in rigidComps)
        {
            var path = PathText(rc.Mesh.DepotPath);
            var name = rc.Name.GetResolvedText() is { Length: > 0 } n && n != "None" ? n : Path.GetFileNameWithoutExtension(path);
            if ((ulong)rc.Mesh.DepotPath == 0 || !seen.Add(name + "|" + path))
            {
                continue;
            }
            if (TryRead(rc.Mesh.DepotPath) is not { RootChunk: CMesh } meshFile)
            {
                leftOut.Add(new EntityMesh(name, path, "not in the loaded archives"));
                continue;
            }
            string? bone = null, parentMesh = null;
            var slot = SharpGLTF.Transforms.AffineTransform.Identity;
            if (rc.ParentTransform?.GetValue() is entHardTransformBinding hb && hb.BindName.GetResolvedText() is { Length: > 0 } bind && byName.TryGetValue(bind, out var target))
            {
                var slotName = hb.SlotName.GetResolvedText();
                if (target is entSlotComponent sc && sc.Slots.FirstOrDefault(x => x.SlotName.GetResolvedText() == slotName) is { } sl)
                {
                    bone = sl.BoneName.GetResolvedText();
                    slot = new SharpGLTF.Transforms.AffineTransform(System.Numerics.Vector3.One, WolvenKit.Modkit.RED4.ModTools.ToQuaternion(sl.RelativeRotation), WolvenKit.Modkit.RED4.ModTools.ToVector3(sl.RelativePosition));
                }
                else if (target is entAnimatedComponent && slotName is { Length: > 0 } && slotName != "None")
                {
                    bone = slotName;
                }
                else if (target is IRedMeshComponent)
                {
                    parentMesh = bind;
                }
            }
            var scale = rc is entMeshComponent emc ? WolvenKit.Modkit.RED4.ModTools.ToVector3(emc.VisualScale) : System.Numerics.Vector3.One;
            var local = new SharpGLTF.Transforms.AffineTransform(scale, WolvenKit.Modkit.RED4.ModTools.ToQuaternion(rc.LocalTransform.Orientation), WolvenKit.Modkit.RED4.ModTools.ToVector3(rc.LocalTransform.Position));
            rigid.Add(new RigidPart(name, meshFile, bone, parentMesh, slot, local, (ulong)rc.ChunkMask));
            used.Add(new EntityMesh(name, path, On: bone is not null ? $"bone {bone}" : parentMesh ?? "root"));
        }
        if (rigs.Count == 0)
        {
            throw new InvalidDataException($"{source.Name} names no rig in an animated component, so its meshes have no skeleton to go on");
        }
        if (meshes.Count == 0 && rigid.Count == 0)
        {
            throw new InvalidDataException($"{source.Name} in '{look.Name.GetResolvedText()}' has no meshes to export ({leftOut.Count} left out)");
        }

        Directory.CreateDirectory(rawDir);
        _host.ModTools.ClearFileLookup();
        var args = new MeshExportArgs
        {
            meshExportType = MeshExportType.Multimesh,
            MaterialRepo = Path.GetFullPath(rawDir),
        };
        if (o.MeshBinary is { } bin)
        {
            args.isGLBinary = bin;
        }
        if (o.MeshMaterials is { } mats)
        {
            args.withMaterials = mats;
        }
        if (o.MaterialTextureFormat is { } mtf)
        {
            args.MaterialUncookExtension = ParseExtension(mtf);
        }
        if (o.MeshLodFilter is { } lod)
        {
            args.LodFilter = lod;
        }
        var before = Snapshot(rawDir);
        var ok = MultiMeshWithRig(meshes, rigs.Select(r => r.File).ToList(), OutFile(source, archive, rawDir), args, rigid);
        var joints = rigs.Select(r => r.File.RootChunk).OfType<animRig>().SelectMany(r => r.BoneNames).Select(n => n.GetResolvedText()).Distinct().Count();
        return new ModkitResult(ok, Changed(rawDir, before), Entity: new EntityExport(look.Name.GetResolvedText() ?? "", rigs.Select(r => r.Path).ToList(), joints, used, leftOut));
    }

    /// <summary>
    /// Several meshes skinned on several rigs joined into one skeleton. With materials, WolvenKit's
    /// ExportMultiMeshWithRig; without, the same steps here, since WolvenKit's then fails saving an
    /// empty material list (10.0.0-beta.1: SaveMaterials reads its first entry).
    /// </summary>
    private bool MultiMeshWithRig(Dictionary<CR2WFile, string> meshFiles, List<CR2WFile> rigFiles, FileInfo outfile, MeshExportArgs args, List<RigidPart>? rigid = null)
    {
        rigid ??= [];
        if (args.withMaterials)
        {
            // WolvenKit writes the material files (for the rigid meshes too); with rigid meshes the GLB
            // is then written again below, with them placed on their bones.
            var withRigid = new Dictionary<CR2WFile, string>(meshFiles);
            foreach (var r in rigid)
            {
                withRigid.TryAdd(r.Mesh, r.Name);
            }
            var wrote = Modkit.ExportMultiMeshWithRig(withRigid, rigFiles, outfile, args);
            if (!wrote || rigid.Count == 0)
            {
                return wrote;
            }
        }
        var rig = RIG.CombineRigs(rigFiles.Select(f => RIG.ProcessRig(f) ?? throw new InvalidDataException("a rig did not read")).ToList());
        var all = new List<RawMeshContainer>();
        foreach (var (cr2w, name) in meshFiles)
        {
            if (cr2w.RootChunk is not CMesh { RenderResourceBlob.Chunk: rendRenderMeshBlob blob } cMesh)
            {
                continue;
            }
            using var ms = new MemoryStream(blob.RenderBuffer.Buffer.GetBytes());
            var info = MeshTools.GetMeshesinfo(blob, cMesh, name);
            var meshes = MeshTools.ContainRawMesh(ms, info, args.LodFilter, ulong.MaxValue, false, name);
            MeshTools.UpdateSkinningParamCloth(ref meshes, cr2w);
            MeshTools.WriteGarmentParametersToMesh(ref meshes, cMesh, args.ExportGarmentSupport);
            try
            {
                MeshTools.UpdateMeshJoints(ref meshes, rig, MeshTools.GetOrphanRig(cMesh), name);
            }
            catch (WolvenKit.Core.Exceptions.WolvenKitException e)
            {
                _host.Log.Warning($"{name}: bones missing from the rig: {e.Message}");
            }
            all.AddRange(meshes);
        }
        var model = MeshTools.RawMeshesToGLTF(all, rig, withMaterials: args.withMaterials);
        var materials = args.withMaterials ? model.LogicalMaterials.GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First()) : null;
        var placed = new Dictionary<string, SharpGLTF.Schema2.Node>();
        // Parts on bones first, then parts on other parts, so a parent is placed before its child.
        foreach (var r in rigid.OrderBy(r => r.ParentMesh is null ? 0 : 1))
        {
            SharpGLTF.Schema2.IVisualNodeContainer parent =
                r.ParentMesh is not null && placed.TryGetValue(r.ParentMesh, out var pm) ? pm
                : (SharpGLTF.Schema2.IVisualNodeContainer?)model.LogicalNodes.FirstOrDefault(n => n.Name == (r.Bone ?? "Root"))
                ?? (SharpGLTF.Schema2.IVisualNodeContainer?)model.LogicalNodes.FirstOrDefault(n => n.VisualParent is null)
                ?? model.UseScene(0);
            var at = parent;
            if (r.Slot != SharpGLTF.Transforms.AffineTransform.Identity)
            {
                var slotNode = parent.CreateNode(r.Name + "_slot");
                slotNode.LocalTransform = r.Slot;
                at = slotNode;
            }
            var node = at.CreateNode(r.Name);
            node.LocalTransform = r.Local;
            MeshTools.AddMeshToModel(r.Mesh, model, null!, node, args.LodFilter, r.ChunkMask, materials);
            foreach (var child in node.VisualChildren)
            {
                child.Name = r.Name + "_" + child.Name;
            }
            placed.TryAdd(r.Name, node);
        }
        model.Save(GLTFHelper.PrepareFilePath(outfile.FullName, args.isGLBinary), new SharpGLTF.Schema2.WriteSettings(SharpGLTF.Validation.ValidationMode.TryFix));
        return true;
    }

    private static IEnumerable<entIComponent> ComponentsOf(IParseableBuffer? data) =>
        data is RedPackage pkg ? pkg.Chunks.OfType<entIComponent>() : [];

    private static string PathText(ResourcePath p) => p.GetResolvedText() ?? ((ulong)p).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A game file as CR2W, or null when it is not in the loaded archives or does not read.</summary>
    private CR2WFile? TryRead(ResourcePath p)
    {
        var found = _host.Archives.Lookup((ulong)p);
        if (!found.HasValue)
        {
            return null;
        }
        try
        {
            return ReadCr2w(found.Value);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private WolvenKit.Modkit.RED4.ModTools Modkit =>
        _host.ModTools as WolvenKit.Modkit.RED4.ModTools ?? throw new InvalidOperationException("WolvenKit's ModTools is not the Modkit one");

    private CR2WFile ReadProjectCr2w(FileInfo file)
    {
        using var fs = file.OpenRead();
        return _host.Parser.ReadRed4File(fs) ?? throw new InvalidDataException($"'{file.Name}' is not a CR2W file");
    }

    /// <summary>The export's output path before WolvenKit adds .glb: rawDir plus the file's path under archiveDir.</summary>
    private static FileInfo OutFile(FileInfo source, DirectoryInfo archive, string rawDir)
    {
        var outfile = new FileInfo(Path.Combine(Path.GetFullPath(rawDir), Path.GetRelativePath(archive.FullName, source.FullName)));
        outfile.Directory?.Create();
        return outfile;
    }

    /// <summary>A rig's bytes: a .rig file on disk or under archiveDir first, then a game path or hash.</summary>
    private Stream RigStream(string rig, DirectoryInfo archive)
    {
        foreach (var path in new[] { rig, Path.Combine(archive.FullName, rig) })
        {
            if (Path.IsPathFullyQualified(path) && File.Exists(path))
            {
                return new MemoryStream(File.ReadAllBytes(path));
            }
        }
        var ms = new MemoryStream();
        Find(rig).Extract(ms);
        ms.Position = 0;
        return ms;
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
