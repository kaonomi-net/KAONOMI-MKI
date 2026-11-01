using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KAONOMI.SearchEngine.Http;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace KAONOMI.SearchEngine.Engine;

/// <summary>A field of the <c>sort</c> of a search.</summary>
public sealed record SortSpec(string Field, bool Descending, bool MissingLast, JsonElement? Missing, string? UnmappedType, string? Mode);

/// <summary>The <c>_source</c> of the hits: all, none, or the fields matching includes / excludes (wildcards allowed).</summary>
public sealed record SourceFilter(bool Enabled, IReadOnlyList<string> Includes, IReadOnlyList<string> Excludes)
{
    public static readonly SourceFilter All = new(true, [], []);
    public bool IsAll => Enabled && Includes.Count == 0 && Excludes.Count == 0;
}

/// <summary>A search request: the body of <c>_search</c> and the parameters of its URL.</summary>
public sealed class SearchRequest
{
    public int From { get; set; }
    public int Size { get; set; } = 10;
    public JsonElement? Query { get; set; }
    public List<SortSpec>? Sort { get; set; }
    public JsonElement? SearchAfter { get; set; }

    /// <summary>Up to how many hits are counted exactly: 10,000 by default, <see cref="int.MaxValue"/> (true), 0 (false).</summary>
    public int TrackTotalHits { get; set; } = 10000;
    public bool TrackScores { get; set; }
    public SourceFilter Source { get; set; } = SourceFilter.All;
    public bool Version { get; set; }
    public bool SeqNoPrimaryTerm { get; set; }
    public float? MinScore { get; set; }

