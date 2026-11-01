using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Keeps the machine from going to sleep while the chat runs (<c>keepawake</c> in config.yml): on macOS a
/// <c>caffeinate -i -w &lt;pid&gt;</c> holds the idle sleep off for as long as this process lives (even if it ends
/// abruptly); on Windows <c>SetThreadExecutionState</c> marks the system as required; on Linux a
/// <c>systemd-inhibit</c> blocks idle and sleep. The screen can still turn off; only the machine stays awake.
/// </summary>
public static class SleepGuard
{
    private static Process? _inhibitor;
    private static bool _windows;

    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    /// <summary>True while the sleep of the machine is held off.</summary>
    public static bool IsActive => _windows || _inhibitor is { HasExited: false };

    /// <summary>Holds the sleep of the machine off until <see cref="Stop"/> (or the end of the process).</summary>
    public static void Start()
    {
        if (IsActive)
            return;
        try
        {
            var pid = Environment.ProcessId;
            if (OperatingSystem.IsWindows())
            {
                _windows = SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired) != 0;
            }
            else if (OperatingSystem.IsMacOS())
            {
                // -i: no idle sleep of the system; -w: until this process ends.
                _inhibitor = Launch("/usr/bin/caffeinate", $"-i -w {pid}");
            }
            else if (OperatingSystem.IsLinux())
            {
                _inhibitor = Launch("systemd-inhibit",
                    $"--what=idle:sleep --who=Kaonomi --why=\"The Kaonomi chat is running\" --mode=block tail --pid={pid} -f /dev/null");
            }
            ActivityLog.Info("System", IsActive ? "The machine is kept awake while the chat runs" : "The machine could not be kept awake");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
        {
            ActivityLog.Error("System", "The machine could not be kept awake", ex);
        }
    }

    /// <summary>Lets the machine sleep again.</summary>
    public static void Stop()
    {
        try
        {
            if (_windows)
            {
                SetThreadExecutionState(ExecutionState.Continuous);
                _windows = false;
            }
            if (_inhibitor is { HasExited: false } inhibitor)
                inhibitor.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already ended.
        }
        _inhibitor?.Dispose();
        _inhibitor = null;
    }

    private static Process? Launch(string program, string arguments) => Process.Start(new ProcessStartInfo(program, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    });
}
