using KAONOMI.MongoEngine.Configuration;

namespace KAONOMI.MongoEngine;

/// <summary>The log of the engine: on the console and in <c>systemLog.path</c>.</summary>
public sealed class EngineLog
{
    private readonly string? _file;
    private readonly object _lock = new();

    public int Verbosity { get; }

    private EngineLog(string? file, int verbosity)
    {
        _file = file;
        Verbosity = verbosity;
    }

    public static EngineLog For(EngineSettings settings)
    {
        if (settings.LogPath is { } file)
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return new EngineLog(settings.LogPath, settings.Verbosity);
    }

    public void Info(string component, string message) => Write("I", component, message);
    public void Warn(string component, string message) => Write("W", component, message);
    public void Error(string component, string message) => Write("E", component, message);

    /// <summary>Written only with <c>systemLog.verbosity</c> ≥ 1.</summary>
    public void Debug(string component, string message)
    {
        if (Verbosity >= 1)
            Write("D1", component, message);
    }

    private void Write(string severity, string component, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} {severity,-2} {component,-8} {message}";
        lock (_lock)
        {
            (severity == "E" ? Console.Error : Console.Out).WriteLine(line);
            if (_file is not null)
                System.IO.File.AppendAllText(_file, line + Environment.NewLine);
        }
    }
}
