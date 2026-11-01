using MongoDB.Bson;

namespace KAONOMI.MongoEngine.Engine;

/// <summary>
/// A command error, answered as MongoDB answers it: <c>{ok: 0, errmsg, code, codeName}</c>.
/// </summary>
public sealed class MongoError : Exception
{
    public int Code { get; }
    public string CodeName { get; }

    /// <summary>Other fields of the error (<c>keyPattern</c>, <c>keyValue</c> of a duplicate key…).</summary>
    public BsonDocument? Extra { get; init; }

    public MongoError(int code, string codeName, string message) : base(message)
    {
        Code = code;
        CodeName = codeName;
    }

    public static MongoError BadValue(string message) => new(2, "BadValue", message);
    public static MongoError FailedToParse(string message) => new(9, "FailedToParse", message);
    public static MongoError TypeMismatch(string message) => new(14, "TypeMismatch", message);
    public static MongoError NamespaceNotFound(string message) => new(26, "NamespaceNotFound", message);
    public static MongoError CursorNotFound(long id) => new(43, "CursorNotFound", $"cursor id {id} not found");
    public static MongoError NamespaceExists(string message) => new(48, "NamespaceExists", message);
    public static MongoError CommandNotFound(string name) => new(59, "CommandNotFound", $"no such command: '{name}'");
    public static MongoError InvalidNamespace(string message) => new(73, "InvalidNamespace", message);
    public static MongoError NotSupported(string message) => new(115, "CommandNotSupported", message);
    public static MongoError IllegalOperation(string message) => new(20, "IllegalOperation", message);

    /// <summary>The error of a write that would give two documents the same key of a unique index.</summary>
    public static MongoError DuplicateKey(string ns, string indexName, BsonDocument keyPattern, BsonDocument keyValue) =>
        new(11000, "DuplicateKey", $"E11000 duplicate key error collection: {ns} index: {indexName} dup key: {DupKey(keyValue)}")
        {
            Extra = new BsonDocument { { "keyPattern", keyPattern }, { "keyValue", keyValue } }
        };

    private static string DupKey(BsonDocument keyValue) =>
        "{ " + string.Join(", ", keyValue.Elements.Select(e => $"{e.Name}: {e.Value.ToJson()}")) + " }";

    /// <summary>The reply of the error.</summary>
    public BsonDocument ToReply()
    {
        var reply = new BsonDocument { { "ok", 0.0 }, { "errmsg", Message }, { "code", Code }, { "codeName", CodeName } };
        if (Extra is not null)
            reply.Merge(Extra);
        return reply;
    }

    /// <summary>The <c>writeErrors</c> entry of the error for the write at <paramref name="index"/>.</summary>
    public BsonDocument ToWriteError(int index)
    {
        var error = new BsonDocument { { "index", index }, { "code", Code }, { "errmsg", Message } };
        if (Extra is not null)
            error.Merge(Extra);
        return error;
    }
}
