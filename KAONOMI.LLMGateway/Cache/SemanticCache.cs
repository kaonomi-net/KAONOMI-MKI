using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KAONOMI.LLMGateway.Configuration;

namespace KAONOMI.LLMGateway.Cache;

/// <summary>An answer kept by the cache.</summary>
public sealed class CacheEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>The model that answered (after the router); <see cref="SemanticCache.Any"/> for every model (an import).</summary>
    public string Model { get; set; } = "";
    /// <summary>
    /// The hash of what comes before the last prompt: the system prompt, the history, the format asked;
    /// <see cref="SemanticCache.Any"/> for every context (an import that names none).
    /// </summary>
    public string Context { get; set; } = "";
    /// <summary>The system prompt of the request, to read the context (the history is in the hash only).</summary>
    public string? System { get; set; }
    /// <summary>The last prompt of the user, as asked.</summary>
    public string Prompt { get; set; } = "";
    /// <summary>The entities that must be the same (<see cref="Entities.EntityExtractor.Signature"/>).</summary>
    public string EntitySignature { get; set; } = "";
    public string Embedder { get; set; } = "";
    public float[] Vector { get; set; } = [];
    public string Answer { get; set; } = "";
    /// <summary>The thinking of the model, when it gave one.</summary>
    public string? Thinking { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastHit { get; set; } = DateTimeOffset.UtcNow;
    public int Hits { get; set; }
    /// <summary>How long the model took to answer, in milliseconds: the time saved by each hit.</summary>
    public long AnswerMs { get; set; }
}

/// <summary>An answer of a phrase of the cache: its text, how many times it was given back, its entries.</summary>
public sealed record CacheAnswer(string Text, int Hits, IReadOnlyList<string> Ids);

/// <summary>A phrase of the cache: a prompt, its model and context, and the answers kept for it.</summary>
public sealed record CachePhrase(
    string Prompt, string Model, string Context, string? System, IReadOnlyList<CacheAnswer> Answers, int Hits, DateTimeOffset LastHit);

/// <summary>
/// What a lookup found: the entry given back (null on a miss), the closest one seen with its score, and how many
/// answers are kept for a prompt that close (<see cref="Similar"/>, of the <see cref="Needed"/> before the cache answers).
/// </summary>
public sealed record CacheLookup(CacheEntry? Hit, double BestScore, CacheEntry? Closest, string Reason, int Similar = 0, int Needed = 1);

/// <summary>
/// The semantic cache: the answers of the model are kept for the prompts close to each other (the cosine of their
/// embeddings at least the threshold), by the same model, after the same system prompt and history, and holding the
/// same entities (numbers, dates, names… when the entity guard is on). Once <c>minAnswers</c> answers are kept for a
/// prompt that close (3 by default), the cache answers it, with one of them drawn at random (not the one it gave last),
/// so that its answers vary; until then the prompt goes to the model and its answer is kept with the others. The
/// entries expire after the TTL; past the maximum, the ones used the longest ago go. They are kept in cache.json,
/// written a few seconds after a change.
/// </summary>
public sealed class SemanticCache : IDisposable
{
    /// <summary>The model or the context of an entry that holds for every one.</summary>
    public const string Any = "*";

    private readonly Func<CacheSettings> _settings;
    private readonly string? _file;
    private readonly List<CacheEntry> _entries = [];
    private readonly object _lock = new();
    private readonly Timer? _saver;
    private bool _dirty;

