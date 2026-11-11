using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Client for the local LLM server: model list and structured chat. With the provider "ollama" (the default) it speaks
/// the API of Ollama (<c>GET /api/tags</c>, <c>POST /api/chat</c>); with "lmstudio" (or "openai") the OpenAI-compatible
/// API of LM Studio (<c>GET /v1/models</c>, <c>POST /v1/chat/completions</c>): the requests are written once, in the
/// form of Ollama, and translated for LM Studio.
/// </summary>
public sealed class OllamaModelService
{
    /// <summary>Time allowed to list the models.</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;

    private readonly bool _openAi;
    private readonly string _openAiPrefix = "";

    /// <summary>True when <paramref name="provider"/> names a server of the OpenAI-compatible API ("lmstudio", "openai").</summary>
    public static bool IsOpenAiCompatible(string? provider)
    {
        var name = (provider ?? "").Trim().Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
        return name is "lmstudio" or "openai" or "openaicompatible";
    }

    /// <summary>The name shown for <paramref name="provider"/>: "LM Studio" or "Ollama".</summary>
    public static string DisplayName(string? provider) => IsOpenAiCompatible(provider) ? "LM Studio" : "Ollama";

    /// <summary>
    /// Creates a client for the LLM server at the given base URL; <paramref name="provider"/> is <c>llmProvider</c> of
    /// the settings ("ollama" when null, or "lmstudio").
    /// </summary>
    public OllamaModelService(string baseUrl, string? provider = null)
    {
        _openAi = IsOpenAiCompatible(provider);
        // http://localhost:1234 and http://localhost:1234/v1 are both the address of LM Studio.
        if (_openAi && !baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            _openAiPrefix = "v1/";
        var baseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        // Sent from the thread pool, without proxy lookup for a local Ollama: never a frozen window (Windows).
        _httpClient = new HttpClient(HttpHandlers.Create(baseAddress))
        {
            BaseAddress = baseAddress,
            // Generation can take minutes on a local model: timeouts are set per call.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>Normalized base URI used for the requests.</summary>
    public Uri BaseAddress => _httpClient.BaseAddress!;

    /// <summary>The name of the server for the messages: "LM Studio" or "Ollama".</summary>
    public string ProviderName => _openAi ? "LM Studio" : "Ollama";

    /// <summary>True when the server is LM Studio (OpenAI-compatible API), false for Ollama.</summary>
    public bool IsOpenAi => _openAi;

    /// <summary>Returns sorted model names from <c>GET /api/tags</c> (Ollama) or <c>GET /v1/models</c> (LM Studio).</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ListTimeout);
        if (_openAi)
        {
            var list = await _httpClient.GetJsonAsync<JsonObject>(_openAiPrefix + "models", timeout.Token);
            // LM Studio lists its embedding models too ("text-embedding-…"): they are not chat models.
            return (list?["data"] as JsonArray ?? [])
                .Select(m => m?["id"] is JsonValue id && id.TryGetValue<string>(out var name) ? name : "")
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Where(n => !n.StartsWith("text-embedding-", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();
        }
        var tags = await _httpClient.GetJsonAsync<OllamaTagsResponse>("api/tags", timeout.Token);
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

        return await SendChatAsync(request, cancellationToken)
               ?? throw new HttpRequestException($"{ProviderName} returned an empty answer.");
    }

    /// <summary>Non-streamed chat with a free-text (Markdown) answer; returns the text of the answer.</summary>
    public async Task<string> ChatAsync(
        string model,
        string? systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = userPrompt });
        var request = new JsonObject { ["model"] = model, ["stream"] = false, ["messages"] = messages };

        return await SendChatAsync(request, cancellationToken) is { Length: > 0 } content
            ? content
            : throw new HttpRequestException($"{ProviderName} returned an empty answer.");
    }

    /// <summary>
    /// Non-streamed chat with a vision model: <paramref name="userPrompt"/> with the picture <paramref name="image"/>
    /// (PNG or JPEG bytes); returns the text of the answer.
    /// </summary>
    public Task<string> DescribeImageAsync(string model, string? systemPrompt, string userPrompt, byte[] image,
        CancellationToken cancellationToken = default) =>
        ChatWithImagesAsync(model, systemPrompt, userPrompt, [image], cancellationToken);

    /// <summary>
    /// One answer of a vision model (<c>POST /api/chat</c>) to <paramref name="userPrompt"/> with <paramref name="images"/>
    /// (PNG or JPEG, sent in base 64; none is a text question).
    /// </summary>
    public async Task<string> ChatWithImagesAsync(string model, string? systemPrompt, string userPrompt,
        System.Collections.Generic.IReadOnlyList<byte[]> images, CancellationToken cancellationToken = default)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = userPrompt,
            ["images"] = new JsonArray(images.Select(i => (JsonNode?)JsonValue.Create(Convert.ToBase64String(i))).ToArray())
        });
        var request = new JsonObject { ["model"] = model, ["stream"] = false, ["messages"] = messages };

