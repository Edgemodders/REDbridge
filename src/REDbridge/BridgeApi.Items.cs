using StreamJsonRpc;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// The TweakDB items that use a mesh, walking the chain an item takes to its mesh backwards:
/// record (<c>entityName</c>, <c>appearanceName</c>) to factory <c>.csv</c> to the root <c>.ent</c>,
/// whose appearance (<c>appearanceName</c> plus suffixes such as <c>&amp;Male</c>) names an <c>.app</c>
/// and a look in it, whose mesh components name the mesh and its mesh appearance. Asked for in
/// REDcode's mesh viewer (Find Items). The first call builds an index from file headers only (the
/// root entities' and their appearance files' imports), so later calls read just the files on the
/// way to the mesh.
/// </summary>
public sealed partial class BridgeApi
{
    private sealed class ItemIndex
    {
        /// <summary>Item records by entityName: (record id, appearanceName).</summary>
        public required Dictionary<string, List<(ulong Id, string AppearanceName)>> Records { get; init; }
        /// <summary>Root entity path (lower case) to the entityNames the factories give it.</summary>
        public required Dictionary<string, List<string>> EntityNames { get; init; }
        /// <summary>.app path hash to the root entities that import it.</summary>
        public required Dictionary<ulong, List<string>> EntitiesOfApp { get; init; }
        /// <summary>Mesh path hash to the .app files that import it.</summary>
        public required Dictionary<ulong, List<string>> AppsOfMesh { get; init; }
        /// <summary>Root entities read so far: their appearances as (entry name, .app hash, look).</summary>
        public Dictionary<string, List<(string Name, ulong App, string Look)>> Entities { get; } = new(StringComparer.OrdinalIgnoreCase);
        public required double Seconds { get; init; }
    }

    private static ItemIndex? _items;
    private static readonly object ItemsLock = new();

    /// <summary>
    /// One item that uses the mesh. <see cref="AppearanceName"/> is the record's; <see cref="Entity"/>
    /// the root entity and <see cref="EntityAppearances"/> its entries for this look (one per body and
    /// camera); <see cref="App"/> and <see cref="Look"/> the appearance file and its look;
    /// <see cref="MeshAppearance"/> the mesh's appearance that look shows.
    /// </summary>
    public sealed record MeshItem(string Record, string Type, LocKeyValue? DisplayName, string AppearanceName, string MeshAppearance, string App, string Look, string Entity, IReadOnlyList<string> EntityAppearances);

    /// <summary>A look of an .app file that shows the mesh, and how many items reach it.</summary>
    public sealed record MeshLook(string App, string Look, string MeshAppearance, int Items);

    /// <summary>
    /// <see cref="Items"/>, sorted by record; <see cref="Looks"/> every .app look that shows the mesh,
    /// with or without items (NPC appearances have none). <see cref="IndexSeconds"/> is how long the
    /// index took on the first call.
    /// </summary>
    public sealed record MeshItems(string Mesh, IReadOnlyList<MeshItem> Items, IReadOnlyList<MeshLook> Looks, double IndexSeconds);

