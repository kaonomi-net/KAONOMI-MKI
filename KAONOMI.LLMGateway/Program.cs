using KAONOMI.LLMGateway;
using KAONOMI.LLMGateway.Configuration;

// KAONOMI.LLMGateway: a gateway in front of Ollama, with a semantic cache and a router of the models by the entities of
// the prompt.
//   KAONOMI.LLMGateway [--config llmgateway.json] [--port 11435] [--upstream http://localhost:11434] [--provider ollama|lmstudio]

string? configFile = null;
int? port = null;
string? upstream = null;
string? provider = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" or "-c" when i + 1 < args.Length:
            configFile = args[++i];
            break;
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p):
            port = p;
            i++;
            break;
        case "--upstream" when i + 1 < args.Length:
            upstream = args[++i];
            break;
        case "--provider" when i + 1 < args.Length:
            provider = args[++i];
            break;
        case "-h" or "--help" or "help":
            Console.WriteLine("""
                KAONOMI.LLMGateway: a gateway in front of Ollama (its API, and the one of OpenAI), with a semantic cache
                and a router of the models by the entities of the prompt.

                  KAONOMI.LLMGateway [--config config/llmgateway.json] [--port 11435] [--upstream http://localhost:11434]
                                     [--provider ollama|lmstudio]

                In front of LM Studio: --upstream http://localhost:1234 --provider lmstudio (the API of OpenAI only:
                /v1/chat/completions, /v1/models; its clients are set to llmProvider "lmstudio").

                Point a client of Ollama to http://localhost:11435 (ollamaUrl in the config.json of the server and of the
                agents). The model "auto" lets the router choose. The API to drive it is under /gateway (status,
                settings, rules, cache, log, analyze): the window /gateway of the chat uses it.
                """);
            return 0;
    }
}

var file = GatewaySettings.Find(configFile);
var settings = GatewaySettings.Load(file);
if (port is { } given)
    settings.Port = given;
if (upstream is not null)
    settings.Upstream = upstream;
if (provider is not null)
    settings.UpstreamProvider = provider;

var app = GatewayApp.Build(settings, args);
Console.WriteLine($"KAONOMI.LLMGateway on http://localhost:{settings.Port} → {settings.UpstreamName} at {settings.Upstream} (settings: {file ?? "the defaults"}; data: {settings.ResolvedDataDirectory})");
await app.RunAsync();
return 0;
