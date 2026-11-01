using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using KAONOMI.SearchEngine.Configuration;
using KAONOMI.SearchEngine.Http;
using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = System.IO.Directory;

namespace KAONOMI.SearchEngine.Engine;

/// <summary>The result of a write (index, create, delete) of a document.</summary>
public sealed record WriteResult(string Id, long Version, long SeqNo, string Result, bool ForcedRefresh);

/// <summary>A document read by id: its version, sequence number and source (null: not found).</summary>
public sealed record StoredDocument(string Id, long Version, long SeqNo, byte[] Source);

/// <summary>
/// One index: a Lucene index on disk (one shard, no replica), its mapping and settings in <c>meta.json</c>. The writes
/// are searchable after the next refresh (every <c>index.refresh_interval</c>, or <c>?refresh</c>), but a read by id
/// sees them at once (realtime, as Elasticsearch); the commits to disk happen every <c>index.translog.sync_interval</c>
/// and when the engine stops.
/// </summary>
public sealed class EsIndex : IDisposable
{
    public string Name { get; }
    public string Uuid { get; }
    public long CreationDate { get; }
    public IndexMapping Mapping { get; }
    public int NumberOfReplicas { get; }
    public int MaxResultWindow { get; }
    public string Folder { get; }
    public Analyzer Analyzer { get; } = new EsStandardAnalyzer();

    /// <summary>The settings given at the creation of the index, written back by <c>GET /index/_settings</c>.</summary>
    public JsonObject ExtraSettings { get; }

    private readonly FSDirectory _directory;
    private readonly IndexWriter _writer;
    private readonly SearcherManager _searchers;
    private readonly object[] _stripes = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private readonly object _refreshLock = new();
    private readonly object _metaLock = new();
    private readonly Timer _refreshTimer, _commitTimer;
    private volatile ConcurrentDictionary<string, LiveEntry> _live = new(StringComparer.Ordinal);
    private volatile ConcurrentDictionary<string, LiveEntry> _refreshing = new(StringComparer.Ordinal);
    private long _seqNo;
    private volatile bool _dirty, _uncommitted, _disposed;

    /// <summary>A document written since the last refresh (null source: deleted).</summary>
    private sealed record LiveEntry(long Version, long SeqNo, byte[]? Source);

    private EsIndex(string folder, string name, string uuid, long creationDate, IndexMapping mapping, int replicas,
        int maxResultWindow, JsonObject extraSettings, EngineSettings settings, bool create)
    {
        Folder = folder;
        Name = name;
        Uuid = uuid;
        CreationDate = creationDate;
        Mapping = mapping;
        NumberOfReplicas = replicas;
        MaxResultWindow = maxResultWindow;
        ExtraSettings = extraSettings;
        _directory = FSDirectory.Open(Path.Combine(folder, "lucene"));
        var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, Analyzer)
        {
            OpenMode = create ? OpenMode.CREATE : OpenMode.CREATE_OR_APPEND,
            Similarity = new EsSimilarity(),
            RAMBufferSizeMB = settings.RamBufferMb
        };
        _writer = new IndexWriter(_directory, config);
        _seqNo = -1;
        if (!create && DirectoryReader.IndexExists(_directory))
        {
            var commit = DirectoryReader.ListCommits(_directory).LastOrDefault();
            if (commit?.UserData is { } data && data.TryGetValue("seq_no", out var seq) && long.TryParse(seq, out var n))
                _seqNo = n;
        }
        if (create)
            _writer.Commit();
        _searchers = new SearcherManager(_writer, true, new SimilarityFactory());
        Mapping.Changed += SaveMeta;

