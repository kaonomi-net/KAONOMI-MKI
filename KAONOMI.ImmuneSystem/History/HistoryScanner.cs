namespace KAONOMI.ImmuneSystem.History;

using System.Runtime.InteropServices;
using KAONOMI.ImmuneSystem.Images;
using KAONOMI.ImmuneSystem.Office;
using Microsoft.Data.Sqlite;

/// <summary>
/// Reads the browser histories of the user: the Chromium browsers (Chrome, Edge, Brave, Arc, Vivaldi, Opera, Dia…),
/// Firefox (and Zen, LibreWolf) and Safari. Each database is copied first (the browser keeps it locked), with its
/// write-ahead log, and the copy is deleted after. Only the visits to generative AI services are kept, as counts per
/// service and per month (no address, no page title), and the images and Office files downloaded, with their source.
/// </summary>
public static class HistoryScanner
{
    private sealed record Source(string Browser, string Profile, string Database, string Engine, bool Protected = false);

    private static readonly DateTime ChromeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SafariEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Reads every profile found under <paramref name="home"/>; <paramref name="found"/> is told each AI service seen.</summary>
    public static BrowserReport Scan(string home, Action<AiServiceUse>? found = null, CancellationToken token = default)
    {
        var profiles = new List<BrowserProfile>();
        var visits = new List<(AiServiceInfo Service, DateTime Time, string Browser)>();
        var downloads = new List<BrowserDownload>();
        var temp = Path.Combine(Path.GetTempPath(), $"kaonomi-immune-{Guid.NewGuid():N}");
        try
        {
            foreach (var source in Sources(home))
            {
                if (token.IsCancellationRequested)
                    break;
                profiles.Add(Read(source, temp, visits, downloads));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // A copy left in the temporary folder: the system cleans it.
            }
        }

        var services = visits.GroupBy(v => v.Service.Name).Select(g => new AiServiceUse
        {
            Name = g.Key,
            Category = g.First().Service.Category,
            Visits = g.Count(),
            FirstVisit = g.Min(v => v.Time),
            LastVisit = g.Max(v => v.Time),
            Browsers = g.Select(v => v.Browser).Distinct().Order().ToList(),
            Months = g.GroupBy(v => v.Time.ToString("yyyy-MM")).OrderBy(m => m.Key).ToDictionary(m => m.Key, m => (long)m.Count()),
        }).OrderByDescending(s => s.Visits).ToList();
        foreach (var s in services)
            found?.Invoke(s);

        var readable = profiles.Where(p => p.Readable).ToList();
        return new BrowserReport
        {
            Profiles = profiles,
            AiServices = services,
            Downloads = downloads.OrderByDescending(d => d.Time).ToList(),
            Visits = readable.Sum(p => p.Visits),
            AiVisits = visits.Count,
            From = readable.Where(p => p.From is not null).Select(p => p.From).Min(),
            To = readable.Where(p => p.To is not null).Select(p => p.To).Max(),
        };
    }

    // ---- Where the histories are ------------------------------------------------------------------------------

    private static IEnumerable<Source> Sources(string home)
    {
        var mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var support = Path.Combine(home, "Library", "Application Support");
        var local = windows ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : "";
        var roaming = windows ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : "";
        var config = Path.Combine(home, ".config");

        (string Browser, string Folder)[] chromium = mac
            ?
            [
                ("Chrome", Path.Combine(support, "Google", "Chrome")), ("Chrome Beta", Path.Combine(support, "Google", "Chrome Beta")),
                ("Chrome Canary", Path.Combine(support, "Google", "Chrome Canary")), ("Chromium", Path.Combine(support, "Chromium")),
                ("Edge", Path.Combine(support, "Microsoft Edge")), ("Brave", Path.Combine(support, "BraveSoftware", "Brave-Browser")),
                ("Arc", Path.Combine(support, "Arc", "User Data")), ("Vivaldi", Path.Combine(support, "Vivaldi")),
                ("Opera", Path.Combine(support, "com.operasoftware.Opera")), ("Opera GX", Path.Combine(support, "com.operasoftware.OperaGX")),
                ("Dia", Path.Combine(support, "Dia", "User Data")), ("Comet", Path.Combine(support, "Comet")),
            ]
            : windows
            ?
            [
                ("Chrome", Path.Combine(local, "Google", "Chrome", "User Data")), ("Chromium", Path.Combine(local, "Chromium", "User Data")),
                ("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")), ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
                ("Vivaldi", Path.Combine(local, "Vivaldi", "User Data")), ("Opera", Path.Combine(roaming, "Opera Software", "Opera Stable")),
                ("Opera GX", Path.Combine(roaming, "Opera Software", "Opera GX Stable")), ("Arc", Path.Combine(local, "Packages", "TheBrowserCompany.Arc_ttt1ap7aakyb4", "LocalCache", "Local", "Arc", "User Data")),
            ]
            :
            [
                ("Chrome", Path.Combine(config, "google-chrome")), ("Chromium", Path.Combine(config, "chromium")),
                ("Edge", Path.Combine(config, "microsoft-edge")), ("Brave", Path.Combine(config, "BraveSoftware", "Brave-Browser")),
                ("Vivaldi", Path.Combine(config, "vivaldi")), ("Opera", Path.Combine(config, "opera")),
            ];

