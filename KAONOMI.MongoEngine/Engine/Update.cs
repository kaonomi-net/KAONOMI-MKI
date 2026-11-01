using MongoDB.Bson;

namespace KAONOMI.MongoEngine.Engine;

/// <summary>
/// The updates of MongoDB: a replacement document, or the operators <c>$set</c>, <c>$unset</c>,
/// <c>$setOnInsert</c>, <c>$inc</c>, <c>$mul</c>, <c>$min</c>, <c>$max</c>, <c>$rename</c>, <c>$currentDate</c>,
/// <c>$push</c> (<c>$each</c>, <c>$position</c>, <c>$slice</c>), <c>$addToSet</c> (<c>$each</c>), <c>$pull</c>,
/// <c>$pullAll</c>, <c>$pop</c>.
/// </summary>
public static class Update
{
    /// <summary>True when <paramref name="update"/> holds operators (else it replaces the document).</summary>
    public static bool IsOperators(BsonDocument update) => update.ElementCount > 0 && update.GetElement(0).Name.StartsWith('$');

    /// <summary>
    /// The document after <paramref name="update"/> (a copy: <paramref name="document"/> is not changed).
    /// <paramref name="inserting"/>: an upsert creates it (<c>$setOnInsert</c> applies).
    /// </summary>
    public static BsonDocument Apply(BsonDocument document, BsonDocument update, bool inserting)
    {
        if (!IsOperators(update))
        {
            if (update.Names.Any(n => n.StartsWith('$')))
                throw MongoError.FailedToParse("A replacement document cannot hold update operators");
            var replacement = new BsonDocument();
            if (document.TryGetValue("_id", out var id))
                replacement["_id"] = id;
            foreach (var element in update)
            {
                if (element.Name == "_id" && replacement.Contains("_id") && !BsonOrder.Instance.Equals(replacement["_id"], element.Value))
                    throw new MongoError(66, "ImmutableField", "After applying the update, the (immutable) field '_id' was found to have been altered");
                replacement[element.Name] = element.Value.DeepClone();
            }
            return replacement;
        }

        var result = document.DeepClone().AsBsonDocument;
        foreach (var element in update)
        {
            if (element.Value is not BsonDocument fields)
                throw MongoError.FailedToParse($"Modifiers operate on fields but we found type {element.Value.BsonType} instead");
            foreach (var field in fields)
            {
                // _id can be given only to a new document, or set to the value it has.
                var touchesId = field.Name == "_id" || field.Name.StartsWith("_id.", StringComparison.Ordinal);
                var allowed = inserting && element.Name is "$set" or "$setOnInsert"
                    || element.Name == "$set" && field.Name == "_id" && result.TryGetValue("_id", out var id) && BsonOrder.Instance.Equals(id, field.Value);
                if (touchesId && !allowed)
                    throw new MongoError(66, "ImmutableField", "Performing an update on the path '_id' would modify the immutable field '_id'");
                ApplyOperator(result, element.Name, field.Name, field.Value, inserting);
            }
        }
        return result;
    }

