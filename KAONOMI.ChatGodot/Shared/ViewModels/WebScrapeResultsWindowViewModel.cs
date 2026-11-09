using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// What the web scraper agent (KAONOMI.Agent.WebScrapper) saved for a collection plan: its pages (index <c>webpage</c>
/// of the search engine), the newest first, each with its screenshot, its pictures and its text. A picture opens in the
/// image analysis at its real size (<see cref="OpenImage"/>). Opened by 🗂 RESULTS of the collection plan window.
/// </summary>
public partial class WebScrapeResultsWindowViewModel : ViewModelBase
{
    private const int PageSize = 200;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly string _searchEngineUrl;
    private readonly Func<string, ImageAnalysisViewModel>? _createAnalysis;

    /// <summary>Design-time constructor.</summary>
    public WebScrapeResultsWindowViewModel() : this(ChatSettings.Load().SearchEngineUrl, string.Empty, "Collection plan", null, load: false)
    {
    }

    /// <summary>
    /// The pages of the collection plan <paramref name="planId"/> (named <paramref name="planName"/>), read in the search
    /// engine at <paramref name="searchEngineUrl"/>; <paramref name="createAnalysis"/> makes the analysis of a picture.
    /// </summary>
    public WebScrapeResultsWindowViewModel(string searchEngineUrl, string planId, string planName,
        Func<string, ImageAnalysisViewModel>? createAnalysis, bool load = true)
    {
        _searchEngineUrl = searchEngineUrl.TrimEnd('/');
        PlanId = planId;
        PlanName = planName;
        _createAnalysis = createAnalysis;
        if (load)
            _ = RefreshAsync();
    }

    public string PlanId { get; }

    public string PlanName { get; }

    /// <summary>The title of the window.</summary>
    public string Title => $"Scraper results · {PlanName}";

    /// <summary>The pages saved for the plan, the newest first.</summary>
    public ObservableCollection<WebPageResultViewModel> Pages { get; } = [];

    [ObservableProperty]
    private WebPageResultViewModel? _selectedPage;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isRunning;

    private bool CanRefresh() => !IsRunning;

