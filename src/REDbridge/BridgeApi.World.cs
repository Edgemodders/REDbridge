using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using StreamJsonRpc;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// The game's world as data, for EDGEbuilder's world map, sector view and in-game inspector
/// (issue #11). <c>sector.find</c> lists the streaming sectors of Night City's streaming blocks
/// (the base game's and Phantom Liberty's) with their streaming boxes; <c>sector.nodes</c> lists
/// one sector's placed nodes with world transforms, their resources and their streaming ranges.
///
/// Game 2.5 sectors are cooked: a node such as <c>worldCookedStaticMeshNode</c> holds several
/// meshes (its inner nodes), and each node-data entry places one of them, named by the entry's
/// <c>instanceIndex</c>. A <c>worldInstancedMeshNode</c> places many copies of each inner mesh, with
/// transforms in its cooked buffer relative to the entry. sector.nodes resolves both, so each item
/// is one placed thing with its own resource.
/// </summary>
public sealed partial class BridgeApi
{
    // ---- sector.find -----------------------------------------------------------------------

    /// <summary>
    /// One streaming sector. <see cref="Kind"/> is its category (exterior, interior, quest,
    /// navigation, alwaysLoaded). <see cref="Grid"/> is [x, y, z] from names like
    /// <c>exterior_-12_-7_0_1</c>, null for names without a grid. <see cref="Block"/> is "base" or
    /// "ep1", the streaming block that lists it. <see cref="Replaces"/> is true for a Phantom Liberty
    /// sector whose path the base block lists too: the game streams Phantom Liberty's.
    /// <see cref="Variants"/> counts the sector's named variants (node ranges a quest switches on or off).
    /// </summary>
    public sealed record SectorEntry(string Path, string Kind, int[]? Grid, int Level, Bounds Box, string Block, string? Archive, bool Replaces, int Variants);

    public sealed record SectorList(int Total, IReadOnlyList<SectorEntry> Sectors);

    private static readonly (string Block, string Path)[] WorldBlocks =
    [
        ("base", @"base\worlds\03_night_city\_compiled\default\blocks\all.streamingblock"),
        ("ep1", @"base\worlds\03_night_city\_compiled\default\ep1\blocks\all.streamingblock"),
    ];

    private static readonly Regex GridName = new(@"_(-?\d+)_(-?\d+)_(-?\d+)_(\d+)\.streamingsector$", RegexOptions.Compiled);

    private List<SectorEntry>? _sectors;
    private string? _sectorsGame;