    public SemanticCache(Func<CacheSettings> settings, string? file)
    {
        _settings = settings;
        _file = file;
        if (file is not null && File.Exists(file))
        {
            try
            {
                _entries.AddRange(JsonSerializer.Deserialize<List<CacheEntry>>(File.ReadAllText(file), GatewaySettings.Json) ?? []);
            }
            catch (JsonException)
            {
                File.Move(file, file + ".broken", overwrite: true);
            }
        }
        if (file is not null)
            _saver = new Timer(_ => Save(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long SavedMs { get; private set; }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>The hash of a context (the parts joined by a separator that no text holds).</summary>
    public static string ContextKey(params string?[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("␞", parts.Select(p => p ?? ""))));
        return Convert.ToHexString(bytes, 0, 12);
    }

    public CacheLookup Find(string model, string context, string embedder, float[] vector, string signature, bool count = true)
    {
        var settings = _settings();
        var threshold = embedder.StartsWith("lexical", StringComparison.Ordinal) ? settings.LexicalThreshold : settings.Threshold;
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            Expire(settings, now);
            var needed = Math.Max(1, settings.MinAnswers);
            CacheEntry? closest = null;
            var closestScore = double.MinValue;
            var similar = new List<(CacheEntry Entry, double Score)>();
            foreach (var entry in _entries)
            {
                if (entry.Embedder != embedder || entry.Vector.Length != vector.Length)
                    continue;
                var score = Cosine(entry.Vector, vector);
                var sameScope = (entry.Model == Any || string.Equals(entry.Model, model, StringComparison.OrdinalIgnoreCase))
                                && (entry.Context == Any || entry.Context == context);
                if (sameScope && score > closestScore)
                {
                    closest = entry;
                    closestScore = score;
                }
                if (!sameScope || (settings.EntityGuard && entry.EntitySignature != signature) || score < threshold)
                    continue;
                similar.Add((entry, score));
            }
            if (similar.Count >= needed)
            {
                // The answers vary: one of them at random, but not the text given last when there is another one (the
                // model may have given the same answer twice), else not the same entry.
                var last = similar.Where(s => s.Entry.Hits > 0).OrderByDescending(s => s.Entry.LastHit).Select(s => s.Entry).FirstOrDefault();
                var pool = last is null ? similar
                    : similar.Where(s => s.Entry.Answer != last.Answer).ToList() is { Count: > 0 } other ? other
                    : similar.Count > 1 ? similar.Where(s => s.Entry != last).ToList()
                    : similar;
                var (hit, score) = pool[Random.Shared.Next(pool.Count)];
                if (count)
                {
                    hit.Hits++;
                    hit.LastHit = now;
                    Hits++;
                    SavedMs += hit.AnswerMs;
                    _dirty = true;
                }
                return new CacheLookup(hit, score, hit,
                    $"hit {score:0.000} ≥ {threshold:0.00}, one of {similar.Count} answer(s) at random", similar.Count, needed);
            }
            if (count)
                Misses++;
            var reason = similar.Count > 0 ? $"{similar.Count}/{needed} answer(s) kept for this prompt: the model answers once more"
                : closest is null ? "no prompt of this model and context yet"
                : closestScore >= threshold ? $"closest {closestScore:0.000}, but its entities differ"
                : $"closest {closestScore:0.000} < {threshold:0.00}";
            var best = similar.Count > 0 ? similar.Max(s => s.Score) : closest is null ? 0 : closestScore;
            return new CacheLookup(null, best, closest, reason, similar.Count, needed);
        }
    }

    public CacheEntry Add(CacheEntry entry)
    {
        var settings = _settings();
        lock (_lock)
        {
            // The same prompt asked again is kept as one more answer: the cache answers once it has minAnswers of them.
            _entries.Add(entry);
            var over = _entries.Count - Math.Max(1, settings.MaxEntries);
            if (over > 0)
                foreach (var old in _entries.OrderBy(e => e.LastHit).Take(over).ToList())
                    _entries.Remove(old);
            _dirty = true;
        }
        return entry;
    }

    public IReadOnlyList<CacheEntry> List(int take, string? search = null)
    {
        lock (_lock)
            return _entries
                .Where(e => string.IsNullOrWhiteSpace(search) || e.Prompt.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || e.Answer.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Model.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.LastHit).Take(Math.Max(1, take)).ToList();
    }

    /// <summary>
    /// The phrases of the cache: its entries gathered by prompt (the same words, without case nor accents), model and
    /// context, each with its answers (the same text once), the most recently used first.
    /// </summary>
    public IReadOnlyList<CachePhrase> Phrases(string? search, int take)
    {
        lock (_lock)
            return _entries
                .GroupBy(e => (Key: Entities.EntityExtractor.Normalize(e.Prompt), Model: e.Model.ToLowerInvariant(), e.Context))
                .Select(g =>
                {
                    var entries = g.OrderByDescending(e => e.LastHit).ToList();
                    return new CachePhrase(entries[0].Prompt, entries[0].Model, g.Key.Context, entries.Select(e => e.System).FirstOrDefault(s => s is not null),
                        entries.GroupBy(e => e.Answer).Select(a => new CacheAnswer(a.Key, a.Sum(e => e.Hits), a.Select(e => e.Id).ToList())).ToList(),
                        entries.Sum(e => e.Hits), entries[0].LastHit);
                })
                .Where(p => string.IsNullOrWhiteSpace(search) || p.Prompt.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || p.Model.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || p.Answers.Any(a => a.Text.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(p => p.LastHit)
                .Take(Math.Max(1, take))
                .ToList();
    }

    /// <summary>True when this answer is already kept for this prompt, model and context.</summary>
    public bool Contains(string model, string context, string prompt, string answer)
    {
        lock (_lock)
            return _entries.Any(e => string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase) && e.Context == context
                                     && e.Prompt == prompt && e.Answer == answer);
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var removed = _entries.RemoveAll(e => e.Id == id) > 0;
            _dirty |= removed;
            return removed;
        }
    }

    public int Clear()
    {
        lock (_lock)
        {
            var count = _entries.Count;
            _entries.Clear();
            Hits = Misses = SavedMs = 0;
            _dirty = true;
            return count;
        }
    }

    public void Save()
    {
        if (_file is null)
            return;
        string json;
        lock (_lock)
        {
            if (!_dirty)
                return;
            _dirty = false;
            json = JsonSerializer.Serialize(_entries, GatewaySettings.Json);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _file, overwrite: true);
    }

    private void Expire(CacheSettings settings, DateTimeOffset now)
    {
        if (settings.TtlHours <= 0)
            return;
        var limit = now - TimeSpan.FromHours(settings.TtlHours);
        if (_entries.RemoveAll(e => e.Created < limit) > 0)
            _dirty = true;
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }

    public void Dispose()
    {
        _saver?.Dispose();
        Save();
    }
}
