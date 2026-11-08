using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A process of KAONOMI the chat watches, starts and stops: an engine, the server, an agent, the Fooocus API, Ollama.</summary>
public sealed partial class ManagedProcess : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Group { get; init; }
    /// <summary>The regular expression of its command line (pgrep -f), to find it and to stop it.</summary>
    public string? Pattern { get; init; }
    /// <summary>The port it listens on: open, it runs.</summary>
    public (string Host, int Port)? Port { get; init; }
    /// <summary>How to start it (null: it cannot be started from here).</summary>
    public Func<ProcessStartInfo?>? Start { get; init; }
    /// <summary>How to stop it kindly (the engines have a shutdown call); null: an interrupt, then a kill.</summary>
    public Func<CancellationToken, Task<string>>? GracefulStop { get; init; }
    /// <summary>What it needs to run (its order in START CORE and STOP ALL follows the list).</summary>
    public bool Core { get; init; }

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _detail = "checking…";

    /// <summary>True while it is started or stopped from here.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The process started from the chat (null when it runs from elsewhere, a terminal).</summary>
    public Process? Started { get; set; }

    public bool CanStart => Start is not null;
    public bool CanStop => Pattern is not null || GracefulStop is not null;
}

/// <summary>
/// The processes of KAONOMI (<c>/process</c>): the two engines, the server, the agents, the Fooocus API and Ollama,
/// each with its state read every few seconds (its port open, or its process found), and START, STOP, RESTART.
/// A process started from here runs with its output in <c>~/kaonomi/logs/&lt;name&gt;.log</c> and goes on when the
/// chat closes; one running from a terminal is found and stopped all the same (an interrupt, then a kill after ten
/// seconds; the engines are asked to stop as /bye does). START CORE starts the engines then the server; STOP ALL stops
/// everything but Ollama, the agents first. The projects are those of the folder holding KAONOMI.Server, found from
/// the folder of the chat (<see cref="ProjectsDirectory"/>).
/// </summary>
public sealed partial class ProcessWindowViewModel : ViewModelBase, INoteContextProvider, IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(3);
    private readonly ChatSettings _settings;
    private readonly Timer _timer;
    private int _refreshing;
    private bool _disposed;

    /// <summary>For the designer.</summary>
    public ProcessWindowViewModel() : this(new ChatSettings())
    {
    }

    public ProcessWindowViewModel(ChatSettings settings)
    {
        _settings = settings;
        ProjectsDirectory = FindProjects();
        foreach (var process in Build())
            Processes.Add(process);
        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, RefreshEvery);
    }

    /// <summary>The folder of the projects (KAONOMI.Server, the engines, the agents…).</summary>
    public string ProjectsDirectory { get; }

    public ObservableCollection<ManagedProcess> Processes { get; } = [];

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Where the output of the processes started from here goes.</summary>
    public static string LogsDirectory => Path.Combine(ChatSettings.KaonomiHome, "logs");

    // ── The list ──

    internal static string FindProjects()
    {
        // From the folder of the chat up (its binaries, or its project when run by the Godot editor): the first folder
        // holding KAONOMI.Server.
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var folder = new DirectoryInfo(start);
            for (var i = 0; i < 6 && folder is not null; i++, folder = folder.Parent)
                if (Directory.Exists(Path.Combine(folder.FullName, "KAONOMI.Server")))
                    return folder.FullName;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Projects", "kaonomi");
    }

    private IEnumerable<ManagedProcess> Build()
    {
        var search = Uri.TryCreate(_settings.SearchEngineUrl, UriKind.Absolute, out var searchUri) ? (searchUri.Host, searchUri.Port) : ("localhost", 9200);
        var mongo = ParseAddress(_settings.MongoEngineAddress, 27017);
        var server = Uri.TryCreate(_settings.ServerUrl, UriKind.Absolute, out var serverUri) ? (serverUri.Host, serverUri.Port) : ("localhost", 49165);
        var fooocus = _settings.FindFooocus();
        var fooocusApi = Path.Combine(ProjectsDirectory, "KAONOMI.Server", "Fooocus", "fooocus_api.py");

        yield return new ManagedProcess
        {
            Name = "SearchEngine", Group = "Engines", Core = true, Port = search,
            Description = $"KAONOMI.SearchEngine · the indices, {search.Item1}:{search.Item2}",
            Pattern = @"KAONOMI\.SearchEngine($|/bin/|\\bin\\)",
            Start = () => Dotnet(ProjectsDirectory, "run", "--project", "KAONOMI.SearchEngine"),
            GracefulStop = ct => EngineShutdown.StopSearchEngineAsync(_settings.SearchEngineUrl, ct)
        };
        yield return new ManagedProcess
        {
            Name = "MongoEngine", Group = "Engines", Core = true, Port = mongo,
            Description = $"KAONOMI.MongoEngine · the cluster of Orleans, {mongo.Host}:{mongo.Port}",
            Pattern = @"KAONOMI\.MongoEngine($|/bin/|\\bin\\)",
            Start = () => Dotnet(ProjectsDirectory, "run", "--project", "KAONOMI.MongoEngine"),
            GracefulStop = ct => EngineShutdown.StopMongoEngineAsync(_settings.MongoEngineAddress, ct)
        };
        yield return new ManagedProcess
        {
            Name = "Server", Group = "Server", Core = true, Port = server,
            Description = $"KAONOMI.Server · the REST API and the silo, {server.Item1}:{server.Item2} (needs the two engines)",
            Pattern = @"KAONOMI\.Server($|/bin/|\\bin\\)",
            Start = () => Dotnet(ProjectsDirectory, "run", "--project", "KAONOMI.Server")
        };
        foreach (var (name, what) in new[]
                 {
                     ("Dreamer", "the dreams of the night"), ("Reflex", "the reflexes"), ("Vision", "the pictures"),
                     ("Metacognition", "the metacognition"), ("EarlyWarningSystem", "the early warnings"), ("ACHEngine", "the analysis of competing hypotheses")
                 })
        {
            var project = "KAONOMI." + name;
            yield return new ManagedProcess
            {
                Name = name, Group = "Agents",
                Description = $"{project} · {what} (an agent of the silo: needs the server; runs from its own folder)",
                Pattern = $@"KAONOMI\.{name}($|/bin/|\\bin\\)",
                Start = () => Dotnet(Path.Combine(ProjectsDirectory, project), "run")
            };
        }
        yield return new ManagedProcess
        {
            Name = "WebScrapper", Group = "Agents",
            Description = "KAONOMI.Agent.WebScrapper · the web pages of the active collection plans: screenshots, pictures, paragraphs and their entities (an agent of the silo: needs the server; runs from its own folder)",
            Pattern = @"KAONOMI\.Agent\.WebScrapper($|/bin/|\\bin\\)",
            Start = () => Dotnet(Path.Combine(ProjectsDirectory, "KAONOMI.Agent.WebScrapper"), "run")
        };
        yield return new ManagedProcess
        {
            Name = "Fooocus API", Group = "Images", Port = ("127.0.0.1", 8888),
            Description = $"fooocus_api.py in the Python of Fooocus ({fooocus?.Folder ?? "not found: fooocus.directory in config.yml"}) · the images of /fooocus, 127.0.0.1:8888",
            Pattern = @"fooocus_api\.py",
            Start = () => fooocus is var (folder, python) && File.Exists(fooocusApi)
                ? Info(python, folder, fooocusApi, "--fooocus", folder)
                : null
        };
        var gateway = Uri.TryCreate(_settings.LlmGatewayUrl, UriKind.Absolute, out var gatewayUri) ? (gatewayUri.Host, gatewayUri.Port) : ("localhost", 11435);
        yield return new ManagedProcess
        {
            Name = "LLM Gateway", Group = "Models", Port = gateway,
            Description = $"KAONOMI.LLMGateway · the gateway in front of Ollama (semantic cache, router of the models by entities), {gateway.Item1}:{gateway.Item2}; /gateway drives it",
            Pattern = @"KAONOMI\.LLMGateway($|/bin/|\\bin\\)",
            Start = () => Dotnet(ProjectsDirectory, "run", "--project", "KAONOMI.LLMGateway")
        };
        yield return new ManagedProcess
        {
            Name = "Ollama", Group = "Models", Port = ("127.0.0.1", 11434),
            Description = "Ollama · the models of the chat, 127.0.0.1:11434 (the application of the system)",
            Start = () => OperatingSystem.IsMacOS() ? Info("open", null, "-a", "Ollama") : OperatingSystem.IsWindows() ? Info("ollama", null, "serve") : Info("ollama", null, "serve")
        };
    }

    private static (string Host, int Port) ParseAddress(string? address, int defaultPort)
    {
        var text = (address ?? string.Empty).Trim();
        if (text.StartsWith("mongodb://", StringComparison.OrdinalIgnoreCase))
            text = text["mongodb://".Length..];
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var port))
            return (text[..colon], port);
        return (text.Length == 0 ? "localhost" : text, defaultPort);
    }

    internal static ProcessStartInfo? Dotnet(string workingDirectory, params string[] arguments)
    {
        var dotnet = FindDotnet();
        return dotnet is null ? null : Info(dotnet, workingDirectory, arguments);
    }

    private static string? FindDotnet()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            "/usr/local/share/dotnet/dotnet", "/opt/homebrew/bin/dotnet", "/usr/local/bin/dotnet", "/usr/bin/dotnet", "/usr/share/dotnet/dotnet",
            Path.Combine(home, ".dotnet", "dotnet"), @"C:\Program Files\dotnet\dotnet.exe"
        ];
        if (candidates.FirstOrDefault(File.Exists) is { } found)
            return found;
        // On the PATH of the chat.
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return path.Split(Path.PathSeparator).Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
    }

    private static ProcessStartInfo Info(string file, string? workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        if (workingDirectory is not null)
            info.WorkingDirectory = workingDirectory;
        // The tools of the machine (dotnet, uv…) even when the chat was started from the Finder with a short PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        info.Environment["PATH"] = string.Join(Path.PathSeparator, new[] { path, "/usr/local/share/dotnet", "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin" }.Where(p => p.Length > 0));
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return info;
    }

    // ── The state ──

    /// <summary>Reads the state of every process (its port, or its process found), a few at a time.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) == 1)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await Task.WhenAll(Processes.Select(p => CheckAsync(p, timeout.Token)));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The next refresh will say.
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private static async Task CheckAsync(ManagedProcess process, CancellationToken ct)
    {
        var pids = process.Pattern is null ? [] : await FindAsync(process.Pattern, ct);
        var portOpen = process.Port is { } port && await EngineShutdown.PortOpenAsync(port.Host, port.Port, ct);
        var running = portOpen || pids.Count > 0;
        process.IsRunning = running;
        process.Detail = !running ? "stopped"
            : (process.Port is { } p ? $"{p.Host}:{p.Port} answers" : "running")
              + (pids.Count > 0 ? $" · pid {string.Join(", ", pids)}" : string.Empty)
              + (process.Port is not null && !portOpen ? " · port not open yet" : string.Empty);
    }

    /// <summary>The ids of the processes whose command line matches <paramref name="pattern"/> (pgrep -f; the names on Windows).</summary>
    private static async Task<List<int>> FindAsync(string pattern, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var name = pattern.Split('(')[0].Replace(@"\.", ".");
            return Process.GetProcesses().Where(p => p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToList();
        }
        var (code, output) = await RunAsync("pgrep", ["-f", pattern], ct);
        if (code != 0)
            return [];
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => int.TryParse(line, out var pid) ? pid : -1).Where(pid => pid > 0 && pid != Environment.ProcessId).ToList();
    }

    private static async Task<(int Code, string Output)> RunAsync(string file, string[] arguments, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null)
                return (-1, string.Empty);
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (-1, string.Empty);
        }
    }

    // ── Start, stop ──

    /// <summary>Starts <paramref name="process"/> (its output in ~/kaonomi/logs), and waits for it to run.</summary>
    [RelayCommand]
    private async Task StartAsync(ManagedProcess process)
    {
        if (process.IsBusy)
            return;
        if (process.IsRunning)
        {
            SetStatus($"{process.Name} is already running", false);
            return;
        }
        var info = process.Start?.Invoke();
        if (info is null)
        {
            SetStatus($"{process.Name} cannot be started from here" + (process.Name == "Fooocus API" ? ": Fooocus not found (fooocus.directory in config.yml, see /setup)" : FindDotnet() is null ? ": dotnet not found" : string.Empty), true);
            return;
        }
        process.IsBusy = true;
        try
        {
            Directory.CreateDirectory(LogsDirectory);
            var log = new StreamWriter(Path.Combine(LogsDirectory, Safe(process.Name) + ".log"), append: true) { AutoFlush = true };
            await log.WriteLineAsync($"──── {DateTime.Now:yyyy-MM-dd HH:mm:ss} · started from the chat: {info.FileName} {string.Join(' ', info.ArgumentList)} (in {info.WorkingDirectory}) ────");
            var started = new Process { StartInfo = info, EnableRaisingEvents = true };
            started.OutputDataReceived += (_, e) => { if (e.Data is not null) Write(log, e.Data); };
            started.ErrorDataReceived += (_, e) => { if (e.Data is not null) Write(log, e.Data); };
            started.Exited += (_, _) =>
            {
                Write(log, $"──── {DateTime.Now:yyyy-MM-dd HH:mm:ss} · exited with code {started.ExitCode} ────");
                lock (log)
                    log.Dispose();
                if (ReferenceEquals(process.Started, started))
                    process.Started = null;
                _ = RefreshAsync();
            };
            if (!started.Start())
                throw new InvalidOperationException("the process did not start");
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
            process.Started = started;
            SetStatus($"{process.Name} started (pid {started.Id}, output in {LogsDirectory})…", false);
            ActivityLog.Info("Process", $"{process.Name} started from the chat: {info.FileName} {string.Join(' ', info.ArgumentList)}");
            // Running once its port answers (the engines take a few seconds, the server more), or its process is seen.
            var up = await WaitAsync(process, running: true, TimeSpan.FromSeconds(process.Core ? 90 : 60));
            SetStatus(up ? $"{process.Name} is running" : $"{process.Name} started, but does not answer yet (see {Path.Combine(LogsDirectory, Safe(process.Name) + ".log")})", !up);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            SetStatus($"{process.Name} not started: {ex.Message}", true);
            ActivityLog.Error("Process", $"{process.Name} not started", ex);
        }
        finally
        {
            process.IsBusy = false;
            await RefreshAsync();
        }
    }

    private static void Write(StreamWriter log, string line)
    {
        try
        {
            lock (log)
                log.WriteLine(line);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Stops <paramref name="process"/>: the engines kindly (their shutdown), the others by an interrupt, then a kill after ten seconds.</summary>
    [RelayCommand]
    private async Task StopAsync(ManagedProcess process)
    {
        if (process.IsBusy)
            return;
        if (!process.IsRunning)
        {
            SetStatus($"{process.Name} is not running", false);
            return;
        }
        process.IsBusy = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (process.GracefulStop is not null)
            {
                SetStatus($"{process.Name}: {await process.GracefulStop(timeout.Token)}", false);
                if (await WaitAsync(process, running: false, TimeSpan.FromSeconds(15)))
                {
                    ActivityLog.Info("Process", $"{process.Name} stopped from the chat");
                    SetStatus($"{process.Name} stopped", false);
                    return;
                }
            }
            var pids = process.Started is { HasExited: false } own ? [own.Id] : process.Pattern is null ? [] : await FindAsync(process.Pattern, timeout.Token);
            if (pids.Count == 0)
            {
                SetStatus($"{process.Name}: its process was not found (it answers on its port but was not started here; stop it where it runs)", true);
                return;
            }
            await SignalAsync(pids, kill: false, timeout.Token);
            if (!await WaitAsync(process, running: false, TimeSpan.FromSeconds(10)))
            {
                await SignalAsync(pids, kill: true, timeout.Token);
                await WaitAsync(process, running: false, TimeSpan.FromSeconds(5));
            }
            ActivityLog.Info("Process", $"{process.Name} stopped from the chat (pid {string.Join(", ", pids)})");
            SetStatus(process.IsRunning ? $"{process.Name} did not stop" : $"{process.Name} stopped", process.IsRunning);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            SetStatus($"{process.Name} not stopped: {ex.Message}", true);
        }
        finally
        {
            process.IsBusy = false;
            await RefreshAsync();
        }
    }

    private static async Task SignalAsync(List<int> pids, bool kill, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var pid in pids)
            {
                try
                {
                    Process.GetProcessById(pid).Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
            return;
        }
        await RunAsync("kill", [kill ? "-KILL" : "-INT", .. pids.Select(p => p.ToString())], ct);
    }

    /// <summary>Stops then starts <paramref name="process"/>.</summary>
    [RelayCommand]
    private async Task RestartAsync(ManagedProcess process)
    {
        if (process.IsRunning)
            await StopAsync(process);
        if (!process.IsRunning)
            await StartAsync(process);
    }

    /// <summary>Starts what is missing of the core: the two engines together, then the server.</summary>
    [RelayCommand]
    private async Task StartCoreAsync()
    {
        IsBusy = true;
        try
        {
            var engines = Processes.Where(p => p.Group == "Engines" && !p.IsRunning).ToList();
            await Task.WhenAll(engines.Select(StartAsync));
            if (Processes.Where(p => p.Group == "Engines").Any(p => !p.IsRunning))
            {
                SetStatus("An engine did not start: the server is not started", true);
                return;
            }
            if (Processes.FirstOrDefault(p => p.Name == "Server") is { IsRunning: false } server)
                await StartAsync(server);
            SetStatus("Core started: the engines and the server", false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Stops everything but Ollama: the agents and the Fooocus API, then the server, then the engines.</summary>
    [RelayCommand]
    private async Task StopAllAsync()
    {
        IsBusy = true;
        try
        {
            foreach (var group in new[] { "Agents", "Images", "Server", "Engines" })
                await Task.WhenAll(Processes.Where(p => p.Group == group && p.IsRunning && p.CanStop).Select(StopAsync));
            SetStatus("Everything stopped (Ollama left running)", false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Stops everything of Kaonomi but Ollama as fast as it can (<c>/killall</c>), all at once: the server, the agents
    /// and the Fooocus API are killed at once (they keep nothing on disk), the two engines are asked to stop (they
    /// write their data) and killed when they have not stopped within three seconds. <see cref="Status"/> tells what
    /// was stopped.
    /// </summary>
    public async Task KillEverythingAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var targets = Processes.Where(p => p.Name != "Ollama" && p.CanStop).ToList();
        var stopped = await Task.WhenAll(targets.Select(async process =>
        {
            var wasRunning = false;
            try
            {
                if (process.GracefulStop is not null && process.Port is { } port
                    && await EngineShutdown.PortOpenAsync(port.Host, port.Port, timeout.Token))
                {
                    wasRunning = true;
                    // An engine: its own shutdown (a fraction of a second), then a kill if it still runs after 3 s.
                    using var engine = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    engine.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        await process.GracefulStop(engine.Token);
                        for (var i = 0; i < 30 && await EngineShutdown.PortOpenAsync(port.Host, port.Port, engine.Token); i++)
                            await Task.Delay(100, engine.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Http.HttpRequestException)
                    {
                    }
                }
                var pids = process.Pattern is null ? [] : await FindAsync(process.Pattern, timeout.Token);
                if (pids.Count > 0)
                    await SignalAsync(pids, kill: true, timeout.Token);
                return wasRunning || pids.Count > 0 ? process.Name : null;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
            {
                return null;
            }
        }));
        var names = stopped.Where(n => n is not null).ToList();
        SetStatus(names.Count == 0 ? "Nothing of Kaonomi was running" : "Stopped: " + string.Join(", ", names), false);
        ActivityLog.Info("Process", "/killall: " + Status);
    }

    /// <summary>Waits until <paramref name="process"/> runs (or not), up to <paramref name="limit"/>.</summary>
    private async Task<bool> WaitAsync(ManagedProcess process, bool running, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await CheckAsync(process, timeout.Token);
            if (process.IsRunning == running)
                return true;
            await Task.Delay(1000);
        }
        return false;
    }

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));

    private void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }

    /// <summary>Stops the refresh (the window closed); the processes started from here go on.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Dispose();
    }

    /// <inheritdoc />
    public string NoteWindowName => "Processes";

    /// <inheritdoc />
    public object? GetNoteContext() => new { running = Processes.Where(p => p.IsRunning).Select(p => p.Name).ToList(), stopped = Processes.Where(p => !p.IsRunning).Select(p => p.Name).ToList() };
}
