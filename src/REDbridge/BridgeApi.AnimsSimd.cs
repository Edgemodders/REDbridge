using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// SIMD animation buffers with extra joints (bridge 0.17.0, #20). The game stores a SIMD clip's
/// rotations (and per-frame scales) for every joint, the rig's own and the extra ones (a synced
/// clip's <c>syncTrajectory</c> joints), padded to a multiple of 4. WolvenKit 10.0.0-beta.1's decoder
/// pads <c>NumJoints - NumExtraJoints</c> instead, so when the two round differently (152 + 2 joints:
/// 156 stored, 152 read) every frame after the first is read from the wrong place: translations
/// zero, a scale of about 0.19 on every bone, rotations flipping to identity. Before an export,
/// such a clip's buffer is rewritten in memory to the layout WolvenKit reads (the extra joints'
/// columns dropped), and <c>anims.sync</c> reads the extra joints from the buffer as the game stores it.
/// </summary>
public sealed partial class BridgeApi
{
    /// <summary>One SIMD buffer's layout, as the game writes it (with every joint) or as WolvenKit reads it.</summary>
    private sealed class SimdBuffer
    {
        public required byte[] Bytes;
        public int Frames, Joints, Extra, Bits, Eval, Copy, Tracks;
        public bool ScaleConstant, TrackConstant;
        public float Duration;

        public int Base => Joints - Extra;
        public static int A4(int n) => (n + 3) & ~3;
        public int AllAligned => A4(Joints);
        public int BaseAligned => A4(Base);

        public int RotationBytes(int aligned)
        {
            if (Bits == 0)
            {
                return Frames * aligned * 16;
            }
            var floats = (Frames * aligned * 3 + 3) & ~3;
            var bytes = (int)((long)floats * Bits / 8);
            return (bytes + 15) & ~15;
        }

        public int PositionBytes => Frames * Eval * 3 * 4;
        public int ScaleBytes(int aligned) => ScaleConstant ? 16 : Frames * aligned * 3 * 4;
        public int TrackBytes => Tracks == 0 ? 0 : TrackConstant ? 4 : Frames * A4(Tracks) * 4;
        public int TailBytes => Copy * 12 + Copy * 2 + Eval * 2;
        public int Expected(int aligned) => RotationBytes(aligned) + PositionBytes + ScaleBytes(aligned) + TrackBytes + TailBytes;

        /// <summary>Whether the bytes hold the given layout (the end may carry up to 15 bytes of padding).</summary>
        public bool Fits(int aligned) => Bytes.Length >= Expected(aligned) && Bytes.Length < Expected(aligned) + 16;

        public uint Packed(int index)
        {
            var bit = (long)index * Bits;
            var at = (int)(bit / 8);
            uint v = 0;
            for (var k = 0; k < 4 && at + k < Bytes.Length; k++)
            {
                v |= (uint)Bytes[at + k] << (8 * k);
            }
            return (v >> (int)(bit % 8)) & ((1u << Bits) - 1u);
        }

        public float Float(int offset) => BitConverter.ToSingle(Bytes, offset);
        public short Short(int offset) => BitConverter.ToInt16(Bytes, offset);
    }

    private static SimdBuffer? SimdOf(animAnimationBufferSimd s, animAnimSet set)
    {
        byte[]? bytes = null;
        if (s.InplaceCompressedBuffer is { } inplace)
        {
            bytes = inplace.Buffer.GetBytes();
        }
        else if (s.DataAddress is { } addr && (uint)addr.UnkIndex != uint.MaxValue)
        {
            var i = (int)(uint)addr.UnkIndex;
            if (i < set.AnimationDataChunks.Count && set.AnimationDataChunks[i]?.Buffer?.Buffer is { } chunk)
            {
                var all = chunk.GetBytes();
                var off = (int)(uint)addr.FsetInBytes;
                var len = Math.Min((int)(uint)addr.ZeInBytes, Math.Max(0, all.Length - off));
                bytes = all.AsSpan(off, len).ToArray();
            }
        }
        else if (s.DefferedBuffer?.Buffer is { } deferred)
        {
            bytes = deferred.GetBytes();
        }
        if (bytes is null)
        {
            return null;
        }
        return new SimdBuffer
        {
            Bytes = bytes,
            Frames = (int)(uint)s.NumFrames,
            Joints = (ushort)s.NumJoints,
            Extra = (byte)s.NumExtraJoints,
            Bits = (int)(uint)s.QuantizationBits,
            Eval = (ushort)s.NumTranslationsToEvalAlignedToSimd,
            Copy = (ushort)s.NumTranslationsToCopy,
            Tracks = (ushort)s.NumTracks,
            ScaleConstant = s.IsScaleConstant,
            TrackConstant = s.IsTrackConstant,
            Duration = s.Duration,
        };
    }

