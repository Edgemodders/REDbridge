
using System.Text.Json.Nodes;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;
using StreamJsonRpc;
using WolvenKit.Common.Model.Arguments;
using WolvenKit.Modkit.RED4.Tools.Common;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// Synced animation sets (bridge 0.17.0, #19 for REDcode's A3). <c>anims.sync</c> reads the extra
/// joints of a clip's buffer, one per partner in the set (<c>syncTrajectory</c>, <c>syncTrajectory1</c>,
/// ...): each a partner's Root as seen from this member's own Root, which WolvenKit's glTF export
/// leaves out. The .anims export also writes spline root motion in seconds, where WolvenKit 10.0.0-beta.1
/// writes the clip's fraction (WolvenKit issue 3304), and the .anims import stores such a clip's root
/// motion as linear, since WolvenKit's spline encoder cannot.
/// </summary>
public sealed partial class BridgeApi
{
    /// <summary>Keys of one channel: times in seconds, values in glTF axes ([x,y,z] or [x,y,z,w]).</summary>
    public sealed record SyncKeys(IReadOnlyList<float> Times, IReadOnlyList<float[]> Values);

    /// <summary>One extra joint of a clip, by its name in the buffer's extraDataNames.</summary>
    public sealed record SyncTrack(string Name, SyncKeys Translation, SyncKeys Rotation);

    /// <summary>
    /// A clip and its extra joints. Error says why a clip's tracks could not be read; Note says what a
    /// clip's buffer does not hold (a SIMD clip may store no translation for its sync joints).
    /// </summary>
    public sealed record SyncClip(string Name, double Duration, uint Frames, IReadOnlyList<SyncTrack> Tracks, string? Error = null, string? Note = null);

    /// <summary>What anims.sync read: the set's path and the clips asked for.</summary>
    public sealed record AnimsSyncResult(string Path, IReadOnlyList<SyncClip> Clips);

    /// <summary>
    /// The sync tracks of a set's clips: the extra joints past the rig's own, each a partner's Root
    /// from this member's Root. <paramref name="path"/> is a game path, a hash, or an .anims file on
    /// disk; <paramref name="clips"/> names clips (all of them without it). A clip with no extra joints
    /// answers no tracks. Constant keys come as one key at time 0.
    /// </summary>
    [JsonRpcMethod("anims.sync")]
    public AnimsSyncResult AnimsSync(string path, IReadOnlyList<string>? clips = null)
    {
        CR2WFile cr2w;
        string name;
        if (Path.IsPathFullyQualified(path) && File.Exists(path))
        {
            cr2w = ReadProjectCr2w(new FileInfo(path));
            name = path;
        }
        else
        {
            RequireGame();
            var file = Find(path);
            cr2w = ReadCr2w(file);
            name = file.Name;
        }
        if (cr2w.RootChunk is not animAnimSet set)
        {
            throw new InvalidDataException($"{name} is not an animation set");
        }
        var anims = set.Animations
            .Select(e => e?.Chunk?.Animation?.Chunk)
            .OfType<animAnimation>()
            .ToList();
        if (clips is { Count: > 0 })
        {
            var have = anims.Select(a => a.Name.GetResolvedText() ?? "").ToHashSet(StringComparer.Ordinal);
            var lacking = clips.Where(c => !have.Contains(c)).Distinct().ToList();
            if (lacking.Count > 0)
            {
                throw new ArgumentException($"{name} has no clip {string.Join(", ", lacking.Select(n => $"'{n}'"))} (it has {anims.Count} clips; anims.info lists them)");
            }
            var wanted = clips.ToHashSet(StringComparer.Ordinal);
            anims = anims.Where(a => wanted.Contains(a.Name.GetResolvedText() ?? "")).ToList();
        }
        return new AnimsSyncResult(name, anims.Select(a => SyncOf(a, set)).ToList());
    }

