using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;
using WolvenKit.Common.FNV1A;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// Bakes an EDGEbuilder scene into game files: one streaming sector with a node per object, one
/// streaming block whose descriptor holds the sector's streaming box, and an ArchiveXL file,
/// <c>&lt;name&gt;.archive.xl</c>, that adds the block to the world under <c>streaming: blocks:</c>
/// (the form EDGEbuilder's bake probe loaded in game 2.5 with ArchiveXL 1.28.6, issue #13). Written with WolvenKit's own writers into a REDcode project,
/// under source/archive (game files) and source/resources (the .xl). Asked for by EDGEbuilder (issue #1).
///
/// The node-data fields follow base-game 2.5 sectors of the same node types: node flags, streaming
/// priority, reference points and the sector version were copied from mesh, entity and light nodes.
/// Streaming ranges default to a function of the object's size and can be set per object.
/// Game 2.5 changed the node-data record (worldCompiledNodeInstanceSetupInfo); the bridge writes
/// only the 2.5 form.
/// </summary>
public sealed partial class BridgeApi
{
    // ---- the scene file, EDGEbuilder's format 1 ---------------------------------------------

    public sealed class Scene
    {
        public int Format { get; set; } = 1;
        public string Name { get; set; } = "";
        public string? Game { get; set; }
        public List<SceneObject> Objects { get; set; } = [];
    }

    public sealed class SceneObject
    {
        public string Id { get; set; } = "";
        /// <summary>mesh, entity or light.</summary>
        public string Kind { get; set; } = "mesh";
        /// <summary>The game path of the mesh or entity template; none for a light.</summary>
        public string? Resource { get; set; }
        public string? Appearance { get; set; }
        /// <summary>A mesh that stops V, bullets and cars, baked as a kinematic instanced destructible mesh node (issue #15).</summary>
        public bool Solid { get; set; }
        public SceneTransform Transform { get; set; } = new();
        public SceneStreaming? Streaming { get; set; }
        /// <summary>The node reference, unique in the scene. Generated from the id when missing.</summary>
        public string? Ref { get; set; }
        /// <summary>The resource's local bounds, when the caller has them; else they are read from the game.</summary>
        public Bounds? Bounds { get; set; }
        public Dictionary<string, JsonElement>? Props { get; set; }
        public string? Group { get; set; }
    }

    public sealed class SceneTransform
    {
        public float[] Position { get; set; } = [0, 0, 0];
        /// <summary>A quaternion, x y z w.</summary>
        public float[] Rotation { get; set; } = [0, 0, 0, 1];
        public float[] Scale { get; set; } = [1, 1, 1];
    }

    public sealed class SceneStreaming
    {
        /// <summary>Metres. The distance the node streams in at (the node data's streamingDistance); for a mesh, also the distance it hides at (forceAutoHideDistance, issue #16).</summary>
        public float? Primary { get; set; }
        /// <summary>Metres. The node's secondary streaming distance (secondaryRefPointDistance); none means always.</summary>
        public float? Secondary { get; set; }
    }

    /// <summary>
    /// A node-data entry of the game's own sectors to remove (issue #18). <see cref="Index"/> is the
    /// entry's index in the sector's node data (what sector.nodes calls <c>index</c>, and REDnode's
    /// registry the number after the colon), <see cref="Kind"/> the class of the node it places (with
    /// or without the <c>world</c> prefix), <see cref="Hash"/> the entry's global node id when it has
    /// one, decimal or <c>0x</c> hex, a string or a number. For an entity, its proxy meshes go too
    /// unless <see cref="Proxies"/> is false.
    /// </summary>
    public sealed class SectorRemoval
    {
        public string Sector { get; set; } = "";
        public int? Index { get; set; }
        public string Kind { get; set; } = "";
        public JsonElement? Hash { get; set; }
        public bool? Proxies { get; set; }
    }

    /// <summary>The entity entry a removed proxy mesh stands in for.</summary>
    public sealed record ProxyOwner(string Sector, int Index);

    /// <summary>
    /// A removed entry: its sector, its index, the node it places and that node's class, its global
    /// node id, and for a proxy mesh removed with its entity, <see cref="ProxyOf"/>.
    /// </summary>
    public sealed record RemovedNode(string Sector, int Index, int Node, string Kind, string? Id, ProxyOwner? ProxyOf);

    public sealed record RefusedRemoval(string Sector, int? Index, string Why);

    /// <summary>
    /// <see cref="Sector"/>, <see cref="Block"/>, their paths and <see cref="StreamingBox"/> are null
    /// when the scene has no objects (a bake of removals only); <see cref="Xl"/> is null when nothing
    /// was written at all.
    /// </summary>
    public sealed record SectorBuildResult(string Name, string? Sector, string? Block, string? Xl, string? SectorPath, string? BlockPath, int Nodes, Bounds? StreamingBox, IReadOnlyList<string> Warnings, IReadOnlyList<RemovedNode> Removed, IReadOnlyList<RefusedRemoval> Refused);

    private sealed record SectorDeletions(string Path, int ExpectedNodes, IReadOnlyList<(int Index, string Type)> Nodes);

