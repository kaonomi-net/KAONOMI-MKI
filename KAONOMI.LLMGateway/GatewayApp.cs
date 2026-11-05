using KAONOMI.LLMGateway.Admin;
using KAONOMI.LLMGateway.Configuration;
using KAONOMI.LLMGateway.Proxy;

namespace KAONOMI.LLMGateway;

/// <summary>The web application of the gateway (Program and the tests build it the same way).</summary>
public static class GatewayApp
{
    public static WebApplication Build(GatewaySettings settings, string[]? args = null, string? url = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args ?? []);
        builder.WebHost.UseUrls(url ?? $"http://localhost:{settings.Port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            options.SerializerOptions.ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip;
            options.SerializerOptions.AllowTrailingCommas = true;
        });
        // A model takes its time: no timeout but the one of the client.
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(_ => new Gateway(settings, http));
        builder.Services.AddSingleton<LlmProxy>();

        var app = builder.Build();
        app.MapPost("/api/chat", (HttpContext context, LlmProxy proxy) => proxy.HandleAsync(context, LlmApi.OllamaChat));
        app.MapPost("/api/generate", (HttpContext context, LlmProxy proxy) => proxy.HandleAsync(context, LlmApi.OllamaGenerate));
        app.MapPost("/v1/chat/completions", (HttpContext context, LlmProxy proxy) => proxy.HandleAsync(context, LlmApi.OpenAiChat));
        AdminEndpoints.Map(app);
        app.MapFallback((HttpContext context, LlmProxy proxy) => proxy.PassThroughAsync(context));
        // The gateway is made at once (it reads the models of Ollama), and its cache written when it stops.
        app.Services.GetRequiredService<Gateway>();
        return app;
    }
}
