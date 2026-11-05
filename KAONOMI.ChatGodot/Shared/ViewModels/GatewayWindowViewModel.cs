using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A rule of the router, as edited in /gateway (its entities as text, separated by commas).</summary>
public sealed partial class GatewayRuleViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _any = "";
    [ObservableProperty] private string _all = "";
    [ObservableProperty] private string _none = "";
    [ObservableProperty] private double _minCount = 1;
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private double _priority;
    [ObservableProperty] private bool _enabled = true;
    /// <summary>How many requests it routed since the gateway started.</summary>
    [ObservableProperty] private string _hits = "";

    public static GatewayRuleViewModel From(GatewayRule rule) => new()
    {
        Name = rule.Name, Description = rule.Description ?? "", Any = string.Join(", ", rule.Any), All = string.Join(", ", rule.All),
        None = string.Join(", ", rule.None), MinCount = rule.MinCount, Model = rule.Model, Priority = rule.Priority, Enabled = rule.Enabled
    };

    public GatewayRule ToRule() => new()
    {
        Name = Name.Trim(), Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(), Any = Split(Any), All = Split(All),
        None = Split(None), MinCount = Math.Max(1, (int)MinCount), Model = Model.Trim(), Priority = (int)Priority, Enabled = Enabled
    };

    private static List<string> Split(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant() is var upper && s.Contains(':')
            ? upper[..upper.IndexOf(':')] + s[s.IndexOf(':')..] : upper).ToList();
}

/// <summary>A line of the requests seen by the gateway.</summary>
public sealed record GatewayLogLine(string Time, string Route, string Cache, bool Hit, string Entities, string Prompt, string Tooltip);

/// <summary>A phrase of the cache in the search popup: the prompt, where it holds, its answers.</summary>
public sealed record GatewayPhraseLine(string Prompt, string Scope, IReadOnlyList<string> Answers, string Details);

/// <summary>An answer kept by the cache of the gateway.</summary>
public sealed record GatewayCacheLine(string Id, string Prompt, string Answer, string Details);