        return await SendChatAsync(request, cancellationToken) is { Length: > 0 } content
            ? content
            : throw new HttpRequestException($"{ProviderName} returned an empty answer.");
    }

    /// <summary>
    /// One answer of a vision model to <paramref name="userPrompt"/> with <paramref name="images"/>, held to
    /// <paramref name="jsonSchema"/> (structured output); <paramref name="context"/> tokens of context (each picture
    /// takes about 1 200 of them), <paramref name="maxTokens"/> at most in the answer. Returns the raw JSON text.
    /// </summary>
    public async Task<string> ChatJsonWithImagesAsync(string model, string systemPrompt, string userPrompt,
        System.Collections.Generic.IReadOnlyList<byte[]> images, JsonNode jsonSchema, int context = 16384, CancellationToken cancellationToken = default,
        double temperature = 0.1, int maxTokens = 6000, double repeatPenalty = 1.05)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            ["format"] = jsonSchema.DeepClone(),
            // A cap on the answer and a light penalty on repeats: a page full of similar lines can make the model loop.
            ["options"] = new JsonObject { ["temperature"] = temperature, ["num_ctx"] = context, ["num_predict"] = maxTokens, ["repeat_penalty"] = repeatPenalty },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = userPrompt,
                    ["images"] = new JsonArray(images.Select(i => (JsonNode?)JsonValue.Create(Convert.ToBase64String(i))).ToArray())
                }
            }
        };

        return await SendChatAsync(request, cancellationToken) is { Length: > 0 } content
            ? content
            : throw new HttpRequestException($"{ProviderName} returned an empty answer.");
    }

    /// <summary>
    /// Sends <paramref name="request"/> (a non-streamed <c>/api/chat</c> body of Ollama) to the server, translated for
    /// LM Studio when it is the provider, and returns the text of the answer (null when there is none).
    /// </summary>
    private async Task<string?> SendChatAsync(JsonObject request, CancellationToken cancellationToken)
    {
        using var response = _openAi
            ? await _httpClient.PostJsonAsync(_openAiPrefix + "chat/completions", ToOpenAi(request), cancellationToken)
            : await _httpClient.PostJsonAsync("api/chat", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"{ProviderName} responded {(int)response.StatusCode} ({response.ReasonPhrase}): {body}", null, response.StatusCode);
        }

        if (_openAi)
        {
            var answer = await response.Content.ReadJsonAsync<JsonObject>(cancellationToken);
            return (answer?["choices"] as JsonArray)?.FirstOrDefault()?["message"]?["content"] is JsonValue value
                   && value.TryGetValue<string>(out var text) ? text : null;
        }
        var chat = await response.Content.ReadJsonAsync<OllamaChatAnswer>(cancellationToken);
        return chat?.Message?.Content;
    }

    /// <summary>
    /// The body of <c>POST /v1/chat/completions</c> for a body of <c>/api/chat</c>: the pictures become
    /// <c>image_url</c> parts (data URLs), the schema of <c>format</c> a <c>response_format</c> of type
    /// <c>json_schema</c>, and the options their OpenAI names (<c>num_predict</c> is <c>max_tokens</c>; the context
    /// length <c>num_ctx</c> is set on the model in LM Studio, not in a request).
    /// </summary>
    public static JsonObject ToOpenAi(JsonObject request)
    {
        var messages = new JsonArray();
        foreach (var node in request["messages"] as JsonArray ?? [])
        {
            if (node is not JsonObject message)
                continue;
            var converted = new JsonObject { ["role"] = message["role"]?.DeepClone() };
            if (message["images"] is JsonArray { Count: > 0 } images)
            {
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = message["content"]?.DeepClone() } };
                foreach (var image in images)
                {
                    var base64 = image is JsonValue encoded && encoded.TryGetValue<string>(out var data) ? data : "";
                    // JPEG starts with FF D8 FF ("/9j/" in base 64); anything else is sent as PNG.
                    var type = base64.StartsWith("/9j/", StringComparison.Ordinal) ? "image/jpeg" : "image/png";
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{type};base64,{base64}" }
                    });
                }
                converted["content"] = parts;
            }
            else
                converted["content"] = message["content"]?.DeepClone();
            messages.Add(converted);
        }

        var body = new JsonObject { ["model"] = request["model"]?.DeepClone(), ["stream"] = false, ["messages"] = messages };
        if (request["format"] is JsonObject schema)
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "answer", ["strict"] = true, ["schema"] = schema.DeepClone() }
            };
        if (request["options"] is JsonObject options)
        {
            if (options["temperature"] is { } temperature)
                body["temperature"] = temperature.DeepClone();
            if (options["num_predict"] is { } maxTokens)
                body["max_tokens"] = maxTokens.DeepClone();
            if (options["repeat_penalty"] is { } repeatPenalty)
                body["repeat_penalty"] = repeatPenalty.DeepClone();
        }
        return body;
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
