using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KAONOMI.Core;
using KAONOMI.Core.Models;
using Microsoft.Playwright;

namespace KAONOMI.Agent.WebScrapper.Scraping
{
    /// <summary>
    /// Visits the pages of the collection plans with a headless browser (Playwright, the installed Edge by default). For
    /// each page: a full-page screenshot in <c>&lt;root&gt;/&lt;yyyy-MM&gt;/&lt;page id&gt;.png</c>, its pictures downloaded
    /// in <c>&lt;root&gt;/&lt;yyyy-MM&gt;/&lt;full date&gt;/</c>, its text cut into paragraphs and the entities of each
    /// paragraph found by the NER of the server. One browser is shared by the runs (started at the first page).
    /// </summary>
    public sealed class WebCrawler : IAsyncDisposable
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static WebCrawler? _shared;

        private readonly WebScrapperConfig _config;
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;
        private readonly HttpClient _http;
        private readonly RobotsTxt _robots;
        private readonly NerClient _ner;

        private WebCrawler(WebScrapperConfig config, IPlaywright playwright, IBrowser browser)
        {
            _config = config;
            _playwright = playwright;
            _browser = browser;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(config.userAgent);
            _robots = new RobotsTxt(_http);
            _ner = new NerClient(_http, config.ServerUrl);
        }

