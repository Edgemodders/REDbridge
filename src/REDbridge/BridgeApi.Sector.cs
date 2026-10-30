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
        /// <summary>Metres. The distance the node streams in at (the node data's streamingDistance).</summary>
        public float? Primary { get; set; }
        /// <summary>Metres. The node's secondary streaming distance (secondaryRefPointDistance); none means always.</summary>
        public float? Secondary { get; set; }
    }

    public sealed record SectorBuildResult(string Name, string Sector, string Block, string Xl, string SectorPath, string BlockPath, int Nodes, Bounds StreamingBox, IReadOnlyList<string> Warnings);

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
    /// Nothing is written when validation fails.
    /// </summary>
    [JsonRpcMethod("sector.build")]
    public SectorBuildResult BuildSector(string project, string? name = null, string? sceneFile = null, JsonElement? scene = null, float? margin = null)
    {
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
        if (model.Format != 1)
        {
            errors.Add($"Scene format {model.Format} is not supported; this bridge writes format 1");
        }
        if (model.Objects.Count == 0)
        {
            errors.Add("The scene has no objects");
        }
        var refs = new HashSet<string>(StringComparer.Ordinal);
        var gameLoaded = _host.GameDir is not null;
        if (!gameLoaded)
        {
            warnings.Add("No game loaded: resources were not checked and bounds not read; nodes without bounds get a 1 m box");
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
            if (gameLoaded && !string.IsNullOrEmpty(o.Resource) && !_host.Archives.Lookup(o.Resource.Replace('/', '\\')).HasValue)
            {
                errors.Add($"{label}: '{o.Resource}' is not in the game's archives");
            }
        }
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"The scene did not validate:\n  {string.Join("\n  ", errors)}");
        }

        // Bounds per object: given, read from the game, or a unit box.
        var relFolder = Path.Combine("edgebuilder", safeName);
        var sectorPath = Path.Combine(relFolder, safeName + ".streamingsector");
        var blockPath = Path.Combine(relFolder, safeName + ".streamingblock");

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
            var local = o.Bounds ?? (gameLoaded && o.Kind != "light" ? TryReadBounds(o.Resource!, warnings) : null) ?? new Bounds([-0.5f, -0.5f, 0f], [0.5f, 0.5f, 1f]);
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
        var streamingBox = new Bounds(
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

        var archiveDir = Path.Combine(projectDir, "source", "archive");
        var sectorFile = WriteCr2w(Path.Combine(archiveDir, sectorPath), sector);
        var blockFile = WriteCr2w(Path.Combine(archiveDir, blockPath), block);
        // Named <name>.archive.xl, so it sits beside <name>.archive when the project is packed
        // under that mod name: the pairing EDGEbuilder's bake probe loaded in game 2.5 (issue #13).
        var xlDir = Path.Combine(projectDir, "source", "resources", "archive", "pc", "mod");
        var xlFile = Path.Combine(xlDir, safeName + ".archive.xl");
        Directory.CreateDirectory(xlDir);
        File.WriteAllText(xlFile, XlFor(safeName, blockPath), new UTF8Encoding(false));
        // Before 0.12.0 the file was <name>.xl with a resource patch; left beside the new one it
        // would add the block a second time, so it goes when it is ours.
        var oldXl = Path.Combine(xlDir, safeName + ".xl");
        if (File.Exists(oldXl) && File.ReadLines(oldXl).FirstOrDefault()?.StartsWith("# Written by REDbridge sector.build") == true)
        {
            File.Delete(oldXl);
            warnings.Add($"Removed {safeName}.xl, written by an earlier sector.build; {safeName}.archive.xl replaces it");
        }
        _host.Log.Info($"sector.build {safeName}: {model.Objects.Count} nodes -> {sectorFile}");
        return new SectorBuildResult(safeName, sectorFile, blockFile, xlFile, sectorPath, blockPath, model.Objects.Count, streamingBox, warnings);
    }

    private worldNode NodeFor(SceneObject o)
    {
        var appearance = string.IsNullOrEmpty(o.Appearance) ? "default" : o.Appearance;
        switch (o.Kind)
        {
            case "mesh":
                return new worldMeshNode
                {
                    Mesh = new CResourceAsyncReference<CMesh>(o.Resource!.Replace('/', '\\'), InternalEnums.EImportFlags.Soft),
                    MeshAppearance = appearance,
                };
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

    private static string Prop(Dictionary<string, JsonElement> p, string key, string fallback) =>
        p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;

    private static float Prop(Dictionary<string, JsonElement> p, string key, float fallback) =>
        p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

    private static float[] Prop(Dictionary<string, JsonElement> p, string key, float[] fallback) =>
        p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == fallback.Length ? v.EnumerateArray().Select(e => e.GetSingle()).ToArray() : fallback;

    private Bounds? TryReadBounds(string resource, List<string> warnings)
    {
        try
        {
            var info = InfoOf(Find(resource));
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

    private static string XlFor(string name, string blockPath) =>
        $"""
        # Written by REDbridge sector.build for the EDGEbuilder scene "{name}".
        # ArchiveXL merges this streaming block into the world's, so the sector it names streams in.
        streaming:
          blocks:
            - {blockPath}

        """;
}
