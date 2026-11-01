using System.Text.Json;

namespace KAONOMI.SearchEngine.Http;

/// <summary>
/// An error answered as Elasticsearch answers it: <c>{"error":{"root_cause":[…],"type":…,"reason":…},"status":…}</c>.
/// <see cref="Extra"/> holds the other fields of the error (<c>index</c>, <c>index_uuid</c>, <c>resource.id</c>…).
/// </summary>
public class EsException : Exception
{
    public int Status { get; }
    public string Type { get; }
    public IReadOnlyList<(string Key, string Value)> Extra { get; }
    public EsException? CausedBy { get; init; }

    /// <summary>For the search errors: the shard failure is wrapped as Elasticsearch does ("all shards failed").</summary>
    public bool ShardFailure { get; init; }

    /// <summary>The index the shard failure is about.</summary>
    public string? FailedIndex { get; init; }

    public EsException(int status, string type, string reason, params (string Key, string Value)[] extra) : base(reason)
    {
        Status = status;
        Type = type;
        Extra = extra;
    }

    public static EsException IndexNotFound(string index, string resourceType = "index_expression") =>
        new(404, "index_not_found_exception", $"no such index [{index}]",
            ("resource.type", resourceType), ("resource.id", index), ("index_uuid", "_na_"), ("index", index));

    public static EsException BadRequest(string reason) => new(400, "illegal_argument_exception", reason);

    public static EsException Parsing(string reason) => new(400, "parsing_exception", reason, ("line", "1"), ("col", "1"));

    public static EsException MapperParsing(string reason, EsException? cause = null) =>
        new(400, "mapper_parsing_exception", reason) { CausedBy = cause };

    /// <summary>Writes the error body.</summary>
    public void Write(Utf8JsonWriter json, string nodeId)
    {
        json.WriteStartObject();
        json.WritePropertyName("error");
        if (ShardFailure)
        {
            json.WriteStartObject();
            json.WriteStartArray("root_cause");
            WriteCause(json, this, withCause: false);
            json.WriteEndArray();
            json.WriteString("type", "search_phase_execution_exception");
            json.WriteString("reason", "all shards failed");
            json.WriteString("phase", "query");
            json.WriteBoolean("grouped", true);
            json.WriteStartArray("failed_shards");
            json.WriteStartObject();
            json.WriteNumber("shard", 0);
            json.WriteString("index", FailedIndex);
            json.WriteString("node", nodeId);
            json.WritePropertyName("reason");
            WriteCause(json, this, withCause: true);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }
        else
        {
            json.WriteStartObject();
            json.WriteStartArray("root_cause");
            WriteCause(json, this, withCause: false);
            json.WriteEndArray();
            WriteFields(json, this);
            if (CausedBy is not null)
            {
                json.WritePropertyName("caused_by");
                WriteCause(json, CausedBy, withCause: true);
            }
            json.WriteEndObject();
        }
        json.WriteNumber("status", Status);
        json.WriteEndObject();
    }

    private static void WriteCause(Utf8JsonWriter json, EsException error, bool withCause)
    {
        json.WriteStartObject();
        WriteFields(json, error);
        if (withCause && error.CausedBy is not null)
        {
            json.WritePropertyName("caused_by");
            WriteCause(json, error.CausedBy, withCause: true);
        }
        json.WriteEndObject();
    }

    private static void WriteFields(Utf8JsonWriter json, EsException error)
    {
        json.WriteString("type", error.Type);
        json.WriteString("reason", error.Message);
        foreach (var (key, value) in error.Extra)
        {
            if (key is "line" or "col" && int.TryParse(value, out var n))
                json.WriteNumber(key, n);
            else
                json.WriteString(key, value);
        }
    }
}
