using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

/// <summary>The gateway in front of a fake Ollama that counts its calls and says which model answered.</summary>
public class GatewayEndToEndTests : IAsyncLifetime
{
    private readonly string _folder = Directory.CreateTempSubdirectory("llmgateway-e2e-").FullName;
    private WebApplication _ollama = null!;
    private WebApplication _gateway = null!;
    private HttpClient _client = null!;
    private int _calls;
    private int _answers;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _ollama = builder.Build();
        _ollama.MapGet("/api/tags", () => Results.Ok(new { models = new[] { new { name = "general:latest" }, new { name = "coder:latest" } } }));
        _ollama.MapPost("/api/chat", async (HttpContext context) =>
        {
            Interlocked.Increment(ref _calls);
            var body = await JsonNode.ParseAsync(context.Request.Body);
            var model = body!["model"]!.GetValue<string>();
            var prompt = body["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
            var answer = $"[{model}] answer to {prompt} #{Interlocked.Increment(ref _answers)}";
            if (body["stream"]?.GetValue<bool>() ?? true)
            {
                context.Response.ContentType = "application/x-ndjson";
                foreach (var word in answer.Split(' '))
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new { model, message = new { role = "assistant", content = word + " " }, done = false }) + "\n");
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { model, message = new { role = "assistant", content = "" }, done = true, done_reason = "stop" }) + "\n");
                return;
            }
            await context.Response.WriteAsJsonAsync(new { model, message = new { role = "assistant", content = answer }, done = true, done_reason = "stop", eval_count = 5 });
        });
        _ollama.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            Interlocked.Increment(ref _calls);
            var body = await JsonNode.ParseAsync(context.Request.Body);
            var model = body!["model"]!.GetValue<string>();
            await context.Response.WriteAsJsonAsync(new
            {
                id = "x", @object = "chat.completion", model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = $"[{model}] openai answer" }, finish_reason = "stop" } }
            });
        });
        await _ollama.StartAsync();
        var upstream = Address(_ollama);

        var settings = new GatewaySettings
        {
            Upstream = upstream,
            DataDirectory = _folder,
            Cache = new CacheSettings { EmbeddingModel = "", LexicalThreshold = 0.85, MinAnswers = 1 },
            Routing = new RoutingSettings
            {
                DefaultModel = "general",
                Rules = [new RouteRule { Name = "code", Any = ["CODE", "PROGRAMMING_LANGUAGE"], Model = "coder", Priority = 10 }]
            }
        };
        _gateway = GatewayApp.Build(settings, url: "http://127.0.0.1:0");
        await _gateway.StartAsync();
        await _gateway.Services.GetRequiredService<Gateway>().RefreshModelsAsync();
        _client = new HttpClient { BaseAddress = new Uri(Address(_gateway)) };
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private static object Chat(string model, string prompt, bool stream = false) =>
        new { model, stream, messages = new[] { new { role = "system", content = "Be brief." }, new { role = "user", content = prompt } } };

    [Fact]
    public async Task The_second_close_prompt_comes_from_the_cache()
    {
        var first = await _client.PostAsJsonAsync("/api/chat", Chat("general", "What is the capital of France?"));
        Assert.Equal("MISS", first.Headers.GetValues("X-Gateway-Cache").Single());
        var second = await _client.PostAsJsonAsync("/api/chat", Chat("general", "what's the capital of France ?"));
        Assert.Equal("HIT", second.Headers.GetValues("X-Gateway-Cache").Single());
        var answer = await second.Content.ReadFromJsonAsync<JsonObject>();
        Assert.StartsWith("[general] answer to What is the capital of France? #", answer!["message"]!["content"]!.GetValue<string>());
        Assert.True(answer["done"]!.GetValue<bool>());
        Assert.Equal(1, _calls);

        var spain = await _client.PostAsJsonAsync("/api/chat", Chat("general", "What is the capital of Spain?"));
        Assert.Equal("MISS", spain.Headers.GetValues("X-Gateway-Cache").Single());
        Assert.Equal(2, _calls);
    }

    [Fact]
    public async Task A_streamed_answer_is_relayed_then_cached_and_streamed_back()
    {
        var first = await _client.PostAsJsonAsync("/api/chat", Chat("general", "Name three colours", stream: true));
        var lines = (await first.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length > 2);
        var second = await _client.PostAsJsonAsync("/api/chat", Chat("general", "name three colours", stream: true));
        Assert.Equal("HIT", second.Headers.GetValues("X-Gateway-Cache").Single());
        var parts = (await second.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.StartsWith("[general] answer to Name three colours #", string.Concat(parts.Select(p => p["message"]!["content"]!.GetValue<string>())));
        Assert.True(parts[^1]["done"]!.GetValue<bool>());
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Code_is_routed_to_the_coder()
    {
        var response = await _client.PostAsJsonAsync("/api/chat", Chat("general", "Why does this fail?\n```csharp\nvar x = null;\n```"));
        Assert.Equal("coder", response.Headers.GetValues("X-Gateway-Model").Single());
        Assert.Equal("code", response.Headers.GetValues("X-Gateway-Rule").Single());
        var answer = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.StartsWith("[coder]", answer!["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAi_requests_are_routed_and_cached_too()
    {
        object Body(string prompt) => new { model = "auto", messages = new[] { new { role = "user", content = prompt } } };
        var first = await _client.PostAsJsonAsync("/v1/chat/completions", Body("Give me a motto"));
        Assert.Equal("general", first.Headers.GetValues("X-Gateway-Model").Single());
        var second = await _client.PostAsJsonAsync("/v1/chat/completions", Body("give me a motto"));
        Assert.Equal("HIT", second.Headers.GetValues("X-Gateway-Cache").Single());
        var answer = await second.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("[general] openai answer", answer!["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task The_header_off_skips_the_cache_and_other_paths_go_through()
    {
        await _client.PostAsJsonAsync("/api/chat", Chat("general", "Tell me a fact"));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(Chat("general", "Tell me a fact")) };
        request.Headers.Add("X-Gateway-Cache", "off");
        var again = await _client.SendAsync(request);
        Assert.Equal("BYPASS", again.Headers.GetValues("X-Gateway-Cache").Single());
        Assert.Equal(2, _calls);
        var tags = await _client.GetFromJsonAsync<JsonObject>("/api/tags");
        Assert.Equal(2, tags!["models"]!.AsArray().Count);
    }

    [Fact]
    public async Task The_admin_api_reports_and_changes_the_gateway()
    {
        await _client.PostAsJsonAsync("/api/chat", Chat("general", "What is 12 * 7?"));
        await _client.PostAsJsonAsync("/api/chat", Chat("general", "what is 12 * 7 ?"));
        var status = await _client.GetFromJsonAsync<JsonObject>("/gateway/status");
        Assert.Equal(1, status!["cache"]!["hits"]!.GetValue<int>());
        Assert.Equal("lexical-1024", status["embedder"]!.GetValue<string>());

        var log = await _client.GetFromJsonAsync<JsonArray>("/gateway/log");
        Assert.Equal("HIT", log![0]!["cache"]!.GetValue<string>());

        var analysis = await _client.PostAsJsonAsync("/gateway/analyze", new { prompt = "Fix this:\n```python\nprint(1)\n```", model = "general" });
        var result = await analysis.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("coder", result!["route"]!["model"]!.GetValue<string>());

        var rules = new[] { new { name = "maths", any = new[] { "MATH" }, model = "coder", priority = 5 } };
        Assert.True((await _client.PutAsJsonAsync("/gateway/rules", rules)).IsSuccessStatusCode);
        var routed = await _client.PostAsJsonAsync("/api/chat", Chat("general", "Compute 3 + 4"));
        Assert.Equal("coder", routed.Headers.GetValues("X-Gateway-Model").Single());

        var cleared = await (await _client.DeleteAsync("/gateway/cache")).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(cleared!["removed"]!.GetValue<int>() >= 1);
        Assert.True(File.Exists(Path.Combine(_folder, "state.json")));
    }

    [Fact]
    public async Task With_three_answers_needed_the_model_answers_three_times_then_the_cache_varies()
    {
        var settings = await _client.GetFromJsonAsync<JsonObject>("/gateway/settings");
        var cache = settings!["cache"]!.AsObject();
        cache["minAnswers"] = 3;
        Assert.True((await _client.PutAsJsonAsync("/gateway/settings", new JsonObject { ["cache"] = cache.DeepClone() })).IsSuccessStatusCode);

        async Task<(string Cache, string Answer)> Ask()
        {
            var response = await _client.PostAsJsonAsync("/api/chat", Chat("general", "Tell me a joke about cats"));
            var body = await response.Content.ReadFromJsonAsync<JsonObject>();
            return (response.Headers.GetValues("X-Gateway-Cache").Single(), body!["message"]!["content"]!.GetValue<string>());
        }
        var first = new[] { await Ask(), await Ask(), await Ask() };
        Assert.All(first, a => Assert.Equal("MISS", a.Cache));
        Assert.Equal(3, _calls);

        var later = new List<(string Cache, string Answer)>();
        for (var i = 0; i < 12; i++)
            later.Add(await Ask());
        Assert.All(later, a => Assert.Equal("HIT", a.Cache));
        Assert.Equal(3, _calls);
        Assert.True(later.Select(a => a.Answer).Distinct().Count() > 1);
        Assert.All(later.Select(a => a.Answer), answer => Assert.Contains(answer, first.Select(a => a.Answer)));

        var refused = await _client.PutAsJsonAsync("/gateway/settings", new { cache = new { minAnswers = 0 } });
        Assert.False(refused.IsSuccessStatusCode);
    }

    [Fact]
    public async Task The_phrases_are_listed_exported_and_imported_back()
    {
        await _client.PostAsJsonAsync("/api/chat", Chat("general", "What is the capital of France?"));
        await _client.PostAsJsonAsync("/api/chat", Chat("general", "Name three colours"));
        var phrases = await _client.GetFromJsonAsync<JsonArray>("/gateway/cache/phrases?search=france");
        Assert.Single(phrases!);
        Assert.Equal("Be brief.", phrases![0]!["system"]!.GetValue<string>());
        Assert.Single(phrases[0]!["answers"]!.AsArray());

        var exported = await _client.GetFromJsonAsync<JsonObject>("/gateway/cache/export");
        Assert.Equal("kaonomi-llmgateway-cache", exported!["format"]!.GetValue<string>());
        Assert.Equal(2, exported["phrases"]!.AsArray().Count);

        await _client.DeleteAsync("/gateway/cache");
        var imported = await (await _client.PostAsJsonAsync("/gateway/cache/import", exported)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(2, imported!["added"]!.GetValue<int>());
        var again = await (await _client.PostAsJsonAsync("/gateway/cache/import", exported)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(0, again!["added"]!.GetValue<int>());
        Assert.Equal(2, again["skipped"]!.GetValue<int>());

        // The phrase imported keeps its model and its context: the same request is answered by the cache.
        var calls = _calls;
        var hit = await _client.PostAsJsonAsync("/api/chat", Chat("general", "what is the capital of France ?"));
        Assert.Equal("HIT", hit.Headers.GetValues("X-Gateway-Cache").Single());
        Assert.Equal(calls, _calls);
    }

    [Fact]
    public async Task A_phrase_written_by_hand_holds_for_every_model_and_context()
    {
        var settings = await _client.GetFromJsonAsync<JsonObject>("/gateway/settings");
        var cache = settings!["cache"]!.AsObject();
        cache["minAnswers"] = 3;
        await _client.PutAsJsonAsync("/gateway/settings", new JsonObject { ["cache"] = cache.DeepClone() });

        var file = new
        {
            phrases = new[]
            {
                new { prompt = "Say hello to the team", answers = new[] { "Hello team!", "Hi everyone!", "Good morning, team!" } },
                new { prompt = "", answers = new[] { "nothing" } }
            }
        };
        var result = await (await _client.PostAsJsonAsync("/gateway/cache/import", file)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(3, result!["added"]!.GetValue<int>());
        Assert.Single(result["errors"]!.AsArray());

        var answers = new HashSet<string>();
        foreach (var (model, system) in new[] { ("general", "Be brief."), ("coder", "You are a pirate."), ("general", "") })
        {
            var body = new { model, stream = false, messages = system.Length == 0
                ? new object[] { new { role = "user", content = "say hello to the team" } }
                : new object[] { new { role = "system", content = system }, new { role = "user", content = "say hello to the team" } } };
            var response = await _client.PostAsJsonAsync("/api/chat", body);
            Assert.Equal("HIT", response.Headers.GetValues("X-Gateway-Cache").Single());
            answers.Add((await response.Content.ReadFromJsonAsync<JsonObject>())!["message"]!["content"]!.GetValue<string>());
        }
        Assert.Equal(0, _calls);
        Assert.True(answers.Count > 1);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _gateway.StopAsync();
        await _gateway.DisposeAsync();
        await _ollama.StopAsync();
        await _ollama.DisposeAsync();
        Directory.Delete(_folder, true);
    }
}
