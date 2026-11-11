using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Avalonia.Threading;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Watches the UI thread, shared by every window of the chat: a background thread posts a beat to it twice a second;
/// when a beat waits more than <see cref="FreezeThreshold"/>, every window is frozen. The freeze and its length are
/// written to <c>~/kaonomi/freeze.log</c> and to the activity log once the UI thread answers again, to find what blocks it.
/// </summary>
public static class UiWatchdog
{
    /// <summary>Wait of a beat beyond which the windows are considered frozen.</summary>
    private static readonly TimeSpan FreezeThreshold = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan BeatInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>File of the freezes, in the Kaonomi home folder.</summary>
    public static string Path => System.IO.Path.Combine(ChatSettings.KaonomiHome, "freeze.log");

    private static Thread? _thread;

    /// <summary>Starts the watch; to call once Avalonia is initialized.</summary>
    public static void Install()
    {
        if (_thread is not null)
            return;
        _thread = new Thread(Watch) { IsBackground = true, Name = "Kaonomi UI watchdog", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private static void Watch()
    {
        while (true)
        {
            var posted = Stopwatch.GetTimestamp();
            using var answered = new ManualResetEventSlim();
            Dispatcher.UIThread.Post(() => answered.Set(), DispatcherPriority.Send);

            if (!answered.Wait(FreezeThreshold))
            {
                var start = DateTime.Now;
                answered.Wait();
                Report(start, Stopwatch.GetElapsedTime(posted));
            }
            Thread.Sleep(BeatInterval);
        }
    }

    private static void Report(DateTime start, TimeSpan length)
    {
        var message = $"UI thread frozen for {length.TotalSeconds:0.0} s (every window blocked) from {start:HH:mm:ss}";
        try
        {
            Directory.CreateDirectory(ChatSettings.KaonomiHome);
            File.AppendAllText(Path, $"[{start:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing else to write to.
        }
        ActivityLog.Error("Freeze", message);
    }
}