    private static readonly JsonSerializerOptions SceneJson = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Node flags and streaming priority seen on most base-game nodes of each type in 2.5's exterior sectors
    /// (12 sectors sampled on 2026-10-25).
    /// </summary>
    private static readonly Dictionary<string, (Enums.ENodeFlags Flags, ulong Priority)> NodeSetup = new()
    {
        ["mesh"] = (Enums.ENodeFlags.FLAG_Building | Enums.ENodeFlags.FLAG_Culling_IsCullable | Enums.ENodeFlags.FLAG_AllowDistanceBoosting, 1),
        ["entity"] = (Enums.ENodeFlags.FLAG_AllowForceStreamIn | Enums.ENodeFlags.FLAG_Building | Enums.ENodeFlags.FLAG_Culling_IsCullable | Enums.ENodeFlags.FLAG_Culling_IsForceCulledNearPeripheral, 21),
        ["light"] = (Enums.ENodeFlags.FLAG_Building, 1),
    };

    private const uint NodeDataBufferFlags = 7471104; // as on base-game 2.5 sectors
    private const int SectorVersion = 68; // as on base-game 2.5 sectors

    /// <summary>
    /// Bakes a scene. <paramref name="project"/> is a REDcode project folder (or its .cpmodproj);
    /// <paramref name="sceneFile"/> or <paramref name="scene"/> gives the scene; <paramref name="name"/>
    /// names the files and defaults to the scene's name; <paramref name="margin"/> is added around the
    /// nodes' bounds for the streaming box and defaults to the largest streaming range, at least 100.
    /// <paramref name="archive"/> (a mod's .archive) and <paramref name="root"/> (a folder of loose
    /// files, such as a project's source\archive) are searched before the game's archives for the
    /// objects' resources (issue #12). A resource found nowhere is an error, or a warning that names
    /// it when the scene gives the object's bounds: the game then needs the mod that has it.
    /// Nothing is written when validation fails.
    /// </summary>
    /// <para>
    /// <paramref name="removals"/> (issue #18) removes nodes of the game's own sectors through
    /// ArchiveXL's node deletions, written into the same .xl under <c>streaming: sectors:</c>. ArchiveXL
    /// deletes node-data entries, so each removal names an entry, and is checked against its sector
    /// first: the entry in range, the node it places of that class, and with a hash, the entry's own
    /// global node id. One that fails is not written and comes back in <c>refused</c> with why; the rest
    /// of the bake goes on. <c>expectedNodes</c> is the sector's entry count, so ArchiveXL skips the
    /// deletions if another version of the sector is loaded. An entity's proxy meshes (a
    /// <c>worldEntityProxyMeshNode</c> whose <c>ownerGlobalId</c> is the entity's id, in a sector whose
    /// streaming box holds the entity, quest sectors only for an entity in one; the game shows a proxy
    /// while its entity is not there) are removed
    /// with it, in <c>removed</c> with <c>proxyOf</c>. A scene with removals and no objects writes only
    /// the .xl.
    /// </para>
    [JsonRpcMethod("sector.build")]
    public SectorBuildResult BuildSector(string project, string? name = null, string? sceneFile = null, JsonElement? scene = null, float? margin = null, string? archive = null, string? root = null, JsonElement? removals = null)
    {
        using var mods = ModFiles.Open(_host, archive, root);
        var sceneText = sceneFile is not null ? File.ReadAllText(sceneFile) : scene?.GetRawText() ?? throw new ArgumentException("sceneFile or scene is required");
        var model = JsonSerializer.Deserialize<Scene>(sceneText, SceneJson) ?? throw new InvalidDataException("The scene is not JSON");
        var projectDir = project.EndsWith(".cpmodproj", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(Path.GetFullPath(project))! : Path.GetFullPath(project);
        if (!Directory.Exists(projectDir))
        {
            throw new DirectoryNotFoundException($"Project folder not found: {projectDir}");
        }
        var safeName = SafeName(name ?? model.Name);
        if (safeName.Length == 0)
        {
            throw new ArgumentException("The scene needs a name: pass name, or set name in the scene");
        }

        var errors = new List<string>();
        var warnings = new List<string>();
        var asked = new List<SectorRemoval>();
        if (removals is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } r)
        {
            asked = r.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<SectorRemoval>>(r.GetRawText(), SceneJson) ?? []
                : throw new ArgumentException("removals must be a list of { sector, index, kind, hash }");
        }
        if (model.Format != 1)
        {
            errors.Add($"Scene format {model.Format} is not supported; this bridge writes format 1");
        }
        if (model.Objects.Count == 0 && asked.Count == 0)
        {
            errors.Add("The scene has no objects and no removals");
        }
        var refs = new HashSet<string>(StringComparer.Ordinal);
        var gameLoaded = _host.GameDir is not null;
        if (!gameLoaded)
        {
            warnings.Add(mods.IsEmpty
                ? "No game loaded: resources were not checked and bounds not read; nodes without bounds get a 1 m box"
                : $"No game loaded: resources were not checked, and bounds were read only from {mods.Where}; nodes without bounds get a 1 m box");
        }
        for (var i = 0; i < model.Objects.Count; i++)
        {
            var o = model.Objects[i];
            var label = $"object {i} ({(string.IsNullOrEmpty(o.Id) ? "no id" : o.Id)})";
            if (string.IsNullOrEmpty(o.Id))
            {
                errors.Add($"{label}: needs an id");
            }
            if (!NodeSetup.ContainsKey(o.Kind))
            {
                errors.Add($"{label}: kind '{o.Kind}' is not mesh, entity or light");
            }
            if (o.Kind != "light" && string.IsNullOrEmpty(o.Resource))
            {
                errors.Add($"{label}: a {o.Kind} needs a resource path");
            }
            if (o.Transform.Position.Length != 3 || o.Transform.Rotation.Length != 4 || o.Transform.Scale.Length != 3)
            {
                errors.Add($"{label}: transform needs position[3], rotation[4] (a quaternion) and scale[3]");
            }
            o.Ref ??= $"edgebuilder/{safeName}/{o.Id}";
            if (!refs.Add(o.Ref))
            {
                errors.Add($"{label}: the reference '{o.Ref}' is used twice");
            }
            if (gameLoaded && !string.IsNullOrEmpty(o.Resource) && !_host.Archives.Lookup(o.Resource.Replace('/', '\\')).HasValue && !mods.Has(o.Resource))
            {
                var where = mods.IsEmpty ? "the game's archives" : $"{mods.Where}, nor in the game's archives";
                if (o.Bounds is null)
                {
                    errors.Add($"{label}: '{o.Resource}' is not in {where}; give the scene its bounds, or pass the mod's archive or root");
                }
                else
                {
                    warnings.Add($"{label}: '{o.Resource}' is not in {where}; the scene's bounds are used, and the game needs the mod that has it");
                }
            }
        }
        foreach (var o in model.Objects.Where(o => o.Solid))
        {
            if (o.Kind != "mesh")
            {
                warnings.Add($"object '{o.Id}': solid is for a mesh; a {o.Kind}'s collision comes from what it is made of, so it is baked as usual");
            }
            else if (!string.IsNullOrEmpty(o.Resource) && (gameLoaded || !mods.IsEmpty) && CollisionOf(o.Resource, mods, warnings) == false)
            {
                warnings.Add($"object '{o.Id}': '{o.Resource}' has no collision data, so it shows but is not solid");
            }
        }
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"The scene did not validate:\n  {string.Join("\n  ", errors)}");
        }

        // Node removals in the game's own sectors (issue #18), checked before anything is written.
        var (deletions, removed, refused) = CheckRemovals(asked, mods, warnings);

        var relFolder = Path.Combine("edgebuilder", safeName);
        var sectorPath = Path.Combine(relFolder, safeName + ".streamingsector");
        var blockPath = Path.Combine(relFolder, safeName + ".streamingblock");
        var archiveDir = Path.Combine(projectDir, "source", "archive");
        string? sectorFile = null, blockFile = null;
        Bounds? streamingBox = null;
        if (model.Objects.Count > 0)
        {
            // Bounds per object: given, read from the game, or a unit box.
            var sector = new worldStreamingSector
            {
                CookingPlatform = Enums.ECookingPlatform.PLATFORM_PC,
                Version = SectorVersion,
                Level = 0,
                Category = Enums.worldStreamingSectorCategory.Exterior,
            };
            var nodeData = new worldNodeDataBuffer();
            float[] boxMin = [float.MaxValue, float.MaxValue, float.MaxValue];
            float[] boxMax = [float.MinValue, float.MinValue, float.MinValue];
            var largestRange = 0f;

            for (var i = 0; i < model.Objects.Count; i++)
            {
                var o = model.Objects[i];
                var local = o.Bounds ?? ((gameLoaded || !mods.IsEmpty) && o.Kind != "light" ? TryReadBounds(o.Resource!, mods, warnings) : null) ?? new Bounds([-0.5f, -0.5f, 0f], [0.5f, 0.5f, 1f]);
                var world = o.Kind == "light" ? PointBounds(o.Transform.Position) : WorldBounds(local, o.Transform);
                var size = Math.Max(Math.Max(world.Max[0] - world.Min[0], world.Max[1] - world.Min[1]), world.Max[2] - world.Min[2]);
                var primary = o.Streaming?.Primary ?? Math.Clamp(size * 11f, 40f, 400f);
                var secondary = o.Streaming?.Secondary ?? primary * 0.8f;
                largestRange = Math.Max(largestRange, Math.Max(primary, secondary));

                sector.Nodes.Add(new CHandle<worldNode>(NodeFor(o)));
                var setup = NodeSetup[o.Kind];
                var position = new Vector3 { X = o.Transform.Position[0], Y = o.Transform.Position[1], Z = o.Transform.Position[2] };
                // Base-game mesh nodes stream from the middle of their bounds; entities and lights from their position.
                var refPoint = o.Kind == "mesh"
                    ? new Vector3 { X = (world.Min[0] + world.Max[0]) / 2, Y = (world.Min[1] + world.Max[1]) / 2, Z = (world.Min[2] + world.Max[2]) / 2 }
                    : position;
                nodeData.Add(new worldCompiledNodeInstanceSetupInfo
                {
                    Transform = new Transform
                    {
                        Position = new Vector4 { X = position.X, Y = position.Y, Z = position.Z, W = 0 },
                        Orientation = new Quaternion { I = o.Transform.Rotation[0], J = o.Transform.Rotation[1], K = o.Transform.Rotation[2], R = o.Transform.Rotation[3] },
                    },
                    Scale = new Vector3 { X = o.Transform.Scale[0], Y = o.Transform.Scale[1], Z = o.Transform.Scale[2] },
                    SecondaryRefPointPosition = new Vector3 { X = refPoint.X, Y = refPoint.Y, Z = refPoint.Z },
                    StreamingRefPoint = new Vector3 { X = refPoint.X, Y = refPoint.Y, Z = refPoint.Z },
                    PreboostStreamingRefPoint = new Vector3 { X = position.X, Y = position.Y, Z = position.Z },
                    PatchedNode = 0,
                    GlobalNodeId = new worldGlobalNodeID { Hash = (ulong)(NodeRef)o.Ref! },
                    ProxyMeshId = new worldGlobalNodeID { Hash = 0 },
                    ExternalInplaceResourceHash = new CResourceReference<worldCookedPrefabData>(0UL),
                    NodeIdx = (ulong)i,
                    NodeFlags = setup.Flags,
                    StreamingPriority = setup.Priority,
                    TodvisMask = 0,
                    SecondaryRefPointDistance = o.Streaming?.Secondary is null && o.Streaming?.Primary is null ? secondary : (o.Streaming.Secondary ?? float.MaxValue),
                    StreamingDistance = primary,
                    InstanceIndex = uint.MaxValue,
                    DataGroup = Enums.worldStreamingDataGroup.Base,
                });
                for (var k = 0; k < 3; k++)
                {
                    boxMin[k] = Math.Min(boxMin[k], world.Min[k]);
                    boxMax[k] = Math.Max(boxMax[k], world.Max[k]);
                }
            }

            var buffer = RedBuffer.CreateBuffer(NodeDataBufferFlags, []);
            buffer.Data = nodeData;
            buffer.Parent = sector;
            buffer.ParentTypes.Add("worldStreamingSector.transforms");
            sector.NodeData = new DataBuffer { Buffer = buffer };
            sector.VariantIndices.Add(0);
            sector.PersisentNodeIndex = 0;

            var pad = margin ?? Math.Max(100f, largestRange);
            streamingBox = new Bounds(
                [boxMin[0] - pad, boxMin[1] - pad, boxMin[2] - pad],
                [boxMax[0] + pad, boxMax[1] + pad, boxMax[2] + pad]);
            var block = new worldStreamingBlock { CookingPlatform = Enums.ECookingPlatform.PLATFORM_PC };
            block.Descriptors.Add(new worldStreamingSectorDescriptor
            {
                Data = new CResourceAsyncReference<worldStreamingSector>(sectorPath, InternalEnums.EImportFlags.Soft),
                StreamingBox = new Box
                {
                    Min = new Vector4 { X = streamingBox.Min[0], Y = streamingBox.Min[1], Z = streamingBox.Min[2], W = 1 },
                    Max = new Vector4 { X = streamingBox.Max[0], Y = streamingBox.Max[1], Z = streamingBox.Max[2], W = 1 },
                },
                Level = 0,
                Category = Enums.worldStreamingSectorCategory.Exterior,
                NumNodeRanges = 1,
                BlockIndex = new worldStreamingBlockIndex { Oup = Enums.worldStreamingDataGroup.Base },
            });
            block.Index = new worldStreamingBlockIndex { Oup = Enums.worldStreamingDataGroup.Base };

            sectorFile = WriteCr2w(Path.Combine(archiveDir, sectorPath), sector);
            blockFile = WriteCr2w(Path.Combine(archiveDir, blockPath), block);
        }
        else
        {
            // A bake of removals only: an earlier bake's sector and block would be packed for nothing.
            foreach (var stale in new[] { sectorPath, blockPath })
            {
                var file = Path.Combine(archiveDir, stale);
                if (File.Exists(file))
                {
                    File.Delete(file);
                    warnings.Add($"Removed {stale}, written by an earlier bake: the scene has no objects now");
                }
            }
        }

        // Named <name>.archive.xl, so it sits beside <name>.archive when the project is packed
        // under that mod name: the pairing EDGEbuilder's bake probe loaded in game 2.5 (issue #13).
        var xlDir = Path.Combine(projectDir, "source", "resources", "archive", "pc", "mod");
        var xlFile = Path.Combine(xlDir, safeName + ".archive.xl");
        string? xlWritten = null;
        if (model.Objects.Count > 0 || deletions.Count > 0)
        {
            Directory.CreateDirectory(xlDir);
            File.WriteAllText(xlFile, XlFor(safeName, model.Objects.Count > 0 ? blockPath : null, deletions), new UTF8Encoding(false));
            xlWritten = Path.GetFullPath(xlFile);
        }
        else
        {
            if (IsOurXl(xlFile))
            {
                File.Delete(xlFile);
                warnings.Add($"Removed {safeName}.archive.xl, written by an earlier bake");
            }
            warnings.Add("Nothing written: the scene has no objects and every removal was refused");
        }
        // Before 0.12.0 the file was <name>.xl with a resource patch; left beside the new one it
        // would add the block a second time, so it goes when it is ours.
        var oldXl = Path.Combine(xlDir, safeName + ".xl");
        if (IsOurXl(oldXl))
        {
            File.Delete(oldXl);
            warnings.Add($"Removed {safeName}.xl, written by an earlier sector.build; {safeName}.archive.xl replaces it");
        }
        _host.Log.Info($"sector.build {safeName}: {model.Objects.Count} nodes, {removed.Count} removals ({refused.Count} refused) -> {xlWritten ?? "nothing"}");
        return new SectorBuildResult(safeName, sectorFile, blockFile, xlWritten, sectorFile is null ? null : sectorPath, blockFile is null ? null : blockPath, model.Objects.Count, streamingBox, warnings, removed, refused);
    }

    private worldNode NodeFor(SceneObject o)
    {
        var appearance = string.IsNullOrEmpty(o.Appearance) ? "default" : o.Appearance;
        switch (o.Kind)
        {
            case "mesh":
            {
                var mesh = o.Solid
                    ? SolidMeshNode(o.Resource!, appearance)
                    : new worldMeshNode
                    {
                        Mesh = new CResourceAsyncReference<CMesh>(o.Resource!.Replace('/', '\\'), InternalEnums.EImportFlags.Soft),
                        MeshAppearance = appearance,
                    };
                // The game hides a small mesh by its size whatever its streaming distance says, so a
                // streaming distance from the scene is also the hide distance; 0 lets the game decide (issue #16).
                if (o.Streaming?.Primary is float seenFrom)
                {
                    mesh.ForceAutoHideDistance = seenFrom;
                }
                return mesh;
            }
            case "entity":
                return new worldEntityNode
                {
                    EntityTemplate = new CResourceAsyncReference<entEntityTemplate>(o.Resource!.Replace('/', '\\'), InternalEnums.EImportFlags.Soft),
                    AppearanceName = appearance,
                };
            case "light":
            {
                var p = o.Props ?? [];
                var light = new worldStaticLightNode
                {
                    Type = Prop(p, "type", "point") switch { "spot" => Enums.ELightType.LT_Spot, "area" => Enums.ELightType.LT_Area, _ => Enums.ELightType.LT_Point },
                    Intensity = Prop(p, "intensity", 100f),
                    Radius = Prop(p, "radius", 5f),
                    InnerAngle = Prop(p, "innerAngle", 30f),
                    OuterAngle = Prop(p, "outerAngle", 45f),
                    EV = Prop(p, "ev", 0f),
                    Temperature = Prop(p, "temperature", -1f),
                };
                var color = Prop(p, "color", new float[] { 255, 255, 255 });
                light.Color = new CColor { Red = (byte)Math.Clamp(color[0], 0, 255), Green = (byte)Math.Clamp(color[1], 0, 255), Blue = (byte)Math.Clamp(color[2], 0, 255), Alpha = 255 };
                return light;
            }
            default:
                throw new InvalidDataException($"kind '{o.Kind}'");
        }
    }

    /// <summary>
    /// A mesh that stops V, bullets, melee and cars and stays put: the node EDGEbuilder's bake probe
    /// proved in game 2.5 (round 3, issue #15). The masks are the game's World Dynamic preset. With
    /// turnDynamicOnImpulse on, or as a worldDynamicMeshNode, V walks through; the mesh needs
    /// meshMeshParamPhysics to be solid.
    /// </summary>
    private static worldInstancedDestructibleMeshNode SolidMeshNode(string resource, string appearance) => new()
    {
        Mesh = new CResourceAsyncReference<CMesh>(resource.Replace('/', '\\'), InternalEnums.EImportFlags.Soft),
        MeshAppearance = appearance,
        SimulationType = Enums.physicsSimulationType.Kinematic,
        IsDestructible = false,
        TurnDynamicOnImpulse = false,
        StartInactive = false,
        IsPierceable = false,
        IsWorkspot = false,
        UseAggregate = false,
        EnableSelfCollisionInAggregate = false,
        FilterDataSource = Enums.physicsFilterDataSource.Parent,
        FilterData = new CHandle<physicsFilterData>(new physicsFilterData
        {
            Preset = "World Dynamic",
            QueryFilter = new physicsQueryFilter { Mask1 = 0, Mask2 = 143753224 },
            SimulationFilter = new physicsSimulationFilter { Mask1 = 114696, Mask2 = 9223372036854780879 },
        }),
        DamageThreshold = 1,
        DamageEndurance = 30,
        AccumulateDamage = true,
        ImpulseToDamage = 1,
        FracturingEffect = new CResourceAsyncReference<worldEffect>(0UL, InternalEnums.EImportFlags.Soft),
        IdleEffect = new CResourceAsyncReference<worldEffect>(0UL, InternalEnums.EImportFlags.Soft),
        StaticMesh = new CResourceAsyncReference<CMesh>(0UL, InternalEnums.EImportFlags.Soft),
        StaticMeshAppearance = "None",
        NavigationSetting = new NavGenNavigationSetting { NavmeshImpact = Enums.NavGenNavmeshImpact.Blocking },
        UseMeshNavmeshSettings = true,
        SystemsToNotifyFlags = 14,
        InstanceTransforms = [new Transform { Position = new Vector4 { X = 0, Y = 0, Z = 0, W = 0 }, Orientation = new Quaternion { I = 0, J = 0, K = 0, R = 1 } }],
    };

    /// <summary>Whether a mesh has collision data; null, after a warning, when it cannot be read or is no mesh. A mesh found nowhere has its own warning.</summary>
    private bool? CollisionOf(string resource, ModFiles mods, List<string> warnings)
    {
        if (!_host.Archives.Lookup(resource.Replace('/', '\\')).HasValue && !mods.Has(resource))
        {
            return null;
        }
        try
        {
            return InfoOf(resource, mods).HasCollision;
        }
        catch (Exception e)
        {
            warnings.Add($"{resource}: whether it has collision data was not read ({e.Message})");
            return null;
        }
    }

    private static string Prop(Dictionary<string, JsonElement> p, string key, string fallback) =>
        p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;

    private static float Prop(Dictionary<string, JsonElement> p, string key, float fallback) =>
        p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

    private static float[] Prop(Dictionary<string, JsonElement> p, string key, float[] fallback) =>
        p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == fallback.Length ? v.EnumerateArray().Select(e => e.GetSingle()).ToArray() : fallback;

    private Bounds? TryReadBounds(string resource, ModFiles mods, List<string> warnings)
    {
        try
        {
            var info = InfoOf(resource, mods);
            if (info.Bounds is null)
            {
                warnings.Add($"{resource}: no bounds in the file; a 1 m box is used");
            }
            return info.Bounds;
        }
        catch (Exception e)
        {
            warnings.Add($"{resource}: bounds not read ({e.Message}); a 1 m box is used");
            return null;
        }
    }

    private static Bounds PointBounds(float[] p) => new([p[0], p[1], p[2]], [p[0], p[1], p[2]]);

    /// <summary>The axis-aligned box around the local box after scale, rotation and translation.</summary>
    private static Bounds WorldBounds(Bounds local, SceneTransform t)
    {
        float[] min = [float.MaxValue, float.MaxValue, float.MaxValue];
        float[] max = [float.MinValue, float.MinValue, float.MinValue];
        for (var corner = 0; corner < 8; corner++)
        {
            var x = ((corner & 1) == 0 ? local.Min[0] : local.Max[0]) * t.Scale[0];
            var y = ((corner & 2) == 0 ? local.Min[1] : local.Max[1]) * t.Scale[1];
            var z = ((corner & 4) == 0 ? local.Min[2] : local.Max[2]) * t.Scale[2];
            var (rx, ry, rz) = Rotate(x, y, z, t.Rotation);
            float[] w = [rx + t.Position[0], ry + t.Position[1], rz + t.Position[2]];
            for (var k = 0; k < 3; k++)
            {
                min[k] = Math.Min(min[k], w[k]);
                max[k] = Math.Max(max[k], w[k]);
            }
        }
        return new Bounds(min, max);
    }

    /// <summary>Rotates a vector by a unit quaternion (x, y, z, w).</summary>
    private static (float, float, float) Rotate(float x, float y, float z, float[] q)
    {
        float qx = q[0], qy = q[1], qz = q[2], qw = q[3];
        // t = 2 * cross(q.xyz, v); v' = v + w * t + cross(q.xyz, t)
        var tx = 2 * (qy * z - qz * y);
        var ty = 2 * (qz * x - qx * z);
        var tz = 2 * (qx * y - qy * x);
        return (x + qw * tx + (qy * tz - qz * ty), y + qw * ty + (qz * tx - qx * tz), z + qw * tz + (qx * ty - qy * tx));
    }

    private static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        }
        return sb.ToString().Trim('_');
    }

    private string WriteCr2w(string file, RedBaseClass root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var cr2w = new CR2WFile { RootChunk = root };
        using var fs = File.Create(file);
        using var writer = new CR2WWriter(fs) { LoggerService = _host.Log };
        writer.WriteFile(cr2w);
        return Path.GetFullPath(file);
    }

    private const string XlMark = "# Written by REDbridge sector.build";

    private static bool IsOurXl(string file) =>
        File.Exists(file) && File.ReadLines(file).FirstOrDefault()?.StartsWith(XlMark, StringComparison.Ordinal) == true;

    /// <summary>
    /// The .xl: the scene's block under <c>streaming: blocks:</c>, and ArchiveXL's node deletions
    /// per game sector under <c>streaming: sectors:</c> (issue #18).
    /// </summary>
    private static string XlFor(string name, string? blockPath, IReadOnlyList<SectorDeletions> sectors)
    {
        var sb = new StringBuilder();
        sb.Append(XlMark).Append($" for the EDGEbuilder scene \"{name}\".\n");
        if (blockPath is not null)
        {
            sb.Append("# ArchiveXL merges this streaming block into the world's, so the sector it names streams in.\n");
        }
        if (sectors.Count > 0)
        {
            sb.Append("# ArchiveXL removes the listed node-data entries from the game's sectors, only while each sector has expectedNodes entries.\n");
        }
        sb.Append("streaming:\n");
        if (blockPath is not null)
        {
            sb.Append("  blocks:\n");
            sb.Append($"    - {blockPath}\n");
        }
        if (sectors.Count > 0)
        {
            sb.Append("  sectors:\n");
            foreach (var s in sectors)
            {
                sb.Append($"    - path: {s.Path}\n");
                sb.Append($"      expectedNodes: {s.ExpectedNodes.ToString(CultureInfo.InvariantCulture)}\n");
                sb.Append("      nodeDeletions:\n");
                foreach (var (index, type) in s.Nodes)
                {
                    sb.Append($"        - index: {index.ToString(CultureInfo.InvariantCulture)}\n");
                    sb.Append($"          type: {type}\n");
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>A sector read for removals: its node data as a list, or why it could not be read.</summary>
    private sealed class RemovalSector
    {
        public string Path = "";
        public worldStreamingSector? Sector;
        public List<worldCompiledNodeInstanceSetupInfo>? Entries;
        public string? Why;
    }

    /// <summary>
    /// Proxy meshes per game sector: entry, the node it places, its owner's id and its own. Kept in
    /// <c>%LOCALAPPDATA%\REDbridge\cache\sector-proxies.json</c> for the game's exe it was read from,
    /// since the first search around a place reads a few hundred sectors (about 40 s).
    /// </summary>
    private readonly Dictionary<string, List<(int Entry, int Node, ulong Owner, ulong Id)>> _proxies = new(StringComparer.OrdinalIgnoreCase);
    private string? _proxiesGame;
    private bool _proxiesChanged;

    private static string ProxiesFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "REDbridge", "cache", "sector-proxies.json");

    private sealed record ProxyCache(string Game, Dictionary<string, ulong[][]> Sectors);

    /// <summary>The game's exe size and time, so a game update starts the kept proxies afresh.</summary>
    private string GameStamp()
    {
        var exe = new FileInfo(Path.Combine(_host.GameDir ?? "", "bin", "x64", "Cyberpunk2077.exe"));
        return exe.Exists ? $"{exe.Length}-{exe.LastWriteTimeUtc.Ticks}" : _host.GameDir ?? "";
    }

    private void LoadProxies()
    {
        var stamp = GameStamp();
        if (_proxiesGame == stamp)
        {
            return;
        }
        _proxies.Clear();
        _proxiesGame = stamp;
        _proxiesChanged = false;
        try
        {
            if (File.Exists(ProxiesFile) && JsonSerializer.Deserialize<ProxyCache>(File.ReadAllText(ProxiesFile), SceneJson) is { } kept && kept.Game == stamp)
            {
                foreach (var (path, rows) in kept.Sectors)
                {
                    _proxies[path] = rows.Where(r => r.Length == 4).Select(r => ((int)r[0], (int)r[1], r[2], r[3])).ToList();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _host.Log.Warning($"sector.build: the kept proxy list could not be read, so it starts afresh: {ex.Message}");
        }
    }

    private void SaveProxies()
    {
        if (!_proxiesChanged || _proxiesGame is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProxiesFile)!);
            var rows = _proxies.ToDictionary(kv => kv.Key, kv => kv.Value.Select(x => new[] { (ulong)x.Entry, (ulong)x.Node, x.Owner, x.Id }).ToArray());
            var temp = ProxiesFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new ProxyCache(_proxiesGame, rows)));
            File.Move(temp, ProxiesFile, true);
            _proxiesChanged = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.Log.Warning($"sector.build: the proxy list could not be kept: {ex.Message}");
        }
    }

    private RemovalSector ReadForRemoval(string path, ModFiles mods, Dictionary<string, RemovalSector> seen)
    {
        var key = path.Trim().Replace('/', '\\');
        if (seen.TryGetValue(key, out var s))
        {
            return s;
        }
        s = new RemovalSector { Path = key };
        try
        {
            var (found, cr2w) = ReadResource(key, mods);
            s.Path = found;
            if (cr2w.RootChunk is not worldStreamingSector sector)
            {
                s.Why = $"'{found}' is a {TypeNameOf(cr2w.RootChunk)}, not a streaming sector";
            }
            else if (sector.NodeData.Data is not worldNodeDataBuffer data)
            {
                s.Why = $"'{found}': its node data could not be read";
            }
            else
            {
                s.Sector = sector;
                s.Entries = [.. data];
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException)
        {
            s.Why = ex.Message;
        }
        seen[key] = s;
        seen[s.Path] = s;
        return s;
    }

    private static string NodeKind(RemovalSector s, int node) =>
        node >= 0 && node < s.Sector!.Nodes.Count ? s.Sector.Nodes[node].GetValue()?.GetType().Name ?? "nothing" : "nothing";

    /// <summary>The proxy meshes a sector holds, read once per game and kept.</summary>
    private List<(int Entry, int Node, ulong Owner, ulong Id)> ProxiesIn(string path, ModFiles mods, Dictionary<string, RemovalSector> seen)
    {
        LoadProxies();
        if (mods.IsEmpty && _proxies.TryGetValue(path, out var kept))
        {
            return kept;
        }
        var list = new List<(int, int, ulong, ulong)>();
        var s = ReadForRemoval(path, mods, seen);
        if (s.Entries is not null)
        {
            for (var i = 0; i < s.Entries.Count; i++)
            {
                var e = s.Entries[i];
                var node = (int)(ulong)e.NodeIdx;
                if (node >= 0 && node < s.Sector!.Nodes.Count && s.Sector.Nodes[node].GetValue() is worldEntityProxyMeshNode proxy)
                {
                    list.Add((i, node, (ulong)proxy.OwnerGlobalId.Hash, (ulong)e.GlobalNodeId.Hash));
                }
            }
        }
        if (mods.IsEmpty && s.Entries is not null)
        {
            _proxies[path] = list;
            _proxiesChanged = true;
        }
        return list;
    }

    /// <summary>
    /// Checks each removal against its sector (read once per sector, from the mods first, then the
    /// game): the entry in range, the node it places of the class asked for, and the entry's global
    /// node id when a hash is given. Then finds each removed entity's proxy meshes. Answers the
    /// deletions to write, grouped and sorted per sector, what was removed and what was refused with why.
    /// </summary>
    private (List<SectorDeletions> Deletions, List<RemovedNode> Removed, List<RefusedRemoval> Refused) CheckRemovals(List<SectorRemoval> asked, ModFiles mods, List<string> warnings)
    {
        var removed = new List<RemovedNode>();
        var refused = new List<RefusedRemoval>();
        var seen = new Dictionary<string, RemovalSector>(StringComparer.OrdinalIgnoreCase);
        var picked = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
        var owners = new List<(string Sector, int Index, ulong Id, float[] At, bool Quest)>();

        bool Pick(string sector, int entry, string type)
        {
            if (!picked.TryGetValue(sector, out var set))
            {
                picked[sector] = set = [];
            }
            return set.TryAdd(entry, type);
        }

        foreach (var r in asked)
        {
            if (string.IsNullOrWhiteSpace(r.Sector))
            {
                refused.Add(new RefusedRemoval(r.Sector ?? "", r.Index, "needs the sector's path"));
                continue;
            }
            var s = ReadForRemoval(r.Sector, mods, seen);
            if (s.Why is not null)
            {
                refused.Add(new RefusedRemoval(s.Path, r.Index, s.Why));
                continue;
            }
            if (r.Index is not int index)
            {
                refused.Add(new RefusedRemoval(s.Path, null, "needs the node-data entry's index (sector.nodes' index)"));
                continue;
            }
            var count = s.Entries!.Count;
            if (index < 0 || index >= count)
            {
                refused.Add(new RefusedRemoval(s.Path, index, $"the sector has {count} node-data entries, so there is no entry {index}"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(r.Kind))
            {
                refused.Add(new RefusedRemoval(s.Path, index, "needs the kind, the class of the node the entry places"));
                continue;
            }
            var e = s.Entries[index];
            var node = (int)(ulong)e.NodeIdx;
            var kind = NodeKind(s, node);
            var want = r.Kind.StartsWith("world", StringComparison.Ordinal) ? r.Kind : "world" + char.ToUpperInvariant(r.Kind[0]) + r.Kind[1..];
            if (!string.Equals(kind, want, StringComparison.OrdinalIgnoreCase))
            {
                refused.Add(new RefusedRemoval(s.Path, index, $"entry {index} places node {node}, a {kind}, not a {want}"));
                continue;
            }
            var id = (ulong)e.GlobalNodeId.Hash;
            if (r.Hash is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } hashJson)
            {
                if (!TryNodeHash(hashJson, out var hash))
                {
                    refused.Add(new RefusedRemoval(s.Path, index, $"hash {hashJson.GetRawText()} is not a number (decimal, or hex with 0x)"));
                    continue;
                }
                if (hash != 0 && hash != id)
                {
                    var have = id == 0 ? "it has no id" : $"its id is {id.ToString(CultureInfo.InvariantCulture)}";
                    refused.Add(new RefusedRemoval(s.Path, index, $"entry {index} does not have the id {hash}: {have}"));
                    continue;
                }
            }
            if (!Pick(s.Path, index, kind))
            {
                warnings.Add($"{s.Path}: entry {index} is in removals twice; removed once");
                continue;
            }
            removed.Add(new RemovedNode(s.Path, index, node, kind, id == 0 ? null : id.ToString(CultureInfo.InvariantCulture), null));
            if (kind == nameof(worldEntityNode) && r.Proxies != false && id != 0)
            {
                var p = e.Transform.Position;
                owners.Add((s.Path, index, id, [p.X, p.Y, p.Z], s.Sector!.Category == Enums.worldStreamingSectorCategory.Quest));
            }
        }

        // An entity's proxy meshes, in the sectors whose streaming box holds the entity. Quest sectors'
        // boxes cover the whole world (2,789 of 3,112 sectors around V's flat door), so they are
        // searched only for an entity in a quest sector.
        if (owners.Count > 0)
        {
            if (_host.GameDir is null)
            {
                warnings.Add("The game is not loaded, so the removed entities' proxy meshes were not looked for: they may still show");
            }
            else
            {
                var started = DateTime.Now;
                var sectors = AllSectors();
                var looked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in owners)
                {
                    var at = new Bounds(o.At, o.At);
                    foreach (var cand in sectors.Where(x => Meets(x.Box, at) && (o.Quest || x.Kind != "quest")))
                    {
                        looked.Add(cand.Path);
                        foreach (var px in ProxiesIn(cand.Path, mods, seen))
                        {
                            if (px.Owner != o.Id)
                            {
                                continue;
                            }
                            var ps = ReadForRemoval(cand.Path, mods, seen);
                            if (Pick(ps.Path, px.Entry, nameof(worldEntityProxyMeshNode)))
                            {
                                removed.Add(new RemovedNode(ps.Path, px.Entry, px.Node, nameof(worldEntityProxyMeshNode), px.Id == 0 ? null : px.Id.ToString(CultureInfo.InvariantCulture), new ProxyOwner(o.Sector, o.Index)));
                            }
                        }
                    }
                }
                SaveProxies();
                _host.Log.Info($"sector.build: looked for proxies of {owners.Count} entities in {looked.Count} sectors in {(DateTime.Now - started).TotalSeconds:F1} s");
            }
        }

        var deletions = picked
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => new SectorDeletions(kv.Key, seen[kv.Key].Entries!.Count, kv.Value.Select(x => (x.Key, x.Value)).ToList()))
            .ToList();
        return (deletions, removed, refused);
    }

    private static bool TryNodeHash(JsonElement json, out ulong hash)
    {
        hash = 0;
        if (json.ValueKind == JsonValueKind.Number)
        {
            return json.TryGetUInt64(out hash);
        }
        if (json.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        var text = json.GetString()!.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out hash);
    }
}
