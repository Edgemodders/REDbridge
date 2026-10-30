using StreamJsonRpc;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// What an editor needs to know about a mesh or an entity template before placing it: its bounds
/// and its appearance names. Read from the file's header fields, so an index of thousands of
/// meshes costs a header read each, not a full parse into JSON. Asked for by EDGEbuilder (issue #2).
/// With <c>archive</c> (a mod's .archive) or <c>root</c> (a folder of loose files laid out as in an
/// archive), the files are looked up there first, then in the game's archives (issue #12).
/// </summary>
public sealed partial class BridgeApi
{
    /// <summary>An axis-aligned box as two [x, y, z] corners.</summary>
    public sealed record Bounds(float[] Min, float[] Max);

    /// <summary>
    /// One mesh component of an entity: the mesh, its local offset and scale, and the mesh's own
    /// bounds. <see cref="Error"/> says why the bounds are missing when the mesh could not be read.
    /// </summary>
    public sealed record MeshPart(string Mesh, string? Appearance, float[] Offset, float[] Scale, Bounds? Bounds, string? Error = null);

    /// <summary>
    /// <see cref="Type"/> is "mesh" or "entity". <see cref="Bounds"/> is the mesh's bounding box, or
    /// for an entity the union of its mesh components' boxes placed at their local offsets (rotation
    /// of a component is ignored). <see cref="Appearances"/> are the names to choose from.
    /// </summary>
    public sealed record MeshInfo(string Path, string Type, Bounds? Bounds, IReadOnlyList<string> Appearances, IReadOnlyList<MeshPart>? Parts, IReadOnlyList<string>? AppearanceResources, string? Error);

    [JsonRpcMethod("mesh.info")]
    public MeshInfo MeshInfoOf(string path, string? archive = null, string? root = null)
    {
        using var mods = ModFiles.Open(_host, archive, root);
        return InfoOf(path, mods);
    }

    /// <summary>The same for many paths in one call; a path that fails carries its error instead of failing the batch.</summary>
    [JsonRpcMethod("mesh.infoMany")]
    public IReadOnlyList<MeshInfo> MeshInfoMany(IReadOnlyList<string> paths, string? archive = null, string? root = null)
    {
        using var mods = ModFiles.Open(_host, archive, root);
        var result = new List<MeshInfo>(paths.Count);
        foreach (var p in paths)
        {
            try
            {
                result.Add(InfoOf(p, mods));
            }
            catch (Exception e)
            {
                result.Add(new MeshInfo(p, "", null, [], null, null, e.Message));
            }
        }
        return result;
    }

    private MeshInfo InfoOf(string pathOrHash, ModFiles? mods)
    {
        var (name, cr2w) = ReadResource(pathOrHash, mods);
        return cr2w.RootChunk switch
        {
            CMesh mesh => new MeshInfo(name, "mesh", BoundsOf(mesh.BoundingBox), AppearanceNames(mesh), null, null, null),
            entEntityTemplate ent => EntityInfoOf(name, ent, mods),
            var other => throw new InvalidDataException($"'{name}' is a {TypeNameOf(other)}, not a mesh or an entity template"),
        };
    }

    private static List<string> AppearanceNames(CMesh mesh) =>
        mesh.Appearances.Select(a => a.Chunk?.Name.GetResolvedText() ?? "").Where(n => n.Length > 0 && n != "None").ToList();

    private MeshInfo EntityInfoOf(string name, entEntityTemplate ent, ModFiles? mods)
    {
        var parts = new List<MeshPart>();
        var components = ent.CompiledData?.Buffer?.Data is RedPackage package ? package.Chunks : [];
        foreach (var component in components)
        {
            if (component is not entMeshComponent mc)
            {
                continue;
            }
            var meshPath = mc.Mesh.DepotPath.GetResolvedText() ?? "";
            var position = mc.LocalTransform?.Position;
            var offset = position is null ? new float[3] : [position.X, position.Y, position.Z];
            var scale = mc.VisualScale is { } vs && (vs.X != 0 || vs.Y != 0 || vs.Z != 0) ? new[] { (float)vs.X, (float)vs.Y, (float)vs.Z } : [1f, 1f, 1f];
            Bounds? bounds = null;
            string? error = null;
            if (meshPath.Length > 0)
            {
                // A part may name a mesh in the same mod, a mod it depends on, or the game.
                try
                {
                    if (ReadResource(meshPath, mods).File.RootChunk is CMesh mesh)
                    {
                        bounds = BoundsOf(mesh.BoundingBox);
                    }
                    else
                    {
                        error = $"'{meshPath}' is not a mesh";
                    }
                }
                catch (Exception e)
                {
                    error = e.Message;
                    _host.Log.Warning($"mesh.info {name}: component mesh {meshPath}: {e.Message}");
                }
            }
            parts.Add(new MeshPart(meshPath, mc.MeshAppearance.GetResolvedText(), offset, scale, bounds, error));
        }
        var union = UnionOf(parts);
        var appearances = ent.Appearances.Select(a => a.Name.GetResolvedText() ?? "").Where(n => n.Length > 0 && n != "None").ToList();
        var resources = ent.Appearances.Select(a => a.AppearanceResource.DepotPath.GetResolvedText() ?? "").Where(p => p.Length > 0).Distinct().ToList();
        return new MeshInfo(name, "entity", union, appearances, parts, resources, null);
    }

    private static Bounds BoundsOf(Box box) =>
        new([box.Min.X, box.Min.Y, box.Min.Z], [box.Max.X, box.Max.Y, box.Max.Z]);

    /// <summary>The box around every part's box, each scaled and moved to its offset. Null when no part has bounds.</summary>
    private static Bounds? UnionOf(IEnumerable<MeshPart> parts)
    {
        float[]? min = null, max = null;
        foreach (var p in parts)
        {
            if (p.Bounds is null)
            {
                continue;
            }
            for (var i = 0; i < 3; i++)
            {
                var a = p.Offset[i] + p.Bounds.Min[i] * p.Scale[i];
                var b = p.Offset[i] + p.Bounds.Max[i] * p.Scale[i];
                var lo = Math.Min(a, b);
                var hi = Math.Max(a, b);
                min ??= [float.MaxValue, float.MaxValue, float.MaxValue];
                max ??= [float.MinValue, float.MinValue, float.MinValue];
                min[i] = Math.Min(min[i], lo);
                max[i] = Math.Max(max[i], hi);
            }
        }
        return min is null || max is null ? null : new Bounds(min, max);
    }
}
