using System.Collections.Concurrent;
using System.Diagnostics;
using KAONOMI.MongoEngine.Storage;
using KAONOMI.MongoEngine.Wire;
using MongoDB.Bson;

namespace KAONOMI.MongoEngine.Engine;

/// <summary>
/// The commands of MongoDB the engine answers, as a standalone <c>mongod</c> 7.0 answers them: the handshake
/// (<c>hello</c>, <c>isMaster</c>, <c>buildInfo</c>…), the CRUD commands (<c>find</c>, <c>getMore</c>,
/// <c>killCursors</c>, <c>insert</c>, <c>update</c>, <c>delete</c>, <c>findAndModify</c>, <c>count</c>,
/// <c>distinct</c>, <c>aggregate</c>), the collections and indexes (<c>create</c>, <c>drop</c>,
/// <c>listCollections</c>, <c>createIndexes</c>, <c>listIndexes</c>, <c>dropIndexes</c>…) and the databases.
/// </summary>
public sealed class CommandHandler
{
    public const string Version = "7.0.14";
    private const int MaxWireVersion = 21;
    private const int DefaultFirstBatch = 101;
    private const int MaxBatchBytes = 16 * 1024 * 1024 - 16 * 1024;

    private readonly StorageEngine _storage;
    private readonly EngineLog _log;
    private readonly ConcurrentDictionary<long, Cursor> _cursors = new();
    private readonly DateTime _started = DateTime.UtcNow;
    private long _nextCursor = Random.Shared.NextInt64(1L << 40, 1L << 50);

    private sealed class Cursor(long id, string ns, List<BsonDocument> documents, long connection)
    {
        public long Id { get; } = id;
        public string Namespace { get; } = ns;
        public List<BsonDocument> Documents { get; } = documents;
        public int Position { get; set; }
        public long Connection { get; } = connection;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }

    public CommandHandler(StorageEngine storage, EngineLog log)
    {
        _storage = storage;
        _log = log;
    }

    /// <summary>Raised by the <c>shutdown</c> command: the host stops the engine (after the reply).</summary>
    public event Action? ShutdownRequested;

    public void ConnectionClosed(ClientConnection connection)
    {
        // The cursors of a closed connection stay until they time out (10 minutes), as in MongoDB.
        foreach (var cursor in _cursors.Values.Where(c => DateTime.UtcNow - c.LastUsed > TimeSpan.FromMinutes(10)))
            _cursors.TryRemove(cursor.Id, out _);
    }

    /// <summary>Runs the command of <paramref name="request"/> and gives its reply (an error reply when it fails).</summary>
    public BsonDocument Execute(WireRequest request, ClientConnection connection)
    {
        var command = request.Command;
        if (command.ElementCount == 0)
            return MongoError.FailedToParse("empty command").ToReply();
        var name = command.GetElement(0).Name;
        var watch = Stopwatch.StartNew();
        try
        {
            if (command.Contains("startTransaction") || command.Contains("autocommit"))
                throw MongoError.IllegalOperation("Transaction numbers are only allowed on a replica set member or mongos");
            var reply = Dispatch(name, request.Database, command, connection);
            if (!reply.Contains("ok"))
                reply["ok"] = 1.0;
            if (_log.Verbosity >= 1 && name is not ("hello" or "isMaster" or "ismaster"))
                _log.Debug("COMMAND", $"conn{connection.Id} {request.Database}.{name} {Summary(command)} {watch.ElapsedMilliseconds}ms");
            return reply;
        }
        catch (MongoError error)
        {
            _log.Debug("COMMAND", $"conn{connection.Id} {request.Database}.{name} failed: {error.CodeName} {error.Message}");
            return error.ToReply();
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or KeyNotFoundException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return MongoError.BadValue($"{name}: {ex.Message}").ToReply();
        }
        catch (Exception ex)
        {
            _log.Error("COMMAND", $"conn{connection.Id} {request.Database}.{name}: {ex}");
            return new MongoError(1, "InternalError", ex.Message).ToReply();
        }
    }

