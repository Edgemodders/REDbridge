using System.Globalization;
using StreamJsonRpc;
using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// Spoken lines (issue #9, for EDGEquest): the subtitles of each language, read offline from the
/// base game's and Phantom Liberty's <c>localization\&lt;language&gt;\subtitles\**\*.json</c>, with
/// the voice-over files from the voice-over maps and the line lengths from
/// <c>stringidvariantlengthsreport.json</c>. A scene line's <c>locstringId.ruid</c> is the
/// <c>stringId</c> in all three. A project's own subtitle files win over the game's, by the same
/// rule <c>loc.search</c> uses for onscreens: the files its <c>.xl</c> files name under
/// <c>localization: subtitles:</c>, and any under source/archive in a <c>&lt;language&gt;\subtitles</c> folder.
/// </summary>
public sealed partial class BridgeApi
{
    /// <summary>One subtitle entry: the stringId (as text, since it can pass 2^53), both texts, where it comes from, its file and its row in that file.</summary>
    public sealed record LineEntry(string StringId, string Female, string Male, string Source, string File, int Index);

    /// <summary>The voice-over files of a line (game paths of .wem files, or a decimal hash when the path is not known). Null for a side with none.</summary>
    public sealed record LineVoice(string? Female, string? Male);

    /// <summary>The length of a line's recording in seconds, per side.</summary>
    public sealed record LineLength(float Female, float Male);

    /// <summary>
    /// A line looked up by stringId. The texts, source, file and index are null when no subtitle file
    /// has the id. Voice is from the game's main voice-over maps; OtherVoices names the helmet, holocall
    /// and rewinded maps that also have the id.
    /// </summary>
    public sealed record LineDetail(
        string StringId,
        string? Female,
        string? Male,
        string? Source,
        string? File,
        int? Index,
        LineVoice? Voice,
        LineLength? Length,
        IReadOnlyDictionary<string, LineVoice>? OtherVoices);

    public sealed record LineSearch(int Total, IReadOnlyList<LineEntry> Items);

    private sealed class LinesIndex
    {
        public required List<LineEntry> Entries { get; init; }
        public required Dictionary<ulong, LineEntry> ById { get; init; }
    }

    private sealed class VoiceIndex
    {
        public required Dictionary<ulong, LineVoice> Main { get; init; }
        public required Dictionary<string, Dictionary<ulong, LineVoice>> Others { get; init; }
        public required Dictionary<ulong, LineLength> Lengths { get; init; }
    }

    private static readonly Dictionary<string, LinesIndex> GameLines = new();
    private static readonly Dictionary<string, VoiceIndex> GameVoices = new();
    private static readonly Dictionary<string, (DateTime Written, List<LineEntry> Entries)> ProjectLineFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The voice-over maps besides the main ones (voiceovermap.json, voiceovermap_1.json), by the name OtherVoices gives them.</summary>
    private static readonly string[] OtherVoiceMaps = ["helmet", "holocall", "rewinded"];

    // ---- methods --------------------------------------------------------------------------