    /// <summary>Reads a search body (<c>from</c>, <c>size</c>, <c>query</c>, <c>sort</c>, <c>search_after</c>…).</summary>
    public static SearchRequest Parse(JsonElement? body, IReadOnlyDictionary<string, string> parameters)
    {
        var request = new SearchRequest();
        if (body is { ValueKind: JsonValueKind.Object } b)
        {
            foreach (var p in b.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "from": request.From = Int(p.Value, "from"); break;
                    case "size": request.Size = Int(p.Value, "size"); break;
                    case "query": request.Query = p.Value; break;
                    case "sort": request.Sort = ParseSort(p.Value); break;
                    case "search_after": request.SearchAfter = p.Value; break;
                    case "track_total_hits":
                        request.TrackTotalHits = p.Value.ValueKind switch
                        {
                            JsonValueKind.True => int.MaxValue,
                            JsonValueKind.False => 0,
                            _ => Int(p.Value, "track_total_hits")
                        };
                        break;
                    case "track_scores": request.TrackScores = p.Value.ValueKind == JsonValueKind.True; break;
                    case "_source": request.Source = ParseSource(p.Value); break;
                    case "version": request.Version = p.Value.ValueKind == JsonValueKind.True; break;
                    case "seq_no_primary_term": request.SeqNoPrimaryTerm = p.Value.ValueKind == JsonValueKind.True; break;
                    case "min_score": request.MinScore = (float)p.Value.GetDouble(); break;
                    case "aggs" or "aggregations":
                        if (p.Value.EnumerateObject().Any())
                            throw EsException.BadRequest("KAONOMI.SearchEngine does not support the aggregations");
                        break;
                    case "timeout" or "highlight" or "explain" or "stored_fields" or "docvalue_fields" or "fields"
                        or "terminate_after" or "indices_boost" or "collapse" or "profile" or "suggest" or "stats" or "ext" or "pit"
                        or "runtime_mappings" or "script_fields" or "rescore" or "post_filter" or "slice":
                        if (p.Name == "post_filter")
                            throw EsException.BadRequest("KAONOMI.SearchEngine does not support post_filter");
                        break;
                    default:
                        throw new EsException(400, "parsing_exception", $"Unknown key for a START_OBJECT in [{p.Name}].", ("line", "1"), ("col", "1"));
                }
            }
        }
        if (parameters.TryGetValue("from", out var from))
            request.From = int.Parse(from, CultureInfo.InvariantCulture);
        if (parameters.TryGetValue("size", out var size))
            request.Size = int.Parse(size, CultureInfo.InvariantCulture);
        if (parameters.TryGetValue("track_total_hits", out var track))
            request.TrackTotalHits = track == "true" ? int.MaxValue : track == "false" ? 0 : int.Parse(track, CultureInfo.InvariantCulture);
        if (parameters.TryGetValue("sort", out var sort))
            request.Sort = sort.Split(',').Select(s =>
            {
                var colon = s.LastIndexOf(':');
                var field = colon < 0 ? s : s[..colon];
                var desc = colon >= 0 ? s[(colon + 1)..] == "desc" : field == "_score";
                return new SortSpec(field, desc, true, null, null, null);
            }).ToList();
        request.Source = SourceFromParameters(request.Source, parameters);
        if (request.From < 0)
            throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: [from] parameter cannot be negative;");
        if (request.Size < 0)
            throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: [size] parameter cannot be negative;");
        return request;
    }

    private static int Int(JsonElement value, string name) => value.ValueKind == JsonValueKind.Number
        ? value.GetInt32()
        : int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
        : throw EsException.Parsing($"[{name}] must be a number");

    /// <summary><c>"field"</c>, <c>{"field": "desc"}</c>, <c>{"field": {"order", "missing", "unmapped_type", "mode"}}</c>, or a list of them.</summary>
    public static List<SortSpec> ParseSort(JsonElement sort)
    {
        var specs = new List<SortSpec>();
        foreach (var item in sort.ValueKind == JsonValueKind.Array ? sort.EnumerateArray().ToList() : [sort])
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var name = item.GetString()!;
                specs.Add(new SortSpec(name, name == "_score", true, null, null, null));
                continue;
            }
            foreach (var p in item.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    specs.Add(new SortSpec(p.Name, p.Value.GetString() == "desc", true, null, null, null));
                    continue;
                }
                var desc = p.Name == "_score";
                var missingLast = true;
                JsonElement? missing = null;
                string? unmapped = null, mode = null;
                foreach (var option in p.Value.EnumerateObject())
                {
                    switch (option.Name)
                    {
                        case "order": desc = option.Value.GetString() == "desc"; break;
                        case "missing":
                            if (option.Value.ValueKind == JsonValueKind.String && option.Value.GetString() is "_last" or "_first")
                                missingLast = option.Value.GetString() == "_last";
                            else
                                missing = option.Value;
                            break;
                        case "unmapped_type": unmapped = option.Value.GetString(); break;
                        case "mode": mode = option.Value.GetString(); break;
                    }
                }
                specs.Add(new SortSpec(p.Name, desc, missingLast, missing, unmapped, mode));
            }
        }
        return specs;
    }

    public static SourceFilter ParseSource(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.False:
                return new SourceFilter(false, [], []);
            case JsonValueKind.True:
                return SourceFilter.All;
            case JsonValueKind.String:
                return new SourceFilter(true, [value.GetString()!], []);
            case JsonValueKind.Array:
                return new SourceFilter(true, value.EnumerateArray().Select(v => v.GetString()!).ToList(), []);
            case JsonValueKind.Object:
            {
                List<string> Read(params string[] names)
                {
                    foreach (var name in names)
                        if (value.TryGetProperty(name, out var v))
                            return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString()!).ToList() : [v.GetString()!];
                    return [];
                }
                return new SourceFilter(true, Read("includes", "include"), Read("excludes", "exclude"));
            }
            default:
                return SourceFilter.All;
        }
    }

    public static SourceFilter SourceFromParameters(SourceFilter filter, IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("_source", out var source))
            filter = source switch
            {
                "false" => new SourceFilter(false, [], []),
                "true" => SourceFilter.All,
                _ => new SourceFilter(true, source.Split(','), [])
            };
        var includes = parameters.TryGetValue("_source_includes", out var inc) || parameters.TryGetValue("_source_include", out inc) ? inc.Split(',') : null;
        var excludes = parameters.TryGetValue("_source_excludes", out var exc) || parameters.TryGetValue("_source_exclude", out exc) ? exc.Split(',') : null;
        if (includes is not null || excludes is not null)
            filter = new SourceFilter(true, includes ?? filter.Includes, excludes ?? filter.Excludes);
        return filter;
    }
}

