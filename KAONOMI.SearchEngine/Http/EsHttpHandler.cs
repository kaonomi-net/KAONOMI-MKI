using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KAONOMI.SearchEngine.Configuration;
using KAONOMI.SearchEngine.Engine;
using Microsoft.AspNetCore.Http.Features;

namespace KAONOMI.SearchEngine.Http;

/// <summary>
/// The REST API of Elasticsearch 7.14 that the engine answers: the root, the documents (<c>_doc</c>, <c>_create</c>,
/// <c>_update</c>, <c>_source</c>, <c>_bulk</c>, <c>_mget</c>), the searches (<c>_search</c>, <c>_count</c>), the
/// indices (create, delete, exists, <c>_mapping</c>, <c>_settings</c>, <c>_refresh</c>, <c>_flush</c>) and the health
/// (<c>_cluster/health</c>, <c>_cat/indices</c>, <c>_cat/health</c>, <c>_cat/count</c>). The answers have the shape,
/// the status codes and the errors of Elasticsearch, and the <c>X-elastic-product</c> header the clients check.
/// </summary>
public sealed class EsHttpHandler
{
    public const string Version = "0.1";

    /// <summary>Raised by <c>POST /_shutdown</c>: the host stops the engine (after the answer).</summary>
    public event Action? ShutdownRequested;

    private readonly IndexManager _indices;
    private readonly EngineSettings _settings;
    private readonly HashSet<string>? _credentials;
    private readonly EngineLog _log;

