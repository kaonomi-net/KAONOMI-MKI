using System;
using System.IO;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Trace of the exceptions that end the application (or that nobody observed): appended to <c>~/kaonomi/crash.log</c>
/// and sent to the activity log (type ERROR) when the server can still be reached. Without it, a crash under the
/// debugger or from the systray leaves no trace at all.
/// </summary>
public static class CrashLog
{
    /// <summary>File of the crash traces, in the Kaonomi home folder.</summary>
    public static string Path => System.IO.Path.Combine(ChatSettings.KaonomiHome, "crash.log");

    /// <summary>Installs the handlers of the unhandled exceptions of the process; to call first thing in Main.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("Unhandled exception", e.ExceptionObject as Exception, fatal: e.IsTerminating);
        // A faulted Task nobody awaited: logged, and kept from ending the process.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Unobserved task exception", e.Exception, fatal: false);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Installs the handler of the exceptions of the UI thread (event handlers, async void, timers, commands); to call
    /// once Avalonia is initialized. They are logged and the application goes on, instead of ending.
    /// </summary>
    public static void InstallUi()
    {
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Write("UI thread exception", e.Exception, fatal: false);
            e.Handled = true;
        };
    }

    /// <summary>Writes the trace of <paramref name="exception"/> to the crash file and to the activity log.</summary>
    public static void Write(string context, Exception? exception, bool fatal)
    {
        var text = exception?.ToString() ?? "(no exception object)";
        try
        {
            Directory.CreateDirectory(ChatSettings.KaonomiHome);
            File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{(fatal ? " (fatal)" : "")}{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing else to write to.
        }

        ActivityLog.Error("Crash", $"{context}{(fatal ? " (fatal)" : "")}: {ActivityLog.Excerpt(text, 900)}");
        if (fatal)
            ActivityLog.Flush(TimeSpan.FromSeconds(3));
    }
}
