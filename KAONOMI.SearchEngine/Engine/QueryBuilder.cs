using System.Globalization;
using System.Text.Json;
using KAONOMI.SearchEngine.Http;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Index;
using Lucene.Net.Queries;
using Lucene.Net.Search;
using Lucene.Net.Util;
using Lucene.Net.Util.Automaton;

namespace KAONOMI.SearchEngine.Engine;

/// <summary>
/// The query DSL of Elasticsearch turned into Lucene queries, for the fields of one index: <c>match_all</c>,
/// <c>match</c> (operator, fuzziness, minimum_should_match), <c>match_phrase</c>, <c>multi_match</c>, <c>term</c>,
/// <c>terms</c>, <c>ids</c>, <c>range</c>, <c>exists</c>, <c>prefix</c>, <c>wildcard</c>, <c>regexp</c>,
/// <c>fuzzy</c>, <c>bool</c>, <c>constant_score</c>, <c>dis_max</c>, <c>nested</c> (as an object).
/// </summary>
public sealed class QueryBuilder
{
    private readonly IndexMapping _mapping;
    private readonly Analyzer _analyzer;

    /// <summary>The queries with a <c>_name</c> (for the <c>matched_queries</c> of the hits).</summary>
    public List<(string Name, Query Query)> Named { get; } = new();

    public QueryBuilder(IndexMapping mapping, Analyzer analyzer)
    {
        _mapping = mapping;
        _analyzer = analyzer;
    }

    /// <summary>The query of a search body (<c>query</c>), or every document when there is none.</summary>
    public Query Build(JsonElement? query) =>
        query is { ValueKind: JsonValueKind.Object } q ? Parse(q) : new MatchAllDocsQuery();

