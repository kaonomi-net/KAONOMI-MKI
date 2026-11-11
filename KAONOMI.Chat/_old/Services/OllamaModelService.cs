using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Client for the local Ollama instance: model list (<c>GET /api/tags</c>) and structured chat (<c>POST /api/chat</c>).
/// </summary>
public sealed class OllamaModelService
{
    /// <summary>Time allowed to list the models.</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;

    /// <summary>Creates a client for the Ollama instance at the given base URL.</summary>
    public OllamaModelService(string baseUrl)
    {
        var baseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        // Sent from the thread pool, without proxy lookup for a local Ollama: never a frozen window (Windows).
        _httpClient = new HttpClient(HttpHandlers.Create(baseAddress))
        {
            BaseAddress = baseAddress,
            // Generation can take minutes on a local model: timeouts are set per call.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>Normalized base URI used for Ollama requests.</summary>
    public Uri BaseAddress => _httpClient.BaseAddress!;

    /// <summary>Returns sorted model names from <c>GET /api/tags</c>.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ListTimeout);
        var tags = await _httpClient.GetFromJsonAsync<OllamaTagsResponse>("api/tags", timeout.Token);
        // Recent Ollama versions can list the same model twice (e.g. "gpt-oss:20b" from two model stores): keep one.
        // The internal "llamacpp:<hash>" entries are not chat models and are hidden.
        return tags?.Models.Select(m => m.Name)
                   .Where(n => !string.IsNullOrWhiteSpace(n))
                   .Where(n => !n.StartsWith("llamacpp:", StringComparison.OrdinalIgnoreCase))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(n => n)
                   .ToList() ?? [];
    }

    /// <summary>
    /// Non-streamed chat whose answer is constrained to <paramref name="jsonSchema"/> (Ollama structured outputs);
    /// returns the raw JSON text of the answer.
    /// </summary>
    public async Task<string> ChatJsonAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        JsonNode jsonSchema,
        CancellationToken cancellationToken = default)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["format"] = jsonSchema.DeepClone(),
            ["options"] = new JsonObject { ["temperature"] = 0.4 },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("api/chat", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Ollama responded {(int)response.StatusCode} ({response.ReasonPhrase}): {body}", null, response.StatusCode);
        }

        var chat = await response.Content.ReadFromJsonAsync<OllamaChatAnswer>(cancellationToken);
        return chat?.Message?.Content
               ?? throw new HttpRequestException("Ollama returned an empty answer.");
    }

    private sealed class OllamaChatAnswer
    {
        [JsonPropertyName("message")]
        public OllamaChatMessage? Message { get; set; }
    }

    private sealed class OllamaChatMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
