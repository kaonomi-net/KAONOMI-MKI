using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Views;
using PortAudioSharp;

namespace KAONOMI.Chat.Services;

public enum DependencyState
{
    Checking,
    Ok,
    Warning,
    Missing
}

/// <summary>One dependency of the chat: its check, its state, and when it can be installed from the chat, its fix.</summary>
public sealed class Dependency
{
    public required string Group { get; init; }
    public required string Name { get; init; }
    public required Func<Dependency, CancellationToken, Task> Check { get; init; }
    public DependencyState State { get; set; } = DependencyState.Checking;
    public string Detail { get; set; } = "checking…";
    /// <summary>What to do by hand (a command, or a sentence), shown under the detail.</summary>
    public string? Hint { get; set; }
    /// <summary>A page to open (the download of Python, Ollama…).</summary>
    public string? Link { get; set; }
    /// <summary>The label of the button of <see cref="Fix"/> (INSTALL, DOWNLOAD…).</summary>
    public string? FixLabel { get; set; }
    /// <summary>Installs the dependency from the chat, reporting its progress line by line; null when it cannot.</summary>
    public Func<IProgress<string>, CancellationToken, Task>? Fix { get; set; }

    public void Set(DependencyState state, string detail, string? hint = null, string? link = null, string? fixLabel = null,
        Func<IProgress<string>, CancellationToken, Task>? fix = null)
    {
        State = state;
        Detail = detail;
        Hint = hint;
        Link = link;
        FixLabel = fixLabel;
        Fix = fix;
    }
}

/// <summary>
/// The dependencies of the chat, checked at its first start (and with <c>/setup</c>): speech to text (the Whisper
/// model, the microphone), text to speech (Python, the Piper venv, the voices), the 3D animations (the face images),
/// the services (Ollama and its models, the Kaonomi server, the two engines, the data folder). What can be installed
/// from the chat has a fix: the Whisper model and the voices are downloaded, the Piper venv is built with Python, a
/// model of Ollama is pulled; the rest says what to do by hand.
/// </summary>
public sealed class SetupCheck
{
    public const string SpeechToText = "Speech to text (the microphone)";
    public const string TextToSpeech = "Text to speech (Piper)";
    public const string Animations = "3D animations";
    public const string Services = "Services";

    /// <summary>The English voices of the intro ("Let's go to think"), the preferred first (the same list as StartIntro).</summary>
    private static readonly string[] IntroVoices = ["en_GB-alan-medium", "en_US-ryan-high", "en_US-john-medium", "en_GB-alan-low", "en_US-danny-low"];

