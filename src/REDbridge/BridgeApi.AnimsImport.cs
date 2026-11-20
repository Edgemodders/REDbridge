using SharpGLTF.Schema2;
using SharpGLTF.Validation;
using WolvenKit.Common.Model.Arguments;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// project.import of an animation set from glTF (bridge 0.15.0, #14 for EDGEfab's F7b-1): a
/// <c>&lt;name&gt;.anims.glb</c> under rawDir becomes <c>&lt;name&gt;.anims</c> under archiveDir, through
/// WolvenKit's ImportAnims. The answer says which clips were updated, added and kept, which were
/// skipped and why, and what WolvenKit noted (root motion kept or dropped, additive clips).
/// </summary>
public sealed partial class BridgeApi
{
    /// <summary>A clip of the GLB that did not go into the set, and why.</summary>
    public sealed record AnimSkipped(string Name, string Why);

    /// <summary>
    /// What an .anims import wrote. From is where the set came from: "existing" (imported over the file at
    /// the target), "template" (a copy of an .anims given) or "rig" (a new, empty set on the rig given).
    /// Clips is the whole set as written, read back from the file.
    /// </summary>
    public sealed record AnimsImport(
        string File,
        string? Rig,
        string From,
        IReadOnlyList<AnimClipInfo> Clips,
        IReadOnlyList<string> Updated,
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Kept,
        IReadOnlyList<AnimSkipped> Skipped,
        IReadOnlyList<string> Notes);

