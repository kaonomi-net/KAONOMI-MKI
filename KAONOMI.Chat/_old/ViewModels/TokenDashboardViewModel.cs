using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A choice of the period selector.</summary>
public sealed record TokenPeriod(int Minutes, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Consumption of one model (or one application) over the period.</summary>
public sealed class TokenStatViewModel(string name, int calls, long prompt, long completion, double share)
{
    public string Name { get; } = name;
    public int Calls { get; } = calls;
    public long Prompt { get; } = prompt;
    public long Completion { get; } = completion;
    public long Total { get; } = prompt + completion;
    public string TotalText { get; } = $"{prompt + completion:N0}";
    public string Details { get; } = $"{calls:N0} call(s) · {prompt:N0} prompt + {completion:N0} completion · {(calls == 0 ? 0 : (prompt + completion) / calls):N0} / call";

    /// <summary>Width of the bar, 0–100 (the biggest consumer = 100).</summary>
    public double Share { get; } = share;
}

/// <summary>An LLM call of the table.</summary>
public sealed class TokenCallViewModel(TokenUsageEntry usage)
{
    public string Date { get; } = usage.Date.ToLocalTime().ToString("HH:mm:ss");
    public string Model { get; } = usage.Model ?? "?";
    public string App { get; } = usage.AppName ?? string.Empty;
    public string Prompt { get; } = usage.PromptTokens.ToString("N0");
    public string Completion { get; } = usage.CompletionTokens.ToString("N0");
    public string Total { get; } = usage.TotalTokens.ToString("N0");
}

/// <summary>
/// <c>/token</c> and <c>/tokens</c> window: tokens and models consumed over the last hour (or another period),
/// from <c>GET /tokensusage/last</c>: key figures, tokens over time, consumption by model and by application, and
/// the list of the LLM calls. It can refresh itself every 30 seconds.
/// </summary>
public partial class TokenDashboardViewModel : ViewModelBase, INoteContextProvider, IDisposable
{
    /// <summary>Points of the tokens-over-time graph.</summary>
    public const int Buckets = 60;

    private readonly KaonomiAssistantService _assistant;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private IReadOnlyList<TokenUsageEntry> _usages = [];

    /// <summary>Design-time constructor.</summary>
    public TokenDashboardViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    /// <summary>Uses <paramref name="assistant"/> to read <c>GET /tokensusage/last</c>.</summary>
    public TokenDashboardViewModel(KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _period = Periods[1]; // last hour
        _refreshTimer.Tick += (_, _) =>
        {
            if (!RefreshCommand.IsRunning)
                RefreshCommand.Execute(null);
        };
    }

    public IReadOnlyList<TokenPeriod> Periods { get; } =
    [
        new(15, "Last 15 minutes"), new(60, "Last hour"), new(240, "Last 4 hours"), new(1440, "Last 24 hours")
    ];

    [ObservableProperty] private TokenPeriod _period;
    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] private string _status = "Loading…";
    [ObservableProperty] private bool _isError;

    [ObservableProperty] private string _totalText = "–";
    [ObservableProperty] private string _totalDetails = string.Empty;
    [ObservableProperty] private string _promptText = "–";
    [ObservableProperty] private string _promptDetails = string.Empty;
    [ObservableProperty] private string _completionText = "–";
    [ObservableProperty] private string _completionDetails = string.Empty;
    [ObservableProperty] private string _callsText = "–";
    [ObservableProperty] private string _callsDetails = string.Empty;
    [ObservableProperty] private string _modelsText = "–";
    [ObservableProperty] private string _modelsDetails = string.Empty;
    [ObservableProperty] private string _rateText = "–";
    [ObservableProperty] private string _rateDetails = string.Empty;

    [ObservableProperty] private double _chartMax = 1;
    [ObservableProperty] private string _chartMaxLabel = string.Empty;
    [ObservableProperty] private string _chartStart = string.Empty;
    [ObservableProperty] private string _chartEnd = string.Empty;
    [ObservableProperty] private string _chartTitle = "Tokens over time";
    [ObservableProperty] private string _callsTitle = "LLM calls";

    /// <summary>All tokens per time slot.</summary>
    public ObservableCollection<double> TokensOverTime { get; } = [];

    /// <summary>Completion tokens per time slot.</summary>
    public ObservableCollection<double> CompletionOverTime { get; } = [];

    public ObservableCollection<TokenStatViewModel> Models { get; } = [];

    public ObservableCollection<TokenStatViewModel> Apps { get; } = [];

    public ObservableCollection<TokenCallViewModel> Calls { get; } = [];

    public bool HasCalls => Calls.Count > 0;

    partial void OnPeriodChanged(TokenPeriod value) => RefreshCommand.Execute(null);

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();
    }

    /// <summary>Reads the token usages of the period, then rebuilds the dashboard.</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsError = false;
        Status = $"Loading the token usages ({Period.Label.ToLowerInvariant()})…";
        try
        {
            _usages = await _assistant.GetTokensUsageAsync(Period.Minutes, cancellationToken);
            Apply();
            Status = $"{_usages.Count:N0} LLM call(s) loaded at {DateTime.Now:HH:mm:ss}" + (AutoRefresh ? " · auto-refresh every 30 s" : string.Empty);
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
            Status = $"Token usages not loaded: {ex.Message}";
            ActivityLog.Error("Tokens", "Token dashboard: GET /tokensusage/last failed", ex);
        }
    }

    private void Apply()
    {
        var usages = _usages.OrderByDescending(u => u.Date).ToList();
        long prompt = usages.Sum(u => (long)u.PromptTokens);
        long completion = usages.Sum(u => (long)u.CompletionTokens);
        var total = prompt + completion;

        TotalText = total.ToString("N0");
        TotalDetails = Period.Label.ToLowerInvariant();
        PromptText = prompt.ToString("N0");
        PromptDetails = total == 0 ? string.Empty : $"{prompt * 100.0 / total:0.#} % of the tokens";
        CompletionText = completion.ToString("N0");
        CompletionDetails = total == 0 ? string.Empty : $"{completion * 100.0 / total:0.#} % of the tokens";
        CallsText = usages.Count.ToString("N0");
        CallsDetails = usages.Count == 0 ? "no LLM call" : $"{total / usages.Count:N0} tokens per call on average";

        var byModel = Group(usages, u => u.Model ?? "?");
        ModelsText = byModel.Count.ToString("N0");
        ModelsDetails = byModel.Count == 0 ? string.Empty : $"most used: {byModel[0].Name}";
        RateText = $"{total / (double)Period.Minutes:N0}";
        RateDetails = "tokens per minute";

        BuildChart(usages);

        Models.Clear();
        foreach (var model in byModel)
            Models.Add(model);
        Apps.Clear();
        foreach (var app in Group(usages, u => u.AppName ?? "(none)").Take(10))
            Apps.Add(app);

        Calls.Clear();
        foreach (var usage in usages)
            Calls.Add(new TokenCallViewModel(usage));
        CallsTitle = $"LLM calls ({usages.Count:N0})";
        OnPropertyChanged(nameof(HasCalls));
    }

    /// <summary>Consumption grouped by <paramref name="key"/>, biggest first, with a bar relative to the biggest one.</summary>
    private static List<TokenStatViewModel> Group(List<TokenUsageEntry> usages, Func<TokenUsageEntry, string> key)
    {
        var groups = usages.GroupBy(key)
            .Select(g => (Name: g.Key, Calls: g.Count(), Prompt: g.Sum(u => (long)u.PromptTokens), Completion: g.Sum(u => (long)u.CompletionTokens)))
            .OrderByDescending(g => g.Prompt + g.Completion).ToList();
        var biggest = Math.Max(1, groups.Count == 0 ? 1 : groups[0].Prompt + groups[0].Completion);
        return groups.Select(g => new TokenStatViewModel(g.Name, g.Calls, g.Prompt, g.Completion, (g.Prompt + g.Completion) * 100.0 / biggest)).ToList();
    }

    /// <summary>Tokens per slot over the whole period (now on the right), all tokens and completion tokens.</summary>
    private void BuildChart(List<TokenUsageEntry> usages)
    {
        var end = DateTimeOffset.Now;
        var start = end.AddMinutes(-Period.Minutes);
        var slot = TimeSpan.FromTicks((end - start).Ticks / Buckets);
        var totals = new double[Buckets];
        var completions = new double[Buckets];
        foreach (var usage in usages)
        {
            var index = (int)Math.Clamp((usage.Date - start).Ticks / slot.Ticks, 0, Buckets - 1);
            totals[index] += usage.TotalTokens;
            completions[index] += usage.CompletionTokens;
        }

        TokensOverTime.Clear();
        CompletionOverTime.Clear();
        foreach (var value in totals)
            TokensOverTime.Add(value);
        foreach (var value in completions)
            CompletionOverTime.Add(value);
        ChartMax = Math.Max(1, totals.Max());
        ChartMaxLabel = $"max {totals.Max():N0} tokens / slot";
        ChartStart = start.ToString("dd/MM HH:mm");
        ChartEnd = end.ToString("dd/MM HH:mm");
        ChartTitle = $"Tokens over time · {Buckets} slots of {(slot.TotalMinutes >= 1 ? $"{slot.TotalMinutes:0.#} min" : $"{slot.TotalSeconds:0} s")}";
    }

    /// <inheritdoc />
    public string NoteWindowName => "Tokens dashboard";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        period = Period.Label,
        figures = new { tokens = TotalText, prompt = PromptText, completion = CompletionText, calls = CallsText, models = ModelsText, rate = RateText },
        byModel = Models.Select(m => new { m.Name, m.Calls, m.Prompt, m.Completion }).ToArray(),
        byApplication = Apps.Select(a => new { a.Name, a.Calls, a.Total }).ToArray()
    };

    /// <summary>Stops the auto-refresh.</summary>
    public void Dispose() => _refreshTimer.Stop();
}
