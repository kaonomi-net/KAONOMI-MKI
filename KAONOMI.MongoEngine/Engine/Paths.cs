using MongoDB.Bson;

namespace KAONOMI.MongoEngine.Engine;

/// <summary>The dotted paths of the updates: read, set (the missing objects are created) and removed.</summary>
public static class Paths
{
    /// <summary>The value at <paramref name="path"/> (no traversal of the arrays but by index), or null.</summary>
    public static BsonValue? Get(BsonValue root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.'))
        {
            switch (current)
            {
                case BsonDocument document when document.TryGetValue(part, out var child):
                    current = child;
                    break;
                case BsonArray array when int.TryParse(part, out var index) && index >= 0 && index < array.Count:
                    current = array[index];
                    break;
                default:
                    return null;
            }
        }
        return current;
    }

    /// <summary>Sets <paramref name="value"/> at <paramref name="path"/>, creating the missing objects.</summary>
    public static void Set(BsonDocument root, string path, BsonValue value)
    {
        var parts = path.Split('.');
        BsonValue current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var last = i == parts.Length - 1;
            if (part.Length == 0)
                throw MongoError.BadValue($"The update path '{path}' contains an empty field name, which is not allowed.");
            if (part == "$" || part.StartsWith("$[", StringComparison.Ordinal))
                throw MongoError.NotSupported($"the positional operator in '{path}' is not supported by KAONOMI.MongoEngine");
            switch (current)
            {
                case BsonDocument document:
                    if (last)
                    {
                        document[part] = value;
                        return;
                    }
                    if (!document.TryGetValue(part, out var child) || child.IsBsonNull)
                    {
                        child = new BsonDocument();
                        document[part] = child;
                    }
                    else if (child is not (BsonDocument or BsonArray))
                        throw new MongoError(28, "PathNotViable",
                            $"Cannot create field '{parts[i + 1]}' in element {{{part}: {child.ToJson()}}}");
                    current = child;
                    break;
                case BsonArray array:
                    if (!int.TryParse(part, out var index) || index < 0)
                        throw new MongoError(28, "PathNotViable", $"Cannot create field '{part}' in an array");
                    while (array.Count <= index)
                        array.Add(BsonNull.Value);
                    if (last)
                    {
                        array[index] = value;
                        return;
                    }
                    if (array[index] is not (BsonDocument or BsonArray))
                        array[index] = new BsonDocument();
                    current = array[index];
                    break;
            }
        }
    }

    /// <summary>Removes the field at <paramref name="path"/> (an array element becomes null); false when there is none.</summary>
    public static bool Unset(BsonDocument root, string path)
    {
        var lastDot = path.LastIndexOf('.');
        var parent = lastDot < 0 ? root : Get(root, path[..lastDot]);
        var name = lastDot < 0 ? path : path[(lastDot + 1)..];
        switch (parent)
        {
            case BsonDocument document when document.Contains(name):
                document.Remove(name);
                return true;
            case BsonArray array when int.TryParse(name, out var index) && index >= 0 && index < array.Count:
                array[index] = BsonNull.Value;
                return true;
            default:
                return false;
        }
    }
}
