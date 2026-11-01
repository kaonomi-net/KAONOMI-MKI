using System.Globalization;
using System.Text;
using System.Text.Json;
using KAONOMI.SearchEngine.Http;
using Lucene.Net.Documents;
using Lucene.Net.Util;

namespace KAONOMI.SearchEngine.Engine;

/// <summary>
/// Turns the JSON of a document into the fields of a Lucene document, as Elasticsearch indexes it: the new fields get
/// the dynamic mapping (a string: <c>text</c> + <c>keyword</c>, or <c>date</c> when it reads as a date; an integer:
/// <c>long</c>; a decimal: <c>float</c>; <c>boolean</c>; an object: its fields), the values are coerced to the type
/// of their field (<c>"7"</c> in a <c>long</c>) or refused as Elasticsearch refuses them.
/// </summary>
public sealed class DocumentParser
{
    /// <summary>Internal fields of every document.</summary>
    public const string IdField = "_id";
    public const string SourceField = "_source";
    public const string VersionField = "_version";
    public const string SeqNoField = "_seq_no";
    public const string FieldNamesField = "_field_names";

    private readonly IndexMapping _mapping;
    private readonly string _id;
    private readonly Document _doc = new();
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Min, long Max)> _longs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Min, double Max)> _doubles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (BytesRef Min, BytesRef Max)> _keywords = new(StringComparer.Ordinal);

    private DocumentParser(IndexMapping mapping, string id)
    {
        _mapping = mapping;
        _id = id;
    }

    /// <summary>The Lucene document of <paramref name="source"/> (its <c>_id</c>, <c>_source</c> and fields).</summary>
    public static Document Parse(IndexMapping mapping, string id, byte[] source, long version, long seqNo)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(source);
        }
        catch (JsonException ex)
        {
            throw EsException.MapperParsing("failed to parse", new EsException(400, "json_parse_exception", ex.Message));
        }
        using (json)
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw EsException.MapperParsing("failed to parse",
                    new EsException(400, "not_x_content_exception", "Compressor detection can only be called on some xcontent bytes or compressed xcontent bytes"));
            var parser = new DocumentParser(mapping, id);
            parser._doc.Add(new StringField(IdField, id, Field.Store.YES));
            parser._doc.Add(new StoredField(SourceField, source));
            parser._doc.Add(new StoredField(VersionField, version));
            parser._doc.Add(new StoredField(SeqNoField, seqNo));
            parser.ParseObject(json.RootElement, string.Empty);
            parser.Finish();
            return parser._doc;
        }
    }

    private void ParseObject(JsonElement obj, string prefix)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Name.Length == 0)
                throw EsException.MapperParsing("field name cannot be an empty string");
            var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            ParseValue(property.Value, path, prefix);
        }
    }

    private void ParseValue(JsonElement value, string path, string parent)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    ParseValue(item, path, parent);
                return;
            case JsonValueKind.Object:
            {
                var field = _mapping.Get(path) ?? NewField(path, parent, value);
                if (field is null)
                    return; // dynamic: false
                if (field.Type == EsType.Other)
                    return;
                if (field.Type != EsType.Object)
                    throw EsException.MapperParsing(
                        $"failed to parse field [{path}] of type [{field.TypeName}] in document with id '{_id}'. Preview of field's value: '{Preview(value)}'",
                        new EsException(400, "illegal_state_exception", $"Can't get text on a START_OBJECT at 1:1"));
                AddName(path);
                ParseObject(value, path);
                return;
            }
            default:
            {
                var field = _mapping.Get(path) ?? NewField(path, parent, value);
                if (field is null)
                    return;
                if (field.Type == EsType.Object)
                    throw EsException.MapperParsing($"object mapping for [{path}] tried to parse field [{path}] as object, but found a concrete value");
                IndexScalar(field, value);
                return;
            }
        }
    }

    /// <summary>The dynamic mapping of a new field, or null when the new fields are not indexed.</summary>
    private FieldMapping? NewField(string path, string parent, JsonElement value)
    {
        // A dotted name ("a.b") creates its objects.
        var dot = path.LastIndexOf('.');
        if (dot > 0 && parent.Length < dot)
        {
            var objectPath = path[..dot];
            var existing = _mapping.Get(objectPath);
            if (existing is null)
            {
                if (NewObject(objectPath) is null)
                    return null;
            }
            else if (existing.Type == EsType.Other)
                return null;
        }
        var dynamic = _mapping.DynamicAt(dot < 0 ? string.Empty : path[..dot]);
        if (dynamic == "false")
            return null;
        if (dynamic == "strict")
            throw new EsException(400, "strict_dynamic_mapping_exception",
                $"mapping set to strict, dynamic introduction of [{(dot < 0 ? path : path[(dot + 1)..])}] within [{(dot < 0 ? "_doc" : path[..dot])}] is not allowed");
        FieldMapping field = value.ValueKind switch
        {
            JsonValueKind.Object => new FieldMapping { Path = path, Type = EsType.Object, TypeName = "object" },
            JsonValueKind.True or JsonValueKind.False => new FieldMapping { Path = path, Type = EsType.Boolean, TypeName = "boolean" },
            JsonValueKind.Number => IsInteger(value)
                ? new FieldMapping { Path = path, Type = EsType.Long, TypeName = "long" }
                : new FieldMapping { Path = path, Type = EsType.Double, TypeName = "float" },
            _ => StringField(path, value.GetString() ?? string.Empty)
        };
        return _mapping.Add(field);
    }

    private FieldMapping? NewObject(string path)
    {
        var dot = path.LastIndexOf('.');
        if (dot > 0 && _mapping.Get(path[..dot]) is null && NewObject(path[..dot]) is null)
            return null;
        var dynamic = _mapping.DynamicAt(dot < 0 ? string.Empty : path[..dot]);
        if (dynamic == "false")
            return null;
        if (dynamic == "strict")
            throw new EsException(400, "strict_dynamic_mapping_exception",
                $"mapping set to strict, dynamic introduction of [{path}] within [_doc] is not allowed");
        return _mapping.Add(new FieldMapping { Path = path, Type = EsType.Object, TypeName = "object" });
    }

    private FieldMapping StringField(string path, string value)
    {
        if (_mapping.DateDetection && EsDates.Detect(value) is { } format)
            return new FieldMapping { Path = path, Type = EsType.Date, TypeName = "date", Format = format == EsDates.DefaultFormat ? null : format };
        if (_mapping.NumericDetection)
        {
            if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                return new FieldMapping { Path = path, Type = EsType.Long, TypeName = "long" };
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return new FieldMapping { Path = path, Type = EsType.Double, TypeName = "float" };
        }
        return FieldMapping.DynamicText(path);
    }

    private static bool IsInteger(JsonElement value)
    {
        var raw = value.GetRawText();
        return raw.IndexOfAny(['.', 'e', 'E']) < 0 && value.TryGetInt64(out _);
    }

    private void IndexScalar(FieldMapping field, JsonElement value)
    {
        if (field.Type == EsType.Other || !field.Index && field.SubFields.Count == 0)
            return;
        AddName(field.Path);
        switch (field.Type)
        {
            case EsType.Text:
            {
                var text = AsString(value);
                if (field.Index)
                    _doc.Add(new TextField(field.Path, text, Field.Store.NO));
                foreach (var sub in field.SubFields.Values)
                    IndexSub(sub, text);
                break;
            }
            case EsType.Keyword:
                IndexKeyword(field, AsString(value));
                foreach (var sub in field.SubFields.Values)
                    IndexSub(sub, AsString(value));
                break;
            case EsType.Date:
            {
                long millis;
                try
                {
                    millis = value.ValueKind == JsonValueKind.Number
                        ? (long)value.GetDouble()
                        : EsDates.Parse(AsString(value), field.Format ?? EsDates.DefaultFormat);
                }
                catch (EsException cause)
                {
                    throw Refused(field, value, cause);
                }
                AddLong(field, millis);
                break;
            }
            case EsType.Long:
                AddLong(field, ToLong(field, value));
                break;
            case EsType.Double:
            {
                var number = ToDouble(field, value);
                if (field.TypeName is "float" or "half_float")
                    number = (float)number;
                if (field.Index)
                    _doc.Add(new DoubleField(field.Path, number, Field.Store.NO));
                _doubles[field.Path] = _doubles.TryGetValue(field.Path, out var d) ? (Math.Min(d.Min, number), Math.Max(d.Max, number)) : (number, number);
                break;
            }
            case EsType.Boolean:
            {
                var flag = ToBoolean(field, value);
                if (field.Index)
                    _doc.Add(new StringField(field.Path, flag ? "T" : "F", Field.Store.NO));
                AddSortLong(field.Path, flag ? 1 : 0);
                break;
            }
        }
    }

    private void IndexSub(FieldMapping sub, string text)
    {
        switch (sub.Type)
        {
            case EsType.Keyword:
                IndexKeyword(sub, text);
                break;
            case EsType.Text when sub.Index:
                _doc.Add(new TextField(sub.Path, text, Field.Store.NO));
                break;
        }
    }

    private void IndexKeyword(FieldMapping field, string text)
    {
        if (field.IgnoreAbove is { } above && text.Length > above)
            return;
        if (Encoding.UTF8.GetByteCount(text) > 32766)
            throw new EsException(400, "illegal_argument_exception",
                $"Document contains at least one immense term in field=\"{field.Path}\" (whose UTF8 encoding is longer than the max length 32766), all of which were skipped.");
        if (field.IsSubField)
            AddName(field.Path);
        if (field.Index)
        {
            _doc.Add(new StringField(field.Path, text, Field.Store.NO));
            _doc.Add(new StringField(field.LowerField, text.ToLowerInvariant(), Field.Store.NO));
        }
        var bytes = new BytesRef(text);
        _keywords[field.Path] = _keywords.TryGetValue(field.Path, out var k)
            ? (k.Min.CompareTo(bytes) <= 0 ? k.Min : bytes, k.Max.CompareTo(bytes) >= 0 ? k.Max : bytes)
            : (bytes, bytes);
    }

    private void AddLong(FieldMapping field, long value)
    {
        if (field.Index)
            _doc.Add(new Int64Field(field.Path, value, Field.Store.NO));
        AddSortLong(field.Path, value);
    }

    private void AddSortLong(string path, long value) =>
        _longs[path] = _longs.TryGetValue(path, out var l) ? (Math.Min(l.Min, value), Math.Max(l.Max, value)) : (value, value);

    private void AddName(string path)
    {
        if (_names.Add(path))
            _doc.Add(new StringField(FieldNamesField, path, Field.Store.NO));
    }

    /// <summary>The doc values used to sort: the smallest (ascending) and the largest (descending) value of each field.</summary>
    private void Finish()
    {
        foreach (var (path, (min, max)) in _longs)
        {
            _doc.Add(new NumericDocValuesField(path + "\u0001min", min));
            _doc.Add(new NumericDocValuesField(path + "\u0001max", max));
        }
        foreach (var (path, (min, max)) in _doubles)
        {
            _doc.Add(new DoubleDocValuesField(path + "\u0001min", min));
            _doc.Add(new DoubleDocValuesField(path + "\u0001max", max));
        }
        foreach (var (path, (min, max)) in _keywords)
        {
            _doc.Add(new SortedDocValuesField(path + "\u0001min", min));
            _doc.Add(new SortedDocValuesField(path + "\u0001max", max));
        }
    }

    private static string AsString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText()
    };

    private long ToLong(FieldMapping field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out var n) ? n : (long)Math.Truncate(value.GetDouble());
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString()!.Trim();
            if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                return n;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d))
                return (long)Math.Truncate(d);
        }
        throw Refused(field, value, new EsException(400, "illegal_argument_exception", $"For input string: \"{AsString(value)}\""));
    }

    private double ToDouble(FieldMapping field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        throw Refused(field, value, new EsException(400, "number_format_exception", $"For input string: \"{AsString(value)}\""));
    }

    private bool ToBoolean(FieldMapping field, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String when value.GetString() is "true":
                return true;
            case JsonValueKind.String when value.GetString() is "false" or "":
                return false;
        }
        throw Refused(field, value, new EsException(400, "illegal_argument_exception",
            $"Failed to parse value [{AsString(value)}] as only [true] or [false] are allowed."));
    }

    private EsException Refused(FieldMapping field, JsonElement value, EsException cause) =>
        EsException.MapperParsing(
            $"failed to parse field [{field.Path}] of type [{field.TypeName}] in document with id '{_id}'. Preview of field's value: '{Preview(value)}'",
            cause);

    private static string Preview(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        return text.Length > 100 ? text[..100] : text;
    }
}
