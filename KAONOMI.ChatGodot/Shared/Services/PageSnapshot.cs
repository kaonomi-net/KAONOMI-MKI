using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Pictures of a web page as a browser renders it, screen by screen (1280 × 1600 each, down to the end of the page, at
/// most <see cref="Screens"/>), for the web scraper: what the vision model reads, and what the document shows. On
/// macOS a small helper written in Swift (<see cref="HelperSource"/>, compiled once with swiftc into
/// ~/Library/Caches/Kaonomi) loads the page in WebKit in a window off the screens, with no icon in the Dock, and saves
/// JPEGs; elsewhere Edge, Chrome or Chromium take the first screen headless (a PNG). Two pages at a time at most.
/// </summary>
public static class PageSnapshot
{
    public const int Width = 1280, Height = 1600;

    /// <summary>The most screens of one page.</summary>
    public const int Screens = 3;

    /// <summary>The longest a page may take, loading and drawing.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(40);

    private static readonly SemaphoreSlim Slots = new(2);
    private static readonly SemaphoreSlim Compiling = new(1);
    private static string? _helper;
    private static string? _unavailable;

    /// <summary>
    /// Takes the pictures of <paramref name="url"/> into <paramref name="folder"/>, named <paramref name="name"/>-1,
    /// -2… (the extension is added): their paths from the top of the page, or why none could be taken.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Paths, string? Error)> CaptureAsync(string url, string folder, string name, CancellationToken cancellationToken)
    {
        await Slots.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(folder);
            if (OperatingSystem.IsMacOS())
            {
                var helper = await HelperAsync(cancellationToken);
                if (helper is null)
                    return ([], _unavailable);
                var stem = Path.Combine(folder, name);
                var (code, output) = await RunAsync(helper, [url, stem, Width.ToString(), Height.ToString(), "25", Screens.ToString()], cancellationToken);
                var paths = Enumerable.Range(1, Screens).Select(k => $"{stem}-{k}.jpg").Where(File.Exists).ToList();
                return code == 0 && paths.Count > 0 ? (paths, null) : ([], Why(output));
            }
            var browser = Browser();
            if (browser is null)
                return ([], "no Edge, Chrome or Chromium to draw the page");
            var png = Path.Combine(folder, name + "-1.png");
            var (exit, text) = await RunAsync(browser,
                ["--headless=new", "--disable-gpu", "--hide-scrollbars", "--mute-audio", "--no-first-run", $"--window-size={Width},{Height}",
                 "--virtual-time-budget=8000", $"--user-agent={WebScraper.UserAgent}", $"--screenshot={png}", url], cancellationToken);
            return File.Exists(png) ? ([png], null) : ([], Why(text) ?? $"the browser ended with {exit}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ([], ex.Message);
        }
        finally
        {
            Slots.Release();
        }
    }

    private static string? Why(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("error ", StringComparison.Ordinal));
        return line?[6..] ?? (output.Trim().Length > 0 ? output.Trim().Split('\n')[^1] : null);
    }

    private static async Task<(int Code, string Output)> RunAsync(string program, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(program)} could not be started");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            cancellationToken.ThrowIfCancellationRequested();
            return (-1, $"error no picture within {Timeout.TotalSeconds:0} s");
        }
        return (process.ExitCode, (await output) + "\n" + (await errors));
    }

    /// <summary>The compiled helper of macOS (compiled once, named after the hash of its source), or null when swiftc fails.</summary>
    private static async Task<string?> HelperAsync(CancellationToken cancellationToken)
    {
        if (_helper is not null || _unavailable is not null)
            return _helper;
        await Compiling.WaitAsync(cancellationToken);
        try
        {
            if (_helper is not null || _unavailable is not null)
                return _helper;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(HelperSource)))[..16];
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "Kaonomi");
            var binary = Path.Combine(cache, $"page-snapshot-{hash}");
            if (!File.Exists(binary))
            {
                Directory.CreateDirectory(cache);
                var source = Path.Combine(cache, "page-snapshot.swift");
                await File.WriteAllTextAsync(source, HelperSource, cancellationToken);
                if (!File.Exists("/usr/bin/swiftc"))
                {
                    _unavailable = "no Swift compiler (install the Xcode command line tools: xcode-select --install)";
                    return null;
                }
                var (code, output) = await RunAsync("/usr/bin/swiftc", ["-O", source, "-o", binary], cancellationToken);
                if (code != 0 || !File.Exists(binary))
                {
                    _unavailable = "the picture helper could not be compiled: " + output.Trim();
                    ActivityLog.Error("WebScraper", _unavailable);
                    return null;
                }
            }
            _helper = binary;
            return binary;
        }
        finally
        {
            Compiling.Release();
        }
    }

    /// <summary>Edge, Chrome or Chromium on this computer (Windows, Linux), or null.</summary>
    private static string? Browser()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("LOCALAPPDATA") })
            {
                if (string.IsNullOrEmpty(root))
                    continue;
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
            }
        }
        else
        {
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
                foreach (var program in new[] { "microsoft-edge", "google-chrome", "chromium", "chromium-browser" })
                    candidates.Add(Path.Combine(folder, program));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The helper of macOS: page-snapshot &lt;url&gt; &lt;output base&gt; [width] [screen height] [timeout] [screens].</summary>
    public const string HelperSource = """
        // Pictures of a web page as WebKit renders it, for the web scraper of Kaonomi Chat (collection plans).
        // page-snapshot <url> <output base> [width] [screen height] [timeout seconds] [screens]
        // The page is drawn down to its end (at most [screens] screens) and cut into screens: <output base>-1.jpg, -2.jpg…
        // Prints "ok <count>" or "error <why>".
        import AppKit
        import WebKit

        setvbuf(stdout, nil, _IOLBF, 0)
        let arguments = CommandLine.arguments
        guard arguments.count >= 3, let url = URL(string: arguments[1]) else {
            print("error usage: page-snapshot <url> <output base> [width] [screen height] [timeout] [screens]")
            exit(64)
        }
        let output = arguments[2]
        let width = arguments.count > 3 ? Double(arguments[3]) ?? 1280 : 1280
        let screen = arguments.count > 4 ? Double(arguments[4]) ?? 1600 : 1600
        let timeout = arguments.count > 5 ? Double(arguments[5]) ?? 25 : 25
        let screens = arguments.count > 6 ? max(1, Int(arguments[6]) ?? 1) : 1

        // No icon in the Dock, never in front.
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)

        final class Shooter: NSObject, WKNavigationDelegate {
            let web: WKWebView
            let window: NSWindow
            let url: URL
            let output: String
            let width: Double
            let screen: Double
            let timeout: Double
            let screens: Int
            var done = false

            init(url: URL, output: String, width: Double, screen: Double, timeout: Double, screens: Int) {
                self.url = url
                self.output = output
                self.width = width
                self.screen = screen
                self.timeout = timeout
                self.screens = screens
                let frame = NSRect(x: 0, y: 0, width: width, height: screen)
                let configuration = WKWebViewConfiguration()
                configuration.websiteDataStore = .nonPersistent()
                configuration.mediaTypesRequiringUserActionForPlayback = .all
                web = WKWebView(frame: frame, configuration: configuration)
                web.customUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15 KAONOMI-WebScraper/1.0"
                // A window far off the screens: WebKit draws only in a window.
                window = NSWindow(contentRect: frame.offsetBy(dx: -30000, dy: -30000), styleMask: [.borderless], backing: .buffered, defer: false)
                window.isReleasedWhenClosed = false
                window.ignoresMouseEvents = true
                window.contentView = web
                super.init()
                web.navigationDelegate = self
                window.orderFrontRegardless()
            }

            func start() {
                web.load(URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout))
                // What is shown when the page takes too long.
                DispatchQueue.main.asyncAfter(deadline: .now() + timeout) { self.measure() }
            }

            func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
                // The scripts, the fonts and the pictures of the page a moment more.
                DispatchQueue.main.asyncAfter(deadline: .now() + 2.0) { self.measure() }
            }

            func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
                fail(error.localizedDescription)
            }

            func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
                fail(error.localizedDescription)
            }

            func fail(_ why: String) {
                if done { return }
                done = true
                print("error \(why)")
                exit(1)
            }

            /// The height of the page: the view is made that tall (at most the screens asked), then drawn.
            func measure() {
                if done { return }
                done = true
                let script = "Math.max(document.body ? document.body.scrollHeight : 0, document.documentElement ? document.documentElement.scrollHeight : 0)"
                web.evaluateJavaScript(script) { value, _ in
                    let page = (value as? Double) ?? (value as? NSNumber)?.doubleValue ?? self.screen
                    let height = min(max(page, self.screen), self.screen * Double(self.screens))
                    let count = max(1, Int((height / self.screen).rounded(.up)))
                    let total = Double(count) * self.screen
                    if total > self.screen {
                        let frame = NSRect(x: 0, y: 0, width: self.width, height: total)
                        self.window.setContentSize(frame.size)
                        self.web.frame = frame
                    }
                    // A moment for the parts of the page that only draw once in view.
                    DispatchQueue.main.asyncAfter(deadline: .now() + (total > self.screen ? 1.2 : 0.1)) { self.shoot(count: count) }
                }
            }

            func shoot(count: Int) {
                let snapshot = WKSnapshotConfiguration()
                snapshot.rect = CGRect(x: 0, y: 0, width: width, height: screen * Double(count))
                snapshot.afterScreenUpdates = true
                web.takeSnapshot(with: snapshot) { image, error in
                    guard let image = image, let whole = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
                        print("error \(error?.localizedDescription ?? "no picture")")
                        exit(1)
                    }
                    // Each screen at its size in points (not twice on a Retina screen), in JPEG.
                    let scale = Double(whole.height) / (self.screen * Double(count))
                    for index in 0..<count {
                        let part = CGRect(x: 0, y: Double(index) * self.screen * scale, width: Double(whole.width), height: self.screen * scale)
                        guard let piece = whole.cropping(to: part) else { continue }
                        let pixels = NSSize(width: self.width, height: self.screen)
                        guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(pixels.width), pixelsHigh: Int(pixels.height),
                                                            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                                            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { continue }
                        NSGraphicsContext.saveGraphicsState()
                        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
                        NSColor.white.setFill()
                        NSRect(origin: .zero, size: pixels).fill()
                        NSImage(cgImage: piece, size: pixels).draw(in: NSRect(origin: .zero, size: pixels))
                        NSGraphicsContext.restoreGraphicsState()
                        guard let data = bitmap.representation(using: .jpeg, properties: [.compressionFactor: 0.82]) else { continue }
                        try? data.write(to: URL(fileURLWithPath: "\(self.output)-\(index + 1).jpg"))
                    }
                    print("ok \(count)")
                    exit(0)
                }
            }
        }

        let shooter = Shooter(url: url, output: output, width: width, screen: screen, timeout: timeout, screens: screens)
        shooter.start()
        app.run()
        """;
}
