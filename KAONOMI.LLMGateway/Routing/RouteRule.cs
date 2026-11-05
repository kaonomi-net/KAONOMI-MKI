using KAONOMI.LLMGateway.Entities;

namespace KAONOMI.LLMGateway.Routing;

/// <summary>
/// A rule of the router: when the entities of the prompt match it, the request goes to <see cref="Model"/>. An entity
/// is named by its type (CODE) or by its type and value (LANGUAGE:fr, CODE:python). The rule matches when the prompt
/// holds at least <see cref="MinCount"/> entities of <see cref="Any"/> (when it is not empty), every one of
/// <see cref="All"/>, and none of <see cref="None"/>. The rules are tried by <see cref="Priority"/>, the highest first.
/// </summary>
public sealed class RouteRule
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Any { get; set; } = [];
    public List<string> All { get; set; } = [];
    public List<string> None { get; set; } = [];
    public int MinCount { get; set; } = 1;
    public string Model { get; set; } = "";
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>True when <paramref name="entities"/> match the rule; <paramref name="matched"/> are the entities that made it.</summary>
    public bool Matches(IReadOnlyList<Entity> entities, out List<Entity> matched)
    {
        matched = [];
        if (!Enabled || string.IsNullOrWhiteSpace(Model) || (Any.Count == 0 && All.Count == 0))
            return false;
        foreach (var selector in None)
            if (entities.Any(e => Selects(selector, e)))
                return false;
        foreach (var selector in All)
        {
            var hit = entities.FirstOrDefault(e => Selects(selector, e));
            if (hit is null)
                return false;
            matched.Add(hit);
        }
        if (Any.Count > 0)
        {
            var hits = entities.Where(e => Any.Any(selector => Selects(selector, e))).ToList();
            if (hits.Count < Math.Max(1, MinCount))
                return false;
            matched.AddRange(hits);
        }
        matched = matched.Distinct().ToList();
        return true;
    }

    /// <summary>TYPE selects every entity of that type; TYPE:value only the ones of that value (case ignored); * any.</summary>
    public static bool Selects(string selector, Entity entity)
    {
        var s = selector.Trim();
        if (s == "*")
            return true;
        var colon = s.IndexOf(':');
        if (colon < 0)
            return string.Equals(s, entity.Type, StringComparison.OrdinalIgnoreCase);
        return string.Equals(s[..colon].Trim(), entity.Type, StringComparison.OrdinalIgnoreCase)
               && string.Equals(EntityExtractor.Normalize(s[(colon + 1)..]), EntityExtractor.Normalize(entity.Value), StringComparison.Ordinal);
    }
}
