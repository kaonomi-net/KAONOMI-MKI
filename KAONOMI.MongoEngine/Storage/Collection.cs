using KAONOMI.MongoEngine.Engine;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace KAONOMI.MongoEngine.Storage;

/// <summary>An index of a collection: its name, its keys and whether two documents may share its key.</summary>
public sealed record IndexSpec(string Name, BsonDocument Key, bool Unique, BsonDocument Raw);

/// <summary>The result of an update command on one statement.</summary>
public sealed record UpdateResult(int Matched, int Modified, BsonValue? UpsertedId);

/// <summary>
/// A collection: its documents in memory in their natural order, its indexes, and on disk a snapshot
/// (<c>&lt;name&gt;.bson</c>) plus a journal of the writes since (<c>&lt;name&gt;.journal</c>). Every write is in
/// the journal before it is answered; a write is atomic (one lock per collection): the find of the documents to
/// change and their change happen without another write in between (the membership table of Orleans counts on it).
/// </summary>
public sealed class Collection : IDisposable
{
    public const int MaxDocumentSize = 16 * 1024 * 1024;

    private readonly object _sync = new();
    private readonly SortedDictionary<long, BsonDocument> _documents = new();
    private readonly Dictionary<string, long> _ids = new(StringComparer.Ordinal);
    private readonly List<IndexSpec> _indexes = new();
    private readonly string _folder;
    private readonly int _compactAfter;
    private FileStream? _journal;
    private long _nextSeq;
    private int _journalRecords;
    private bool _dirty, _dropped;

    public string Database { get; }
    public string Name { get; }
    public string Namespace => $"{Database}.{Name}";
    public Guid Uuid { get; private set; } = Guid.NewGuid();
    public BsonDocument Options { get; private set; } = new();

    private Collection(string database, string name, string folder, int compactAfter)
    {
        Database = database;
        Name = name;
        _folder = folder;
        _compactAfter = compactAfter;
    }

    private string SnapshotFile => Path.Combine(_folder, FileName(Name) + ".bson");
    private string JournalFile => Path.Combine(_folder, FileName(Name) + ".journal");
    private string MetaFile => Path.Combine(_folder, FileName(Name) + ".meta");

