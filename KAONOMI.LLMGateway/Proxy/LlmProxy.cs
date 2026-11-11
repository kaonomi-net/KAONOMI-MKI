using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KAONOMI.LLMGateway.Cache;
using KAONOMI.LLMGateway.Entities;
using KAONOMI.LLMGateway.Routing;

namespace KAONOMI.LLMGateway.Proxy;

/// <summary>
/// The gateway in front of Ollama. A request to a model (<c>/api/chat</c>, <c>/api/generate</c>,
/// <c>/v1/chat/completions</c>) has the entities of its last prompt read, its model chosen by the router, then its
/// answer given back by the semantic cache when a close prompt was already answered, or asked to Ollama (streamed as it
/// comes) and kept in the cache. Every other request goes to Ollama unchanged. The answer says what was done in its
/// headers: <c>X-Gateway-Model</c>, <c>X-Gateway-Rule</c>, <c>X-Gateway-Cache</c> (HIT, MISS, BYPASS, OFF) and
/// <c>X-Gateway-Score</c>. <c>X-Gateway-Cache: off</c> in a request skips the cache for it.
/// </summary>
public sealed class LlmProxy(Gateway gateway)
{
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer", "Host", "Content-Length"
    };

    public async Task HandleAsync(HttpContext context, LlmApi api)
    {
        var stopwatch = Stopwatch.StartNew();
        var cancellationToken = context.RequestAborted;
        string raw;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
            raw = await reader.ReadToEndAsync(cancellationToken);
        JsonObject? body;
        try
        {
            body = raw.Length == 0 ? null : JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException)
        {
            body = null;
        }
        if (body is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "the body is not a JSON object" }, cancellationToken);
            return;
        }

        if (gateway.Settings.UpstreamIsOpenAi && api != LlmApi.OpenAiChat)
        {
            // LM Studio does not speak the API of Ollama, and the gateway does not translate one into the other.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"the upstream of the gateway is LM Studio, which has no {context.Request.Path}: ask /v1/chat/completions (llmProvider \"lmstudio\" in the settings of the client)"
            }, cancellationToken);
            return;
        }

        var request = LlmRequest.Parse(api, body);
        var entities = gateway.Extractor.Extract(request.LastPrompt).ToList();
        if (request.HasImages)
            entities.Add(new Entity(EntityTypes.Image, "image", "", 0, 0));
        var decision = gateway.Router.Route(request.Model, entities);
        body["model"] = decision.Model;

        context.Response.Headers["X-Gateway-Model"] = decision.Model;
        if (decision.Rule is { } rule)
            context.Response.Headers["X-Gateway-Rule"] = rule;

        // The cache: the same model, the same context, a close question with the same entities. The question is the
        // prompt without the name it is addressed to and without the previous exchanges written before it, which
        // count as context (see CacheQuestion).
        var (question, discussion) = CacheQuestion.Of(request.LastPrompt);
        var cacheState = "OFF";
        var cacheReason = "cache off";
        double score = 0;
        string? contextKey = null, signature = null;
        IEmbedder? embedder = null;
        float[]? vector = null;
        var bypass = string.Equals(context.Request.Headers["X-Gateway-Cache"], "off", StringComparison.OrdinalIgnoreCase);
        if (gateway.CacheSettings.Enabled && !bypass && request.NotCacheable is null)
        {
            contextKey = SemanticCache.ContextKey(discussion is null ? request.ContextParts() : [.. request.ContextParts(), "discussion:" + discussion]);
            signature = EntityExtractor.Signature(question == request.LastPrompt ? entities : gateway.Extractor.Extract(question));
            (embedder, vector) = await gateway.EmbedAsync(question, cancellationToken);
            var lookup = gateway.Cache.Find(decision.Model, contextKey, embedder.Name, vector, signature);
            score = lookup.BestScore;
            cacheReason = lookup.Reason;
            if (lookup.Hit is { } hit)
            {
                context.Response.Headers["X-Gateway-Cache"] = "HIT";
                context.Response.Headers["X-Gateway-Score"] = score.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                await WriteCachedAsync(context, request, decision.Model, hit, stopwatch, cancellationToken);
                Record(context, api, decision, "HIT", score, cacheReason, stopwatch, 200, request);
                return;
            }
            cacheState = "MISS";
        }
        else if (gateway.CacheSettings.Enabled)
        {
            cacheState = "BYPASS";
            cacheReason = bypass ? "X-Gateway-Cache: off" : request.NotCacheable ?? "";
        }
        context.Response.Headers["X-Gateway-Cache"] = cacheState;

        // Ollama.
        var upstream = new HttpRequestMessage(HttpMethod.Post, Upstream(context))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        CopyRequestHeaders(context, upstream);
        HttpResponseMessage response;
        try
        {
            response = await gateway.Http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new { error = $"{gateway.Settings.UpstreamName} not reached at {gateway.Settings.Upstream}: {ex.Message}" }, cancellationToken);
            Record(context, api, decision, cacheState, score, cacheReason, stopwatch, 502, request);
            return;
        }
        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response, context);
            var answer = request.Stream
                ? await RelayStreamAsync(api, response, context, cancellationToken)
                : await RelayWholeAsync(api, response, context, cancellationToken);
            if (cacheState == "MISS" && response.IsSuccessStatusCode && answer is { Complete: true, Text.Length: > 0 }
                && embedder is not null && vector is not null)
            {
                gateway.Cache.Add(new CacheEntry
                {
                    Model = decision.Model, Context = contextKey!, System = request.SystemPrompt, Prompt = question, EntitySignature = signature!,
                    Embedder = embedder.Name, Vector = vector, Answer = answer.Text, Thinking = answer.Thinking,
                    AnswerMs = Gateway.Elapsed(stopwatch)
                });
                cacheReason += " · kept";
            }
            Record(context, api, decision, cacheState, score, cacheReason, stopwatch, (int)response.StatusCode, request);
        }
    }

    /// <summary>Any other request (the list of the models, /api/show, /api/embed…): to Ollama and back, unchanged.</summary>
    public async Task PassThroughAsync(HttpContext context)
    {
        var upstream = new HttpRequestMessage(new HttpMethod(context.Request.Method), Upstream(context));
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            upstream.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } type)
                upstream.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        }
        CopyRequestHeaders(context, upstream);
        try
        {
            using var response = await gateway.Http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response, context);
            await using var stream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new { error = $"{gateway.Settings.UpstreamName} not reached at {gateway.Settings.Upstream}: {ex.Message}" });
        }
    }

    /// <summary>The text of an answer relayed, its thinking, and whether it ended normally (not cut, no tool called).</summary>
    private sealed record Relayed(string Text, string? Thinking, bool Complete);

    private static async Task<Relayed> RelayWholeAsync(LlmApi api, HttpResponseMessage response, HttpContext context, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await context.Response.Body.WriteAsync(bytes, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new Relayed("", null, false);
        try
        {
            var node = JsonNode.Parse(bytes);
            return api switch
            {
                LlmApi.OllamaChat => new Relayed(Json.Str(node?["message"]?["content"]) ?? "", Json.Str(node?["message"]?["thinking"]),
                    node?["message"]?["tool_calls"] is null && Json.Str(node?["done_reason"]) is null or "stop"),
                LlmApi.OllamaGenerate => new Relayed(Json.Str(node?["response"]) ?? "", Json.Str(node?["thinking"]),
                    Json.Str(node?["done_reason"]) is null or "stop"),
                _ => new Relayed(Json.Str(node?["choices"]?[0]?["message"]?["content"]) ?? "", Reasoning(node?["choices"]?[0]?["message"]),
                    !HasToolCalls(node?["choices"]?[0]?["message"]) && Json.Str(node?["choices"]?[0]?["finish_reason"]) is null or "stop")
            };
        }
        catch (JsonException)
        {
            return new Relayed("", null, false);
        }
    }

    /// <summary>The thinking of a message or delta of OpenAI: "reasoning" for Ollama, "reasoning_content" for LM Studio (empty when none).</summary>
    private static string? Reasoning(JsonNode? message) =>
        (Json.Str(message?["reasoning"]) ?? Json.Str(message?["reasoning_content"])) is { Length: > 0 } text ? text : null;

    /// <summary>True when a message or delta of OpenAI calls tools; LM Studio writes "tool_calls": [] when it calls none.</summary>
    private static bool HasToolCalls(JsonNode? message) =>
        message?["tool_calls"] is { } calls && calls is not JsonArray { Count: 0 };

    /// <summary>
    /// A streamed answer relayed line by line as it comes (NDJSON of Ollama, server-sent events of OpenAI), its text
    /// gathered on the way for the cache.
    /// </summary>
    private static async Task<Relayed> RelayStreamAsync(LlmApi api, HttpResponseMessage response, HttpContext context, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var complete = false;
        var cut = false;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await context.Response.WriteAsync(line + "\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            if (!response.IsSuccessStatusCode || line.Length == 0)
                continue;
            var json = api == LlmApi.OpenAiChat ? line.StartsWith("data:", StringComparison.Ordinal) ? line[5..].Trim() : null : line;
            if (json is null)
                continue;
            if (json == "[DONE]")
            {
                complete = !cut;
                continue;
            }
            try
            {
                var node = JsonNode.Parse(json);
                switch (api)
                {
                    case LlmApi.OllamaChat:
                        text.Append(Json.Str(node?["message"]?["content"]));
                        thinking.Append(Json.Str(node?["message"]?["thinking"]));
                        cut |= node?["message"]?["tool_calls"] is not null || node?["error"] is not null;
                        if (Json.Bool(node?["done"]) == true)
                            complete = !cut && Json.Str(node?["done_reason"]) is null or "stop";
                        break;
                    case LlmApi.OllamaGenerate:
                        text.Append(Json.Str(node?["response"]));
                        thinking.Append(Json.Str(node?["thinking"]));
                        cut |= node?["error"] is not null;
                        if (Json.Bool(node?["done"]) == true)
                            complete = !cut && Json.Str(node?["done_reason"]) is null or "stop";
                        break;
                    default:
                        var choice = node?["choices"]?[0];
                        text.Append(Json.Str(choice?["delta"]?["content"]));
                        // "reasoning" for Ollama, "reasoning_content" for LM Studio.
                        thinking.Append(Reasoning(choice?["delta"]));
                        cut |= HasToolCalls(choice?["delta"]);
                        if (Json.Str(choice?["finish_reason"]) is { } finish && finish != "stop")
                            cut = true;
                        break;
                }
            }
            catch (JsonException)
            {
                cut = true;
            }
        }
        return new Relayed(text.ToString(), thinking.Length > 0 ? thinking.ToString() : null, complete && !cut);
    }

    /// <summary>A cached answer in the form the client asked for: whole or streamed, of Ollama or of OpenAI.</summary>
    private static async Task WriteCachedAsync(HttpContext context, LlmRequest request, string model, CacheEntry hit, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var created = now.ToString("O");
        var nanoseconds = stopwatch.Elapsed.Ticks * 100;
        var response = context.Response;
        switch (request.Api)
        {
            case LlmApi.OllamaChat or LlmApi.OllamaGenerate:
            {
                var chat = request.Api == LlmApi.OllamaChat;
                JsonObject Part(string content, string? thought, bool done)
                {
                    var part = new JsonObject { ["model"] = model, ["created_at"] = created };
                    if (chat)
                    {
                        var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
                        if (thought is not null)
                            message["thinking"] = thought;
                        part["message"] = message;
                    }
                    else
                    {
                        part["response"] = content;
                        if (thought is not null)
                            part["thinking"] = thought;
                    }
                    part["done"] = done;
                    if (done)
                    {
                        part["done_reason"] = "stop";
                        part["total_duration"] = nanoseconds;
                        part["load_duration"] = 0;
                        part["prompt_eval_count"] = 0;
                        part["prompt_eval_duration"] = 0;
                        part["eval_count"] = 0;
                        part["eval_duration"] = 0;
                    }
                    return part;
                }
                if (request.Stream)
                {
                    response.ContentType = "application/x-ndjson";
                    await response.WriteAsync(Part(hit.Answer, hit.Thinking, false).ToJsonString() + "\n", cancellationToken);
                    await response.WriteAsync(Part("", null, true).ToJsonString() + "\n", cancellationToken);
                }
                else
                {
                    response.ContentType = "application/json; charset=utf-8";
                    await response.WriteAsync(Part(hit.Answer, hit.Thinking, true).ToJsonString(), cancellationToken);
                }
                break;
            }
            default:
            {
                var id = "chatcmpl-cache-" + hit.Id;
                var unix = now.ToUnixTimeSeconds();
                if (request.Stream)
                {
                    response.ContentType = "text/event-stream";
                    var first = new JsonObject
                    {
                        ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = unix, ["model"] = model,
                        ["choices"] = new JsonArray(new JsonObject
                        {
                            ["index"] = 0, ["delta"] = new JsonObject { ["role"] = "assistant", ["content"] = hit.Answer }, ["finish_reason"] = null
                        })
                    };
                    var last = new JsonObject
                    {
                        ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = unix, ["model"] = model,
                        ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject(), ["finish_reason"] = "stop" })
                    };
                    await response.WriteAsync($"data: {first.ToJsonString()}\n\ndata: {last.ToJsonString()}\n\ndata: [DONE]\n\n", cancellationToken);
                }
                else
                {
                    response.ContentType = "application/json; charset=utf-8";
                    var whole = new JsonObject
                    {
                        ["id"] = id, ["object"] = "chat.completion", ["created"] = unix, ["model"] = model,
                        ["choices"] = new JsonArray(new JsonObject
                        {
                            ["index"] = 0, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = hit.Answer }, ["finish_reason"] = "stop"
                        }),
                        ["usage"] = new JsonObject { ["prompt_tokens"] = 0, ["completion_tokens"] = 0, ["total_tokens"] = 0 }
                    };
                    await response.WriteAsync(whole.ToJsonString(), cancellationToken);
                }
                break;
            }
        }
    }

    private void Record(HttpContext context, LlmApi api, RouteDecision decision, string cache, double score, string cacheReason, Stopwatch stopwatch, int status, LlmRequest request)
    {
        gateway.Log.Add(new RequestRecord(DateTimeOffset.UtcNow, context.Request.Path, decision.RequestedModel, decision.Model, decision.Rule,
            decision.Reason, decision.Entities.Select(e => e.ToString()).Distinct().Take(20).ToList(), cache, Math.Round(score, 3), cacheReason,
            Gateway.Elapsed(stopwatch), status, RequestLog.Excerpt(request.LastPrompt)));
        Console.WriteLine($"[{api}] {decision.RequestedModel} → {decision.Model} ({decision.Reason}) · cache {cache} ({cacheReason}) · {Gateway.Elapsed(stopwatch)} ms");
    }

    private Uri Upstream(HttpContext context) =>
        new($"{gateway.Settings.UpstreamBase}{context.Request.Path}{context.Request.QueryString}");

    private static void CopyRequestHeaders(HttpContext context, HttpRequestMessage upstream)
    {
        foreach (var (name, values) in context.Request.Headers)
        {
            if (HopHeaders.Contains(name) || name.StartsWith("X-Gateway", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            upstream.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage response, HttpContext context)
    {
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            if (!HopHeaders.Contains(name))
                context.Response.Headers[name] = values.ToArray();
    }
}
