using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Player of a YouTube video showing nothing but the video: a window of Chrome (or Edge, Brave, Chromium) in app mode
/// (no address bar, no tabs) on a black page holding the YouTube player, started at once with its sound.
/// <para>
/// YouTube refuses its player when the page has no web address (error 153, "video player configuration error"), so
/// the page is served by a small local web server (<c>http://127.0.0.1:port</c>) started with the first video. The
/// browser runs with a profile of its own (<c>~/kaonomi/youtube-player</c>), so that it can start the video with its
/// sound and leaves the browser of the user as it is. Without a Chromium browser, the page opens in the default browser
/// (a click may then be needed to start the video).
/// </para>
/// <para>
/// A bar above the video shows its title and a link icon: the page tells the chat, every moment, where that icon is
/// on the screen (<c>POST /anchor</c>), so that the red string of the links (<see cref="Views.LinkString"/>) can join the
/// player, and a click on it starts a string from the player (<c>POST /link</c>).
/// </para>
/// </summary>
public static class YoutubePlayer
{
    private static readonly object Gate = new();
    private static HttpListener? _server;
    private static int _port;

    private static readonly Regex VideoIdPattern = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    /// <summary>Plays the video <paramref name="videoId"/> in a window that shows only the video.</summary>
    public static void Play(string videoId, string? title)
    {
        if (!VideoIdPattern.IsMatch(videoId))
            throw new ArgumentException($"\"{videoId}\" is not a YouTube video id.", nameof(videoId));
        var url = $"{EnsureServer()}watch?v={videoId}&title={Uri.EscapeDataString(title ?? string.Empty)}";

        if (FindChromium() is { } browser)
        {
            var profile = Path.Combine(Models.ChatSettings.KaonomiHome, "youtube-player");
            Directory.CreateDirectory(profile);
            var start = new ProcessStartInfo(browser) { UseShellExecute = false };
            foreach (var argument in new[]
                     {
                         $"--app={url}", $"--user-data-dir={profile}", "--autoplay-policy=no-user-gesture-required",
                         "--window-size=1280,760", "--no-first-run", "--no-default-browser-check", "--disable-features=Translate"
                     })
                start.ArgumentList.Add(argument);
            Process.Start(start)?.Dispose();
            ActivityLog.Info("YouTube", $"Video {videoId} played in {Path.GetFileNameWithoutExtension(browser)} (app window)");
        }
        else
        {
            PictureLibrary.OpenWithSystem(url);
            ActivityLog.Info("YouTube", $"Video {videoId} played in the default browser (no Chromium browser found)");
        }
    }

