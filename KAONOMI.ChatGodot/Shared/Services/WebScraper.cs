using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>A page read by the web scraper: what it says, the passages that hold the keywords, or why it could not be read.</summary>
public sealed record ScrapedPage(
    string Url,
    string Website,
    string Mode,
    IReadOnlyList<string> Keywords,
    string Title,
    string Description,
    string Text,
    IReadOnlyList<string> Excerpts,
    int KeywordHits,
    string? Error)
{
    public bool Ok => Error is null;

    /// <summary>The links of the content of the page with a text of their own (the headlines of the articles it lists), to cite real addresses.</summary>
    public IReadOnlyList<(string Text, string Url)> Headlines { get; init; } = [];
}

/// <summary>
/// The web scraper of the collection plans: for each URL of each website, by its mode, <c>fixed</c> reads the page
/// alone, <c>explore</c> the page and the pages of the same site it links to (those whose link holds a keyword first),
/// <c>random</c> the page and a few pages of the same site it links to, picked at random. Of each page it keeps the
/// title, the description, the readable text and the passages holding the keywords. It is polite: it tells who it is,
/// follows robots.txt, waits between two pages of a site, reads at most a few megabytes a page and a few pages a URL.
/// </summary>
public sealed partial class WebScraper
{
    public const string UserAgent = "KAONOMI-WebScraper/1.0 (+https://www.kaonomi.net)";

    /// <summary>The pages read beyond the first one, for explore and random.</summary>
    public const int ExplorePages = 8, RandomPages = 4;

    public const int MaxBytes = 3 * 1024 * 1024;

    public static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(20);

    /// <summary>The pause between two pages of the same site.</summary>
    public static readonly TimeSpan Politeness = TimeSpan.FromMilliseconds(700);

    // The pages in windows-1252, ISO-8859-x… are read too.
    static WebScraper() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly Dictionary<string, List<string>> _robots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastVisit = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new();

