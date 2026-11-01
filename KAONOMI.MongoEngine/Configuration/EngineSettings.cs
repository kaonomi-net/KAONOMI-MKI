using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace KAONOMI.MongoEngine.Configuration;

/// <summary>
/// The settings of the engine, read from a YAML file in the style of <c>mongod.conf</c>: the keys can be written
/// nested (<c>net: { port: 27017 }</c>) or dotted (<c>net.port: 27017</c>), and <c>--set key=value</c> on the command
/// line overrides them.
/// </summary>
public sealed class EngineSettings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>The file the settings were read from, or null (defaults only).</summary>
    public string? File { get; private set; }

    public int Port => GetInt("net.port", 27017);
    public string BindIp => Get("net.bindIp", "127.0.0.1");
    public int MaxConnections => GetInt("net.maxIncomingConnections", 10000);
    public string DbPath => Path.GetFullPath(Get("storage.dbPath", "data"), BaseFolder);
    public int CommitIntervalMs => GetInt("storage.journal.commitIntervalMs", 100);
    public int CompactAfter => GetInt("storage.journal.compactAfter", 10000);
    public string? LogPath => _values.TryGetValue("systemLog.path", out var path) ? Path.GetFullPath(path, BaseFolder) : null;
    public int Verbosity => GetInt("systemLog.verbosity", 0);

    /// <summary>The relative paths are relative to the folder of the settings file.</summary>
    public string BaseFolder => File is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(Path.GetFullPath(File))!;

    /// <summary>Reads <paramref name="file"/> (when it exists), then applies the <c>key=value</c> overrides.</summary>
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
                throw new ArgumentException($"--set expects key=value, not [{item}]");
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

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
}