    /// <summary>
    /// Rewrites, in memory, every SIMD clip of the set whose buffer WolvenKit would misread (see the
    /// class summary) into the layout it reads, as an inplace buffer. Answers the clips rewritten.
    /// The file on disk is not touched.
    /// </summary>
    private static List<string> SimdForWolvenKit(animAnimSet set)
    {
        var fixedClips = new List<string>();
        foreach (var entry in set.Animations)
        {
            if (entry?.Chunk?.Animation?.Chunk is not { AnimBuffer.Chunk: animAnimationBufferSimd s } anim || (byte)s.NumExtraJoints == 0)
            {
                continue;
            }
            var b = SimdOf(s, set);
            if (b is null || b.AllAligned == b.BaseAligned || !b.Fits(b.AllAligned) || b.Fits(b.BaseAligned))
            {
                continue;
            }
            s.InplaceCompressedBuffer = new DataBuffer(Repacked(b));
            fixedClips.Add(anim.Name.GetResolvedText() ?? "");
        }
        return fixedClips;
    }

    /// <summary>The buffer with the extra joints' rotation and scale columns dropped: the base joints padded to 4.</summary>
    private static byte[] Repacked(SimdBuffer b)
    {
        int all = b.AllAligned, keep = b.BaseAligned;
        var output = new List<byte>(b.Bytes.Length);
        // Rotations: per frame, blocks of 4 joints, each block x, y, z (and w unquantized) lanes of 4.
        if (b.Bits == 0)
        {
            for (var f = 0; f < b.Frames; f++)
            {
                output.AddRange(b.Bytes.AsSpan(f * all * 16, keep * 16).ToArray());
            }
        }
        else
        {
            var rot = new byte[b.RotationBytes(keep)];
            for (var f = 0; f < b.Frames; f++)
            {
                for (var i = 0; i < keep * 3; i++)
                {
                    var v = b.Packed(f * all * 3 + i);
                    var bit = (long)(f * keep * 3 + i) * b.Bits;
                    var at = (int)(bit / 8);
                    var shifted = (ulong)v << (int)(bit % 8);
                    for (var k = 0; k < 4 && at + k < rot.Length; k++)
                    {
                        rot[at + k] |= (byte)(shifted >> (8 * k));
                    }
                }
            }
            output.AddRange(rot);
        }
        var at0 = b.RotationBytes(all);
        output.AddRange(b.Bytes.AsSpan(at0, b.PositionBytes).ToArray());
        at0 += b.PositionBytes;
        if (b.ScaleConstant)
        {
            output.AddRange(b.Bytes.AsSpan(at0, 16).ToArray());
            at0 += 16;
        }
        else
        {
            for (var f = 0; f < b.Frames; f++)
            {
                output.AddRange(b.Bytes.AsSpan(at0 + f * all * 12, keep * 12).ToArray());
            }
            at0 += b.ScaleBytes(all);
        }
        output.AddRange(b.Bytes.AsSpan(at0).ToArray());
        return [.. output];
    }