    public EsHttpHandler(IndexManager indices, EngineSettings settings, EngineLog log)
    {
        _indices = indices;
        _settings = settings;
        _log = log;
        if (settings.SecurityEnabled)
            _credentials = settings.Users.Select(u => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{u.Name}:{u.Password}"))).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The request being answered.</summary>
    private sealed class Call
    {
        public required string Method { get; init; }
        public required string[] Segments { get; init; }
        public required Dictionary<string, string> Parameters { get; init; }
        public required byte[] Body { get; init; }
        public int Status { get; set; } = 200;
        public string? Location { get; set; }
        public string ContentType { get; set; } = "application/json; charset=UTF-8";
        public byte[]? Text { get; set; }

        public string? Param(string name) => Parameters.TryGetValue(name, out var value) ? value : null;
        public bool Flag(string name) => Parameters.TryGetValue(name, out var value) && value is "" or "true";

        public JsonElement? Json()
        {
            if (Body.Length == 0 || Body.All(b => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
                return null;
            try
            {
                return JsonDocument.Parse(Body).RootElement;
            }
            catch (JsonException ex)
            {
                throw new EsException(400, "x_content_parse_exception", ex.Message, ("line", (ex.LineNumber + 1 ?? 1).ToString()), ("col", (ex.BytePositionInLine ?? 1).ToString()))
                {
                    CausedBy = new EsException(400, "json_parse_exception", ex.Message)
                };
            }
        }
    }

    public async Task HandleAsync(HttpContext context)
    {
        var started = DateTime.UtcNow;
        var response = context.Response;
        response.Headers["X-elastic-product"] = "Elasticsearch";
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path.Value ?? "/";
        var question = raw.IndexOf('?');
        var pathPart = question < 0 ? raw : raw[..question];
        var segments = pathPart.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var parameters = context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
        var method = context.Request.Method.ToUpperInvariant();

        var stream = new MemoryStream();
        Call? call = null;
        try
        {
            if (_credentials is not null && !Authorized(context))
            {
                response.Headers["WWW-Authenticate"] = "Basic realm=\"security\" charset=\"UTF-8\"";
                throw new EsException(401, "security_exception", User(context) is { } user
                    ? $"unable to authenticate user [{user}] for REST request [{pathPart}]"
                    : $"missing authentication credentials for REST request [{pathPart}]", ("header", "WWW-Authenticate"));
            }
            call = new Call { Method = method, Segments = segments, Parameters = parameters, Body = await ReadBodyAsync(context) };
            using (var json = new Utf8JsonWriter(stream, EsJson.Writer))
                Dispatch(call, json);
            response.StatusCode = call.Status;
            if (call.Location is not null)
                response.Headers.Location = call.Location;
        }
        catch (EsException ex)
        {
            stream = Error(ex);
            response.StatusCode = ex.Status;
            call = null;
        }
        catch (JsonException ex)
        {
            stream = Error(new EsException(400, "parsing_exception", ex.Message, ("line", "1"), ("col", "1")));
            response.StatusCode = 400;
            call = null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            stream = Error(new EsException(400, "illegal_argument_exception", ex.Message));
            response.StatusCode = 400;
            call = null;
        }
        catch (Exception ex)
        {
            _log.Warn("r.suppressed", $"path: {pathPart}, {ex}");
            stream = Error(new EsException(500, "exception", ex.Message));
            response.StatusCode = 500;
            call = null;
        }

        byte[] body;
        if (call?.Text is { } text)
        {
            body = text;
            response.ContentType = call.ContentType;
        }
        else
        {
            body = stream.ToArray();
            if (parameters.TryGetValue("pretty", out var pretty) && pretty != "false" && body.Length > 0)
                body = Encoding.UTF8.GetBytes(JsonNode.Parse(body)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            response.ContentType = "application/json; charset=UTF-8";
        }
        response.ContentLength = body.Length;
        if (method != "HEAD")
            await response.Body.WriteAsync(body);
        if (_settings.LogRequests)
            _log.Info("http", $"{method} {raw} {response.StatusCode} {(DateTime.UtcNow - started).TotalMilliseconds:F1} ms");
    }

    private MemoryStream Error(EsException ex)
    {
        var stream = new MemoryStream();
        using var json = new Utf8JsonWriter(stream, EsJson.Writer);
        ex.Write(json, _indices.NodeId);
        json.Flush();
        return stream;
    }

    private bool Authorized(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) && _credentials!.Contains(header[6..].Trim());
    }

    /// <summary>The user of the basic authentication header, or null.</summary>
    private static string? User(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var colon = decoded.IndexOf(':');
            return colon < 0 ? decoded : decoded[..colon];
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        var input = context.Request.Body;
        var encoding = context.Request.Headers.ContentEncoding.ToString();
        if (encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            input = new GZipStream(input, CompressionMode.Decompress);
        else if (encoding.Contains("deflate", StringComparison.OrdinalIgnoreCase))
            input = new DeflateStream(input, CompressionMode.Decompress);
        await input.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    // ── Routes ──

    private void Dispatch(Call call, Utf8JsonWriter json)
    {
        var s = call.Segments;
        var m = call.Method;
        switch (s.Length)
        {
            case 0 when m is "GET" or "HEAD":
                Root(json);
                return;
            case >= 1 when s[0] == "_cluster" && s.Length >= 2 && s[1] == "health" && m == "GET":
                ClusterHealth(json, s.Length > 2 ? s[2] : null);
                return;
            case >= 2 when s[0] == "_cat" && m == "GET":
                Cat(call, s[1], s.Length > 2 ? s[2] : null);
                return;
            case 1 when s[0] is "_shutdown" && m == "POST":
                Shutdown(json);
                return;
            case 1 when s[0] is "_search" && m is "GET" or "POST":
                Search(call, json, null);
                return;
            case 1 when s[0] is "_count" && m is "GET" or "POST":
                Count(call, json, null);
                return;
            case 1 when s[0] is "_bulk" && m is "POST" or "PUT":
                Bulk(call, json, null);
                return;
            case 1 when s[0] is "_mget" && m is "GET" or "POST":
                MultiGet(call, json, null);
                return;
            case 1 when s[0] is "_refresh" && m is "GET" or "POST":
                Refresh(json, null, flush: false);
                return;
            case 1 when s[0] is "_flush" && m is "GET" or "POST":
                Refresh(json, null, flush: true);
                return;
            case 1 when s[0] is "_mapping" && m == "GET":
                GetMapping(json, null);
                return;
            case 1 when s[0] is "_settings" && m == "GET":
                GetSettings(json, null);
                return;
            case 1 when s[0] is "_analyze" && m is "GET" or "POST":
                Analyze(call, json, null);
                return;
            case 1 when s[0] is "_aliases" or "_alias" && m == "GET":
                Aliases(json, null);
                return;
            case 1 when !s[0].StartsWith('_'):
                IndexAdmin(call, json, s[0]);
                return;
            case 2 when !s[0].StartsWith('_'):
                switch (s[1], m)
                {
                    case ("_doc", "POST"):
                        IndexDocument(call, json, s[0], null, createOnly: false);
                        return;
                    case ("_search", "GET" or "POST"):
                        Search(call, json, s[0]);
                        return;
                    case ("_count", "GET" or "POST"):
                        Count(call, json, s[0]);
                        return;
                    case ("_bulk", "POST" or "PUT"):
                        Bulk(call, json, s[0]);
                        return;
                    case ("_mget", "GET" or "POST"):
                        MultiGet(call, json, s[0]);
                        return;
                    case ("_refresh", "GET" or "POST"):
                        Refresh(json, s[0], flush: false);
                        return;
                    case ("_flush", "GET" or "POST"):
                        Refresh(json, s[0], flush: true);
                        return;
                    case ("_mapping", "GET"):
                        GetMapping(json, s[0]);
                        return;
                    case ("_mapping", "PUT" or "POST"):
                        PutMapping(call, json, s[0]);
                        return;
                    case ("_settings", "GET"):
                        GetSettings(json, s[0]);
                        return;
                    case ("_analyze", "GET" or "POST"):
                        Analyze(call, json, s[0]);
                        return;
                    case ("_alias" or "_aliases", "GET"):
                        Aliases(json, s[0]);
                        return;
                }
                break;
            case 3 when !s[0].StartsWith('_'):
                switch (s[1], m)
                {
                    case ("_doc", "PUT" or "POST"):
                        IndexDocument(call, json, s[0], s[2], createOnly: call.Param("op_type") == "create");
                        return;
                    case ("_create", "PUT" or "POST"):
                        IndexDocument(call, json, s[0], s[2], createOnly: true);
                        return;
                    case ("_doc", "GET" or "HEAD"):
                        GetDocument(call, json, s[0], s[2], sourceOnly: false);
                        return;
                    case ("_source", "GET" or "HEAD"):
                        GetDocument(call, json, s[0], s[2], sourceOnly: true);
                        return;
                    case ("_doc", "DELETE"):
                        DeleteDocument(call, json, s[0], s[2]);
                        return;
                    case ("_update", "POST"):
                        UpdateDocument(call, json, s[0], s[2]);
                        return;
                    case ("_mapping", "GET") when s[2] == "field":
                        break;
                }
                break;
        }
        call.Status = 400;
        json.WriteStartObject();
        json.WriteString("error", $"no handler found for uri [/{string.Join('/', s)}] and method [{m}]");
        json.WriteNumber("status", 400);
        json.WriteEndObject();
    }

    private void Root(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteString("name", _settings.NodeName);
        json.WriteString("cluster_name", _settings.ClusterName);
        json.WriteString("cluster_uuid", _indices.ClusterUuid);
        json.WriteStartObject("version");
        json.WriteString("number", Version);
        json.WriteString("build_flavor", "default");
        json.WriteString("build_type", "tar");
        json.WriteString("build_hash", "6bc13727ce758c0e943c3c21653b3da82f627f75");
        json.WriteString("build_date", "2021-09-15T10:18:09.722761972Z");
        json.WriteBoolean("build_snapshot", false);
        json.WriteString("lucene_version", "8.9.0");
        json.WriteString("minimum_wire_compatibility_version", "6.8.0");
        json.WriteString("minimum_index_compatibility_version", "6.0.0-beta1");
        json.WriteEndObject();
        json.WriteString("tagline", "let's go to think");
        json.WriteEndObject();
    }

    // ── Documents ──

    private void IndexDocument(Call call, Utf8JsonWriter json, string indexName, string? id, bool createOnly)
    {
        if (call.Body.Length == 0)
            throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: source is missing;2: content type is missing;");
        if (call.Param("op_type") is { } opType && opType is not ("index" or "create"))
            throw new EsException(400, "illegal_argument_exception", $"opType must be 'create' or 'index', found: [{opType}]");
        if (id is null && call.Param("op_type") == "create")
            throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: an id must be provided if version type or value are set;");
        var index = _indices.GetForWrite(indexName);
        var result = index.Index(id, call.Body, createOnly, call.Param("refresh"));
        call.Status = result.Result == "created" ? 201 : 200;
        if (call.Status == 201)
            call.Location = $"/{Uri.EscapeDataString(index.Name)}/_doc/{Uri.EscapeDataString(result.Id)}";
        WriteResult(json, index, result);
    }

    private void DeleteDocument(Call call, Utf8JsonWriter json, string indexName, string id)
    {
        var index = _indices.Get(indexName);
        var result = index.Delete(id, call.Param("refresh"));
        call.Status = result.Result == "deleted" ? 200 : 404;
        WriteResult(json, index, result);
    }

    private void UpdateDocument(Call call, Utf8JsonWriter json, string indexName, string id)
    {
        var body = call.Json() ?? throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: script or doc is missing;");
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        var doc = node["doc"] as JsonObject;
        if (doc is null && node["upsert"] is null)
            throw new EsException(400, "action_request_validation_exception",
                node["script"] is not null ? "KAONOMI.SearchEngine does not run scripts" : "Validation Failed: 1: script or doc is missing;");
        var upsert = node["doc_as_upsert"]?.GetValue<bool>() == true ? doc : node["upsert"] as JsonObject;
        var index = _indices.GetForWrite(indexName);
        var result = index.Update(id, doc ?? new JsonObject(), (JsonObject?)upsert?.DeepClone(), call.Param("refresh"));
        call.Status = result.Result == "created" ? 201 : 200;
        WriteResult(json, index, result, noop: result.Result == "noop");
    }

    private void WriteResult(Utf8JsonWriter json, EsIndex index, WriteResult result, bool noop = false, int? status = null)
    {
        json.WriteStartObject();
        json.WriteString("_index", index.Name);
        json.WriteString("_type", "_doc");
        json.WriteString("_id", result.Id);
        json.WriteNumber("_version", result.Version);
        json.WriteString("result", result.Result);
        if (result.ForcedRefresh)
            json.WriteBoolean("forced_refresh", true);
        json.WriteStartObject("_shards");
        json.WriteNumber("total", noop ? 0 : 1 + index.NumberOfReplicas);
        json.WriteNumber("successful", noop ? 0 : 1);
        json.WriteNumber("failed", 0);
        json.WriteEndObject();
        json.WriteNumber("_seq_no", result.SeqNo);
        json.WriteNumber("_primary_term", 1);
        if (status is { } s)
            json.WriteNumber("status", s);
        json.WriteEndObject();
    }

    private void GetDocument(Call call, Utf8JsonWriter json, string indexName, string id, bool sourceOnly)
    {
        var index = _indices.Get(indexName);
        var doc = index.Get(id);
        var filter = SearchRequest.SourceFromParameters(SourceFilter.All, call.Parameters);
        if (sourceOnly)
        {
            if (doc is null)
                throw new EsException(404, "resource_not_found_exception", $"Document not found [{indexName}]/[_doc]/[{id}]");
            call.Status = 200;
            if (filter.IsAll)
            {
                json.WriteRawValue(doc.Source, skipInputValidation: true);
                return;
            }
            // The filtered source alone.
            using var buffer = new MemoryStream();
            using (var inner = new Utf8JsonWriter(buffer, EsJson.Writer))
            {
                inner.WriteStartObject();
                SearchService.WriteSource(inner, doc.Source, filter);
                inner.WriteEndObject();
            }
            json.WriteRawValue(JsonNode.Parse(buffer.ToArray())!["_source"]!.ToJsonString());
            return;
        }
        call.Status = doc is null ? 404 : 200;
        WriteGet(json, index.Name, id, doc, filter);
    }

    private static void WriteGet(Utf8JsonWriter json, string index, string id, StoredDocument? doc, SourceFilter filter)
    {
        json.WriteStartObject();
        json.WriteString("_index", index);
        json.WriteString("_type", "_doc");
        json.WriteString("_id", id);
        if (doc is null)
        {
            json.WriteBoolean("found", false);
        }
        else
        {
            json.WriteNumber("_version", doc.Version);
            json.WriteNumber("_seq_no", doc.SeqNo);
            json.WriteNumber("_primary_term", 1);
            json.WriteBoolean("found", true);
            SearchService.WriteSource(json, doc.Source, filter);
        }
        json.WriteEndObject();
    }

    private void MultiGet(Call call, Utf8JsonWriter json, string? defaultIndex)
    {
        var body = call.Json() ?? throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: no documents to get;");
        var requests = new List<(string? Index, string Id, SourceFilter Filter)>();
        var filter = SearchRequest.SourceFromParameters(SourceFilter.All, call.Parameters);
        if (body.TryGetProperty("ids", out var ids))
            requests.AddRange(ids.EnumerateArray().Select(i => (defaultIndex, i.ToString(), filter)));
        if (body.TryGetProperty("docs", out var docs))
            foreach (var d in docs.EnumerateArray())
                requests.Add((d.TryGetProperty("_index", out var i) ? i.GetString() : defaultIndex, d.GetProperty("_id").ToString(),
                    d.TryGetProperty("_source", out var src) ? SearchRequest.ParseSource(src) : filter));
        json.WriteStartObject();
        json.WriteStartArray("docs");
        foreach (var (indexName, id, sourceFilter) in requests)
        {
            if (indexName is null)
                throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: index is missing for doc 0;");
            var index = _indices.Find(indexName);
            if (index is null)
            {
                json.WriteStartObject();
                json.WriteString("_index", indexName);
                json.WriteString("_type", "_doc");
                json.WriteString("_id", id);
                json.WritePropertyName("error");
                var error = EsException.IndexNotFound(indexName);
                json.WriteStartObject();
                json.WriteStartArray("root_cause");
                json.WriteStartObject();
                json.WriteString("type", error.Type);
                json.WriteString("reason", error.Message);
                json.WriteEndObject();
                json.WriteEndArray();
                json.WriteString("type", error.Type);
                json.WriteString("reason", error.Message);
                json.WriteEndObject();
                json.WriteEndObject();
                continue;
            }
            WriteGet(json, indexName, id, index.Get(id), sourceFilter);
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private void Bulk(Call call, Utf8JsonWriter json, string? defaultIndex)
    {
        var started = DateTime.UtcNow;
        var lines = SplitLines(call.Body);
        var touched = new HashSet<EsIndex>();
        var items = new MemoryStream();
        var errors = false;
        using (var writer = new Utf8JsonWriter(items, EsJson.Writer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < lines.Count; i++)
            {
                using var actionDoc = JsonDocument.Parse(lines[i]);
                var action = actionDoc.RootElement.EnumerateObject().FirstOrDefault();
                var meta = action.Value;
                var op = action.Name;
                var indexName = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("_index", out var ix) ? ix.GetString() : defaultIndex;
                var id = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("_id", out var idValue) ? idValue.ToString() : null;
                byte[]? source = null;
                if (op is "index" or "create" or "update")
                {
                    if (++i >= lines.Count)
                        throw new EsException(400, "illegal_argument_exception", "The bulk request must be terminated by a newline [\\n]");
                    source = lines[i];
                }
                else if (op != "delete")
                    throw new EsException(400, "illegal_argument_exception", $"Malformed action/metadata line [{i + 1}], expected one of [create, delete, index, update] but found [{op}]");

                writer.WriteStartObject();
                writer.WritePropertyName(op);
                try
                {
                    if (indexName is null)
                        throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: index is missing;");
                    EsIndex index;
                    WriteResult result;
                    int status;
                    switch (op)
                    {
                        case "delete":
                            index = _indices.Get(indexName);
                            result = index.Delete(id ?? throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: id is missing;"), null);
                            status = result.Result == "deleted" ? 200 : 404;
                            break;
                        case "update":
                        {
                            index = _indices.GetForWrite(indexName);
                            var node = JsonNode.Parse(source!)!.AsObject();
                            var doc = node["doc"] as JsonObject ?? new JsonObject();
                            var upsert = node["doc_as_upsert"]?.GetValue<bool>() == true ? doc : node["upsert"] as JsonObject;
                            result = index.Update(id ?? throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: id is missing;"),
                                doc, (JsonObject?)upsert?.DeepClone(), null);
                            status = result.Result == "created" ? 201 : 200;
                            break;
                        }
                        default:
                            index = _indices.GetForWrite(indexName);
                            result = index.Index(id, source!, op == "create", null);
                            status = result.Result == "created" ? 201 : 200;
                            break;
                    }
                    touched.Add(index);
                    WriteResult(writer, index, result, noop: result.Result == "noop", status: status);
                }
                catch (EsException ex)
                {
                    errors = true;
                    writer.WriteStartObject();
                    writer.WriteString("_index", indexName);
                    writer.WriteString("_type", "_doc");
                    writer.WriteString("_id", id);
                    writer.WriteNumber("status", ex.Status);
                    writer.WritePropertyName("error");
                    writer.WriteStartObject();
                    writer.WriteString("type", ex.Type);
                    writer.WriteString("reason", ex.Message);
                    foreach (var (key, value) in ex.Extra)
                        writer.WriteString(key, value);
                    if (ex.CausedBy is { } cause)
                    {
                        writer.WriteStartObject("caused_by");
                        writer.WriteString("type", cause.Type);
                        writer.WriteString("reason", cause.Message);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (call.Param("refresh") is { } refresh and not "false")
            foreach (var index in touched)
                index.Refresh();
        json.WriteStartObject();
        json.WriteNumber("took", (long)(DateTime.UtcNow - started).TotalMilliseconds);
        json.WriteBoolean("errors", errors);
        json.WritePropertyName("items");
        json.WriteRawValue(items.ToArray(), skipInputValidation: true);
        json.WriteEndObject();
    }

    private static List<byte[]> SplitLines(byte[] body)
    {
        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i <= body.Length; i++)
        {
            if (i < body.Length && body[i] != (byte)'\n')
                continue;
            var length = i - start;
            if (length > 0 && body[start + length - 1] == (byte)'\r')
                length--;
            if (length > 0 && !body.AsSpan(start, length).ToArray().All(b => b is (byte)' ' or (byte)'\t'))
                lines.Add(body.AsSpan(start, length).ToArray());
            start = i + 1;
        }
        return lines;
    }

    // ── Searches ──

    private List<EsIndex> Targets(Call call, string? expression)
    {
        var ignoreUnavailable = call.Param("ignore_unavailable") == "true";
        if (!ignoreUnavailable)
            return _indices.Resolve(expression, call.Param("allow_no_indices") != "false");
        var found = new List<EsIndex>();
        foreach (var part in (expression ?? "_all").Split(','))
        {
            try
            {
                found.AddRange(_indices.Resolve(part).Where(i => !found.Contains(i)));
            }
            catch (EsException)
            {
            }
        }
        return found;
    }

    private void Search(Call call, Utf8JsonWriter json, string? expression)
    {
        var indices = Targets(call, expression);
        var body = call.Json();
        var request = SearchRequest.Parse(body, call.Parameters);
        if (call.Param("q") is { Length: > 0 } q)
            request.Query = UriQuery(q, call.Param("df"), call.Param("default_operator"));
        if (call.Param("scroll") is not null)
            throw EsException.BadRequest("KAONOMI.SearchEngine does not support the scroll API: use search_after");
        SearchService.Search(indices, request, json);
    }

    private void Count(Call call, Utf8JsonWriter json, string? expression)
    {
        var indices = Targets(call, expression);
        var body = call.Json();
        JsonElement? query = body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("query", out var qe) ? qe : null;
        if (call.Param("q") is { Length: > 0 } q)
            query = UriQuery(q, call.Param("df"), call.Param("default_operator"));
        var count = SearchService.Count(indices, query);
        json.WriteStartObject();
        json.WriteNumber("count", count);
        SearchService.WriteShards(json, indices.Count);
        json.WriteEndObject();
    }

    /// <summary>
    /// The <c>q</c> parameter of a URI search: <c>field:value</c> (a match on that field) or free text (a match on
    /// every field, or on <c>df</c>).
    /// </summary>
    private static JsonElement UriQuery(string q, string? defaultField, string? defaultOperator)
    {
        var op = defaultOperator?.ToLowerInvariant() == "and" ? "and" : "or";
        JsonObject query;
        var colon = q.IndexOf(':');
        if (colon > 0 && !q[..colon].Contains(' '))
        {
            var value = q[(colon + 1)..].Trim('"');
            query = value == "*"
                ? new JsonObject { ["exists"] = new JsonObject { ["field"] = q[..colon] } }
                : new JsonObject { ["match"] = new JsonObject { [q[..colon]] = new JsonObject { ["query"] = value, ["operator"] = op } } };
        }
        else if (q == "*")
            query = new JsonObject { ["match_all"] = new JsonObject() };
        else
            query = new JsonObject
            {
                ["multi_match"] = new JsonObject
                {
                    ["query"] = q, ["fields"] = new JsonArray(defaultField ?? "*"), ["operator"] = op, ["lenient"] = true
                }
            };
        return JsonDocument.Parse(query.ToJsonString()).RootElement;
    }

    // ── Indices ──

    private void IndexAdmin(Call call, Utf8JsonWriter json, string name)
    {
        switch (call.Method)
        {
            case "PUT" or "POST":
            {
                var index = _indices.Create(name, call.Json());
                json.WriteStartObject();
                json.WriteBoolean("acknowledged", true);
                json.WriteBoolean("shards_acknowledged", true);
                json.WriteString("index", index.Name);
                json.WriteEndObject();
                return;
            }
            case "DELETE":
                _indices.Delete(name);
                Acknowledged(json);
                return;
            case "HEAD":
            {
                var exists = name.Contains('*') ? _indices.Resolve(name).Count > 0 : _indices.Find(name) is not null;
                call.Status = exists ? 200 : 404;
                if (!exists)
                    Error404Head(json, name);
                return;
            }
            case "GET":
            {
                var indices = _indices.Resolve(name, allowNoIndices: true, resourceType: "index_or_alias");
                json.WriteStartObject();
                foreach (var index in indices)
                {
                    json.WriteStartObject(index.Name);
                    json.WriteStartObject("aliases");
                    json.WriteEndObject();
                    json.WritePropertyName("mappings");
                    index.Mapping.ToJson().WriteTo(json);
                    json.WritePropertyName("settings");
                    WriteSettings(json, index);
                    json.WriteEndObject();
                }
                json.WriteEndObject();
                return;
            }
        }
        throw new EsException(405, "illegal_argument_exception", $"Incorrect HTTP method for uri [/{name}] and method [{call.Method}], allowed: [GET, HEAD, DELETE, PUT]");
    }

    private static void Error404Head(Utf8JsonWriter json, string name)
    {
        // HEAD has no body: something is still written so that the writer is valid.
        json.WriteStartObject();
        json.WriteString("index", name);
        json.WriteEndObject();
    }

    private static void Acknowledged(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteBoolean("acknowledged", true);
        json.WriteEndObject();
    }

    /// <summary>
    /// <c>POST /_shutdown</c>: the engine writes its indices and stops, as on Ctrl+C (Elasticsearch has no such API:
    /// the chat of KAONOMI sends it on <c>/bye</c>). The answer goes out first.
    /// </summary>
    private void Shutdown(Utf8JsonWriter json)
    {
        _log.Info("node", "stop asked by POST /_shutdown");
        ShutdownRequested?.Invoke();
        json.WriteStartObject();
        json.WriteBoolean("acknowledged", true);
        json.WriteString("node", _settings.NodeName);
        json.WriteString("message", "stopping");
        json.WriteEndObject();
    }

    private void GetMapping(Utf8JsonWriter json, string? expression)
    {
        json.WriteStartObject();
        foreach (var index in _indices.Resolve(expression, resourceType: "index_or_alias"))
        {
            json.WriteStartObject(index.Name);
            json.WritePropertyName("mappings");
            index.Mapping.ToJson().WriteTo(json);
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }

    private void PutMapping(Call call, Utf8JsonWriter json, string expression)
    {
        var body = call.Json() ?? throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: mapping source is empty;");
        foreach (var index in _indices.Resolve(expression, allowNoIndices: false))
            index.Mapping.Merge(body);
        Acknowledged(json);
    }

    private void GetSettings(Utf8JsonWriter json, string? expression)
    {
        json.WriteStartObject();
        foreach (var index in _indices.Resolve(expression, resourceType: "index_or_alias"))
        {
            json.WriteStartObject(index.Name);
            json.WritePropertyName("settings");
            WriteSettings(json, index);
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }

    private static void WriteSettings(Utf8JsonWriter json, EsIndex index)
    {
        json.WriteStartObject();
        json.WriteStartObject("index");
        foreach (var (key, value) in index.ExtraSettings)
            json.WriteString(key, value?.ToString());
        json.WriteString("number_of_shards", "1");
        json.WriteString("number_of_replicas", index.NumberOfReplicas.ToString(CultureInfo.InvariantCulture));
        json.WriteString("max_result_window", index.MaxResultWindow.ToString(CultureInfo.InvariantCulture));
        json.WriteString("creation_date", index.CreationDate.ToString(CultureInfo.InvariantCulture));
        json.WriteString("uuid", index.Uuid);
        json.WriteStartObject("version");
        json.WriteString("created", "7140299");
        json.WriteEndObject();
        json.WriteString("provided_name", index.Name);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private void Aliases(Utf8JsonWriter json, string? expression)
    {
        json.WriteStartObject();
        foreach (var index in _indices.Resolve(expression))
        {
            json.WriteStartObject(index.Name);
            json.WriteStartObject("aliases");
            json.WriteEndObject();
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }

    private void Refresh(Utf8JsonWriter json, string? expression, bool flush)
    {
        var indices = _indices.Resolve(expression);
        foreach (var index in indices)
        {
            index.Refresh();
            if (flush)
                index.Commit();
        }
        json.WriteStartObject();
        json.WriteStartObject("_shards");
        json.WriteNumber("total", indices.Sum(i => 1 + i.NumberOfReplicas));
        json.WriteNumber("successful", indices.Count);
        json.WriteNumber("failed", 0);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    /// <summary><c>_analyze</c>: the tokens of a text for the <c>standard</c> analyzer (or <c>keyword</c>, <c>whitespace</c>).</summary>
    private void Analyze(Call call, Utf8JsonWriter json, string? indexName)
    {
        var body = call.Json();
        var texts = new List<string>();
        if (body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("text", out var t))
            texts.AddRange(t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString() ?? string.Empty) : [t.GetString() ?? string.Empty]);
        else if (call.Param("text") is { } text)
            texts.Add(text);
        else
            throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: text is missing;");
        var analyzerName = body is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("analyzer", out var an) ? an.GetString() : call.Param("analyzer");
        if (body is { ValueKind: JsonValueKind.Object } f && f.TryGetProperty("field", out var field) && indexName is not null
            && _indices.Get(indexName).Mapping.Get(field.GetString()!) is { Type: EsType.Keyword })
            analyzerName = "keyword";
        using Lucene.Net.Analysis.Analyzer analyzer = analyzerName switch
        {
            null or "standard" or "default" => new EsStandardAnalyzer(),
            "keyword" => new Lucene.Net.Analysis.Core.KeywordAnalyzer(),
            "whitespace" => new Lucene.Net.Analysis.Core.WhitespaceAnalyzer(Lucene.Net.Util.LuceneVersion.LUCENE_48),
            "simple" => new Lucene.Net.Analysis.Core.SimpleAnalyzer(Lucene.Net.Util.LuceneVersion.LUCENE_48),
            _ => throw new EsException(400, "illegal_argument_exception", $"failed to find global analyzer [{analyzerName}]")
        };
        json.WriteStartObject();
        json.WriteStartArray("tokens");
        var position = -1;
        var offsetBase = 0;
        foreach (var text in texts)
        {
            using var stream = analyzer.GetTokenStream("_analyze", text);
            var term = stream.AddAttribute<Lucene.Net.Analysis.TokenAttributes.ICharTermAttribute>();
            var offset = stream.AddAttribute<Lucene.Net.Analysis.TokenAttributes.IOffsetAttribute>();
            var type = stream.AddAttribute<Lucene.Net.Analysis.TokenAttributes.ITypeAttribute>();
            var increment = stream.AddAttribute<Lucene.Net.Analysis.TokenAttributes.IPositionIncrementAttribute>();
            stream.Reset();
            while (stream.IncrementToken())
            {
                position += increment.PositionIncrement;
                json.WriteStartObject();
                json.WriteString("token", term.ToString());
                json.WriteNumber("start_offset", offsetBase + offset.StartOffset);
                json.WriteNumber("end_offset", offsetBase + offset.EndOffset);
                json.WriteString("type", type.Type);
                json.WriteNumber("position", position);
                json.WriteEndObject();
            }
            stream.End();
            offsetBase += text.Length + 1;
            position += 100; // position_increment_gap of the arrays
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    // ── Health ──

    private (string Status, int Primaries, int Unassigned) Health(IEnumerable<EsIndex> indices)
    {
        var list = indices.ToList();
        var unassigned = list.Sum(i => i.NumberOfReplicas);
        return (unassigned > 0 ? "yellow" : "green", list.Count, unassigned);
    }

    private void ClusterHealth(Utf8JsonWriter json, string? expression)
    {
        var (status, primaries, unassigned) = Health(_indices.Resolve(expression));
        json.WriteStartObject();
        json.WriteString("cluster_name", _settings.ClusterName);
        json.WriteString("status", status);
        json.WriteBoolean("timed_out", false);
        json.WriteNumber("number_of_nodes", 1);
        json.WriteNumber("number_of_data_nodes", 1);
        json.WriteNumber("active_primary_shards", primaries);
        json.WriteNumber("active_shards", primaries);
        json.WriteNumber("relocating_shards", 0);
        json.WriteNumber("initializing_shards", 0);
        json.WriteNumber("unassigned_shards", unassigned);
        json.WriteNumber("delayed_unassigned_shards", 0);
        json.WriteNumber("number_of_pending_tasks", 0);
        json.WriteNumber("number_of_in_flight_fetch", 0);
        json.WriteNumber("task_max_waiting_in_queue_millis", 0);
        json.WriteNumber("active_shards_percent_as_number", primaries + unassigned == 0 ? 100.0 : 100.0 * primaries / (primaries + unassigned));
        json.WriteEndObject();
    }

    /// <summary>The <c>_cat</c> APIs: columns of text (<c>?v</c>: with their names), or JSON (<c>?format=json</c>).</summary>
    private void Cat(Call call, string what, string? expression)
    {
        string[] headers;
        var rows = new List<string[]>();
        var now = DateTimeOffset.UtcNow;
        switch (what)
        {
            case "indices":
                headers = ["health", "status", "index", "uuid", "pri", "rep", "docs.count", "docs.deleted", "store.size", "pri.store.size"];
                foreach (var index in _indices.Resolve(expression))
                {
                    var (docs, deleted) = index.Count();
                    var size = Bytes(index.SizeInBytes(), call.Param("bytes"));
                    rows.Add([index.NumberOfReplicas > 0 ? "yellow" : "green", "open", index.Name, index.Uuid, "1",
                        index.NumberOfReplicas.ToString(CultureInfo.InvariantCulture), docs.ToString(CultureInfo.InvariantCulture),
                        deleted.ToString(CultureInfo.InvariantCulture), size, size]);
                }
                break;
            case "health":
            {
                headers = ["epoch", "timestamp", "cluster", "status", "node.total", "node.data", "shards", "pri", "relo", "init", "unassign", "pending_tasks", "max_task_wait_time", "active_shards_percent"];
                var (status, primaries, unassigned) = Health(_indices.Resolve(null));
                var percent = primaries + unassigned == 0 ? 100.0 : 100.0 * primaries / (primaries + unassigned);
                rows.Add([now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    _settings.ClusterName, status, "1", "1", primaries.ToString(CultureInfo.InvariantCulture), primaries.ToString(CultureInfo.InvariantCulture),
                    "0", "0", unassigned.ToString(CultureInfo.InvariantCulture), "0", "-", percent.ToString("0.0", CultureInfo.InvariantCulture) + "%"]);
                break;
            }
            case "count":
            {
                headers = ["epoch", "timestamp", "count"];
                var count = SearchService.Count(_indices.Resolve(expression), null);
                rows.Add([now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    count.ToString(CultureInfo.InvariantCulture)]);
                break;
            }
            default:
                throw new EsException(400, "illegal_argument_exception", $"KAONOMI.SearchEngine does not support _cat/{what}");
        }
        if (call.Param("h") is { } h)
        {
            var wanted = h.Split(',').Select(c => Array.IndexOf(headers, c.Trim())).Where(i => i >= 0).ToArray();
            headers = wanted.Select(i => headers[i]).ToArray();
            rows = rows.Select(r => wanted.Select(i => r[i]).ToArray()).ToList();
        }
        if (call.Param("format") == "json")
        {
            var array = new JsonArray();
            foreach (var row in rows)
            {
                var obj = new JsonObject();
                for (var i = 0; i < headers.Length; i++)
                    obj[headers[i]] = row[i];
                array.Add(obj);
            }
            call.Text = Encoding.UTF8.GetBytes(array.ToJsonString());
            return;
        }
        var all = call.Flag("v") ? new[] { headers }.Concat(rows).ToList() : rows;
        var widths = Enumerable.Range(0, headers.Length).Select(c => all.Count == 0 ? 0 : all.Max(r => r[c].Length)).ToArray();
        var text = new StringBuilder();
        foreach (var row in all)
            text.Append(string.Join(' ', row.Select((cell, c) => cell.PadRight(widths[c]))).TrimEnd()).Append('\n');
        call.Text = Encoding.UTF8.GetBytes(text.ToString());
        call.ContentType = "text/plain; charset=UTF-8";
    }

    private static string Bytes(long bytes, string? unit)
    {
        switch (unit)
        {
            case "b": return bytes.ToString(CultureInfo.InvariantCulture);
            case "kb": return (bytes / 1024).ToString(CultureInfo.InvariantCulture);
            case "mb": return (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
            case "gb": return (bytes / (1024L * 1024 * 1024)).ToString(CultureInfo.InvariantCulture);
        }
        string[] units = ["b", "kb", "mb", "gb", "tb"];
        double value = bytes;
        var u = 0;
        while (value >= 1024 && u < units.Length - 1)
        {
            value /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes}b" : value.ToString("0.#", CultureInfo.InvariantCulture) + units[u];
    }
}
