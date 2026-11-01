using MongoDB.Bson;

namespace KAONOMI.MongoEngine.Engine;

/// <summary>
/// The aggregation pipelines: <c>$match</c>, <c>$sort</c>, <c>$skip</c>, <c>$limit</c>, <c>$project</c>,
/// <c>$addFields</c> / <c>$set</c>, <c>$unset</c>, <c>$replaceRoot</c> / <c>$replaceWith</c>, <c>$unwind</c>,
/// <c>$group</c>, <c>$count</c>, <c>$sortByCount</c>, <c>$lookup</c> (equality), with the common expressions.
/// </summary>
public static class Aggregation
{
    /// <summary>Runs <paramref name="pipeline"/> on <paramref name="input"/>; <paramref name="collection"/> gives the documents of another collection ($lookup).</summary>
    public static IEnumerable<BsonDocument> Run(IEnumerable<BsonDocument> input, BsonArray pipeline, Func<string, IEnumerable<BsonDocument>> collection)
    {
        var current = input;
        foreach (var stageValue in pipeline)
        {
            if (stageValue is not BsonDocument { ElementCount: 1 } stage)
                throw new MongoError(40323, "Location40323", "A pipeline stage specification object must contain exactly one field.");
            var (name, spec) = (stage.GetElement(0).Name, stage.GetElement(0).Value);
            var source = current;
            current = name switch
            {
                "$match" => source.Where(d => Filter.Matches(d, spec.AsBsonDocument)),
                "$sort" => source.OrderBy(d => (BsonValue)d, Sorting.Comparer(spec)).ToList(),
                "$skip" => source.Skip(spec.ToInt32()),
                "$limit" => source.Take(spec.ToInt32()),
                "$project" => source.Select(d => ProjectStage(d, spec.AsBsonDocument)),
                "$addFields" or "$set" => source.Select(d => AddFields(d, spec.AsBsonDocument)),
                "$unset" => source.Select(d => Unset(d, spec)),
                "$replaceRoot" => source.Select(d => NewRoot(d, spec.AsBsonDocument["newRoot"])),
                "$replaceWith" => source.Select(d => NewRoot(d, spec)),
                "$unwind" => source.SelectMany(d => Unwind(d, spec)),
                "$group" => Group(source, spec.AsBsonDocument),
                "$count" => [new BsonDocument(spec.AsString, source.Count())],
                "$sortByCount" => Group(source, new BsonDocument { { "_id", spec }, { "count", new BsonDocument("$sum", 1) } })
                    .OrderByDescending(d => d["count"].ToInt64()).ToList(),
                "$lookup" => Lookup(source, spec.AsBsonDocument, collection),
                _ => throw MongoError.NotSupported($"the stage {name} is not supported by KAONOMI.MongoEngine")
            };
        }
        return current;
    }

    private static BsonDocument NewRoot(BsonDocument document, BsonValue expression) =>
        Evaluate(document, expression) as BsonDocument
        ?? throw new MongoError(40228, "Location40228", "'newRoot' expression must evaluate to an object");

    private static BsonDocument ProjectStage(BsonDocument document, BsonDocument spec)
    {
        // Only 0/1/true/false: the projection of find; any expression: computed fields.
        if (spec.All(e => e.Value.BsonType is BsonType.Boolean or BsonType.Int32 or BsonType.Int64 or BsonType.Double))
            return Sorting.Project(document, spec);
        var result = new BsonDocument();
        var keepId = !spec.TryGetValue("_id", out var idSpec) || idSpec is BsonBoolean { Value: true } || idSpec.IsNumeric && idSpec.ToDouble() != 0;
        if (keepId && document.TryGetValue("_id", out var id))
            result["_id"] = id;
        foreach (var field in spec)
        {
            if (field.Name == "_id" && (field.Value.IsNumeric || field.Value.IsBoolean))
                continue;
            if (field.Value.IsNumeric || field.Value.IsBoolean)
            {
                if (Paths.Get(document, field.Name) is { } value)
                    Paths.Set(result, field.Name, value);
                continue;
            }
            Paths.Set(result, field.Name, Evaluate(document, field.Value));
        }
        return result;
    }

    private static BsonDocument AddFields(BsonDocument document, BsonDocument spec)
    {
        var result = document.DeepClone().AsBsonDocument;
        foreach (var field in spec)
            Paths.Set(result, field.Name, Evaluate(document, field.Value));
        return result;
    }

    private static BsonDocument Unset(BsonDocument document, BsonValue spec)
    {
        var result = document.DeepClone().AsBsonDocument;
        foreach (var path in spec is BsonArray list ? list.Select(v => v.AsString) : [spec.AsString])
            Paths.Unset(result, path);
        return result;
    }

