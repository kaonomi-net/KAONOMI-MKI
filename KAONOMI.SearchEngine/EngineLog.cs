using KAONOMI.SearchEngine.Configuration;

namespace KAONOMI.SearchEngine;

/// <summary>The log of the engine, in the format of Elasticsearch, on the console and in <c>path.logs/&lt;cluster&gt;.log</c>.</summary>
public sealed class EngineLog
{
    private readonly string _node;
    private readonly string? _file;
    private readonly object _lock = new();

    private EngineLog(string node, string? file)
    {
        _node = node;
        _file = file;
    }

    public static EngineLog For(EngineSettings settings)
    {
        string? file = null;
        if (settings.LogsPath is { } folder)
        {
            Directory.CreateDirectory(folder);
            file = Path.Combine(folder, settings.ClusterName + ".log");
        }
        return new EngineLog(settings.NodeName, file);
    }

    public void Info(string component, string message) => Write("INFO ", component, message);
    public void Warn(string component, string message) => Write("WARN ", component, message);
    public void Error(string component, string message) => Write("ERROR", component, message);

    private void Write(string level, string component, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss,fff}][{level}][{component}] [{_node}] {message}";
        lock (_lock)
        {
            (level == "ERROR" ? Console.Error : Console.Out).WriteLine(line);
            if (_file is not null)
                File.AppendAllText(_file, line + Environment.NewLine);
        }
    }
}
