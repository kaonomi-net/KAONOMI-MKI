using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using KAONOMI.Chat.Services;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The web view of the browser of the chat on Windows: a WebView2 (the engine of Edge, installed with Windows) laid as
/// a child of the window of the chat. Its place is in pixels of that window; it is created in the background (the page
/// shows once it is ready). Its data (cookies, cache) are kept in <c>~/kaonomi/webview2</c>.
/// </summary>
internal sealed class EdgeView
{
    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    private readonly IntPtr _host;
    private CoreWebView2Controller? _controller;
    private string? _pending;
    private System.Drawing.Rectangle _bounds;
    private bool _visible;
    private bool _closed;

    private EdgeView(IntPtr host) => _host = host;

    /// <summary>The folder of the data of the web view.</summary>
    private static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "kaonomi", "webview2");

    /// <summary>
    /// A web view in the window <paramref name="host"/> showing <paramref name="url"/>; <paramref name="failed"/> is
    /// called (on the main thread) when WebView2 cannot start (no runtime, no loader).
    /// </summary>
    public static EdgeView Create(IntPtr host, string url, Action<Exception> failed)
    {
        var view = new EdgeView(host) { _pending = url };
        _ = view.StartAsync(failed);
        return view;
    }

    private async Task StartAsync(Action<Exception> failed)
    {
        try
        {
            LoadLoader();
            Directory.CreateDirectory(DataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, DataFolder);
            var controller = await environment.CreateCoreWebView2ControllerAsync(_host);
            if (_closed)
            {
                controller.Close();
                return;
            }
            _controller = controller;
            controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 8, 12, 18);
            controller.Bounds = _bounds;
            controller.IsVisible = _visible;
            // The links that would open a window of their own open in this page.
            controller.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                controller.CoreWebView2.Navigate(e.Uri);
            };
            if (_pending is { } url)
            {
                _pending = null;
                Load(url);
            }
            ActivityLog.Info("Web", $"Browser of the chat ready (WebView2 {environment.BrowserVersionString})");
        }
        catch (Exception ex) when (ex is DllNotFoundException or WebView2RuntimeNotFoundException or COMException or InvalidOperationException or ArgumentException or FileNotFoundException)
        {
            ActivityLog.Error("Web", "The browser of the chat cannot start (WebView2)", ex);
            failed(ex);
        }
    }

    /// <summary>WebView2Loader.dll of the package: beside the assembly, or under runtimes/win-x64/native.</summary>
    private static void LoadLoader()
    {
        var folder = Path.GetDirectoryName(typeof(CoreWebView2Environment).Assembly.Location) ?? AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(folder, "WebView2Loader.dll"),
                     Path.Combine(folder, "runtimes", RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64", "native", "WebView2Loader.dll")
                 })
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _))
                return;
        }
    }

    public bool IsReady => _controller is not null;

    public void Load(string url)
    {
        if (_controller is null)
        {
            _pending = url;
            return;
        }
        try
        {
            _controller.CoreWebView2.Navigate(url);
        }
        catch (ArgumentException)
        {
            OpenExternally(url);
        }
    }

    private static void OpenExternally(string url) => Godot.OS.ShellOpen(url);

    public void Back()
    {
        if (_controller?.CoreWebView2 is { CanGoBack: true } web)
            web.GoBack();
    }

    public void Forward()
    {
        if (_controller?.CoreWebView2 is { CanGoForward: true } web)
            web.GoForward();
    }

    public void Reload() => _controller?.CoreWebView2.Reload();

    public string? Title => _controller?.CoreWebView2.DocumentTitle;
    public string? Url => _controller?.CoreWebView2.Source;
    public bool CanGoBack => _controller?.CoreWebView2.CanGoBack == true;
    public bool CanGoForward => _controller?.CoreWebView2.CanGoForward == true;

    /// <summary>Saves what the page shows (its visible part) in the PNG file <paramref name="path"/>.</summary>
    public async Task CaptureAsync(string path)
    {
        if (_controller is null)
            throw new InvalidOperationException("The page is not ready yet");
        await using var file = File.Create(path);
        await _controller.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
    }

    /// <summary>Places the view at <paramref name="x"/>, <paramref name="y"/> (pixels of the window of the chat).</summary>
    public void Place(int x, int y, int width, int height)
    {
        var bounds = new System.Drawing.Rectangle(x, y, width, height);
        if (bounds == _bounds)
            return;
        _bounds = bounds;
        if (_controller is not null)
            _controller.Bounds = bounds;
    }

    /// <summary>Shows or hides the view; hidden, the keyboard goes back to the chat.</summary>
    public void Show(bool visible)
    {
        _visible = visible;
        if (_controller is null)
            return;
        _controller.IsVisible = visible;
        if (!visible)
            SetFocus(_host);
    }

    public void Close()
    {
        _closed = true;
        _controller?.Close();
        _controller = null;
    }
}