    /// <summary>
    /// Lines by stringId (decimal, as WolvenKit's JSON has a <c>locstringId.ruid</c>), one result per id
    /// in the order asked, with the subtitle texts, the voice-over files and the lengths. With
    /// <paramref name="projectDir"/>, the project's subtitle entries replace the game's ones with the same id.
    /// </summary>
    [JsonRpcMethod("loc.lines")]
    public IReadOnlyList<LineDetail> LocLines(IReadOnlyList<string> stringIds, string language = "en-us", string? projectDir = null)
    {
        var game = GameLinesIndex(language);
        var voices = GameVoiceIndex(language);
        var project = projectDir is null ? [] : ProjectLineEntries(projectDir, language);
        var projectById = new Dictionary<ulong, LineEntry>();
        foreach (var e in project)
        {
            projectById[ulong.Parse(e.StringId, CultureInfo.InvariantCulture)] = e;
        }
        var result = new List<LineDetail>();
        foreach (var raw in stringIds)
        {
            var text = raw.Trim();
            if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                throw new ArgumentException($"'{raw}' is not a stringId: give it as a decimal number");
            }
            var entry = projectById.GetValueOrDefault(id) ?? game.ById.GetValueOrDefault(id);
            var others = OtherVoiceMaps
                .Where(m => voices.Others.TryGetValue(m, out var map) && map.ContainsKey(id))
                .ToDictionary(m => m, m => voices.Others[m][id]);
            result.Add(new LineDetail(
                id.ToString(CultureInfo.InvariantCulture),
                entry?.Female,
                entry?.Male,
                entry?.Source,
                entry?.File,
                entry?.Index,
                voices.Main.GetValueOrDefault(id),
                voices.Lengths.GetValueOrDefault(id),
                others.Count == 0 ? null : others));
        }
        return result;
    }

    /// <summary>
    /// Lines whose text has every word of the query, any case, in either variant; a number finds that
    /// stringId. With <paramref name="projectDir"/>, the project's entries replace the game's ones with
    /// the same id and are searched too. Project entries come first.
    /// </summary>
    [JsonRpcMethod("loc.searchLines")]
    public LineSearch LocSearchLines(string query, string language = "en-us", int limit = 200, string? projectDir = null)
    {
        var all = MergedLines(language, projectDir);
        var q = query.Trim();
        List<LineEntry> hits;
        if (ulong.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            var text = id.ToString(CultureInfo.InvariantCulture);
            hits = all.Where(e => e.StringId == text).ToList();
        }
        else
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            hits = words.Length == 0 ? [] : all
                .Where(e => words.All(w =>
                    e.Female.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                    e.Male.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        return new LineSearch(hits.Count, hits.Take(Math.Clamp(limit, 1, 20000)).ToList());
    }

    /// <summary>The project's subtitle files for a language: those its .xl files name under <c>localization: subtitles:</c> (existing or not), then any .json under source/archive in a <c>&lt;language&gt;\subtitles</c> folder.</summary>
    [JsonRpcMethod("loc.projectLinesFiles")]
    public IReadOnlyList<LocProjectFile> LocProjectLinesFiles(string projectDir, string language = "en-us") => ProjectLocFilesOf(projectDir, language, "subtitles");

    /// <summary>
    /// Makes sure the project has a subtitle file for the language and returns it: the first existing
    /// one; else a file an .xl already names, written empty; else
    /// <c>source/archive/&lt;mod&gt;/localization/&lt;language&gt;/subtitles/&lt;mod&gt;.json</c>, written
    /// as an empty <c>localizationPersistenceSubtitleEntries</c> and added to an .xl under
    /// <c>localization: subtitles:</c> (a new <c>&lt;mod&gt;.archive.xl</c> in source/resources when the
    /// project has none), without rewriting the rest of the .xl.
    /// </summary>
    [JsonRpcMethod("loc.newLinesFile")]
    public LocNewFile LocNewLinesFile(string projectDir, string modName, string language = "en-us")
    {
        var known = ProjectLocFilesOf(projectDir, language, "subtitles");
        if (known.FirstOrDefault(f => f.Exists) is { } existing)
        {
            return new LocNewFile(existing.File, false, existing.Xl, false);
        }
        if (known.FirstOrDefault(f => f.Xl is not null) is { } named)
        {
            WriteCr2w(Path.Combine(projectDir, named.File), EmptySubtitles());
            return new LocNewFile(named.File, true, named.Xl, false);
        }
        var safe = SafeName(modName);
        if (safe.Length == 0)
        {
            safe = "mod";
        }
        var archivePath = $"{safe}\\localization\\{language}\\subtitles\\{safe}.json";
        var file = Path.Combine("source", "archive", safe, "localization", language, "subtitles", $"{safe}.json");
        WriteCr2w(Path.Combine(projectDir, file), EmptySubtitles());
        var resources = Path.Combine(projectDir, "source", "resources");
        var xl = Directory.Exists(resources) ? Directory.EnumerateFiles(resources, "*.xl", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
        xl ??= Path.Combine(resources, $"{safe}.archive.xl");
        var text = File.Exists(xl) ? File.ReadAllText(xl) : "";
        Directory.CreateDirectory(Path.GetDirectoryName(xl)!);
        File.WriteAllText(xl, XlWithLoc(text, "subtitles", language, archivePath));
        return new LocNewFile(file, true, Path.GetRelativePath(projectDir, xl), true);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static RedBaseClass EmptySubtitles() =>
        new JsonResource { Root = new CHandle<ISerializable>(new localizationPersistenceSubtitleEntries()) };

    /// <summary>The game's subtitle entries in a language, base game first, then Phantom Liberty, each in path order; read once.</summary>
    private LinesIndex GameLinesIndex(string language)
    {
        lock (GameLines)
        {
            if (GameLines.TryGetValue(language, out var known))
            {
                return known;
            }
            RequireGame();
            var started = DateTime.Now;
            var entries = new List<LineEntry>();
            var files = 0;
            foreach (var root in new[] { "base", "ep1" })
            {
                var prefix = $"{root}\\localization\\{language}\\subtitles\\";
                var names = _host.Archives.GetGameArchives()
                    .SelectMany(a => a.Files.Values)
                    .Select(f => f.Name)
                    .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.Ordinal)
                    .ToList();
                foreach (var name in names)
                {
                    var file = _host.Archives.Lookup(name);
                    if (!file.HasValue)
                    {
                        continue;
                    }
                    try
                    {
                        entries.AddRange(LineEntriesOf(ReadCr2w(file.Value).RootChunk, root, name));
                        files++;
                    }
                    catch (Exception e)
                    {
                        _host.Log.Warning($"Subtitles {name}: {e.Message}");
                    }
                }
            }
            var byId = new Dictionary<ulong, LineEntry>();
            foreach (var e in entries)
            {
                byId[ulong.Parse(e.StringId, CultureInfo.InvariantCulture)] = e;
            }
            _host.Log.Info($"Subtitles {language}: {entries.Count:N0} lines from {files:N0} files in {(DateTime.Now - started).TotalSeconds:F1} s");
            return GameLines[language] = new LinesIndex { Entries = entries, ById = byId };
        }
    }

    /// <summary>The game's voice-over maps and line lengths in a language, base game then Phantom Liberty; read once.</summary>
    private VoiceIndex GameVoiceIndex(string language)
    {
        lock (GameVoices)
        {
            if (GameVoices.TryGetValue(language, out var known))
            {
                return known;
            }
            RequireGame();
            var started = DateTime.Now;
            var main = new Dictionary<ulong, LineVoice>();
            var others = OtherVoiceMaps.ToDictionary(m => m, _ => new Dictionary<ulong, LineVoice>());
            var lengths = new Dictionary<ulong, LineLength>();
            foreach (var root in new[] { "base", "ep1" })
            {
                var dir = $"{root}\\localization\\{language}\\";
                foreach (var name in new[] { "voiceovermap.json", "voiceovermap_1.json" })
                {
                    AddVoices(dir + name, main);
                }
                foreach (var m in OtherVoiceMaps)
                {
                    AddVoices($"{dir}voiceovermap_{m}.json", others[m]);
                }
                if (ReadGameJson(dir + "stringidvariantlengthsreport.json") is JsonResource { Root.Chunk: locVoiceoverLengthMap lengthMap })
                {
                    foreach (var e in lengthMap.Entries)
                    {
                        lengths[e.StringId] = new LineLength(e.FemaleLength, e.MaleLength);
                    }
                }
            }
            _host.Log.Info($"Voice-over {language}: {main.Count:N0} lines, {lengths.Count:N0} lengths, in {(DateTime.Now - started).TotalSeconds:F1} s");
            return GameVoices[language] = new VoiceIndex { Main = main, Others = others, Lengths = lengths };
        }
    }

    private void AddVoices(string path, Dictionary<ulong, LineVoice> into)
    {
        if (ReadGameJson(path) is JsonResource { Root.Chunk: locVoiceoverMap map })
        {
            foreach (var e in map.Entries)
            {
                into[e.StringId] = new LineVoice(VoicePath(e.FemaleResPath.DepotPath), VoicePath(e.MaleResPath.DepotPath));
            }
        }
    }

    private static string? VoicePath(ResourcePath path)
    {
        var hash = path.GetRedHash();
        return hash == 0 ? null : path.GetResolvedText() ?? hash.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A game JSON resource by path, or null when the loaded archives do not have it (a language without voice-over, say).</summary>
    private RedBaseClass? ReadGameJson(string path)
    {
        var file = _host.Archives.Lookup(path);
        if (!file.HasValue)
        {
            return null;
        }
        try
        {
            return ReadCr2w(file.Value).RootChunk;
        }
        catch (Exception e)
        {
            _host.Log.Warning($"{path}: {e.Message}");
            return null;
        }
    }

    private static IEnumerable<LineEntry> LineEntriesOf(RedBaseClass? root, string source, string file) =>
        root is JsonResource { Root.Chunk: localizationPersistenceSubtitleEntries list }
            ? list.Entries.Select((e, i) => new LineEntry(((ulong)e.StringId).ToString(CultureInfo.InvariantCulture), (string?)e.FemaleVariant ?? "", (string?)e.MaleVariant ?? "", source, file, i))
            : [];

    /// <summary>The game's lines with the project's laid over them: a project entry replaces the game's with the same stringId.</summary>
    private List<LineEntry> MergedLines(string language, string? projectDir)
    {
        var game = GameLinesIndex(language);
        if (projectDir is null)
        {
            return game.Entries;
        }
        var project = ProjectLineEntries(projectDir, language);
        if (project.Count == 0)
        {
            return game.Entries;
        }
        var ids = project.Select(e => e.StringId).ToHashSet();
        return project.Concat(game.Entries.Where(e => !ids.Contains(e.StringId))).ToList();
    }

    /// <summary>The project's subtitle entries in a language, in file order; each file is read again only when it changes.</summary>
    private List<LineEntry> ProjectLineEntries(string projectDir, string language)
    {
        var entries = new List<LineEntry>();
        foreach (var file in ProjectLocFilesOf(projectDir, language, "subtitles").Where(f => f.Exists).Select(f => Path.Combine(projectDir, f.File)))
        {
            var written = File.GetLastWriteTimeUtc(file);
            lock (ProjectLineFiles)
            {
                if (!ProjectLineFiles.TryGetValue(file, out var cached) || cached.Written != written)
                {
                    List<LineEntry> read;
                    try
                    {
                        using var fs = File.OpenRead(file);
                        read = LineEntriesOf(_host.Parser.ReadRed4File(fs)?.RootChunk, "project", Path.GetRelativePath(projectDir, file)).ToList();
                    }
                    catch
                    {
                        read = []; // not a CR2W subtitles file
                    }
                    cached = ProjectLineFiles[file] = (written, read);
                }
                entries.AddRange(cached.Entries);
            }
        }
        return entries;
    }
}
