using System.Buffers.Binary;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace KAONOMI.MongoEngine.Wire;

/// <summary>A request of a client: its id, the database, the command (with the documents of its sequences merged in).</summary>
public sealed record WireRequest(int RequestId, int OpCode, string Database, BsonDocument Command, bool MoreToCome, bool ExhaustAllowed);

/// <summary>
/// The wire protocol of MongoDB: the header (length, requestID, responseTo, opCode), OP_MSG (2013: flags, a body
/// section, document sequences) and the legacy OP_QUERY (2004) of the first handshake, answered with OP_REPLY (1).
/// </summary>
public static class WireProtocol
{
    public const int OpReply = 1;
    public const int OpQuery = 2004;
    public const int OpCompressed = 2012;
    public const int OpMsg = 2013;

    private const uint ChecksumPresent = 1;
    private const uint MoreToComeFlag = 2;
    private const uint ExhaustAllowedFlag = 1 << 16;

    /// <summary>Reads one message (null when the client closed the connection).</summary>
    public static async Task<(int RequestId, int OpCode, byte[] Body)?> ReadAsync(Stream stream, int maxSize, CancellationToken token)
    {
        var header = new byte[16];
        if (!await ReadExactly(stream, header, token))
            return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 16 || length > maxSize)
            throw new InvalidDataException($"message length {length} is not valid");
        var requestId = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var opCode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        var body = new byte[length - 16];
        if (!await ReadExactly(stream, body, token))
            return null;
        return (requestId, opCode, body);
    }

    private static async Task<bool> ReadExactly(Stream stream, byte[] buffer, CancellationToken token)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), token);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    /// <summary>The request of an OP_MSG or an OP_QUERY on <c>db.$cmd</c>.</summary>
    public static WireRequest Parse(int requestId, int opCode, byte[] body)
    {
        switch (opCode)
        {
            case OpMsg:
                return ParseMsg(requestId, body);
            case OpQuery:
                return ParseQuery(requestId, body);
            case OpCompressed:
                throw new NotSupportedException("compression was not negotiated");
            default:
                throw new NotSupportedException($"opCode {opCode} is not supported (only OP_MSG and the OP_QUERY of the handshake)");
        }
    }

    private static WireRequest ParseMsg(int requestId, byte[] body)
    {
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var end = body.Length - ((flags & ChecksumPresent) != 0 ? 4 : 0);
        var offset = 4;
        BsonDocument? command = null;
        var sequences = new List<(string Name, BsonArray Documents)>();
        while (offset < end)
        {
            var kind = body[offset++];
            if (kind == 0)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset));
                command = ReadDocument(body, offset, length);
                offset += length;
            }
            else if (kind == 1)
            {
                var size = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset));
                var sectionEnd = offset + size;
                var position = offset + 4;
                var nameEnd = Array.IndexOf(body, (byte)0, position);
                var name = Encoding.UTF8.GetString(body, position, nameEnd - position);
                position = nameEnd + 1;
                var documents = new BsonArray();
                while (position < sectionEnd)
                {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(position));
                    documents.Add(ReadDocument(body, position, length));
                    position += length;
                }
                sequences.Add((name, documents));
                offset = sectionEnd;
            }
            else
                throw new InvalidDataException($"OP_MSG section kind {kind} is not valid");
        }
        if (command is null)
            throw new InvalidDataException("OP_MSG without a body section");
        foreach (var (name, documents) in sequences)
            command[name] = documents;
        var database = command.TryGetValue("$db", out var db) ? db.AsString : "admin";
        return new WireRequest(requestId, OpMsg, database, command, (flags & MoreToComeFlag) != 0, (flags & ExhaustAllowedFlag) != 0);
    }

    private static WireRequest ParseQuery(int requestId, byte[] body)
    {
        var offset = 4; // flags
        var nameEnd = Array.IndexOf(body, (byte)0, offset);
        var fullName = Encoding.UTF8.GetString(body, offset, nameEnd - offset);
        offset = nameEnd + 1 + 8; // numberToSkip, numberToReturn
        var length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset));
        var query = ReadDocument(body, offset, length);
        if (!fullName.EndsWith(".$cmd", StringComparison.Ordinal))
            throw new NotSupportedException($"OP_QUERY on {fullName}: only commands are supported (use OP_MSG)");
        if (query.TryGetValue("$query", out var wrapped) && wrapped is BsonDocument inner)
            query = inner;
        return new WireRequest(requestId, OpQuery, fullName[..^5], query, false, false);
    }

    private static BsonDocument ReadDocument(byte[] bytes, int offset, int length)
    {
        using var reader = new BsonBinaryReader(new MemoryStream(bytes, offset, length, writable: false));
        return BsonSerializer.Deserialize<BsonDocument>(reader);
    }

    /// <summary>The reply to <paramref name="request"/>: an OP_MSG, or an OP_REPLY to an OP_QUERY.</summary>
    public static byte[] Reply(WireRequest request, int responseId, BsonDocument reply)
    {
        var document = reply.ToBson();
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[16];
        stream.Write(header); // written at the end
        if (request.OpCode == OpQuery)
        {
            Span<byte> fields = stackalloc byte[20];
            BinaryPrimitives.WriteInt32LittleEndian(fields, 8); // AwaitCapable
            BinaryPrimitives.WriteInt64LittleEndian(fields[4..], 0); // cursorID
            BinaryPrimitives.WriteInt32LittleEndian(fields[12..], 0); // startingFrom
            BinaryPrimitives.WriteInt32LittleEndian(fields[16..], 1); // numberReturned
            stream.Write(fields);
            stream.Write(document);
        }
        else
        {
            Span<byte> flags = stackalloc byte[5];
            BinaryPrimitives.WriteUInt32LittleEndian(flags, 0);
            flags[4] = 0; // section kind 0
            stream.Write(flags);
            stream.Write(document);
        }
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), responseId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), request.RequestId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), request.OpCode == OpQuery ? OpReply : OpMsg);
        return bytes;
    }
}
