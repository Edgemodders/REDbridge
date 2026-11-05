using System.Collections;
using System.Globalization;
using System.Text.Json;
using StreamJsonRpc;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.CR2W.JSON;
using WolvenKit.RED4.Types;

namespace REDbridge;

/// <summary>
/// CR2W document sessions for REDcode's editor: a file is parsed once and kept in memory while the
/// editor has it open; the editor reads one node at a time and sends edits as JSON Patch operations.
/// Paths are dotted, from the root chunk, and match the paths in WolvenKit's JSON: property names,
/// array indices, "Data" under a handle or a buffer, "Chunks" under a package.
/// See REDcode's docs/design/cr2w-editor.md.
/// </summary>
public sealed partial class BridgeApi
{
    private sealed class OpenDocument
    {
        public required string Id { get; init; }
        public required CR2WFile File { get; init; }
        public required string Type { get; init; }
        public string? Path { get; set; }
        public string? GamePath { get; init; }
        public bool ReadOnly { get; set; }
        public long Bytes { get; set; }
    }

    private readonly Dictionary<string, OpenDocument> _documents = new();
    private int _nextDocument;

    public sealed record DocumentInfo(string Doc, string Type, string? File, string? GamePath, bool ReadOnly, long Bytes, int EmbeddedFiles);
    /// <summary>One child of a node. Value is filled for leaves and for small structs (a table row, a vector), so a page of rows needs one call.</summary>
    public sealed record NodeChild(string Name, string Kind, string Type, string Preview, int? Count, JsonElement? Value);
    public sealed record NodeDto(string Path, string Kind, string Type, JsonElement? Value, IReadOnlyList<NodeChild>? Children, int? Total, int? Offset);
    public sealed record PatchOp(string Op, string Path, JsonElement? Value = null, string? From = null);
    public sealed record PatchResult(int Applied, IReadOnlyList<PatchOp> Inverse);

    private const int DefaultPageSize = 200;
    private const int SmallStructProperties = 8;

    // ---- session ---------------------------------------------------------------------------

    /// <summary>Opens a CR2W file from disk (editable) or from the archives by game path (read-only).</summary>
    [JsonRpcMethod("cr2w.open")]
    public DocumentInfo Open(string? file = null, string? path = null)
    {
        CR2WFile cr2w;
        var doc = new OpenDocument { Id = $"d{++_nextDocument}", File = null!, Type = "" };
        if (file is not null)
        {
            using var fs = File.OpenRead(file);
            cr2w = _host.Parser.ReadRed4File(fs) ?? throw new InvalidDataException($"'{file}' is not a CR2W file");
            doc = new OpenDocument { Id = doc.Id, File = cr2w, Type = TypeNameOf(cr2w.RootChunk), Path = System.IO.Path.GetFullPath(file), Bytes = fs.Length };
        }
        else if (path is not null)
        {
            var gameFile = Find(path);
            cr2w = ReadCr2w(gameFile);
            doc = new OpenDocument { Id = doc.Id, File = cr2w, Type = TypeNameOf(cr2w.RootChunk), GamePath = gameFile.Name, ReadOnly = true, Bytes = gameFile.Size };
        }
        else
        {
            throw new ArgumentException("file or path is required");
        }
        _documents[doc.Id] = doc;
        return Describe(doc);
    }

    [JsonRpcMethod("cr2w.close")]
    public void Close(string doc)
    {
        if (_documents.Remove(doc, out var open))
        {
            open.File.Dispose();
        }
    }

    [JsonRpcMethod("cr2w.documents")]
    public IReadOnlyList<DocumentInfo> Documents() => _documents.Values.Select(Describe).ToList();