    private static SyncClip SyncOf(animAnimation anim, animAnimSet set)
    {
        var clip = anim.Name.GetResolvedText() ?? "";
        var duration = Math.Round((double)(float)anim.Duration, 4);
        switch (anim.AnimBuffer?.Chunk)
        {
            case animAnimationBufferCompressed c:
                {
                    var extra = (int)(byte)c.NumExtraJoints;
                    if (extra == 0)
                    {
                        return new SyncClip(clip, duration, c.NumFrames, []);
                    }
                    try
                    {
                        c.ReadBuffer();
                    }
                    catch (Exception e)
                    {
                        return new SyncClip(clip, duration, c.NumFrames, [], $"its buffer could not be read: {e.Message}");
                    }
                    var first = (ushort)c.NumJoints - extra;
                    var tracks = new List<SyncTrack>(extra);
                    for (var i = 0; i < extra; i++)
                    {
                        var idx = first + i;
                        var trackName = i < c.ExtraDataNames.Count ? c.ExtraDataNames[i].GetResolvedText() ?? $"extra{i}" : $"extra{i}";
                        var t = KeysOf(c, idx, k => k is animKeyPosition p ? Position(p.Position) : null);
                        var r = KeysOf(c, idx, k => k is animKeyRotation q ? Rotation(q.Rotation) : null);
                        tracks.Add(new SyncTrack(trackName, t, r));
                    }
                    return new SyncClip(clip, duration, c.NumFrames, tracks);
                }
            case animAnimationBufferSimd s:
                return (byte)s.NumExtraJoints == 0
                    ? new SyncClip(clip, duration, s.NumFrames, [])
                    : SimdSyncOf(anim, s, set);
            case null:
                return new SyncClip(clip, duration, 0, [], "it has no buffer");
            case var other:
                return new SyncClip(clip, duration, 0, [], $"its buffer is a {other.GetType().Name}");
        }
    }

    /// <summary>One joint's keys of one kind: the animated keys (quantized and raw), else the constant ones.</summary>
    private static SyncKeys KeysOf(animAnimationBufferCompressed c, int idx, Func<animKey, float[]?> value)
    {
        static SortedDictionary<float, float[]> Collect(IEnumerable<animKey?> keys, int idx, Func<animKey, float[]?> value)
        {
            var d = new SortedDictionary<float, float[]>();
            foreach (var k in keys)
            {
                if (k is not null && (ushort)k.Idx == idx && value(k) is { } v)
                {
                    d[(float)k.Time] = v;
                }
            }
            return d;
        }
        var animated = Collect(c.AnimKeys.Concat(c.AnimKeysRaw), idx, value);
        var keys = animated.Count > 0 ? animated : Collect(c.ConstAnimKeys, idx, value);
        return new SyncKeys(keys.Keys.ToList(), keys.Values.ToList());
    }

    // The game's Z-up left-handed axes to glTF's Y-up right-handed ones, as WolvenKit's export
    // converts the rig's joints (TRVectorYupRhs, RQuaternionYupRhs, normalized).
    private static float[] Position(WolvenKit.RED4.Types.Vector3 v) => [(float)v.X, (float)v.Z, -(float)v.Y];

    private static float[] Rotation(WolvenKit.RED4.Types.Quaternion q)
    {
        var x = (float)q.I;
        var y = (float)q.K;
        var z = -(float)q.J;
        var w = (float)q.R;
        var n = MathF.Sqrt(x * x + y * y + z * z + w * w);
        return n > 0 ? [x / n, y / n, z / n, w / n] : [0, 0, 0, 1];
    }

