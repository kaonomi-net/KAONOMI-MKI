using System.Text.Json;
using System.Text.Json.Nodes;
using KAONOMI.SearchEngine.Http;

namespace KAONOMI.SearchEngine.Engine;

/// <summary>The kinds of fields the engine indexes (the types of Elasticsearch are mapped on them).</summary>
public enum EsType
{
    Text,
    Keyword,
    Date,
    Long,
    Double,
    Boolean,
    Object,
    /// <summary>A type the engine does not search (geo, ip…): the value stays in <c>_source</c> only.</summary>
    Other
}

/// <summary>The mapping of one field (or one sub-field such as <c>comment.keyword</c>).</summary>
public sealed class FieldMapping
{
    public required string Path { get; init; }
    public required EsType Type { get; init; }

    /// <summary>The type as Elasticsearch names it (<c>float</c>, <c>integer</c>, <c>nested</c>…).</summary>
    public required string TypeName { get; init; }

    public int? IgnoreAbove { get; init; }
    public string? Format { get; init; }
    public bool Index { get; init; } = true;

    /// <summary>For an object: <c>enabled: false</c> (its content is kept in <c>_source</c> but not indexed).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>For an object: its own <c>dynamic</c> setting, or null (the one of its parent).</summary>
    public string? Dynamic { get; init; }

    /// <summary>The sub-fields (<c>fields</c>), by name.</summary>
    public IReadOnlyDictionary<string, FieldMapping> SubFields { get; init; } = new Dictionary<string, FieldMapping>();

    /// <summary>The parameters the engine does not use, written back as they were given.</summary>
    public JsonObject? Extra { get; init; }

    public bool IsSubField { get; init; }

    /// <summary>The internal Lucene fields: the lower-case copy of a keyword, the sort values.</summary>
    public string LowerField => Path + "\u0001lc";
    public string MinField => Path + "\u0001min";
    public string MaxField => Path + "\u0001max";

    /// <summary>The <c>text</c> field with a <c>keyword</c> sub-field (<c>ignore_above: 256</c>) of the dynamic mapping.</summary>
    public static FieldMapping DynamicText(string path) => new()
    {
        Path = path,
        Type = EsType.Text,
        TypeName = "text",
        SubFields = new Dictionary<string, FieldMapping>
        {
            ["keyword"] = new() { Path = path + ".keyword", Type = EsType.Keyword, TypeName = "keyword", IgnoreAbove = 256, IsSubField = true }
        }
    };
}

/// <summary>
/// The mapping of an index: every field by its full path (<c>obj.x</c>, sub-fields included: <c>comment.keyword</c>),
/// read without lock (a new dictionary replaces the old one when a field is added), written as Elasticsearch writes it.
/// </summary>
public sealed class IndexMapping
{
    private readonly object _lock = new();
    private volatile Dictionary<string, FieldMapping> _fields = new(StringComparer.Ordinal);

    /// <summary><c>true</c>, <c>false</c> (new fields are not indexed) or <c>strict</c> (a new field is an error).</summary>
    public string Dynamic { get; private set; } = "true";
    public bool DateDetection { get; private set; } = true;
    public bool NumericDetection { get; private set; }
    public JsonNode? Meta { get; private set; }

    /// <summary>Raised when a field was added (the mapping is saved).</summary>
    public event Action? Changed;

    public FieldMapping? Get(string path) => _fields.TryGetValue(path, out var field) ? field : null;

    public IReadOnlyDictionary<string, FieldMapping> Fields => _fields;

    /// <summary>Adds the field (and its sub-fields), unless it exists; returns the field of the mapping.</summary>
    public FieldMapping Add(FieldMapping field)
    {
        lock (_lock)
        {
            if (_fields.TryGetValue(field.Path, out var existing))
                return existing;
            var copy = new Dictionary<string, FieldMapping>(_fields, StringComparer.Ordinal) { [field.Path] = field };
            foreach (var sub in field.SubFields.Values)
                copy[sub.Path] = sub;
            _fields = copy;
        }
        Changed?.Invoke();
        return field;
    }