        foreach (var (browser, folder) in chromium)
            foreach (var (profile, db, locked) in Profiles(folder, "History"))
                yield return new Source(browser, profile, db, "chromium", locked);

        (string Browser, string Folder)[] gecko = mac
            ?
            [
                ("Firefox", Path.Combine(support, "Firefox", "Profiles")), ("Zen", Path.Combine(support, "zen", "Profiles")),
                ("LibreWolf", Path.Combine(support, "librewolf", "Profiles")),
            ]
            : windows
            ? [("Firefox", Path.Combine(roaming, "Mozilla", "Firefox", "Profiles")), ("Zen", Path.Combine(roaming, "zen", "Profiles"))]
            : [("Firefox", Path.Combine(home, ".mozilla", "firefox")), ("LibreWolf", Path.Combine(home, ".librewolf"))];
        foreach (var (browser, folder) in gecko)
            foreach (var (profile, db, locked) in Profiles(folder, "places.sqlite"))
                yield return new Source(browser, profile, db, "firefox", locked);

        if (mac)
        {
            var safari = Path.Combine(home, "Library", "Safari", "History.db");
            if (File.Exists(safari) || Directory.Exists(Path.GetDirectoryName(safari)))
                yield return new Source("Safari", "Default", safari, "safari");
        }
    }

    /// <summary>
    /// The profiles of a browser folder: the folder itself or its sub-folders holding <paramref name="file"/>; the folder
    /// alone, marked protected, when it cannot be listed.
    /// </summary>
    private static List<(string Profile, string Database, bool Protected)> Profiles(string folder, string file)
    {
        var list = new List<(string, string, bool)>();
        try
        {
            if (!Directory.Exists(folder))
                return list;
            if (File.Exists(Path.Combine(folder, file)))
                list.Add((Path.GetFileName(folder), Path.Combine(folder, file), false));
            foreach (var dir in Directory.EnumerateDirectories(folder))
                if (File.Exists(Path.Combine(dir, file)))
                    list.Add((Path.GetFileName(dir), Path.Combine(dir, file), false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            list.Add(("(all profiles)", folder, true));
        }
        return list;
    }

    // ---- Reading one history ----------------------------------------------------------------------------------

    private static BrowserProfile Read(Source source, string temp, List<(AiServiceInfo, DateTime, string)> aiVisits, List<BrowserDownload> downloads)
    {
        string copy;
        try
        {
            if (source.Protected)
                throw new UnauthorizedAccessException($"{source.Database} cannot be listed");
            copy = Copy(source, temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            return new BrowserProfile
            {
                Browser = source.Browser,
                Profile = source.Profile,
                Path = source.Database,
                Readable = false,
                Error = mac && (ex is UnauthorizedAccessException || ex.Message.Contains("not permitted", StringComparison.OrdinalIgnoreCase))
                    ? "Protected by macOS: give Full Disk Access (System Settings › Privacy & Security) to the app that runs the scan"
                    : ex.Message,
            };
        }

        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            db.Open();
            var times = new List<DateTime>();
            var ai = 0L;
            foreach (var (url, time) in Visits(db, source.Engine))
            {
                times.Add(time);
                if (AiServices.Of(url) is { } service)
                {
                    aiVisits.Add((service, time, source.Browser));
                    ai++;
                }
            }
            downloads.AddRange(Downloads(db, source));
            return new BrowserProfile
            {
                Browser = source.Browser,
                Profile = source.Profile,
                Path = source.Database,
                Readable = true,
                Visits = times.Count,
                AiVisits = ai,
                From = times.Count > 0 ? times.Min() : null,
                To = times.Count > 0 ? times.Max() : null,
            };
        }
        catch (SqliteException ex)
        {
            return new BrowserProfile { Browser = source.Browser, Profile = source.Profile, Path = source.Database, Readable = false, Error = ex.Message };
        }
    }

    private static string Copy(Source source, string temp)
    {
        var folder = Path.Combine(temp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(source.Database));
        File.Copy(source.Database, copy);
        foreach (var suffix in new[] { "-wal", "-journal" })
            if (File.Exists(source.Database + suffix))
                File.Copy(source.Database + suffix, copy + suffix);
        return copy;
    }

    private static IEnumerable<(string Url, DateTime Time)> Visits(SqliteConnection db, string engine)
    {
        var sql = engine switch
        {
            "chromium" => "SELECT u.url, v.visit_time FROM visits v JOIN urls u ON u.id = v.url",
            "firefox" => "SELECT p.url, v.visit_date FROM moz_historyvisits v JOIN moz_places p ON p.id = v.place_id",
            _ => "SELECT i.url, v.visit_time FROM history_visits v JOIN history_items i ON i.id = v.history_item",
        };
        using var command = db.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                continue;
            yield return (reader.GetString(0), Time(engine, reader.GetValue(1)));
        }
    }

    private static DateTime Time(string engine, object value)
    {
        var n = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        var utc = engine switch
        {
            "chromium" => ChromeEpoch.AddTicks((long)n * 10),
            "firefox" => DateTime.UnixEpoch.AddTicks((long)n * 10),
            _ => SafariEpoch.AddSeconds(n),
        };
        return utc.ToLocalTime();
    }

    private static IEnumerable<BrowserDownload> Downloads(SqliteConnection db, Source source)
    {
        var rows = new List<(string File, string? Source, string? Referrer, DateTime? Time, long? Size)>();
        try
        {
            using var command = db.CreateCommand();
            if (source.Engine == "chromium")
            {
                command.CommandText = """
                    SELECT d.target_path,
                           (SELECT c.url FROM downloads_url_chains c WHERE c.id = d.id ORDER BY c.chain_index DESC LIMIT 1),
                           COALESCE(NULLIF(d.referrer, ''), NULLIF(d.tab_url, '')), d.start_time, d.total_bytes
                    FROM downloads d WHERE d.target_path <> ''
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) || reader.GetInt64(3) == 0 ? null : Time("chromium", reader.GetValue(3)), reader.IsDBNull(4) ? null : reader.GetInt64(4)));
            }
            else if (source.Engine == "firefox")
            {
                command.CommandText = """
                    SELECT a.content, p.url, a.dateAdded FROM moz_annos a
                    JOIN moz_anno_attributes n ON n.id = a.anno_attribute_id JOIN moz_places p ON p.id = a.place_id
                    WHERE n.name = 'downloads/destinationFileURI'
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var uri = reader.GetString(0);
                    var file = Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : uri;
                    rows.Add((file, reader.GetString(1), null, reader.IsDBNull(2) ? null : Time("firefox", reader.GetValue(2)), null));
                }
            }
        }
        catch (SqliteException)
        {
            // A history without downloads (Safari keeps them elsewhere).
        }

        foreach (var row in rows)
        {
            var ext = Path.GetExtension(row.File);
            var kind = AiImageDetector.Extensions.Contains(ext) ? "image" : OfficeInspector.Extensions.Contains(ext) ? "office" : null;
            if (kind is null)
                continue;
            yield return new BrowserDownload
            {
                Browser = source.Browser,
                Profile = source.Profile,
                File = row.File,
                Kind = kind,
                Source = row.Source,
                Referrer = row.Referrer,
                Time = row.Time,
                Size = row.Size,
                Exists = File.Exists(row.File),
                Service = (AiServices.Of(row.Source) ?? AiServices.Of(row.Referrer))?.Name,
            };
        }
    }
}
