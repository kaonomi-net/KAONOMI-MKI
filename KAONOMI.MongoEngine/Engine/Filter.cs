using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace KAONOMI.MongoEngine.Engine;

/// <summary>
/// The query filters of MongoDB: equality (an array field matches when one of its elements is equal), the dotted
/// paths through objects and arrays, <c>$eq</c>, <c>$ne</c>, <c>$gt</c>, <c>$gte</c>, <c>$lt</c>, <c>$lte</c>,
/// <c>$in</c>, <c>$nin</c>, <c>$exists</c>, <c>$type</c>, <c>$regex</c> (<c>$options</c>), <c>$mod</c>,
/// <c>$size</c>, <c>$all</c>, <c>$elemMatch</c>, <c>$not</c>, <c>$and</c>, <c>$or</c>, <c>$nor</c>.
/// </summary>
public static class Filter
{
    public static bool Matches(BsonDocument document, BsonDocument? filter)
    {
        if (filter is null)
            return true;
        foreach (var element in filter)
        {
            if (!MatchesElement(document, element.Name, element.Value))
                return false;
        }
        return true;
    }

    private static bool MatchesElement(BsonDocument document, string name, BsonValue value)
    {
        switch (name)
        {
            case "$and":
                return Clauses(value, name).All(c => Matches(document, c));
            case "$or":
                return Clauses(value, name).Any(c => Matches(document, c));
            case "$nor":
                return !Clauses(value, name).Any(c => Matches(document, c));
            case "$comment":
                return true;
            case "$expr" or "$where" or "$text" or "$jsonSchema":
                throw MongoError.NotSupported($"{name} is not supported by KAONOMI.MongoEngine");
        }
        if (name.StartsWith('$'))
            throw MongoError.BadValue($"unknown top level operator: {name}");
        var candidates = Lookup(document, name);
        if (IsOperatorDocument(value))
            return value.AsBsonDocument.All(op => Operator(candidates, op.Name, op.Value, value.AsBsonDocument));
        if (value.BsonType == BsonType.RegularExpression)
            return AnyLeaf(candidates, c => RegexMatches(c, value.AsBsonRegularExpression));
        return EqualsAny(candidates, value);
    }

    private static IEnumerable<BsonDocument> Clauses(BsonValue value, string name)
    {
        if (value is not BsonArray { Count: > 0 } array || array.Any(v => v.BsonType != BsonType.Document))
            throw MongoError.BadValue($"{name} argument must be a non-empty array of objects");
        return array.Select(v => v.AsBsonDocument);
    }

    /// <summary>A document whose first field is an operator (<c>{$gt: 1}</c>), as opposed to a value to be equal to.</summary>
    public static bool IsOperatorDocument(BsonValue value) =>
        value is BsonDocument { ElementCount: > 0 } d && d.GetElement(0).Name.StartsWith('$');

    // ── Paths ──

    /// <summary>The values at a dotted path, through the arrays (empty: the path does not exist).</summary>
    public static List<BsonValue> Lookup(BsonValue root, string path)
    {
        var found = new List<BsonValue>();
        Walk(root, path.Split('.'), 0, found);
        return found;
    }

    private static void Walk(BsonValue current, string[] parts, int i, List<BsonValue> found)
    {
        if (i == parts.Length)
        {
            found.Add(current);
            return;
        }
        switch (current)
        {
            case BsonDocument document:
                if (document.TryGetValue(parts[i], out var child))
                    Walk(child, parts, i + 1, found);
                break;
            case BsonArray array:
                if (int.TryParse(parts[i], out var index) && index >= 0)
                {
                    if (index < array.Count)
                        Walk(array[index], parts, i + 1, found);
                }
                foreach (var item in array)
                    if (item is BsonDocument)
                        Walk(item, parts, i, found);
                break;
        }
    }

    // ── Operators ──

