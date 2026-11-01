using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;

namespace KAONOMI.Chat.Services;

/// <summary>Tags of a track: title, artist, album, cover file and duration (any of them may be missing).</summary>
public sealed record TrackInfo(string Path, string? Title, string? Artist, string? Album, string? ArtworkPath, double Duration);

/// <summary>
/// Plays the tracks of <c>/music</c> in a helper process driven through its standard input and output (one command or
/// event per line):
/// <list type="bullet">
/// <item>macOS: <c>Assets/Audio/music-player.swift</c> (AVFoundation: MP3, AAC / M4A, ALAC, WAV, AIFF, FLAC, CAF), compiled once
/// with <c>swiftc</c> into the cache folder; seek, volume, loudness level, tags and cover. Without a Swift compiler, the
/// tracks are played with <c>afplay</c> (pause and resume, no seek, no level);</item>
/// <item>Windows: <c>Assets/Audio/music-player.ps1</c> (Windows Media Player through PowerShell); seek, volume and tags.</item>
/// </list>
/// The events are raised on a background thread.
/// </summary>
public sealed class MusicEngine : IDisposable
{
    /// <summary>Extensions of the tracks listed by <c>/music</c>.</summary>
    public static readonly HashSet<string> TrackExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".wav", ".aif", ".aiff", ".aifc", ".flac", ".caf", ".alac", ".wma", ".mp4"
    };

    private Process? _helper;
    private readonly object _gate = new();

    // afplay fallback (macOS without swiftc).
    private bool _useAfplay;
    private Process? _afplay;
    private string? _afplayPath;
    private double _afplayDuration;
    private float _volume = 0.8f;
    private readonly Stopwatch _afplayClock = new();
    private double _afplayOffset;
    private Timer? _afplayTimer;
    private bool _afplayPaused;

    /// <summary>A track is ready: its duration in seconds.</summary>
    public event Action<double>? Loaded;

    /// <summary>Four times a second while a track is loaded: position, duration, playing, loudness (0 to 1).</summary>
    public event Action<double, double, bool, double>? Position;

    /// <summary>The track played to its end.</summary>
    public event Action? Ended;

    /// <summary>The tags of a track asked with <see cref="RequestInfo"/>.</summary>
    public event Action<TrackInfo>? Info;

    /// <summary>A command failed (unreadable file, unknown format…).</summary>
    public event Action<string>? Error;

    /// <summary>Name of the engine in use, for the status bar.</summary>
    public string Backend { get; private set; } = "none";

    /// <summary>True when seeking inside a track is possible.</summary>
    public bool CanSeek => !_useAfplay;

    /// <summary>
    /// Starts the helper of this system (compiling it on macOS the first time, a second or two). Throws with the
    /// reason when no player can run here.
    /// </summary>
    public async Task StartAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            var helper = await Task.Run(CompileMacHelper);
            if (helper is not null)
            {
                StartHelper(new ProcessStartInfo(helper));
                Backend = "AVFoundation";
                return;
            }
            _useAfplay = true;
            Backend = "afplay (no seek: install the Xcode command line tools for the full player)";
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(Path.GetTempPath(), "kaonomi-music-player.ps1");
            await File.WriteAllTextAsync(script, ReadAsset("music-player.ps1"));
            StartHelper(new ProcessStartInfo("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-File", script]));
            Backend = "Windows Media Player";
            return;
        }

        throw new PlatformNotSupportedException("The music player runs on macOS and Windows only");
    }

    private static string ReadAsset(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://KAONOMI.Chat/Assets/Audio/{name}"));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The compiled macOS helper (in ~/Library/Caches/Kaonomi, named after the hash of its source, compiled when
    /// missing), or null when swiftc is not available or fails.
    /// </summary>
    private static string? CompileMacHelper()
    {
        var source = ReadAsset("music-player.swift");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "Kaonomi");
        var binary = Path.Combine(cache, $"music-player-{hash}");
        if (File.Exists(binary))
            return binary;

        try
        {
            Directory.CreateDirectory(cache);
            var sourceFile = Path.Combine(cache, "music-player.swift");
            File.WriteAllText(sourceFile, source);
            using var swiftc = Process.Start(new ProcessStartInfo("/usr/bin/swiftc", ["-O", sourceFile, "-o", binary])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (swiftc is null)
                return null;
            swiftc.StandardOutput.ReadToEnd();
            var errors = swiftc.StandardError.ReadToEnd();
            swiftc.WaitForExit(120_000);
            if (swiftc.ExitCode == 0 && File.Exists(binary))
                return binary;
            ActivityLog.Error("Music", $"The music player could not be compiled: {errors.Trim()}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ActivityLog.Error("Music", "The music player could not be compiled", ex);
        }
        return null;
    }

    private void StartHelper(ProcessStartInfo start)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.StandardInputEncoding = new UTF8Encoding(false);
        start.StandardOutputEncoding = Encoding.UTF8;
        var helper = Process.Start(start) ?? throw new InvalidOperationException("The music player could not be started");
        helper.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
                OnHelperLine(line);
        };
        helper.ErrorDataReceived += (_, _) => { };
        helper.BeginOutputReadLine();
        helper.BeginErrorReadLine();
        _helper = helper;
    }

    private void Send(string command)
    {
        lock (_gate)
        {
            try
            {
                _helper?.StandardInput.WriteLine(command);
                _helper?.StandardInput.Flush();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Error?.Invoke("The music player stopped");
            }
        }
    }

    private void OnHelperLine(string line)
    {
        var space = line.IndexOf(' ');
        var name = space < 0 ? line : line[..space];
        var rest = space < 0 ? string.Empty : line[(space + 1)..];
        switch (name)
        {
            case "loaded":
                Loaded?.Invoke(ParseDouble(rest));
                break;
            case "pos":
                var parts = rest.Split(' ');
                if (parts.Length >= 3)
                    Position?.Invoke(ParseDouble(parts[0]), ParseDouble(parts[1]), parts[2] == "1", parts.Length > 3 ? ParseDouble(parts[3]) : 0);
                break;
            case "ended":
                Ended?.Invoke();
                break;
            case "error":
                Error?.Invoke(rest);
                break;
            case "info":
                try
                {
                    using var json = JsonDocument.Parse(rest);
                    var root = json.RootElement;
                    string? Get(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 } text ? text : null;
                    Info?.Invoke(new TrackInfo(Get("path") ?? string.Empty, Get("title"), Get("artist"), Get("albumName"), Get("artwork"),
                        root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number ? duration.GetDouble() : 0));
                }
                catch (JsonException)
                {
                    // Tags are optional.
                }
                break;
        }
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;

    /// <summary>Loads a track (stopping the current one); <see cref="Loaded"/> follows.</summary>
    public void Load(string path)
    {
        if (!_useAfplay)
        {
            Send("load " + path);
            return;
        }
        StopAfplay();
        _afplayPath = path;
        _afplayOffset = 0;
        _afplayDuration = AfplayDuration(path);
        Loaded?.Invoke(_afplayDuration);
    }

    /// <summary>Plays (or resumes) the loaded track.</summary>
    public void Play()
    {
        if (!_useAfplay)
        {
            Send("play");
            return;
        }
        if (_afplay is { HasExited: false } running && _afplayPaused)
        {
            Signal(running, "CONT");
            _afplayPaused = false;
            _afplayClock.Start();
            return;
        }
        if (_afplayPath is null || _afplay is { HasExited: false })
            return;
        var process = Process.Start(new ProcessStartInfo("afplay", ["-v", _volume.ToString(CultureInfo.InvariantCulture), _afplayPath])
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null)
            return;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (ReferenceEquals(_afplay, process) && !_afplayPaused)
            {
                _afplayClock.Reset();
                _afplay = null;
                Ended?.Invoke();
            }
        };
        _afplay = process;
        _afplayClock.Restart();
        _afplayTimer ??= new Timer(_ => Position?.Invoke(_afplayOffset + _afplayClock.Elapsed.TotalSeconds, _afplayDuration,
            _afplay is { HasExited: false } && !_afplayPaused, 0), null, 250, 250);
    }

    /// <summary>Pauses the track.</summary>
    public void Pause()
    {
        if (!_useAfplay)
        {
            Send("pause");
            return;
        }
        if (_afplay is { HasExited: false } running && !_afplayPaused)
        {
            Signal(running, "STOP");
            _afplayPaused = true;
            _afplayClock.Stop();
        }
    }

    /// <summary>Stops the track and goes back to its start.</summary>
    public void Stop()
    {
        if (!_useAfplay)
            Send("stop");
        else
            StopAfplay();
    }

    /// <summary>Goes to <paramref name="seconds"/> in the track (not with afplay).</summary>
    public void Seek(double seconds)
    {
        if (!_useAfplay)
            Send("seek " + seconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Volume from 0 to 1 (with afplay, from the next track).</summary>
    public void SetVolume(double volume)
    {
        _volume = (float)Math.Clamp(volume, 0, 1);
        if (!_useAfplay)
            Send("volume " + _volume.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Asks the tags of a track; <see cref="Info"/> follows.</summary>
    public void RequestInfo(string path)
    {
        if (!_useAfplay)
            Send("info " + path);
    }

    private void StopAfplay()
    {
        var running = _afplay;
        _afplay = null;
        _afplayPaused = false;
        _afplayClock.Reset();
        if (running is { HasExited: false })
        {
            Signal(running, "CONT");
            try { running.Kill(); } catch (InvalidOperationException) { }
        }
    }

    private static void Signal(Process process, string signal)
    {
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", [$"-{signal}", process.Id.ToString(CultureInfo.InvariantCulture)]) { UseShellExecute = false });
            kill?.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The track has ended meanwhile.
        }
    }

    /// <summary>Duration of a track read by afinfo ("estimated duration: 123.4 sec"), 0 when unknown.</summary>
    private static double AfplayDuration(string path)
    {
        try
        {
            using var afinfo = Process.Start(new ProcessStartInfo("afinfo", [path]) { RedirectStandardOutput = true, UseShellExecute = false });
            var output = afinfo?.StandardOutput.ReadToEnd() ?? string.Empty;
            afinfo?.WaitForExit(5000);
            var line = output.Split('\n').FirstOrDefault(l => l.Contains("estimated duration", StringComparison.Ordinal));
            return line is null ? 0 : ParseDouble(new string(line.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _afplayTimer?.Dispose();
        StopAfplay();
        if (_helper is { HasExited: false } helper)
        {
            Send("quit");
            if (!helper.WaitForExit(1000))
            {
                try { helper.Kill(); } catch (InvalidOperationException) { }
            }
        }
        _helper?.Dispose();
        _helper = null;
    }
}