    private static bool IsAnimsGlb(string name) =>
        name.EndsWith(".anims.glb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".anims.gltf", StringComparison.OrdinalIgnoreCase);

    private ModkitResult ImportAnimsGlb(FileInfo source, string relative, string archiveDir, bool keep, string? template, string? rig)
    {
        if (template is not null && rig is not null)
        {
            throw new ArgumentException("An .anims import takes template or rig, not both");
        }
        var targetRel = relative[..^Path.GetExtension(relative).Length];
        var target = Path.Combine(Path.GetFullPath(archiveDir), targetRel);
        var exists = File.Exists(target);

        // Where the set comes from. With keep (the default) a set already at the target is imported
        // over, so clips the GLB lacks stay; keep: false starts again from the template or rig.
        string from;
        byte[] start;
        if (exists && (keep || (template is null && rig is null)))
        {
            from = "existing";
            start = File.ReadAllBytes(target);
        }
        else if (template is not null)
        {
            from = "template";
            start = File.Exists(template) ? File.ReadAllBytes(template) : ExtractBytes(template);
        }
        else if (rig is not null)
        {
            from = "rig";
            start = NewAnimSet(rig);
        }
        else
        {
            throw new InvalidOperationException(
                $"There is no animation set at {targetRel} to import {source.Name} over. For a new set, pass rig (the game rig its clips are made for) or template (an .anims to start from).");
        }

        var before = ReadAnimSet(start, from == "existing" ? targetRel : template ?? "the new set");
        var originals = ClipsOf(before).Select(c => c.Name).ToList();
        var incoming = GlbAnimationNames(source);
        if (incoming.Count == 0)
        {
            throw new InvalidOperationException($"{source.Name} has no animations");
        }

        // WolvenKit cannot encode spline root motion; such clips come in as linear, in seconds (#19).
        var (glb, toLinear) = SplineRootMotionToLinear(source);
        var lines = new List<(string Level, string Text)>();
        var ms = new MemoryStream();
        ms.Write(start);
        ms.Position = 0;
        bool ok;
        using (StderrLoggerService.Capture(lines))
        {
            try
            {
                ok = Modkit.ImportAnims(glb, ms, new GltfImportArgs { Keep = true });
            }
            catch (Exception e)
            {
                // WolvenKit throws for a joint the rig lacks, a morph (weights) channel, two clips of one
                // name, root motion it cannot encode, or an original clip it cannot copy. Nothing is written.
                throw new InvalidOperationException($"{source.Name} was not imported: {e.Message}");
            }
            finally
            {
                if (glb != source)
                {
                    try { glb.Directory?.Delete(true); } catch (IOException) { }
                }
            }
        }
        if (!ok)
        {
            var why = lines.Where(l => l.Level == "error").Select(l => l.Text).ToList();
            throw new InvalidOperationException($"{source.Name} was not imported: {(why.Count > 0 ? string.Join("; ", why) : "WolvenKit could not read the set or its rig")}");
        }

        var after = ReadAnimSet(ms.ToArray(), targetRel);
        var clips = ClipsOf(after);
        var written = clips.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var skipped = incoming
            .Where(n => !written.Contains(n))
            .Select(n => new AnimSkipped(n, lines.FirstOrDefault(l => l.Level == "error" && l.Text.Contains($"`{n}`")).Text is { } t
                ? t[(t.IndexOf(": ", t.IndexOf($"`{n}`", StringComparison.Ordinal), StringComparison.Ordinal) is var i and >= 0 ? i + 2 : 0)..].TrimEnd('!')
                : "WolvenKit did not import it"))
            .ToList();
        var imported = incoming.Where(written.Contains).ToList();
        if (imported.Count == 0)
        {
            throw new InvalidOperationException($"None of the {incoming.Count} clips of {source.Name} could be imported: {string.Join("; ", skipped.Select(s => $"{s.Name}: {s.Why}"))}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, ms.ToArray());

        var notes = lines
            .Where(l => l.Level is "warn" or "error" || l.Text.Contains("Root Motion", StringComparison.Ordinal))
            .Where(l => !skipped.Any(s => l.Text.Contains($"`{s.Name}`", StringComparison.Ordinal)))
            .Select(l => l.Text)
            .Distinct()
            .ToList();
        if (toLinear.Count > 0)
        {
            notes.Add($"Root motion of {string.Join(", ", toLinear)} stored as linear motion extraction (animLinearCompressedMotionExtraction), keyed in seconds: WolvenKit cannot write the spline kind");
        }
        var anims = new AnimsImport(
            targetRel,
            PathText(after.Rig.DepotPath) is { Length: > 0 } r ? r : null,
            from,
            clips,
            imported.Where(originals.Contains).ToList(),
            imported.Where(n => !originals.Contains(n)).ToList(),
            originals.Where(n => !incoming.Contains(n)).ToList(),
            skipped,
            notes);
        return new ModkitResult(true, [targetRel], Anims: anims);
    }

    private byte[] ExtractBytes(string gamePath)
    {
        using var ms = new MemoryStream();
        Find(gamePath).Extract(ms);
        return ms.ToArray();
    }

    /// <summary>A new, empty animation set on a game rig. The rig must be a game file: WolvenKit's import reads it from the archives.</summary>
    private byte[] NewAnimSet(string rig)
    {
        var file = Find(rig);
        if (ReadCr2w(file).RootChunk is not animRig)
        {
            throw new ArgumentException($"{rig} is not a rig");
        }
        var set = new animAnimSet { Rig = new CResourceReference<animRig>(file.Name) };
        using var ms = new MemoryStream();
        using (var writer = new CR2WWriter(ms, System.Text.Encoding.UTF8, true))
        {
            writer.WriteFile(new CR2WFile { RootChunk = set });
        }
        return ms.ToArray();
    }

    private animAnimSet ReadAnimSet(byte[] bytes, string name)
    {
        using var ms = new MemoryStream(bytes);
        return _host.Parser.ReadRed4File(ms)?.RootChunk as animAnimSet
            ?? throw new InvalidDataException($"{name} is not an animation set");
    }

    private static List<string> GlbAnimationNames(FileInfo glb) =>
        ModelRoot.Load(glb.FullName, new ReadSettings(ValidationMode.TryFix)).LogicalAnimations.Select(a => a.Name ?? "").ToList();
}