    /// <summary>
    /// An .anims export to glTF, as WolvenKit's ExportAnim does it, with spline root motion keyed in
    /// seconds (see <see cref="RootMotionToSeconds"/>). Answers the clips whose root keys it scaled.
    /// </summary>
    private (bool Ok, List<string> Scaled) ExportAnimSet(CR2WFile animsFile, FileInfo outfile, DirectoryInfo archive, AnimationExportArgs a)
    {
        if (animsFile.RootChunk is not animAnimSet set)
        {
            return (false, []);
        }
        var rigPath = PathText(set.Rig.DepotPath);
        if (rigPath is not { Length: > 0 })
        {
            throw new InvalidDataException("The animation set names no rig");
        }
        CR2WFile rig;
        using (var s = RigStream(rigPath, archive))
        {
            rig = _host.Parser.ReadRed4File(s) is { RootChunk: animRig } f ? f : throw new InvalidDataException($"{rigPath} is not a rig");
        }
        // SIMD clips whose extra joints WolvenKit's decoder would misread (#20).
        SimdForWolvenKit(set);
        var model = ModelRoot.CreateModel();
        if (!Modkit.GetAnimation(animsFile, rig, outfile.Name, ref model, true, a.AdditiveRelativeToLocalTransform, a.incRootMotion))
        {
            return (false, []);
        }
        var scaled = a.incRootMotion ? RootMotionToSeconds(model, set) : [];
        model.Save(GLTFHelper.PrepareFilePath(outfile.FullName, a.IsBinary), new WriteSettings(ValidationMode.TryFix));
        return (true, scaled);
    }

    /// <summary>
    /// WolvenKit 10.0.0-beta.1 keys spline-compressed root motion by the clip's fraction (0 to 1),
    /// not in seconds (WolvenKit issue 3304), so the motion plays in the clip's first second. This
    /// scales the Root's translation and rotation keys of those clips by the clip's duration. Keys
    /// past 1 are taken as seconds already (a WolvenKit with the fix) and left alone.
    /// </summary>
    private static List<string> RootMotionToSeconds(ModelRoot model, animAnimSet set)
    {
        var scaled = new List<string>();
        var root = model.LogicalSkins.FirstOrDefault(s => s.Name == "Armature")?.GetJoint(0).Joint;
        if (root is null)
        {
            return scaled;
        }
        foreach (var entry in set.Animations)
        {
            if (entry?.Chunk?.Animation?.Chunk is not { MotionExtraction.Chunk: animSplineCompressedMotionExtraction } anim)
            {
                continue;
            }
            var name = anim.Name.GetResolvedText() ?? "";
            var duration = (float)anim.Duration;
            var gltf = model.LogicalAnimations.FirstOrDefault(x => x.Name == name);
            if (gltf is null || duration <= 0 || MathF.Abs(duration - 1f) < 1e-4f)
            {
                continue;
            }
            if (ScaleRoot(gltf, root, duration, toSeconds: true))
            {
                scaled.Add(name);
            }
        }
        return scaled;
    }