    /// <summary>A Chrome, Edge, Brave or Chromium executable of this system (they have the app mode), or null.</summary>
    private static string? FindChromium()
    {
        IEnumerable<string> candidates;
        if (OperatingSystem.IsMacOS())
            candidates = new[] { "Google Chrome", "Microsoft Edge", "Brave Browser", "Chromium" }
                .SelectMany(name => new[]
                {
                    $"/Applications/{name}.app/Contents/MacOS/{name}",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", $"{name}.app", "Contents", "MacOS", name)
                });
        else if (OperatingSystem.IsWindows())
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            }.Where(r => r.Length > 0);
            candidates = roots.SelectMany(root => new[]
            {
                Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
            });
        }
        else
            candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Where(folder => folder.Length > 0)
                .SelectMany(folder => new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "brave-browser" }
                    .Select(name => Path.Combine(folder, name)));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Address of the local web server of the player (started the first time), ending with a slash.</summary>
    private static string EnsureServer()
    {
        lock (Gate)
        {
            if (_server is { IsListening: true })
                return $"http://127.0.0.1:{_port}/";

            // A free port of this machine, only reachable from it.
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _server = new HttpListener();
            _server.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _server.Start();
            _ = Task.Run(() => ServeAsync(_server));
            ActivityLog.Info("YouTube", $"Player page served on http://127.0.0.1:{_port}/");
            return $"http://127.0.0.1:{_port}/";
        }
    }

    /// <summary>Answers the requests of the player windows: <c>/watch?v=id&amp;title=…</c> gives the page of a video.</summary>
    private static async Task ServeAsync(HttpListener server)
    {
        while (server.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await server.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath is "/anchor" or "/link")
                {
                    await AnswerLinkAsync(context);
                    continue;
                }
                var id = context.Request.QueryString["v"] ?? string.Empty;
                if (context.Request.Url?.AbsolutePath != "/watch" || !VideoIdPattern.IsMatch(id))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }
                var body = Encoding.UTF8.GetBytes(Page(id, context.Request.QueryString["title"]));
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
            {
                // The window was closed during the answer.
            }
            catch (Exception ex)
            {
                // A request that cannot be read (not JSON, a wrong field…): answered 400, the loop goes on.
                ActivityLog.Error("YouTube", "A request to the player page failed", ex);
                try
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // Already answered or closed.
                }
            }
        }
    }

    /// <summary>
    /// The link icon of a player page: <c>/anchor</c> tells where it is on the screen (points, and the scale of the
    /// screen) and answers whether it is part of a string; <c>/link</c> starts a string from it.
    /// </summary>
    private static async Task AnswerLinkAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await reader.ReadToEndAsync()) as System.Text.Json.Nodes.JsonObject;
        var player = json?["player"]?.GetValue<string>() ?? string.Empty;
        var answer = "{}";
        if (context.Request.Url!.AbsolutePath == "/anchor" && json is not null && player.Length > 0)
        {
            double Number(string name) => json[name]?.GetValue<double>() ?? 0;
            var scale = Math.Max(1, Number("scale"));
            var rect = new Avalonia.PixelRect((int)Math.Round(Number("x") * scale), (int)Math.Round(Number("y") * scale),
                (int)Math.Round(Number("width") * scale), (int)Math.Round(Number("height") * scale));
            var anchor = Views.LinkAnchors.Report(player, json["videoId"]?.GetValue<string>() ?? string.Empty, json["title"]?.GetValue<string>(), rect);
            answer = anchor.Linking ? "{\"linking\":true}" : "{\"linking\":false}";
        }
        else if (player.Length > 0)
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!Views.LinkString.IsActive && Views.LinkAnchors.Player(player) is { } anchor)
                    Views.LinkString.Start(anchor);
            });
        var body = Encoding.UTF8.GetBytes(answer);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    /// <summary>Language of the player (its menus and the subtitles it offers): the one of the system.</summary>
    private static string Language => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is { Length: 2 } language ? language : "en";

    /// <summary>
    /// The page of a video: a dark bar with the title of the video and the link icon, then the YouTube player filling
    /// the rest, started at once in the language of the system, without subtitles (no related videos at the end,
    /// fullscreen allowed); the title of the video as the title of the window. The page tells the chat where its link
    /// icon is (and lights it while it is part of a string), and a click on it starts a string.
    /// </summary>
    private static string Page(string videoId, string? title)
    {
        var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? "YouTube" : title);
        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta name="referrer" content="strict-origin-when-cross-origin">
            <title>{{name}}</title>
            <style>
            html,body{margin:0;height:100%;background:#000;overflow:hidden}
            #bar{position:fixed;top:0;left:0;right:0;height:40px;background:#16181d;border-bottom:1px solid #2a2e36;display:flex;align-items:center;gap:10px;padding:0 8px 0 14px;box-sizing:border-box}
            #title{flex:1;color:#e5e7eb;font:600 13px -apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
            #link{width:34px;height:30px;border:0;border-radius:7px;background:transparent;display:flex;align-items:center;justify-content:center;cursor:pointer}
            #link:hover{background:#2a2e36}
            #link.linking{background:#7f1d1d}
            #link svg{width:17px;height:17px;fill:#d1d5db}
            #link.linking svg{fill:#fecaca}
            iframe{position:fixed;top:40px;left:0;width:100%;height:calc(100% - 40px);border:0}
            </style>
            </head>
            <body>
            <div id="bar">
              <div id="title">{{name}}</div>
              <button id="link" title="Link this video to other windows: click, then click the link icons of the other windows (Enter saves, Escape cancels)">
                <svg viewBox="0 0 24 24"><path d="M10.59,13.41C11,13.8 11,14.44 10.59,14.83C10.2,15.22 9.56,15.22 9.17,14.83C7.22,12.88 7.22,9.71 9.17,7.76L12.71,4.22C14.66,2.27 17.83,2.27 19.78,4.22C21.73,6.17 21.73,9.34 19.78,11.29L18.29,12.78C18.3,11.96 18.17,11.14 17.89,10.36L18.36,9.88C19.54,8.71 19.54,6.81 18.36,5.64C17.19,4.46 15.29,4.46 14.12,5.64L10.59,9.17C9.41,10.34 9.41,12.24 10.59,13.41M13.41,9.17C13.8,8.78 14.44,8.78 14.83,9.17C16.78,11.12 16.78,14.29 14.83,16.24L11.29,19.78C9.34,21.73 6.17,21.73 4.22,19.78C2.27,17.83 2.27,14.66 4.22,12.71L5.71,11.22C5.7,12.04 5.83,12.86 6.11,13.65L5.64,14.12C4.46,15.29 4.46,17.19 5.64,18.36C6.81,19.54 8.71,19.54 9.88,18.36L13.41,14.83C14.59,13.66 14.59,11.76 13.41,10.59C13,10.2 13,9.56 13.41,9.17Z"/></svg>
              </button>
            </div>
            <script>
            // Where the link icon is on the screen (points, and the scale of the screen), told to the chat every moment.
            const player = (self.crypto && crypto.randomUUID) ? crypto.randomUUID() : String(Math.random()).slice(2);
            const link = document.getElementById('link');
            function report() {
              const r = link.getBoundingClientRect();
              const top = window.screenY + (window.outerHeight - window.innerHeight);
              const left = window.screenX + (window.outerWidth - window.innerWidth) / 2;
              fetch('/anchor', { method: 'POST', body: JSON.stringify({
                player, videoId: {{System.Text.Json.JsonSerializer.Serialize(videoId)}}, title: {{System.Text.Json.JsonSerializer.Serialize(title ?? string.Empty)}},
                x: left + r.left, y: top + r.top, width: r.width, height: r.height, scale: window.devicePixelRatio || 1 }) })
                .then(a => a.json()).then(state => link.classList.toggle('linking', !!state.linking)).catch(() => {});
            }
            report();
            setInterval(report, 600);
            addEventListener('resize', report);
            link.addEventListener('click', () => { report(); fetch('/link', { method: 'POST', body: JSON.stringify({ player }) }).catch(() => {}); });
            </script>
            <iframe src="https://www.youtube.com/embed/{{videoId}}?autoplay=1&rel=0&playsinline=1&iv_load_policy=3&fs=1&cc_load_policy=0&hl={{Language}}"
                    allow="autoplay; encrypted-media; picture-in-picture; fullscreen; clipboard-write" allowfullscreen
                    referrerpolicy="strict-origin-when-cross-origin" title="{{name}}"></iframe>
            </body>
            </html>
            """;
    }
}
