using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace KAONOMI.SearchEngine.Configuration;

/// <summary>A user allowed to call the engine when the security is on (basic authentication).</summary>
public sealed record EngineUser(string Name, string Password);

/// <summary>
/// The settings of the engine, read from a YAML file in the style of <c>elasticsearch.yml</c>: the keys can be written
/// nested (<c>http: { port: 9200 }</c>) or dotted (<c>http.port: 9200</c>), and <c>-E key=value</c> on the command line
/// overrides them.
/// </summary>
public sealed class EngineSettings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>The users of <c>xpack.security.users</c> (name and password).</summary>
    public List<EngineUser> Users { get; } = new();

    /// <summary>The file the settings were read from, or null (defaults only).</summary>
    public string? File { get; private set; }

    public string ClusterName => Get("cluster.name", "kaonomi");
    public string NodeName => Get("node.name", Environment.MachineName);
    public string Host => Get("network.host", "127.0.0.1");
    public int Port => FirstPort(Get("http.port", "9200"));
    /// <summary>By default <c>~/kaonomi/data</c>: the same folder on Windows, macOS and Linux, created on the first start.</summary>
    public string DataPath => ResolvePath(Get("path.data", "~/kaonomi/data"));
    public string? LogsPath => _values.TryGetValue("path.logs", out var logs) ? ResolvePath(logs) : null;
    public bool SecurityEnabled => GetBool("xpack.security.enabled", false);
    public bool AutoCreateIndex => GetBool("action.auto_create_index", true);
    public TimeSpan RefreshInterval => GetTime("index.refresh_interval", TimeSpan.FromSeconds(1));
    public TimeSpan SyncInterval => GetTime("index.translog.sync_interval", TimeSpan.FromSeconds(5));
    public int MaxResultWindow => GetInt("index.max_result_window", 10000);
    public int NumberOfReplicas => GetInt("index.number_of_replicas", 1);
    public int MaxClauseCount => GetInt("indices.query.bool.max_clause_count", 1024);
    public double RamBufferMb => GetDouble("kaonomi.ram_buffer_mb", 64);
    public bool LogRequests => GetBool("kaonomi.log_requests", false);

    /// <summary>The relative paths (data, logs) are relative to the folder of the settings file.</summary>
    public string BaseFolder => File is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(Path.GetFullPath(File))!;

    /// <summary>A path of the settings: <c>~</c> is the home folder of the user, a relative path is relative to <see cref="BaseFolder"/>.</summary>
    private string ResolvePath(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : string.Empty);
        return Path.GetFullPath(path, BaseFolder);
    }

    /// <summary>Reads <paramref name="file"/> (when it exists), then applies the <c>-E key=value</c> overrides.</summary>
    public static EngineSettings Load(string? file, IEnumerable<string> overrides)
    {
        var settings = new EngineSettings();
        if (file is not null && System.IO.File.Exists(file))
        {
            settings.File = file;
            var yaml = new YamlStream();
            using (var reader = new StreamReader(file))
                yaml.Load(reader);
            if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlMappingNode root)
                settings.Flatten(root, string.Empty);
        }
        foreach (var item in overrides)
        {
            var equal = item.IndexOf('=');
            if (equal <= 0)
                throw new ArgumentException($"-E expects key=value, not [{item}]");
            settings._values[item[..equal].Trim()] = item[(equal + 1)..].Trim();
        }
        return settings;
    }

    private void Flatten(YamlMappingNode node, string prefix)
    {
        foreach (var (keyNode, valueNode) in node.Children)
        {
            var key = prefix + ((YamlScalarNode)keyNode).Value;
            switch (valueNode)
            {
                case YamlMappingNode map:
                    Flatten(map, key + ".");
                    break;
                case YamlSequenceNode list when key is "xpack.security.users" or "security.users":
                    foreach (var user in list.Children.OfType<YamlMappingNode>())
                    {
                        string? Field(string name) => user.Children.TryGetValue(new YamlScalarNode(name), out var v) ? ((YamlScalarNode)v).Value : null;
                        var name = Field("username") ?? Field("name");
                        if (!string.IsNullOrEmpty(name))
                            Users.Add(new EngineUser(name, Field("password") ?? string.Empty));
                    }
                    break;
                case YamlSequenceNode list:
                    _values[key] = string.Join(",", list.Children.OfType<YamlScalarNode>().Select(s => s.Value));
                    break;
                case YamlScalarNode scalar:
                    _values[key] = scalar.Value ?? string.Empty;
                    break;
            }
        }
    }

    public string Get(string key, string fallback) => _values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;

    public bool GetBool(string key, bool fallback) =>
        _values.TryGetValue(key, out var value) ? value.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1" : fallback;

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    public double GetDouble(string key, double fallback) =>
        _values.TryGetValue(key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    public TimeSpan GetTime(string key, TimeSpan fallback) =>
        _values.TryGetValue(key, out var value) ? ParseTime(value) ?? fallback : fallback;

    /// <summary>An Elasticsearch time value (<c>500ms</c>, <c>1s</c>, <c>2m</c>, <c>1h</c>, <c>-1</c> = never).</summary>
    public static TimeSpan? ParseTime(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value == "-1")
            return Timeout.InfiniteTimeSpan;
        var units = new (string Suffix, double Ms)[] { ("nanos", 1e-6), ("micros", 1e-3), ("ms", 1), ("s", 1000), ("m", 60_000), ("h", 3_600_000), ("d", 86_400_000) };
        foreach (var (suffix, ms) in units)
            if (value.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(value[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return TimeSpan.FromMilliseconds(n * ms);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) ? TimeSpan.FromMilliseconds(plain) : null;
    }

    private static int FirstPort(string value)
    {
        var first = value.Split('-', ',')[0].Trim();
        return int.TryParse(first, out var port) ? port : 9200;
    }
}