/// <summary>Runs the searches and the counts on one or several indices (one shard each) and writes the answer.</summary>
public static class SearchService
{
    private enum SortKind { Score, Doc, Long, Double, String }

    private sealed record ResolvedSort(SortSpec Spec, SortKind Kind, SortField Field);

    private sealed record Hit(EsIndex Index, int Doc, float Score, object?[]? Sort, string Id, byte[] Source, long Version, long SeqNo, List<string>? Matched);

    private sealed record ShardResult(List<Hit> Hits, long Total, float MaxScore);

    /// <summary>The documents matching the query (<c>_count</c>).</summary>
    public static long Count(IReadOnlyList<EsIndex> indices, JsonElement? query)
    {
        long total = 0;
        foreach (var index in indices)
        {
            var builder = new QueryBuilder(index.Mapping, index.Analyzer);
            var lucene = builder.Build(query);
            total += index.Search(s =>
            {
                var collector = new TotalHitCountCollector();
                s.Search(lucene, collector);
                return collector.TotalHits;
            });
        }
        return total;
    }

    /// <summary>Runs <paramref name="request"/> on <paramref name="indices"/> and writes the answer of Elasticsearch.</summary>
    public static void Search(IReadOnlyList<EsIndex> indices, SearchRequest request, Utf8JsonWriter json)
    {
        var watch = Stopwatch.StartNew();
        var wanted = request.From + request.Size;
        foreach (var index in indices)
            if (wanted > index.MaxResultWindow)
                throw new EsException(400, "illegal_argument_exception",
                    $"Result window is too large, from + size must be less than or equal to: [{index.MaxResultWindow}] but was [{wanted}]. See the scroll api for a more efficient way to request large data sets. This limit can be set by changing the [index.max_result_window] index level setting.")
                {
                    ShardFailure = true,
                    FailedIndex = index.Name
                };
        if (request.SearchAfter is not null && request.From > 0)
            throw new EsException(400, "illegal_argument_exception", "`from` parameter must be set to 0 when `search_after` is used.");

        var sorted = request.Sort is { Count: > 0 } && !(request.Sort.Count == 1 && request.Sort[0] is { Field: "_score", Descending: true });
        var results = new List<ShardResult>();
        foreach (var index in indices)
            results.Add(SearchIndex(index, request, sorted, indices.Count == 1 ? request.From : 0));

        // The hits of the indices merged in the order of the sort.
        var hits = results.Count == 1 ? results[0].Hits : results.SelectMany(r => r.Hits).OrderBy(h => h, new HitComparer(request, sorted)).ToList();
        var page = hits.Skip(request.From).Take(request.Size).ToList();
        var total = results.Sum(r => r.Total);
        var scores = results.Select(r => r.MaxScore).Where(s => !float.IsNaN(s)).ToList();
        var maxScore = scores.Count > 0 ? scores.Max() : float.NaN;
        var withScores = !sorted || request.TrackScores || request.Sort!.Any(s => s.Field == "_score");

        json.WriteStartObject();
        json.WriteNumber("took", watch.ElapsedMilliseconds);
        json.WriteBoolean("timed_out", false);
        WriteShards(json, indices.Count);
        json.WriteStartObject("hits");
        if (request.TrackTotalHits > 0)
        {
            json.WriteStartObject("total");
            json.WriteNumber("value", Math.Min(total, request.TrackTotalHits));
            json.WriteString("relation", total > request.TrackTotalHits ? "gte" : "eq");
            json.WriteEndObject();
        }
        json.WritePropertyName("max_score");
        if (withScores && !float.IsNaN(maxScore) && page.Count > 0)
            json.WriteNumberValue(maxScore);
        else
            json.WriteNullValue();
        json.WriteStartArray("hits");
        foreach (var hit in page)
        {
            json.WriteStartObject();
            json.WriteString("_index", hit.Index.Name);
            json.WriteString("_type", "_doc");
            json.WriteString("_id", hit.Id);
            if (request.Version)
                json.WriteNumber("_version", hit.Version);
            if (request.SeqNoPrimaryTerm)
            {
                json.WriteNumber("_seq_no", hit.SeqNo);
                json.WriteNumber("_primary_term", 1);
            }
            json.WritePropertyName("_score");
            if (withScores && !float.IsNaN(hit.Score))
                json.WriteNumberValue(hit.Score);
            else
                json.WriteNullValue();
            WriteSource(json, hit.Source, request.Source);
            if (sorted && hit.Sort is not null)
            {
                json.WriteStartArray("sort");
                foreach (var value in hit.Sort)
                    WriteSortValue(json, value);
                json.WriteEndArray();
            }
            if (hit.Matched is { Count: > 0 })
            {
                json.WriteStartArray("matched_queries");
                foreach (var name in hit.Matched)
                    json.WriteStringValue(name);
                json.WriteEndArray();
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    public static void WriteShards(Utf8JsonWriter json, int count)
    {
        json.WriteStartObject("_shards");
        json.WriteNumber("total", count);
        json.WriteNumber("successful", count);
        json.WriteNumber("skipped", 0);
        json.WriteNumber("failed", 0);
        json.WriteEndObject();
    }

    private static ShardResult SearchIndex(EsIndex index, SearchRequest request, bool sorted, int skip)
    {
        var builder = new QueryBuilder(index.Mapping, index.Analyzer);
        Query query;
        List<ResolvedSort>? sorts;
        try
        {
            query = builder.Build(request.Query);
            sorts = sorted ? request.Sort!.Select(s => Resolve(index, s)).ToList() : null;
        }
        catch (EsException ex) when (ex.Type is "query_shard_exception" or "illegal_argument_exception" && !ex.ShardFailure)
        {
            throw new EsException(ex.Status, ex.Type, ex.Message, [.. ex.Extra]) { CausedBy = ex.CausedBy, ShardFailure = true, FailedIndex = index.Name };
        }
        var n = Math.Max(1, request.From + request.Size);
        var withScores = !sorted || request.TrackScores || sorts!.Any(s => s.Kind == SortKind.Score);

        return index.Search(searcher =>
        {
            TopDocs top;
            long? total = null;
            if (request.Size == 0 && request.From == 0)
            {
                var counter = new TotalHitCountCollector();
                searcher.Search(query, counter);
                return new ShardResult([], counter.TotalHits, float.NaN);
            }
            if (sorts is null)
            {
                if (request.SearchAfter is { } after)
                {
                    var values = after.EnumerateArray().ToList();
                    var score = values.Count > 0 ? (float)values[0].GetDouble() : float.MaxValue;
                    top = searcher.SearchAfter(new ScoreDoc(int.MaxValue, score), query, n);
                }
                else
                    top = searcher.Search(query, n);
            }
            else
            {
                var sort = new Sort(sorts.Select(s => s.Field).ToArray());
                FieldDoc? fieldDoc = null;
                if (request.SearchAfter is { } after)
                {
                    var values = after.EnumerateArray().ToList();
                    if (values.Count != sorts.Count)
                        throw EsException.BadRequest($"search_after has {values.Count} value(s) but sort has {sorts.Count}.");
                    var fields = new object?[sorts.Count];
                    for (var i = 0; i < sorts.Count; i++)
                        fields[i] = AfterValue(sorts[i], values[i]);
                    fieldDoc = new FieldDoc(int.MaxValue, float.NaN, fields);
                }
                if (!withScores && request.MinScore is null
                    && DateWindow.TrySearch(searcher, index, query, sorts[0].Spec, fieldDoc, n, sort, out var windowed))
                {
                    top = windowed;
                    total = request.TrackTotalHits > 0 ? DateWindow.Count(searcher, query, request.TrackTotalHits) : 0;
                }
                else
                    top = fieldDoc is null
                        ? searcher.Search(query, null, n, sort, withScores, withScores)
                        : searcher.SearchAfter(fieldDoc, query, null, n, sort, withScores, withScores);
            }

            var named = builder.Named.Count > 0
                ? JavaHashMapOrder(builder.Named).Select(q => (q.Name, Weight: searcher.CreateNormalizedWeight(q.Query))).ToList()
                : null;
            var hits = new List<Hit>();
            for (var i = 0; i < top.ScoreDocs.Length; i++)
            {
                var scoreDoc = top.ScoreDocs[i];
                if (request.MinScore is { } min && scoreDoc.Score < min)
                    continue;
                if (i < skip)
                {
                    // Before "from" (one index): the hit is not read.
                    hits.Add(new Hit(index, scoreDoc.Doc, scoreDoc.Score, (scoreDoc as FieldDoc)?.Fields, string.Empty, [], 0, 0, null));
                    continue;
                }
                var doc = searcher.Doc(scoreDoc.Doc);
                hits.Add(new Hit(index, scoreDoc.Doc, scoreDoc.Score, (scoreDoc as FieldDoc)?.Fields,
                    doc.Get(DocumentParser.IdField),
                    doc.GetBinaryValue(DocumentParser.SourceField).Bytes,
                    doc.GetField(DocumentParser.VersionField)?.GetInt64Value() ?? 1,
                    doc.GetField(DocumentParser.SeqNoField)?.GetInt64Value() ?? 0,
                    named is null ? null : Matched(searcher, named, scoreDoc.Doc)));
            }
            return new ShardResult(hits, total ?? top.TotalHits, top.MaxScore);
        });
    }

    private static List<string> Matched(IndexSearcher searcher, List<(string Name, Weight Weight)> named, int doc)
    {
        var leaves = searcher.IndexReader.Leaves;
        var leaf = leaves[ReaderUtil.SubIndex(doc, leaves)];
        var local = doc - leaf.DocBase;
        var matched = new List<string>();
        foreach (var (name, weight) in named)
        {
            var scorer = weight.GetScorer(leaf, leaf.AtomicReader.LiveDocs);
            if (scorer is not null && scorer.Advance(local) == local)
                matched.Add(name);
        }
        return matched;
    }

    /// <summary>
    /// The named queries in the order Elasticsearch lists them in <c>matched_queries</c>: the order of a Java
    /// <c>HashMap</c> copied from the names (buckets of <c>hash ^ hash &gt;&gt;&gt; 16</c>, then the order of the
    /// query); a name given twice keeps its last query.
    /// </summary>
    private static List<(string Name, Query Query)> JavaHashMapOrder(List<(string Name, Query Query)> named)
    {
        var distinct = new List<(string Name, Query Query)>();
        foreach (var (name, query) in named)
        {
            var at = distinct.FindIndex(d => d.Name == name);
            if (at >= 0)
                distinct[at] = (name, query);
            else
                distinct.Add((name, query));
        }
        var capacity = 1;
        var wanted = (int)(distinct.Count / 0.75f + 1f);
        while (capacity < wanted)
            capacity <<= 1;
        capacity = Math.Max(capacity, 1);
        static int Hash(string s)
        {
            var h = 0;
            foreach (var c in s)
                h = unchecked(31 * h + c);
            return h ^ (int)((uint)h >> 16);
        }
        return distinct.Select((d, i) => (d, Bucket: Hash(d.Name) & (capacity - 1), i))
            .OrderBy(x => x.Bucket).ThenBy(x => x.i).Select(x => x.d).ToList();
    }

    /// <summary>The Lucene sort of a field of the request, for the mapping of <paramref name="index"/>.</summary>
    private static ResolvedSort Resolve(EsIndex index, SortSpec spec)
    {
        if (spec.Field == "_score")
            return new ResolvedSort(spec, SortKind.Score, new SortField(null, SortFieldType.SCORE, !spec.Descending));
        if (spec.Field == "_doc")
            return new ResolvedSort(spec, SortKind.Doc, new SortField(null, SortFieldType.DOC, spec.Descending));
        var mapping = index.Mapping.Get(spec.Field);
        var useMax = spec.Mode is "max" || spec.Mode is null && spec.Descending;
        string luceneField;
        SortKind kind;
        if (mapping is null)
        {
            if (spec.UnmappedType is null)
                throw new EsException(400, "query_shard_exception", $"No mapping found for [{spec.Field}] in order to sort on",
                    ("index_uuid", index.Uuid), ("index", index.Name));
            luceneField = spec.Field + "\u0001unmapped";
            kind = spec.UnmappedType switch
            {
                "keyword" or "text" or "string" => SortKind.String,
                "double" or "float" or "half_float" or "scaled_float" => SortKind.Double,
                _ => SortKind.Long
            };
        }
        else
        {
            luceneField = useMax ? mapping.MaxField : mapping.MinField;
            kind = mapping.Type switch
            {
                EsType.Date or EsType.Long or EsType.Boolean => SortKind.Long,
                EsType.Double => SortKind.Double,
                EsType.Keyword => SortKind.String,
                EsType.Text => throw new EsException(400, "illegal_argument_exception",
                    $"Text fields are not optimised for operations that require per-document field data like aggregations and sorting, so these operations are disabled by default. Please use a keyword field instead. Alternatively, set fielddata=true on [{spec.Field}] in order to load field data by uninverting the inverted index. Note that this can use significant memory."),
                _ => throw new EsException(400, "illegal_argument_exception", $"Can't sort on field [{spec.Field}] of type [{mapping.TypeName}]")
            };
        }
        // The missing values come last (or first) whatever the order, as in Elasticsearch.
        var lastInLucene = spec.MissingLast != spec.Descending;
        SortField field;
        switch (kind)
        {
            case SortKind.String:
                field = new SortField(luceneField, SortFieldType.STRING, spec.Descending);
                if (spec.Missing is { } custom)
                    throw EsException.BadRequest($"a custom missing value is not supported on [{spec.Field}]");
                field.SetMissingValue(lastInLucene ? SortField.STRING_LAST : SortField.STRING_FIRST);
                break;
            case SortKind.Double:
                field = new SortField(luceneField, SortFieldType.DOUBLE, spec.Descending);
                field.SetMissingValue(LuceneValues.Double(spec.Missing is { } md ? Number(md) : lastInLucene ? double.PositiveInfinity : double.NegativeInfinity));
                break;
            default:
                field = new SortField(luceneField, SortFieldType.INT64, spec.Descending);
                long missing = spec.Missing is { } ml
                    ? ml.ValueKind == JsonValueKind.Number ? (long)ml.GetDouble() : EsDates.Parse(ml.GetString()!, mapping?.Format)
                    : lastInLucene ? long.MaxValue : long.MinValue;
                field.SetMissingValue(LuceneValues.Long(missing));
                break;
        }
        return new ResolvedSort(spec, kind, field);
    }

    private static double Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.Parse(value.GetString()!, CultureInfo.InvariantCulture);

    /// <summary>A value of <c>search_after</c> as Lucene compares it.</summary>
    private static object? AfterValue(ResolvedSort sort, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        return sort.Kind switch
        {
            SortKind.Score => LuceneValues.Single((float)Number(value)),
            SortKind.Doc => LuceneValues.Int(value.GetInt32()),
            SortKind.Double => LuceneValues.Double(Number(value)),
            SortKind.String => new BytesRef(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()),
            _ => LuceneValues.Long(value.ValueKind == JsonValueKind.Number
                ? value.TryGetInt64(out var l) ? l : (long)value.GetDouble()
                : long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p) ? p
                : EsDates.Parse(value.GetString()!, null))
        };
    }

    private static void WriteSortValue(Utf8JsonWriter json, object? value)
    {
        switch (LuceneValues.ToPlain(value))
        {
            case null:
                json.WriteNullValue();
                break;
            case long l:
                json.WriteNumberValue(l);
                break;
            case int i:
                json.WriteNumberValue(i);
                break;
            case double d:
                if (double.IsInfinity(d))
                    json.WriteStringValue(d > 0 ? "Infinity" : "-Infinity");
                else
                    json.WriteNumberValue(d);
                break;
            case float f:
                json.WriteNumberValue(f);
                break;
            case string s:
                json.WriteStringValue(s);
                break;
            default:
                json.WriteStringValue(value!.ToString());
                break;
        }
    }

    /// <summary>The order of the hits of several indices: by score, or by the values of the sort.</summary>
    private sealed class HitComparer(SearchRequest request, bool sorted) : IComparer<Hit>
    {
        public int Compare(Hit? a, Hit? b)
        {
            if (!sorted)
            {
                var byScore = b!.Score.CompareTo(a!.Score);
                return byScore != 0 ? byScore : a.Doc.CompareTo(b.Doc);
            }
            for (var i = 0; i < request.Sort!.Count; i++)
            {
                var spec = request.Sort[i];
                var x = LuceneValues.ToPlain(a!.Sort?[i]);
                var y = LuceneValues.ToPlain(b!.Sort?[i]);
                int c;
                if (x is null || y is null)
                    c = x is null && y is null ? 0 : (x is null) == spec.MissingLast ? 1 : -1;
                else
                {
                    c = x switch
                    {
                        string s => string.CompareOrdinal(s, (string)y),
                        _ => Convert.ToDouble(x, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(y, CultureInfo.InvariantCulture))
                    };
                    if (spec.Descending)
                        c = -c;
                }
                if (c != 0)
                    return c;
            }
            return 0;
        }
    }

    /// <summary>Writes <c>_source</c> as it was indexed, or filtered.</summary>
    public static void WriteSource(Utf8JsonWriter json, byte[] source, SourceFilter filter)
    {
        if (!filter.Enabled)
            return;
        json.WritePropertyName("_source");
        if (filter.IsAll)
        {
            json.WriteRawValue(source, skipInputValidation: true);
            return;
        }
        var node = JsonNode.Parse(source)!.AsObject();
        Filter(node, string.Empty, filter);
        node.WriteTo(json);
    }

    private static void Filter(JsonObject obj, string prefix, SourceFilter filter)
    {
        foreach (var (name, value) in obj.ToList())
        {
            var path = prefix + name;
            if (filter.Excludes.Any(e => PathMatches(e, path)))
            {
                obj.Remove(name);
                continue;
            }
            if (value is JsonObject child)
            {
                Filter(child, path + ".", filter);
                if (child.Count == 0 && !Included(filter, path))
                    obj.Remove(name);
            }
            else if (!Included(filter, path))
                obj.Remove(name);
        }
    }

    private static bool Included(SourceFilter filter, string path)
    {
        if (filter.Includes.Count == 0)
            return true;
        for (var p = path; ; p = p[..p.LastIndexOf('.')])
        {
            if (filter.Includes.Any(i => PathMatches(i, p)))
                return true;
            if (!p.Contains('.'))
                return false;
        }
    }

    private static bool PathMatches(string pattern, string path) =>
        pattern.Contains('*')
            ? System.Text.RegularExpressions.Regex.IsMatch(path, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$")
            : pattern == path;
}
