using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KAONOMI.SearchEngine.Configuration;
using KAONOMI.SearchEngine.Http;

namespace KAONOMI.SearchEngine.Engine;

/// <summary>
/// The indices of the node, in <c>path.data/indices/&lt;name&gt;</c>: opened at start, created on the first write when
/// <c>action.auto_create_index</c> is on, found by name or by expression (<c>a,b</c>, <c>log*</c>, <c>_all</c>).
/// </summary>
public sealed class IndexManager : IDisposable
{
    private readonly EngineSettings _settings;
    private readonly EngineLog _log;
    private readonly FileStream _dataLock;
    private readonly object _lock = new();
    private volatile Dictionary<string, EsIndex> _indices = new(StringComparer.Ordinal);

    public string NodeId { get; }
    public string ClusterUuid { get; }

    public IndexManager(EngineSettings settings)
    {
        _settings = settings;
        _log = EngineLog.For(settings);
        Directory.CreateDirectory(IndicesFolder);
        _dataLock = LockDataPath(settings.DataPath);
        var nodeFile = Path.Combine(settings.DataPath, "node.json");
        if (File.Exists(nodeFile))
        {
            var node = JsonNode.Parse(File.ReadAllText(nodeFile))!;
            NodeId = node["node_id"]!.GetValue<string>();
            ClusterUuid = node["cluster_uuid"]!.GetValue<string>();
        }
        else
        {
            NodeId = RandomId();
            ClusterUuid = RandomId();
            File.WriteAllText(nodeFile, new JsonObject { ["node_id"] = NodeId, ["cluster_uuid"] = ClusterUuid }.ToJsonString());
        }
        var opened = new Dictionary<string, EsIndex>(StringComparer.Ordinal);
        foreach (var folder in Directory.GetDirectories(IndicesFolder))
        {
            if (!File.Exists(Path.Combine(folder, "meta.json")))
                continue;
            var index = EsIndex.Open(folder, settings);
            opened[index.Name] = index;
        }
        _indices = opened;
    }

    private string IndicesFolder => Path.Combine(_settings.DataPath, "indices");

    private static string RandomId() => Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_')[..22];

    public IReadOnlyCollection<EsIndex> All => _indices.Values.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();

    public EsIndex? Find(string name) => _indices.TryGetValue(name, out var index) ? index : null;

    /// <summary>The index <paramref name="name"/>, or a 404 <c>index_not_found_exception</c>.</summary>
    public EsIndex Get(string name, string resourceType = "index_expression") =>
        Find(name) ?? throw EsException.IndexNotFound(name, resourceType);

    /// <summary>The index of a write: created when it does not exist (and the auto creation is on).</summary>
    public EsIndex GetForWrite(string name)
    {
        if (Find(name) is { } index)
            return index;
        if (!_settings.AutoCreateIndex)
            throw EsException.IndexNotFound(name);
        lock (_lock)
            return Find(name) ?? CreateLocked(name, null);
    }

    /// <summary>Creates the index (<c>PUT /name</c>): 400 <c>resource_already_exists_exception</c> when it exists.</summary>
    public EsIndex Create(string name, JsonElement? body)
    {
        lock (_lock)
        {
            if (Find(name) is { } existing)
                throw new EsException(400, "resource_already_exists_exception", $"index [{name}/{existing.Uuid}] already exists",
                    ("index_uuid", existing.Uuid), ("index", name));
            return CreateLocked(name, body);
        }
    }

    private EsIndex CreateLocked(string name, JsonElement? body)
    {
        Validate(name);
        var folder = Path.Combine(IndicesFolder, FolderName(name));
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        var index = EsIndex.Create(folder, name, body, _settings);
        _indices = new Dictionary<string, EsIndex>(_indices, StringComparer.Ordinal) { [name] = index };
        _log.Info("cluster.metadata", $"[{name}] creating index");
        return index;
    }

    /// <summary>Deletes the indices of an expression (<c>DELETE /a,b</c>).</summary>
    public void Delete(string expression)
    {
        var targets = Resolve(expression, allowNoIndices: false);
        lock (_lock)
        {
            var copy = new Dictionary<string, EsIndex>(_indices, StringComparer.Ordinal);
            foreach (var index in targets)
                copy.Remove(index.Name);
            _indices = copy;
        }
        foreach (var index in targets)
        {
            index.Dispose();
            Directory.Delete(index.Folder, recursive: true);
            _log.Info("cluster.metadata", $"[{index.Name}] deleting index");
        }
    }

