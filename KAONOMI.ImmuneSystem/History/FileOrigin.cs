namespace KAONOMI.ImmuneSystem.History;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

/// <summary>
/// Where a downloaded file comes from, as the system wrote it. On macOS: <c>kMDItemWhereFroms</c> (read with mdls), else
/// the quarantine attribute and its event in <c>QuarantineEventsV2</c> (the address, or at least the app that downloaded
/// it: "downloaded with Chrome"). On Windows: the <c>Zone.Identifier</c> stream (HostUrl, ReferrerUrl). Null when the
/// file says nothing.
/// </summary>
public static partial class FileOrigin
{
    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex Quoted();

    public static string? Of(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var zone = path + ":Zone.Identifier";
                if (!File.Exists(zone))
                    return null;
                var lines = File.ReadAllLines(zone);
                return lines.Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0] is "HostUrl" or "ReferrerUrl")
                    .OrderBy(p => p[0] == "HostUrl" ? 0 : 1).Select(p => p[1]).FirstOrDefault(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase));
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var start = new ProcessStartInfo("/usr/bin/mdls") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                start.ArgumentList.Add("-raw");
                start.ArgumentList.Add("-name");
                start.ArgumentList.Add("kMDItemWhereFroms");
                start.ArgumentList.Add(path);
                using var mdls = Process.Start(start);
                if (mdls is null)
                    return null;
                var output = mdls.StandardOutput.ReadToEnd();
                mdls.WaitForExit(5000);
                // The first address that is a web page (the second one is often the page that offered it).
                return Quoted().Matches(output).Select(m => m.Groups[1].Value).FirstOrDefault(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    ?? Quarantine(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        return null;
    }

    private static readonly Lazy<Dictionary<string, (string? Agent, string? Url)>> Events = new(LoadEvents);

    /// <summary>The quarantine attribute (<c>flags;time;agent;event id</c>) and its event: the address, else the app.</summary>
    private static string? Quarantine(string path)
    {
        var start = new ProcessStartInfo("/usr/bin/xattr") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add("com.apple.quarantine");
        start.ArgumentList.Add(path);
        using var xattr = Process.Start(start);
        if (xattr is null)
            return null;
        var parts = xattr.StandardOutput.ReadToEnd().Trim().Split(';');
        xattr.WaitForExit(5000);
        if (parts.Length < 3)
            return null;
        var agent = parts[2];
        if (parts.Length >= 4 && Events.Value.TryGetValue(parts[3], out var e))
        {
            if (e.Url is not null)
                return e.Url;
            agent = e.Agent ?? agent;
        }
        return agent.Length > 0 ? $"downloaded with {agent}" : null;
    }

    private static Dictionary<string, (string?, string?)> LoadEvents()
    {
        var events = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        var db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Preferences", "com.apple.LaunchServices.QuarantineEventsV2");
        if (!File.Exists(db))
            return events;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT LSQuarantineEventIdentifier, LSQuarantineAgentName, LSQuarantineDataURLString, LSQuarantineOriginURLString FROM LSQuarantineEvent";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) || reader.GetString(i).Length == 0 ? null : reader.GetString(i);
                var url = new[] { Text(2), Text(3) }.FirstOrDefault(u => u is not null && u.StartsWith("http", StringComparison.OrdinalIgnoreCase));
                if (Text(0) is { } id)
                    events[id] = (Text(1), url);
            }
        }
        catch (SqliteException)
        {
            // Unreadable: the app that downloaded is still in the attribute.
        }
        return events;
    }
}
