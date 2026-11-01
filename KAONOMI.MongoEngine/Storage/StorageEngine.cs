using KAONOMI.MongoEngine.Configuration;
using KAONOMI.MongoEngine.Engine;
using MongoDB.Bson;

namespace KAONOMI.MongoEngine.Storage;

/// <summary>
/// The databases of the engine, in <c>storage.dbPath</c>: one folder per database, the collections in it. A database
/// and a collection exist as soon as something is written in them (as MongoDB); <c>dbPath/mongod.lock</c> is held
/// while the engine runs, so that a second engine on the same data stops at once.
/// </summary>
public sealed class StorageEngine : IDisposable
{
    private readonly EngineSettings _settings;
    private readonly EngineLog _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, Collection>> _databases = new(StringComparer.Ordinal);
    private readonly FileStream _dataLock;
    private readonly Timer _syncTimer;

    public string DbPath => _settings.DbPath;

    public StorageEngine(EngineSettings settings, EngineLog log)
    {
        _settings = settings;
        _log = log;
        Directory.CreateDirectory(settings.DbPath);
        try
        {
            _dataLock = new FileStream(Path.Combine(settings.DbPath, "mongod.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _dataLock.SetLength(0);
            _dataLock.Write(System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString()));
            _dataLock.Flush();
        }
        catch (IOException ex)
        {
            throw new DataPathInUseException(settings.DbPath, ex);
        }
        foreach (var folder in Directory.GetDirectories(settings.DbPath))
        {
            var database = Collection.NameFromFile(Path.GetFileName(folder));
            var collections = new Dictionary<string, Collection>(StringComparer.Ordinal);
            foreach (var meta in Directory.GetFiles(folder, "*.meta"))
            {
                var name = Collection.NameFromFile(Path.GetFileNameWithoutExtension(meta));
                collections[name] = Collection.Load(database, name, folder, settings.CompactAfter, log);
            }
            if (collections.Count > 0)
                _databases[database] = collections;
        }
        var interval = Math.Max(1, settings.CommitIntervalMs);
        _syncTimer = new Timer(_ => SyncAll(), null, interval, interval);
    }

    public int CollectionCount
    {
        get
        {
            lock (_lock)
                return _databases.Values.Sum(d => d.Count);
        }
    }

    /// <summary>The collection, or null when it does not exist (and <paramref name="create"/> is false).</summary>
    public Collection? Get(string database, string name, bool create, BsonDocument? options = null)
    {
        lock (_lock)
        {
            if (_databases.TryGetValue(database, out var collections) && collections.TryGetValue(name, out var collection))
                return collection;
            if (!create)
                return null;
            ValidateNames(database, name);
            if (!_databases.TryGetValue(database, out collections))
                _databases[database] = collections = new Dictionary<string, Collection>(StringComparer.Ordinal);
            collection = Collection.Create(database, name, Path.Combine(DbPath, Collection.FileName(database)), options, _settings.CompactAfter);
            collections[name] = collection;
            _log.Info("STORAGE", $"createCollection {collection.Namespace}");
            return collection;
        }
    }

    /// <summary><c>create</c>: a new collection, or <c>NamespaceExists</c>.</summary>
    public Collection Create(string database, string name, BsonDocument? options)
    {
        lock (_lock)
        {
            if (Get(database, name, create: false) is not null)
                throw MongoError.NamespaceExists($"Collection {database}.{name} already exists.");
            return Get(database, name, create: true, options)!;
        }
    }

    public bool Drop(string database, string name)
    {
        lock (_lock)
        {
            if (!_databases.TryGetValue(database, out var collections) || !collections.Remove(name, out var collection))
                return false;
            collection.Drop();
            if (collections.Count == 0)
                RemoveDatabase(database);
            _log.Info("STORAGE", $"dropCollection {database}.{name}");
            return true;
        }
    }

    public void DropDatabase(string database)
    {
        lock (_lock)
        {
            if (!_databases.Remove(database, out var collections))
                return;
            foreach (var collection in collections.Values)
                collection.Drop();
            RemoveDatabase(database);
            _log.Info("STORAGE", $"dropDatabase {database}");
        }
    }

    private void RemoveDatabase(string database)
    {
        _databases.Remove(database);
        var folder = Path.Combine(DbPath, Collection.FileName(database));
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }

    public List<string> DatabaseNames()
    {
        lock (_lock)
            return _databases.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public List<Collection> Collections(string database)
    {
        lock (_lock)
            return _databases.TryGetValue(database, out var collections)
                ? collections.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList()
                : [];
    }

    private static void ValidateNames(string database, string name)
    {
        if (database.Length == 0 || database.IndexOfAny(['/', '\\', '.', ' ', '"', '$', '\0']) >= 0 || database.Length > 63)
            throw MongoError.InvalidNamespace($"Invalid database name: '{database}'");
        if (name.Length == 0 || name.Contains('$') || name.Contains('\0') || name.StartsWith("system.", StringComparison.Ordinal) && name != "system.profile")
            throw MongoError.InvalidNamespace($"Invalid collection name: {name}");
    }

    private void SyncAll()
    {
        List<Collection> all;
        lock (_lock)
            all = _databases.Values.SelectMany(d => d.Values).ToList();
        foreach (var collection in all)
        {
            try
            {
                collection.Sync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _log.Error("STORAGE", $"{collection.Namespace}: the journal could not be written to disk: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _syncTimer.Dispose();
        lock (_lock)
        {
            foreach (var collection in _databases.Values.SelectMany(d => d.Values))
            {
                try
                {
                    collection.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Error("STORAGE", $"{collection.Namespace}: close failed: {ex.Message}");
                }
            }
            _databases.Clear();
        }
        _dataLock.Dispose();
    }
}

/// <summary><c>storage.dbPath</c> is used by another engine that is running.</summary>
public sealed class DataPathInUseException(string dbPath, Exception inner)
    : Exception($"storage.dbPath [{dbPath}] is used by another engine (mongod.lock is held)", inner);