    /// <summary>Collection names are file names once the characters a file name cannot have are escaped.</summary>
    public static string FileName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c.ToString() : $"%{(int)c:X2}"));

    public static string NameFromFile(string file) =>
        System.Text.RegularExpressions.Regex.Replace(file, "%([0-9A-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    /// <summary>A new, empty collection (its meta file written at once).</summary>
    public static Collection Create(string database, string name, string folder, BsonDocument? options, int compactAfter)
    {
        Directory.CreateDirectory(folder);
        var collection = new Collection(database, name, folder, compactAfter) { Options = options ?? new BsonDocument() };
        collection._indexes.Add(IdIndex);
        collection.WriteMeta();
        collection.OpenJournal();
        return collection;
    }

    /// <summary>The collection saved in <paramref name="folder"/>: the snapshot, then the journal replayed.</summary>
    public static Collection Load(string database, string name, string folder, int compactAfter, EngineLog log)
    {
        var collection = new Collection(database, name, folder, compactAfter);
        if (File.Exists(collection.MetaFile))
        {
            var meta = BsonSerializer.Deserialize<BsonDocument>(File.ReadAllBytes(collection.MetaFile));
            collection.Uuid = meta["uuid"].AsGuid;
            collection.Options = meta.GetValue("options", new BsonDocument()).AsBsonDocument;
            foreach (var index in meta.GetValue("indexes", new BsonArray()).AsBsonArray.OfType<BsonDocument>())
                collection._indexes.Add(ToSpec(index));
        }
        if (!collection._indexes.Any(i => i.Name == "_id_"))
            collection._indexes.Insert(0, IdIndex);
        foreach (var (record, _) in ReadRecords(collection.SnapshotFile))
            collection.ApplyRecord(record);
        var replayed = 0;
        long good = 0;
        foreach (var (record, end) in ReadRecords(collection.JournalFile))
        {
            collection.ApplyRecord(record);
            replayed++;
            good = end;
        }
        if (File.Exists(collection.JournalFile) && new FileInfo(collection.JournalFile).Length > good)
        {
            log.Warn("STORAGE", $"{collection.Namespace}: the end of the journal is incomplete (a crash during a write): it is left out");
            using var truncate = new FileStream(collection.JournalFile, FileMode.Open);
            truncate.SetLength(good);
        }
        collection._journalRecords = replayed;
        collection.OpenJournal();
        if (replayed > 0)
            collection.Compact();
        return collection;
    }

    private static readonly IndexSpec IdIndex = new("_id_", new BsonDocument("_id", 1), true,
        new BsonDocument { { "v", 2 }, { "key", new BsonDocument("_id", 1) }, { "name", "_id_" } });

    private static IndexSpec ToSpec(BsonDocument raw) =>
        new(raw["name"].AsString, raw["key"].AsBsonDocument, raw.GetValue("unique", false).ToBoolean() || raw["name"].AsString == "_id_", raw);

    // ── Reads ──

    public int Count
    {
        get
        {
            lock (_sync)
                return _documents.Count;
        }
    }

    /// <summary>The documents matching <paramref name="filter"/>, in their natural order (the stored ones: not to be changed).</summary>
    public List<BsonDocument> Find(BsonDocument? filter)
    {
        lock (_sync)
        {
            if (TryIdLookup(filter, out var seq))
                return seq is { } s && Filter.Matches(_documents[s], filter) ? [_documents[s]] : [];
            return _documents.Values.Where(d => Filter.Matches(d, filter)).ToList();
        }
    }

    /// <summary>The fast path of <c>{_id: value}</c>: the document found by its id.</summary>
    private bool TryIdLookup(BsonDocument? filter, out long? seq)
    {
        seq = null;
        if (filter is null || !filter.TryGetValue("_id", out var id) || Filter.IsOperatorDocument(id) || id.BsonType == BsonType.RegularExpression)
            return false;
        seq = _ids.TryGetValue(BsonOrder.Key(id), out var s) ? s : null;
        return true;
    }

    public List<IndexSpec> Indexes
    {
        get
        {
            lock (_sync)
                return _indexes.ToList();
        }
    }

    public long DataSize
    {
        get
        {
            lock (_sync)
                return _documents.Values.Sum(d => (long)d.ToBson().Length);
        }
    }

    // ── Writes ──

    /// <summary>Inserts <paramref name="document"/> (an <c>_id</c> is given when it has none); returns its id.</summary>
    public BsonValue Insert(BsonDocument document)
    {
        lock (_sync)
        {
            CheckAlive();
            var stored = WithId(document);
            CheckSize(stored);
            CheckUnique(stored, ignoreSeq: null);
            var seq = _nextSeq++;
            Put(seq, stored);
            return stored["_id"];
        }
    }

    /// <summary>
    /// The <c>update</c> of one statement: the first matching document (or all with <paramref name="multi"/>)
    /// changed by <paramref name="update"/>; none matching and <paramref name="upsert"/>: a new document made of the
    /// equalities of the filter and the update (a duplicate key error when its <c>_id</c> exists already).
    /// </summary>
    public UpdateResult Update(BsonDocument filter, BsonDocument update, bool upsert, bool multi)
    {
        lock (_sync)
        {
            CheckAlive();
            if (multi && !Engine.Update.IsOperators(update))
                throw MongoError.FailedToParse("multi update is not supported for replacement-style update");
            var matched = MatchingSeqs(filter, multi ? int.MaxValue : 1);
            if (matched.Count == 0)
            {
                if (!upsert)
                    return new UpdateResult(0, 0, null);
                var created = Engine.Update.Apply(Filter.SeedFromFilter(filter), update, inserting: true);
                if (!Engine.Update.IsOperators(update) && filter.TryGetValue("_id", out var filterId) && !Filter.IsOperatorDocument(filterId))
                    created = new BsonDocument("_id", filterId).Merge(created, overwriteExistingElements: false);
                var id = Insert(created);
                return new UpdateResult(0, 0, id);
            }
            var modified = 0;
            foreach (var seq in matched)
            {
                var before = _documents[seq];
                var after = Engine.Update.Apply(before, update, inserting: false);
                if (after.Equals(before))
                    continue;
                CheckSize(after);
                CheckUnique(after, ignoreSeq: seq);
                Put(seq, after);
                modified++;
            }
            return new UpdateResult(matched.Count, modified, null);
        }
    }

    /// <summary>Deletes the matching documents (at most <paramref name="limit"/>, 0: all); returns how many.</summary>
    public int Delete(BsonDocument? filter, int limit)
    {
        lock (_sync)
        {
            CheckAlive();
            var seqs = MatchingSeqs(filter, limit == 0 ? int.MaxValue : limit);
            foreach (var seq in seqs)
                Remove(seq);
            return seqs.Count;
        }
    }

    /// <summary>
    /// <c>findAndModify</c>: the first document matching (in the order of <paramref name="sort"/>) removed or
    /// updated, atomically. Returns the document before or after (<paramref name="returnNew"/>), and what was done.
    /// </summary>
    public (BsonDocument? Value, bool UpdatedExisting, BsonValue? UpsertedId, int N) FindAndModify(BsonDocument? filter, BsonDocument? sort,
        BsonDocument? update, bool remove, bool upsert, bool returnNew)
    {
        lock (_sync)
        {
            CheckAlive();
            var candidates = _documents.Where(kv => Filter.Matches(kv.Value, filter));
            if (sort is not null)
                candidates = candidates.OrderBy(kv => (BsonValue)kv.Value, Sorting.Comparer(sort));
            var found = candidates.Select(kv => (KeyValuePair<long, BsonDocument>?)kv).FirstOrDefault();
            if (remove)
            {
                if (found is not { } f)
                    return (null, false, null, 0);
                Remove(f.Key);
                return (f.Value, false, null, 1);
            }
            if (update is null)
                throw MongoError.FailedToParse("Either an update or remove=true must be specified");
            if (found is not { } existing)
            {
                if (!upsert)
                    return (null, false, null, 0);
                var created = Engine.Update.Apply(Filter.SeedFromFilter(filter), update, inserting: true);
                var id = Insert(created);
                return (returnNew ? _documents[_ids[BsonOrder.Key(id)]] : null, false, id, 1);
            }
            var after = Engine.Update.Apply(existing.Value, update, inserting: false);
            if (!after.Equals(existing.Value))
            {
                CheckSize(after);
                CheckUnique(after, ignoreSeq: existing.Key);
                Put(existing.Key, after);
            }
            return (returnNew ? after : existing.Value, true, null, 1);
        }
    }

    private List<long> MatchingSeqs(BsonDocument? filter, int limit)
    {
        if (TryIdLookup(filter, out var seq))
            return seq is { } s && Filter.Matches(_documents[s], filter) ? [s] : [];
        var result = new List<long>();
        foreach (var (s, document) in _documents)
        {
            if (result.Count >= limit)
                break;
            if (Filter.Matches(document, filter))
                result.Add(s);
        }
        return result;
    }

    /// <summary>The document with its <c>_id</c> first (a new ObjectId when it has none), as MongoDB stores it.</summary>
    private static BsonDocument WithId(BsonDocument document)
    {
        if (document.TryGetValue("_id", out var id))
        {
            if (id.BsonType is BsonType.Array or BsonType.RegularExpression || id.BsonType == BsonType.Undefined)
                throw new MongoError(53, "InvalidIdField", $"The '_id' value cannot be of type {id.BsonType.ToString().ToLowerInvariant()}");
            if (document.GetElement(0).Name == "_id")
                return document.DeepClone().AsBsonDocument;
            var ordered = new BsonDocument("_id", id.DeepClone());
            foreach (var element in document.Where(e => e.Name != "_id"))
                ordered.Add(element.Name, element.Value.DeepClone());
            return ordered;
        }
        var withId = new BsonDocument("_id", ObjectId.GenerateNewId());
        foreach (var element in document)
            withId.Add(element.Name, element.Value.DeepClone());
        return withId;
    }

    private static void CheckSize(BsonDocument document)
    {
        var size = document.ToBson().Length;
        if (size > MaxDocumentSize)
            throw new MongoError(10334, "BSONObjectTooLarge", $"object to insert too large. size in bytes: {size}, max size: {MaxDocumentSize}");
    }

    private void CheckUnique(BsonDocument document, long? ignoreSeq)
    {
        var idKey = BsonOrder.Key(document["_id"]);
        if (_ids.TryGetValue(idKey, out var other) && other != ignoreSeq)
            throw MongoError.DuplicateKey(Namespace, "_id_", new BsonDocument("_id", 1), new BsonDocument("_id", document["_id"]));
        foreach (var index in _indexes.Where(i => i.Unique && i.Name != "_id_"))
        {
            var key = IndexKey(document, index);
            var keyText = BsonOrder.Key(key);
            foreach (var (seq, existing) in _documents)
            {
                if (seq == ignoreSeq)
                    continue;
                if (BsonOrder.Key(IndexKey(existing, index)) == keyText)
                    throw MongoError.DuplicateKey(Namespace, index.Name, index.Key, key);
            }
        }
    }

    private static BsonDocument IndexKey(BsonDocument document, IndexSpec index)
    {
        var key = new BsonDocument();
        foreach (var field in index.Key)
            key[field.Name] = Paths.Get(document, field.Name) ?? BsonNull.Value;
        return key;
    }

    private void Put(long seq, BsonDocument document)
    {
        if (_documents.TryGetValue(seq, out var previous))
            _ids.Remove(BsonOrder.Key(previous["_id"]));
        _documents[seq] = document;
        _ids[BsonOrder.Key(document["_id"])] = seq;
        Journal(new BsonDocument { { "o", "p" }, { "s", seq }, { "d", document } });
    }

    private void Remove(long seq)
    {
        if (!_documents.Remove(seq, out var document))
            return;
        _ids.Remove(BsonOrder.Key(document["_id"]));
        Journal(new BsonDocument { { "o", "r" }, { "s", seq } });
    }

    private void ApplyRecord(BsonDocument record)
    {
        var seq = record["s"].ToInt64();
        _nextSeq = Math.Max(_nextSeq, seq + 1);
        if (record["o"].AsString == "p")
        {
            if (_documents.TryGetValue(seq, out var previous))
                _ids.Remove(BsonOrder.Key(previous["_id"]));
            var document = record["d"].AsBsonDocument;
            _documents[seq] = document;
            _ids[BsonOrder.Key(document["_id"])] = seq;
        }
        else if (_documents.Remove(seq, out var removed))
            _ids.Remove(BsonOrder.Key(removed["_id"]));
    }

    // ── Indexes ──

    /// <summary>Adds the index (true), or finds it already there (false); a unique index must hold for the documents.</summary>
    public bool CreateIndex(BsonDocument raw)
    {
        lock (_sync)
        {
            CheckAlive();
            var spec = ToSpec(raw);
            var existing = _indexes.FirstOrDefault(i => i.Name == spec.Name);
            if (existing is not null)
            {
                if (!existing.Key.Equals(spec.Key))
                    throw new MongoError(86, "IndexKeySpecsConflict",
                        $"An existing index has the same name as the requested index. Requested index: {raw.ToJson()}, existing index: {existing.Raw.ToJson()}");
                return false;
            }
            if (_indexes.FirstOrDefault(i => i.Key.Equals(spec.Key)) is { } sameKey)
            {
                if (sameKey.Unique != spec.Unique)
                    throw new MongoError(85, "IndexOptionsConflict", $"An equivalent index already exists with a different name and options. Requested index: {raw.ToJson()}, existing index: {sameKey.Raw.ToJson()}");
                return false;
            }
            if (spec.Unique)
            {
                var seen = new HashSet<string>();
                foreach (var document in _documents.Values)
                {
                    var key = IndexKey(document, spec);
                    if (!seen.Add(BsonOrder.Key(key)))
                        throw MongoError.DuplicateKey(Namespace, spec.Name, spec.Key, key);
                }
            }
            var stored = new BsonDocument { { "v", 2 }, { "key", spec.Key }, { "name", spec.Name } };
            foreach (var element in raw.Where(e => e.Name is not ("v" or "key" or "name" or "ns")))
                stored[element.Name] = element.Value;
            _indexes.Add(spec with { Raw = stored });
            WriteMeta();
            return true;
        }
    }

    /// <summary>Drops an index by name, or every index but <c>_id_</c> for <c>*</c>.</summary>
    public void DropIndex(string name)
    {
        lock (_sync)
        {
            if (name == "_id_")
                throw new MongoError(72, "InvalidOptions", "cannot drop _id index");
            if (name == "*")
                _indexes.RemoveAll(i => i.Name != "_id_");
            else if (_indexes.RemoveAll(i => i.Name == name) == 0)
                throw new MongoError(27, "IndexNotFound", $"index not found with name [{name}]");
            WriteMeta();
        }
    }

    public void DropIndex(BsonDocument key)
    {
        lock (_sync)
        {
            var index = _indexes.FirstOrDefault(i => i.Key.Equals(key))
                ?? throw new MongoError(27, "IndexNotFound", $"can't find index with key: {key.ToJson()}");
            DropIndex(index.Name);
        }
    }

    // ── Disk ──

    private void Journal(BsonDocument record)
    {
        _journal!.Write(record.ToBson());
        _journal.Flush(flushToDisk: false);
        _dirty = true;
        if (++_journalRecords >= _compactAfter)
            Compact();
    }

    /// <summary>Forces the journal to the disk (every <c>storage.journal.commitIntervalMs</c>).</summary>
    public void Sync()
    {
        lock (_sync)
        {
            if (_dirty && _journal is not null && !_dropped)
            {
                _journal.Flush(flushToDisk: true);
                _dirty = false;
            }
        }
    }

    /// <summary>Writes the documents as a new snapshot and empties the journal.</summary>
    public void Compact()
    {
        lock (_sync)
        {
            if (_dropped)
                return;
            var temp = SnapshotFile + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                foreach (var (seq, document) in _documents)
                    stream.Write(new BsonDocument { { "o", "p" }, { "s", seq }, { "d", document } }.ToBson());
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, SnapshotFile, overwrite: true);
            _journal!.SetLength(0);
            _journal.Flush(flushToDisk: true);
            _journalRecords = 0;
            _dirty = false;
        }
    }

    private void WriteMeta()
    {
        var meta = new BsonDocument
        {
            { "name", Name },
            { "uuid", new BsonBinaryData(Uuid, GuidRepresentation.Standard) },
            { "options", Options },
            { "indexes", new BsonArray(_indexes.Select(i => i.Raw)) }
        };
        var temp = MetaFile + ".tmp";
        File.WriteAllBytes(temp, meta.ToBson());
        File.Move(temp, MetaFile, overwrite: true);
    }

    private void OpenJournal()
    {
        Directory.CreateDirectory(_folder);
        // Not FileMode.Append: .NET refuses SetLength below the length the file had when it was opened, and Compact()
        // empties the journal after a load that replayed records (a restart after a crash).
        _journal = new FileStream(JournalFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        _journal.Seek(0, SeekOrigin.End);
    }

    private static IEnumerable<(BsonDocument Record, long End)> ReadRecords(string file)
    {
        if (!File.Exists(file))
            yield break;
        var bytes = File.ReadAllBytes(file);
        var offset = 0;
        while (offset + 4 <= bytes.Length)
        {
            var length = BitConverter.ToInt32(bytes, offset);
            if (length < 5 || offset + length > bytes.Length)
                yield break;
            BsonDocument? record;
            try
            {
                using var reader = new BsonBinaryReader(new MemoryStream(bytes, offset, length));
                record = BsonSerializer.Deserialize<BsonDocument>(reader);
            }
            catch (Exception)
            {
                yield break;
            }
            offset += length;
            yield return (record, offset);
        }
    }

    private void CheckAlive()
    {
        if (_dropped)
            throw MongoError.NamespaceNotFound($"Collection {Namespace} was dropped");
    }

    /// <summary>Deletes the files of the collection (<c>drop</c>).</summary>
    public void Drop()
    {
        lock (_sync)
        {
            _dropped = true;
            _journal?.Dispose();
            foreach (var file in new[] { SnapshotFile, JournalFile, MetaFile })
                if (File.Exists(file))
                    File.Delete(file);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_dropped || _journal is null)
                return;
            Compact();
            _journal.Dispose();
            _journal = null;
        }
    }
}
