using MongoDB.Bson;

namespace KAONOMI.MongoEngine.Engine;

/// <summary>The sorts (<c>{a: 1, b: -1}</c>) and the projections (<c>{a: 1}</c>, <c>{b: 0}</c>) of MongoDB.</summary>
public static class Sorting
{
    /// <summary>
    /// The comparer of a sort specification. For an array field, the ascending sort uses its smallest element, the
    /// descending one its largest; a missing field sorts as null.
    /// </summary>
    public static IComparer<BsonValue> Comparer(BsonValue spec)
    {
        if (spec.IsNumeric)
        {
            var direction = spec.ToInt32() < 0 ? -1 : 1;
            return Comparer<BsonValue>.Create((a, b) => direction * BsonOrder.Instance.Compare(a, b));
        }
        var keys = spec.AsBsonDocument.Select(e => (Path: e.Name, Direction: e.Value.IsNumeric && e.Value.ToInt32() < 0 ? -1 : 1)).ToList();
        return Comparer<BsonValue>.Create((a, b) =>
        {
            foreach (var (path, direction) in keys)
            {
                var c = BsonOrder.Instance.Compare(SortKey(a, path, direction), SortKey(b, path, direction));
                if (c != 0)
                    return direction * c;
            }
            return 0;
        });
    }

    private static BsonValue SortKey(BsonValue document, string path, int direction)
    {
        var values = Filter.Lookup(document, path).SelectMany(v => v is BsonArray a && a.Count > 0 ? a.AsEnumerable() : [v]).ToList();
        if (values.Count == 0)
            return BsonNull.Value;
        return direction > 0 ? values.Min(BsonOrder.Instance)! : values.Max(BsonOrder.Instance)!;
    }

    /// <summary>
    /// The document through a projection: inclusions (<c>{a: 1}</c>, <c>_id</c> kept unless <c>_id: 0</c>), or
    /// exclusions (<c>{a: 0}</c>); dotted paths allowed.
    /// </summary>
    public static BsonDocument Project(BsonDocument document, BsonDocument? projection)
    {
        if (projection is null || projection.ElementCount == 0)
            return document;
        var fields = projection.Where(e => e.Name != "_id").ToList();
        var inclusion = fields.Any(e => IsTrue(e.Value));
        if (fields.Any(e => IsTrue(e.Value) != inclusion))
            throw new MongoError(31254, "Location31254", "Cannot do exclusion on field in inclusion projection");
        var keepId = !projection.TryGetValue("_id", out var id) || IsTrue(id);
        if (!inclusion && fields.Count == 0 && keepId)
            return document;
        if (inclusion)
        {
            var result = new BsonDocument();
            if (keepId && document.TryGetValue("_id", out var idValue))
                result["_id"] = idValue;
            foreach (var field in fields)
                CopyPath(document, result, field.Name);
            return result;
        }
        var copy = document.DeepClone().AsBsonDocument;
        foreach (var field in fields)
            Paths.Unset(copy, field.Name);
        if (!keepId)
            copy.Remove("_id");
        return copy;
    }

    private static bool IsTrue(BsonValue value) => value.BsonType switch
    {
        BsonType.Boolean => value.AsBoolean,
        BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 => value.ToDouble() != 0,
        BsonType.Document or BsonType.String => throw MongoError.NotSupported("projection operators and expressions are not supported by KAONOMI.MongoEngine"),
        _ => true
    };

    private static void CopyPath(BsonDocument source, BsonDocument target, string path)
    {
        var dot = path.IndexOf('.');
        var name = dot < 0 ? path : path[..dot];
        if (!source.TryGetValue(name, out var value))
            return;
        if (dot < 0)
        {
            target[name] = value;
            return;
        }
        var rest = path[(dot + 1)..];
        switch (value)
        {
            case BsonDocument child:
            {
                var into = target.TryGetValue(name, out var existing) && existing is BsonDocument e ? e : new BsonDocument();
                CopyPath(child, into, rest);
                if (into.ElementCount > 0)
                    target[name] = into;
                break;
            }
            case BsonArray array:
            {
                var into = new BsonArray();
                foreach (var item in array.OfType<BsonDocument>())
                {
                    var part = new BsonDocument();
                    CopyPath(item, part, rest);
                    into.Add(part);
                }
                target[name] = into;
                break;
            }
        }
    }
}
