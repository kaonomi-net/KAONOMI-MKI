using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>The state of KAONOMI.LLMGateway (<c>GET /gateway/status</c>).</summary>
public sealed record GatewayStatus(
    int Port, string Upstream, bool UpstreamOk, long UptimeSeconds, long Requests, string Embedder, string EmbedderNote,
    string DataDirectory, GatewayCacheStats Cache, Dictionary<string, long> RuleHits, List<string> Models);

public sealed record GatewayCacheStats(int Entries, long Hits, long Misses, double HitRate, long SavedMs);

/// <summary>The settings of the cache of the gateway.</summary>
public sealed class GatewayCacheSettings
{
    public bool Enabled { get; set; } = true;
    public string EmbeddingModel { get; set; } = "";
    public double Threshold { get; set; } = 0.92;
    public double LexicalThreshold { get; set; } = 0.9;
    public double TtlHours { get; set; } = 72;
    public int MaxEntries { get; set; } = 5000;
    public bool EntityGuard { get; set; } = true;
    /// <summary>The answers of the model kept for a prompt before the cache answers it, one of them at random.</summary>
    public int MinAnswers { get; set; } = 3;
}

/// <summary>The settings of the routing of the gateway (its rules apart).</summary>
public sealed class GatewayRoutingSettings
{
    public bool Enabled { get; set; } = true;
    public bool OverrideRequested { get; set; } = true;
    public string DefaultModel { get; set; } = "";
}

public sealed record GatewaySettingsDocument(GatewayCacheSettings Cache, GatewayRoutingSettings Routing);

/// <summary>A rule of the router of the gateway.</summary>
public sealed class GatewayRule
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Any { get; set; } = [];
    public List<string> All { get; set; } = [];
    public List<string> None { get; set; } = [];
    public int MinCount { get; set; } = 1;
    public string Model { get; set; } = "";
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed record GatewayCacheItem(
    string Id, string Model, string Prompt, string Answer, string Embedder, string EntitySignature,
    DateTimeOffset Created, DateTimeOffset LastHit, int Hits, long AnswerMs);

public sealed record GatewayRequestRecord(
    DateTimeOffset Time, string Endpoint, string RequestedModel, string Model, string? Rule, string Reason,
    List<string> Entities, string Cache, double Score, string CacheReason, long Ms, int Status, string Prompt);

/// <summary>An answer of a phrase of the cache, and how many times it was given back.</summary>
public sealed record GatewayPhraseAnswer(string Text, int Hits, List<string> Ids);

/// <summary>A phrase of the cache of the gateway: a prompt, its model and context (* for every one), its answers.</summary>
public sealed record GatewayPhrase(
    string Prompt, string Model, string Context, string? System, List<GatewayPhraseAnswer> Answers, int Hits, DateTimeOffset LastHit);

/// <summary>
/// What the gateway did with a disliked answer: the answers taken out of its cache, the model that had answered, and
/// the model to ask again (null when the answer did not come from the default model, or no escalation model is set).
/// </summary>
public sealed record GatewayDislikeResult(int Removed, string? Model, string? EscalationModel, string? Reason);

/// <summary>What an import of phrases did.</summary>
public sealed record GatewayImportResult(int Phrases, int Added, int Skipped, List<string> Errors);