    /// <summary>
    /// The Root's translation and rotation keys of one clip of <paramref name="length"/> seconds, from
    /// fractions to seconds or back. The keys are taken as seconds when the last one is nearer the
    /// clip's length than 1, else as fractions; nothing changes when they are already in the unit asked for.
    /// </summary>
    private static bool ScaleRoot(SharpGLTF.Schema2.Animation gltf, Node root, float length, bool toSeconds)
    {
        var t = gltf.FindTranslationChannel(root)?.GetTranslationSampler();
        var r = gltf.FindRotationChannel(root)?.GetRotationSampler();
        if (t?.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE || r?.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
        {
            return false;
        }
        var tKeys = t?.GetLinearKeys().ToList() ?? [];
        var rKeys = r?.GetLinearKeys().ToList() ?? [];
        if (tKeys.Count + rKeys.Count == 0)
        {
            return false;
        }
        var max = tKeys.Select(k => k.Key).Concat(rKeys.Select(k => k.Key)).Max();
        var inSeconds = MathF.Abs(max - length) < MathF.Abs(max - 1f);
        if (inSeconds == toSeconds)
        {
            return false;
        }
        var f = toSeconds ? length : 1f / length;
        if (tKeys.Count > 0)
        {
            gltf.CreateTranslationChannel(root, ScaledKeys(tKeys, f), t!.InterpolationMode != AnimationInterpolationMode.STEP);
        }
        if (rKeys.Count > 0)
        {
            gltf.CreateRotationChannel(root, ScaledKeys(rKeys, f), r!.InterpolationMode != AnimationInterpolationMode.STEP);
        }
        return true;
    }

    private static Dictionary<float, T> ScaledKeys<T>(List<(float Key, T Value)> keys, float f)
    {
        var d = new Dictionary<float, T>(keys.Count);
        foreach (var (k, v) in keys)
        {
            d[k * f] = v;
        }
        return d;
    }

    /// <summary>
    /// Before an .anims import. WolvenKit 10.0.0-beta.1 cannot import spline root motion: its encoder
    /// writes every joint's keys into the motion extraction, not only the Root's, so the clip cannot be
    /// read again ("Motion extraction in joint 1"), and it takes the Root's times as fractions. So for
    /// each clip whose extras name spline root motion, the Root's keys are put in seconds (a GLB from
    /// WolvenKit's own export has fractions) and the clip is marked for linear root motion, which
    /// WolvenKit encodes from the Root alone, in seconds, as the game's animLinearCompressedMotionExtraction.
    /// Answers a GLB to import (the source when nothing changed, else a copy in a temporary folder)
    /// and the clips changed.
    /// </summary>
    private static (FileInfo Glb, List<string> Changed) SplineRootMotionToLinear(FileInfo source)
    {
        var model = ModelRoot.Load(source.FullName, new ReadSettings(ValidationMode.TryFix));
        var root = model.LogicalSkins.FirstOrDefault(s => s.Name == "Armature")?.GetJoint(0).Joint;
        var changed = new List<string>();
        if (root is null)
        {
            return (source, changed);
        }
        foreach (var gltf in model.LogicalAnimations)
        {
            if (gltf.Extras is not JsonObject extras || RootMotionKey(extras) is not { } key)
            {
                continue;
            }
            // The clip's length from its other joints, which are keyed in seconds.
            var length = gltf.Channels.Where(c => c.TargetNode != root).Select(ChannelEnd).DefaultIfEmpty(0f).Max();
            if (length > 0 && MathF.Abs(length - 1f) >= 1e-4f)
            {
                ScaleRoot(gltf, root, length, toSeconds: true);
            }
            extras[key] = "TransformLinearSparseFrames";
            gltf.Extras = extras;
            changed.Add(gltf.Name ?? "");
        }
        if (changed.Count == 0)
        {
            return (source, changed);
        }
        var dir = Directory.CreateTempSubdirectory("redbridge-anims-");
        var copy = new FileInfo(Path.Combine(dir.FullName, source.Name));
        model.Save(copy.FullName, new WriteSettings(ValidationMode.TryFix));
        return (copy, changed);
    }

    private static float ChannelEnd(AnimationChannel c) => c.TargetNodePath switch
    {
        PropertyPath.translation => c.GetTranslationSampler().GetLinearKeys().Select(k => k.Key).DefaultIfEmpty(0f).Max(),
        PropertyPath.rotation => c.GetRotationSampler().GetLinearKeys().Select(k => k.Key).DefaultIfEmpty(0f).Max(),
        PropertyPath.scale => c.GetScaleSampler().GetLinearKeys().Select(k => k.Key).DefaultIfEmpty(0f).Max(),
        _ => 0f,
    };

    /// <summary>The extras' root motion type key when it names spline root motion, else null.</summary>
    private static string? RootMotionKey(JsonObject extras) =>
        extras.FirstOrDefault(p => p.Key.Equals("rootMotionType", StringComparison.OrdinalIgnoreCase)) is { Value: JsonValue v } p
        && v.TryGetValue<string>(out var s) && s == "TransformSplineSparseFrames"
            ? p.Key
            : null;
}