    /// <summary>
    /// The game's streaming sectors, optionally only those whose streaming box meets
    /// <paramref name="box"/>, whose kind is in <paramref name="kinds"/> and whose level is in
    /// <paramref name="levels"/>. The whole list is read once per bridge (a few seconds) and kept.
    /// </summary>
    [JsonRpcMethod("sector.find")]
    public SectorList FindSectors(Bounds? box = null, IReadOnlyList<string>? kinds = null, IReadOnlyList<int>? levels = null)
    {
        RequireGame();
        var all = AllSectors();
        IEnumerable<SectorEntry> hits = all;
        if (box is not null)
        {
            if (box.Min.Length != 3 || box.Max.Length != 3)
            {
                throw new ArgumentException("box needs min[3] and max[3]");
            }
            hits = hits.Where(s => Meets(s.Box, box));
        }
        if (kinds is { Count: > 0 })
        {
            var set = new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);
            hits = hits.Where(s => set.Contains(s.Kind));
        }
        if (levels is { Count: > 0 })
        {
            var set = new HashSet<int>(levels);
            hits = hits.Where(s => set.Contains(s.Level));
        }
        var list = hits.ToList();
        return new SectorList(list.Count, list);
    }

    private static bool Meets(Bounds a, Bounds b) =>
        a.Min[0] <= b.Max[0] && a.Max[0] >= b.Min[0] &&
        a.Min[1] <= b.Max[1] && a.Max[1] >= b.Min[1] &&
        a.Min[2] <= b.Max[2] && a.Max[2] >= b.Min[2];

    private List<SectorEntry> AllSectors()
    {
        if (_sectors is not null && _sectorsGame == _host.GameDir)
        {
            return _sectors;
        }
        var started = DateTime.Now;
        var perBlock = new List<(string Block, worldStreamingSectorDescriptor D)>();
        foreach (var (block, path) in WorldBlocks)
        {
            var found = _host.Archives.Lookup(path);
            if (!found.HasValue)
            {
                _host.Log.Warning($"sector.find: {path} is not in the loaded archives");
                continue;
            }
            if (ReadCr2w(found.Value).RootChunk is not worldStreamingBlock sb)
            {
                throw new InvalidDataException($"'{path}' is not a streaming block");
            }
            perBlock.AddRange(sb.Descriptors.Select(d => (block, d)));
        }
        var ep1Paths = new HashSet<string>(
            perBlock.Where(x => x.Block == "ep1").Select(x => x.D.Data.DepotPath.GetResolvedText() ?? ""),
            StringComparer.OrdinalIgnoreCase);
        var basePaths = new HashSet<string>(
            perBlock.Where(x => x.Block == "base").Select(x => x.D.Data.DepotPath.GetResolvedText() ?? ""),
            StringComparer.OrdinalIgnoreCase);
        var read = DateTime.Now;
        var result = new List<SectorEntry>(perBlock.Count);
        foreach (var (block, d) in perBlock)
        {
            var path = d.Data.DepotPath.GetResolvedText() ?? ((ulong)d.Data.DepotPath).ToString();
            var listedByEp1 = ep1Paths.Contains(path);
            if (block == "base" && listedByEp1)
            {
                continue; // Phantom Liberty's entry stands for it
            }
            var m = GridName.Match(path);
            int[]? grid = m.Success ? [int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)] : null;
            var file = _host.Archives.Lookup(path);
            var archive = file.HasValue ? file.Value.GetArchive().Name : null;
            var replaces = block == "ep1" && basePaths.Contains(path);
            result.Add(new SectorEntry(path, KindOf(d.Category), grid, (byte)d.Level, BoundsOf(d.StreamingBox), block, archive, replaces, d.Variants.Count));
        }
        _sectors = result;
        _sectorsGame = _host.GameDir;
        _host.Log.Info($"sector.find: {result.Count:N0} sectors in {(DateTime.Now - started).TotalSeconds:F1} s (blocks read in {(read - started).TotalSeconds:F1} s)");
        return result;
    }

    private static string KindOf(Enums.worldStreamingSectorCategory c) => c switch
    {
        Enums.worldStreamingSectorCategory.Exterior => "exterior",
        Enums.worldStreamingSectorCategory.Interior => "interior",
        Enums.worldStreamingSectorCategory.Quest => "quest",
        Enums.worldStreamingSectorCategory.Navigation => "navigation",
        Enums.worldStreamingSectorCategory.AlwaysLoaded => "alwaysLoaded",
        _ => "unknown",
    };

    // ---- sector.nodes ----------------------------------------------------------------------

    /// <summary>A position, a rotation quaternion (x y z w) and a scale, in world space.</summary>
    public sealed record Placement(float[] Position, float[] Rotation, float[] Scale);

    /// <summary>
    /// Metres: <see cref="Primary"/> is the node data's streamingDistance, <see cref="Secondary"/>
    /// its secondaryRefPointDistance, null when the game sets none (its largest float). The same
    /// names as a scene's <c>streaming</c> in sector.build.
    /// </summary>
    public sealed record NodeStreaming(float Primary, float? Secondary);

    /// <summary>
    /// One placed thing in a sector: a node-data entry. <see cref="Index"/> is the entry's index in
    /// the sector's node data, <see cref="Node"/> the index of its node in the sector's nodes, and
    /// <see cref="Kind"/> the node's class. <see cref="Ref"/> is the node's reference path when it
    /// is known (the sector's own list, or WolvenKit's), <see cref="Hash"/> its global node id
    /// (2.5's <c>globalNodeId.hash</c>; null when 0). <see cref="Resource"/> is the mesh, entity
    /// template, decal material or effect it shows; <see cref="ResourceHash"/> the resource's hash
    /// when its path is not known. <see cref="Instances"/> is set for instanced meshes: one world
    /// placement per copy. <see cref="Bounds"/> is a world box, when it was cheap to get or asked for.
    /// </summary>
    public sealed record NodeEntry(
        int Index,
        int Node,
        string Kind,
        string? Ref,
        string? Hash,
        string? Resource,
        string? ResourceHash,
        string? Appearance,
        Placement Transform,
        IReadOnlyList<Placement>? Instances,
        NodeStreaming Streaming,
        Bounds? Bounds);

    public sealed record SectorNodes(string Path, string Kind, int Level, int Nodes, int Total, IReadOnlyList<NodeEntry> Items, IReadOnlyList<string> Warnings);

    private readonly Dictionary<string, Bounds?> _meshBounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One sector's placed nodes. <paramref name="kinds"/> keeps only nodes of those classes
    /// (<c>worldEntityNode</c>, or without the <c>world</c> prefix: <c>entityNode</c>).
    /// <paramref name="bounds"/> reads each mesh's bounds (once per mesh per bridge) to give every
    /// mesh item a world box; without it, only instanced meshes have one, from the sector itself.
    /// <paramref name="archive"/> and <paramref name="root"/> read a mod's sector (issue #12's lookup).
    /// </summary>
    [JsonRpcMethod("sector.nodes")]
    public SectorNodes SectorNodesOf(string path, IReadOnlyList<string>? kinds = null, bool bounds = false, string? archive = null, string? root = null)
    {
        using var mods = ModFiles.Open(_host, archive, root);
        var (name, cr2w) = ReadResource(path, mods);
        if (cr2w.RootChunk is not worldStreamingSector sector)
        {
            throw new InvalidDataException($"'{name}' is a {TypeNameOf(cr2w.RootChunk)}, not a streaming sector");
        }
        if (sector.NodeData.Data is not worldNodeDataBuffer data)
        {
            throw new InvalidDataException($"'{name}': its node data could not be read");
        }
        var warnings = new List<string>();
        var keep = kinds is { Count: > 0 } ? new HashSet<string>(kinds.Select(k => k.StartsWith("world", StringComparison.Ordinal) ? k : "world" + char.ToUpperInvariant(k[0]) + k[1..]), StringComparer.OrdinalIgnoreCase) : null;

        // Hash to reference path, from the sector's own list (2.5 hashes the path with every '#' left out).
        var refs = new Dictionary<ulong, string>();
        foreach (var r in sector.NodeRefs)
        {
            if (r.GetResolvedText() is { Length: > 0 } text)
            {
                refs.TryAdd(((NodeRef)text.Replace("#", "")).GetRedHash(), text);
                refs.TryAdd(r.GetRedHash(), text);
            }
        }

        var items = new List<NodeEntry>();
        var i = -1;
        foreach (var e in data)
        {
            i++;
            var nodeIndex = (int)(ulong)e.NodeIdx;
            var node = nodeIndex >= 0 && nodeIndex < sector.Nodes.Count ? sector.Nodes[nodeIndex].GetValue() as worldNode : null;
            var kind = node?.GetType().Name ?? "unknown";
            if (keep is not null && !keep.Contains(kind))
            {
                continue;
            }
            var hash = (ulong)e.GlobalNodeId.Hash;
            string? reference = null;
            if (hash != 0 && !refs.TryGetValue(hash, out reference))
            {
                reference = ((NodeRef)hash).GetResolvedText();
            }

            var (resource, appearance, inner) = node is null ? (null, null, -1) : WhatItShows(node, e);
            string? resourcePath = null, resourceHash = null;
            if (resource is { } rp)
            {
                resourcePath = rp.GetResolvedText();
                if (resourcePath is null && (ulong)rp != 0)
                {
                    resourceHash = ((ulong)rp).ToString();
                }
            }

            var place = PlacementOf(e);
            List<Placement>? instances = null;
            Bounds? world = null;
            if (node is worldInstancedMeshNode im)
            {
                (instances, world) = InstancesOf(im, inner, place, warnings, i);
            }
            else if (node is worldInstancedDestructibleMeshNode idm)
            {
                instances = idm.InstanceTransforms.Select(t => Compose(place, new Placement([t.Position.X, t.Position.Y, t.Position.Z], [t.Orientation.I, t.Orientation.J, t.Orientation.K, t.Orientation.R], [1, 1, 1]))).ToList();
            }
            if (bounds && world is null && resourcePath is not null && resourcePath.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase))
            {
                var local = MeshBoundsOf(resourcePath, mods);
                if (local is not null)
                {
                    world = instances is { Count: > 0 }
                        ? instances.Select(p => WorldBounds(local, ToSceneTransform(p))).Aggregate(Union)
                        : WorldBounds(local, ToSceneTransform(place));
                }
            }

            items.Add(new NodeEntry(
                i,
                nodeIndex,
                kind,
                reference,
                hash == 0 ? null : hash.ToString(),
                resourcePath,
                resourceHash,
                appearance,
                place,
                instances,
                new NodeStreaming(e.StreamingDistance, e.SecondaryRefPointDistance >= float.MaxValue ? null : e.SecondaryRefPointDistance),
                world));
        }
        return new SectorNodes(name, KindOf(sector.Category), sector.Level, sector.Nodes.Count, items.Count, items, warnings);
    }

    private static Placement PlacementOf(worldCompiledNodeInstanceSetupInfo e)
    {
        var p = e.Transform.Position;
        var q = e.Transform.Orientation;
        return new Placement([p.X, p.Y, p.Z], [q.I, q.J, q.K, q.R], [e.Scale.X, e.Scale.Y, e.Scale.Z]);
    }

    /// <summary>
    /// The resource a node-data entry shows, its appearance, and which inner node it places. Cooked
    /// nodes keep their resources in inner nodes, picked by the entry's instanceIndex; other nodes
    /// name theirs in a property (mesh, entityTemplate, material, effect, ...).
    /// </summary>
    private static (ResourcePath? Resource, string? Appearance, int Inner) WhatItShows(worldNode node, worldCompiledNodeInstanceSetupInfo e)
    {
        if (node is worldCookedNode cooked && InnerNodesOf(cooked) is { Count: > 0 } inner)
        {
            var k = e.InstanceIndex == uint.MaxValue ? 0 : (int)(uint)e.InstanceIndex;
            if (k >= inner.Count)
            {
                k = 0;
            }
            var (r, a) = RefAndAppearance(inner[k]!);
            return (r, a, k);
        }
        var (res, app) = RefAndAppearance(node);
        return (res, app, -1);
    }

    /// <summary>The inner nodes WolvenKit read from a cooked node's buffer, whatever the node type.</summary>
    private static IList? InnerNodesOf(worldCookedNode cooked)
    {
        var data = cooked.Buffer.Data;
        return data?.GetType().GetProperty("InnerNodes", BindingFlags.Public | BindingFlags.Instance)?.GetValue(data) as IList;
    }

    private static readonly string[] ResourceProps = ["Mesh", "EntityTemplate", "Material", "Effect", "Particles", "Decal", "Template"];
    private static readonly string[] AppearanceProps = ["MeshAppearance", "AppearanceName", "Appearance"];

    private static (ResourcePath? Resource, string? Appearance) RefAndAppearance(object o)
    {
        var type = o.GetType();
        ResourcePath? resource = null;
        foreach (var name in ResourceProps)
        {
            if (type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o) is IRedRef r && (ulong)r.DepotPath != 0)
            {
                resource = r.DepotPath;
                break;
            }
        }
        if (resource is null)
        {
            // Any other resource reference the node has (a light's IES profile is skipped: rarely what a map wants).
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (typeof(IRedRef).IsAssignableFrom(p.PropertyType) && p.GetValue(o) is IRedRef r && (ulong)r.DepotPath != 0)
                {
                    resource = r.DepotPath;
                    break;
                }
            }
        }
        string? appearance = null;
        foreach (var name in AppearanceProps)
        {
            if (type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o) is CName cn)
            {
                var text = cn.GetResolvedText();
                if (text is { Length: > 0 } && text != "None")
                {
                    appearance = text;
                }
                break;
            }
        }
        return (resource, appearance);
    }

    /// <summary>
    /// An instanced mesh entry's copies: the transforms in the node's cooked buffer for the entry's
    /// inner node (its transform range), each placed relative to the entry, and the inner node's
    /// world box, which the sector keeps.
    /// </summary>
    private static (List<Placement>?, Bounds?) InstancesOf(worldInstancedMeshNode node, int inner, Placement entry, List<string> warnings, int index)
    {
        if (node.CookedWorldTransformsBuffer.Buffer?.Data is not WorldTransformsBuffer wtb || node.Buffer.Data is not worldInstancedMeshNodeBuffer buffer)
        {
            warnings.Add($"entry {index}: the instanced mesh's transforms could not be read");
            return (null, null);
        }
        var k = Math.Max(0, inner);
        int start = 0, count = wtb.Transforms.Count;
        if (k < buffer.TransformRanges.Count)
        {
            start = buffer.TransformRanges[k].TransformIndex;
            count = buffer.TransformRanges[k].TransformCount;
        }
        var list = new List<Placement>(count);
        for (var t = start; t < start + count && t < wtb.Transforms.Count; t++)
        {
            var x = wtb.Transforms[t];
            var p = x.Transform.Position;
            var q = x.Transform.Orientation;
            list.Add(Compose(entry, new Placement([(float)p.X, (float)p.Y, (float)p.Z], [q.I, q.J, q.K, q.R], [x.Scale.X, x.Scale.Y, x.Scale.Z])));
        }
        Bounds? box = null;
        if (k < buffer.InnerNodes.Count)
        {
            var b = buffer.InnerNodes[k].GroupWorldBoundingBox;
            if (b.Max.X >= b.Min.X)
            {
                box = BoundsOf(b);
            }
        }
        return (list, box);
    }

    /// <summary>A placement inside a parent: scale, then rotate, then move by the parent.</summary>
    private static Placement Compose(Placement parent, Placement local)
    {
        float[] scaled = [local.Position[0] * parent.Scale[0], local.Position[1] * parent.Scale[1], local.Position[2] * parent.Scale[2]];
        var (x, y, z) = Rotate(scaled[0], scaled[1], scaled[2], parent.Rotation);
        return new Placement(
            [x + parent.Position[0], y + parent.Position[1], z + parent.Position[2]],
            Multiply(parent.Rotation, local.Rotation),
            [parent.Scale[0] * local.Scale[0], parent.Scale[1] * local.Scale[1], parent.Scale[2] * local.Scale[2]]);
    }

    /// <summary>The quaternion product a * b (x y z w): b's rotation, then a's.</summary>
    private static float[] Multiply(float[] a, float[] b) =>
    [
        a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
        a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
        a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
        a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
    ];

    private static SceneTransform ToSceneTransform(Placement p) => new() { Position = p.Position, Rotation = p.Rotation, Scale = p.Scale };

    private static Bounds Union(Bounds a, Bounds b) =>
        new([Math.Min(a.Min[0], b.Min[0]), Math.Min(a.Min[1], b.Min[1]), Math.Min(a.Min[2], b.Min[2])],
            [Math.Max(a.Max[0], b.Max[0]), Math.Max(a.Max[1], b.Max[1]), Math.Max(a.Max[2], b.Max[2])]);

    private Bounds? MeshBoundsOf(string mesh, ModFiles mods)
    {
        if (mods.IsEmpty && _meshBounds.TryGetValue(mesh, out var cached))
        {
            return cached;
        }
        Bounds? b = null;
        try
        {
            if (ReadResource(mesh, mods).File.RootChunk is CMesh m)
            {
                b = BoundsOf(m.BoundingBox);
            }
        }
        catch (Exception e)
        {
            _host.Log.Warning($"sector.nodes: bounds of {mesh}: {e.Message}");
        }
        if (mods.IsEmpty)
        {
            _meshBounds[mesh] = b;
        }
        return b;
    }
}
