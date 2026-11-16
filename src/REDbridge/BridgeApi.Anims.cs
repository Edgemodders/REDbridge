using StreamJsonRpc;
using WolvenKit.Common;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// Animation sets (.anims), read without decoding any clip (bridge 0.14.0, #14 and #17): what an
/// index of the game's animations needs, the clip names, lengths and frame counts, type, root motion
/// and rig, per file and for many files in one call.
/// </summary>
public sealed partial class BridgeApi
{
    /// <summary>One clip of an animation set. Fps is (frames - 1) / duration, null for a one-frame clip.</summary>
    public sealed record AnimClipInfo(string Name, double Duration, uint Frames, double? Fps, string Type, string? RootMotion, string Buffer, int Joints);

    /// <summary>One animation set: its game path, rig and clips, or why it could not be read.</summary>
    public sealed record AnimsInfo(string Path, string? Rig, IReadOnlyList<AnimClipInfo> Clips, string? Error = null);

    /// <summary>Total is the number of sets the call covers (every .anims in the game when no files are given).</summary>
    public sealed record AnimsInfoResult(long Total, int Offset, IReadOnlyList<AnimsInfo> Items);

    /// <summary>
    /// The clips of animation sets without decoding them. With <paramref name="files"/> (game paths
    /// or hashes) it answers for those; without, for every .anims file in the game, a page at a time,
    /// in path order. A file that cannot be read answers its error and the rest still answer.
    /// </summary>
    [JsonRpcMethod("anims.info")]
    public AnimsInfoResult AnimsInfoOf(IReadOnlyList<string>? files = null, int offset = 0, int limit = 100)
    {
        RequireGame();
        limit = Math.Clamp(limit, 1, 1000);
        IReadOnlyList<string> paths;
        long total;
        if (files is { Count: > 0 })
        {
            paths = files;
            total = files.Count;
        }
        else
        {
            var all = AllWithExtension(".anims");
            total = all.Count;
            paths = all.Skip(Math.Max(0, offset)).Take(limit).Select(x => x.file.Name).ToList();
        }
        var items = new List<AnimsInfo>(paths.Count);
        foreach (var p in paths)
        {
            try
            {
                var file = Find(p);
                var cr2w = ReadCr2w(file);
                if (cr2w.RootChunk is not animAnimSet set)
                {
                    items.Add(new AnimsInfo(file.Name, null, [], $"{file.Name} is not an animation set"));
                    continue;
                }
                items.Add(new AnimsInfo(file.Name, PathText(set.Rig.DepotPath) is { Length: > 0 } r ? r : null, ClipsOf(set)));
            }
            catch (Exception e)
            {
                items.Add(new AnimsInfo(p, null, [], e.Message));
            }
        }
        return new AnimsInfoResult(total, files is { Count: > 0 } ? 0 : Math.Max(0, offset), items);
    }

    private static List<AnimClipInfo> ClipsOf(animAnimSet set)
    {
        var clips = new List<AnimClipInfo>(set.Animations.Count);
        foreach (var entry in set.Animations)
        {
            if (entry?.Chunk?.Animation?.Chunk is not { } anim)
            {
                continue;
            }
            var (frames, joints, buffer) = anim.AnimBuffer?.Chunk switch
            {
                animAnimationBufferSimd s => ((uint)s.NumFrames, (int)(ushort)s.NumJoints, "simd"),
                animAnimationBufferCompressed c => ((uint)c.NumFrames, (int)(ushort)c.NumJoints, "compressed"),
                null => (0u, 0, "none"),
                var other => (0u, 0, other.GetType().Name),
            };
            var duration = (double)(float)anim.Duration;
            var motion = anim.MotionExtraction?.Chunk is { } m ? m.GetType().Name : null;
            clips.Add(new AnimClipInfo(
                anim.Name.GetResolvedText() ?? "",
                Math.Round(duration, 4),
                frames,
                frames > 1 && duration > 0 ? Math.Round((frames - 1) / duration, 3) : null,
                ((Enums.animAnimationType)anim.AnimationType).ToString(),
                motion,
                buffer,
                joints));
        }
        return clips;
    }

    /// <summary>Every game file whose name ends with <paramref name="extension"/>, any case, in path order.</summary>
    private List<(IGameFile file, string archive)> AllWithExtension(string extension)
    {
        var dot = extension.StartsWith('.') ? extension : "." + extension;
        return _host.Archives.GetGameArchives()
            .SelectMany(a => a.Files.Values.Where(f => f.Name.EndsWith(dot, StringComparison.OrdinalIgnoreCase)).Select(f => (file: f, archive: a.Name)))
            .OrderBy(x => x.file.Name, StringComparer.Ordinal)
            .ToList();
    }
}
