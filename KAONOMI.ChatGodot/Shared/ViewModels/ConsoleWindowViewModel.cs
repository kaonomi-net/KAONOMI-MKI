using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// A console (<c>/cmd</c>): the shell of the system (cmd.exe on Windows, the login shell elsewhere) started in the home
/// folder with its input and its outputs redirected. A line typed is written to its input (the screen shows it already:
/// the shell does not repeat it); what it writes (output and errors) comes back in pieces through
/// <see cref="Output"/>, the colour codes of the terminals removed. There is no
/// terminal behind it: the programs that need one (a full-screen editor, a password prompt) do not work there.
/// STOP kills what runs and starts a new shell in the same folder; the shell and its children are killed when the
/// window closes, or when the chat ends. A note or an annotation written in the window has the shell, its folder and
/// the end of what it wrote as context.
/// </summary>
public sealed partial class ConsoleWindowViewModel : ViewModelBase, IDisposable, INoteContextProvider
{
    /// <summary>The shells still running, killed when the chat ends (a window not closed first).</summary>
    private static readonly ConcurrentDictionary<ConsoleWindowViewModel, byte> Running = new();

    static ConsoleWindowViewModel()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var console in Running.Keys)
                console.Kill();
        };
    }

    /// <summary>The escape sequences of the terminals (colours, cursor moves, titles).</summary>
    [GeneratedRegex(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex Escape();

    /// <summary>The prompt of cmd.exe at the end of what it wrote: the folder it is in, then <c>&gt;</c>.</summary>
    [GeneratedRegex(@"(?:^|\n)([A-Za-z]:\\[^\n>]*)>$")]
    private static partial Regex Prompt();

    private const int HistoryLength = 200;

    /// <summary>The end of what the shell wrote kept for the context of a note.</summary>
    private const int RecentLength = 4000;

    private readonly StringBuilder _recent = new();

    private readonly object _gate = new();
    private readonly List<string> _history = [];
    private int _historyIndex;
    private Process? _process;
    private CancellationTokenSource? _reading;
    private bool _disposed;

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isError;

    public ConsoleWindowViewModel()
    {
        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Start();
    }

    /// <summary>A piece of what the shell wrote (its output and its errors), on a thread of the pool.</summary>
    public event Action<string>? Output;

    /// <summary>Raised by CLEAR.</summary>
    public event Action? Cleared;

    /// <summary>The folder the shell starts in (the one of the shell killed by STOP, when it was known).</summary>
    public string WorkingDirectory { get; private set; }

    public string ShellName => OperatingSystem.IsWindows() ? "cmd.exe" : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";

    /// <inheritdoc />
    public string NoteWindowName => "Console";

    /// <inheritdoc />
    public object? GetNoteContext()
    {
        string recent;
        lock (_recent)
            recent = _recent.ToString();
        // The context names no user: the home folder (in the prompts too) is ~.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Anonymous(string? text) => string.IsNullOrEmpty(home) || text is null ? text ?? "" : text.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        return new
        {
            shell = ShellName,
            folder = Anonymous(WorkingDirectory),
            lastCommand = Anonymous(_history.Count > 0 ? _history[^1] : null),
            output = Anonymous(recent)
        };
    }

    /// <summary>Writes <see cref="Input"/> to the shell, as typed at its prompt.</summary>
    [RelayCommand]
    private void Send()
    {
        var line = Input;
        Input = "";
        if (line.Trim().Length > 0 && (_history.Count == 0 || _history[^1] != line))
        {
            _history.Add(line);
            if (_history.Count > HistoryLength)
                _history.RemoveAt(0);
        }
        _historyIndex = _history.Count;

        if (_process is not { HasExited: false })
        {
            Start();
            if (_process is null)
                return;
        }
        Write(line);
    }

    private void Write(string line)
    {
        if (_process is not { HasExited: false })
            return;
        try
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Report($"The shell does not read any more: {ex.Message}", error: true);
        }
    }

    /// <summary>STOP: kills the shell and what it runs, then starts a new one.</summary>
    [RelayCommand]
    private void Restart()
    {
        Kill();
        Output?.Invoke("^C\n");
        Start();
    }

    /// <summary>CLEAR: an empty screen, then a new prompt (an empty line asks the shell for one).</summary>
    [RelayCommand]
    private void Clear()
    {
        Cleared?.Invoke();
        Write("");
    }

    /// <summary>The line typed before (<paramref name="older"/>) or after the one shown, ↑ and ↓ at the prompt.</summary>
    public void Browse(bool older)
    {
        if (_history.Count == 0)
            return;
        _historyIndex = Math.Clamp(_historyIndex + (older ? -1 : 1), 0, _history.Count);
        Input = _historyIndex < _history.Count ? _history[_historyIndex] : "";
    }

    private void Start()
    {
        if (_disposed)
            return;
        var windows = OperatingSystem.IsWindows();
        // cmd.exe mangles the UTF-8 it reads from a pipe (chcp 65001): the ANSI code page of the system keeps the
        // accents and the euro both ways.
        var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        var encoding = windows ? Encoding.GetEncoding(codePage) : new UTF8Encoding(false);
        var info = new ProcessStartInfo(ShellName)
        {
            WorkingDirectory = Directory.Exists(WorkingDirectory) ? WorkingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            StandardInputEncoding = encoding
        };
        if (windows)
        {
            // /Q: the echo off, cmd.exe does not repeat the line it reads (its prompt, the folder, still shows).
            info.ArgumentList.Add("/Q");
            info.ArgumentList.Add("/K");
            info.ArgumentList.Add($"chcp {codePage} >nul");
        }
        else
        {
            info.ArgumentList.Add("-i");
            info.Environment["TERM"] = "dumb";
        }

        try
        {
            var process = Process.Start(info) ?? throw new InvalidOperationException($"{ShellName} did not start.");
            lock (_gate)
            {
                _process = process;
                _reading = new CancellationTokenSource();
            }
            Running[this] = 0;
            IsRunning = true;
            Report($"{ShellName} · {info.WorkingDirectory}");
            _ = PumpAsync(process, process.StandardOutput, _reading.Token);
            _ = PumpAsync(process, process.StandardError, _reading.Token);
            _ = WatchAsync(process);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            Report($"{ShellName} could not start: {ex.Message}", error: true);
        }
    }

    /// <summary>Hands what the shell writes to <see cref="Output"/> as it comes (a prompt ends with no new line).</summary>
    private async Task PumpAsync(Process process, StreamReader reader, CancellationToken cancel)
    {
        var buffer = new char[4096];
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancel);
                if (read == 0)
                    break;
                var text = Escape().Replace(new string(buffer, 0, read), "").Replace("\r\n", "\n").Replace('\r', '\n');
                if (text.Length == 0)
                    continue;
                // The folder of the prompt: the one STOP starts the new shell in.
                if (Prompt().Match(text) is { Success: true } prompt)
                    WorkingDirectory = prompt.Groups[1].Value;
                lock (_recent)
                {
                    _recent.Append(text);
                    if (_recent.Length > RecentLength)
                        _recent.Remove(0, _recent.Length - RecentLength);
                }
                Output?.Invoke(text);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private async Task WatchAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        lock (_gate)
        {
            if (!ReferenceEquals(process, _process))
                return;
        }
        Running.TryRemove(this, out _);
        IsRunning = false;
        if (!_disposed)
            Report($"{ShellName} ended (code {process.ExitCode}). Type a command to start a new one.", error: process.ExitCode != 0);
    }

    /// <summary>Kills the shell and every program it started.</summary>
    private void Kill()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
            _reading?.Cancel();
            _reading = null;
        }
        Running.TryRemove(this, out _);
        IsRunning = false;
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
        process.Dispose();
    }

    private void Report(string status, bool error = false)
    {
        Status = status;
        IsError = error;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Kill();
    }
}
