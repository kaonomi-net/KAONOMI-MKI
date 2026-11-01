using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A completion of the results of <c>/completion</c>, with what the lists and the statistics read of it.</summary>
public sealed class CompletionItemViewModel
{
    public CompletionItemViewModel(CompletionEntry entry)
    {
        Entry = entry;
        PromptWords = Words(entry.Prompt);
        CompletionWords = Words(entry.Completion);
        Duration = Meta("DurationSeconds") is { ValueDouble: > 0 } duration ? duration.ValueDouble
            : Meta("DurationSeconds") is { ValueInt: > 0 } whole ? whole.ValueInt : null;
        HasCode = entry.Completion.Contains("```", StringComparison.Ordinal);
    }

    /// <summary>The completion as saved.</summary>
    public CompletionEntry Entry { get; }

    public int PromptWords { get; }
    public int CompletionWords { get; }

    /// <summary>How long the model took (the metadata DurationSeconds), null when it was not kept.</summary>
    public double? Duration { get; }

    /// <summary>Words of the completion per second, null without a duration.</summary>
    public double? WordsPerSecond => Duration is > 0 ? CompletionWords / Duration : null;

    /// <summary>True when the completion holds a block of code (```).</summary>
    public bool HasCode { get; }

    /// <summary>First line of the prompt, shown in the list.</summary>
    public string Title => FirstLine(Entry.Prompt) is { Length: > 0 } line ? line : "(empty prompt)";

