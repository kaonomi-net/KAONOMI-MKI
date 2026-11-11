using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Paths written on another system (Windows, macOS or Linux) brought to the standards of this one at startup: a path
/// in the home folder of the other system (<c>C:\Users\jerome\…</c>, <c>/Users/jerome/…</c>, <c>/home/jerome/…</c>,
/// <c>~/…</c>) becomes the same path in the home folder of this one, its standard folders (Music, Pictures, Documents,
/// Desktop, Videos / Movies, Downloads, also under OneDrive on Windows) becoming the ones of this system, and the Piper
/// executable of a venv following the layout of this system (<c>bin/piper</c> / <c>Scripts\piper.exe</c>).
/// </summary>
public static class OsPaths
{
    private static readonly Regex WindowsPath = new(@"^([A-Za-z]:|\\\\)");

    /// <summary>Home folder of another system: the drive and user folder, then the rest of the path.</summary>
    private static readonly Regex[] HomeFolders =
    [
        new(@"^[A-Za-z]:[\\/]+Users[\\/]+[^\\/]+(?<rest>[\\/].*)?$", RegexOptions.IgnoreCase),
        new(@"^/Users/[^/]+(?<rest>/.*)?$"),
        new(@"^/home/[^/]+(?<rest>/.*)?$"),
        new(@"^~(?<rest>[\\/].*)?$")
    ];

    /// <summary>Standard folders of the home folder, by their name on any system.</summary>
    private static readonly Dictionary<string, Func<string>> StandardFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Music"] = () => Special(Environment.SpecialFolder.MyMusic, "Music"),
        ["Pictures"] = () => Special(Environment.SpecialFolder.MyPictures, "Pictures"),
        ["Documents"] = () => Special(Environment.SpecialFolder.MyDocuments, "Documents"),
        ["Desktop"] = () => Special(Environment.SpecialFolder.DesktopDirectory, "Desktop"),
        ["Videos"] = Videos,
        ["Movies"] = Videos,
        ["Downloads"] = () => Path.Combine(Home, "Downloads")
    };

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Videos() => Special(Environment.SpecialFolder.MyVideos, OperatingSystem.IsMacOS() ? "Movies" : "Videos");

    /// <summary>A standard folder of this system, or <paramref name="name"/> in the home folder when it has none.</summary>
    private static string Special(Environment.SpecialFolder folder, string name) =>
        Environment.GetFolderPath(folder) is { Length: > 0 } path ? path : Path.Combine(Home, name);

    /// <summary>
    /// True when <paramref name="path"/> is written for another system: a drive letter, a UNC path or backslashes on
    /// macOS / Linux, a Unix absolute path on Windows, a Linux home folder (<c>/home/…</c>) on macOS, a macOS one
    /// (<c>/Users/…</c>) on Linux, and <c>~</c> (never expanded by the file system) everywhere.
    /// </summary>
    public static bool IsForOtherSystem(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith('~'))
            return true;
        if (OperatingSystem.IsWindows())
            return trimmed.StartsWith('/') && !trimmed.StartsWith("//", StringComparison.Ordinal);
        if (WindowsPath.IsMatch(trimmed) || trimmed.Contains('\\'))
            return true;
        return OperatingSystem.IsMacOS()
            ? trimmed.StartsWith("/home/", StringComparison.Ordinal)
            : trimmed.StartsWith("/Users/", StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="path"/> for this system when it is in the home folder of another one (see the class), null
    /// otherwise (another drive, a system folder…: nothing to map it to).
    /// </summary>
    public static string? Translate(string path)
    {
        var trimmed = path.Trim();
        var match = HomeFolders.Select(r => r.Match(trimmed)).FirstOrDefault(m => m.Success);
        if (match is null)
            return null;

        var segments = match.Groups["rest"].Value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).ToList();

        // Piper in a venv: bin/piper (macOS, Linux) <-> Scripts\piper.exe (Windows).
        if (segments.Count >= 2)
        {
            var (folder, file) = (segments[^2], segments[^1]);
            if (OperatingSystem.IsWindows() && folder == "bin" && file == "piper")
                (segments[^2], segments[^1]) = ("Scripts", "piper.exe");
            else if (!OperatingSystem.IsWindows() && folder.Equals("Scripts", StringComparison.OrdinalIgnoreCase)
                                                  && file.Equals("piper.exe", StringComparison.OrdinalIgnoreCase))
                (segments[^2], segments[^1]) = ("bin", "piper");
        }

        // A standard folder (OneDrive keeps them on Windows: OneDrive\Pictures, "OneDrive - Company"\Documents…).
        var start = segments.Count >= 2 && segments[0].StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)
                    && StandardFolders.ContainsKey(segments[1]) ? 1 : 0;
        if (segments.Count > start && StandardFolders.TryGetValue(segments[start], out var standard))
            return Path.Combine([standard(), .. segments.Skip(start + 1)]);

        return Path.Combine([Home, .. segments]);
    }

    /// <summary>Links of a Markdown text (<c>](path)</c>) written for another system, translated (see <see cref="Translate"/>).</summary>
    public static string TranslateLinks(string text) => MarkdownLink.Replace(text, match =>
    {
        var path = match.Groups["path"].Value;
        return IsForOtherSystem(path) && Translate(path) is { } translated
            ? $"]({translated})"
            : match.Value;
    });

    private static readonly Regex MarkdownLink = new(@"\]\((?<path>[^)\r\n]+)\)");
}
