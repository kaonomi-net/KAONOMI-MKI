using KAONOMI.LLMGateway.Configuration;
using KAONOMI.LLMGateway.Entities;

namespace KAONOMI.LLMGateway.Routing;

/// <summary>The choice of the router for a request, and why.</summary>
public sealed record RouteDecision(
    string RequestedModel, string Model, string? Rule, IReadOnlyList<Entity> Entities, IReadOnlyList<Entity> Matched, string Reason)
{
    public bool Rerouted => !string.Equals(RequestedModel, Model, StringComparison.Ordinal);
}

/// <summary>
/// The router of the models: the first rule (by priority) whose entities are in the prompt gives the model, when the
/// routing is on, when the model is installed in Ollama, and when the client asked for auto (or any model, with
/// overrideRequested). A request for auto that no rule matches goes to the default model.
/// </summary>
public sealed class ModelRouter(Func<RoutingSettings> settings, Func<IReadOnlyCollection<string>?> installedModels)
{
    /// <summary>The model a client asks for to leave the choice to the router.</summary>
    public const string Auto = "auto";

    public RouteDecision Route(string? requestedModel, IReadOnlyList<Entity> entities)
    {
        var routing = settings();
        var requested = string.IsNullOrWhiteSpace(requestedModel) ? Auto : requestedModel.Trim();
        var auto = string.Equals(requested, Auto, StringComparison.OrdinalIgnoreCase);
        var fallback = auto ? routing.DefaultModel : requested;
        if (!routing.Enabled)
            return new RouteDecision(requested, fallback, null, entities, [], auto ? "routing off: the default model" : "routing off");
        if (!auto && !routing.OverrideRequested)
            return new RouteDecision(requested, requested, null, entities, [], "the model asked is kept (overrideRequested off)");

        var installed = installedModels();
        var skipped = new List<string>();
        foreach (var rule in routing.Rules.OrderByDescending(r => r.Priority))
        {
            if (!rule.Matches(entities, out var matched))
                continue;
            if (installed is not null && !IsInstalled(installed, rule.Model))
            {
                skipped.Add($"{rule.Name} ({rule.Model} not installed)");
                continue;
            }
            var because = string.Join(", ", matched.Select(e => e.ToString()).Distinct().Take(6));
            return new RouteDecision(requested, rule.Model.Trim(), rule.Name, entities, matched,
                $"rule {rule.Name}: {because}" + (skipped.Count > 0 ? $" (skipped: {string.Join("; ", skipped)})" : ""));
        }
        var why = auto ? "no rule matches: the default model" : "no rule matches: the model asked";
        return new RouteDecision(requested, fallback, null, entities, [], skipped.Count > 0 ? $"{why} (skipped: {string.Join("; ", skipped)})" : why);
    }

    /// <summary>mistral-small3.2 is mistral-small3.2:latest for Ollama.</summary>
    public static bool IsInstalled(IReadOnlyCollection<string> installed, string model)
    {
        var name = model.Trim();
        return installed.Any(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase)
                                  || (!name.Contains(':') && string.Equals(m, name + ":latest", StringComparison.OrdinalIgnoreCase)));
    }
}