/// <summary>
/// /gateway: drives KAONOMI.LLMGateway, the gateway in front of Ollama. Its state (Ollama reached, the embedder of the
/// cache, hits and misses, the time saved) read every 3 seconds with the last requests and the cached answers; the
/// settings of the cache (on, entity guard, embedding model, thresholds, TTL, size, the answers kept for a prompt
/// before the cache answers it with one of them at random) and of the routing (on, the rules
/// replace the model asked, the default model of auto) with SAVE SETTINGS; the rules of the router (entities any / all
/// / none, model, priority) with ADD RULE and SAVE RULES; CLEAR CACHE; and ANALYZE, which shows what the gateway would do
/// with a prompt (its entities, the model chosen and why, the closest cached prompt) without asking a model.
/// </summary>
public sealed partial class GatewayWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(3);
    private readonly LlmGatewayClient _client;
    private readonly Timer _timer;
    /// <summary>The thread of the interface: the lists are changed there only (the view reads them there).</summary>
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private int _refreshing;
    private long _seenRequests = -1;
    private bool _loaded;
    private bool _disposed;

    /// <summary>For the designer.</summary>
    public GatewayWindowViewModel() : this("http://localhost:11435")
    {
    }

    public GatewayWindowViewModel(string url)
    {
        _client = new LlmGatewayClient(url);
        Address = _client.BaseUrl;
        _timer = new Timer(_ =>
        {
            if (_ui is null)
                _ = PollAsync();
            else
                _ui.Post(_ => _ = PollAsync(), null);
        }, null, TimeSpan.Zero, RefreshEvery);
    }

    public string Address { get; }

    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<GatewayRuleViewModel> Rules { get; } = [];
    public ObservableCollection<GatewayLogLine> Log { get; } = [];
    public ObservableCollection<GatewayCacheLine> CacheEntries { get; } = [];
    public ObservableCollection<GatewayPhraseLine> Phrases { get; } = [];

    /// <summary>The words searched in the popup of the phrases (empty: all of them).</summary>
    [ObservableProperty] private string _phraseSearch = "";
    [ObservableProperty] private string _phrasesSummary = "";

    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _summary = "connecting…";
    [ObservableProperty] private string _cacheSummary = "";
    [ObservableProperty] private string _embedderNote = "";
    [ObservableProperty] private string _entityTypes = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;

    // The cache.
    [ObservableProperty] private bool _cacheEnabled = true;
    [ObservableProperty] private bool _entityGuard = true;
    [ObservableProperty] private string _embeddingModel = "";
    [ObservableProperty] private double _threshold = 0.92;
    [ObservableProperty] private double _lexicalThreshold = 0.9;
    [ObservableProperty] private double _ttlHours = 72;
    [ObservableProperty] private double _maxEntries = 5000;
    /// <summary>The answers kept for a prompt before the cache answers it (one of them at random, so that they vary).</summary>
    [ObservableProperty] private double _minAnswers = 3;

    // The routing.
    [ObservableProperty] private bool _routingEnabled = true;
    [ObservableProperty] private bool _overrideRequested = true;
    [ObservableProperty] private string _defaultModel = "";

    // The analysis of a prompt.
    [ObservableProperty] private string _analyzePrompt = "";
    [ObservableProperty] private string _analyzeModel = "auto";
    [ObservableProperty] private string _analysis = "";

    [ObservableProperty] private string _cacheSearch = "";

    /// <summary>Reads everything again, the settings and the rules too (the changes not saved are lost).</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        _loaded = false;
        _seenRequests = -1;
        await PollAsync();
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        await RunAsync("Settings saved", async () =>
        {
            await _client.SaveSettingsAsync(
                new GatewayCacheSettings
                {
                    Enabled = CacheEnabled, EntityGuard = EntityGuard, EmbeddingModel = EmbeddingModel.Trim(), Threshold = Threshold,
                    LexicalThreshold = LexicalThreshold, TtlHours = TtlHours, MaxEntries = (int)MaxEntries, MinAnswers = Math.Max(1, (int)MinAnswers)
                },
                new GatewayRoutingSettings { Enabled = RoutingEnabled, OverrideRequested = OverrideRequested, DefaultModel = DefaultModel.Trim() });
            ActivityLog.Info("Gateway", $"Settings saved: cache {(CacheEnabled ? "on" : "off")}, routing {(RoutingEnabled ? "on" : "off")}");
        });
        await PollAsync();
    }

    [RelayCommand]
    private void AddRule()
    {
        var name = "rule";
        for (var n = 2; Rules.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"rule{n}";
        Rules.Insert(0, new GatewayRuleViewModel { Name = name, Any = "CODE", Model = DefaultModel, Priority = 50 });
        Status = $"Rule {name} added: set its entities and its model, then SAVE RULES.";
        IsError = false;
    }

    [RelayCommand]
    private void RemoveRule(GatewayRuleViewModel? rule)
    {
        if (rule is null)
            return;
        Rules.Remove(rule);
        Status = $"Rule {rule.Name} removed: SAVE RULES to keep it so.";
        IsError = false;
    }

    [RelayCommand]
    private async Task SaveRulesAsync()
    {
        await RunAsync("Rules saved", async () =>
        {
            await _client.SaveRulesAsync(Rules.Select(r => r.ToRule()).ToList());
            ActivityLog.Info("Gateway", $"Rules saved: {Rules.Count}");
            var rules = await _client.RulesAsync();
            Replace(Rules, rules.Select(GatewayRuleViewModel.From));
        });
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        await RunAsync("", async () =>
        {
            var removed = await _client.ClearCacheAsync();
            Status = $"Cache cleared: {removed} answer(s) removed";
            ActivityLog.Info("Gateway", Status);
        });
        await PollAsync();
    }

    [RelayCommand]
    private async Task RemoveEntryAsync(GatewayCacheLine? entry)
    {
        if (entry is null)
            return;
        await RunAsync("Answer removed from the cache", () => _client.RemoveCacheEntryAsync(entry.Id));
        CacheEntries.Remove(entry);
    }

    [RelayCommand]
    private async Task SearchCacheAsync() => await RunAsync("", async () => await LoadCacheAsync(CancellationToken.None));

    /// <summary>The phrases of the cache matching <see cref="PhraseSearch"/>, with their answers (the popup of the phrases).</summary>
    [RelayCommand]
    private async Task SearchPhrasesAsync()
    {
        await RunAsync("", async () =>
        {
            var phrases = await _client.PhrasesAsync(PhraseSearch, 500);
            Replace(Phrases, phrases.Select(PhraseLine));
            var answers = phrases.Sum(p => p.Answers.Count);
            PhrasesSummary = $"{phrases.Count} phrase(s), {answers} answer(s)" + (string.IsNullOrWhiteSpace(PhraseSearch) ? "" : $" for \"{PhraseSearch.Trim()}\"");
        });
    }

    /// <summary>
    /// Writes the phrases of the cache and their answers to <paramref name="path"/> (JSON): all of them, or the ones
    /// of the search of the popup when <paramref name="searched"/>.
    /// </summary>
    public async Task ExportPhrasesAsync(string path, bool searched)
    {
        await RunAsync("", async () =>
        {
            var json = await _client.ExportAsync(searched ? PhraseSearch : null);
            var count = JsonNode.Parse(json)?["phrases"]?.AsArray().Count ?? 0;
            await System.IO.File.WriteAllTextAsync(path, PrettyJson(json), new UTF8Encoding(false));
            Status = $"{count} phrase(s) exported to {path}";
            ActivityLog.Info("Gateway", Status);
        });
    }

    /// <summary>Adds the phrases of the JSON file <paramref name="path"/> (an export, or phrases written by hand) to the cache.</summary>
    public async Task ImportPhrasesAsync(string path)
    {
        await RunAsync("", async () =>
        {
            var json = await System.IO.File.ReadAllTextAsync(path);
            var result = await _client.ImportAsync(json);
            if (result is null)
                return;
            Status = $"Imported from {System.IO.Path.GetFileName(path)}: {result.Phrases} phrase(s), {result.Added} answer(s) added, {result.Skipped} already kept"
                     + (result.Errors.Count > 0 ? $" · {result.Errors.Count} problem(s): {string.Join("; ", result.Errors.Take(3))}" : "");
            IsError = result.Errors.Count > 0 && result.Added == 0;
            ActivityLog.Info("Gateway", Status);
        });
        _seenRequests = -1;
        await PollAsync();
        if (Phrases.Count > 0 || PhrasesSummary.Length > 0)
            await SearchPhrasesAsync();
    }

    private static GatewayPhraseLine PhraseLine(GatewayPhrase p)
    {
        var model = p.Model == "*" ? "every model" : p.Model;
        var context = p.Context == "*" ? "every context"
            : (p.System is { Length: > 0 } system ? $"after: {Excerpt(system, 80)}" : "no system prompt")
              + $" · context #{p.Context[..Math.Min(6, p.Context.Length)].ToLowerInvariant()}";
        return new GatewayPhraseLine(p.Prompt, $"{model} · {context}",
            p.Answers.Select(a => a.Hits > 0 ? $"{a.Text.Trim()}   ({a.Hits}×)" : a.Text.Trim()).ToList(),
            $"{p.Answers.Count} answer(s) · given {p.Hits} time(s) · last {Age(p.LastHit)} ago");
    }

    private static string PrettyJson(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }) ?? json;
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (string.IsNullOrWhiteSpace(AnalyzePrompt))
        {
            Status = "Type a prompt to analyze.";
            IsError = true;
            return;
        }
        await RunAsync("Analyzed (no model was asked)", async () =>
        {
            var result = await _client.AnalyzeAsync(AnalyzePrompt, string.IsNullOrWhiteSpace(AnalyzeModel) ? "auto" : AnalyzeModel.Trim());
            Analysis = Describe(result);
        });
    }

    /// <summary>The analysis as Markdown: the model chosen and why, the cache, the entities.</summary>
    private static string Describe(JsonObject? result)
    {
        if (result is null)
            return "";
        var text = new StringBuilder();
        var route = result["route"];
        var asked = route?["requestedModel"]?.GetValue<string>();
        var model = route?["model"]?.GetValue<string>();
        text.AppendLine($"**Model:** {model}" + (asked != model ? $" (asked: {asked})" : ""));
        text.AppendLine($"**Why:** {route?["reason"]?.GetValue<string>()}");
        var cache = result["cache"];
        var hit = cache?["hit"]?.GetValue<bool>() ?? false;
        text.AppendLine($"**Cache:** {(hit ? "HIT" : "miss")}, {cache?["reason"]?.GetValue<string>()} ({cache?["embedder"]?.GetValue<string>()})");
        text.AppendLine($"**Answers kept for this prompt:** {cache?["answers"]?.GetValue<int>()} / {cache?["minAnswers"]?.GetValue<int>()} before the cache answers");
        if (cache?["closest"] is JsonObject closest)
            text.AppendLine($"**Closest prompt:** {Excerpt(closest["prompt"]?.GetValue<string>(), 160)}");
        var entities = (result["entities"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(e => $"`{e["type"]?.GetValue<string>()}:{e["value"]?.GetValue<string>()}`").Distinct().ToList();
        text.AppendLine($"**Entities ({entities.Count}):** {(entities.Count == 0 ? "none" : string.Join(" ", entities))}");
        return text.ToString();
    }

    /// <summary>Every 3 seconds: the state, the requests and the cache; the settings and the rules once (and after REFRESH).</summary>
    private async Task PollAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) == 1)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var status = await _client.StatusAsync(timeout.Token);
            if (status is null)
                return;
            IsOnline = true;
            var uptime = TimeSpan.FromSeconds(status.UptimeSeconds);
            Summary = $"{Address} → {status.Upstream} {(status.UpstreamOk ? "✓" : "✗ (Ollama not reached)")} · {status.Requests} request(s) · up {(int)uptime.TotalHours}h{uptime.Minutes:00}";
            var cache = status.Cache;
            CacheSummary = $"{cache.Entries} answer(s) · {cache.Hits} hit(s), {cache.Misses} miss(es), {cache.HitRate:P0} · {TimeSpan.FromMilliseconds(cache.SavedMs).TotalSeconds:0} s saved";
            EmbedderNote = $"Embeddings: {status.EmbedderNote}";
            if (!SameItems(Models, status.Models))
                Replace(Models, status.Models.Prepend("auto"));
            if (!_loaded)
            {
                await LoadSettingsAsync(timeout.Token);
                _loaded = true;
            }
            foreach (var rule in Rules)
                rule.Hits = status.RuleHits.TryGetValue(rule.Name, out var hits) ? $"{hits} routed" : "";
            if (status.Requests != _seenRequests)
            {
                _seenRequests = status.Requests;
                var log = await _client.LogAsync(60, timeout.Token);
                Replace(Log, log.Select(Line));
                await LoadCacheAsync(timeout.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            IsOnline = false;
            Summary = $"{Address}: not reached. Start KAONOMI.LLMGateway from /process (or: cd KAONOMI.LLMGateway && dotnet run).";
            _loaded = false;
            _seenRequests = -1;
        }
        catch (Exception ex)
        {
            IsOnline = false;
            Summary = $"{Address}: the answer could not be read ({ex.GetType().Name}: {ex.Message})";
            ActivityLog.Error("Gateway", "State of the gateway not read", ex);
            _loaded = false;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _client.SettingsAsync(cancellationToken);
        if (settings is not null)
        {
            CacheEnabled = settings.Cache.Enabled;
            EntityGuard = settings.Cache.EntityGuard;
            EmbeddingModel = settings.Cache.EmbeddingModel;
            Threshold = settings.Cache.Threshold;
            LexicalThreshold = settings.Cache.LexicalThreshold;
            TtlHours = settings.Cache.TtlHours;
            MaxEntries = settings.Cache.MaxEntries;
            MinAnswers = settings.Cache.MinAnswers;
            RoutingEnabled = settings.Routing.Enabled;
            OverrideRequested = settings.Routing.OverrideRequested;
            DefaultModel = settings.Routing.DefaultModel;
        }
        Replace(Rules, (await _client.RulesAsync(cancellationToken)).Select(GatewayRuleViewModel.From));
        EntityTypes = "Entities: " + string.Join(", ", await _client.EntityTypesAsync(cancellationToken))
                      + " · TYPE:value selects a value (LANGUAGE:fr, CODE:python)";
    }

    private async Task LoadCacheAsync(CancellationToken cancellationToken)
    {
        var entries = await _client.CacheAsync(80, CacheSearch, cancellationToken);
        Replace(CacheEntries, entries.Select(e => new GatewayCacheLine(e.Id, Excerpt(e.Prompt, 140), Excerpt(e.Answer, 220),
            $"{e.Model} · {e.Hits} hit(s) · kept {Age(e.Created)} ago · answered in {e.AnswerMs / 1000.0:0.0} s · {e.Embedder}")));
    }

    private static GatewayLogLine Line(GatewayRequestRecord r)
    {
        var route = r.RequestedModel == r.Model ? r.Model : $"{r.RequestedModel} → {r.Model}";
        if (r.Rule is not null)
            route += $" ({r.Rule})";
        var cache = r.Cache switch
        {
            "HIT" => $"HIT {r.Score:0.00}",
            "MISS" => r.Score > 0 ? $"MISS {r.Score:0.00}" : "MISS",
            var other => other
        };
        return new GatewayLogLine(r.Time.ToLocalTime().ToString("HH:mm:ss"), route, cache, r.Cache == "HIT",
            string.Join(" ", r.Entities.Take(8)), r.Prompt,
            $"{r.Endpoint} · HTTP {r.Status} · {r.Ms} ms\n{r.Reason}\ncache: {r.CacheReason}\n{string.Join(" ", r.Entities)}");
    }

    private async Task RunAsync(string done, Func<Task> action)
    {
        IsBusy = true;
        IsError = false;
        try
        {
            await action();
            if (done.Length > 0)
                Status = done;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            IsError = true;
            Status = $"File not read or written: {ex.Message}";
            ActivityLog.Error("Gateway", "File of the phrases", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            IsError = true;
            Status = $"The gateway refused or was not reached: {ex.Message}";
            ActivityLog.Error("Gateway", "Call to the gateway failed", ex);
        }
        catch (Exception ex)
        {
            // Anything else is shown too, rather than lost in the log of Godot.
            IsError = true;
            Status = $"Failed: {ex.GetType().Name}: {ex.Message}";
            ActivityLog.Error("Gateway", "Action of /gateway failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Excerpt(string? text, int length)
    {
        var line = (text ?? "").ReplaceLineEndings(" ").Trim();
        return line.Length > length ? line[..length] + "…" : line;
    }

    private static string Age(DateTimeOffset since)
    {
        var age = DateTimeOffset.UtcNow - since;
        return age.TotalMinutes < 1 ? "<1 min" : age.TotalHours < 1 ? $"{age.TotalMinutes:0} min" : age.TotalDays < 1 ? $"{age.TotalHours:0} h" : $"{age.TotalDays:0} d";
    }

    private static bool SameItems(IEnumerable<string> shown, IEnumerable<string> models) =>
        shown.Skip(1).SequenceEqual(models);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