        var refresh = settings.RefreshInterval;
        _refreshTimer = new Timer(_ => { if (_dirty) TryBackground(Refresh); }, null,
            refresh == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : refresh, refresh);
        var sync = settings.SyncInterval;
        _commitTimer = new Timer(_ => { if (_uncommitted) TryBackground(Commit); }, null, sync, sync);
    }

    /// <summary>The searchers, with the scores of Elasticsearch.</summary>
    private sealed class SimilarityFactory : SearcherFactory
    {
        public override IndexSearcher NewSearcher(IndexReader reader) => new(reader) { Similarity = new EsSimilarity() };
    }

    /// <summary>Creates a new index in <paramref name="folder"/>.</summary>
    public static EsIndex Create(string folder, string name, JsonElement? body, EngineSettings settings)
    {
        Directory.CreateDirectory(folder);
        var mapping = new IndexMapping();
        var replicas = settings.NumberOfReplicas;
        var window = settings.MaxResultWindow;
        var extra = new JsonObject();
        if (body is { ValueKind: JsonValueKind.Object } b)
        {
            if (b.TryGetProperty("mappings", out var mappings))
                mapping.Merge(mappings);
            if (b.TryGetProperty("settings", out var s))
                ReadSettings(s, ref replicas, ref window, extra);
        }
        var index = new EsIndex(folder, name, Guid.NewGuid().ToString("N")[..22], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            mapping, replicas, window, extra, settings, create: true);
        index.SaveMeta();
        return index;
    }

    /// <summary>Opens the index saved in <paramref name="folder"/>.</summary>
    public static EsIndex Open(string folder, EngineSettings settings)
    {
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "meta.json")))!.AsObject();
        var mapping = new IndexMapping();
        if (meta["mappings"] is JsonObject m)
        {
            using var json = JsonDocument.Parse(m.ToJsonString());
            mapping.Merge(json.RootElement);
        }
        var replicas = meta["number_of_replicas"]?.GetValue<int>() ?? settings.NumberOfReplicas;
        var window = meta["max_result_window"]?.GetValue<int>() ?? settings.MaxResultWindow;
        var extra = meta["settings"] as JsonObject ?? new JsonObject();
        return new EsIndex(folder, meta["name"]!.GetValue<string>(), meta["uuid"]!.GetValue<string>(),
            meta["creation_date"]!.GetValue<long>(), mapping, replicas, window, (JsonObject)extra.DeepClone(), settings, create: false);
    }

    private static void ReadSettings(JsonElement s, ref int replicas, ref int window, JsonObject extra)
    {
        // {"settings": {"index": {"number_of_replicas": 0}}}, {"settings": {"number_of_replicas": 0}} or dotted names.
        void Read(JsonElement element, string prefix, ref int r, ref int w)
        {
            foreach (var p in element.EnumerateObject())
            {
                var key = prefix + p.Name;
                if (p.Value.ValueKind == JsonValueKind.Object)
                {
                    Read(p.Value, key + ".", ref r, ref w);
                    continue;
                }
                var name = key.StartsWith("index.", StringComparison.Ordinal) ? key[6..] : key;
                switch (name)
                {
                    case "number_of_replicas":
                        r = int.Parse(p.Value.ToString());
                        break;
                    case "max_result_window":
                        w = int.Parse(p.Value.ToString());
                        break;
                    default:
                        extra[name] = p.Value.ToString();
                        break;
                }
            }
        }
        Read(s, string.Empty, ref replicas, ref window);
    }

    private void SaveMeta()
    {
        lock (_metaLock)
        {
            if (_disposed)
                return;
            var meta = new JsonObject
            {
                ["name"] = Name,
                ["uuid"] = Uuid,
                ["creation_date"] = CreationDate,
                ["number_of_replicas"] = NumberOfReplicas,
                ["max_result_window"] = MaxResultWindow,
                ["settings"] = ExtraSettings.DeepClone(),
                ["mappings"] = Mapping.ToJson()
            };
            var file = Path.Combine(Folder, "meta.json");
            var temp = file + ".tmp";
            File.WriteAllText(temp, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, overwrite: true);
        }
    }

    // ── Writes ──

    /// <summary>
    /// Indexes <paramref name="source"/> as the document <paramref name="id"/> (null: a new id). With
    /// <paramref name="createOnly"/>, a document that exists already is a conflict (409).
    /// </summary>
    public WriteResult Index(string? id, byte[] source, bool createOnly, string? refresh)
    {
        var autoId = id is null;
        id ??= NewId();
        CheckId(id);
        lock (Stripe(id))
        {
            var existing = autoId ? null : Lookup(id);
            if (createOnly && existing?.Source is not null)
                throw new EsException(409, "version_conflict_engine_exception",
                    $"[{id}]: version conflict, document already exists (current version [{existing.Version}])",
                    ("index_uuid", Uuid), ("shard", "0"), ("index", Name));
            var version = existing is null ? 1 : existing.Version + 1;
            var seqNo = Interlocked.Increment(ref _seqNo);
            var document = DocumentParser.Parse(Mapping, id, source, version, seqNo);
            try
            {
                if (autoId)
                    _writer.AddDocument(document);
                else
                    _writer.UpdateDocument(new Term(DocumentParser.IdField, id), document);
            }
            catch (ArgumentException ex)
            {
                throw new EsException(400, "illegal_argument_exception", ex.Message);
            }
            _live[id] = new LiveEntry(version, seqNo, source);
            MarkWritten();
            var created = existing?.Source is null;
            return new WriteResult(id, version, seqNo, created ? "created" : "updated", ApplyRefresh(refresh));
        }
    }

    /// <summary>
    /// Adds a document copied from another Elasticsearch to a new index (version 1, no lookup of an older version, not
    /// kept in memory for the realtime reads): the fast path of the import.
    /// </summary>
    public void Import(string id, byte[] source)
    {
        CheckId(id);
        var seqNo = Interlocked.Increment(ref _seqNo);
        _writer.AddDocument(DocumentParser.Parse(Mapping, id, source, 1, seqNo));
        MarkWritten();
    }

    /// <summary>Deletes the document <paramref name="id"/>: <c>deleted</c>, or <c>not_found</c>.</summary>
    public WriteResult Delete(string id, string? refresh)
    {
        CheckId(id);
        lock (Stripe(id))
        {
            var existing = Lookup(id);
            var found = existing?.Source is not null;
            var version = existing is null ? 1 : existing.Version + 1;
            var seqNo = Interlocked.Increment(ref _seqNo);
            if (found)
                _writer.DeleteDocuments(new Term(DocumentParser.IdField, id));
            _live[id] = new LiveEntry(version, seqNo, null);
            MarkWritten();
            return new WriteResult(id, version, seqNo, found ? "deleted" : "not_found", ApplyRefresh(refresh));
        }
    }

    /// <summary>
    /// The partial update of a document (<c>_update</c>): <paramref name="change"/> merged into its source, or
    /// <paramref name="upsert"/> when it does not exist (null: 404).
    /// </summary>
    public WriteResult Update(string id, JsonObject change, JsonObject? upsert, string? refresh)
    {
        lock (Stripe(id))
        {
            var existing = Get(id);
            JsonObject merged;
            if (existing is null)
            {
                merged = upsert ?? throw new EsException(404, "document_missing_exception", $"[_doc][{id}]: document missing",
                    ("index_uuid", Uuid), ("shard", "0"), ("index", Name));
            }
            else
            {
                merged = JsonNode.Parse(existing.Source)!.AsObject();
                var before = merged.ToJsonString();
                Merge(merged, change);
                if (merged.ToJsonString() == before)
                    return new WriteResult(id, existing.Version, existing.SeqNo, "noop", false);
            }
            var result = Index(id, JsonSerializer.SerializeToUtf8Bytes(merged), createOnly: false, refresh);
            return result;
        }
    }

    private static void Merge(JsonObject target, JsonObject change)
    {
        foreach (var (key, value) in change.ToList())
        {
            if (value is JsonObject sub && target[key] is JsonObject existing)
                Merge(existing, sub);
            else
                target[key] = value?.DeepClone();
        }
    }

    private object Stripe(string id) => _stripes[(id.GetHashCode() & 0x7fffffff) % _stripes.Length];

    private void MarkWritten()
    {
        _dirty = true;
        _uncommitted = true;
    }

    /// <summary><c>?refresh=true</c> (or empty) and <c>wait_for</c>: the write is searchable before the answer.</summary>
    private bool ApplyRefresh(string? refresh)
    {
        if (refresh is null || refresh == "false")
            return false;
        Refresh();
        return refresh != "wait_for";
    }

    private static void CheckId(string id)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(id) > 512)
            throw new EsException(400, "action_request_validation_exception",
                $"Validation Failed: 1: id [{id}] is too long, must be no longer than 512 bytes but was: {System.Text.Encoding.UTF8.GetByteCount(id)};");
        if (id.Length == 0)
            throw new EsException(400, "action_request_validation_exception", "Validation Failed: 1: an id must be provided if version type or value are set;");
    }

    /// <summary>A new id as Elasticsearch makes them: 20 characters of URL-safe base 64.</summary>
    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[15];
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        BitConverter.TryWriteBytes(bytes, time);
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes[6..]);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
    }

    // ── Reads ──

    /// <summary>The document <paramref name="id"/> (realtime: the last write, even not refreshed), or null.</summary>
    public StoredDocument? Get(string id)
    {
        var entry = Lookup(id);
        return entry?.Source is null ? null : new StoredDocument(id, entry.Version, entry.SeqNo, entry.Source);
    }

    private LiveEntry? Lookup(string id)
    {
        if (_live.TryGetValue(id, out var live) || _refreshing.TryGetValue(id, out live))
            return live;
        var searcher = _searchers.Acquire();
        try
        {
            var top = searcher.Search(new TermQuery(new Term(DocumentParser.IdField, id)), 1);
            if (top.TotalHits == 0)
                return null;
            var doc = searcher.Doc(top.ScoreDocs[0].Doc);
            return new LiveEntry(doc.GetField(DocumentParser.VersionField)?.GetInt64Value() ?? 1,
                doc.GetField(DocumentParser.SeqNoField)?.GetInt64Value() ?? 0,
                doc.GetBinaryValue(DocumentParser.SourceField).Bytes);
        }
        finally
        {
            _searchers.Release(searcher);
        }
    }

    /// <summary>Runs <paramref name="search"/> on the searcher of the last refresh.</summary>
    public T Search<T>(Func<IndexSearcher, T> search)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var searcher = _searchers.Acquire();
        try
        {
            return search(searcher);
        }
        finally
        {
            _searchers.Release(searcher);
        }
    }

    /// <summary>The number of documents (searchable ones) and deleted ones not merged away yet.</summary>
    public (int Docs, int Deleted) Count() => Search(s => (s.IndexReader.NumDocs, s.IndexReader.NumDeletedDocs));

    /// <summary>The size of the index on disk, in bytes.</summary>
    public long SizeInBytes()
    {
        try
        {
            return new DirectoryInfo(Path.Combine(Folder, "lucene")).EnumerateFiles().Sum(f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    // ── Refresh, commit, close ──

    /// <summary>Makes the writes searchable.</summary>
    public void Refresh()
    {
        lock (_refreshLock)
        {
            if (_disposed)
                return;
            _dirty = false;
            _refreshing = _live;
            _live = new ConcurrentDictionary<string, LiveEntry>(StringComparer.Ordinal);
            _searchers.MaybeRefreshBlocking();
            _refreshing = new ConcurrentDictionary<string, LiveEntry>(StringComparer.Ordinal);
        }
    }

    /// <summary>Writes the index to disk (a Lucene commit with the last sequence number).</summary>
    public void Commit()
    {
        lock (_refreshLock)
        {
            if (_disposed)
                return;
            _uncommitted = false;
            _writer.SetCommitData(new Dictionary<string, string> { ["seq_no"] = Interlocked.Read(ref _seqNo).ToString() });
            _writer.Commit();
        }
    }

    private void TryBackground(Action action)
    {
        try
        {
            action();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{DateTime.Now:O}][ERROR][index][{Name}] {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_refreshLock)
        {
            if (_disposed)
                return;
            _refreshTimer.Dispose();
            _commitTimer.Dispose();
            _writer.SetCommitData(new Dictionary<string, string> { ["seq_no"] = Interlocked.Read(ref _seqNo).ToString() });
            _writer.Commit();
            _disposed = true;
            _searchers.Dispose();
            _writer.Dispose();
            _directory.Dispose();
            Analyzer.Dispose();
        }
    }
}