    /// <summary>Writes the document with WolvenKit's writer, so its pre-processors run, to its own path or a new one.</summary>
    [JsonRpcMethod("cr2w.save")]
    public ExtractResult Save(string doc, string? outFile = null)
    {
        var open = Require(doc);
        var target = outFile ?? (open.ReadOnly ? throw new InvalidOperationException("This document was opened from the archives; pass outFile to save a copy.") : open.Path!);
        target = System.IO.Path.GetFullPath(target);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        using (var fs = File.Create(target))
        {
            using var writer = new CR2WWriter(fs) { LoggerService = _host.Log };
            writer.WriteFile(open.File);
            open.Bytes = fs.Length;
        }
        open.Path = target;
        open.ReadOnly = false;
        return new ExtractResult(target, open.Bytes);
    }

    private OpenDocument Require(string doc) =>
        _documents.TryGetValue(doc, out var open) ? open : throw new KeyNotFoundException($"No open document '{doc}'. Call cr2w.open first.");

    private static DocumentInfo Describe(OpenDocument d) =>
        new(d.Id, d.Type, d.Path, d.GamePath, d.ReadOnly, d.Bytes, d.File.EmbeddedFiles.Count);

    // ---- reading -------------------------------------------------------------------------

    /// <summary>One node by path. Arrays come back in a window of <paramref name="limit"/> items from <paramref name="offset"/>.</summary>
    [JsonRpcMethod("cr2w.getNode")]
    public NodeDto GetNode(string doc, string path = "", int offset = 0, int limit = DefaultPageSize)
    {
        var open = Require(doc);
        var node = Resolve(open.File, path);
        return ToDto(node, path, offset, Math.Clamp(limit, 1, 5000));
    }

    private static object? Resolve(CR2WFile file, string path)
    {
        object? current = file.RootChunk;
        foreach (var segment in Segments(path))
        {
            current = Step(current, segment, path);
        }
        return current;
    }

    private static IEnumerable<string> Segments(string path) =>
        string.IsNullOrEmpty(path) ? Array.Empty<string>() : path.Split('.');

    /// <summary>One step down from a node, by the same name the JSON uses.</summary>
    private static object? Step(object? current, string segment, string fullPath)
    {
        switch (current)
        {
            case RedBaseClass cls:
                if (!cls.HasProperty(segment))
                {
                    throw new KeyNotFoundException($"'{fullPath}': {TypeNameOf(cls)} has no property '{segment}'");
                }
                return cls.GetProperty(segment);
            case IRedBaseHandle handle:
                return segment == "Data" ? handle.GetValue() : throw new KeyNotFoundException($"'{fullPath}': a handle has only 'Data', not '{segment}'");
            case IRedBufferWrapper buffer:
                return segment == "Data" ? buffer.Buffer.Data : throw new KeyNotFoundException($"'{fullPath}': a buffer has only 'Data', not '{segment}'");
            case RedPackage package:
                return segment == "Chunks" ? package.Chunks : throw new KeyNotFoundException($"'{fullPath}': a package has only 'Chunks', not '{segment}'");
            case IList list:
                return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < list.Count
                    ? list[index]
                    : throw new KeyNotFoundException($"'{fullPath}': index '{segment}' is outside 0..{list.Count - 1}");
            case null:
                throw new KeyNotFoundException($"'{fullPath}': nothing at '{segment}', the parent is null");
            default:
                throw new KeyNotFoundException($"'{fullPath}': '{segment}' is below a value, which has no children");
        }
    }

    private static string KindOf(object? value) => value switch
    {
        null => "null",
        IRedBaseHandle => "handle",
        IRedBufferWrapper => "buffer",
        RedPackage => "package",
        IList => "array",
        RedBaseClass => "class",
        _ => "value",
    };