    private static void ApplyOperator(BsonDocument doc, string op, string path, BsonValue value, bool inserting)
    {
        switch (op)
        {
            case "$set":
                Paths.Set(doc, path, value.DeepClone());
                break;
            case "$setOnInsert":
                if (inserting)
                    Paths.Set(doc, path, value.DeepClone());
                break;
            case "$unset":
                Paths.Unset(doc, path);
                break;
            case "$inc":
                Paths.Set(doc, path, Arithmetic(Paths.Get(doc, path), value, path, multiply: false));
                break;
            case "$mul":
                Paths.Set(doc, path, Arithmetic(Paths.Get(doc, path), value, path, multiply: true));
                break;
            case "$min":
            {
                var current = Paths.Get(doc, path);
                if (current is null || BsonOrder.Instance.Compare(value, current) < 0)
                    Paths.Set(doc, path, value.DeepClone());
                break;
            }
            case "$max":
            {
                var current = Paths.Get(doc, path);
                if (current is null || BsonOrder.Instance.Compare(value, current) > 0)
                    Paths.Set(doc, path, value.DeepClone());
                break;
            }
            case "$rename":
            {
                var current = Paths.Get(doc, path);
                if (current is not null)
                {
                    Paths.Unset(doc, path);
                    Paths.Set(doc, value.AsString, current);
                }
                break;
            }
            case "$currentDate":
            {
                var timestamp = value is BsonDocument { } spec && spec.TryGetValue("$type", out var type) && type.AsString == "timestamp";
                Paths.Set(doc, path, timestamp
                    ? new BsonTimestamp((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1)
                    : new BsonDateTime(DateTime.UtcNow));
                break;
            }
            case "$push":
            {
                var array = ArrayAt(doc, path, op);
                if (value is BsonDocument modifiers && modifiers.Contains("$each"))
                {
                    var each = modifiers["$each"].AsBsonArray.Select(v => v.DeepClone()).ToList();
                    var position = modifiers.TryGetValue("$position", out var p) ? Math.Clamp(p.ToInt32() < 0 ? array.Count + p.ToInt32() : p.ToInt32(), 0, array.Count) : array.Count;
                    for (var i = 0; i < each.Count; i++)
                        array.Insert(position + i, each[i]);
                    if (modifiers.TryGetValue("$sort", out var sort))
                    {
                        var sorted = array.OrderBy(v => v, Sorting.Comparer(sort)).ToList();
                        array.Clear();
                        array.AddRange(sorted);
                    }
                    if (modifiers.TryGetValue("$slice", out var slice))
                    {
                        var n = slice.ToInt32();
                        var kept = n >= 0 ? array.Take(n).ToList() : array.Skip(Math.Max(0, array.Count + n)).ToList();
                        array.Clear();
                        array.AddRange(kept);
                    }
                }
                else
                    array.Add(value.DeepClone());
                break;
            }
            case "$addToSet":
            {
                var array = ArrayAt(doc, path, op);
                var values = value is BsonDocument modifiers && modifiers.Contains("$each") ? modifiers["$each"].AsBsonArray.ToList() : [value];
                foreach (var v in values)
                    if (!array.Any(existing => BsonOrder.Instance.Equals(existing, v)))
                        array.Add(v.DeepClone());
                break;
            }
            case "$pull":
            {
                if (Paths.Get(doc, path) is not BsonArray array)
                    break;
                var kept = array.Where(item => !PullMatches(item, value)).ToList();
                array.Clear();
                array.AddRange(kept);
                break;
            }
            case "$pullAll":
            {
                if (Paths.Get(doc, path) is not BsonArray array)
                    break;
                var removed = value.AsBsonArray;
                var kept = array.Where(item => !removed.Any(r => BsonOrder.Instance.Equals(r, item))).ToList();
                array.Clear();
                array.AddRange(kept);
                break;
            }
            case "$pop":
            {
                if (Paths.Get(doc, path) is BsonArray { Count: > 0 } array)
                    array.RemoveAt(value.ToInt32() < 0 ? 0 : array.Count - 1);
                break;
            }
            default:
                throw MongoError.FailedToParse($"Unknown modifier: {op}. Expected a valid update modifier or pipeline-style update specified as an array");
        }
    }

    private static BsonArray ArrayAt(BsonDocument doc, string path, string op)
    {
        var current = Paths.Get(doc, path);
        if (current is null || current.IsBsonNull)
        {
            var created = new BsonArray();
            Paths.Set(doc, path, created);
            return created;
        }
        return current as BsonArray ?? throw new MongoError(2, "BadValue",
            $"The field '{path}' must be an array but is of type {current.BsonType.ToString().ToLowerInvariant()} in document");
    }

    private static bool PullMatches(BsonValue item, BsonValue condition)
    {
        if (condition is BsonDocument spec)
        {
            if (Filter.IsOperatorDocument(spec))
                return Filter.Matches(new BsonDocument("v", item), new BsonDocument("v", spec));
            return item is BsonDocument d && Filter.Matches(d, spec);
        }
        return BsonOrder.Instance.Equals(item, condition);
    }

    private static BsonValue Arithmetic(BsonValue? current, BsonValue operand, string path, bool multiply)
    {
        if (!operand.IsNumeric)
            throw MongoError.TypeMismatch($"Cannot {(multiply ? "multiply" : "increment")} with non-numeric argument: {{{path}: {operand.ToJson()}}}");
        if (current is null)
            return multiply ? Zero(operand) : operand.DeepClone();
        if (!current.IsNumeric)
            throw MongoError.TypeMismatch($"Cannot apply {(multiply ? "$mul" : "$inc")} to a value of non-numeric type. {{_id: ...}} has the field '{path}' of non-numeric type {current.BsonType.ToString().ToLowerInvariant()}");
        if (current.BsonType == BsonType.Decimal128 || operand.BsonType == BsonType.Decimal128)
            return new BsonDecimal128(multiply ? current.ToDecimal() * operand.ToDecimal() : current.ToDecimal() + operand.ToDecimal());
        if (current.BsonType == BsonType.Double || operand.BsonType == BsonType.Double)
            return new BsonDouble(multiply ? current.ToDouble() * operand.ToDouble() : current.ToDouble() + operand.ToDouble());
        var result = multiply ? current.ToInt64() * operand.ToInt64() : current.ToInt64() + operand.ToInt64();
        return current.BsonType == BsonType.Int32 && operand.BsonType == BsonType.Int32 && result is >= int.MinValue and <= int.MaxValue
            ? new BsonInt32((int)result)
            : new BsonInt64(result);
    }

    private static BsonValue Zero(BsonValue like) => like.BsonType switch
    {
        BsonType.Double => new BsonDouble(0),
        BsonType.Int64 => new BsonInt64(0),
        BsonType.Decimal128 => new BsonDecimal128(0),
        _ => new BsonInt32(0)
    };
}