    private static bool Operator(List<BsonValue> candidates, string op, BsonValue argument, BsonDocument all)
    {
        switch (op)
        {
            case "$eq":
                return argument.BsonType == BsonType.RegularExpression
                    ? AnyLeaf(candidates, c => BsonOrder.Instance.Equals(c, argument))
                    : EqualsAny(candidates, argument);
            case "$ne":
                return !EqualsAny(candidates, argument);
            case "$gt":
                return AnyLeaf(candidates, c => Comparable(c, argument) && BsonOrder.Instance.Compare(c, argument) > 0);
            case "$gte":
                return AnyLeaf(candidates, c => Comparable(c, argument) && BsonOrder.Instance.Compare(c, argument) >= 0);
            case "$lt":
                return AnyLeaf(candidates, c => Comparable(c, argument) && BsonOrder.Instance.Compare(c, argument) < 0);
            case "$lte":
                return AnyLeaf(candidates, c => Comparable(c, argument) && BsonOrder.Instance.Compare(c, argument) <= 0);
            case "$in":
                return InList(candidates, ArrayArgument(op, argument));
            case "$nin":
                return !InList(candidates, ArrayArgument(op, argument));
            case "$exists":
                return (candidates.Count > 0) == argument.ToBoolean();
            case "$type":
            {
                var types = argument is BsonArray list ? list.ToList() : [argument];
                return AnyLeaf(candidates, c => types.Any(t => TypeMatches(c, t)), arraysToo: true);
            }
            case "$regex":
            {
                var options = all.TryGetValue("$options", out var o) ? o.AsString : string.Empty;
                var regex = argument.BsonType == BsonType.RegularExpression
                    ? new BsonRegularExpression(argument.AsBsonRegularExpression.Pattern, options.Length > 0 ? options : argument.AsBsonRegularExpression.Options)
                    : new BsonRegularExpression(argument.AsString, options);
                return AnyLeaf(candidates, c => RegexMatches(c, regex));
            }
            case "$options":
                return true;
            case "$mod":
            {
                var args = ArrayArgument(op, argument);
                if (args.Count != 2)
                    throw MongoError.BadValue("malformed mod, needs to be an array of two numbers");
                var divisor = args[0].ToInt64();
                if (divisor == 0)
                    throw MongoError.BadValue("divisor cannot be 0");
                var remainder = args[1].ToInt64();
                return AnyLeaf(candidates, c => c.IsNumeric && (long)c.ToDouble() % divisor == remainder);
            }
            case "$size":
                return candidates.Any(c => c is BsonArray a && a.Count == argument.ToInt32());
            case "$all":
            {
                var values = ArrayArgument(op, argument);
                return values.Count > 0 && values.All(v => v is BsonDocument d && d.Contains("$elemMatch")
                    ? Operator(candidates, "$elemMatch", d["$elemMatch"], d)
                    : EqualsAny(candidates, v));
            }
            case "$elemMatch":
            {
                if (argument is not BsonDocument spec)
                    throw MongoError.BadValue("$elemMatch needs an Object");
                return candidates.Any(c => c is BsonArray array && array.Any(item => IsOperatorDocument(spec)
                    ? spec.All(o => Operator([item], o.Name, o.Value, spec))
                    : item is BsonDocument d && Matches(d, spec)));
            }
            case "$not":
                return argument switch
                {
                    BsonRegularExpression regex => !AnyLeaf(candidates, c => RegexMatches(c, regex)),
                    BsonDocument d when IsOperatorDocument(d) => !d.All(o => Operator(candidates, o.Name, o.Value, d)),
                    _ => throw MongoError.BadValue("$not needs a regex or a document")
                };
            case "$comment":
                return true;
            default:
                throw MongoError.BadValue($"unknown operator: {op}");
        }
    }

    private static BsonArray ArrayArgument(string op, BsonValue argument) =>
        argument as BsonArray ?? throw MongoError.BadValue($"{op} needs an array");

    /// <summary>
    /// Equality as a filter means it: a value equal to <paramref name="value"/>, or an array holding it; null also
    /// matches a missing field.
    /// </summary>
    private static bool EqualsAny(List<BsonValue> candidates, BsonValue value)
    {
        if (value.IsBsonNull && candidates.Count == 0)
            return true;
        foreach (var candidate in candidates)
        {
            if (BsonOrder.Instance.Equals(candidate, value))
                return true;
            if (candidate is BsonArray array && array.Any(item => BsonOrder.Instance.Equals(item, value)))
                return true;
        }
        return false;
    }