    /// <summary>The extra joints of a SIMD clip, read from its buffer as the game stores it.</summary>
    private static SyncClip SimdSyncOf(animAnimation anim, animAnimationBufferSimd s, animAnimSet set)
    {
        var clip = anim.Name.GetResolvedText() ?? "";
        var duration = Math.Round((double)(float)anim.Duration, 4);
        var b = SimdOf(s, set);
        if (b is null)
        {
            return new SyncClip(clip, duration, s.NumFrames, [], "its buffer could not be read");
        }
        if (!b.Fits(b.AllAligned))
        {
            return new SyncClip(clip, duration, s.NumFrames, [], $"its SIMD buffer is {b.Bytes.Length} bytes, not the {b.Expected(b.AllAligned)} its layout gives, so its sync tracks are not read");
        }
        int all = b.AllAligned;
        var times = Enumerable.Range(0, b.Frames).Select(f => b.Frames > 1 ? (float)(f * b.Duration / (b.Frames - 1)) : 0f).ToList();
        var posAt = b.RotationBytes(all);
        var tailAt = posAt + b.PositionBytes + b.ScaleBytes(all) + b.TrackBytes;
        var copyAt = tailAt;
        var copyIdxAt = copyAt + b.Copy * 12;
        var evalIdxAt = copyIdxAt + b.Copy * 2;
        var tracks = new List<SyncTrack>(b.Extra);
        for (var k = 0; k < b.Extra; k++)
        {
            var joint = b.Base + k;
            var name = k < s.ExtraDataNames.Count ? s.ExtraDataNames[k].GetResolvedText() ?? $"extra{k}" : $"extra{k}";
            var rotations = new List<float[]>(b.Frames);
            for (var f = 0; f < b.Frames; f++)
            {
                rotations.Add(SimdRotation(b, f, joint));
            }
            SyncKeys translation = new([], []);
            for (var e = 0; e < b.Eval; e++)
            {
                if (b.Short(evalIdxAt + e * 2) != joint)
                {
                    continue;
                }
                var values = new List<float[]>(b.Frames);
                for (var f = 0; f < b.Frames; f++)
                {
                    var i = f * b.Eval * 3 + (e & ~3) * 3 + (e & 3);
                    values.Add(GltfPosition(b.Float(posAt + i * 4), b.Float(posAt + (i + 4) * 4), b.Float(posAt + (i + 8) * 4)));
                }
                translation = new SyncKeys(times, values);
                break;
            }
            if (translation.Times.Count == 0)
            {
                for (var e = 0; e < b.Copy; e++)
                {
                    if (b.Short(copyIdxAt + e * 2) == joint)
                    {
                        var o = copyAt + e * 12;
                        translation = new SyncKeys([0f], [GltfPosition(b.Float(o), b.Float(o + 4), b.Float(o + 8))]);
                        break;
                    }
                }
            }
            tracks.Add(new SyncTrack(name, translation, new SyncKeys(times, rotations)));
        }
        var noMove = tracks.Where(t => t.Translation.Times.Count == 0).Select(t => t.Name).ToList();
        return new SyncClip(clip, duration, (uint)b.Frames, tracks, Note: noMove.Count == 0 ? null
            : $"its SIMD buffer holds no translation for {string.Join(", ", noMove)} (only rotations): place this member from its partner's track for it instead");
    }

    private static float[] SimdRotation(SimdBuffer b, int frame, int joint)
    {
        int all = b.AllAligned, block = joint & ~3, lane = joint & 3;
        float x, y, z, w;
        if (b.Bits == 0)
        {
            // Per frame, per block of 4 joints: 4 x, 4 y, 4 z, 4 w floats.
            var o = (frame * all * 4 + block * 4 + lane) * 4;
            x = b.Float(o);
            y = b.Float(o + 16);
            z = b.Float(o + 32);
            w = b.Float(o + 48);
        }
        else
        {
            var mask = (1u << b.Bits) - 1u;
            var i = frame * all * 3 + block * 3 + lane;
            x = 2f * b.Packed(i) / mask - 1f;
            y = 2f * b.Packed(i + 4) / mask - 1f;
            z = 2f * b.Packed(i + 8) / mask - 1f;
            var dot = x * x + y * y + z * z;
            var m = MathF.Sqrt(MathF.Max(0f, 2f - dot));
            x *= m;
            y *= m;
            z *= m;
            w = 1f - dot;
        }
        // The game's Z-up left-handed axes to glTF's, as WolvenKit's RQuaternionGltf, normalized.
        var (gx, gy, gz, gw) = (x, z, -y, w);
        var n = MathF.Sqrt(gx * gx + gy * gy + gz * gz + gw * gw);
        return n > 0 ? [gx / n, gy / n, gz / n, gw / n] : [0, 0, 0, 1];
    }

    private static float[] GltfPosition(float x, float y, float z) => [x, z, -y];
}
