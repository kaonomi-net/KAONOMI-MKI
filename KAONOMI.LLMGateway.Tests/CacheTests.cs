using KAONOMI.LLMGateway.Cache;
using KAONOMI.LLMGateway.Configuration;
using KAONOMI.LLMGateway.Entities;

namespace KAONOMI.LLMGateway.Tests;

public class CacheTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("llmgateway-cache-").FullName;
    private readonly CacheSettings _settings = new() { LexicalThreshold = 0.8, MaxEntries = 100, TtlHours = 1, MinAnswers = 1 };
    private readonly EntityExtractor _extractor = new();
    private readonly LexicalEmbedder _embedder = new();

    private CacheEntry Entry(string prompt, string answer, string model = "m", string context = "c") => new()
    {
        Model = model, Context = context, Prompt = prompt, Answer = answer, Embedder = _embedder.Name,
        Vector = LexicalEmbedder.Embed(prompt), EntitySignature = EntityExtractor.Signature(_extractor.Extract(prompt))
    };

    private CacheLookup Find(SemanticCache cache, string prompt, string model = "m", string context = "c") =>
        cache.Find(model, context, _embedder.Name, LexicalEmbedder.Embed(prompt), EntityExtractor.Signature(_extractor.Extract(prompt)));

    [Theory]
    [InlineData("What is the capital of France?", "what's the capital of France ?")]
    [InlineData("Quelle est la capitale de la France ?", "quelle est la capitale de la France")]
    [InlineData("Explain the difference between a process and a thread", "Please explain the difference between a thread and a process")]
    public void A_close_prompt_gets_the_cached_answer(string asked, string again)
    {
        using var cache = new SemanticCache(() => _settings, null);
        cache.Add(Entry(asked, "answer"));
        var lookup = Find(cache, again);
        Assert.NotNull(lookup.Hit);
        Assert.Equal(1, cache.Hits);
    }

    [Theory]
    [InlineData("What is the capital of France?", "What is the capital of Spain?")]
    [InlineData("Combien font 2 + 2 ?", "Combien font 2 + 3 ?")]
    [InlineData("Convert 100 USD to EUR", "Convert 250 USD to EUR")]
    public void Other_entities_get_no_cached_answer(string asked, string other)
    {
        using var cache = new SemanticCache(() => _settings, null);
        cache.Add(Entry(asked, "answer"));
        var lookup = Find(cache, other);
        Assert.Null(lookup.Hit);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public void Another_prompt_another_model_or_another_context_is_a_miss()
    {
        using var cache = new SemanticCache(() => _settings, null);
        cache.Add(Entry("Write a haiku about autumn", "leaves"));
        Assert.Null(Find(cache, "How do I bake bread?").Hit);
        Assert.Null(Find(cache, "Write a haiku about autumn", model: "other").Hit);
        Assert.Null(Find(cache, "Write a haiku about autumn", context: "another history").Hit);
        Assert.NotNull(Find(cache, "Write a haiku about autumn").Hit);
    }

    [Fact]
    public void Old_entries_expire_and_the_least_used_go_past_the_maximum()
    {
        _settings.MaxEntries = 2;
        using var cache = new SemanticCache(() => _settings, null);
        var old = Entry("first question about cats", "a");
        old.Created = DateTimeOffset.UtcNow.AddHours(-2);
        cache.Add(old);
        Assert.Null(Find(cache, "first question about cats").Hit);
        cache.Add(Entry("second question about dogs", "b"));
        cache.Add(Entry("third question about birds", "c"));
        cache.Add(Entry("fourth question about fish", "d"));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void The_entries_are_kept_in_a_file()
    {
        var file = Path.Combine(_folder, "cache.json");
        using (var cache = new SemanticCache(() => _settings, file))
            cache.Add(Entry("What is the capital of France?", "Paris"));
        using var again = new SemanticCache(() => _settings, file);
        Assert.Equal("Paris", Find(again, "What is the capital of France?").Hit?.Answer);
    }

    [Fact]
    public void With_three_answers_needed_the_cache_waits_for_them_then_varies()
    {
        _settings.MinAnswers = 3;
        using var cache = new SemanticCache(() => _settings, null);
        cache.Add(Entry("Tell me a joke about cats", "joke 1"));
        cache.Add(Entry("tell me a joke about cats", "joke 2"));
        var waiting = Find(cache, "Tell me a joke about cats!");
        Assert.Null(waiting.Hit);
        Assert.Equal(2, waiting.Similar);
        Assert.Contains("2/3", waiting.Reason);

        cache.Add(Entry("Tell me a joke about cats", "joke 3"));
        var given = new List<string>();
        for (var i = 0; i < 60; i++)
            given.Add(Find(cache, "Tell me a joke about cats").Hit!.Answer);
        Assert.Equal(["joke 1", "joke 2", "joke 3"], given.Distinct().Order());
        // Never the same answer twice in a row.
        Assert.All(given.Zip(given.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact]
    public void An_answer_given_twice_by_the_model_is_not_given_back_twice_in_a_row()
    {
        _settings.MinAnswers = 3;
        using var cache = new SemanticCache(() => _settings, null);
        cache.Add(Entry("Name a red cat", "Rusty"));
        cache.Add(Entry("Name a red cat", "Rusty"));
        cache.Add(Entry("Name a red cat", "Ginger"));
        var given = Enumerable.Range(0, 30).Select(_ => Find(cache, "Name a red cat").Hit!.Answer).ToList();
        Assert.All(given.Zip(given.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact]
    public void The_answers_of_another_prompt_do_not_count()
    {
        _settings.MinAnswers = 2;
        using var cache = new SemanticCache(() => _settings, null);
        cache.Add(Entry("Tell me a joke about cats", "joke"));
        cache.Add(Entry("Tell me a joke about dogs", "joke"));
        Assert.Null(Find(cache, "Tell me a joke about cats").Hit);
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