        /// <summary>The crawler of the process, its browser started at the first call.</summary>
        public static async Task<WebCrawler> GetAsync(WebScrapperConfig config)
        {
            await Gate.WaitAsync();
            try
            {
                if (_shared is null)
                {
                    var playwright = await Playwright.CreateAsync();
                    IBrowser browser;
                    try
                    {
                        browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                        {
                            Headless = true,
                            Channel = string.IsNullOrWhiteSpace(config.browserChannel) ? null : config.browserChannel
                        });
                    }
                    catch (PlaywrightException) when (!string.IsNullOrWhiteSpace(config.browserChannel))
                    {
                        // The installed browser is missing: the browser of Playwright (installed with "playwright install chromium").
                        browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
                    }
                    _shared = new WebCrawler(config, playwright, browser);
                }
                return _shared;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// Visits the URL of <paramref name="job"/> and, in explore or random mode, links of the same site; the pages
        /// visited less than <see cref="WebScrapperConfig.revisitHours"/> ago are left out (the start URL of a job given
        /// on the command line excepted).
        /// </summary>
        public async Task<List<PageResult>> CrawlAsync(ScrapeJob job, SessionManager session, Action<string> log, CancellationToken cancellationToken)
        {
            var results = new List<PageResult>();
            if (!Uri.TryCreate(job.url, UriKind.Absolute, out var start) || (start.Scheme != Uri.UriSchemeHttp && start.Scheme != Uri.UriSchemeHttps))
            {
                log($"Not a web URL: {job.url}");
                return results;
            }

            var mode = (job.mode ?? "fixed").Trim().ToLowerInvariant();
            var limit = mode switch
            {
                "explore" => Math.Max(1, _config.maxPagesPerUrl),
                "random" => 1 + Math.Max(0, _config.randomPages),
                _ => 1
            };
            var queue = new List<(Uri Url, int Depth)> { (start, 0) };
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (queue.Count > 0 && results.Count < limit && !cancellationToken.IsCancellationRequested)
            {
                var (url, depth) = queue[0];
                queue.RemoveAt(0);
                var key = Normalize(url);
                if (!visited.Add(key))
                    continue;

                if (depth > 0 && RecentlyVisited(session, url.AbsoluteUri))
                {
                    log($"Visited less than {_config.revisitHours} h ago: {url}");
                    continue;
                }
                if (_config.respectRobotsTxt && !await _robots.IsAllowedAsync(url, cancellationToken))
                {
                    log($"Disallowed by robots.txt: {url}");
                    continue;
                }

                if (results.Count > 0)
                    await Task.Delay(Math.Max(0, _config.delayBetweenPagesMs), cancellationToken);
                var result = await ScrapePageAsync(job, url, depth, log, cancellationToken);
                if (result is null)
                    continue;
                results.Add(result);

                // The links to follow: same site, not visited, those holding a keyword first.
                if (mode == "fixed" || (mode == "explore" && depth >= _config.maxDepth) || (mode == "random" && depth >= 1))
                    continue;
                var next = result.page.links
                    .Select(l => Uri.TryCreate(l, UriKind.Absolute, out var u) ? u : null)
                    .OfType<Uri>()
                    .Where(u => (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && SameSite(u, start) && !visited.Contains(Normalize(u)) && !LooksLikeFile(u))
                    .DistinctBy(Normalize)
                    .ToList();
                if (mode == "random")
                    next = next.OrderBy(_ => Random.Shared.Next()).Take(_config.randomPages).ToList();
                else
                    next = next.OrderByDescending(u => KeywordsIn(u.AbsoluteUri, job.keywords).Count).ToList();
                queue.AddRange(next.Select(u => (u, depth + 1)));
            }
            return results;
        }

        /// <summary>The page was saved less than <see cref="WebScrapperConfig.revisitHours"/> ago.</summary>
        public bool RecentlyVisited(SessionManager session, string url)
        {
            try
            {
                var last = session.WebPageLastVisit(url);
                return last is not null && DateTime.Now - last.scrapedAt < TimeSpan.FromHours(_config.revisitHours);
            }
            catch
            {
                return false;
            }
        }

        private async Task<PageResult?> ScrapePageAsync(ScrapeJob job, Uri url, int depth, Action<string> log, CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            var now = DateTime.Now;
            var page = new WebPage
            {
                id = Guid.NewGuid().ToString(),
                url = url.AbsoluteUri,
                startUrl = job.url,
                depth = depth,
                mode = job.mode,
                keywords = job.keywords ?? new List<string>(),
                collectionPlanId = job.collectionPlanId,
                collectionPlanName = job.collectionPlanName,
                websiteName = job.websiteName,
                scrapedAt = now,
                date = now,
                appName = _config.appName,
                machineName = Environment.MachineName,
                userName = Environment.UserName
            };

            var monthFolder = Path.Combine(_config.ResolvedOutputFolder, now.ToString("yyyy-MM"));
            Directory.CreateDirectory(monthFolder);

            await using var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = _config.userAgent,
                ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
                Locale = "fr-FR",
                IgnoreHTTPSErrors = true
            });
            var browserPage = await context.NewPageAsync();
            var timeout = Math.Max(5, _config.pageTimeoutSeconds) * 1000f;

            try
            {
                var response = await browserPage.GotoAsync(url.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = timeout });
                page.httpStatus = response?.Status ?? 0;
                if (response is not null && !response.Ok)
                {
                    log($"HTTP {response.Status}: {url}");
                    if (response.Status >= 400)
                        return null;
                }
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                log($"Page not loaded ({ex.Message.Split('\n')[0]}): {url}");
                return null;
            }

