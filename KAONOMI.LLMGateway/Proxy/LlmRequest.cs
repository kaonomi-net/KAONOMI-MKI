using System.Text.Json.Nodes;

namespace KAONOMI.LLMGateway.Proxy;

/// <summary>The three APIs of a model the gateway understands (the others go through unchanged).</summary>
public enum LlmApi
{
    /// <summary>Ollama <c>POST /api/chat</c>.</summary>
    OllamaChat,
    /// <summary>Ollama <c>POST /api/generate</c>.</summary>
    OllamaGenerate,
    /// <summary>OpenAI <c>POST /v1/chat/completions</c> (served by Ollama too).</summary>
    OpenAiChat
}

/// <summary>A message of a request, as the router and the cache see it.</summary>
public sealed record LlmMessage(string Role, string Content, bool HasImages);

/// <summary>
/// A request to a model read once: its body (changed in place by the router), its messages, the last prompt of the
/// user, whether it streams, and whether its answer may come from the cache (and why not).
/// </summary>
public sealed class LlmRequest
{
    public required LlmApi Api { get; init; }
    public required JsonObject Body { get; init; }
    public required IReadOnlyList<LlmMessage> Messages { get; init; }
    public required bool Stream { get; init; }
    public string? Model => Json.Str(Body["model"]);
    public string LastPrompt => Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
    public bool HasImages => Messages.Any(m => m.HasImages);

    /// <summary>Null when the cache may answer; else why not.</summary>
    public string? NotCacheable { get; set; }

    /// <summary>The system prompt of the request (its first system message), null when it has none.</summary>
    public string? SystemPrompt => Messages.FirstOrDefault(m => m.Role == "system")?.Content;

    /// <summary>
    /// The parts of the context of a request made of <paramref name="system"/> (when not empty) and a prompt, without
    /// format nor thinking: the context of a phrase imported with its system prompt.
    /// </summary>
    public static string[] ContextPartsFor(string? system) =>
        string.IsNullOrEmpty(system) ? ["format:", "think:"] : ["system:" + system, "format:", "think:"];

    /// <summary>What comes before the last prompt: the system prompt, the history, the format and the thinking asked.</summary>
    public string[] ContextParts()
    {
        var last = Messages.Select((m, i) => (m, i)).LastOrDefault(x => x.m.Role == "user").i;
        var before = Messages.Take(Math.Max(0, last)).Select(m => $"{m.Role}:{m.Content}");
        return [.. before, "format:" + (Body["format"]?.ToJsonString() ?? Body["response_format"]?.ToJsonString() ?? ""),
            "think:" + (Body["think"]?.ToJsonString() ?? Body["reasoning_effort"]?.ToJsonString() ?? "")];
    }

    public static LlmRequest Parse(LlmApi api, JsonObject body)
    {
        var messages = new List<LlmMessage>();
        bool stream;
        switch (api)
        {
            case LlmApi.OllamaGenerate:
                if (Json.Str(body["system"]) is { Length: > 0 } system)
                    messages.Add(new LlmMessage("system", system, false));
                messages.Add(new LlmMessage("user", Json.Str(body["prompt"]) ?? "", body["images"] is JsonArray { Count: > 0 }));
                // Ollama streams unless told not to.
                stream = Json.Bool(body["stream"]) ?? true;
                break;
            case LlmApi.OllamaChat:
                foreach (var node in body["messages"] as JsonArray ?? [])
                    if (node is JsonObject message)
                        messages.Add(new LlmMessage(Json.Str(message["role"]) ?? "user", Text(message["content"]),
                            message["images"] is JsonArray { Count: > 0 }));
                stream = Json.Bool(body["stream"]) ?? true;
                break;
            default:
                foreach (var node in body["messages"] as JsonArray ?? [])
                    if (node is JsonObject message)
                        messages.Add(new LlmMessage(Json.Str(message["role"]) ?? "user", Text(message["content"]), HasImagePart(message["content"])));
                stream = Json.Bool(body["stream"]) ?? false;
                break;
        }
        var request = new LlmRequest { Api = api, Body = body, Messages = messages, Stream = stream };
        request.NotCacheable =
            request.LastPrompt.Trim().Length == 0 ? "no prompt of the user"
            : request.HasImages ? "a picture in the request"
            : body["tools"] is JsonArray { Count: > 0 } ? "tools in the request"
            : body["context"] is JsonArray { Count: > 0 } ? "a context of /api/generate"
            : null;
        return request;
    }

    /// <summary>The text of a content: a string, or the text parts of an array (OpenAI).</summary>
    private static string Text(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>()
            .Where(p => Json.Str(p["type"]) == "text").Select(p => Json.Str(p["text"]) ?? "")),
        _ => ""
    };

    private static bool HasImagePart(JsonNode? content) =>
        content is JsonArray parts && parts.OfType<JsonObject>().Any(p => Json.Str(p["type"]) is "image_url" or "input_image");
}

/// <summary>Reads a value of a JSON node without throwing when it is of another kind.</summary>
public static class Json
{
    public static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