    private static string Summary(BsonDocument command)
    {
        var copy = new BsonDocument(command.Where(e => e.Name is not ("lsid" or "$db" or "$clusterTime" or "$readPreference" or "documents" or "updates" or "deletes")));
        var text = copy.ToJson();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    private BsonDocument Dispatch(string name, string db, BsonDocument c, ClientConnection connection)
    {
        switch (name)
        {
            case "hello" or "isMaster" or "ismaster":
                return Hello(name, c, connection);
            case "ping":
                return new BsonDocument();
            case "buildInfo" or "buildinfo":
                return BuildInfo();
            case "getParameter":
                return GetParameter(c);
            case "connectionStatus":
                return new BsonDocument("authInfo", new BsonDocument { { "authenticatedUsers", new BsonArray() }, { "authenticatedUserRoles", new BsonArray() } });
            case "whatsmyuri":
                return new BsonDocument("you", connection.Remote);
            case "hostInfo":
                return HostInfo();
            case "getCmdLineOpts":
                return new BsonDocument { { "argv", new BsonArray(Environment.GetCommandLineArgs()) }, { "parsed", new BsonDocument() } };
            case "serverStatus":
                return ServerStatus();
            case "getLog":
                return c[0].ToString() == "*" ? new BsonDocument("names", new BsonArray { "global", "startupWarnings" }) : new BsonDocument { { "totalLinesWritten", 0 }, { "log", new BsonArray() } };
            case "endSessions" or "killSessions" or "killAllSessions" or "refreshSessions" or "killAllSessionsByPattern":
                return new BsonDocument();
            case "startSession":
                return new BsonDocument { { "id", new BsonDocument("id", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard)) }, { "timeoutMinutes", 30 } };
            case "getLastError" or "getlasterror":
                return new BsonDocument { { "n", 0 }, { "err", BsonNull.Value } };
            case "currentOp":
                return new BsonDocument("inprog", new BsonArray());
            case "killOp":
                return new BsonDocument("info", "attempting to kill op");
            case "replSetGetStatus":
                throw new MongoError(76, "NoReplicationEnabled", "not running with --replSet");
            case "saslStart" or "authenticate" or "saslContinue":
                throw new MongoError(18, "AuthenticationFailed", "Authentication failed: KAONOMI.MongoEngine runs without authentication");
            case "listCommands":
                return ListCommands();
            case "shutdown":
                // As mongod: the server stops (the chat of KAONOMI sends it on /bye). The reply goes out first.
                _log.Info("CONTROL", $"shutdown command received from conn{connection.Id}: stopping");
                ShutdownRequested?.Invoke();
                return new BsonDocument();
            case "explain":
                throw MongoError.NotSupported("explain is not supported by KAONOMI.MongoEngine");

            case "find":
                return Find(db, c, connection);
            case "getMore":
                return GetMore(c);
            case "killCursors":
                return KillCursors(c);
            case "insert":
                return Insert(db, c);
            case "update":
                return UpdateCommand(db, c);
            case "delete":
                return Delete(db, c);
            case "findAndModify" or "findandmodify":
                return FindAndModify(db, c);
            case "count":
                return Count(db, c);
            case "distinct":
                return Distinct(db, c);
            case "aggregate":
                return Aggregate(db, c, connection);

            case "create":
                _storage.Create(db, c["create"].AsString, new BsonDocument(c.Where(e => e.Name is not ("create" or "lsid" or "$db" or "$readPreference" or "writeConcern"))));
                return new BsonDocument();
            case "drop":
            {
                var collection = _storage.Get(db, c["drop"].AsString, create: false)
                    ?? throw MongoError.NamespaceNotFound("ns not found");
                var indexes = collection.Indexes.Count;
                _storage.Drop(db, collection.Name);
                return new BsonDocument { { "nIndexesWas", indexes }, { "ns", collection.Namespace } };
            }
            case "dropDatabase":
                _storage.DropDatabase(db);
                return new BsonDocument("dropped", db);
            case "listCollections":
                return ListCollections(db, c, connection);
            case "listDatabases":
                return ListDatabases(c);
            case "createIndexes":
                return CreateIndexes(db, c);
            case "listIndexes":
                return ListIndexes(db, c, connection);
            case "dropIndexes" or "deleteIndexes":
                return DropIndexes(db, c);
            case "collStats":
                return CollStats(db, c);
            case "dbStats":
                return DbStats(db);
            case "renameCollection":
                throw MongoError.NotSupported("renameCollection is not supported by KAONOMI.MongoEngine");
            default:
                throw MongoError.CommandNotFound(name);
        }
    }

