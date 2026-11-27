using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
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
    /// <see cref="Variants"/> counts the sector's named variants (node ranges a quest switches on or off),
    /// or lists them (<see cref="SectorVariant"/>) when sector.find is asked for <c>variants</c>.
    /// <see cref="Source"/> is "game", "mod" (a sector an installed mod adds through an ArchiveXL
    /// <c>.xl</c>, named by <see cref="Mod"/>) or "project" (one a project's own <c>.xl</c> adds).
    /// For those, <see cref="Block"/> is the streaming block's path.
    /// </summary>
    public sealed record SectorEntry(
        string Path,
        string Kind,
        int[]? Grid,
        int Level,
        Bounds Box,
        string Block,
        string? Archive,
        bool Replaces,
        object Variants,
        string Source = "game",
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mod = null);

    /// <summary>
    /// A named variant of a sector (issue #23): a range of its node-data entries that a quest switches
    /// on or off. <see cref="RangeIndex"/> is the range sector.nodes gives each entry with
    /// <c>ranges</c>. Ids are text, as 64-bit values are elsewhere; <see cref="NodeRef"/> is the
    /// variant's node reference, its path when known, else its hash.
    /// </summary>
    public sealed record SectorVariant(string Name, string Id, string Parent, int RangeIndex, bool EnabledByDefault, string? NodeRef);

    public sealed record SectorList(
        int Total,
        IReadOnlyList<SectorEntry> Sectors,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Warnings = null);

    private static readonly (string Block, string Path)[] WorldBlocks =
    [
        ("base", @"base\worlds\03_night_city\_compiled\default\blocks\all.streamingblock"),
        ("ep1", @"base\worlds\03_night_city\_compiled\default\ep1\blocks\all.streamingblock"),
    ];

    private static readonly Regex GridName = new(@"_(-?\d+)_(-?\d+)_(-?\d+)_(\d+)\.streamingsector$", RegexOptions.Compiled);

    private List<SectorEntry>? _sectors;
    private Dictionary<string, List<SectorVariant>>? _sectorVariants;
    private string? _sectorsGame;

    /// <summary>
    /// The game's streaming sectors, optionally only those whose streaming box meets
    /// <paramref name="box"/>, whose kind is in <paramref name="kinds"/> and whose level is in
    /// <paramref name="levels"/>. The whole list is read once per bridge (a few seconds) and kept.
    /// <paramref name="variants"/> lists each sector's variants by name in place of their count
    /// (issue #23). <paramref name="sources"/> picks where sectors come from: "game" (the default),
    /// "mods" (sectors installed mods add through ArchiveXL <c>.xl</c> files under
    /// <c>streaming: blocks:</c>, read from archive\pc\mod) and "project" (those a project's own
    /// <c>.xl</c> files add, read from <paramref name="project"/>'s source folder).
    /// </summary>
    [JsonRpcMethod("sector.find")]
    public SectorList FindSectors(Bounds? box = null, IReadOnlyList<string>? kinds = null, IReadOnlyList<int>? levels = null, bool variants = false, IReadOnlyList<string>? sources = null, string? project = null)
    {
        RequireGame();
        var from = new HashSet<string>(sources is { Count: > 0 } ? sources : ["game"], StringComparer.OrdinalIgnoreCase);
        foreach (var src in from)
        {
            if (src is not ("game" or "mods" or "mod" or "project"))
            {
                throw new ArgumentException($"Unknown source '{src}': use game, mods or project");
            }
        }
        var all = new List<SectorEntry>();
        var variantsOf = new Dictionary<string, List<SectorVariant>>(StringComparer.OrdinalIgnoreCase);
        List<string>? warnings = null;
        if (from.Contains("game"))
        {
            all.AddRange(AllSectors());
            if (variants)
            {
                foreach (var (k, v) in _sectorVariants!) variantsOf[k] = v;
            }
        }
        if (from.Contains("mods") || from.Contains("mod"))
        {
            var mods = ModSectors();
            all.AddRange(mods.Sectors);
            foreach (var (k, v) in mods.Variants) variantsOf[k] = v;
            (warnings ??= new()).AddRange(mods.Warnings);
        }
        if (from.Contains("project"))
        {
            if (string.IsNullOrEmpty(project))
            {
                throw new ArgumentException("The project source needs project, the project's folder");
            }
            var mine = ProjectSectors(project);
            all.AddRange(mine.Sectors);
            foreach (var (k, v) in mine.Variants) variantsOf[k] = v;
            (warnings ??= new()).AddRange(mine.Warnings);
        }
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
        if (variants)
        {
            list = list.Select(e => e with { Variants = variantsOf.TryGetValue(e.Path + "|" + e.Source + "|" + e.Mod, out var v) ? v : new List<SectorVariant>() }).ToList();
        }
        return new SectorList(list.Count, list, warnings is { Count: > 0 } ? warnings : null);
    }

    /// <summary>A descriptor's variants, for sector.find's <c>variants</c>.</summary>
    private static List<SectorVariant> VariantsOf(worldStreamingSectorDescriptor d) => d.Variants.Select(v => new SectorVariant(
        v.Name.GetResolvedText() ?? v.Name.ToString() ?? "",
        ((uint)v.VariantId).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ((uint)v.ParentVariantID).ToString(System.Globalization.CultureInfo.InvariantCulture),
        (int)(uint)v.RangeIndex,
        v.EnabledByDefault,
        v.NodeRef.GetResolvedText() is { Length: > 0 } text ? text : ((ulong)v.NodeRef.GetRedHash() == 0 ? null : v.NodeRef.GetRedHash().ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToList();

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
        var variantLists = new Dictionary<string, List<SectorVariant>>(StringComparer.OrdinalIgnoreCase);
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
            variantLists[path + "|game|"] = VariantsOf(d);
        }
        _sectors = result;
        _sectorVariants = variantLists;
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
    /// when its path is not known. <see cref="Instances"/> is set for nodes that keep their places in
    /// a buffer (instanced meshes, instanced destructible meshes, instanced occluders): one world
    /// placement per copy. <see cref="Bounds"/> is a world box, when it was cheap to get or asked for,
    /// and <see cref="BoundsFrom"/> says where it came from (issue #25): <c>group</c> the box the
    /// sector keeps for an instanced mesh's copies, <c>copies</c> the copies' mesh boxes together,
    /// <c>mesh</c> the mesh's box at the entry's place, <c>node</c> the node's own world box, <c>none</c>
    /// no box. With <c>bounds</c>, <see cref="InstanceBounds"/> holds each copy's world box, in the
    /// order of <see cref="Instances"/>, when the mesh's box could be read.
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
        Bounds? Bounds,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Range = null,
        string BoundsFrom = "none",
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Bounds>? InstanceBounds = null);

    /// <summary>
    /// With <c>ranges</c>, <see cref="BaseRange"/> is the range of entries every variant keeps (the
    /// sector's persistent range, 0 in the game's sectors) and <see cref="Ranges"/> how many there are.
    /// </summary>
    public sealed record SectorNodes(
        string Path,
        string Kind,
        int Level,
        int Nodes,
        int Total,
        IReadOnlyList<NodeEntry> Items,
        IReadOnlyList<string> Warnings,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? BaseRange = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Ranges = null);

    private readonly Dictionary<string, Bounds?> _meshBounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One sector's placed nodes. <paramref name="kinds"/> keeps only nodes of those classes
    /// (<c>worldEntityNode</c>, or without the <c>world</c> prefix: <c>entityNode</c>).
    /// <paramref name="bounds"/> reads each mesh's bounds (once per mesh per bridge) to give every
    /// mesh item a world box; without it, only instanced meshes have one, from the sector itself.
    /// <paramref name="archive"/> and <paramref name="root"/> read a mod's sector (issue #12's lookup).
    /// <paramref name="ranges"/> gives each item the range of node-data entries it is in (issue #23):
    /// a sector's entries are kept in ranges, one the base that is always on, the others switched by
    /// the variants sector.find lists with their <c>rangeIndex</c>.
    /// </summary>
    [JsonRpcMethod("sector.nodes")]
    public SectorNodes SectorNodesOf(string path, IReadOnlyList<string>? kinds = null, bool bounds = false, string? archive = null, string? root = null, bool ranges = false)
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
        // A range starts at its entry in variantIndices and runs to the next one's start (WolvenKit's reader splits them the same way).
        var starts = sector.VariantIndices.Select(v => (int)v).ToList();
        int RangeOf(int entry)
        {
            var r = -1;
            for (var k = 0; k < starts.Count; k++)
            {
                if (starts[k] <= entry) r = k;
            }
            return Math.Max(r, 0);
        }
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
            var from = "none";
            if (node is worldInstancedMeshNode im)
            {
                (instances, world) = InstancesOf(im, inner, place, warnings, i);
                if (world is not null)
                {
                    from = "group";
                }
            }
            else if (node is worldInstancedDestructibleMeshNode idm)
            {
                instances = idm.InstanceTransforms.Select(t => Compose(place, new Placement([t.Position.X, t.Position.Y, t.Position.Z], [t.Orientation.I, t.Orientation.J, t.Orientation.K, t.Orientation.R], [1, 1, 1]))).ToList();
            }
            else if (node is worldInstancedOccluderNode occ)
            {
                instances = OccluderCopies(occ, place, warnings, i);
            }
            List<Bounds>? copyBoxes = null;
            if (bounds && resourcePath is not null && IsMeshPath(resourcePath) && (world is null || instances is { Count: > 0 }))
            {
                var local = MeshBoundsOf(resourcePath, mods);
                if (local is not null)
                {
                    if (instances is { Count: > 0 })
                    {
                        copyBoxes = instances.Select(p => WorldBounds(local, ToSceneTransform(p))).ToList();
                        if (world is null)
                        {
                            world = copyBoxes.Aggregate(Union);
                            from = "copies";
                        }
                    }
                    else if (instances is null)
                    {
                        world = WorldBounds(local, ToSceneTransform(place));
                        from = "mesh";
                    }
                }
            }
            if (world is null && node is worldInstancedOccluderNode { WorldBounds: var wb } && wb.Max.X >= wb.Min.X)
            {
                world = BoundsOf(wb);
                from = "node";
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
                world,
                ranges ? RangeOf(i) : null,
                from,
                copyBoxes));
        }
        return ranges
            ? new SectorNodes(name, KindOf(sector.Category), sector.Level, sector.Nodes.Count, items.Count, items, warnings, (int)sector.PersisentNodeIndex, Math.Max(starts.Count, 1))
            : new SectorNodes(name, KindOf(sector.Category), sector.Level, sector.Nodes.Count, items.Count, items, warnings);
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

    private static bool IsMeshPath(string path) =>
        path.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An instanced occluder's copies (issue #25). Its buffer keeps one matrix per copy, row vectors:
    /// the first three rows are the copy's x, y and z axes with its scale in their lengths, the fourth
    /// its place. In the game's sectors the entry stands at the origin and the places are in world
    /// space; each copy is still placed relative to the entry, as instanced meshes' are (an entry
    /// away from the origin is unverified). A matrix whose axes are not square to each other loses
    /// its shear and is named in the warnings.
    /// </summary>
    private static List<Placement> OccluderCopies(worldInstancedOccluderNode node, Placement entry, List<string> warnings, int index)
    {
        var list = new List<Placement>(node.Buffer.Count);
        var sheared = false;
        foreach (var m in node.Buffer)
        {
            double[] ax = [m.Unknown1.X, m.Unknown1.Y, m.Unknown1.Z];
            double[] ay = [m.Unknown2.X, m.Unknown2.Y, m.Unknown2.Z];
            double[] az = [m.Unknown3.X, m.Unknown3.Y, m.Unknown3.Z];
            double sx = Length(ax), sy = Length(ay), sz = Length(az);
            if (sx == 0 || sy == 0 || sz == 0)
            {
                warnings.Add($"entry {index}: an occluder copy with a flat axis was left out");
                continue;
            }
            for (var k = 0; k < 3; k++)
            {
                ax[k] /= sx;
                ay[k] /= sy;
                az[k] /= sz;
            }
            if (Math.Abs(Dot(ax, ay)) > 1e-3 || Math.Abs(Dot(ax, az)) > 1e-3 || Math.Abs(Dot(ay, az)) > 1e-3)
            {
                sheared = true;
            }
            // A mirrored copy: keep a proper rotation and carry the mirror in the z scale.
            if (Dot(Cross(ax, ay), az) < 0)
            {
                sz = -sz;
                for (var k = 0; k < 3; k++)
                {
                    az[k] = -az[k];
                }
            }
            var local = new Placement(
                [m.Unknown4.X, m.Unknown4.Y, m.Unknown4.Z],
                QuaternionOf(ax, ay, az),
                [(float)sx, (float)sy, (float)sz]);
            list.Add(Compose(entry, local));
        }
        if (sheared)
        {
            warnings.Add($"entry {index}: an occluder copy's axes are not square to each other; its shear is left out");
        }
        return list;
    }

    private static double Length(double[] v) => Math.Sqrt(Dot(v, v));

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static double[] Cross(double[] a, double[] b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

    /// <summary>The unit quaternion (x y z w) of the rotation taking the local x, y and z axes to <paramref name="ax"/>, <paramref name="ay"/> and <paramref name="az"/>.</summary>
    private static float[] QuaternionOf(double[] ax, double[] ay, double[] az)
    {
        // The matrix's columns are the axes: m[row, col].
        double m00 = ax[0], m10 = ax[1], m20 = ax[2];
        double m01 = ay[0], m11 = ay[1], m21 = ay[2];
        double m02 = az[0], m12 = az[1], m22 = az[2];
        double x, y, z, w;
        var trace = m00 + m11 + m22;
        if (trace > 0)
        {
            var s = Math.Sqrt(trace + 1) * 2;
            w = s / 4; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            var s = Math.Sqrt(1 + m00 - m11 - m22) * 2;
            w = (m21 - m12) / s; x = s / 4; y = (m01 + m10) / s; z = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            var s = Math.Sqrt(1 + m11 - m00 - m22) * 2;
            w = (m02 - m20) / s; x = (m01 + m10) / s; y = s / 4; z = (m12 + m21) / s;
        }
        else
        {
            var s = Math.Sqrt(1 + m22 - m00 - m11) * 2;
            w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = s / 4;
        }
        var n = Math.Sqrt(x * x + y * y + z * z + w * w);
        return [(float)(x / n), (float)(y / n), (float)(z / n), (float)(w / n)];
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