/// <summary>
/// The client of the API of KAONOMI.LLMGateway (<c>llmGatewayUrl</c> in config.yml, http://localhost:11435): its
/// state, its settings and rules, its cache, its log, and the analysis of a prompt. Every call throws
/// <see cref="HttpRequestException"/> when the gateway is not reached or refuses. The JSON goes through
/// <see cref="HttpJson"/>, not System.Net.Http.Json: the Godot chat loads another System.Text.Json beside the one of the
/// runtime, and the methods of System.Net.Http.Json built against it are then missing ("Method not found").
/// </summary>
public sealed class LlmGatewayClient(string baseUrl)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string BaseUrl { get; } = baseUrl.TrimEnd('/');

    public Task<GatewayStatus?> StatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync<GatewayStatus>("/gateway/status", cancellationToken);

    public Task<GatewaySettingsDocument?> SettingsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<GatewaySettingsDocument>("/gateway/settings", cancellationToken);

    public async Task SaveSettingsAsync(GatewayCacheSettings cache, GatewayRoutingSettings routing, CancellationToken cancellationToken = default)
    {
        using var response = await Http.PutJsonAsync(BaseUrl + "/gateway/settings", new { cache, routing }, cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }

    public async Task<List<GatewayRule>> RulesAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<GatewayRule>>("/gateway/rules", cancellationToken) ?? [];

    public async Task SaveRulesAsync(IEnumerable<GatewayRule> rules, CancellationToken cancellationToken = default)
    {
        using var response = await Http.PutJsonAsync(BaseUrl + "/gateway/rules", rules, cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }

    public async Task<List<GatewayCacheItem>> CacheAsync(int take, string? search = null, CancellationToken cancellationToken = default) =>
        await GetAsync<List<GatewayCacheItem>>($"/gateway/cache?take={take}&search={Uri.EscapeDataString(search ?? "")}", cancellationToken) ?? [];

    public async Task<int> ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.DeleteAsync(BaseUrl + "/gateway/cache", cancellationToken);
        await EnsureAsync(response, cancellationToken);
        var body = await response.Content.ReadJsonAsync<JsonObject>(cancellationToken);
        return body?["removed"]?.GetValue<int>() ?? 0;
    }

    public async Task RemoveCacheEntryAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await Http.DeleteAsync($"{BaseUrl}/gateway/cache/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }

    /// <summary>The phrases of the cache, each with its answers (<c>GET /gateway/cache/phrases</c>).</summary>
    public async Task<List<GatewayPhrase>> PhrasesAsync(string? search, int take, CancellationToken cancellationToken = default) =>
        await GetAsync<List<GatewayPhrase>>($"/gateway/cache/phrases?take={take}&search={Uri.EscapeDataString(search ?? "")}", cancellationToken) ?? [];

    /// <summary>The JSON file of the phrases of the cache and their answers (all of them, or the ones of <paramref name="search"/>).</summary>
    public async Task<string> ExportAsync(string? search, CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync($"{BaseUrl}/gateway/cache/export?search={Uri.EscapeDataString(search ?? "")}", cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Adds the phrases of a JSON file (an export, or phrases written by hand) to the cache.</summary>
    public async Task<GatewayImportResult?> ImportAsync(string json, CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(BaseUrl + "/gateway/cache/import", content, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<GatewayImportResult>(cancellationToken);
    }

    /// <summary>
    /// Tells the gateway that <paramref name="answer"/> to <paramref name="prompt"/> (as it was sent) was disliked
    /// (<c>POST /gateway/feedback/dislike</c>): the interaction leaves its cache.
    /// </summary>
    public async Task<GatewayDislikeResult?> DislikeAsync(string prompt, string answer, string? model, CancellationToken cancellationToken = default)
    {
        using var response = await Http.PostJsonAsync(BaseUrl + "/gateway/feedback/dislike", new { prompt, answer, model }, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<GatewayDislikeResult>(cancellationToken);
    }

    public async Task<List<GatewayRequestRecord>> LogAsync(int take, CancellationToken cancellationToken = default) =>
        await GetAsync<List<GatewayRequestRecord>>($"/gateway/log?take={take}", cancellationToken) ?? [];

    public async Task<List<string>> EntityTypesAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<string>>("/gateway/entity-types", cancellationToken) ?? [];

    /// <summary>What the gateway would do with a prompt (the JSON of <c>POST /gateway/analyze</c>).</summary>
    public async Task<JsonObject?> AnalyzeAsync(string prompt, string? model, CancellationToken cancellationToken = default)
    {
        using var response = await Http.PostJsonAsync(BaseUrl + "/gateway/analyze", new { prompt, model }, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<JsonObject>(cancellationToken);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(BaseUrl + path, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<T>(cancellationToken);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        string? error = null;
        try
        {
            error = JsonNode.Parse(text)?["error"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }
        throw new HttpRequestException(error ?? $"HTTP {(int)response.StatusCode}", null, response.StatusCode);
    }
}
