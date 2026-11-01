using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A log line of the dashboard table.</summary>
public sealed class LogRowViewModel(LogEntry log)
{
    public LogEntry Log { get; } = log;
    public string Date { get; } = log.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string Type { get; } = string.IsNullOrWhiteSpace(log.Type) ? "INFO" : log.Type.ToUpperInvariant();
    public string App { get; } = log.AppName ?? string.Empty;
    public string Comment { get; } = log.Comment ?? string.Empty;
    public string Origin { get; } = $"{log.UserName}@{log.MachineName}";
    public bool IsError => Type == LogEntry.Error;
}

/// <summary>
/// What the dashboard loads: every log of the last <see cref="Minutes"/> minutes (<c>GET /log/since</c>), or the last
/// <see cref="Size"/> logs (<c>GET /log/last</c>).
/// </summary>
public sealed record LogScope(string Label, int? Minutes, int? Size)
{
    public override string ToString() => Label;
}

/// <summary>An application of the "By application" panel: its number of logs, errors and a bar proportional to the busiest one.</summary>
public sealed class AppStatViewModel(string app, int count, int errors, double share)
{
    public string App { get; } = app;
    public int Count { get; } = count;
    public int Errors { get; } = errors;
    public bool HasErrors => Errors > 0;
    public string CountText { get; } = errors > 0 ? $"{count:N0} · {errors:N0} err." : $"{count:N0}";

    /// <summary>Width of the bar, 0–100 (the busiest application = 100).</summary>
    public double Share { get; } = share;
}

/// <summary>A distinct error message with its number of occurrences.</summary>
public sealed class ErrorStatViewModel(string message, int count, string apps, string last)
{
    public string Message { get; } = message;
    public int Count { get; } = count;
    public string Details { get; } = $"{count:N0}× · {apps} · last {last}";
}

/// <summary>
/// <c>/log</c> and <c>/logs</c> window: dashboard of the logs of every application of the last hour (<c>GET /log/since</c>;
/// last 15 minutes or last 10,000 logs on demand):
/// key figures, activity over time (all logs and errors), logs by application, most frequent errors and the log
/// table. The filters (text, type, application) apply to the whole dashboard; it can refresh itself every 15 seconds.
/// </summary>
public partial class LogDashboardViewModel : ViewModelBase, INoteContextProvider, IDisposable
{
    /// <summary>Points of the activity graph.</summary>
    public const int Buckets = 60;

    private const string AllTypes = "All types";
    private const string AllApps = "All applications";

    private readonly KaonomiAssistantService _assistant;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private IReadOnlyList<LogEntry> _logs = [];