    // ── Handshake and server ──

    private BsonDocument Hello(string name, BsonDocument c, ClientConnection connection)
    {
        if (c.TryGetValue("client", out var client) && client is BsonDocument metadata)
        {
            connection.ClientMetadata = metadata;
            _log.Debug("NETWORK", $"client metadata conn{connection.Id} {metadata.ToJson()}");
        }
        var reply = new BsonDocument();
        if (c.TryGetValue("helloOk", out var helloOk) && helloOk.ToBoolean())
            reply["helloOk"] = true;
        reply[name == "hello" ? "isWritablePrimary" : "ismaster"] = true;
        reply["maxBsonObjectSize"] = Collection.MaxDocumentSize;
        reply["maxMessageSizeBytes"] = 48_000_000;
        reply["maxWriteBatchSize"] = 100_000;
        reply["localTime"] = new BsonDateTime(DateTime.UtcNow);
        reply["logicalSessionTimeoutMinutes"] = 30;
        reply["connectionId"] = connection.Id;
        reply["minWireVersion"] = 0;
        reply["maxWireVersion"] = MaxWireVersion;
        reply["readOnly"] = false;
        return reply;
    }

    private static BsonDocument BuildInfo() => new()
    {
        { "version", Version },
        { "gitVersion", "kaonomi-mongoengine" },
        { "modules", new BsonArray() },
        { "allocator", "system" },
        { "javascriptEngine", "none" },
        { "sysInfo", "deprecated" },
        { "versionArray", new BsonArray { 7, 0, 14, 0 } },
        { "openssl", new BsonDocument { { "running", "disabled" }, { "compiled", "disabled" } } },
        { "buildEnvironment", new BsonDocument("distmod", "kaonomi") },
        { "bits", 64 },
        { "debug", false },
        { "maxBsonObjectSize", Collection.MaxDocumentSize },
        { "storageEngines", new BsonArray { "kaonomi" } }
    };

    private static BsonDocument GetParameter(BsonDocument c)
    {
        var reply = new BsonDocument();
        if (c.Contains("featureCompatibilityVersion") || c[0].ToString() == "*")
            reply["featureCompatibilityVersion"] = new BsonDocument("version", "7.0");
        return reply;
    }

    private static BsonDocument HostInfo() => new()
    {
        {
            "system", new BsonDocument
            {
                { "currentTime", new BsonDateTime(DateTime.UtcNow) },
                { "hostname", Environment.MachineName },
                { "cpuAddrSize", 64 },
                { "numCores", Environment.ProcessorCount },
                { "cpuArch", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() }
            }
        },
        { "os", new BsonDocument { { "type", Environment.OSVersion.Platform.ToString() }, { "name", System.Runtime.InteropServices.RuntimeInformation.OSDescription } } },
        { "extra", new BsonDocument() }
    };

    private BsonDocument ServerStatus() => new()
    {
        { "host", Environment.MachineName },
        { "version", Version },
        { "process", "mongod" },
        { "pid", (long)Environment.ProcessId },
        { "uptime", (DateTime.UtcNow - _started).TotalSeconds },
        { "uptimeMillis", (long)(DateTime.UtcNow - _started).TotalMilliseconds },
        { "localTime", new BsonDateTime(DateTime.UtcNow) },
        { "connections", new BsonDocument { { "current", 0 }, { "available", 10000 } } },
        { "storageEngine", new BsonDocument { { "name", "kaonomi" }, { "persistent", true } } }
    };

    private static BsonDocument ListCommands()
    {
        var commands = new BsonDocument();
        foreach (var name in new[] { "aggregate", "buildInfo", "collStats", "count", "create", "createIndexes", "dbStats", "delete", "distinct", "drop",
                     "dropDatabase", "dropIndexes", "endSessions", "find", "findAndModify", "getMore", "hello", "insert", "isMaster", "killCursors",
                     "listCollections", "listDatabases", "listIndexes", "ping", "update" })
            commands[name] = new BsonDocument("help", name);
        return new BsonDocument("commands", commands);
    }

    // ── Reads ──