    /// <summary>The <c>dynamic</c> setting that applies inside <paramref name="objectPath"/> (empty: the root).</summary>
    public string DynamicAt(string objectPath)
    {
        var path = objectPath;
        while (path.Length > 0)
        {
            if (Get(path)?.Dynamic is { } dynamic)
                return dynamic;
            var dot = path.LastIndexOf('.');
            path = dot < 0 ? string.Empty : path[..dot];
        }
        return Dynamic;
    }

    // ── JSON ──

    /// <summary>Reads the <c>mappings</c> of a create index or put mapping request (merged into this mapping).</summary>
    public void Merge(JsonElement mappings)
    {
        if (mappings.ValueKind != JsonValueKind.Object)
            throw EsException.MapperParsing("Failed to parse mapping: the mapping must be an object");
        // The type name of the old requests ({"_doc": {"properties": …}}).
        if (!mappings.TryGetProperty("properties", out _) && mappings.EnumerateObject().Count() == 1
            && mappings.EnumerateObject().First() is { Value.ValueKind: JsonValueKind.Object } only
            && !only.Name.StartsWith("dynamic", StringComparison.Ordinal) && only.Name != "date_detection")
            mappings = only.Value;
        foreach (var property in mappings.EnumerateObject())
        {
            switch (property.Name)
            {
                case "dynamic":
                    Dynamic = DynamicValue(property.Value);
                    break;
                case "date_detection":
                    DateDetection = property.Value.ValueKind == JsonValueKind.True || property.Value.ToString() == "true";
                    break;
                case "numeric_detection":
                    NumericDetection = property.Value.ValueKind == JsonValueKind.True || property.Value.ToString() == "true";
                    break;
                case "_meta":
                    Meta = JsonNode.Parse(property.Value.GetRawText());
                    break;
                case "properties":
                    ReadProperties(property.Value, string.Empty);
                    break;
            }
        }
        Changed?.Invoke();
    }

