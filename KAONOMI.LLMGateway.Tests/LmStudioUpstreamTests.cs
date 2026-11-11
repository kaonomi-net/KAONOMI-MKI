using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using KAONOMI.LLMGateway.Configuration;
using KAONOMI.LLMGateway.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace KAONOMI.LLMGateway.Tests;

/// <summary>
/// The gateway in front of a fake LM Studio (the API of OpenAI only: /v1/models, /v1/chat/completions,
/// /v1/embeddings), its address given with /v1.
/// </summary>
public class LmStudioUpstreamTests : IAsyncLifetime
{
    private readonly string _folder = Directory.CreateTempSubdirectory("llmgateway-lmstudio-").FullName;
    private WebApplication _lmStudio = null!;
    private WebApplication _gateway = null!;
    private HttpClient _client = null!;
    private int _calls;
    private int _embeddings;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _lmStudio = builder.Build();
        _lmStudio.MapGet("/v1/models", () => Results.Ok(new
        {
            @object = "list",
            data = new[] { new { id = "openai/gpt-oss-20b" }, new { id = "qwen/qwen3-coder" }, new { id = "text-embedding-nomic-embed-text-v1.5" } }
        }));
        _lmStudio.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            Interlocked.Increment(ref _calls);
            var body = await JsonNode.ParseAsync(context.Request.Body);
            var model = body!["model"]!.GetValue<string>();
            await context.Response.WriteAsJsonAsync(new
            {
                id = "x", @object = "chat.completion", model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = $"[{model}] answer", reasoning_content = "", tool_calls = Array.Empty<object>() }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 3, completion_tokens = 2, total_tokens = 5 }
            });
        });
        _lmStudio.MapPost("/v1/embeddings", () =>
        {
            Interlocked.Increment(ref _embeddings);
            // The same vector for every prompt: the cache then tells them apart by their entities only.
            return Results.Ok(new { data = new[] { new { embedding = new[] { 0.6f, 0.8f, 0f } } } });
        });
        await _lmStudio.StartAsync();

        var settings = new GatewaySettings
        {
            Upstream = Address(_lmStudio).TrimEnd('/') + "/v1",
            UpstreamProvider = "lmstudio",
            DataDirectory = _folder,
            Cache = new CacheSettings { EmbeddingModel = "text-embedding-nomic-embed-text-v1.5", MinAnswers = 1 },
            Routing = new RoutingSettings
            {
                DefaultModel = "openai/gpt-oss-20b",
                EscalationModel = "qwen/qwen3-coder",
                Rules = [new RouteRule { Name = "code", Any = ["CODE", "PROGRAMMING_LANGUAGE"], Model = "qwen/qwen3-coder", Priority = 10 }]
            }
        };
        _gateway = GatewayApp.Build(settings, url: "http://127.0.0.1:0");
        await _gateway.StartAsync();
        await _gateway.Services.GetRequiredService<Gateway>().RefreshModelsAsync();
        _client = new HttpClient { BaseAddress = new Uri(Address(_gateway)) };
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private static object Body(string prompt) => new { model = "auto", messages = new[] { new { role = "user", content = prompt } } };

    [Fact]
    public async Task The_models_and_the_embedder_are_read_from_LM_Studio()
    {
        var gateway = _gateway.Services.GetRequiredService<Gateway>();
        Assert.True(gateway.UpstreamOk);
        Assert.Contains("openai/gpt-oss-20b", gateway.InstalledModels);
        Assert.Equal("openai:text-embedding-nomic-embed-text-v1.5", gateway.Embedder.Name);

        var status = await _client.GetFromJsonAsync<JsonObject>("/gateway/status");
        Assert.Equal("lmstudio", status!["upstreamProvider"]!.GetValue<string>());
        Assert.True(status["upstreamOk"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_request_is_routed_answered_by_LM_Studio_then_by_the_cache()
    {
        var first = await _client.PostAsJsonAsync("/v1/chat/completions", Body("Give me a motto"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("openai/gpt-oss-20b", first.Headers.GetValues("X-Gateway-Model").Single());
        Assert.Equal("MISS", first.Headers.GetValues("X-Gateway-Cache").Single());
        var answer = await first.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("[openai/gpt-oss-20b] answer", answer!["choices"]![0]!["message"]!["content"]!.GetValue<string>());

        var second = await _client.PostAsJsonAsync("/v1/chat/completions", Body("give me a motto"));
        Assert.Equal("HIT", second.Headers.GetValues("X-Gateway-Cache").Single());
        Assert.Equal(1, _calls);
        Assert.True(_embeddings >= 2);

        var code = await _client.PostAsJsonAsync("/v1/chat/completions", Body("Why does this fail?\n```csharp\nvar x = null;\n```"));
        Assert.Equal("qwen/qwen3-coder", code.Headers.GetValues("X-Gateway-Model").Single());
    }

    [Fact]
    public async Task The_models_are_listed_through_the_gateway()
    {
        var models = await _client.GetFromJsonAsync<JsonObject>("/v1/models");
        Assert.Equal(3, models!["data"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_request_in_the_API_of_Ollama_is_refused_with_what_to_do()
    {
        var response = await _client.PostAsJsonAsync("/api/chat", new { model = "auto", messages = new[] { new { role = "user", content = "hello" } } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("/v1/chat/completions", (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>());
        Assert.Equal(0, _calls);
    }

    [Fact]
    public async Task A_disliked_answer_of_the_default_model_leaves_the_cache_and_names_the_model_to_ask_again()
    {
        var first = await _client.PostAsJsonAsync("/v1/chat/completions", Body("Tell me about the weather"));
        var answer = (await first.Content.ReadFromJsonAsync<JsonObject>())!["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        Assert.Equal("HIT", (await _client.PostAsJsonAsync("/v1/chat/completions", Body("Tell me about the weather"))).Headers.GetValues("X-Gateway-Cache").Single());

        // The client only knows the model it asked for: the gateway finds who answered.
        var dislike = await _client.PostAsJsonAsync("/gateway/feedback/dislike", new { prompt = "Tell me about the weather", answer = " " + answer + " ", model = "auto" });
        var result = await dislike.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(1, result!["removed"]!.GetValue<int>());
        Assert.Equal("openai/gpt-oss-20b", result["model"]!.GetValue<string>());
        Assert.Equal("qwen/qwen3-coder", result["escalationModel"]!.GetValue<string>());

        // The interaction is gone: the same prompt goes to the model again.
        Assert.Equal("MISS", (await _client.PostAsJsonAsync("/v1/chat/completions", Body("Tell me about the weather"))).Headers.GetValues("X-Gateway-Cache").Single());

        // Asked again to the escalation model, as the chat does: that model answers, and a dislike of its answer goes nowhere.
        var again = await _client.PostAsJsonAsync("/v1/chat/completions", new { model = "qwen/qwen3-coder", messages = new[] { new { role = "user", content = "Tell me about the weather" } } });
        Assert.Equal("qwen/qwen3-coder", again.Headers.GetValues("X-Gateway-Model").Single());
        var second = await (await _client.PostAsJsonAsync("/gateway/feedback/dislike", new { prompt = "Tell me about the weather", answer = "[qwen/qwen3-coder] answer", model = "qwen/qwen3-coder" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(1, second!["removed"]!.GetValue<int>());
        Assert.Null(second["escalationModel"]);
    }

    [Fact]
    public async Task Saving_the_settings_without_an_escalation_model_keeps_it()
    {
        var saved = await _client.PutAsJsonAsync("/gateway/settings", new { routing = new { enabled = true, overrideRequested = true, defaultModel = "openai/gpt-oss-20b" } });
        var routing = (await saved.Content.ReadFromJsonAsync<JsonObject>())!["routing"]!;
        Assert.Equal("qwen/qwen3-coder", routing["escalationModel"]!.GetValue<string>());
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _gateway.StopAsync();
        await _gateway.DisposeAsync();
        await _lmStudio.StopAsync();
        await _lmStudio.DisposeAsync();
        Directory.Delete(_folder, true);
    }
}