    private static IEnumerable<BsonDocument> Unwind(BsonDocument document, BsonValue spec)
    {
        var path = spec is BsonDocument d ? d["path"].AsString : spec.AsString;
        var keepEmpty = spec is BsonDocument o && o.TryGetValue("preserveNullAndEmptyArrays", out var p) && p.ToBoolean();
        var indexField = spec is BsonDocument i && i.TryGetValue("includeArrayIndex", out var f) ? f.AsString : null;
        var field = path.TrimStart('$');
        var value = Paths.Get(document, field);
        if (value is BsonArray { Count: > 0 } array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var copy = document.DeepClone().AsBsonDocument;
                Paths.Set(copy, field, array[index]);
                if (indexField is not null)
                    copy[indexField] = (long)index;
                yield return copy;
            }
        }
        else if (value is not null && value is not BsonArray && !value.IsBsonNull)
            yield return document;
        else if (keepEmpty)
            yield return document;
    }

    private static List<BsonDocument> Group(IEnumerable<BsonDocument> source, BsonDocument spec)
    {
        var groups = new Dictionary<string, (BsonValue Id, List<BsonDocument> Docs)>();
        var order = new List<string>();
        foreach (var document in source)
        {
            var id = Evaluate(document, spec["_id"]);
            var key = BsonOrder.Key(id);
            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = group = (id, new List<BsonDocument>());
                order.Add(key);
            }
            group.Docs.Add(document);
        }
        var results = new List<BsonDocument>();
        foreach (var key in order)
        {
            var (id, docs) = groups[key];
            var result = new BsonDocument("_id", id);
            foreach (var field in spec.Where(e => e.Name != "_id"))
            {
                var accumulator = field.Value.AsBsonDocument.GetElement(0);
                var values = docs.Select(d => Evaluate(d, accumulator.Value)).ToList();
                result[field.Name] = accumulator.Name switch
                {
                    "$sum" => Sum(values.Where(v => v.IsNumeric)),
                    "$avg" => values.Any(v => v.IsNumeric) ? new BsonDouble(values.Where(v => v.IsNumeric).Average(v => v.ToDouble())) : BsonNull.Value,
                    "$min" => values.Where(v => !v.IsBsonNull).DefaultIfEmpty(BsonNull.Value).Min(BsonOrder.Instance)!,
                    "$max" => values.Where(v => !v.IsBsonNull).DefaultIfEmpty(BsonNull.Value).Max(BsonOrder.Instance)!,
                    "$first" => values.FirstOrDefault() ?? BsonNull.Value,
                    "$last" => values.LastOrDefault() ?? BsonNull.Value,
                    "$push" => new BsonArray(values),
                    "$addToSet" => new BsonArray(values.Distinct(BsonOrder.Instance)),
                    "$count" => new BsonInt32(values.Count),
                    _ => throw MongoError.NotSupported($"the accumulator {accumulator.Name} is not supported by KAONOMI.MongoEngine")
                };
            }
            results.Add(result);
        }
        return results;
    }

    private static BsonValue Sum(IEnumerable<BsonValue> values)
    {
        var list = values.ToList();
        if (list.All(v => v.BsonType == BsonType.Int32) && list.Sum(v => (long)v.AsInt32) is var s and >= int.MinValue and <= int.MaxValue)
            return new BsonInt32((int)s);
        if (list.All(v => v.BsonType is BsonType.Int32 or BsonType.Int64))
            return new BsonInt64(list.Sum(v => v.ToInt64()));
        return new BsonDouble(list.Sum(v => v.ToDouble()));
    }

    private static IEnumerable<BsonDocument> Lookup(IEnumerable<BsonDocument> source, BsonDocument spec, Func<string, IEnumerable<BsonDocument>> collection)
    {
        if (spec.Contains("pipeline"))
            throw MongoError.NotSupported("$lookup with a pipeline is not supported by KAONOMI.MongoEngine");
        var from = collection(spec["from"].AsString).ToList();
        var local = spec["localField"].AsString;
        var foreign = spec["foreignField"].AsString;
        var target = spec["as"].AsString;
        foreach (var document in source)
        {
            var values = Filter.Lookup(document, local).SelectMany(v => v is BsonArray a ? a.AsEnumerable() : [v]).DefaultIfEmpty(BsonNull.Value).ToList();
            var matches = from.Where(f => values.Any(v => Filter.Matches(f, new BsonDocument(foreign, v)))).Select(f => (BsonValue)f);
            var copy = document.DeepClone().AsBsonDocument;
            Paths.Set(copy, target, new BsonArray(matches));
            yield return copy;
        }
    }

    // ── Expressions ──

    /// <summary>The value of an aggregation expression for <paramref name="document"/>.</summary>
    public static BsonValue Evaluate(BsonDocument document, BsonValue expression)
    {
        switch (expression)
        {
            case BsonString { Value: "$$ROOT" or "$$CURRENT" }:
                return document;
            case BsonString s when s.Value.StartsWith("$$", StringComparison.Ordinal):
                throw MongoError.NotSupported($"the variable {s.Value} is not supported by KAONOMI.MongoEngine");
            case BsonString s when s.Value.StartsWith('$'):
            {
                var values = Filter.Lookup(document, s.Value[1..]);
                return values.Count switch
                {
                    0 => BsonNull.Value,
                    1 => values[0],
                    _ => new BsonArray(values)
                };
            }
            case BsonArray array:
                return new BsonArray(array.Select(v => Evaluate(document, v)));
            case BsonDocument d when d.ElementCount == 1 && d.GetElement(0).Name.StartsWith('$'):
                return Operator(document, d.GetElement(0).Name, d.GetElement(0).Value);
            case BsonDocument d:
            {
                var result = new BsonDocument();
                foreach (var element in d)
                    result[element.Name] = Evaluate(document, element.Value);
                return result;
            }
            default:
                return expression;
        }
    }

    private static BsonValue Operator(BsonDocument document, string op, BsonValue argument)
    {
        if (op == "$literal")
            return argument;
        var args = argument is BsonArray list ? list.Select(a => Evaluate(document, a)).ToList() : [Evaluate(document, argument)];
        BsonValue Arg(int i) => i < args.Count ? args[i] : BsonNull.Value;
        int Cmp() => BsonOrder.Instance.Compare(Arg(0), Arg(1));
        return op switch
        {
            "$eq" => Cmp() == 0,
            "$ne" => Cmp() != 0,
            "$gt" => Cmp() > 0,
            "$gte" => Cmp() >= 0,
            "$lt" => Cmp() < 0,
            "$lte" => Cmp() <= 0,
            "$cmp" => Cmp(),
            "$and" => args.All(Truthy),
            "$or" => args.Any(Truthy),
            "$not" => !Truthy(Arg(0)),
            "$ifNull" => args.FirstOrDefault(a => !a.IsBsonNull) ?? BsonNull.Value,
            "$cond" => argument is BsonDocument c
                ? Truthy(Evaluate(document, c["if"])) ? Evaluate(document, c["then"]) : Evaluate(document, c["else"])
                : Truthy(Arg(0)) ? Arg(1) : Arg(2),
            "$concat" => args.Any(a => a.IsBsonNull) ? BsonNull.Value : string.Concat(args.Select(a => a.AsString)),
            "$toLower" => Arg(0).IsBsonNull ? "" : Arg(0).ToString()!.ToLowerInvariant(),
            "$toUpper" => Arg(0).IsBsonNull ? "" : Arg(0).ToString()!.ToUpperInvariant(),
            "$toString" => Arg(0).IsBsonNull ? BsonNull.Value : Arg(0).BsonType == BsonType.String ? Arg(0) : Arg(0).ToString(),
            "$size" => Arg(0) is BsonArray a ? a.Count : throw new MongoError(17124, "Location17124", "The argument to $size must be an array"),
            "$arrayElemAt" => Arg(0) is BsonArray a2 && Arg(1).ToInt32() is var i && (i < 0 ? a2.Count + i : i) is var k && k >= 0 && k < a2.Count ? a2[k] : BsonNull.Value,
            "$in" => Arg(1) is BsonArray a3 && a3.Any(v => BsonOrder.Instance.Equals(v, Arg(0))),
            "$add" => args.Aggregate((BsonValue)0, (s, v) => Number(s.ToDouble() + v.ToDouble(), s, v)),
            "$subtract" => Number(Arg(0).ToDouble() - Arg(1).ToDouble(), Arg(0), Arg(1)),
            "$multiply" => args.Aggregate((BsonValue)1, (s, v) => Number(s.ToDouble() * v.ToDouble(), s, v)),
            "$divide" => new BsonDouble(Arg(0).ToDouble() / Arg(1).ToDouble()),
            "$type" => Arg(0).BsonType.ToString().ToLowerInvariant(),
            _ => throw MongoError.NotSupported($"the expression {op} is not supported by KAONOMI.MongoEngine")
        };
    }

    private static BsonValue Number(double value, BsonValue a, BsonValue b) =>
        a.BsonType is BsonType.Int32 or BsonType.Int64 && b.BsonType is BsonType.Int32 or BsonType.Int64 ? new BsonInt64((long)value) : new BsonDouble(value);

    private static bool Truthy(BsonValue value) => value.BsonType switch
    {
        BsonType.Boolean => value.AsBoolean,
        BsonType.Null or BsonType.Undefined => false,
        BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 => value.ToDouble() != 0,
        _ => true
    };
}