    private static string DynamicValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetString() ?? "true"
    };

    private void ReadProperties(JsonElement properties, string prefix)
    {
        foreach (var property in properties.EnumerateObject())
        {
            var path = prefix + property.Name;
            var field = ReadField(property.Value, path, isSub: false);
            lock (_lock)
            {
                var copy = new Dictionary<string, FieldMapping>(_fields, StringComparer.Ordinal);
                if (copy.TryGetValue(path, out var old) && old.Type != field.Type && !(old.Type == EsType.Object && field.Type == EsType.Object))
                    throw new EsException(400, "illegal_argument_exception",
                        $"mapper [{path}] cannot be changed from type [{old.TypeName}] to [{field.TypeName}]");
                copy[path] = field;
                foreach (var sub in field.SubFields.Values)
                    copy[sub.Path] = sub;
                _fields = copy;
            }
            if (field.Type == EsType.Object && property.Value.TryGetProperty("properties", out var children))
                ReadProperties(children, path + ".");
        }
    }

    private static FieldMapping ReadField(JsonElement json, string path, bool isSub)
    {
        var typeName = json.TryGetProperty("type", out var t) ? t.GetString() ?? "object" : "object";
        var type = typeName switch
        {
            "text" or "match_only_text" or "search_as_you_type" => EsType.Text,
            "keyword" or "constant_keyword" or "wildcard" or "version" => EsType.Keyword,
            "date" or "date_nanos" => EsType.Date,
            "long" or "integer" or "short" or "byte" or "unsigned_long" => EsType.Long,
            "double" or "float" or "half_float" or "scaled_float" => EsType.Double,
            "boolean" => EsType.Boolean,
            "object" or "nested" => EsType.Object,
            _ => EsType.Other
        };
        var extra = new JsonObject();
        var subFields = new Dictionary<string, FieldMapping>();
        int? ignoreAbove = null;
        string? format = null, dynamic = null;
        bool index = true, enabled = true;
        foreach (var p in json.EnumerateObject())
        {
            switch (p.Name)
            {
                case "type" or "properties":
                    break;
                case "ignore_above":
                    ignoreAbove = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : int.Parse(p.Value.GetString()!);
                    break;
                case "format":
                    format = p.Value.GetString();
                    break;
                case "index":
                    index = p.Value.ValueKind != JsonValueKind.False && p.Value.ToString() != "false";
                    break;
                case "enabled":
                    enabled = p.Value.ValueKind != JsonValueKind.False && p.Value.ToString() != "false";
                    break;
                case "dynamic":
                    dynamic = DynamicValue(p.Value);
                    break;
                case "fields" when !isSub:
                    foreach (var sub in p.Value.EnumerateObject())
                        subFields[sub.Name] = ReadField(sub.Value, path + "." + sub.Name, isSub: true);
                    break;
                default:
                    extra[p.Name] = JsonNode.Parse(p.Value.GetRawText());
                    break;
            }
        }
        if (type == EsType.Object && !enabled)
            type = EsType.Other;
        return new FieldMapping
        {
            Path = path,
            Type = type,
            TypeName = typeName,
            IgnoreAbove = ignoreAbove,
            Format = format,
            Index = index,
            Enabled = enabled,
            Dynamic = dynamic,
            SubFields = subFields,
            Extra = extra.Count > 0 ? extra : null,
            IsSubField = isSub
        };
    }

    /// <summary>The mapping as Elasticsearch writes it (<c>{"properties": …}</c>, the fields sorted by name).</summary>
    public JsonObject ToJson()
    {
        var root = new JsonObject();
        if (Dynamic != "true")
            root["dynamic"] = Dynamic;
        if (!DateDetection)
            root["date_detection"] = false;
        if (NumericDetection)
            root["numeric_detection"] = true;
        if (Meta is not null)
            root["_meta"] = Meta.DeepClone();
        var fields = _fields;
        var properties = WriteProperties(fields, string.Empty);
        if (properties.Count > 0)
            root["properties"] = properties;
        return root;
    }

    private static JsonObject WriteProperties(Dictionary<string, FieldMapping> fields, string prefix)
    {
        var properties = new JsonObject();
        var names = fields.Values
            .Where(f => !f.IsSubField && f.Path.StartsWith(prefix, StringComparison.Ordinal) && !f.Path[prefix.Length..].Contains('.'))
            .OrderBy(f => f.Path, StringComparer.Ordinal);
        foreach (var field in names)
            properties[field.Path[prefix.Length..]] = WriteField(field, fields);
        return properties;
    }

    private static JsonObject WriteField(FieldMapping field, Dictionary<string, FieldMapping> fields)
    {
        var json = new JsonObject();
        if (field.TypeName != "object" || !field.Enabled)
            json["type"] = field.TypeName;
        if (field.Dynamic is not null)
            json["dynamic"] = field.Dynamic;
        if (!field.Enabled)
            json["enabled"] = false;
        if (field.Format is not null)
            json["format"] = field.Format;
        if (field.IgnoreAbove is { } above)
            json["ignore_above"] = above;
        if (!field.Index)
            json["index"] = false;
        if (field.Extra is not null)
            foreach (var (key, value) in field.Extra)
                json[key] = value?.DeepClone();
        if (field.SubFields.Count > 0)
        {
            var subs = new JsonObject();
            foreach (var (name, sub) in field.SubFields.OrderBy(s => s.Key, StringComparer.Ordinal))
                subs[name] = WriteField(sub, fields);
            json["fields"] = subs;
        }
        if (field.Type == EsType.Object)
        {
            var children = WriteProperties(fields, field.Path + ".");
            if (children.Count > 0)
                json["properties"] = children;
        }
        return json;
    }
}