    private BsonDocument Find(string db, BsonDocument c, ClientConnection connection)
    {
        var name = c["find"].AsString;
        var collection = _storage.Get(db, name, create: false);
        var filter = c.GetValue("filter", BsonNull.Value) as BsonDocument;
        IEnumerable<BsonDocument> documents = collection?.Find(filter) ?? [];
        if (c.TryGetValue("sort", out var sort) && sort is BsonDocument { ElementCount: > 0 })
            documents = documents.OrderBy(d => (BsonValue)d, Sorting.Comparer(sort)).ToList();
        if (c.TryGetValue("skip", out var skip))
            documents = documents.Skip((int)skip.ToInt64());
        var single = c.GetValue("singleBatch", false).ToBoolean();
        if (c.TryGetValue("limit", out var limitValue) && limitValue.ToInt64() != 0)
        {
            var limit = limitValue.ToInt64();
            if (limit < 0)
                single = true;
            documents = documents.Take((int)Math.Min(Math.Abs(limit), int.MaxValue));
        }
        var projection = c.GetValue("projection", BsonNull.Value) as BsonDocument;
        var results = documents.Select(d => Sorting.Project(d, projection)).ToList();
        var batchSize = c.TryGetValue("batchSize", out var b) ? b.ToInt32() : DefaultFirstBatch;
        return CursorReply($"{db}.{name}", results, batchSize, single, "firstBatch", connection);
    }

    /// <summary>The first batch of <paramref name="results"/>; a cursor keeps the rest for <c>getMore</c>.</summary>
    private BsonDocument CursorReply(string ns, List<BsonDocument> results, int batchSize, bool single, string batchName, ClientConnection connection)
    {
        var (batch, taken) = TakeBatch(results, 0, batchSize);
        long id = 0;
        if (!single && taken < results.Count)
        {
            id = Interlocked.Increment(ref _nextCursor);
            _cursors[id] = new Cursor(id, ns, results, connection.Id) { Position = taken };
        }
        return new BsonDocument("cursor", new BsonDocument { { batchName, batch }, { "id", id }, { "ns", ns } });
    }

    private static (BsonArray Batch, int Taken) TakeBatch(List<BsonDocument> documents, int from, int batchSize)
    {
        var batch = new BsonArray();
        var bytes = 0;
        var i = from;
        var max = batchSize <= 0 ? int.MaxValue : batchSize;
        while (i < documents.Count && batch.Count < max)
        {
            var size = documents[i].ToBson().Length;
            if (batch.Count > 0 && bytes + size > MaxBatchBytes)
                break;
            batch.Add(documents[i]);
            bytes += size;
            i++;
        }
        return (batch, i);
    }

    private BsonDocument GetMore(BsonDocument c)
    {
        var id = c["getMore"].ToInt64();
        if (!_cursors.TryGetValue(id, out var cursor))
            throw MongoError.CursorNotFound(id);
        var batchSize = c.TryGetValue("batchSize", out var b) ? b.ToInt32() : 0;
        var (batch, taken) = TakeBatch(cursor.Documents, cursor.Position, batchSize);
        cursor.Position = taken;
        cursor.LastUsed = DateTime.UtcNow;
        var done = taken >= cursor.Documents.Count;
        if (done)
            _cursors.TryRemove(id, out _);
        return new BsonDocument("cursor", new BsonDocument { { "nextBatch", batch }, { "id", done ? 0L : id }, { "ns", cursor.Namespace } });
    }

    private BsonDocument KillCursors(BsonDocument c)
    {
        var killed = new BsonArray();
        var notFound = new BsonArray();
        foreach (var id in c.GetValue("cursors", new BsonArray()).AsBsonArray.Select(v => v.ToInt64()))
        {
            if (_cursors.TryRemove(id, out _))
                killed.Add(id);
            else
                notFound.Add(id);
        }
        return new BsonDocument
        {
            { "cursorsKilled", killed }, { "cursorsNotFound", notFound }, { "cursorsAlive", new BsonArray() }, { "cursorsUnknown", new BsonArray() }
        };
    }