    /// <summary>Design-time constructor.</summary>
    public LogDashboardViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    /// <summary>Uses <paramref name="assistant"/> to read <c>GET /log/last</c>.</summary>
    public LogDashboardViewModel(KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _scope = Scopes[0];
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            Apply();
        };
        _refreshTimer.Tick += (_, _) =>
        {
            if (!RefreshCommand.IsRunning)
                RefreshCommand.Execute(null);
        };
    }

    /// <summary>Periods offered: the last hour (default), the last 15 minutes, or the last 10,000 logs.</summary>
    public IReadOnlyList<LogScope> Scopes { get; } =
    [
        new("Last hour", 60, null), new("Last 15 minutes", 15, null), new("Last 10,000 logs", null, 10000)
    ];

    public IReadOnlyList<string> Types { get; } = [AllTypes, LogEntry.Info, LogEntry.Error];

    public ObservableCollection<string> Apps { get; } = [AllApps];

    [ObservableProperty] private LogScope _scope = new("Last hour", 60, null);
    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private string _selectedType = AllTypes;
    [ObservableProperty] private string _selectedApp = AllApps;
    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private string _status = "Loading…";
    [ObservableProperty] private bool _isError;

    // Key figures.
    [ObservableProperty] private string _totalText = "–";
    [ObservableProperty] private string _totalDetails = string.Empty;
    [ObservableProperty] private string _errorText = "–";
    [ObservableProperty] private string _errorDetails = string.Empty;
    [ObservableProperty] private IBrush _errorBrush = Brushes.Gray;
    [ObservableProperty] private string _appsText = "–";
    [ObservableProperty] private string _appsDetails = string.Empty;
    [ObservableProperty] private string _spanText = "–";
    [ObservableProperty] private string _spanDetails = string.Empty;
    [ObservableProperty] private string _rateText = "–";
    [ObservableProperty] private string _rateDetails = string.Empty;

    // Activity graph.
    [ObservableProperty] private double _activityMax = 1;
    [ObservableProperty] private string _activityMaxLabel = string.Empty;
    [ObservableProperty] private string _activityStart = string.Empty;
    [ObservableProperty] private string _activityEnd = string.Empty;
    [ObservableProperty] private string _activityTitle = "Activity";

    [ObservableProperty] private string _tableTitle = "Logs";

    /// <summary>Logs per time slot (the whole span cut into <see cref="Buckets"/> slots).</summary>
    public ObservableCollection<double> Activity { get; } = [];

    /// <summary>Errors per time slot.</summary>
    public ObservableCollection<double> ErrorActivity { get; } = [];

    public ObservableCollection<AppStatViewModel> AppStats { get; } = [];

    public ObservableCollection<ErrorStatViewModel> TopErrors { get; } = [];

    /// <summary>Rows of the table (the filtered logs). Replaced as a whole: the last hour can hold 100,000+ logs.</summary>
    [ObservableProperty] private IReadOnlyList<LogRowViewModel> _rows = [];

    /// <summary>One row per loaded log, built once per load (the filters only select among them).</summary>
    private List<LogRowViewModel> _allRows = [];

    /// <summary>The search is applied 300 ms after the last key, not at every key (large log sets).</summary>
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public bool HasTopErrors => TopErrors.Count > 0;

    partial void OnSearchChanged(string value)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    partial void OnSelectedTypeChanged(string value) => Apply();

    partial void OnSelectedAppChanged(string value) => Apply();

    partial void OnScopeChanged(LogScope value) => RefreshCommand.Execute(null);

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();
    }

    /// <summary>
    /// Erases ALL the logs on the server (every application, every date) after a confirmation, then reads them again
    /// (only the logs written since then are left).
    /// </summary>
    [RelayCommand]
    private async Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        if (!await Confirmations.Ask("Erase ALL the logs of every application on the server? They cannot be brought back.", "Erase all"))
            return;
        IsError = false;
        Status = "Erasing all the logs…";
        try
        {
            var result = await _assistant.DeleteAllLogsAsync(cancellationToken);
            if (!result.IsSuccess)
            {
                IsError = true;
                Status = "The logs were not erased: " + result.Message;
                return;
            }
            _logs = [];
            _allRows = [];
            ActivityLog.Info("Logs", $"All the logs erased from the log dashboard ({result.Message})");
            await RefreshAsync(cancellationToken);
            Status = $"{result.Message} at {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            Status = "Erasing cancelled";
        }
        catch (HttpRequestException ex)
        {
            IsError = true;
            Status = "The logs were not erased: " + ex.Message;
        }
    }

    /// <summary>Reads the last logs from the server, then rebuilds the dashboard.</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsError = false;
        Status = Scope.Minutes is { } loading
            ? $"Loading every log of the last {loading} minutes…"
            : $"Loading the last {Scope.Size:N0} logs…";
        try
        {
            // The last hour can hold 100,000+ logs: read, decoded and sorted on the thread pool, never on the UI thread
            // (shared by every window: they would all freeze at each auto-refresh).
            var scope = Scope;
            var (logs, rows, apps) = await Task.Run(async () =>
            {
                var loaded = scope.Minutes is { } minutes
                    ? await _assistant.GetLogsSinceAsync(minutes, cancellationToken)
                    : await _assistant.GetLastLogsAsync(scope.Size ?? 10000, cancellationToken);
                var names = loaded.Select(l => l.AppName ?? string.Empty).Where(a => a.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
                return (loaded, loaded.OrderByDescending(l => l.Date).Select(l => new LogRowViewModel(l)).ToList(), names);
            }, cancellationToken);
            _logs = logs;
            _allRows = rows;

            var selected = SelectedApp;
            Apps.Clear();
            Apps.Add(AllApps);
            foreach (var app in apps)
                Apps.Add(app);
            SelectedApp = apps.Contains(selected) ? selected : AllApps;

            await ApplyAsync();
            Status = $"{_logs.Count:N0} logs loaded at {DateTime.Now:HH:mm:ss}" + (AutoRefresh ? " · auto-refresh every 15 s" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Loading cancelled";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = $"Logs not loaded: {ex.Message}";
            ActivityLog.Error("Logs", "Log dashboard: GET /log/last failed", ex);
        }
    }

    /// <summary>Clears the text, type and application filters.</summary>
    [RelayCommand]
    private void ClearFilters()
    {
        Search = string.Empty;
        SelectedType = AllTypes;
        SelectedApp = AllApps;
    }

    /// <summary>Filters on one application (click in the "By application" panel); a second click shows them all again.</summary>
    [RelayCommand]
    private void FilterApp(AppStatViewModel? app)
    {
        if (app is not null)
            SelectedApp = SelectedApp == app.App ? AllApps : app.App;
    }

    /// <summary>Shows the logs of one error message (click in the "Most frequent errors" panel).</summary>
    [RelayCommand]
    private void FilterError(ErrorStatViewModel? error)
    {
        if (error is null)
            return;
        SelectedType = LogEntry.Error;
        Search = error.Message;
    }

    /// <summary>Number of the last <see cref="ApplyAsync"/>: the result of an older one (filters changed meanwhile) is dropped.</summary>
    private int _applyVersion;

    /// <summary>Rebuilds every panel from the loaded logs and the filters (filter changed: not awaited).</summary>
    private void Apply() => _ = ApplyAsync();

    /// <summary>
    /// Rebuilds every panel from the loaded logs and the filters: the figures are computed on the thread pool (100,000+
    /// logs), only their display runs on the UI thread.
    /// </summary>
    private async Task ApplyAsync()
    {
        var version = ++_applyVersion;
        var allRows = _allRows;
        var loaded = _logs.Count;
        var scopeLabel = Scope.Label;
        var (term, type, app) = (Search.Trim(), SelectedType, SelectedApp);
        DashboardFigures figures;
        try
        {
            figures = await Task.Run(() => Compute(allRows, loaded, scopeLabel, term, type, app));
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Logs", "Log dashboard not rebuilt", ex);
            return;
        }
        if (version == _applyVersion)
            Publish(figures);
    }

    /// <summary>Everything the dashboard shows for a set of filters, computed away from the UI thread.</summary>
    private sealed record DashboardFigures(
        List<LogRowViewModel> Rows, string TotalText, string TotalDetails, string ErrorText, string ErrorDetails,
        string ErrorColor, string AppsText, string AppsDetails, string SpanText, string SpanDetails, string RateText,
        string RateDetails, double[] Activity, double[] ErrorActivity, double ActivityMax, string ActivityMaxLabel,
        string ActivityStart, string ActivityEnd, string ActivityTitle, List<AppStatViewModel> AppStats,
        List<ErrorStatViewModel> TopErrors, string TableTitle);

    private static DashboardFigures Compute(List<LogRowViewModel> allRows, int loaded, string scopeLabel,
        string term, string type, string app)
    {
        var rows = allRows.Where(r =>
                (type == AllTypes || string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase))
                && (app == AllApps || string.Equals(r.App, app, StringComparison.OrdinalIgnoreCase))
                && (term.Length == 0
                    || r.Comment.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || r.App.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var logs = rows.Select(r => r.Log).ToList();

        var errors = logs.Count(IsErrorLog);
        var errorRate = logs.Count == 0 ? 0 : errors * 100.0 / logs.Count;

        var byApp = logs.GroupBy(l => string.IsNullOrWhiteSpace(l.AppName) ? "(none)" : l.AppName!)
            .Select(g => (App: g.Key, Count: g.Count(), Errors: g.Count(IsErrorLog)))
            .OrderByDescending(a => a.Count).ToList();

        string spanText, spanDetails, rateText, rateDetails;
        if (logs.Count > 0)
        {
            var newest = logs[0].Date.ToLocalTime();
            var oldest = logs[^1].Date.ToLocalTime();
            var span = newest - oldest;
            spanText = Duration(span);
            spanDetails = $"{oldest:dd/MM HH:mm:ss} → {newest:dd/MM HH:mm:ss}";
            var minutes = Math.Max(span.TotalMinutes, 1.0 / 60);
            rateText = $"{logs.Count / minutes:N0}";
            rateDetails = "logs per minute";
        }
        else
        {
            spanText = rateText = "–";
            spanDetails = rateDetails = "no log";
        }

        var (activity, errorActivity, activityMax, maxLabel, start, end, title) = BuildActivity(logs);

        var busiest = byApp.Count == 0 ? 1 : byApp[0].Count;
        var appStats = byApp.Take(15)
            .Select(a => new AppStatViewModel(a.App, a.Count, a.Errors, a.Count * 100.0 / busiest)).ToList();

        var topErrors = logs.Where(IsErrorLog)
            .GroupBy(l => Shorten(l.Comment ?? string.Empty, 160))
            .OrderByDescending(g => g.Count()).Take(8)
            .Select(group => new ErrorStatViewModel(group.Key, group.Count(),
                string.Join(", ", group.Select(l => l.AppName).Distinct().Take(2)),
                group.Max(l => l.Date).ToLocalTime().ToString("dd/MM HH:mm:ss")))
            .ToList();

        return new DashboardFigures(
            rows,
            logs.Count.ToString("N0"),
            logs.Count == loaded ? scopeLabel.ToLowerInvariant() : $"of {loaded:N0} loaded (filtered)",
            errors.ToString("N0"),
            $"{errorRate:0.##} % of the logs",
            errors == 0 ? "#3DBB4A" : errorRate < 5 ? "#F28C38" : "#E81123",
            byApp.Count.ToString("N0"),
            byApp.Count == 0 ? string.Empty : $"busiest: {byApp[0].App}",
            spanText, spanDetails, rateText, rateDetails,
            activity, errorActivity, activityMax, maxLabel, start, end, title,
            appStats, topErrors,
            logs.Count == loaded ? $"Logs ({logs.Count:N0})" : $"Logs ({logs.Count:N0} of {loaded:N0})");
    }

    /// <summary>Shows <paramref name="figures"/> (UI thread).</summary>
    private void Publish(DashboardFigures figures)
    {
        TotalText = figures.TotalText;
        TotalDetails = figures.TotalDetails;
        ErrorText = figures.ErrorText;
        ErrorDetails = figures.ErrorDetails;
        ErrorBrush = new SolidColorBrush(Color.Parse(figures.ErrorColor));
        AppsText = figures.AppsText;
        AppsDetails = figures.AppsDetails;
        SpanText = figures.SpanText;
        SpanDetails = figures.SpanDetails;
        RateText = figures.RateText;
        RateDetails = figures.RateDetails;

        Activity.Clear();
        foreach (var value in figures.Activity)
            Activity.Add(value);
        ErrorActivity.Clear();
        foreach (var value in figures.ErrorActivity)
            ErrorActivity.Add(value);
        ActivityMax = figures.ActivityMax;
        ActivityMaxLabel = figures.ActivityMaxLabel;
        ActivityStart = figures.ActivityStart;
        ActivityEnd = figures.ActivityEnd;
        ActivityTitle = figures.ActivityTitle;

        AppStats.Clear();
        foreach (var stat in figures.AppStats)
            AppStats.Add(stat);
        TopErrors.Clear();
        foreach (var error in figures.TopErrors)
            TopErrors.Add(error);
        OnPropertyChanged(nameof(HasTopErrors));

        Rows = figures.Rows;
        TableTitle = figures.TableTitle;
    }

    /// <summary>Logs and errors per slot over the time span of the filtered logs (oldest on the left).</summary>
    private static (double[] Totals, double[] Errors, double Max, string MaxLabel, string Start, string End, string Title)
        BuildActivity(List<LogEntry> logs)
    {
        if (logs.Count == 0)
            return ([], [], 1, string.Empty, string.Empty, string.Empty, "Activity");

        var oldest = logs[^1].Date;
        var newest = logs[0].Date;
        var slot = TimeSpan.FromTicks(Math.Max((newest - oldest).Ticks / Buckets, TimeSpan.TicksPerSecond));
        var totals = new double[Buckets];
        var errors = new double[Buckets];
        foreach (var log in logs)
        {
            var index = (int)Math.Min(Buckets - 1, (log.Date - oldest).Ticks / slot.Ticks);
            totals[index]++;
            if (IsErrorLog(log))
                errors[index]++;
        }

        return (totals, errors, Math.Max(1, totals.Max()), $"max {totals.Max():N0} / slot",
            oldest.ToLocalTime().ToString("dd/MM HH:mm:ss"), newest.ToLocalTime().ToString("dd/MM HH:mm:ss"),
            $"Activity · {Buckets} slots of {Duration(slot)}");
    }

    private static bool IsErrorLog(LogEntry log) => string.Equals(log.Type, LogEntry.Error, StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text, int length)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= length ? line : line[..length] + "…";
    }

    private static string Duration(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{span.TotalDays:0.#} d"
        : span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h"
        : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#} min"
        : $"{span.TotalSeconds:0.#} s";

    /// <inheritdoc />
    public string NoteWindowName => "Logs dashboard";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        scope = Scope.Label,
        filters = new { search = Search, type = SelectedType, app = SelectedApp },
        figures = new { logs = TotalText, errors = ErrorText, errorRate = ErrorDetails, applications = AppsText, span = SpanDetails, rate = RateText },
        byApplication = AppStats.Select(a => new { a.App, a.Count, a.Errors }).ToArray(),
        topErrors = TopErrors.Select(e => new { e.Message, e.Count }).ToArray()
    };

    /// <summary>
    /// Content of the window for the SAVE button: period, filters, key figures, activity graph, applications, top errors
    /// and the logs of the table (filters applied), as shown.
    /// </summary>
    public object BuildExport() => new
    {
        window = NoteWindowName,
        generatedAt = DateTimeOffset.Now,
        machineName = Environment.MachineName,
        userName = Environment.UserName,
        status = Status,
        period = Scope.Label,
        filters = new { search = Search, type = SelectedType, application = SelectedApp },
        figures = new
        {
            logs = new { value = TotalText, details = TotalDetails },
            errors = new { value = ErrorText, details = ErrorDetails },
            applications = new { value = AppsText, details = AppsDetails },
            timeSpan = new { value = SpanText, details = SpanDetails },
            rate = new { value = RateText, details = RateDetails }
        },
        activity = new
        {
            title = ActivityTitle,
            start = ActivityStart,
            end = ActivityEnd,
            maximum = ActivityMaxLabel,
            logs = Activity.ToArray(),
            errors = ErrorActivity.ToArray()
        },
        byApplication = AppStats.Select(a => new { application = a.App, count = a.Count, errors = a.Errors, share = a.Share }).ToArray(),
        topErrors = TopErrors.Select(e => new { message = e.Message, count = e.Count, details = e.Details }).ToArray(),
        logCount = Rows.Count,
        logs = Rows.Select(r => new
        {
            id = r.Log.Id,
            date = r.Log.Date,
            type = r.Type,
            application = r.App,
            message = r.Comment,
            userName = r.Log.UserName,
            machineName = r.Log.MachineName
        })
    };

    /// <summary>Stops the auto-refresh.</summary>
    public void Dispose()
    {
        _refreshTimer.Stop();
        _searchDelay.Stop();
    }
}