    /// <summary>REFRESH: the pages of the plan read again (the agent may have saved new ones meanwhile).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsRunning = true;
        Status = "Reading the pages saved by the web scraper…";
        var selected = SelectedPage?.Id;
        try
        {
            var pages = await LoadAsync(CancellationToken.None);
            Pages.Clear();
            foreach (var page in pages)
                Pages.Add(page);
            SelectedPage = Pages.FirstOrDefault(p => p.Id == selected) ?? Pages.FirstOrDefault();
            var pictures = pages.Sum(p => p.Images.Count);
            Status = pages.Count == 0
                ? "Nothing saved yet for this plan: 🤖 LAUNCH AGENT in the collection plan window visits its websites"
                : $"{pages.Count} page(s) · {pictures} picture(s) · newest {pages[0].DateLabel}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Status = $"The search engine could not be read ({_searchEngineUrl}): {ex.Message}";
            ActivityLog.Error("ScraperResults", "Pages of the plan not read", ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task<List<WebPageResultViewModel>> LoadAsync(CancellationToken cancellationToken)
    {
        // The pages of the plan (its id as a keyword), the newest first; those of a plan never saved: none.
        var query = new Dictionary<string, object>
        {
            ["size"] = PageSize,
            ["sort"] = new object[] { new Dictionary<string, object> { ["scrapedAt"] = new Dictionary<string, string> { ["order"] = "desc" } } },
            ["query"] = new Dictionary<string, object>
            {
                ["term"] = new Dictionary<string, string> { ["collectionPlanId.keyword"] = PlanId }
            }
        };
        using var content = new StringContent(JsonSerializer.Serialize(query), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync($"{_searchEngineUrl}/webpage/_search", content, cancellationToken);
        // No page saved yet: the index does not exist.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var pages = new List<WebPageResultViewModel>();
        if (document.RootElement.TryGetProperty("hits", out var hits) && hits.TryGetProperty("hits", out var list))
            foreach (var hit in list.EnumerateArray())
                if (hit.TryGetProperty("_source", out var source) && !Bool(source, "isDeleted"))
                    pages.Add(WebPageResultViewModel.From(hit.TryGetProperty("_id", out var id) ? id.GetString() ?? "" : "", source));
        // Sorted here too (a search engine that ignores the sort still shows the newest first).
        return pages.OrderByDescending(p => p.ScrapedAt).ToList();
    }

    /// <summary>
    /// The analysis of the picture at <paramref name="path"/>, shown at its real size (scrolled in its window), or null
    /// when the file is not on this machine.
    /// </summary>
    public ImageAnalysisViewModel? OpenImage(string path)
    {
        if (_createAnalysis is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Status = $"The picture is not on this machine: {path}";
            return null;
        }
        var analysis = _createAnalysis(path);
        analysis.OpenAtActualSize = true;
        return analysis;
    }

    internal static string Text(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    internal static int Int(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    internal static bool Bool(JsonElement source, string name) =>
        source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>A picture of a scraped page: where it was downloaded, its address and its alternative text.</summary>
public sealed record WebPageImageItem(string Path, string Url, string Alt, long Bytes, string Error)
{
    /// <summary>True when the file is on this machine.</summary>
    public bool Exists => Path.Length > 0 && File.Exists(Path);

    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            if (Alt.Length > 0)
                lines.Add(Alt);
            lines.Add(Url.Length > 0 ? Url : Path);
            if (Bytes > 0)
                lines.Add($"{Bytes / 1024.0:0.#} KB");
            if (Error.Length > 0)
                lines.Add("⚠ " + Error);
            lines.Add("Click: open it at its real size in the image analysis");
            return string.Join("\n", lines);
        }
    }
}

/// <summary>A page saved by the web scraper (a document of the index <c>webpage</c>).</summary>
public sealed class WebPageResultViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string WebsiteName { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public int Depth { get; init; }
    public int HttpStatus { get; init; }
    public string Language { get; init; } = string.Empty;
    public DateTime ScrapedAt { get; init; }
    public int DurationMs { get; init; }
    public int ParagraphCount { get; init; }
    public int EntityCount { get; init; }
    public string Text { get; init; } = string.Empty;
    public string ScreenshotPath { get; init; } = string.Empty;
    public string ImagesFolder { get; init; } = string.Empty;
    public IReadOnlyList<WebPageImageItem> Images { get; init; } = [];
    public IReadOnlyList<string> KeywordsFound { get; init; } = [];
    public string Error { get; init; } = string.Empty;

    public string DateLabel => ScrapedAt == default ? "—" : ScrapedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>The line under the title in the list: date, website, depth, counts.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { DateLabel };
            if (WebsiteName.Length > 0)
                parts.Add(WebsiteName);
            if (Depth > 0)
                parts.Add($"depth {Depth}");
            parts.Add($"🖼 {Images.Count}");
            parts.Add($"¶ {ParagraphCount}");
            if (HttpStatus is > 0 and not 200)
                parts.Add($"HTTP {HttpStatus}");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The details under the title of the page shown.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string> { $"Visited {DateLabel}" };
            if (DurationMs > 0)
                parts.Add($"in {DurationMs / 1000.0:0.#} s");
            if (HttpStatus > 0)
                parts.Add($"HTTP {HttpStatus}");
            if (Mode.Length > 0)
                parts.Add(Mode + (Depth > 0 ? $" (depth {Depth})" : string.Empty));
            if (Language.Length > 0)
                parts.Add(Language);
            parts.Add($"{ParagraphCount} paragraph(s), {EntityCount} entit{(EntityCount == 1 ? "y" : "ies")}");
            parts.Add($"{Images.Count} picture(s)");
            if (KeywordsFound.Count > 0)
                parts.Add("keywords: " + string.Join(", ", KeywordsFound));
            return string.Join(" · ", parts);
        }
    }

    public bool HasError => Error.Length > 0;

    public bool HasScreenshot => ScreenshotPath.Length > 0 && File.Exists(ScreenshotPath);

    internal static WebPageResultViewModel From(string id, JsonElement source)
    {
        var images = new List<WebPageImageItem>();
        if (source.TryGetProperty("images", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var image in list.EnumerateArray())
            {
                var path = WebScrapeResultsWindowViewModel.Text(image, "path");
                var url = WebScrapeResultsWindowViewModel.Text(image, "url");
                if (path.Length == 0 && url.Length == 0)
                    continue;
                var bytes = image.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var n) ? n : 0;
                images.Add(new WebPageImageItem(path, url, WebScrapeResultsWindowViewModel.Text(image, "alt"), bytes, WebScrapeResultsWindowViewModel.Text(image, "error")));
            }
        var keywords = new List<string>();
        if (source.TryGetProperty("keywordsFound", out var found) && found.ValueKind == JsonValueKind.Array)
            keywords.AddRange(found.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString() ?? "").Where(k => k.Length > 0));
        var finalUrl = WebScrapeResultsWindowViewModel.Text(source, "finalUrl");
        var url0 = finalUrl.Length > 0 ? finalUrl : WebScrapeResultsWindowViewModel.Text(source, "url");
        var title = WebScrapeResultsWindowViewModel.Text(source, "pageTitle");
        var errors = new List<string>();
        foreach (var name in new[] { "warnings", "error" })
            if (source.TryGetProperty(name, out var e))
            {
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } text)
                    errors.Add(text);
                else if (e.ValueKind == JsonValueKind.Array)
                    errors.AddRange(e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
            }
        return new WebPageResultViewModel
        {
            Id = id,
            Title = title.Length > 0 ? title : url0,
            Url = url0,
            WebsiteName = WebScrapeResultsWindowViewModel.Text(source, "websiteName"),
            Mode = WebScrapeResultsWindowViewModel.Text(source, "mode"),
            Depth = WebScrapeResultsWindowViewModel.Int(source, "depth"),
            HttpStatus = WebScrapeResultsWindowViewModel.Int(source, "httpStatus"),
            Language = WebScrapeResultsWindowViewModel.Text(source, "pageLanguage"),
            ScrapedAt = source.TryGetProperty("scrapedAt", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTime(out var date) ? date : default,
            DurationMs = WebScrapeResultsWindowViewModel.Int(source, "durationMs"),
            ParagraphCount = WebScrapeResultsWindowViewModel.Int(source, "paragraphCount"),
            EntityCount = WebScrapeResultsWindowViewModel.Int(source, "entityCount"),
            Text = WebScrapeResultsWindowViewModel.Text(source, "text"),
            ScreenshotPath = WebScrapeResultsWindowViewModel.Text(source, "screenshotPath"),
            ImagesFolder = WebScrapeResultsWindowViewModel.Text(source, "imagesFolder"),
            Images = images,
            KeywordsFound = keywords,
            Error = string.Join(" · ", errors)
        };
    }
}