    private BsonDocument Count(string db, BsonDocument c)
    {
        var collection = _storage.Get(db, c["count"].AsString, create: false);
        var filter = c.GetValue("query", BsonNull.Value) as BsonDocument;
        IEnumerable<BsonDocument> documents = collection?.Find(filter) ?? [];
        if (c.TryGetValue("skip", out var skip))
            documents = documents.Skip((int)skip.ToInt64());
        if (c.TryGetValue("limit", out var limit) && limit.ToInt64() != 0)
            documents = documents.Take((int)Math.Abs(limit.ToInt64()));
        return new BsonDocument("n", documents.Count());
    }

    private BsonDocument Distinct(string db, BsonDocument c)
    {
        var collection = _storage.Get(db, c["distinct"].AsString, create: false);
        var key = c["key"].AsString;
        var filter = c.GetValue("query", BsonNull.Value) as BsonDocument;
        var values = new List<BsonValue>();
        var seen = new HashSet<string>();
        foreach (var document in collection?.Find(filter) ?? [])
            foreach (var value in Filter.Lookup(document, key).SelectMany(v => v is BsonArray a ? a.AsEnumerable() : [v]))
                if (seen.Add(BsonOrder.Key(value)))
                    values.Add(value);
        return new BsonDocument("values", new BsonArray(values));
    }

    private BsonDocument Aggregate(string db, BsonDocument c, ClientConnection connection)
    {
        if (c["aggregate"].IsNumeric)
            throw MongoError.NotSupported("database aggregations ($currentOp…) are not supported by KAONOMI.MongoEngine");
        var name = c["aggregate"].AsString;
        var pipeline = c["pipeline"].AsBsonArray;
        if (pipeline.OfType<BsonDocument>().Any(s => s.Contains("$out") || s.Contains("$merge")))
            throw MongoError.NotSupported("$out and $merge are not supported by KAONOMI.MongoEngine");
        if (c.GetValue("explain", false).ToBoolean())
            throw MongoError.NotSupported("explain is not supported by KAONOMI.MongoEngine");
        var collection = _storage.Get(db, name, create: false);
        var results = Aggregation.Run(collection?.Find(null) ?? [], pipeline,
            other => _storage.Get(db, other, create: false)?.Find(null) ?? []).ToList();
        var batchSize = c.TryGetValue("cursor", out var cursor) && cursor is BsonDocument d && d.TryGetValue("batchSize", out var b) ? b.ToInt32() : DefaultFirstBatch;
        return CursorReply($"{db}.{name}", results, batchSize, false, "firstBatch", connection);
    }

    // ── Writes ──

    private BsonDocument Insert(string db, BsonDocument c)
    {
        var name = c["insert"].AsString;
        var documents = c["documents"].AsBsonArray;
        var ordered = c.GetValue("ordered", true).ToBoolean();
        var collection = _storage.Get(db, name, create: true)!;
        var errors = new BsonArray();
        var n = 0;
        for (var i = 0; i < documents.Count; i++)
        {
            try
            {
                collection.Insert(documents[i].AsBsonDocument);
                n++;
            }
            catch (MongoError error)
            {
                errors.Add(error.ToWriteError(i));
                if (ordered)
                    break;
            }
        }
        return WriteReply(n, errors);
    }