    /// <summary>The voice downloaded for a language that has none (a single speaker, medium quality).</summary>
    private static readonly Dictionary<string, string> DefaultVoices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FR"] = "fr_FR-siwis-medium",
        ["DE"] = "de_DE-thorsten-medium",
        ["EN"] = "en_GB-alan-medium",
        ["NL"] = "nl_BE-nathalie-medium"
    };

    private static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private readonly ChatSettings _settings;

    private KaonomiAssistantService? _server;

    /// <summary>The client of the Kaonomi server, made once (not one more connection pool at each check).</summary>
    private KaonomiAssistantService Server => _server ??= new KaonomiAssistantService(_settings.ServerUrl);
    private (string File, string[] Args)? _python;

    public SetupCheck(ChatSettings settings)
    {
        _settings = settings;
        Dependencies =
        [
            new() { Group = SpeechToText, Name = "Whisper model", Check = CheckWhisperModelAsync },
            new() { Group = SpeechToText, Name = "Microphone", Check = CheckMicrophoneAsync },
            new() { Group = TextToSpeech, Name = "Python 3", Check = CheckPythonAsync },
            new() { Group = TextToSpeech, Name = "Piper", Check = CheckPiperAsync },
            new() { Group = TextToSpeech, Name = $"Voice of the chat ({settings.Language})", Check = CheckVoiceAsync },
            new() { Group = TextToSpeech, Name = "Voice of the intro (English)", Check = CheckIntroVoiceAsync },
            new() { Group = Animations, Name = "Faces of the animations", Check = CheckFacesAsync },
            new() { Group = Services, Name = "Ollama", Check = CheckOllamaAsync },
            new() { Group = Services, Name = $"Chat model ({settings.Model})", Check = (d, ct) => CheckOllamaModelAsync(d, settings.Model, ct) },
            new() { Group = Services, Name = $"Vision model ({settings.VisionModel})", Check = (d, ct) => CheckOllamaModelAsync(d, settings.VisionModel, ct) },
            new() { Group = Services, Name = "Kaonomi server", Check = CheckServerAsync },
            new() { Group = Services, Name = "Search engine", Check = (d, ct) => CheckPortAsync(d, settings.SearchEngineUrl, "KAONOMI.SearchEngine", ct) },
            new() { Group = Services, Name = "Mongo engine", Check = (d, ct) => CheckPortAsync(d, "tcp://" + settings.MongoEngineAddress, "KAONOMI.MongoEngine", ct) },
            new() { Group = Services, Name = "Fooocus API (images)", Check = CheckFooocusAsync },
            new() { Group = Services, Name = "Data folder", Check = CheckDataFolderAsync }
        ];
    }

    /// <summary>The dependencies, in the order of the screen; the host may add its own before <see cref="RunAsync"/>.</summary>
    public List<Dependency> Dependencies { get; }

    /// <summary>Runs every check (Python first: Piper and the voices depend on it), <paramref name="changed"/> after each one.</summary>
    public async Task RunAsync(Action<Dependency> changed, CancellationToken cancellationToken)
    {
        foreach (var dependency in Dependencies)
        {
            dependency.Set(DependencyState.Checking, "checking…");
            changed(dependency);
        }
        async Task Run(Dependency dependency)
        {
            try
            {
                await dependency.Check(dependency, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                dependency.Set(DependencyState.Missing, $"the check failed: {ex.Message}");
            }
            changed(dependency);
        }
        var python = Dependencies.FirstOrDefault(d => d.Check == CheckPythonAsync);
        if (python is not null)
            await Run(python);
        await Task.WhenAll(Dependencies.Where(d => d != python).Select(Run));
    }

    // ── Speech to text ──

    private Task CheckWhisperModelAsync(Dependency d, CancellationToken ct)
    {
        var path = _settings.SpeechModel;
        if (File.Exists(path))
            d.Set(DependencyState.Ok, $"{path} ({new FileInfo(path).Length >> 20} MB)");
        else
        {
            var url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + Path.GetFileName(path);
            d.Set(DependencyState.Missing, $"not found: {path}", $"Download {url} to that path (speech.model in config.yml)",
                "https://huggingface.co/ggerganov/whisper.cpp", "DOWNLOAD", (progress, token) => DownloadAsync(url, path, progress, token));
        }
        return Task.CompletedTask;
    }

    private static Task CheckMicrophoneAsync(Dependency d, CancellationToken ct)
    {
        try
        {
            PortAudio.Initialize();
            var device = PortAudio.DefaultInputDevice;
            if (device == PortAudio.NoDevice)
                d.Set(DependencyState.Warning, "no input device: plug a microphone (or allow the microphone in the system settings)");
            else
                d.Set(DependencyState.Ok, PortAudio.GetDeviceInfo(device).name);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            d.Set(DependencyState.Missing, $"the PortAudio library did not load: {ex.Message}",
                Windows ? "Rebuild the chat: the PortAudioSharp2 package brings the library" : "brew install portaudio, or rebuild the chat (the PortAudioSharp2 package brings the library)");
        }
        return Task.CompletedTask;
    }

    // ── Text to speech ──

    private async Task CheckPythonAsync(Dependency d, CancellationToken ct)
    {
        _python = null;
        Version? best = null;
        foreach (var candidate in PythonCandidates())
        {
            try
            {
                var (code, output) = await RunAsync(candidate.File, [.. candidate.Args, "--version"], null, ct, TimeSpan.FromSeconds(10));
                var text = output.Trim();
                if (code != 0 || !text.StartsWith("Python ", StringComparison.Ordinal) || !Version.TryParse(text[7..].Split(' ')[0], out var version))
                    continue;
                if (best is null || version > best)
                {
                    best = version;
                    _python = candidate;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
            }
        }
        if (_python is { } python && best is not null && best >= new Version(3, 9))
            d.Set(DependencyState.Ok, $"Python {best} ({python.File}{(python.Args.Length > 0 ? " " + string.Join(' ', python.Args) : "")})");
        else
        {
            _python = null;
            d.Set(DependencyState.Missing, best is null ? "no Python 3 found" : $"Python {best} found: 3.9 or newer is needed",
                "Install Python 3 (python.org, or brew install python), then CHECK AGAIN", "https://www.python.org/downloads/");
        }
    }

    private static IEnumerable<(string File, string[] Args)> PythonCandidates()
    {
        if (Windows)
        {
            yield return ("py", ["-3"]);
            yield return ("python", []);
            yield return ("python3", []);
            yield break;
        }
        yield return ("/Library/Frameworks/Python.framework/Versions/Current/bin/python3", []);
        yield return ("/opt/homebrew/bin/python3", []);
        yield return ("/usr/local/bin/python3", []);
        yield return ("python3", []);
        yield return ("python", []);
    }

    /// <summary>The venv of Piper: the one of tts.piper when it is a venv, else ~/kaonomi/piper/venv.</summary>
    private string PiperVenv
    {
        get
        {
            var piper = _settings.ResolvedTtsPiperPath.Replace('\\', '/');
            var marker = Windows ? "/Scripts/piper.exe" : "/bin/piper";
            return piper.EndsWith(marker, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(piper[..^marker.Length])
                : Path.Combine(ChatSettings.KaonomiHome, "piper", "venv");
        }
    }

    private string VenvBin(string name) => Path.Combine(PiperVenv, Windows ? "Scripts" : "bin", Windows ? name + ".exe" : name);

    private async Task CheckPiperAsync(Dependency d, CancellationToken ct)
    {
        var piper = _settings.ResolvedTtsPiperPath;
        const string install = "python3 -m venv ~/kaonomi/piper/venv && ~/kaonomi/piper/venv/bin/pip install piper-tts";
        const string link = "https://github.com/OHF-voice/piper1-gpl";
        if (!File.Exists(piper))
        {
            d.Set(DependencyState.Missing, $"not found: {piper}", install, link, "INSTALL", InstallPiperAsync);
            return;
        }
        try
        {
            var (code, output) = await RunAsync(piper, ["--help"], null, ct, TimeSpan.FromSeconds(30));
            if (code == 0)
                d.Set(DependencyState.Ok, piper);
            else
                d.Set(DependencyState.Warning, $"{piper} does not run (its Python is gone?): {FirstLine(output)}", install, link, "REBUILD", InstallPiperAsync);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            d.Set(DependencyState.Warning, $"{piper} does not run: {ex.Message}", install, link, "REBUILD", InstallPiperAsync);
        }
    }

    /// <summary>Builds the venv of Piper with the Python found (an old venv is kept aside) and installs piper-tts in it.</summary>
    private async Task InstallPiperAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (_python is not { } python)
            throw new InvalidOperationException("Python 3.9 or newer is needed first (see the line Python 3)");
        var venv = PiperVenv;
        if (Directory.Exists(venv))
        {
            var aside = venv + ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Directory.Move(venv, aside);
            progress.Report($"the old venv is kept as {aside}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(venv)!);
        progress.Report($"{python.File} -m venv {venv}");
        var (code, output) = await RunAsync(python.File, [.. python.Args, "-m", "venv", venv], progress, ct, TimeSpan.FromMinutes(5));
        if (code != 0)
            throw new InvalidOperationException($"python -m venv failed ({code}): {FirstLine(output)}");
        var pip = VenvBin("pip");
        progress.Report($"{pip} install --upgrade pip");
        await RunAsync(pip, ["install", "--quiet", "--upgrade", "pip"], progress, ct, TimeSpan.FromMinutes(5));
        progress.Report($"{pip} install piper-tts");
        (code, output) = await RunAsync(pip, ["install", "piper-tts"], progress, ct, TimeSpan.FromMinutes(15));
        if (code != 0)
            throw new InvalidOperationException($"pip install piper-tts failed ({code}): {FirstLine(output)}");
    }

    private Task CheckVoiceAsync(Dependency d, CancellationToken ct)
    {
        var voice = _settings.ResolvedTtsVoice;
        if (voice is not null && File.Exists(voice))
            d.Set(DependencyState.Ok, Path.GetFileNameWithoutExtension(voice));
        else
        {
            var name = DefaultVoices.TryGetValue(_settings.Language, out var v) ? v : "fr_FR-siwis-medium";
            d.Set(DependencyState.Missing, $"no voice of {_settings.Language} in {ChatSettings.ResolveDirectory(_settings.TtsVoicesDirectory)}",
                $"~/kaonomi/piper/venv/bin/python -m piper.download_voices --data-dir ~/kaonomi/piper/voices {name}", "https://github.com/OHF-voice/piper1-gpl/blob/main/VOICES.md",
                "DOWNLOAD", (progress, token) => DownloadVoiceAsync(name, progress, token));
        }
        return Task.CompletedTask;
    }

    private Task CheckIntroVoiceAsync(Dependency d, CancellationToken ct)
    {
        var directory = ChatSettings.ResolveDirectory(_settings.TtsVoicesDirectory);
        var found = IntroVoices.FirstOrDefault(name => File.Exists(Path.Combine(directory, name + ".onnx")) && File.Exists(Path.Combine(directory, name + ".onnx.json")));
        if (found is not null)
            d.Set(DependencyState.Ok, found);
        else
            d.Set(DependencyState.Warning, "none of the English voices of the intro is installed: the intro stays silent",
                $"~/kaonomi/piper/venv/bin/python -m piper.download_voices --data-dir ~/kaonomi/piper/voices {IntroVoices[0]}", null,
                "DOWNLOAD", (progress, token) => DownloadVoiceAsync(IntroVoices[0], progress, token));
        return Task.CompletedTask;
    }

    /// <summary>Downloads a voice of Piper into the folder of the voices with the venv of Piper.</summary>
    private async Task DownloadVoiceAsync(string voice, IProgress<string> progress, CancellationToken ct)
    {
        var python = VenvBin("python");
        if (!File.Exists(python))
            throw new InvalidOperationException("Piper is needed first (see the line Piper)");
        var directory = ChatSettings.ResolveDirectory(_settings.TtsVoicesDirectory);
        Directory.CreateDirectory(directory);
        progress.Report($"{python} -m piper.download_voices --data-dir {directory} {voice}");
        var (code, output) = await RunAsync(python, ["-m", "piper.download_voices", "--data-dir", directory, voice], progress, ct, TimeSpan.FromMinutes(15));
        if (code != 0)
            throw new InvalidOperationException($"the download of {voice} failed ({code}): {FirstLine(output)}");
    }

    // ── 3D animations ──

    private static Task CheckFacesAsync(Dependency d, CancellationToken ct)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets");
        var images = typeof(FaceShaders).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(FaceShaders.FaceModel))
            .Select(f => Path.GetFileName(new Uri(((FaceShaders.FaceModel)f.GetValue(null)!).Image).AbsolutePath))
            .Distinct()
            .ToList();
        var missing = images.Where(image => !File.Exists(Path.Combine(folder, image))).ToList();
        if (missing.Count == 0)
            d.Set(DependencyState.Ok, $"{images.Count} face(s) in {folder}");
        else
            d.Set(DependencyState.Missing, $"missing in {folder}: {string.Join(", ", missing)}", "Rebuild KAONOMI.ChatGodot (dotnet build): the images of Assets/ are copied next to the assembly");
        return Task.CompletedTask;
    }

    // ── Services ──

    private IReadOnlyList<string>? _ollamaModels;

    private async Task CheckOllamaAsync(Dependency d, CancellationToken ct)
    {
        _ollamaModels = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            _ollamaModels = await new OllamaModelService(_settings.OllamaUrl).ListModelsAsync(timeout.Token);
            d.Set(DependencyState.Ok, $"{_settings.OllamaUrl}: {_ollamaModels.Count} model(s)");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            d.Set(DependencyState.Missing, $"not reachable at {_settings.OllamaUrl}", "Install Ollama and start it (the models are pulled with: ollama pull <model>)", "https://ollama.com/download");
        }
    }

    private async Task CheckOllamaModelAsync(Dependency d, string model, CancellationToken ct)
    {
        // After the check of Ollama (the list of its models).
        for (var i = 0; i < 60 && _ollamaModels is null && Dependencies.First(x => x.Check == CheckOllamaAsync).State == DependencyState.Checking; i++)
            await Task.Delay(100, ct);
        if (_ollamaModels is null)
        {
            d.Set(DependencyState.Missing, "Ollama is not reachable");
            return;
        }
        static string Bare(string name) => name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;
        static string Family(string name) => name.Split(':')[0];
        if (_ollamaModels.Any(m => string.Equals(Bare(m), Bare(model), StringComparison.OrdinalIgnoreCase)))
            d.Set(DependencyState.Ok, model);
        else if (_ollamaModels.FirstOrDefault(m => string.Equals(Family(m), Family(model), StringComparison.OrdinalIgnoreCase)) is { } sibling)
            d.Set(DependencyState.Warning, $"{sibling} is installed instead of {model}: the chat uses it (llmmodel in config.json names the exact one)");
        else
        {
            var ollama = FindOllama();
            d.Set(DependencyState.Missing, $"{model} is not installed in Ollama", $"ollama pull {model}", "https://ollama.com/library",
                ollama is null ? null : "PULL", ollama is null ? null : async (progress, token) =>
                {
                    progress.Report($"{ollama} pull {model}");
                    var (code, output) = await RunAsync(ollama, ["pull", model], progress, token, TimeSpan.FromHours(2));
                    if (code != 0)
                        throw new InvalidOperationException($"ollama pull failed ({code}): {FirstLine(output)}");
                });
        }
    }

    private static string? FindOllama()
    {
        var names = Windows ? new[] { "ollama.exe" } : new[] { "/usr/local/bin/ollama", "/opt/homebrew/bin/ollama", "ollama" };
        foreach (var name in names)
        {
            if (Path.IsPathRooted(name))
            {
                if (File.Exists(name))
                    return name;
                continue;
            }
            var found = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
            if (found is not null)
                return found;
        }
        return null;
    }

    private async Task CheckServerAsync(Dependency d, CancellationToken ct)
    {
        var reachable = await Server.IsReachableAsync(TimeSpan.FromSeconds(5), ct);
        if (reachable)
            d.Set(DependencyState.Ok, _settings.ServerUrl);
        else
            d.Set(DependencyState.Missing, $"not reachable at {_settings.ServerUrl}", "dotnet run --project KAONOMI.Server (from the kaonomi folder; it needs the two engines)");
    }

    private static async Task CheckPortAsync(Dependency d, string address, string project, CancellationToken ct)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            d.Set(DependencyState.Missing, $"no address ({address})");
            return;
        }
        if (await EngineShutdown.PortOpenAsync(uri.Host, uri.Port, ct))
            d.Set(DependencyState.Ok, $"{uri.Host}:{uri.Port}");
        else
            d.Set(DependencyState.Missing, $"nothing listens on {uri.Host}:{uri.Port}", $"dotnet run --project {project} (from the kaonomi folder)");
    }

    /// <summary>
    /// The Fooocus API of the Kaonomi server (KAONOMI.Server/Fooocus/fooocus_api.py, port 8888): the images of
    /// /fooocus. A warning only: the chat works without images. Fooocus itself is expected in ~/kaonomi/fooocus (or
    /// the Pinokio one), with its Python 3.10 environment in its env folder.
    /// </summary>
    private static async Task CheckFooocusAsync(Dependency d, CancellationToken ct)
    {
        if (await EngineShutdown.PortOpenAsync("127.0.0.1", 8888, ct))
        {
            d.Set(DependencyState.Ok, "127.0.0.1:8888");
            return;
        }
        var fooocus = Path.Combine(ChatSettings.KaonomiHome, "fooocus");
        var python = Path.Combine(fooocus, "env", "bin", "python");
        var installed = File.Exists(Path.Combine(fooocus, "launch.py")) && File.Exists(python);
        d.Set(DependencyState.Warning, installed ? "Fooocus is installed but its API is not running (no images in /fooocus)" : "Fooocus is not installed (no images in /fooocus)",
            installed
                ? "cd ~/kaonomi/fooocus && env/bin/python <kaonomi>/KAONOMI.Server/Fooocus/fooocus_api.py --fooocus ~/kaonomi/fooocus"
                : "git clone https://github.com/lllyasviel/Fooocus.git ~/kaonomi/fooocus, a Python 3.10 venv in its env folder (uv venv --python 3.10 --seed env) with fastapi, uvicorn and pygit2, then the command of the API: its first start installs torch and the models (about 8 GB)");
        d.Link = "https://github.com/lllyasviel/Fooocus";
    }

    private static Task CheckDataFolderAsync(Dependency d, CancellationToken ct)
    {
        var home = ChatSettings.KaonomiHome;
        string[] folders = ["models", Path.Combine("piper", "voices"), "promptscollection", "jsonresult", "jsoncollection", "regexcollection"];
        var missing = folders.Where(f => !Directory.Exists(Path.Combine(home, f))).ToList();
        if (Directory.Exists(home) && missing.Count == 0)
            d.Set(DependencyState.Ok, home);
        else
            d.Set(DependencyState.Warning, Directory.Exists(home) ? $"{home}: missing {string.Join(", ", missing)}" : $"{home} does not exist",
                null, null, "CREATE", (progress, token) =>
                {
                    foreach (var folder in folders)
                    {
                        Directory.CreateDirectory(Path.Combine(home, folder));
                        progress.Report($"created {Path.Combine(home, folder)}");
                    }
                    return Task.CompletedTask;
                });
        return Task.CompletedTask;
    }

    // ── Helpers ──

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    /// <summary>Runs a program, its output line by line to <paramref name="progress"/>; killed after <paramref name="timeout"/>.</summary>
    public static async Task<(int Code, string Output)> RunAsync(string file, IEnumerable<string> arguments, IProgress<string>? progress,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        void Line(string? line)
        {
            if (line is null)
                return;
            lock (output)
                output.AppendLine(line);
            progress?.Report(line);
        }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
            linked.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            if (cancellationToken.IsCancellationRequested)
                throw;
            lock (output)
                return (-1, output + "(stopped: too long)");
        }
        lock (output)
            return (process.ExitCode, output.ToString());
    }

    /// <summary>Downloads <paramref name="url"/> to <paramref name="path"/> (a .part file first), the progress in MB.</summary>
    public static async Task DownloadAsync(string url, string path, IProgress<string> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        progress.Report($"downloading {url}");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        var part = path + ".part";
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(part))
        {
            var buffer = new byte[1 << 16];
            long done = 0, reported = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (done - reported >= 10 << 20)
                {
                    reported = done;
                    progress.Report($"{done >> 20} MB{(total is { } size ? $" / {size >> 20} MB" : string.Empty)}");
                }
            }
        }
        File.Move(part, path, overwrite: true);
        progress.Report($"saved {path} ({new FileInfo(path).Length >> 20} MB)");
    }
}