    public Query Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw EsException.Parsing("[_na] query malformed, must start with start_object");
        var properties = json.EnumerateObject().ToList();
        if (properties.Count == 0)
            return new MatchAllDocsQuery();
        if (properties.Count > 1)
            throw EsException.Parsing($"[{properties[0].Name}] malformed query, expected [END_OBJECT] but found [FIELD_NAME]");
        var (type, body) = (properties[0].Name, properties[0].Value);
        return type switch
        {
            "match_all" => Named_(body, new MatchAllDocsQuery { Boost = Boost(body) }),
            "match_none" => new BooleanQuery(),
            "bool" => Bool(body),
            "match" => Match(body, phrase: false),
            "match_phrase" => Match(body, phrase: true),
            "multi_match" => MultiMatch(body),
            "term" => Term(body),
            "terms" => Terms(body),
            "ids" => Ids(body),
            "range" => Range(body),
            "exists" => Exists(body),
            "prefix" => MultiTerm(body, "prefix"),
            "wildcard" => MultiTerm(body, "wildcard"),
            "regexp" => MultiTerm(body, "regexp"),
            "fuzzy" => Fuzzy(body),
            "constant_score" => ConstantScore(body),
            "dis_max" => DisMax(body),
            "nested" => body.TryGetProperty("query", out var inner) ? Parse(inner) : throw EsException.Parsing("[nested] requires 'query' field"),
            _ => throw new EsException(400, "parsing_exception", $"unknown query [{type}]", ("line", "1"), ("col", "1"))
            {
                CausedBy = new EsException(400, "named_object_not_found_exception", $"[1:1] unknown field [{type}]")
            }
        };
    }

    // ── Compound ──

    private Query Bool(JsonElement body)
    {
        var query = new BooleanQuery(disableCoord: true);
        int must = 0, should = 0, filter = 0;
        string? minimum = null;
        foreach (var p in body.EnumerateObject())
        {
            switch (p.Name)
            {
                case "must":
                    foreach (var clause in Clauses(p.Value))
                    {
                        query.Add(Parse(clause), Occur.MUST);
                        must++;
                    }
                    break;
                case "should":
                    foreach (var clause in Clauses(p.Value))
                    {
                        query.Add(Parse(clause), Occur.SHOULD);
                        should++;
                    }
                    break;
                case "must_not":
                    foreach (var clause in Clauses(p.Value))
                        query.Add(Parse(clause), Occur.MUST_NOT);
                    break;
                case "filter":
                    foreach (var clause in Clauses(p.Value))
                    {
                        query.Add(new ConstantScoreQuery(Parse(clause)) { Boost = 0f }, Occur.MUST);
                        filter++;
                    }
                    break;
                case "minimum_should_match":
                    minimum = p.Value.ToString();
                    break;
                case "boost" or "_name" or "adjust_pure_negative" or "disable_coord":
                    break;
                default:
                    throw EsException.Parsing($"[bool] query does not support [{p.Name}]");
            }
        }
        if (should > 0)
        {
            var required = minimum is not null ? MinimumShouldMatch(minimum, should) : must + filter == 0 ? 1 : 0;
            query.MinimumNumberShouldMatch = required;
        }
        if (must + should + filter == 0)
            query.Add(new MatchAllDocsQuery { Boost = 0f }, Occur.MUST); // only must_not: every other document (score 0)
        query.Boost = Boost(body);
        return Named_(body, query);
    }

    private static IEnumerable<JsonElement> Clauses(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [value];

    private Query ConstantScore(JsonElement body)
    {
        if (!body.TryGetProperty("filter", out var filter))
            throw EsException.Parsing("[constant_score] requires a 'filter' element");
        return Named_(body, new ConstantScoreQuery(Parse(filter)) { Boost = Boost(body) });
    }

    private Query DisMax(JsonElement body)
    {
        var tie = body.TryGetProperty("tie_breaker", out var t) ? (float)t.GetDouble() : 0f;
        var query = new DisjunctionMaxQuery(tie) { Boost = Boost(body) };
        if (body.TryGetProperty("queries", out var queries))
            foreach (var q in Clauses(queries))
                query.Add(Parse(q));
        return Named_(body, query);
    }

    // ── Full text ──

    private Query Match(JsonElement body, bool phrase)
    {
        var (path, p) = SingleField(body, phrase ? "match_phrase" : "match");
        var text = p.ValueKind == JsonValueKind.Object ? (p.TryGetProperty("query", out var q) ? q : throw EsException.Parsing($"No text specified for text query")) : p;
        var options = p.ValueKind == JsonValueKind.Object ? p : default;
        var query = MatchField(path, ScalarString(text), options, phrase);
        query.Boost *= Boost(options);
        return Named_(options, query);
    }

    private Query MatchField(string path, string text, JsonElement options, bool phrase)
    {
        var field = _mapping.Get(path);
        if (field is null || field.Type is EsType.Object or EsType.Other || !field.Index)
            return new BooleanQuery();
        var fuzziness = Get(options, "fuzziness");
        switch (field.Type)
        {
            case EsType.Text:
            {
                var tokens = Analyze(path, text);
                if (tokens.Count == 0)
                    return Get(options, "zero_terms_query") is { } zero && zero.GetString() == "all" ? new MatchAllDocsQuery() : new BooleanQuery();
                if (phrase)
                {
                    if (tokens.Count == 1)
                        return new TermQuery(new Term(path, tokens[0].Text));
                    var slop = Get(options, "slop") is { } s ? s.GetInt32() : 0;
                    var phraseQuery = new PhraseQuery { Slop = slop };
                    foreach (var (token, position) in tokens)
                        phraseQuery.Add(new Term(path, token), position);
                    return phraseQuery;
                }
                var clauses = tokens.Select(t => TermOrFuzzy(path, t.Text, fuzziness, options)).ToList();
                if (clauses.Count == 1)
                    return clauses[0];
                var and = Get(options, "operator") is { } op && op.GetString()?.ToLowerInvariant() == "and";
                var query = new BooleanQuery(disableCoord: true);
                foreach (var clause in clauses)
                    query.Add(clause, and ? Occur.MUST : Occur.SHOULD);
                if (!and)
                    query.MinimumNumberShouldMatch = Get(options, "minimum_should_match") is { } msm
                        ? MinimumShouldMatch(msm.ToString(), clauses.Count)
                        : 1;
                return query;
            }
            case EsType.Keyword:
                return TermOrFuzzy(path, text, fuzziness, options);
            default:
                try
                {
                    return new ConstantScoreQuery(ExactValue(field, text));
                }
                catch (EsException) when (Get(options, "lenient") is { ValueKind: JsonValueKind.True })
                {
                    return new BooleanQuery();
                }
        }
    }

    private Query TermOrFuzzy(string path, string term, JsonElement? fuzziness, JsonElement options)
    {
        if (fuzziness is null)
            return new TermQuery(new Term(path, term));
        var edits = Edits(fuzziness.Value, term);
        if (edits == 0)
            return new TermQuery(new Term(path, term));
        var prefix = Get(options, "prefix_length") is { } pl ? pl.GetInt32() : 0;
        var expansions = Get(options, "max_expansions") is { } me ? me.GetInt32() : 50;
        var transpositions = Get(options, "fuzzy_transpositions") is not { ValueKind: JsonValueKind.False };
        return new FuzzyQuery(new Term(path, term), edits, prefix, expansions, transpositions)
        {
            MultiTermRewriteMethod = new BlendedFreqRewrite(expansions)
        };
    }

    private Query MultiMatch(JsonElement body)
    {
        var text = ScalarString(body.TryGetProperty("query", out var q) ? q : throw EsException.Parsing("[multi_match] requires query value"));
        var patterns = body.TryGetProperty("fields", out var f)
            ? Clauses(f).Select(x => x.GetString()!).ToList()
            : ["*"];
        var type = body.TryGetProperty("type", out var t) ? t.GetString() : "best_fields";
        var phrase = type is "phrase" or "phrase_prefix";
        var queries = new List<Query>();
        foreach (var pattern in patterns)
        {
            var boost = 1f;
            var name = pattern;
            var caret = pattern.LastIndexOf('^');
            if (caret > 0 && float.TryParse(pattern[(caret + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            {
                boost = b;
                name = pattern[..caret];
            }
            foreach (var path in ResolveFields(name))
            {
                var query = MatchField(path, text, body, phrase);
                query.Boost *= boost;
                queries.Add(query);
            }
        }
        Query result;
        if (type is "most_fields" or "cross_fields")
        {
            var should = new BooleanQuery(disableCoord: true);
            foreach (var query in queries)
                should.Add(query, Occur.SHOULD);
            result = should;
        }
        else
        {
            var tie = body.TryGetProperty("tie_breaker", out var tb) ? (float)tb.GetDouble() : 0f;
            var dis = new DisjunctionMaxQuery(tie);
            foreach (var query in queries)
                dis.Add(query);
            result = dis;
        }
        result.Boost = Boost(body);
        return Named_(body, result);
    }

    /// <summary>The searchable fields matching a field pattern (<c>*</c>, <c>comm*</c>, <c>obj.*</c>).</summary>
    private IEnumerable<string> ResolveFields(string pattern)
    {
        if (!pattern.Contains('*'))
            return [pattern];
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return _mapping.Fields.Values
            .Where(m => m.Type is EsType.Text or EsType.Keyword && m.Index && !m.IsSubField
                        && System.Text.RegularExpressions.Regex.IsMatch(m.Path, regex))
            .Select(m => m.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    // ── Term level ──

    private Query Term(JsonElement body)
    {
        var (path, p) = SingleField(body, "term");
        var value = p.ValueKind == JsonValueKind.Object ? (p.TryGetProperty("value", out var v) ? v : throw EsException.Parsing("[term] query does not support empty value")) : p;
        var options = p.ValueKind == JsonValueKind.Object ? p : default;
        var field = _mapping.Get(path);
        if (field is null || field.Type is EsType.Object or EsType.Other || !field.Index)
            return new BooleanQuery();
        var text = ScalarString(value);
        Query query = field.Type switch
        {
            EsType.Text => new TermQuery(new Term(path, text)),
            EsType.Keyword when Get(options, "case_insensitive") is { ValueKind: JsonValueKind.True } =>
                new TermQuery(new Term(field.LowerField, text.ToLowerInvariant())),
            EsType.Keyword => new TermQuery(new Term(path, text)),
            _ => new ConstantScoreQuery(ExactValue(field, text))
        };
        query.Boost *= Boost(options);
        return Named_(options, query);
    }

    private Query Terms(JsonElement body)
    {
        var (path, values) = body.EnumerateObject().Where(p => p.Name is not ("boost" or "_name")).Select(p => (p.Name, p.Value)).FirstOrDefault();
        if (path is null)
            throw EsException.Parsing("[terms] query requires a field");
        if (values.ValueKind != JsonValueKind.Array)
            throw EsException.Parsing("[terms] query does not support the terms lookup");
        var field = _mapping.Get(path);
        Query query;
        if (field is null || field.Type is EsType.Object or EsType.Other || !field.Index)
            query = new BooleanQuery();
        else if (field.Type is EsType.Text or EsType.Keyword)
            query = new ConstantScoreQuery(new TermsFilter(values.EnumerateArray().Select(v => new Term(path, ScalarString(v))).ToList()));
        else
        {
            var any = new BooleanQuery(disableCoord: true);
            foreach (var v in values.EnumerateArray())
                any.Add(ExactValue(field, ScalarString(v)), Occur.SHOULD);
            query = new ConstantScoreQuery(any);
        }
        query.Boost = Boost(body);
        return Named_(body, query);
    }

    private Query Ids(JsonElement body)
    {
        var ids = body.TryGetProperty("values", out var values) ? values.EnumerateArray().Select(v => ScalarString(v)).ToList() : [];
        Query query = ids.Count == 0
            ? new BooleanQuery()
            : new ConstantScoreQuery(new TermsFilter(ids.Select(id => new Term(DocumentParser.IdField, id)).ToList()));
        query.Boost = Boost(body);
        return Named_(body, query);
    }

    private Query Range(JsonElement body)
    {
        var (path, p) = SingleField(body, "range");
        var field = _mapping.Get(path);
        if (field is null || field.Type is EsType.Object or EsType.Other || !field.Index)
            return new BooleanQuery();
        JsonElement? lower = null, upper = null;
        bool lowerInclusive = true, upperInclusive = true;
        string? format = null;
        TimeZoneInfo? zone = null;
        foreach (var bound in p.EnumerateObject())
        {
            switch (bound.Name)
            {
                case "gte": lower = bound.Value; lowerInclusive = true; break;
                case "gt": lower = bound.Value; lowerInclusive = false; break;
                case "lte": upper = bound.Value; upperInclusive = true; break;
                case "lt": upper = bound.Value; upperInclusive = false; break;
                case "from": lower = bound.Value; break;
                case "to": upper = bound.Value; break;
                case "include_lower": lowerInclusive = bound.Value.ValueKind == JsonValueKind.True; break;
                case "include_upper": upperInclusive = bound.Value.ValueKind == JsonValueKind.True; break;
                case "format": format = bound.Value.GetString(); break;
                case "time_zone": zone = TimeZone(bound.Value.GetString()!); break;
            }
        }
        if (lower is { ValueKind: JsonValueKind.Null })
            lower = null;
        if (upper is { ValueKind: JsonValueKind.Null })
            upper = null;
        Query query;
        switch (field.Type)
        {
            case EsType.Date:
            {
                long? Bound(JsonElement? v, bool roundUp) => v is null ? null
                    : v.Value.ValueKind == JsonValueKind.Number ? (long)v.Value.GetDouble()
                    : EsDates.ParseBound(v.Value.GetString()!, format ?? field.Format, roundUp, zone);
                var min = Bound(lower, roundUp: !lowerInclusive);
                var max = Bound(upper, roundUp: upperInclusive);
                query = NumericRangeQuery.NewInt64Range(path, min, max, lowerInclusive, upperInclusive);
                break;
            }
            case EsType.Long or EsType.Boolean:
            {
                long? min = null, max = null;
                if (lower is { } l)
                {
                    var x = ToDouble(field, l);
                    (min, lowerInclusive) = lowerInclusive ? ((long)Math.Ceiling(x), true) : ((long)Math.Floor(x), false);
                }
                if (upper is { } u)
                {
                    var x = ToDouble(field, u);
                    (max, upperInclusive) = upperInclusive ? ((long)Math.Floor(x), true) : ((long)Math.Ceiling(x), false);
                }
                query = NumericRangeQuery.NewInt64Range(path, min, max, lowerInclusive, upperInclusive);
                break;
            }
            case EsType.Double:
                query = NumericRangeQuery.NewDoubleRange(path, lower is { } dl ? ToDouble(field, dl) : null,
                    upper is { } du ? ToDouble(field, du) : null, lowerInclusive, upperInclusive);
                break;
            default:
                query = TermRangeQuery.NewStringRange(path, lower is { } sl ? ScalarString(sl) : null,
                    upper is { } su ? ScalarString(su) : null, lowerInclusive, upperInclusive);
                break;
        }
        var constant = new ConstantScoreQuery(query) { Boost = Boost(p) };
        return Named_(p, constant);
    }

    private Query Exists(JsonElement body)
    {
        var path = body.TryGetProperty("field", out var f) ? f.GetString()! : throw EsException.Parsing("[exists] must be provided with a [field]");
        var query = new ConstantScoreQuery(new TermQuery(new Term(DocumentParser.FieldNamesField, path))) { Boost = Boost(body) };
        return Named_(body, query);
    }

    private Query MultiTerm(JsonElement body, string kind)
    {
        var (path, p) = SingleField(body, kind);
        var options = p.ValueKind == JsonValueKind.Object ? p : default;
        var value = p.ValueKind == JsonValueKind.Object
            ? (Get(options, "value") ?? Get(options, kind) ?? throw EsException.Parsing($"[{kind}] requires a value"))
            : p;
        var text = ScalarString(value);
        var field = _mapping.Get(path);
        if (field is null || field.Type is EsType.Object or EsType.Other || !field.Index)
            return new BooleanQuery();
        if (field.Type is not (EsType.Text or EsType.Keyword))
            throw new EsException(400, "query_shard_exception",
                $"Can only use {kind} queries on keyword and text fields - not on [{path}] which is of type [{field.TypeName}]");
        var insensitive = Get(options, "case_insensitive") is { ValueKind: JsonValueKind.True };
        var target = field.Type == EsType.Keyword && insensitive ? field.LowerField : path;
        if (insensitive)
            text = text.ToLowerInvariant();
        var term = new Term(target, text);
        Query query = kind switch
        {
            "prefix" => new PrefixQuery(term),
            "wildcard" => new WildcardQuery(term),
            _ => new RegexpQuery(term, RegExpSyntax.ALL)
        };
        query.Boost = Boost(options);
        return Named_(options, query);
    }

    private Query Fuzzy(JsonElement body)
    {
        var (path, p) = SingleField(body, "fuzzy");
        var options = p.ValueKind == JsonValueKind.Object ? p : default;
        var value = p.ValueKind == JsonValueKind.Object
            ? (Get(options, "value") ?? throw EsException.Parsing("[fuzzy] requires a value"))
            : p;
        var field = _mapping.Get(path);
        if (field is null || field.Type is EsType.Object or EsType.Other || !field.Index)
            return new BooleanQuery();
        if (field.Type is not (EsType.Text or EsType.Keyword))
            throw new EsException(400, "query_shard_exception",
                $"Can only use fuzzy queries on keyword and text fields - not on [{path}] which is of type [{field.TypeName}]");
        var text = ScalarString(value);
        var fuzziness = Get(options, "fuzziness") ?? JsonDocument.Parse("\"AUTO\"").RootElement;
        var edits = Edits(fuzziness, text);
        var prefix = Get(options, "prefix_length") is { } pl ? pl.GetInt32() : 0;
        var expansions = Get(options, "max_expansions") is { } me ? me.GetInt32() : 50;
        var transpositions = Get(options, "transpositions") is not { ValueKind: JsonValueKind.False };
        var query = new FuzzyQuery(new Term(path, text), edits, prefix, expansions, transpositions)
        {
            MultiTermRewriteMethod = new BlendedFreqRewrite(expansions),
            Boost = Boost(options)
        };
        return Named_(options, query);
    }

    /// <summary>The query matching the exact value of a date, number or boolean field.</summary>
    private static Query ExactValue(FieldMapping field, string value)
    {
        switch (field.Type)
        {
            case EsType.Date:
            {
                var millis = EsDates.TryParse(value, field.Format ?? EsDates.DefaultFormat, out var m) ? m
                    : throw new EsException(400, "parse_exception", $"failed to parse date field [{value}] with format [{field.Format ?? EsDates.DefaultFormat}]");
                return NumericRangeQuery.NewInt64Range(field.Path, millis, millis, true, true);
            }
            case EsType.Long:
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new EsException(400, "query_shard_exception", $"failed to create query: For input string: \"{value}\"");
                if (d != Math.Floor(d))
                    return new BooleanQuery();
                var n = (long)d;
                return NumericRangeQuery.NewInt64Range(field.Path, n, n, true, true);
            }
            case EsType.Double:
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new EsException(400, "query_shard_exception", $"failed to create query: For input string: \"{value}\"");
                if (field.TypeName is "float" or "half_float")
                    d = (float)d;
                return NumericRangeQuery.NewDoubleRange(field.Path, d, d, true, true);
            }
            case EsType.Boolean:
                return value switch
                {
                    "true" => new TermQuery(new Term(field.Path, "T")),
                    "false" or "" => new TermQuery(new Term(field.Path, "F")),
                    _ => throw new EsException(400, "query_shard_exception",
                        $"failed to create query: Can't parse boolean value [{value}], expected [true] or [false]")
                };
            default:
                return new TermQuery(new Term(field.Path, value));
        }
    }

    // ── Helpers ──

    /// <summary>The tokens of <paramref name="text"/> for a text field, with their positions.</summary>
    private List<(string Text, int Position)> Analyze(string field, string text)
    {
        var tokens = new List<(string, int)>();
        using var stream = _analyzer.GetTokenStream(field, text);
        var term = stream.AddAttribute<ICharTermAttribute>();
        var increment = stream.AddAttribute<IPositionIncrementAttribute>();
        stream.Reset();
        var position = -1;
        while (stream.IncrementToken())
        {
            position += increment.PositionIncrement;
            tokens.Add((term.ToString(), position));
        }
        stream.End();
        return tokens;
    }

    /// <summary>The number of edits of a fuzziness (<c>AUTO</c>, <c>AUTO:3,6</c>, <c>0</c>, <c>1</c>, <c>2</c>) for <paramref name="term"/>.</summary>
    public static int Edits(JsonElement fuzziness, string term)
    {
        var text = fuzziness.ValueKind == JsonValueKind.String ? fuzziness.GetString()!.Trim() : fuzziness.GetRawText();
        var length = term.Length - term.Count(char.IsLowSurrogate);
        if (text.StartsWith("AUTO", StringComparison.OrdinalIgnoreCase))
        {
            int low = 3, high = 6;
            if (text.Length > 5 && text[4] == ':')
            {
                var parts = text[5..].Split(',');
                low = int.Parse(parts[0], CultureInfo.InvariantCulture);
                high = int.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            return length < low ? 0 : length < high ? 1 : 2;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return n >= 1 ? Math.Min(2, (int)n) : n > 0 ? Math.Min(2, (int)Math.Round(length * (1 - n))) : 0;
        throw EsException.Parsing($"failed to parse fuzziness [{text}]");
    }

    /// <summary>The <c>minimum_should_match</c> of <paramref name="optional"/> clauses (<c>2</c>, <c>-1</c>, <c>75%</c>, <c>-25%</c>).</summary>
    public static int MinimumShouldMatch(string spec, int optional)
    {
        spec = spec.Trim();
        int result;
        if (spec.EndsWith('%'))
        {
            var percent = double.Parse(spec[..^1], CultureInfo.InvariantCulture);
            result = percent < 0 ? optional - (int)Math.Floor(optional * -percent / 100) : (int)Math.Floor(optional * percent / 100);
        }
        else
        {
            var n = int.Parse(spec, CultureInfo.InvariantCulture);
            result = n < 0 ? optional + n : n;
        }
        return Math.Clamp(result, 0, optional);
    }

    private static (string Path, JsonElement Params) SingleField(JsonElement body, string kind)
    {
        foreach (var p in body.EnumerateObject())
            if (p.Name is not ("boost" or "_name"))
                return (p.Name, p.Value);
        throw EsException.Parsing($"[{kind}] query doesn't support multiple fields, found none");
    }

    private static JsonElement? Get(JsonElement options, string name) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) ? value : null;

    private static float Boost(JsonElement options) =>
        Get(options, "boost") is { } b ? (float)(b.ValueKind == JsonValueKind.String ? double.Parse(b.GetString()!, CultureInfo.InvariantCulture) : b.GetDouble()) : 1f;

    private Query Named_(JsonElement options, Query query)
    {
        if (Get(options, "_name") is { ValueKind: JsonValueKind.String } name)
            Named.Add((name.GetString()!, query));
        return query;
    }

    private static string ScalarString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number or JsonValueKind.Null => value.GetRawText(),
        _ => throw EsException.Parsing($"[{value.ValueKind}] is not a valid value")
    };

    private static double ToDouble(FieldMapping field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();
        if (double.TryParse(ScalarString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        throw new EsException(400, "query_shard_exception", $"failed to create query: For input string: \"{ScalarString(value)}\"");
    }

    private static TimeZoneInfo TimeZone(string id)
    {
        if (id is "Z" or "UTC")
            return TimeZoneInfo.Utc;
        if (id.Length > 0 && id[0] is '+' or '-' && TimeSpan.TryParse(id[1..], CultureInfo.InvariantCulture, out var offset))
            return TimeZoneInfo.CreateCustomTimeZone(id, id[0] == '-' ? -offset : offset, id, id);
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }
}