    private BsonDocument UpdateCommand(string db, BsonDocument c)
    {
        var name = c["update"].AsString;
        var statements = c["updates"].AsBsonArray;
        var ordered = c.GetValue("ordered", true).ToBoolean();
        var errors = new BsonArray();
        var upserted = new BsonArray();
        int n = 0, modified = 0;
        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i].AsBsonDocument;
            try
            {
                if (statement["u"] is BsonArray)
                    throw MongoError.NotSupported("pipeline-style updates are not supported by KAONOMI.MongoEngine");
                if (statement.Contains("arrayFilters"))
                    throw MongoError.NotSupported("arrayFilters are not supported by KAONOMI.MongoEngine");
                var upsert = statement.GetValue("upsert", false).ToBoolean();
                var multi = statement.GetValue("multi", false).ToBoolean();
                var collection = _storage.Get(db, name, create: upsert);
                if (collection is null)
                    continue;
                var result = collection.Update(statement["q"].AsBsonDocument, statement["u"].AsBsonDocument, upsert, multi);
                n += result.Matched;
                modified += result.Modified;
                if (result.UpsertedId is { } id)
                {
                    n++;
                    upserted.Add(new BsonDocument { { "index", i }, { "_id", id } });
                }
            }
            catch (MongoError error)
            {
                errors.Add(error.ToWriteError(i));
                if (ordered)
                    break;
            }
        }
        var reply = WriteReply(n, errors);
        reply.InsertAt(1, new BsonElement("nModified", modified));
        if (upserted.Count > 0)
            reply.InsertAt(1, new BsonElement("upserted", upserted));
        return reply;
    }

    private BsonDocument Delete(string db, BsonDocument c)
    {
        var name = c["delete"].AsString;
        var statements = c["deletes"].AsBsonArray;
        var ordered = c.GetValue("ordered", true).ToBoolean();
        var collection = _storage.Get(db, name, create: false);
        var errors = new BsonArray();
        var n = 0;
        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i].AsBsonDocument;
            try
            {
                if (collection is not null)
                    n += collection.Delete(statement["q"].AsBsonDocument, statement.GetValue("limit", 0).ToInt32());
            }
            catch (MongoError error)
            {
                errors.Add(error.ToWriteError(i));
                if (ordered)
                    break;
            }
        }
        return WriteReply(n, errors);
    }

    private static BsonDocument WriteReply(int n, BsonArray errors)
    {
        var reply = new BsonDocument("n", n);
        if (errors.Count > 0)
            reply["writeErrors"] = errors;
        reply["ok"] = 1.0;
        return reply;
    }

    private BsonDocument FindAndModify(string db, BsonDocument c)
    {
        var name = c[0].AsString;
        var remove = c.GetValue("remove", false).ToBoolean();
        var upsert = c.GetValue("upsert", false).ToBoolean();
        var update = c.GetValue("update", BsonNull.Value);
        if (update is BsonArray)
            throw MongoError.NotSupported("pipeline-style updates are not supported by KAONOMI.MongoEngine");
        if (remove && upsert)
            throw MongoError.FailedToParse("Cannot specify both an upsert and remove=true");
        var collection = _storage.Get(db, name, create: upsert);
        var lastError = new BsonDocument();
        BsonValue value = BsonNull.Value;
        if (collection is not null)
        {
            var (document, updatedExisting, upsertedId, n) = collection.FindAndModify(
                c.GetValue("query", BsonNull.Value) as BsonDocument, c.GetValue("sort", BsonNull.Value) as BsonDocument,
                update as BsonDocument, remove, upsert, c.GetValue("new", false).ToBoolean());
            lastError["n"] = n;
            if (!remove)
                lastError["updatedExisting"] = updatedExisting;
            if (upsertedId is not null)
                lastError["upserted"] = upsertedId;
            if (document is not null)
                value = Sorting.Project(document, c.GetValue("fields", BsonNull.Value) as BsonDocument);
        }
        else
            lastError["n"] = 0;
        return new BsonDocument { { "lastErrorObject", lastError }, { "value", value } };
    }

    // ── Collections, indexes, databases ──

    private BsonDocument ListCollections(string db, BsonDocument c, ClientConnection connection)
    {
        var nameOnly = c.GetValue("nameOnly", false).ToBoolean();
        var filter = c.GetValue("filter", BsonNull.Value) as BsonDocument;
        var entries = new List<BsonDocument>();
        foreach (var collection in _storage.Collections(db))
        {
            var entry = nameOnly
                ? new BsonDocument { { "name", collection.Name }, { "type", "collection" } }
                : new BsonDocument
                {
                    { "name", collection.Name },
                    { "type", "collection" },
                    { "options", collection.Options },
                    { "info", new BsonDocument { { "readOnly", false }, { "uuid", new BsonBinaryData(collection.Uuid, GuidRepresentation.Standard) } } },
                    { "idIndex", new BsonDocument { { "v", 2 }, { "key", new BsonDocument("_id", 1) }, { "name", "_id_" } } }
                };
            if (Filter.Matches(entry, filter))
                entries.Add(entry);
        }
        var batchSize = c.TryGetValue("cursor", out var cursor) && cursor is BsonDocument d && d.TryGetValue("batchSize", out var b) ? b.ToInt32() : DefaultFirstBatch;
        return CursorReply($"{db}.$cmd.listCollections", entries, batchSize, false, "firstBatch", connection);
    }

    private BsonDocument ListDatabases(BsonDocument c)
    {
        var nameOnly = c.GetValue("nameOnly", false).ToBoolean();
        var filter = c.GetValue("filter", BsonNull.Value) as BsonDocument;
        var databases = new BsonArray();
        long total = 0;
        foreach (var name in _storage.DatabaseNames())
        {
            var size = _storage.Collections(name).Sum(col => col.DataSize);
            total += size;
            var entry = nameOnly ? new BsonDocument("name", name) : new BsonDocument { { "name", name }, { "sizeOnDisk", size }, { "empty", size == 0 } };
            if (Filter.Matches(entry, filter))
                databases.Add(entry);
        }
        var reply = new BsonDocument("databases", databases);
        if (!nameOnly)
        {
            reply["totalSize"] = total;
            reply["totalSizeMb"] = total / (1024 * 1024);
        }
        return reply;
    }

    private BsonDocument CreateIndexes(string db, BsonDocument c)
    {
        var name = c["createIndexes"].AsString;
        var created = _storage.Get(db, name, create: false) is null;
        var collection = _storage.Get(db, name, create: true)!;
        var before = collection.Indexes.Count;
        var added = 0;
        foreach (var index in c["indexes"].AsBsonArray.OfType<BsonDocument>())
        {
            if (!index.Contains("key") || !index.Contains("name"))
                throw MongoError.FailedToParse("The 'key' and 'name' fields are required for an index specification");
            if (collection.CreateIndex(index))
                added++;
        }
        var reply = new BsonDocument
        {
            { "numIndexesBefore", before },
            { "numIndexesAfter", collection.Indexes.Count },
            { "createdCollectionAutomatically", created }
        };
        if (added == 0)
            reply["note"] = "all indexes already exist";
        return reply;
    }

    private BsonDocument ListIndexes(string db, BsonDocument c, ClientConnection connection)
    {
        var name = c["listIndexes"].AsString;
        var collection = _storage.Get(db, name, create: false)
            ?? throw MongoError.NamespaceNotFound($"ns does not exist: {db}.{name}");
        var batchSize = c.TryGetValue("cursor", out var cursor) && cursor is BsonDocument d && d.TryGetValue("batchSize", out var b) ? b.ToInt32() : DefaultFirstBatch;
        return CursorReply($"{db}.$cmd.listIndexes.{name}", collection.Indexes.Select(i => i.Raw).ToList(), batchSize, false, "firstBatch", connection);
    }

    private BsonDocument DropIndexes(string db, BsonDocument c)
    {
        var name = c[0].AsString;
        var collection = _storage.Get(db, name, create: false)
            ?? throw MongoError.NamespaceNotFound($"ns not found {db}.{name}");
        var before = collection.Indexes.Count;
        switch (c["index"])
        {
            case BsonString s:
                collection.DropIndex(s.Value);
                break;
            case BsonDocument key:
                collection.DropIndex(key);
                break;
            case BsonArray names:
                foreach (var n in names)
                    collection.DropIndex(n.AsString);
                break;
        }
        return new BsonDocument("nIndexesWas", before);
    }

    private BsonDocument CollStats(string db, BsonDocument c)
    {
        var name = c["collStats"].AsString;
        var collection = _storage.Get(db, name, create: false)
            ?? throw MongoError.NamespaceNotFound($"Collection [{db}.{name}] not found.");
        var size = collection.DataSize;
        var count = collection.Count;
        return new BsonDocument
        {
            { "ns", collection.Namespace }, { "size", size }, { "count", count }, { "avgObjSize", count == 0 ? 0 : size / count },
            { "storageSize", size }, { "nindexes", collection.Indexes.Count }, { "totalIndexSize", 0 }
        };
    }

    private BsonDocument DbStats(string db)
    {
        var collections = _storage.Collections(db);
        var size = collections.Sum(col => col.DataSize);
        return new BsonDocument
        {
            { "db", db }, { "collections", collections.Count }, { "views", 0 }, { "objects", collections.Sum(col => (long)col.Count) },
            { "dataSize", (double)size }, { "storageSize", (double)size }, { "indexes", collections.Sum(col => col.Indexes.Count) }, { "indexSize", 0.0 }
        };
    }
}