    private static string TypeNameOf(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case RedPackage: return "RedPackage";
            case List<RedBaseClass>: return "array:RedBaseClass";
            case IRedType t:
                try { return RedReflection.GetRedTypeFromCSType(t.GetType()); }
                catch { return t.GetType().Name; }
            default: return value.GetType().Name;
        }
    }

    private static NodeDto ToDto(object? node, string path, int offset, int limit)
    {
        var kind = KindOf(node);
        var type = TypeNameOf(node);
        switch (node)
        {
            case RedBaseClass cls:
            {
                var props = PropertiesOf(cls).ToList();
                var children = props.Select(p => ChildOf(p.name, p.value)).ToList();
                var small = props.Count <= SmallStructProperties && props.All(p => KindOf(p.value) is "value" or "null");
                return new NodeDto(path, kind, type, small ? ToElement(cls) : null, children, null, null);
            }
            case IRedBaseHandle handle:
            {
                var inner = handle.GetValue();
                var children = inner is null ? new List<NodeChild>() : new List<NodeChild> { ChildOf("Data", inner) };
                return new NodeDto(path, kind, type, null, children, null, null);
            }
            case IRedBufferWrapper buffer:
            {
                var data = buffer.Buffer.Data;
                var children = data is null ? new List<NodeChild>() : new List<NodeChild> { ChildOf("Data", data) };
                return new NodeDto(path, kind, type, null, children, null, null);
            }
            case RedPackage package:
                return new NodeDto(path, kind, type, null, new List<NodeChild> { ChildOf("Chunks", package.Chunks) }, null, null);
            case IList list:
            {
                var children = new List<NodeChild>();
                for (var i = offset; i < list.Count && i < offset + limit; i++)
                {
                    children.Add(ChildOf(i.ToString(CultureInfo.InvariantCulture), list[i]));
                }
                return new NodeDto(path, kind, type, null, children, list.Count, offset);
            }
            default:
                return new NodeDto(path, kind, type, node is null ? null : ToElement(node), null, null, null);
        }
    }

    private static IEnumerable<(string name, object? value)> PropertiesOf(RedBaseClass cls)
    {
        var seen = new HashSet<string>();
        // IsIgnored marks properties the binary writer skips, such as an entity's components and entity,
        // which WolvenKit rebuilds compiledData from. They are in the JSON and are what the editor edits.
        foreach (var p in RedReflection.GetTypeInfo(cls.GetType()).PropertyInfos)
        {
            if (p.RedName is not null && p.Name != "Appendix" && seen.Add(p.RedName))
            {
                yield return (p.RedName, cls.GetProperty(p.RedName));
            }
        }
        foreach (var p in cls.GetDynamicProperties())
        {
            if (p.RedName is not null && seen.Add(p.RedName))
            {
                yield return (p.RedName, cls.GetProperty(p.RedName));
            }
        }
    }

    private static NodeChild ChildOf(string name, object? value)
    {
        var kind = KindOf(value);
        var type = TypeNameOf(value);
        int? count = value is IList l ? l.Count : null;
        JsonElement? inline = kind == "value" ? ToElement(value!) : value is RedBaseClass cls && IsSmallStruct(cls) ? ToElement(cls) : null;
        return new NodeChild(name, kind, type, Preview(value, kind, type), count, inline);
    }

    /// <summary>A class with a few leaf properties and nothing nested: a vector, a rectangle, a localization row.</summary>
    private static bool IsSmallStruct(RedBaseClass cls)
    {
        var n = 0;
        foreach (var (_, value) in PropertiesOf(cls))
        {
            if (++n > SmallStructProperties || KindOf(value) is not ("value" or "null"))
            {
                return false;
            }
        }
        return true;
    }

    private static string Preview(object? value, string kind, string type)
    {
        switch (kind)
        {
            case "null": return "null";
            case "array": return $"{((IList)value!).Count} items";
            case "handle":
            {
                var inner = ((IRedBaseHandle)value!).GetValue();
                return inner is null ? "null" : Preview(inner, "class", TypeNameOf(inner));
            }
            case "buffer":
            {
                var b = (IRedBufferWrapper)value!;
                var bytes = b.Buffer.GetBytes()?.Length ?? 0;
                return b.Buffer.Data is null ? $"{bytes:N0} bytes" : $"{bytes:N0} bytes, {TypeNameOf(b.Buffer.Data)}";
            }
            case "package": return $"{((RedPackage)value!).Chunks.Count} chunks";
            case "class":
            {
                var cls = (RedBaseClass)value!;
                var name = cls.HasProperty("name") ? cls.GetProperty("name")?.ToString() : null;
                return string.IsNullOrEmpty(name) || name == "None" ? type : $"{type} {name}";
            }
            default:
            {
                var text = value is IRedType ? RedJsonSerializer.Serialize(value) : value!.ToString() ?? "";
                return text.Length > 80 ? text[..77] + "..." : text;
            }
        }
    }

    private static JsonElement ToElement(object value)
    {
        using var parsed = JsonDocument.Parse(RedJsonSerializer.Serialize(value));
        return parsed.RootElement.Clone();
    }

    // ---- patching ------------------------------------------------------------------------

    /// <summary>
    /// Applies JSON Patch operations (replace, add, remove, move) atomically: all of them or none.
    /// Values are in WolvenKit's JSON form. The response carries the inverse operations, in the order
    /// that undoes the change, so the editor can implement undo without a second read.
    /// </summary>
    [JsonRpcMethod("cr2w.patch")]
    public PatchResult Patch(string doc, IReadOnlyList<PatchOp> ops)
    {
        var open = Require(doc);
        if (open.ReadOnly)
        {
            throw new InvalidOperationException("This document was opened from the archives and is read-only. Save a copy with cr2w.save first.");
        }
        var inverse = new List<PatchOp>();
        try
        {
            foreach (var op in ops)
            {
                inverse.Insert(0, Apply(open.File, op));
            }
        }
        catch
        {
            foreach (var undo in inverse)
            {
                Apply(open.File, undo);
            }
            throw;
        }
        return new PatchResult(ops.Count, inverse);
    }

    private static PatchOp Apply(CR2WFile file, PatchOp op)
    {
        var (parentPath, last) = SplitPath(op.Path);
        var parent = Resolve(file, parentPath);
        switch (op.Op)
        {
            case "replace":
            {
                if (op.Value is null) throw new ArgumentException($"replace '{op.Path}': value is required");
                var old = Step(parent, last, op.Path);
                var value = Deserialize(TargetType(parent, last, op.Path), op.Value.Value, op.Path);
                Set(parent, last, value, op.Path);
                return new PatchOp("replace", op.Path, old is null ? null : ToElement(old));
            }
            case "add":
            {
                if (op.Value is null) throw new ArgumentException($"add '{op.Path}': value is required");
                if (parent is IList list)
                {
                    var index = last == "-" ? list.Count : ParseIndex(last, list.Count + 1, op.Path);
                    var value = Deserialize(ElementType(parent), op.Value.Value, op.Path);
                    list.Insert(index, value);
                    return new PatchOp("remove", $"{parentPath}{(parentPath.Length > 0 ? "." : "")}{index}");
                }
                // On a class, add means set, as in JSON Patch.
                var previous = Step(parent, last, op.Path);
                Set(parent, last, Deserialize(TargetType(parent, last, op.Path), op.Value.Value, op.Path), op.Path);
                return new PatchOp("replace", op.Path, previous is null ? null : ToElement(previous));
            }
            case "remove":
            {
                if (parent is IList list)
                {
                    var index = ParseIndex(last, list.Count, op.Path);
                    var old = list[index];
                    list.RemoveAt(index);
                    return new PatchOp("add", op.Path, old is null ? null : ToElement(old));
                }
                if (parent is RedBaseClass cls)
                {
                    if (!cls.HasProperty(last)) throw new KeyNotFoundException($"'{op.Path}': {TypeNameOf(cls)} has no property '{last}'");
                    var old = cls.GetProperty(last);
                    if (!cls.ResetProperty(last)) throw new KeyNotFoundException($"'{op.Path}': no property '{last}' to remove");
                    return new PatchOp("replace", op.Path, old is null ? null : ToElement(old));
                }
                throw new ArgumentException($"remove '{op.Path}': only array items and class properties can be removed");
            }
            case "move":
            {
                if (op.From is null) throw new ArgumentException($"move '{op.Path}': from is required");
                var (fromParentPath, fromLast) = SplitPath(op.From);
                if (Resolve(file, fromParentPath) is not IList source || parent is not IList target)
                {
                    throw new ArgumentException($"move '{op.From}' -> '{op.Path}': both must be array items");
                }
                var fromIndex = ParseIndex(fromLast, source.Count, op.From);
                var item = source[fromIndex];
                source.RemoveAt(fromIndex);
                var toIndex = last == "-" ? target.Count : ParseIndex(last, target.Count + 1, op.Path);
                target.Insert(toIndex, item);
                return new PatchOp("move", op.From, null, $"{parentPath}{(parentPath.Length > 0 ? "." : "")}{toIndex}");
            }
            default:
                throw new ArgumentException($"Unknown patch op '{op.Op}'; use replace, add, remove or move");
        }
    }

    private static (string parent, string last) SplitPath(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("The root chunk itself cannot be patched; patch its properties");
        var i = path.LastIndexOf('.');
        return i < 0 ? ("", path) : (path[..i], path[(i + 1)..]);
    }

    private static int ParseIndex(string segment, int count, string path) =>
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < count
            ? index
            : throw new KeyNotFoundException($"'{path}': index '{segment}' is outside 0..{count - 1}");

    /// <summary>The CLR type a value at this position must have.</summary>
    private static Type TargetType(object? parent, string last, string path)
    {
        switch (parent)
        {
            case RedBaseClass cls:
            {
                var info = RedReflection.GetPropertyByRedName(cls.GetType(), last)
                           ?? cls.GetDynamicProperties().FirstOrDefault(p => p.RedName == last)
                           ?? throw new KeyNotFoundException($"'{path}': {TypeNameOf(cls)} has no property '{last}'");
                return info.Type;
            }
            case IRedBaseHandle handle when last == "Data":
                return handle.InnerType;
            case IList:
                return ElementType(parent);
            default:
                throw new ArgumentException($"'{path}': cannot set '{last}' under a {KindOf(parent)}");
        }
    }

    private static Type ElementType(object? list) => list switch
    {
        IRedArray arr => arr.InnerType,
        List<RedBaseClass> => typeof(RedBaseClass),
        _ => throw new ArgumentException("not an array"),
    };

    private static object? Deserialize(Type type, JsonElement element, string path)
    {
        try
        {
            // WolvenKit's JSON writes a CName as { $type, $storage, $value }; a bare string is accepted too.
            if (type == typeof(CName) && element.ValueKind == JsonValueKind.String)
            {
                return (CName)element.GetString()!;
            }
            return RedJsonSerializer.Deserialize(type, element);
        }
        catch (Exception e)
        {
            throw new ArgumentException($"'{path}': the value is not a valid {RedReflection.GetRedTypeFromCSType(type)}: {e.Message}", e);
        }
    }

    private static void Set(object? parent, string last, object? value, string path)
    {
        switch (parent)
        {
            case RedBaseClass cls:
                cls.SetProperty(last, (IRedType?)value);
                break;
            case IRedBaseHandle handle when last == "Data":
                handle.SetValue((RedBaseClass?)value);
                break;
            case IList list:
                list[ParseIndex(last, list.Count, path)] = value;
                break;
            default:
                throw new ArgumentException($"'{path}': cannot set '{last}' under a {KindOf(parent)}");
        }
    }

    // ---- batch reads and search ----------------------------------------------------------

    /// <summary>Several nodes in one call, for a grid or a filtered table. A path that fails gives an error entry instead of failing the call.</summary>
    [JsonRpcMethod("cr2w.getNodes")]
    public IReadOnlyList<NodeDto> GetNodes(string doc, IReadOnlyList<string> paths, int limit = DefaultPageSize)
    {
        var open = Require(doc);
        var result = new List<NodeDto>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                result.Add(ToDto(Resolve(open.File, path), path, 0, Math.Clamp(limit, 1, 5000)));
            }
            catch (Exception e)
            {
                result.Add(new NodeDto(path, "error", e.Message, null, null, null, null));
            }
        }
        return result;
    }

    public sealed record SearchHit(string Path, string Type, string Preview);
    public sealed record SearchResult(int Total, bool Truncated, IReadOnlyList<SearchHit> Hits);

    /// <summary>
    /// Finds leaves whose text contains the query (case-insensitive), or whose property name does,
    /// below a start path. Strings, names, resource paths, enums and numbers are searched as text.
    /// Total counts every match; Hits holds the first <paramref name="limit"/>.
    /// </summary>
    [JsonRpcMethod("cr2w.search")]
    public SearchResult Search(string doc, string query, string path = "", int limit = DefaultPageSize)
    {
        var open = Require(doc);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchResult(0, false, Array.Empty<SearchHit>());
        }
        var start = Resolve(open.File, path);
        var hits = new List<SearchHit>();
        var total = 0;
        // The same object can be reachable by two paths (a component and its copy in the compiled
        // package share instances after parsing), and both paths are wanted. Only cycles are cut.
        var stack = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(object? node, string nodePath, string name, int depth)
        {
            if (depth > 64)
            {
                return;
            }
            switch (node)
            {
                case null:
                    return;
                case RedBaseClass cls:
                    if (!stack.Add(cls)) return;
                    foreach (var (propName, value) in PropertiesOf(cls))
                    {
                        Visit(value, Join(nodePath, propName), propName, depth + 1);
                    }
                    stack.Remove(cls);
                    return;
                case IRedBaseHandle handle:
                    Visit(handle.GetValue(), Join(nodePath, "Data"), name, depth + 1);
                    return;
                case IRedBufferWrapper buffer:
                    Visit(buffer.Buffer.Data, Join(nodePath, "Data"), name, depth + 1);
                    return;
                case RedPackage package:
                    Visit(package.Chunks, Join(nodePath, "Chunks"), name, depth + 1);
                    return;
                case IList list:
                    for (var i = 0; i < list.Count; i++)
                    {
                        Visit(list[i], Join(nodePath, i.ToString(CultureInfo.InvariantCulture)), name, depth + 1);
                    }
                    return;
                default:
                {
                    var text = LeafText(node);
                    if (text.Contains(query, StringComparison.OrdinalIgnoreCase) || name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        total++;
                        if (hits.Count < limit)
                        {
                            hits.Add(new SearchHit(nodePath, TypeNameOf(node), Shorten(text)));
                        }
                    }
                    return;
                }
            }
        }
        Visit(start, path, path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : path, 0);
        return new SearchResult(total, total > hits.Count, hits);
    }

    /// <summary>Long text cut in the middle, so a path keeps its file name.</summary>
    private static string Shorten(string text) => text.Length > 80 ? text[..30] + "..." + text[^47..] : text;

    private static string Join(string parent, string child) => parent.Length == 0 ? child : $"{parent}.{child}";

    /// <summary>A leaf as the text a person would search for: resolved names and paths, enum names, numbers.</summary>
    private static string LeafText(object leaf) => leaf switch
    {
        CName n => n.GetResolvedText() ?? n.ToString() ?? "",
        ResourcePath p => p.GetResolvedText() ?? p.ToString() ?? "",
        // A reference as its path (or its hash, unnamed), without the type and hash its ToString adds.
        IRedRef r => r.DepotPath.GetResolvedText() ?? ((ulong)r.DepotPath).ToString(CultureInfo.InvariantCulture),
        IRedEnum e => e.ToEnumString(),
        // A material's values (MultilayerSetup, GlobalNormal, ...): the key and what it holds, such as a .mlsetup path.
        CKeyValuePair kv => $"{kv.Key.GetResolvedText() ?? kv.Key.ToString()}: {(kv.Value is null ? "" : LeafText(kv.Value))}",
        _ => leaf.ToString() ?? "",
    };

    // ---- types ---------------------------------------------------------------------------

    public sealed record PropertyDescription(string Name, string Type, string Kind, JsonElement? Default, IReadOnlyList<string>? EnumValues, string? ElementType);
    public sealed record TypeDescription(string Type, string Kind, string? BaseType, IReadOnlyList<PropertyDescription>? Properties, IReadOnlyList<string>? EnumValues, string? ElementType);

    private readonly Dictionary<string, TypeDescription> _typeCache = new();

    /// <summary>
    /// What a RED type is made of: for a class its properties with types, kinds and defaults; for an
    /// enum or bit field its values; for an array or handle its element type. Drives the property grid.
    /// </summary>
    [JsonRpcMethod("cr2w.describeType")]
    public TypeDescription DescribeType(string type)
    {
        if (_typeCache.TryGetValue(type, out var cached))
        {
            return cached;
        }
        var (cs, _) = RedReflection.GetCSTypeFromRedType(type);
        var description = Describe(cs);
        _typeCache[type] = description;
        return description;
    }

    /// <summary>A default instance of a RED type, in JSON form, for adding array items and filling handles.</summary>
    [JsonRpcMethod("cr2w.createInstance")]
    public JsonElement CreateInstance(string type)
    {
        var (cs, _) = RedReflection.GetCSTypeFromRedType(type);
        object instance = typeof(RedBaseClass).IsAssignableFrom(cs) ? RedTypeManager.CreateAndInit(cs) : RedTypeManager.CreateRedType(cs);
        return ToElement(instance);
    }

    private static TypeDescription Describe(Type cs)
    {
        var name = RedName(cs);
        var kind = KindOfType(cs);
        switch (kind)
        {
            case "class":
            {
                var properties = new List<PropertyDescription>();
                foreach (var p in RedReflection.GetTypeInfo(cs).PropertyInfos)
                {
                    if (p.RedName is null || p.Name == "Appendix")
                    {
                        continue;
                    }
                    JsonElement? def = null;
                    try
                    {
                        var value = RedReflection.GetClassDefaultValue(cs, p);
                        if (value is not null)
                        {
                            def = ToElement(value);
                        }
                    }
                    catch
                    {
                        // No default for this property: the grid falls back to createInstance.
                    }
                    properties.Add(new PropertyDescription(p.RedName, RedName(p.Type), KindOfType(p.Type), def, EnumValuesOf(p.Type), ElementTypeOf(p.Type)));
                }
                var baseType = cs.BaseType is not null && cs.BaseType != typeof(RedBaseClass) && typeof(RedBaseClass).IsAssignableFrom(cs.BaseType) ? RedName(cs.BaseType) : null;
                return new TypeDescription(name, kind, baseType, properties, null, null);
            }
            case "enum":
            case "flags":
                return new TypeDescription(name, kind, null, null, EnumValuesOf(cs), null);
            default:
                return new TypeDescription(name, kind, null, null, null, ElementTypeOf(cs));
        }
    }

    private static string RedName(Type cs)
    {
        try { return RedReflection.GetRedTypeFromCSType(cs); }
        catch { return cs.Name; }
    }

    private static string KindOfType(Type cs)
    {
        if (typeof(RedBaseClass).IsAssignableFrom(cs)) return "class";
        if (typeof(IRedBaseHandle).IsAssignableFrom(cs)) return "handle";
        if (typeof(IRedArray).IsAssignableFrom(cs) || typeof(IRedArrayFixedSize).IsAssignableFrom(cs)) return "array";
        if (typeof(IRedBufferWrapper).IsAssignableFrom(cs)) return "buffer";
        if (cs.IsGenericType && cs.GetGenericTypeDefinition().Name.StartsWith("CBitField", StringComparison.Ordinal)) return "flags";
        if (typeof(IRedEnum).IsAssignableFrom(cs)) return "enum";
        return "value";
    }

    private static IReadOnlyList<string>? EnumValuesOf(Type cs)
    {
        var kind = KindOfType(cs);
        if (kind is not ("enum" or "flags") || !cs.IsGenericType)
        {
            return null;
        }
        var inner = cs.GetGenericArguments()[0];
        return inner.IsEnum ? Enum.GetNames(inner) : null;
    }

    private static string? ElementTypeOf(Type cs)
    {
        var kind = KindOfType(cs);
        if (kind is not ("array" or "handle") || !cs.IsGenericType)
        {
            return null;
        }
        return RedName(cs.GetGenericArguments()[0]);
    }
}