    /// <summary>
    /// The indices of an expression: names separated by commas, <c>*</c> wildcards, <c>_all</c>, <c>-name</c> to leave
    /// one out. A missing concrete name is a 404; a wildcard matching nothing gives no index.
    /// </summary>
    public List<EsIndex> Resolve(string? expression, bool allowNoIndices = true, string resourceType = "index_or_alias")
    {
        var indices = _indices;
        if (string.IsNullOrEmpty(expression) || expression is "_all" or "*")
            return indices.Values.Where(i => !i.Name.StartsWith('.')).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        var result = new List<EsIndex>();
        foreach (var raw in expression.Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                continue;
            if (part.StartsWith('-') && result.Count > 0)
            {
                var pattern = ToRegex(part[1..]);
                result.RemoveAll(i => pattern.IsMatch(i.Name));
                continue;
            }
            if (part.Contains('*'))
            {
                var pattern = ToRegex(part);
                result.AddRange(indices.Values.Where(i => pattern.IsMatch(i.Name) && (part.StartsWith('.') || !i.Name.StartsWith('.')) && !result.Contains(i)));
                continue;
            }
            if (!indices.TryGetValue(part, out var index))
                throw EsException.IndexNotFound(part, resourceType);
            if (!result.Contains(index))
                result.Add(index);
        }
        if (result.Count == 0 && !allowNoIndices)
            throw EsException.IndexNotFound(expression, resourceType);
        return result.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    private static Regex ToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant);

    /// <summary>The rules of Elasticsearch for the names of indices.</summary>
    public static void Validate(string name)
    {
        string? problem = null;
        if (name.Length == 0)
            problem = "must not be empty";
        else if (name != name.ToLowerInvariant())
            problem = "must be lowercase";
        else if (name is "." or "..")
            problem = "must not be '.' or '..'";
        else if (name[0] is '_' or '-' or '+')
            problem = "must not start with '_', '-', or '+'";
        else if (name.IndexOfAny(['\\', '/', '*', '?', '"', '<', '>', '|', ' ', ',', '#', ':']) >= 0)
            problem = "must not contain the following characters [ , \", *, \\, <, |, ,, >, /, ?]";
        else if (System.Text.Encoding.UTF8.GetByteCount(name) > 255)
            problem = "index name is too long, (" + System.Text.Encoding.UTF8.GetByteCount(name) + " > 255)";
        if (problem is not null)
            throw new EsException(400, "invalid_index_name_exception", $"Invalid index name [{name}], {problem}",
                ("index_uuid", "_na_"), ("index", name));
    }

    /// <summary>The folder of an index: its name (valid names are valid folder names), made unique for the dots.</summary>
    private static string FolderName(string name) => name.StartsWith('.') ? "%2E" + name[1..] : name;

    public void RefreshAll()
    {
        foreach (var index in _indices.Values)
            index.Refresh();
    }

    /// <summary>
    /// Holds <c>path.data/node.lock</c> while the engine runs (an exclusive lock of the system): a second engine, or an
    /// import, on the same data stops at once instead of writing the same Lucene indices (the lock of Lucene.Net does
    /// not hold between processes on macOS).
    /// </summary>
    public static FileStream LockDataPath(string dataPath)
    {
        var file = Path.Combine(dataPath, "node.lock");
        try
        {
            var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Environment.ProcessId.ToString()));
            stream.Flush();
            return stream;
        }
        catch (IOException ex)
        {
            throw new DataPathInUseException(dataPath, ex);
        }
    }

    public void Dispose()
    {
        foreach (var index in _indices.Values)
        {
            try
            {
                index.Dispose();
            }
            catch (Exception ex)
            {
                _log.Error("index", $"[{index.Name}] close failed: {ex.Message}");
            }
        }
        _dataLock.Dispose();
    }
}

/// <summary><c>path.data</c> is used by another engine (or an import) that is running.</summary>
public sealed class DataPathInUseException(string dataPath, Exception inner)
    : Exception($"path.data [{dataPath}] is used by another engine or import (node.lock is held)", inner);