    /// <summary>
    /// The TweakDB item records (clothing, weapons and other items) whose appearance shows the mesh at
    /// <paramref name="path"/>, a game path. With <paramref name="appearance"/>, only the looks that show
    /// that mesh appearance. Covers .app looks whose own components (or compiled data) hold the mesh;
    /// a mesh that only a part entity holds is not found.
    /// </summary>
    [JsonRpcMethod("mesh.items")]
    public MeshItems MeshItemsOf(string path, string? appearance = null, string language = "en-us")
    {
        var index = Items();
        var t = Tweak();
        var mesh = path.Replace('/', '\\');
        ResourcePath meshPath = mesh;
        var loc = LocTexts(language);
        var items = new List<MeshItem>();
        var looks = new List<MeshLook>();
        foreach (var app in index.AppsOfMesh.GetValueOrDefault((ulong)meshPath) ?? [])
        {
            List<(string Look, string MeshAppearance)> shown;
            try
            {
                shown = LooksShowing(ReadCr2w(Find(app)).RootChunk as appearanceAppearanceResource, meshPath);
            }
            catch (Exception e)
            {
                _host.Log.Warning($"mesh.items {app}: {e.Message}");
                continue;
            }
            ResourcePath appPath = app;
            foreach (var (look, meshAppearance) in shown)
            {
                if (appearance is not null && !string.Equals(appearance, meshAppearance, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var count = 0;
                foreach (var entity in index.EntitiesOfApp.GetValueOrDefault((ulong)appPath) ?? [])
                {
                    // Entries for this look, grouped by their name without suffixes: the record's appearanceName.
                    var entries = EntityAppearances(index, entity).Where(e => e.App == (ulong)appPath && string.Equals(e.Look, look, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var group in entries.GroupBy(e => BaseAppearanceName(e.Name), StringComparer.OrdinalIgnoreCase))
                    {
                        foreach (var entityName in index.EntityNames.GetValueOrDefault(entity.ToLowerInvariant()) ?? [])
                        {
                            foreach (var (id, appearanceName) in index.Records.GetValueOrDefault(entityName) ?? [])
                            {
                                if (!string.Equals(appearanceName, group.Key, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                var display = FlatOf(t, id, "displayName") is gamedataLocKeyWrapper k
                                    ? new LocKeyValue($"LocKey#{k.Key}", loc.TryGetValue(k.Key, out var text) ? text : null)
                                    : null;
                                items.Add(new MeshItem(TweakDBIDPoolName(id), TypeName(t.Types[id]), display, appearanceName, meshAppearance, app, look, entity, group.Select(e => e.Name).ToList()));
                                count++;
                            }
                        }
                    }
                }
                looks.Add(new MeshLook(app, look, meshAppearance, count));
            }
        }
        items.Sort((a, b) => string.CompareOrdinal(a.Record, b.Record) is var n && n != 0 ? n : string.CompareOrdinal(a.Look, b.Look));
        return new MeshItems(mesh, items, looks, index.Seconds);
    }

    // ---- helpers --------------------------------------------------------------------------

    private static string TweakDBIDPoolName(ulong id) => WolvenKit.RED4.Types.Pools.TweakDBIDPool.ResolveHash(id) ?? IdText(id);

    /// <summary>An entity appearance's name without its suffixes: "s1_casual_01_basic_01_&amp;Male" is "s1_casual_01_basic_01_".</summary>
    private static string BaseAppearanceName(string name) => name.IndexOf('&') is var i and >= 0 ? name[..i] : name;

    /// <summary>The looks of an .app that show the mesh, each with the mesh appearance, after the parts' component overrides.</summary>
    private static List<(string Look, string MeshAppearance)> LooksShowing(appearanceAppearanceResource? app, ResourcePath mesh)
    {
        var result = new List<(string, string)>();
        if (app is null)
        {
            return result;
        }
        foreach (var handle in app.Appearances)
        {
            if (handle.Chunk is not { } look)
            {
                continue;
            }
            var components = look.Components.Count > 0 ? look.Components.Cast<IRedType>()
                : look.CompiledData?.Buffer?.Data is RedPackage package ? package.Chunks.Cast<IRedType>() : [];
            var overrides = look.PartsOverrides.SelectMany(p => p.ComponentsOverrides)
                .Where(o => o.MeshAppearance.GetResolvedText() is { Length: > 0 } a && a != "None")
                .GroupBy(o => o.ComponentName.GetResolvedText() ?? "")
                .ToDictionary(g => g.Key, g => g.Last().MeshAppearance.GetResolvedText()!);
            foreach (var component in components)
            {
                var (name, path, meshAppearance) = component switch
                {
                    entMeshComponent m => (m.Name.GetResolvedText(), m.Mesh.DepotPath, m.MeshAppearance.GetResolvedText()),
                    entSkinnedMeshComponent s => (s.Name.GetResolvedText(), s.Mesh.DepotPath, s.MeshAppearance.GetResolvedText()),
                    _ => (null, default(ResourcePath), null),
                };
                if ((ulong)path != (ulong)mesh)
                {
                    continue;
                }
                var shown = name is not null && overrides.TryGetValue(name, out var o) ? o : meshAppearance ?? "default";
                var lookName = look.Name.GetResolvedText() ?? "";
                if (!result.Contains((lookName, shown)))
                {
                    result.Add((lookName, shown));
                }
            }
        }
        return result;
    }

    /// <summary>A root entity's appearances, read once.</summary>
    private List<(string Name, ulong App, string Look)> EntityAppearances(ItemIndex index, string entity)
    {
        lock (ItemsLock)
        {
            if (index.Entities.TryGetValue(entity, out var known))
            {
                return known;
            }
            var list = new List<(string, ulong, string)>();
            try
            {
                if (ReadCr2w(Find(entity)).RootChunk is entEntityTemplate ent)
                {
                    foreach (var a in ent.Appearances)
                    {
                        list.Add((a.Name.GetResolvedText() ?? "", (ulong)a.AppearanceResource.DepotPath, a.AppearanceName.GetResolvedText() ?? ""));
                    }
                }
            }
            catch (Exception e)
            {
                _host.Log.Warning($"mesh.items {entity}: {e.Message}");
            }
            return index.Entities[entity] = list;
        }
    }

    /// <summary>The imports of a game file, from its header; empty when it is missing or not CR2W.</summary>
    private List<ResourcePath> ImportsOf(string path)
    {
        var file = _host.Archives.Lookup(path);
        if (!file.HasValue)
        {
            return [];
        }
        using var ms = new MemoryStream();
        file.Value.Extract(ms);
        ms.Position = 0;
        return _host.Parser.TryReadRed4FileHeaders(ms, out var info) ? info.GetImports() : [];
    }

    /// <summary>
    /// Builds the index once: item records by entityName, the factories' root entities, and from
    /// headers the .app files each root entity imports and the meshes each .app imports.
    /// </summary>
    private ItemIndex Items()
    {
        lock (ItemsLock)
        {
            if (_items is not null)
            {
                return _items;
            }
            var t = Tweak();
            var started = DateTime.Now;

            var records = new Dictionary<string, List<(ulong, string)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, _, type) in t.Records)
            {
                if (!type.IsAssignableTo(typeof(gamedataItem_Record)) ||
                    FlatOf(t, id, "entityName") is not CName entity || entity.GetResolvedText() is not { Length: > 0 } entityName || entityName == "None" ||
                    FlatOf(t, id, "appearanceName") is not CName appearance || appearance.GetResolvedText() is not { Length: > 0 } appearanceName || appearanceName == "None")
                {
                    continue;
                }
                if (!records.TryGetValue(entityName, out var list))
                {
                    records[entityName] = list = [];
                }
                list.Add((id, appearanceName));
            }

            // Factories: base\gameplay\factories.csv and Phantom Liberty's list the factory .csv files in
            // their one column; a factory's rows are entityName, root entity path, preload.
            var entityNames = new Dictionary<string, List<string>>();
            foreach (var list in new[] { @"base\gameplay\factories.csv", @"ep1\gameplay\factories.csv" })
            {
                foreach (var factory in CsvRows(list).Select(r => r.Length > 0 ? r[0] : ""))
                {
                    foreach (var row in CsvRows(factory))
                    {
                        if (row.Length > 1 && records.ContainsKey(row[0]) && row[1].EndsWith(".ent", StringComparison.OrdinalIgnoreCase))
                        {
                            var key = row[1].ToLowerInvariant();
                            if (!entityNames.TryGetValue(key, out var names))
                            {
                                entityNames[key] = names = [];
                            }
                            if (!names.Contains(row[0], StringComparer.OrdinalIgnoreCase))
                            {
                                names.Add(row[0]);
                            }
                        }
                    }
                }
            }

            var entitiesOfApp = new Dictionary<ulong, List<string>>();
            var apps = new Dictionary<ulong, string>();
            foreach (var entity in entityNames.Keys)
            {
                foreach (var import in ImportsOf(entity))
                {
                    if (import.GetResolvedText() is not { } p || !p.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!entitiesOfApp.TryGetValue((ulong)import, out var list))
                    {
                        entitiesOfApp[(ulong)import] = list = [];
                    }
                    list.Add(entity);
                    apps[(ulong)import] = p;
                }
            }

            var appsOfMesh = new Dictionary<ulong, List<string>>();
            foreach (var app in apps.Values)
            {
                foreach (var import in ImportsOf(app))
                {
                    if (import.GetResolvedText() is { } p && p.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!appsOfMesh.TryGetValue((ulong)import, out var list))
                        {
                            appsOfMesh[(ulong)import] = list = [];
                        }
                        list.Add(app);
                    }
                }
            }

            var seconds = (DateTime.Now - started).TotalSeconds;
            _host.Log.Info($"mesh.items index: {records.Sum(r => r.Value.Count):N0} item records, {entityNames.Count:N0} root entities, {apps.Count:N0} appearance files, {appsOfMesh.Count:N0} meshes, in {seconds:F1} s");
            return _items = new ItemIndex { Records = records, EntityNames = entityNames, EntitiesOfApp = entitiesOfApp, AppsOfMesh = appsOfMesh, Seconds = seconds };
        }
    }

    /// <summary>The data rows of a game .csv (a C2dArray); empty when it is missing.</summary>
    private List<string[]> CsvRows(string path)
    {
        if (path.Length == 0 || !_host.Archives.Lookup(path).HasValue)
        {
            return [];
        }
        try
        {
            return ReadCr2w(Find(path)).RootChunk is C2dArray csv
                ? csv.Data.Select(r => r.Select(c => (string?)c ?? "").ToArray()).ToList()
                : [];
        }
        catch (Exception e)
        {
            _host.Log.Warning($"mesh.items {path}: {e.Message}");
            return [];
        }
    }
}