            try
            {
                await browserPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 10000 });
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // Pages that keep loading (ads, analytics): what is there is enough.
            }

            // Scrolls down the page so that the pictures loaded lazily are there, then back to the top.
            try
            {
                await browserPage.EvaluateAsync(@"async () => {
                    const step = window.innerHeight;
                    for (let y = 0, i = 0; y < document.body.scrollHeight && i < 40; y += step, i++) {
                        window.scrollTo(0, y);
                        await new Promise(r => setTimeout(r, 150));
                    }
                    window.scrollTo(0, 0);
                }");
                await browserPage.WaitForTimeoutAsync(500);
            }
            catch (PlaywrightException)
            {
            }

            page.finalUrl = browserPage.Url;
            page.pageTitle = await SafeAsync(() => browserPage.TitleAsync(), string.Empty);
            page.title = page.pageTitle;

            // The screenshot, named after the id of the page.
            var screenshot = Path.Combine(monthFolder, page.id + ".png");
            try
            {
                await browserPage.ScreenshotAsync(new PageScreenshotOptions { Path = screenshot, FullPage = true, Timeout = timeout });
                page.screenshotPath = screenshot;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                page.warnings.Add("Screenshot failed: " + ex.Message.Split('\n')[0]);
            }

            var html = await SafeAsync(() => browserPage.ContentAsync(), string.Empty);
            var facts = await ReadFactsAsync(browserPage);
            page.pageLanguage = facts.Language;
            page.links = facts.Links.Distinct().Take(500).ToList();

            // The paragraphs and their entities.
            var paragraphs = ParagraphSplitter.Split(html, _config.minParagraphLength);
            page.text = string.Join("\n\n", paragraphs.Select(p => p.Text));
            page.contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(page.text))).ToLowerInvariant();
            page.keywordsFound = KeywordsIn(page.pageTitle + "\n" + page.text, page.keywords);

            var result = new PageResult { page = page };
            var nerFailed = false;
            for (var i = 0; i < paragraphs.Count; i++)
            {
                var paragraph = new WebParagraph
                {
                    id = $"{page.id}-{i:D4}",
                    pageId = page.id,
                    url = page.finalUrl,
                    index = i,
                    element = paragraphs[i].Element,
                    text = paragraphs[i].Text,
                    title = page.pageTitle,
                    keywordsFound = KeywordsIn(paragraphs[i].Text, page.keywords),
                    collectionPlanId = page.collectionPlanId,
                    scrapedAt = now,
                    date = now,
                    appName = _config.appName,
                    machineName = Environment.MachineName,
                    userName = Environment.UserName
                };
                if (!nerFailed)
                {
                    try
                    {
                        var (entities, warnings) = await _ner.ExtractAsync(paragraph.text, cancellationToken);
                        paragraph.entities = entities;
                        foreach (var warning in warnings.Where(w => !page.warnings.Contains("NER: " + w)))
                            page.warnings.Add("NER: " + warning);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                    {
                        // The server does not answer: the paragraphs are saved without entities.
                        nerFailed = true;
                        page.warnings.Add($"NER unavailable at {_config.ServerUrl}: {ex.Message}");
                    }
                }
                result.paragraphs.Add(paragraph);
            }
            page.paragraphCount = result.paragraphs.Count;
            page.entityCount = result.paragraphs.Sum(p => p.entities.Count);

            // The pictures, in a folder of their own named after the full date of the visit.
            page.imagesFolder = Path.Combine(monthFolder, now.ToString("yyyy-MM-dd_HH-mm-ss-fff"));
            page.images = await DownloadImagesAsync(context, facts.Images, page.finalUrl, page.imagesFolder, cancellationToken);
            if (!page.images.Any(i => i.path.Length > 0) && Directory.Exists(page.imagesFolder) && !Directory.EnumerateFileSystemEntries(page.imagesFolder).Any())
                Directory.Delete(page.imagesFolder);

            page.durationMs = (int)watch.ElapsedMilliseconds;
            log($"{page.finalUrl}: {page.paragraphCount} paragraph(s), {page.entityCount} entit(ies), {page.images.Count(i => i.path.Length > 0)} picture(s)");
            return result;
        }

        private sealed record PageFacts(string Language, List<string> Links, List<(string Url, string Alt)> Images);

        /// <summary>The language, the links and the pictures of the page, as the browser sees them (lazy pictures and srcset resolved).</summary>
        private static async Task<PageFacts> ReadFactsAsync(IPage page)
        {
            try
            {
                var json = await page.EvaluateAsync<string>(@"() => JSON.stringify({
                    lang: document.documentElement.lang || '',
                    links: Array.from(document.querySelectorAll('a[href]')).map(a => a.href),
                    images: [
                        ...Array.from(document.images).map(i => ({ src: i.currentSrc || i.src || i.dataset.src || '', alt: i.alt || '' })),
                        ...Array.from(document.querySelectorAll('meta[property=""og:image""], meta[name=""twitter:image""]')).map(m => ({ src: new URL(m.content, location.href).href, alt: 'og:image' }))
                    ]
                })");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var links = root.GetProperty("links").EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList();
                var images = root.GetProperty("images").EnumerateArray()
                    .Select(e => (Url: e.GetProperty("src").GetString() ?? string.Empty, Alt: e.GetProperty("alt").GetString() ?? string.Empty))
                    .Where(i => i.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return new PageFacts(root.GetProperty("lang").GetString() ?? string.Empty, links, images);
            }
            catch (Exception ex) when (ex is PlaywrightException or JsonException or KeyNotFoundException)
            {
                return new PageFacts(string.Empty, new(), new());
            }
        }

        /// <summary>Downloads the pictures (with the cookies of the page), keeping only real pictures big enough.</summary>
        private async Task<List<WebPageImage>> DownloadImagesAsync(IBrowserContext context, List<(string Url, string Alt)> found, string referer,
            string folder, CancellationToken cancellationToken)
        {
            var images = new List<WebPageImage>();
            var kept = 0;
            foreach (var (url, alt) in found.DistinctBy(i => i.Url))
            {
                if (kept >= Math.Max(0, _config.maxImagesPerPage) || cancellationToken.IsCancellationRequested)
                    break;
                var image = new WebPageImage { url = url, alt = alt };
                try
                {
                    var response = await context.APIRequest.GetAsync(url, new APIRequestContextOptions
                    {
                        Headers = new Dictionary<string, string> { ["Referer"] = referer },
                        Timeout = 20000
                    });
                    if (!response.Ok)
                    {
                        image.error = $"HTTP {response.Status}";
                        images.Add(image);
                        continue;
                    }
                    response.Headers.TryGetValue("content-type", out var contentType);
                    image.contentType = (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
                    if (!image.contentType.StartsWith("image/"))
                        continue;
                    var bytes = await response.BodyAsync();
                    image.bytes = bytes.LongLength;
                    if (bytes.Length < _config.minImageBytes)
                        continue;

                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, $"{kept + 1:D3}-{FileName(url)}{Extension(image.contentType, url)}");
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                    image.path = path;
                    kept++;
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException or IOException)
                {
                    image.error = ex.Message.Split('\n')[0];
                }
                images.Add(image);
            }
            return images;
        }

        private static string FileName(string url)
        {
            var name = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri.AbsolutePath)) : "image";
            var invalid = Path.GetInvalidFileNameChars();
            name = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray()).Trim('_', '.');
            return name.Length == 0 ? "image" : name.Length > 60 ? name[..60] : name;
        }

        private static string Extension(string contentType, string url) => contentType switch
        {
            "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/avif" => ".avif",
            "image/bmp" => ".bmp",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            _ => Uri.TryCreate(url, UriKind.Absolute, out var uri) && Path.GetExtension(uri.AbsolutePath) is { Length: > 1 and <= 5 } ext ? ext : ".img"
        };

        /// <summary>The keywords found in <paramref name="text"/> (case ignored).</summary>
        public static List<string> KeywordsIn(string text, IEnumerable<string>? keywords) =>
            (keywords ?? Enumerable.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k) && text.Contains(k.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static string Normalize(Uri url) => url.GetLeftPart(UriPartial.Query).TrimEnd('/');

        private static bool SameSite(Uri url, Uri start) =>
            string.Equals(url.Host.Replace("www.", ""), start.Host.Replace("www.", ""), StringComparison.OrdinalIgnoreCase);

        private static readonly string[] FileExtensions = { ".pdf", ".zip", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".mp4", ".mp3", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".exe", ".dmg", ".svg", ".xml", ".rss" };

        private static bool LooksLikeFile(Uri url) =>
            FileExtensions.Contains(Path.GetExtension(url.AbsolutePath), StringComparer.OrdinalIgnoreCase);

        private static async Task<T> SafeAsync<T>(Func<Task<T>> action, T fallback)
        {
            try
            {
                return await action();
            }
            catch (PlaywrightException)
            {
                return fallback;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _browser.DisposeAsync();
            _playwright.Dispose();
            _http.Dispose();
        }
    }
}
