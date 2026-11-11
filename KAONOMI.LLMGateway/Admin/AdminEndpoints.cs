using KAONOMI.LLMGateway.Cache;
using KAONOMI.LLMGateway.Configuration;
using KAONOMI.LLMGateway.Entities;
using KAONOMI.LLMGateway.Proxy;
using KAONOMI.LLMGateway.Routing;

namespace KAONOMI.LLMGateway.Admin;

/// <summary>The body of POST /gateway/analyze: a prompt, the model asked, and a system prompt (optional).</summary>
public sealed record AnalyzeRequest(string Prompt, string? Model, string? System);

/// <summary>An answer the user disliked: the prompt as it was sent, the answer, and the model the client believes answered.</summary>
public sealed record DislikeRequest(string? Prompt, string? Answer, string? Model);

/// <summary>
/// The API to drive the gateway (the window /gateway of the chat uses it), under <c>/gateway</c>:
/// <list type="bullet">
/// <item><c>GET status</c>: Ollama, the embedder, the counters of the cache and of the rules.</item>
/// <item><c>GET|PUT settings</c>: the cache and the routing (without the rules).</item>
/// <item><c>GET|PUT rules</c>: the rules of the router.</item>
/// <item><c>GET cache?take=&amp;search=</c>, <c>DELETE cache</c>, <c>DELETE cache/{id}</c>: the answers kept.</item>
/// <item><c>GET log?take=</c>: the last requests, what was routed and cached.</item>
/// <item><c>POST analyze</c>: what the gateway would do with a prompt (entities, model, closest cached prompt), without asking a model.</item>
/// <item><c>POST feedback/dislike</c> <c>{prompt, answer, model}</c>: the disliked answer leaves the cache; when the
/// default model gave it, <c>escalationModel</c> names the model to ask again.</item>
/// <item><c>GET models</c>, <c>GET entity-types</c>, <c>POST reset</c> (forgets the settings changed at run time).</item>
/// </list>
/// </summary>
public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/gateway");

        api.MapGet("/status", (Gateway g) =>
        {
            var hits = g.Cache.Hits;
            var misses = g.Cache.Misses;
            return Results.Ok(new
            {
                name = "KAONOMI.LLMGateway",
                port = g.Settings.Port,
                upstream = g.Settings.Upstream,
                upstreamProvider = g.Settings.UpstreamIsOpenAi ? "lmstudio" : "ollama",
                upstreamOk = g.UpstreamOk,
                started = g.Started,
                uptimeSeconds = (long)(DateTimeOffset.UtcNow - g.Started).TotalSeconds,
                requests = g.Log.Total,
                embedder = g.Embedder.Name,
                embedderNote = g.EmbedderNote,
                dataDirectory = g.Settings.ResolvedDataDirectory,
                cache = new
                {
                    entries = g.Cache.Count, hits, misses,
                    hitRate = hits + misses == 0 ? 0 : Math.Round((double)hits / (hits + misses), 3),
                    savedMs = g.Cache.SavedMs
                },
                ruleHits = g.Log.RuleHits(),
                models = g.InstalledModels
            });
        });

        api.MapGet("/settings", (Gateway g) => Results.Ok(new { cache = g.CacheSettings, routing = Without(g.RoutingSettings) }));

        api.MapPut("/settings", async (Gateway g, SettingsUpdate update) =>
        {
            if (update.Cache is { } cache)
            {
                if (cache.Threshold is < 0 or > 1 || cache.LexicalThreshold is < 0 or > 1)
                    return Results.BadRequest(new { error = "the thresholds are between 0 and 1" });
                if (cache.MinAnswers is < 1 or > 100)
                    return Results.BadRequest(new { error = "minAnswers is between 1 and 100" });
                await g.SetCacheSettingsAsync(cache);
            }
            if (update.Routing is { } routing)
            {
                routing.Rules = g.RoutingSettings.Rules;
                // A client that does not know the escalation model (it sends none) keeps the one in place.
                if (string.IsNullOrWhiteSpace(routing.EscalationModel))
                    routing.EscalationModel = g.RoutingSettings.EscalationModel;
                g.SetRoutingSettings(routing);
            }
            return Results.Ok(new { cache = g.CacheSettings, routing = Without(g.RoutingSettings) });
        });

        api.MapGet("/rules", (Gateway g) => Results.Ok(g.RoutingSettings.Rules.OrderByDescending(r => r.Priority)));

        api.MapPut("/rules", (Gateway g, List<RouteRule> rules) =>
        {
            var names = rules.Select(r => r.Name.Trim()).ToList();
            if (names.Any(n => n.Length == 0) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                return Results.BadRequest(new { error = "each rule needs a name of its own" });
            if (rules.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Model)) is { } noModel)
                return Results.BadRequest(new { error = $"the rule {noModel.Name} has no model" });
            var routing = Copy(g.RoutingSettings);
            routing.Rules = rules;
            g.SetRoutingSettings(routing);
            return Results.Ok(routing.Rules.OrderByDescending(r => r.Priority));
        });

        api.MapGet("/cache", (Gateway g, int? take, string? search) =>
            Results.Ok(g.Cache.List(take ?? 100, search).Select(e => new
            {
                e.Id, e.Model, e.Prompt, e.Answer, e.Embedder, e.EntitySignature, e.Created, e.LastHit, e.Hits, e.AnswerMs
            })));

        api.MapDelete("/cache", (Gateway g) => Results.Ok(new { removed = g.Cache.Clear() }));

        api.MapGet("/cache/phrases", (Gateway g, string? search, int? take) => Results.Ok(g.Cache.Phrases(search, take ?? 200)));

        api.MapGet("/cache/export", (Gateway g, string? search) =>
        {
            var phrases = g.Cache.Phrases(search, int.MaxValue).Select(p => new CacheExchange.Phrase
            {
                Prompt = p.Prompt, Model = p.Model, Context = p.Context, System = p.System, Answers = p.Answers.Select(a => a.Text).ToList()
            }).ToList();
            return Results.Ok(new CacheExchange { Exported = DateTimeOffset.UtcNow, Phrases = phrases });
        });

        api.MapPost("/cache/import", async (Gateway g, CacheExchange document, CancellationToken cancellationToken) =>
            Results.Ok(await CacheExchange.ImportAsync(g, document, cancellationToken)));

        api.MapDelete("/cache/{id}", (Gateway g, string id) => g.Cache.Remove(id) ? Results.Ok(new { removed = 1 }) : Results.NotFound());

        api.MapPost("/feedback/dislike", (Gateway g, DislikeRequest request) =>
        {
            var prompt = request.Prompt ?? "";
            // The cache keeps the question of the prompt (see CacheQuestion); the entries kept before that hold the whole prompt.
            var removed = g.Cache.RemoveInteraction(CacheQuestion.Of(prompt).Question, request.Answer ?? "")
                .Concat(g.Cache.RemoveInteraction(prompt, request.Answer ?? "")).ToList();
            // Who answered: the model of the cached interaction, else the last request of the log for this prompt, else
            // what the client says.
            var answeredBy = removed.Select(e => e.Model).FirstOrDefault(m => m != SemanticCache.Any)
                             ?? g.Log.Last(200).FirstOrDefault(r => r.Status == 200 && r.Prompt == RequestLog.Excerpt(prompt))?.Model
                             ?? request.Model?.Trim() ?? "";
            var routing = g.RoutingSettings;
            var target = routing.EscalationModel.Trim();
            var installed = g.InstalledModels;
            string? escalation = null;
            string reason;
            if (target.Length == 0)
                reason = "no escalation model set (routing.escalationModel)";
            else if (!SameModel(answeredBy, routing.DefaultModel))
                reason = $"answered by {(answeredBy.Length == 0 ? "an unknown model" : answeredBy)}, not by the default model {routing.DefaultModel}";
            else if (SameModel(target, routing.DefaultModel))
                reason = "the escalation model is the default model";
            else if (installed.Count > 0 && !ModelRouter.IsInstalled(installed, target))
                reason = $"{target} is not installed";
            else
            {
                escalation = target;
                reason = $"answered by the default model {routing.DefaultModel}: ask {target}";
            }
            Console.WriteLine($"[dislike] {removed.Count} answer(s) out of the cache · {reason}");
            return Results.Ok(new { removed = removed.Count, model = answeredBy, escalationModel = escalation, reason });
        });

        api.MapGet("/log", (Gateway g, int? take) => Results.Ok(g.Log.Last(take ?? 100)));

        api.MapPost("/analyze", async (Gateway g, AnalyzeRequest request, CancellationToken cancellationToken) =>
        {
            var entities = g.Extractor.Extract(request.Prompt);
            var decision = g.Router.Route(request.Model, entities);
            // As the proxy does: the cache compares the question of the prompt, its previous exchanges are context.
            var (question, discussion) = CacheQuestion.Of(request.Prompt);
            string[] parts = discussion is null ? LlmRequest.ContextPartsFor(request.System) : [.. LlmRequest.ContextPartsFor(request.System), "discussion:" + discussion];
            var (embedder, vector) = await g.EmbedAsync(question, cancellationToken);
            var signature = EntityExtractor.Signature(question == request.Prompt ? entities : g.Extractor.Extract(question));
            var lookup = g.Cache.Find(decision.Model, SemanticCache.ContextKey(parts), embedder.Name, vector, signature, count: false);
            return Results.Ok(new
            {
                entities = entities.Select(e => new { e.Type, e.Value, e.Text, e.Start, e.Length }),
                language = EntityExtractor.DetectLanguage(request.Prompt),
                route = new { decision.RequestedModel, decision.Model, decision.Rule, decision.Reason, matched = decision.Matched.Select(e => e.ToString()) },
                cache = new
                {
                    enabled = g.CacheSettings.Enabled,
                    embedder = embedder.Name,
                    signature,
                    hit = lookup.Hit is not null,
                    answers = lookup.Similar,
                    minAnswers = lookup.Needed,
                    score = Math.Round(lookup.BestScore, 3),
                    reason = lookup.Reason,
                    closest = lookup.Closest is { } c ? new { c.Id, c.Prompt, c.Answer, c.Model } : null
                }
            });
        });

        api.MapGet("/models", async (Gateway g) =>
        {
            await g.RefreshModelsAsync();
            return Results.Ok(g.InstalledModels);
        });

        api.MapGet("/entity-types", (Gateway g) =>
            Results.Ok(EntityTypes.Builtin.Concat(g.Extractor.GazetteerTypes).Distinct(StringComparer.OrdinalIgnoreCase)));

        api.MapPost("/reset", (Gateway g) => Results.Ok(new
        {
            forgotten = g.ResetState(),
            note = "the settings of config/llmgateway.json come back at the next start of the gateway"
        }));
    }

    /// <summary>The body of PUT /gateway/settings: the parts to replace.</summary>
    public sealed record SettingsUpdate(CacheSettings? Cache, RoutingSettings? Routing);

    private static object Without(RoutingSettings routing) => new
    {
        routing.Enabled, routing.OverrideRequested, routing.DefaultModel, routing.EscalationModel, rules = routing.Rules.Count
    };

    /// <summary>The same model, with or without the :latest of Ollama.</summary>
    private static bool SameModel(string a, string b)
    {
        static string Bare(string name) => name.Trim().EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name.Trim()[..^7] : name.Trim();
        return a.Trim().Length > 0 && string.Equals(Bare(a), Bare(b), StringComparison.OrdinalIgnoreCase);
    }

    private static RoutingSettings Copy(RoutingSettings routing) => new()
    {
        Enabled = routing.Enabled, OverrideRequested = routing.OverrideRequested, DefaultModel = routing.DefaultModel,
        EscalationModel = routing.EscalationModel, Rules = routing.Rules
    };
}