    private static bool InList(List<BsonValue> candidates, BsonArray values) =>
        values.Any(v => v.BsonType == BsonType.RegularExpression
            ? AnyLeaf(candidates, c => RegexMatches(c, v.AsBsonRegularExpression))
            : EqualsAny(candidates, v));

    /// <summary>A candidate, or one of the elements of a candidate array, satisfies <paramref name="test"/>.</summary>
    private static bool AnyLeaf(List<BsonValue> candidates, Func<BsonValue, bool> test, bool arraysToo = false)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is BsonArray array)
            {
                if (arraysToo && test(candidate))
                    return true;
                if (array.Any(test))
                    return true;
            }
            else if (test(candidate))
                return true;
        }
        return false;
    }

    /// <summary>The comparisons only compare values of the same kind (a number with a number…), as MongoDB.</summary>
    private static bool Comparable(BsonValue a, BsonValue b) =>
        BsonOrder.Rank(a) == BsonOrder.Rank(b) || b.BsonType is BsonType.MinKey or BsonType.MaxKey;

    private static bool RegexMatches(BsonValue candidate, BsonRegularExpression regex)
    {
        if (candidate.BsonType == BsonType.RegularExpression)
            return BsonOrder.Instance.Equals(candidate, regex);
        if (candidate.BsonType is not (BsonType.String or BsonType.Symbol))
            return false;
        var options = RegexOptions.CultureInvariant;
        foreach (var c in regex.Options)
            options |= c switch
            {
                'i' => RegexOptions.IgnoreCase,
                'm' => RegexOptions.Multiline,
                's' => RegexOptions.Singleline,
                'x' => RegexOptions.IgnorePatternWhitespace,
                _ => RegexOptions.None
            };
        return Regex.IsMatch(candidate.ToString()!, regex.Pattern, options, TimeSpan.FromSeconds(5));
    }

    private static bool TypeMatches(BsonValue value, BsonValue type)
    {
        if (type.IsNumeric)
            return (int)value.BsonType == type.ToInt32();
        return type.AsString switch
        {
            "double" => value.BsonType == BsonType.Double,
            "string" => value.BsonType == BsonType.String,
            "object" => value.BsonType == BsonType.Document,
            "array" => value.BsonType == BsonType.Array,
            "binData" => value.BsonType == BsonType.Binary,
            "objectId" => value.BsonType == BsonType.ObjectId,
            "bool" => value.BsonType == BsonType.Boolean,
            "date" => value.BsonType == BsonType.DateTime,
            "null" => value.BsonType == BsonType.Null,
            "regex" => value.BsonType == BsonType.RegularExpression,
            "int" => value.BsonType == BsonType.Int32,
            "timestamp" => value.BsonType == BsonType.Timestamp,
            "long" => value.BsonType == BsonType.Int64,
            "decimal" => value.BsonType == BsonType.Decimal128,
            "number" => value.IsNumeric,
            "minKey" => value.BsonType == BsonType.MinKey,
            "maxKey" => value.BsonType == BsonType.MaxKey,
            var other => throw MongoError.BadValue($"Unknown type name alias: {other}")
        };
    }

    // ── Upserts ──

    /// <summary>
    /// The fields an upsert takes from its filter: the equalities (<c>{a: 1}</c>, <c>{a: {$eq: 1}}</c>, those of a
    /// <c>$and</c>), set at their dotted paths.
    /// </summary>
    public static BsonDocument SeedFromFilter(BsonDocument? filter)
    {
        var seed = new BsonDocument();
        if (filter is null)
            return seed;
        void Collect(BsonDocument f)
        {
            foreach (var element in f)
            {
                if (element.Name == "$and" && element.Value is BsonArray clauses)
                {
                    foreach (var clause in clauses.OfType<BsonDocument>())
                        Collect(clause);
                    continue;
                }
                if (element.Name.StartsWith('$'))
                    continue;
                if (IsOperatorDocument(element.Value))
                {
                    if (element.Value.AsBsonDocument.TryGetValue("$eq", out var eq))
                        Paths.Set(seed, element.Name, eq.DeepClone());
                    continue;
                }
                if (element.Value.BsonType != BsonType.RegularExpression)
                    Paths.Set(seed, element.Name, element.Value.DeepClone());
            }
        }
        Collect(filter);
        return seed;
    }
}