    /// <summary>Date, model, language, topic and collection, shown under the title.</summary>
    public string Summary => string.Join(" · ", new[]
    {
        Entry.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Entry.Model, Entry.Language, Entry.Topic,
        Entry.CollectionName is "generic" ? null : Entry.CollectionName
    }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>The sizes and the time, shown at the right of the title.</summary>
    public string Figures => $"{PromptWords} → {CompletionWords} words" + (Duration is { } d ? $" · {d:0.#} s" : string.Empty) + (HasCode ? " · code" : string.Empty);

    /// <summary>A metadata of the completion by its key (case ignored), null when it has none.</summary>
    public CompletionMetaData? Meta(string key) => Entry.Metadatas.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    private static string FirstLine(string text) => text.Trim().Split('\n')[0].Trim();

    public static int Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>A term of the word cloud: its size and color follow its number of occurrences.</summary>
public sealed record WordCloudItem(string Term, int Count, double FontSize, IBrush Brush)
{
    /// <summary>Tooltip of the term.</summary>
    public string Tip => $"{Term}: {Count} occurrence(s) · click to search it";
}

/// <summary>A bar of a chart of the statistics: its label, its value, its tooltip; <see cref="From"/> / <see cref="To"/> for a bar of the timeline (a click narrows the period to it).</summary>
public sealed record ChartBar(string Label, double Value, string Tip, DateTime? From = null, DateTime? To = null);

/// <summary>A value of a facet (a model, a language, a topic, a collection): its count in the results, chosen or not as a filter.</summary>
public sealed record FacetValue(string Field, string Value, int Count, int Total, bool Selected)
{
    public double Share => Total == 0 ? 0 : 100.0 * Count / Total;
    public string Label => string.IsNullOrWhiteSpace(Value) ? "(none)" : Value;
}

/// <summary>
/// Completions of Kaonomi (<c>/completion</c>, <c>/ncompletion</c> in its own window): an advanced search on the server
/// (<c>POST /completion/query</c>: a text in the prompt and/or the completion, a period from / to with presets, the models,
/// languages, topics and collections ticked in the facets, the order), the completions that hold code only, then what the
/// results show: key figures (count, period, models, sizes, durations, speed), the activity over time (by hour, day or
/// week; a click on a bar narrows the period to it), the activity by weekday and hour, the histograms of the durations
/// and of the sizes, the languages, the word cloud of the most frequent terms, and the selected completion (prompt,
/// completion, raw completion, metadata). The results export to CSV or JSON.
/// </summary>
public partial class CompletionWindowViewModel : ViewModelBase, INoteContextProvider
{
    public const string ModelField = "model";
    public const string LanguageField = "language";
    public const string TopicField = "topic";
    public const string CollectionField = "collection";

    /// <inheritdoc />
    public string NoteWindowName => "Completions";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        searchTerm = Term,
        filters = ActiveFilters,
        resultCount = Results.Count,
        selected = SelectedResult?.Entry
    };

    /// <summary>Number of terms of the word cloud.</summary>
    private const int CloudSize = 80;

    /// <summary>Colors of the terms and of the slices, from the most frequent ones.</summary>
    private static readonly IBrush[] Palette = new[] { "#2563EB", "#7C3AED", "#DB2777", "#EA580C", "#059669", "#0891B2", "#CA8A04", "#4F46E5" }
        .Select(c => (IBrush)new SolidColorBrush(Color.Parse(c))).ToArray();

    private readonly KaonomiAssistantService _assistant;
    private readonly Dictionary<string, HashSet<string>> _selected = new()
    {
        [ModelField] = [], [LanguageField] = [], [TopicField] = [], [CollectionField] = []
    };
    /// <summary>Every value of each facet seen in a result (a value ticked stays offered when the results have no other).</summary>
    private readonly Dictionary<string, HashSet<string>> _known = new()
    {
        [ModelField] = [], [LanguageField] = [], [TopicField] = [], [CollectionField] = []
    };
    private List<CompletionItemViewModel> _found = [];
    private CancellationTokenSource? _search;
    private bool _quiet;

    /// <summary>Design-time constructor using default server URL from settings.</summary>
    public CompletionWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    /// <summary>Uses the given assistant client; lists the newest completions.</summary>
    public CompletionWindowViewModel(KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _ = SearchAsync(CancellationToken.None);
    }

    // ── The search ──

    [ObservableProperty]
    private string _term = string.Empty;

    public string[] SearchInChoices { get; } = ["Prompt and completion", "Prompt", "Completion"];

    [ObservableProperty]
    private string _searchIn = "Prompt and completion";

    /// <summary>The first day (or instant) of the period, null: no start.</summary>
    [ObservableProperty]
    private DateTime? _from;

    /// <summary>The last day of the period (a date without time: the whole day), null: no end.</summary>
    [ObservableProperty]
    private DateTime? _to;

    public string[] PeriodPresets { get; } = ["Today", "24 h", "7 days", "30 days", "This month", "All"];

    public string[] SortChoices { get; } = ["Newest", "Oldest", "Relevance", "Longest completion", "Shortest completion", "Slowest", "Fastest"];

    [ObservableProperty]
    private string _sort = "Newest";

    /// <summary>Choices of the number of results.</summary>
    public int[] SizeChoices { get; } = [100, 250, 500, 1000, 2500, 5000];

    [ObservableProperty]
    private int _size = 500;

    /// <summary>Only the completions that hold a block of code.</summary>
    [ObservableProperty]
    private bool _codeOnly;

    /// <summary>The filters in use, in words (shown above the results).</summary>
    [ObservableProperty]
    private string _activeFilters = "No filter";

    // ── The results ──

    /// <summary>Completions found.</summary>
    public ObservableCollection<CompletionItemViewModel> Results { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedMetadata), nameof(SelectedTerms))]
    private CompletionItemViewModel? _selectedResult;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Loading the latest completions…";

    [ObservableProperty]
    private bool _isError;

    /// <summary>Asks the user to confirm (message, confirm label); set by the view.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    // ── The facets ──

    public ObservableCollection<FacetValue> ModelFacets { get; } = [];
    public ObservableCollection<FacetValue> LanguageFacets { get; } = [];
    public ObservableCollection<FacetValue> TopicFacets { get; } = [];
    public ObservableCollection<FacetValue> CollectionFacets { get; } = [];

    // ── The statistics ──

    [ObservableProperty] private string _countText = "0";
    [ObservableProperty] private string _countDetails = string.Empty;
    [ObservableProperty] private string _periodText = "—";
    [ObservableProperty] private string _periodDetails = string.Empty;
    [ObservableProperty] private string _modelsText = "0";
    [ObservableProperty] private string _modelsDetails = string.Empty;
    [ObservableProperty] private string _wordsText = "0";
    [ObservableProperty] private string _wordsDetails = string.Empty;
    [ObservableProperty] private string _durationText = "—";
    [ObservableProperty] private string _durationDetails = string.Empty;
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _speedDetails = string.Empty;

    /// <summary>The completions over time, by hour, day or week (<see cref="TimelineUnit"/>).</summary>
    [ObservableProperty] private IReadOnlyList<ChartBar> _timeline = [];
    [ObservableProperty] private string _timelineUnit = "day";

    /// <summary>The completions by weekday (Monday first) and hour: 7 × 24 counts.</summary>
    [ObservableProperty] private IReadOnlyList<int> _weekHours = new int[7 * 24];

    [ObservableProperty] private IReadOnlyList<ChartBar> _durationBars = [];
    [ObservableProperty] private IReadOnlyList<ChartBar> _lengthBars = [];
    [ObservableProperty] private IReadOnlyList<ChartBar> _topicBars = [];

    /// <summary>Languages of the results (field <c>language</c>), for the pie chart: the most frequent first.</summary>
    [ObservableProperty]
    private IReadOnlyList<Views.PieSlice> _languages = [];

    /// <summary>Most frequent terms of the results, in a scattered order.</summary>
    public ObservableCollection<WordCloudItem> Cloud { get; } = [];

    /// <summary>The metadata of the selected completion, one "key: value" per line.</summary>
    public string SelectedMetadata => SelectedResult is not { } item ? string.Empty : string.Join("\n", new[]
        {
            $"Id: {item.Entry.Id}",
            $"Date: {item.Entry.Date.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            $"Model: {item.Entry.Model}",
            $"Language: {item.Entry.Language}",
            $"Topic: {item.Entry.Topic}",
            $"Collection: {item.Entry.CollectionName}",
            $"Machine / user: {item.Entry.MachineName} / {item.Entry.UserName}",
            $"Prompt: {item.PromptWords} words, {item.Entry.Prompt.Length} characters",
            $"Completion: {item.CompletionWords} words, {item.Entry.Completion.Length} characters",
            item.Duration is { } d ? $"Duration: {d:0.##} s ({item.WordsPerSecond:0.#} words/s)" : null,
            item.Entry.Comment.Length > 0 ? $"Comment: {item.Entry.Comment}" : null
        }.Concat(item.Entry.Metadatas.Select(m => $"{m.Key}: {MetaValue(m)}"))
        .Where(l => l is not null));

    /// <summary>The most frequent terms of the selected completion (its word counts, else counted here).</summary>
    public string SelectedTerms => SelectedResult is not { } item ? string.Empty
        : item.Entry.WordCounts.Count > 0
            ? string.Join(" · ", item.Entry.WordCounts.OrderByDescending(w => w.Count).Take(30).Select(w => $"{w.Term} ({w.Count})"))
            : string.Join(" · ", WordCloud.TopTerms([item.Entry.Prompt, item.Entry.Completion], 30).Select(t => $"{t.Term} ({t.Count})"));

    private static string MetaValue(CompletionMetaData m) =>
        m.ValueString.Length > 0 ? m.ValueString : m.ValueDouble != 0 ? m.ValueDouble.ToString("0.###", CultureInfo.InvariantCulture) : m.ValueInt.ToString(CultureInfo.InvariantCulture);

    // ── The search ──

    /// <summary>
    /// Searches the completions on the server with the text, the period, the facets ticked and the order (the orders by
    /// size or duration are made here on the results, newest first from the server), then shows them and their statistics.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        _search?.Cancel();
        var search = _search = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var query = BuildQuery();
        IsBusy = true;
        IsError = false;
        Status = "Searching the completions…";
        try
        {
            var entries = await _assistant.QueryCompletionsAsync(query, search.Token);
            if (search.IsCancellationRequested)
                return;
            _found = entries.Select(e => new CompletionItemViewModel(e)).ToList();
            foreach (var item in _found)
            {
                Remember(ModelField, item.Entry.Model);
                Remember(LanguageField, item.Entry.Language);
                Remember(TopicField, item.Entry.Topic);
                Remember(CollectionField, item.Entry.CollectionName);
            }
            Apply();
            ActivityLog.Info("Completion", $"Advanced search ({ActiveFilters}): {Results.Count} result(s)");
        }
        catch (OperationCanceledException)
        {
            // Another search started (or the window closed).
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Completion", "Completion search failed: server unreachable", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Completion", "Completion search failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_search, search))
                IsBusy = false;
        }
    }

    private CompletionQuery BuildQuery()
    {
        var (from, to) = Period();
        return new CompletionQuery
        {
            Term = Term.Trim(),
            SearchIn = SearchIn switch { "Prompt" => "prompt", "Completion" => "completion", _ => "all" },
            From = from is { } f ? new DateTimeOffset(f) : null,
            To = to is { } t ? new DateTimeOffset(t) : null,
            Models = [.. _selected[ModelField]],
            Languages = [.. _selected[LanguageField]],
            Topics = [.. _selected[TopicField]],
            Collections = [.. _selected[CollectionField]],
            Sort = Sort switch { "Oldest" => "oldest", "Relevance" => "relevance", _ => "newest" },
            Size = Size
        };
    }

    /// <summary>The period searched: <see cref="To"/> without a time is the end of that day.</summary>
    private (DateTime? From, DateTime? To) Period() =>
        (From, To is { } to && to.TimeOfDay == TimeSpan.Zero ? to.Date.AddDays(1).AddTicks(-1) : To);

    private void Remember(string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _known[field].Add(value.Trim());
    }

    /// <summary>A new search for a change of the filters (the one running is cancelled).</summary>
    private void Requery()
    {
        if (!_quiet)
            _ = SearchAsync(CancellationToken.None);
    }

    partial void OnSearchInChanged(string value) => Requery();
    partial void OnFromChanged(DateTime? value) => Requery();
    partial void OnToChanged(DateTime? value) => Requery();
    partial void OnSizeChanged(int value) => Requery();
    partial void OnCodeOnlyChanged(bool value) => Apply();

    partial void OnSortChanged(string value)
    {
        // The orders of the server need a new search; the others sort the results here.
        if (value is "Newest" or "Oldest" or "Relevance")
            Requery();
        else
            Apply();
    }

    /// <summary>A preset of the period (Today, 24 h, 7 days, 30 days, This month, All).</summary>
    [RelayCommand]
    private void SetPeriod(string preset)
    {
        var now = DateTime.Now;
        _quiet = true;
        (From, To) = preset switch
        {
            "Today" => (now.Date, (DateTime?)null),
            "24 h" => (now.AddHours(-24), null),
            "7 days" => (now.Date.AddDays(-6), null),
            "30 days" => (now.Date.AddDays(-29), null),
            "This month" => (new DateTime(now.Year, now.Month, 1), null),
            _ => ((DateTime?)null, (DateTime?)null)
        };
        _quiet = false;
        Requery();
    }

    /// <summary>A bar of the timeline clicked: the period becomes that hour, day or week.</summary>
    [RelayCommand]
    private void ZoomTo(ChartBar? bar)
    {
        if (bar?.From is not { } from || bar.To is not { } to)
            return;
        _quiet = true;
        From = from;
        To = to;
        _quiet = false;
        Requery();
    }

    /// <summary>A value of a facet clicked: ticked (only the completions with one of the ticked values) or unticked.</summary>
    [RelayCommand]
    private void ToggleFacet(FacetValue? value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.Value))
            return;
        var set = _selected[value.Field];
        if (!set.Remove(value.Value))
            set.Add(value.Value);
        Requery();
    }

    /// <summary>All the filters off (the text stays).</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        _quiet = true;
        foreach (var set in _selected.Values)
            set.Clear();
        From = null;
        To = null;
        SearchIn = SearchInChoices[0];
        CodeOnly = false;
        Sort = "Newest";
        _quiet = false;
        Requery();
    }

    /// <summary>A term of the cloud clicked: it becomes the search.</summary>
    [RelayCommand]
    private async Task SearchTermAsync(string term)
    {
        Term = term;
        await SearchAsync(CancellationToken.None);
    }

    /// <summary>The results of the server, filtered (code only) and ordered here, then the facets and the statistics.</summary>
    private void Apply()
    {
        IEnumerable<CompletionItemViewModel> items = _found;
        if (CodeOnly)
            items = items.Where(i => i.HasCode);
        items = Sort switch
        {
            "Longest completion" => items.OrderByDescending(i => i.CompletionWords),
            "Shortest completion" => items.OrderBy(i => i.CompletionWords),
            "Slowest" => items.OrderByDescending(i => i.Duration ?? -1),
            "Fastest" => items.OrderBy(i => i.Duration ?? double.MaxValue),
            _ => items
        };
        var selectedId = SelectedResult?.Entry.Id;
        Results.Clear();
        foreach (var item in items)
            Results.Add(item);
        SelectedResult = Results.FirstOrDefault(r => r.Entry.Id == selectedId) ?? Results.FirstOrDefault();

        ActiveFilters = DescribeFilters();
        BuildFacets();
        BuildStatistics();
        BuildCloud();
        IsError = false;
        Status = Results.Count == 0 ? "No completion matches" : $"{Results.Count} completion(s)" + (Results.Count >= Size ? $" (the first {Size}: raise the number of results to see more)" : string.Empty);
    }

    private string DescribeFilters()
    {
        var parts = new List<string>();
        if (Term.Trim().Length > 0)
            parts.Add($"\"{Term.Trim()}\"" + (SearchIn == SearchInChoices[0] ? string.Empty : $" in the {SearchIn.ToLowerInvariant()}"));
        var (from, to) = Period();
        if (from is not null || to is not null)
            parts.Add($"{(from is { } f ? f.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "…")} → {(to is { } t ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "now")}");
        foreach (var (field, values) in _selected)
            if (values.Count > 0)
                parts.Add($"{field}: {string.Join(" or ", values.Order(StringComparer.OrdinalIgnoreCase))}");
        if (CodeOnly)
            parts.Add("with code");
        if (Sort != "Newest")
            parts.Add($"order: {Sort.ToLowerInvariant()}");
        return parts.Count == 0 ? "No filter" : string.Join(" · ", parts);
    }

    private void BuildFacets()
    {
        Fill(ModelFacets, ModelField, i => i.Entry.Model);
        Fill(LanguageFacets, LanguageField, i => i.Entry.Language);
        Fill(TopicFacets, TopicField, i => i.Entry.Topic);
        Fill(CollectionFacets, CollectionField, i => i.Entry.CollectionName);
    }

    /// <summary>
    /// The values of a facet: the ones of the results (the most frequent first), the ticked ones, and, while a value of
    /// the facet is ticked, the other values known (they have no result then, but can be ticked too).
    /// </summary>
    private void Fill(ObservableCollection<FacetValue> facets, string field, Func<CompletionItemViewModel, string?> value)
    {
        var counts = Results.GroupBy(r => value(r)?.Trim() ?? string.Empty).Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.Count());
        var values = counts.Keys.Concat(_selected[field]).Concat(_selected[field].Count > 0 ? _known[field] : []).Distinct()
            .OrderByDescending(v => _selected[field].Contains(v))
            .ThenByDescending(v => counts.GetValueOrDefault(v))
            .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Take(40);
        facets.Clear();
        foreach (var v in values)
            facets.Add(new FacetValue(field, v, counts.GetValueOrDefault(v), Results.Count, _selected[field].Contains(v)));
    }

    // ── The statistics ──

    private void BuildStatistics()
    {
        var items = Results.ToList();
        CountText = items.Count.ToString("N0", CultureInfo.InvariantCulture);
        CountDetails = $"{items.Count(i => i.HasCode)} with code · {items.Select(i => i.Entry.Topic).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Count()} topic(s)";

        if (items.Count == 0)
        {
            PeriodText = "—";
            PeriodDetails = ModelsDetails = WordsDetails = DurationDetails = SpeedDetails = string.Empty;
            ModelsText = WordsText = "0";
            DurationText = SpeedText = "—";
            Timeline = [];
            WeekHours = new int[7 * 24];
            DurationBars = LengthBars = TopicBars = [];
            BuildLanguages();
            return;
        }

        var dates = items.Select(i => i.Entry.Date.LocalDateTime).ToList();
        var first = dates.Min();
        var last = dates.Max();
        var span = last - first;
        PeriodText = span.TotalDays >= 1 ? $"{span.TotalDays:0.#} days" : span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h" : $"{Math.Max(1, span.TotalMinutes):0} min";
        PeriodDetails = $"{first:yyyy-MM-dd HH:mm} → {last:yyyy-MM-dd HH:mm}";

        var models = items.GroupBy(i => string.IsNullOrWhiteSpace(i.Entry.Model) ? "?" : i.Entry.Model).OrderByDescending(g => g.Count()).ToList();
        ModelsText = models.Count.ToString(CultureInfo.InvariantCulture);
        ModelsDetails = string.Join(" · ", models.Take(3).Select(g => $"{g.Key} ({g.Count()})"));

        WordsText = $"{items.Average(i => i.CompletionWords):0}";
        WordsDetails = $"words per completion · prompts {items.Average(i => i.PromptWords):0} · total {items.Sum(i => (long)i.CompletionWords):N0}";

        var durations = items.Where(i => i.Duration is not null).Select(i => i.Duration!.Value).Order().ToList();
        if (durations.Count > 0)
        {
            DurationText = $"{durations.Average():0.#} s";
            DurationDetails = $"median {Percentile(durations, 0.5):0.#} s · p90 {Percentile(durations, 0.9):0.#} s · max {durations[^1]:0.#} s";
            var speeds = items.Where(i => i.WordsPerSecond is not null).Select(i => i.WordsPerSecond!.Value).Order().ToList();
            SpeedText = $"{speeds.Average():0.#}";
            SpeedDetails = $"words / s · median {Percentile(speeds, 0.5):0.#} · total time {TimeSpan.FromSeconds(durations.Sum()):hh\\:mm\\:ss}";
        }
        else
        {
            DurationText = SpeedText = "—";
            DurationDetails = SpeedDetails = "no duration kept for these completions";
        }

        BuildTimeline(dates, first, last);
        var grid = new int[7 * 24];
        foreach (var date in dates)
            grid[((int)date.DayOfWeek + 6) % 7 * 24 + date.Hour]++;
        WeekHours = grid;

        DurationBars = Histogram(durations, [5, 10, 20, 40, 80, 160], "s", "completion(s) answered in");
        LengthBars = Histogram(items.Select(i => (double)i.CompletionWords).ToList(), [50, 100, 200, 400, 800, 1600], "w", "completion(s) of");
        TopicBars = items.GroupBy(i => string.IsNullOrWhiteSpace(i.Entry.Topic) ? "(none)" : i.Entry.Topic!.Trim())
            .OrderByDescending(g => g.Count()).Take(15)
            .Select(g => new ChartBar(g.Key, g.Count(), $"{g.Key}: {g.Count()} completion(s)")).ToList();
        BuildLanguages();
    }

    /// <summary>The completions by hour (a period of a day and a half or less), by day (four months or less) or by week.</summary>
    private void BuildTimeline(List<DateTime> dates, DateTime first, DateTime last)
    {
        var span = last - first;
        Func<DateTime, DateTime> start;
        TimeSpan step;
        string format;
        if (span.TotalHours <= 36)
        {
            start = d => new DateTime(d.Year, d.Month, d.Day, d.Hour, 0, 0);
            step = TimeSpan.FromHours(1);
            format = "HH:00";
            TimelineUnit = "hour";
        }
        else if (span.TotalDays <= 120)
        {
            start = d => d.Date;
            step = TimeSpan.FromDays(1);
            format = "MM-dd";
            TimelineUnit = "day";
        }
        else
        {
            start = d => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));
            step = TimeSpan.FromDays(7);
            format = "yyyy-MM-dd";
            TimelineUnit = "week";
        }
        var counts = dates.GroupBy(start).ToDictionary(g => g.Key, g => g.Count());
        var bars = new List<ChartBar>();
        for (var at = start(first); at <= last && bars.Count < 400; at += step)
        {
            var count = counts.GetValueOrDefault(at);
            var end = at + step - TimeSpan.FromTicks(1);
            bars.Add(new ChartBar(at.ToString(format, CultureInfo.InvariantCulture), count,
                $"{at:yyyy-MM-dd HH:mm} → {end:yyyy-MM-dd HH:mm}: {count} completion(s) · click to keep only this {TimelineUnit}", at, end));
        }
        Timeline = bars;
    }

    /// <summary>The values counted between the limits (the last bar: above the last limit).</summary>
    private static IReadOnlyList<ChartBar> Histogram(List<double> values, double[] limits, string unit, string tip)
    {
        if (values.Count == 0)
            return [];
        var bars = new List<ChartBar>();
        var low = 0.0;
        foreach (var high in limits.Append(double.MaxValue))
        {
            var count = values.Count(v => v >= low && v < high);
            var label = high == double.MaxValue ? $"{low:0}+ {unit}" : $"{low:0}–{high:0} {unit}";
            bars.Add(new ChartBar(label, count, $"{count} {tip} {label}"));
            low = high;
        }
        return bars;
    }

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>
    /// The cloud of the most frequent terms of the prompts and completions of the results: the font size grows with
    /// the square root of the count (12 to 46 points), the most frequent terms get the first colors; the terms are
    /// scattered in an order fixed by their spelling, so that the big ones are not all together.
    /// </summary>
    private void BuildCloud()
    {
        Cloud.Clear();
        var terms = WordCloud.TopTerms(Results.SelectMany(r => new[] { r.Entry.Prompt, r.Entry.Completion }), CloudSize);
        if (terms.Count == 0)
            return;
        double max = Math.Sqrt(terms[0].Count), min = Math.Sqrt(terms[^1].Count);
        var items = terms.Select((t, rank) =>
        {
            var share = max - min < 1e-9 ? 1 : (Math.Sqrt(t.Count) - min) / (max - min);
            return new WordCloudItem(t.Term, t.Count, Math.Round(12 + 34 * share), Palette[Math.Min(Palette.Length - 1, rank * Palette.Length / terms.Count)]);
        });
        foreach (var item in items.OrderBy(i => StableHash(i.Term)))
            Cloud.Add(item);
    }

    /// <summary>The pie chart of the languages of the results (an empty language is "?").</summary>
    private void BuildLanguages()
    {
        var groups = Results.GroupBy(r => string.IsNullOrWhiteSpace(r.Entry.Language) ? "?" : r.Entry.Language.Trim().ToUpperInvariant())
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var total = Math.Max(1, Results.Count);
        Languages = groups.Select((g, i) => new Views.PieSlice(g.Key, g.Count(), Palette[i % Palette.Length]) { Share = g.Count() / (double)total })
            .ToList();
    }

    /// <summary>A hash of a term that is the same at every run (string.GetHashCode changes from one run to the next).</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;
        return hash;
    }

    // ── Export, delete ──

    /// <summary>Where the exports of the results go: Documents/kaonomi/completions.</summary>
    public static string ExportDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "kaonomi", "completions");

    /// <summary>The path of the last export (null before the first).</summary>
    [ObservableProperty]
    private string? _lastExport;

    /// <summary>The results saved as CSV (one line each: date, model, language, topic, collection, sizes, duration, prompt, completion) or JSON (the completions as saved).</summary>
    [RelayCommand]
    private async Task ExportAsync(string format)
    {
        if (Results.Count == 0)
        {
            Status = "Nothing to export";
            return;
        }
        try
        {
            Directory.CreateDirectory(ExportDirectory);
            var json = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            var path = Path.Combine(ExportDirectory, $"completions-{DateTime.Now:yyyyMMdd-HHmmss}.{(json ? "json" : "csv")}");
            if (json)
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Results.Select(r => r.Entry), new JsonSerializerOptions { WriteIndented = true }));
            else
            {
                var csv = new StringBuilder("id;date;model;language;topic;collection;promptWords;completionWords;durationSeconds;hasCode;prompt;completion\n");
                foreach (var r in Results)
                    csv.AppendJoin(';', new[]
                    {
                        r.Entry.Id, r.Entry.Date.ToString("o", CultureInfo.InvariantCulture), r.Entry.Model, r.Entry.Language, r.Entry.Topic ?? string.Empty, r.Entry.CollectionName,
                        r.PromptWords.ToString(CultureInfo.InvariantCulture), r.CompletionWords.ToString(CultureInfo.InvariantCulture),
                        r.Duration?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty, r.HasCode ? "1" : "0", r.Entry.Prompt, r.Entry.Completion
                    }.Select(Csv)).Append('\n');
                // UTF-8 with its mark: Excel reads the accents.
                await File.WriteAllTextAsync(path, csv.ToString(), new UTF8Encoding(true));
            }
            LastExport = path;
            Status = $"{Results.Count} completion(s) exported: {path}";
            ActivityLog.Info("Completion", $"Exported {Results.Count} completion(s) to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsError = true;
            Status = "Not exported: " + ex.Message;
        }
    }

    private static string Csv(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

    private bool CanDelete() => SelectedResult is not null;

    /// <summary>Deletes the selected completion from Kaonomi (after confirmation).</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (SelectedResult is not { } selected || !await ConfirmAsync($"Delete the completion \"{selected.Title}\" from Kaonomi?", "Delete"))
            return;
        try
        {
            var result = await _assistant.DeleteCompletionAsync(selected.Entry.Id);
            if (!result.Success)
            {
                IsError = true;
                Status = result.Message ?? "Not deleted";
                return;
            }
            _found.Remove(selected);
            var index = Results.IndexOf(selected);
            Apply();
            SelectedResult = Results.Count == 0 ? null : Results[Math.Min(Math.Max(0, index), Results.Count - 1)];
            Status = "Completion deleted";
            ActivityLog.Info("Completion", $"Deleted completion {selected.Entry.Id}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            IsError = true;
            Status = ex.Message;
        }
    }
}