    /// <param name="log">Told each step (a page read, skipped, failed), for the window.</param>
    /// <param name="http">The client (a fake one for the tests); a client of its own by default.</param>
    public WebScraper(Action<string>? log = null, HttpClient? http = null)
    {
        _log = log ?? (_ => { });
        _http = http ?? CreateClient();
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        var client = new HttpClient(handler) { Timeout = PageTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,text/plain;q=0.8,*/*;q=0.5");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr,en;q=0.8");
        return client;
    }

    /// <summary>Reads every URL of every website of <paramref name="plan"/>; the pages read, in their order.</summary>
    public async Task<List<ScrapedPage>> CollectAsync(CollectionPlanEntry plan, CancellationToken cancellationToken)
    {
        var pages = new List<ScrapedPage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var website in plan.Websites ?? [])
        {
            foreach (var process in website.UrlProcesses ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryUri(process.Url, out var start))
                {
                    if (!string.IsNullOrWhiteSpace(process.Url))
                        _log($"✕ {process.Url}: not a web address");
                    continue;
                }
                var name = string.IsNullOrWhiteSpace(website.Name) ? start.Host : website.Name!;
                var mode = UrlProcessEntry.Modes.Contains(process.Mode) ? process.Mode! : "fixed";
                var keywords = (process.Keywords ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList();
                var (first, links) = await ReadAsync(start, name, mode, keywords, seen, cancellationToken);
                if (first is not null)
                    pages.Add(first);
                if (mode == "fixed" || links.Count == 0)
                    continue;

                // The pages it links to on the same site: by the keywords of their link (explore), or at random.
                var sameSite = links.Where(l => SameSite(l.Uri, start) && !seen.Contains(Key(l.Uri))).ToList();
                var chosen = mode == "explore"
                    ? sameSite.OrderByDescending(l => Hits(l.Text + " " + l.Uri, keywords)).ThenBy(l => l.Uri.AbsolutePath.Length).Take(ExplorePages).ToList()
                    : sameSite.OrderBy(_ => _random.Next()).Take(RandomPages).ToList();
                foreach (var link in chosen)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (page, _) = await ReadAsync(link.Uri, name, mode, keywords, seen, cancellationToken);
                    if (page is not null)
                        pages.Add(page);
                }
            }
        }
        return pages;
    }

    /// <summary>Reads one page (unless seen already or forbidden by robots.txt): the page and its links.</summary>
    private async Task<(ScrapedPage? Page, List<(Uri Uri, string Text)> Links)> ReadAsync(Uri uri, string website, string mode,
        IReadOnlyList<string> keywords, HashSet<string> seen, CancellationToken cancellationToken)
    {
        if (!seen.Add(Key(uri)))
            return (null, []);
        if (!await AllowedAsync(uri, cancellationToken))
        {
            _log($"⊘ {uri}: forbidden by robots.txt");
            return (Failed(uri, website, mode, keywords, "forbidden by robots.txt"), []);
        }
        await WaitTurnAsync(uri, cancellationToken);
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log($"✕ {uri}: {(int)response.StatusCode} {response.ReasonPhrase}");
                return (Failed(uri, website, mode, keywords, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"), []);
            }
            var type = response.Content.Headers.ContentType?.MediaType ?? "text/html";
            if (!type.Contains("html", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                _log($"⊘ {uri}: {type}, not a page of text");
                return (Failed(uri, website, mode, keywords, $"{type}: not a page of text"), []);
            }
            var bytes = await ReadLimitedAsync(response, cancellationToken);
            var charset = response.Content.Headers.ContentType?.CharSet;
            var html = Decode(bytes, charset);
            var final = response.RequestMessage?.RequestUri ?? uri;
            var isHtml = type.Contains("html", StringComparison.OrdinalIgnoreCase);
            var title = isHtml ? Title(html) : string.Empty;
            var description = isHtml ? MetaDescription(html) : string.Empty;
            var text = isHtml ? Text(html) : Clean(html);
            var excerpts = Excerpts(text, keywords);
            var hits = Hits(title + "\n" + text, keywords);
            var links = isHtml ? Links(html, final) : [];
            var headlines = isHtml ? Headlines(html, final) : [];
            _log($"✓ {final} · {bytes.Length / 1024.0:0.#} kB · {(title.Length > 0 ? title : "(no title)")}{(keywords.Count > 0 ? $" · {hits} keyword hit(s)" : string.Empty)}{(headlines.Count > 0 ? $" · {headlines.Count} headline link(s)" : string.Empty)}");
            return (new ScrapedPage(final.ToString(), website, mode, keywords, title, description, text, excerpts, hits, null) { Headlines = headlines }, links);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var why = ex is TaskCanceledException ? $"no answer within {PageTimeout.TotalSeconds:0} s" : ex.Message;
            _log($"✕ {uri}: {why}");
            return (Failed(uri, website, mode, keywords, why), []);
        }
    }

    private static ScrapedPage Failed(Uri uri, string website, string mode, IReadOnlyList<string> keywords, string why) =>
        new(uri.ToString(), website, mode, keywords, string.Empty, string.Empty, string.Empty, [], 0, why);

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new System.IO.MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length >= MaxBytes)
                break;
        }
        return buffer.ToArray();
    }

    private async Task WaitTurnAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (_lastVisit.TryGetValue(uri.Host, out var last))
        {
            var wait = last + Politeness - DateTimeOffset.Now;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);
        }
        _lastVisit[uri.Host] = DateTimeOffset.Now;
    }

    // ── robots.txt ──

    private async Task<bool> AllowedAsync(Uri uri, CancellationToken cancellationToken)
    {
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!_robots.TryGetValue(origin, out var disallowed))
        {
            disallowed = [];
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await _http.GetAsync(origin + "/robots.txt", timeout.Token);
                if (response.IsSuccessStatusCode)
                    disallowed = ParseRobots(await response.Content.ReadAsStringAsync(timeout.Token), "KAONOMI-WebScraper");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // No robots.txt to read: everything is allowed.
            }
            _robots[origin] = disallowed;
        }
        var path = uri.PathAndQuery;
        return !disallowed.Any(prefix => prefix.Length > 0 && path.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>The paths robots.txt forbids to <paramref name="agent"/> (its own group, else the group of *).</summary>
    public static List<string> ParseRobots(string text, string agent)
    {
        var groups = new List<(List<string> Agents, List<string> Disallow, List<string> Allow)>();
        (List<string> Agents, List<string> Disallow, List<string> Allow)? current = null;
        var lastWasAgent = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (field == "user-agent")
            {
                if (current is null || !lastWasAgent)
                {
                    current = ([], [], []);
                    groups.Add(current.Value);
                }
                current.Value.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }
            lastWasAgent = false;
            if (current is null)
                continue;
            if (field == "disallow" && value.Length > 0)
                current.Value.Disallow.Add(value.TrimEnd('*'));
            else if (field == "allow" && value.Length > 0)
                current.Value.Allow.Add(value);
        }
        var mine = groups.FirstOrDefault(g => g.Agents.Any(a => a != "*" && agent.Contains(a, StringComparison.OrdinalIgnoreCase)));
        if (mine.Agents is null)
            mine = groups.FirstOrDefault(g => g.Agents.Contains("*"));
        return mine.Disallow ?? [];
    }

    // ── HTML ──

    [GeneratedRegex(@"<(script|style|noscript|svg|template|iframe|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<(nav|footer|aside|form)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Chrome();

    [GeneratedRegex(@"<main\b[^>]*>(.*)</main\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MainTag();

    [GeneratedRegex(@"<article\b[^>]*>(.*?)</article\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ArticleTag();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"(\w[\w:-]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.Singleline)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<a\b([^>]*)>(.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex(@"<(?:/?(?:p|div|section|article|main|header|h[1-6]|li|ul|ol|tr|table|blockquote|pre|dd|dt|figcaption)|br|hr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();

    private static string Decode(byte[] bytes, string? charset)
    {
        Encoding encoding = Encoding.UTF8;
        charset ??= MetaCharset().Match(Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096))) is { Success: true } m ? m.Groups[1].Value : null;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"', '\''));
            }
            catch (ArgumentException)
            {
                encoding = Encoding.UTF8;
            }
        }
        return encoding.GetString(bytes);
    }

    public static string Title(string html) =>
        TitleTag().Match(html) is { Success: true } m ? Clean(WebUtility.HtmlDecode(AnyTag().Replace(m.Groups[1].Value, " "))) : string.Empty;

    /// <summary>The description of the page (<c>meta name="description"</c> or <c>og:description</c>).</summary>
    public static string MetaDescription(string html)
    {
        foreach (Match meta in MetaTag().Matches(html))
        {
            var attributes = Attributes(meta.Value);
            var name = attributes.GetValueOrDefault("name") ?? attributes.GetValueOrDefault("property") ?? string.Empty;
            if (name.Equals("description", StringComparison.OrdinalIgnoreCase) || name.Equals("og:description", StringComparison.OrdinalIgnoreCase))
                return Clean(WebUtility.HtmlDecode(attributes.GetValueOrDefault("content") ?? string.Empty));
        }
        return string.Empty;
    }

    private static Dictionary<string, string> Attributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in Attribute().Matches(tag))
            attributes[a.Groups[1].Value] = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
        return attributes;
    }

    /// <summary>The readable text of a page: one line per paragraph, without the scripts, the styles, the menus and the footers.</summary>
    public static string Text(string html)
    {
        var body = Comments().Replace(html, " ");
        // The content of the page when it says where it is (<main>, else its only <article>): not the menus around it.
        if (MainTag().Match(body) is { Success: true } main && AnyTag().Replace(main.Groups[1].Value, " ").Trim().Length > 200)
            body = main.Groups[1].Value;
        else if (ArticleTag().Matches(body) is { Count: 1 } articles && AnyTag().Replace(articles[0].Groups[1].Value, " ").Trim().Length > 200)
            body = articles[0].Groups[1].Value;
        body = Hidden().Replace(body, " ");
        var withoutChrome = Chrome().Replace(body, " ");
        // A page made only of menus (rare) keeps them.
        if (AnyTag().Replace(withoutChrome, " ").Trim().Length > 80)
            body = withoutChrome;
        body = BlockTag().Replace(body, "\n");
        body = AnyTag().Replace(body, " ");
        body = WebUtility.HtmlDecode(body);
        var lines = body.Split('\n').Select(Clean).Where(l => l.Length > 1);
        // The same line twice in a row (a menu repeated) only once.
        var text = new StringBuilder();
        string? previous = null;
        foreach (var line in lines)
        {
            if (line == previous)
                continue;
            text.Append(line).Append('\n');
            previous = line;
        }
        return text.ToString().Trim();
    }

    private static string Clean(string text) => Spaces().Replace(text.Replace('\r', ' ').Replace('\t', ' '), " ").Trim();

    /// <summary>The links of a page (absolute, http or https, without their #fragment), with their text.</summary>
    public static List<(Uri Uri, string Text)> Links(string html, Uri page)
    {
        var links = new List<(Uri, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in Anchor().Matches(html))
        {
            var href = Attributes("<a " + a.Groups[1].Value + ">").GetValueOrDefault("href");
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(page, WebUtility.HtmlDecode(href.Trim()), out var uri) || uri.Scheme is not ("http" or "https"))
                continue;
            uri = new UriBuilder(uri) { Fragment = string.Empty }.Uri;
            if (!seen.Add(Key(uri)) || Skipped(uri))
                continue;
            links.Add((uri, Clean(WebUtility.HtmlDecode(AnyTag().Replace(a.Groups[2].Value, " ")))));
        }
        return links;
    }

    /// <summary>
    /// The links of the content of the page (inside &lt;main&gt; when there is one) whose text is a sentence of its own
    /// (25 characters at least): the headlines of the articles a page lists, with their real addresses; 80 at most.
    /// </summary>
    public static List<(string Text, string Url)> Headlines(string html, Uri page)
    {
        var content = MainTag().Match(html) is { Success: true } main ? main.Groups[1].Value : html;
        // The links to articles (a deep address: /politique/article/2026/11/03/…) first, the links of the services after.
        return Links(content, page)
            .Where(l => l.Text.Length >= 25 && l.Uri.Host.EndsWith(Host(page), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.Uri.AbsolutePath.Count(c => c == '/') >= 3 ? 1 : 0)
            .GroupBy(l => l.Text, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.First().Uri.ToString()))
            .Take(80)
            .ToList();
    }

    /// <summary>Links to files that are not pages (pictures, archives, documents).</summary>
    private static bool Skipped(Uri uri)
    {
        var path = uri.AbsolutePath.ToLowerInvariant();
        return new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".ico", ".zip", ".gz", ".tar", ".exe", ".dmg", ".mp3", ".mp4", ".avi", ".mov", ".css", ".js", ".woff", ".woff2", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx" }
            .Any(path.EndsWith);
    }

    private static string Key(Uri uri) => uri.GetLeftPart(UriPartial.Query).TrimEnd('/');

    private static bool SameSite(Uri a, Uri b) => string.Equals(Host(a), Host(b), StringComparison.OrdinalIgnoreCase);

    private static string Host(Uri uri) => uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;

    private static bool TryUri(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var address = text.Trim();
        if (!address.Contains("://", StringComparison.Ordinal))
            address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
            return false;
        uri = parsed;
        return true;
    }

    // ── Keywords ──

    /// <summary>The text without accents and in lower case, to find the keywords whatever their accents and case.</summary>
    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                folded.Append(char.ToLowerInvariant(ch));
        return folded.ToString();
    }

    public static int Hits(string text, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0)
            return 0;
        var folded = Fold(text);
        var hits = 0;
        foreach (var keyword in keywords.Select(Fold).Where(k => k.Length > 0))
        {
            for (var at = folded.IndexOf(keyword, StringComparison.Ordinal); at >= 0; at = folded.IndexOf(keyword, at + keyword.Length, StringComparison.Ordinal))
                hits++;
        }
        return hits;
    }

    /// <summary>
    /// The passages of <paramref name="text"/> that hold a keyword (each paragraph cut to about 700 characters around
    /// it), at most 15; without keywords, the first paragraphs (three times as many: the whole page matters).
    /// </summary>
    public static List<string> Excerpts(string text, IReadOnlyList<string> keywords, int most = 15)
    {
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => p.Length >= 30).ToList();
        // Without keywords, the whole page matters: more of it (a page listing articles).
        if (keywords.Count == 0)
            return paragraphs.Take(most * 3).Select(p => Cut(p, 0, 700)).ToList();
        var folded = keywords.Select(Fold).Where(k => k.Length > 0).ToList();
        var excerpts = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            var lower = Fold(paragraph);
            var at = folded.Select(k => lower.IndexOf(k, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
            if (at < 0)
                continue;
            excerpts.Add(Cut(paragraph, at, 700));
            if (excerpts.Count >= most)
                break;
        }
        return excerpts;
    }

    private static string Cut(string paragraph, int around, int length)
    {
        if (paragraph.Length <= length)
            return paragraph;
        var start = Math.Clamp(around - length / 3, 0, paragraph.Length - length);
        return (start > 0 ? "…" : string.Empty) + paragraph.Substring(start, length).Trim() + (start + length < paragraph.Length ? "…" : string.Empty);
    }
}
