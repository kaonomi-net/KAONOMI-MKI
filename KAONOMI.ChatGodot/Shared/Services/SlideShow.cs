using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Plays a presentation (PowerPoint, Keynote…) in full screen: on macOS with Microsoft PowerPoint (or Keynote when
/// PowerPoint is not installed) driven by AppleScript, on Windows with PowerPoint in its slide show mode (<c>/s</c>).
/// </summary>
public static class SlideShow
{
    /// <summary>The presentation of <c>/bnp</c>: Pictures/kaonomi/presentation/Digitribe-AI.pptx.</summary>
    public static string BnpPresentation => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "kaonomi", "presentation", "Digitribe-AI.pptx");

    /// <summary>Starts the slide show of <paramref name="path"/>; null once it is started, else why it could not be.</summary>
    public static async Task<string?> StartAsync(string path)
    {
        if (!File.Exists(path))
            return $"The presentation {path} does not exist.";
        try
        {
            if (OperatingSystem.IsMacOS())
                return await StartOnMacAsync(path);
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("powerpnt.exe") { ArgumentList = { "/s", path }, UseShellExecute = true });
                return null;
            }
            return "The slide show needs PowerPoint (macOS or Windows).";
        }
        catch (Exception ex)
        {
            return $"The slide show could not start: {ex.Message}";
        }
    }

    /// <summary>
    /// macOS: the file is opened by the application (<c>open -a</c>: PowerPoint opens it without asking for the access to
    /// the folder), then AppleScript waits for it to be loaded and starts its slide show.
    /// </summary>
    private static async Task<string?> StartOnMacAsync(string path)
    {
        var name = Path.GetFileName(path);
        string application;
        string[] script;
        if (Directory.Exists("/Applications/Microsoft PowerPoint.app"))
        {
            application = "Microsoft PowerPoint";
            script = new[]
            {
                "on run argv",
                "tell application \"Microsoft PowerPoint\"",
                "repeat 60 times",
                "if exists presentation (item 1 of argv) then exit repeat",
                "delay 0.5",
                "end repeat",
                "activate",
                "run slide show slide show settings of presentation (item 1 of argv)",
                "end tell",
                "end run"
            };
        }
        else if (Directory.Exists("/Applications/Keynote.app"))
        {
            application = "Keynote";
            script = new[]
            {
                "on run argv",
                "tell application \"Keynote\"",
                "repeat 60 times",
                "if (count of documents) > 0 then exit repeat",
                "delay 0.5",
                "end repeat",
                "activate",
                "start front document from first slide of front document",
                "end tell",
                "end run"
            };
        }
        else
            return "Neither Microsoft PowerPoint nor Keynote is installed.";

        var open = await RunAsync(new ProcessStartInfo("open") { ArgumentList = { "-a", application, path } });
        if (open is not null)
            return $"{application} could not open {name}: {open}";
        var start = new ProcessStartInfo("osascript");
        foreach (var line in script)
        {
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add(line);
        }
        start.ArgumentList.Add(name);
        var error = await RunAsync(start);
        return error is null ? null : $"The slide show could not start: {error}";
    }

    /// <summary>Runs a process to its end: null when it succeeded, else what it wrote on its error output.</summary>
    private static async Task<string?> RunAsync(ProcessStartInfo start)
    {
        start.RedirectStandardError = true;
        start.RedirectStandardOutput = true;
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? null : (await error).Trim();
    }
}
