namespace KAONOMI.LLMGateway;

/// <summary>A request seen by the gateway, for /gateway/log and the window /gateway of the chat.</summary>
public sealed record RequestRecord(
    DateTimeOffset Time, string Endpoint, string RequestedModel, string Model, string? Rule, string Reason,
    IReadOnlyList<string> Entities, string Cache, double Score, string CacheReason, long Ms, int Status, string Prompt);

/// <summary>The last requests (a ring of <c>logSize</c>), and how many each rule routed.</summary>
public sealed class RequestLog(Func<int> size)
{
    private readonly LinkedList<RequestRecord> _records = new();
    private readonly Dictionary<string, long> _ruleHits = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public long Total { get; private set; }

    /// <summary>A prompt as the log keeps it: on one line, its first 160 characters.</summary>
    public static string Excerpt(string prompt)
    {
        var line = prompt.ReplaceLineEndings(" ");
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    public void Add(RequestRecord record)
    {
        lock (_lock)
        {
            Total++;
            if (record.Rule is { } rule)
                _ruleHits[rule] = _ruleHits.GetValueOrDefault(rule) + 1;
            _records.AddFirst(record);
            while (_records.Count > Math.Max(10, size()))
                _records.RemoveLast();
        }
    }

    public IReadOnlyList<RequestRecord> Last(int take)
    {
        lock (_lock)
            return _records.Take(Math.Max(1, take)).ToList();
    }

    public IReadOnlyDictionary<string, long> RuleHits()
    {
        lock (_lock)
            return new Dictionary<string, long>(_ruleHits, StringComparer.OrdinalIgnoreCase);
    }
}
