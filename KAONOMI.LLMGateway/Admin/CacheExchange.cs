using KAONOMI.LLMGateway.Cache;
using KAONOMI.LLMGateway.Entities;
using KAONOMI.LLMGateway.Proxy;

namespace KAONOMI.LLMGateway.Admin;

/// <summary>
/// The file of the phrases of the cache, exported by <c>GET /gateway/cache/export</c> and read by
/// <c>POST /gateway/cache/import</c>: each phrase, a prompt with its possible answers. A phrase exported keeps its model
/// and its context (the hash of its system prompt and history), to be imported as it was. A phrase written by hand
/// needs its prompt and its answers only: without a model it holds for every model, and without a context (or a system
/// prompt to make one) for every context. The embedding and the entities are made again at the import.
/// </summary>
public sealed class CacheExchange
{
    public const string FormatName = "kaonomi-llmgateway-cache";

    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    public DateTimeOffset? Exported { get; set; }
    public List<Phrase> Phrases { get; set; } = [];

    public sealed class Phrase
    {
        public string Prompt { get; set; } = "";
        /// <summary>The model; empty or * for every model.</summary>
        public string? Model { get; set; }
        /// <summary>The hash of the context (from an export); empty for the one of <see cref="System"/>, or every context.</summary>
        public string? Context { get; set; }
        /// <summary>The system prompt the phrase follows (used when there is no <see cref="Context"/>).</summary>
        public string? System { get; set; }
        public List<string> Answers { get; set; } = [];
    }

    /// <summary>What an import did.</summary>
    public sealed record Result(int Phrases, int Added, int Skipped, List<string> Errors);

    /// <summary>
    /// Adds the answers of <paramref name="document"/> to the cache; an answer already kept for the same prompt, model
    /// and context is skipped.
    /// </summary>
    public static async Task<Result> ImportAsync(Gateway gateway, CacheExchange document, CancellationToken cancellationToken)
    {
        int added = 0, skipped = 0;
        var errors = new List<string>();
        if (!string.IsNullOrEmpty(document.Format) && document.Format != FormatName)
            errors.Add($"format {document.Format} read as {FormatName}");
        var index = 0;
        foreach (var phrase in document.Phrases)
        {
            index++;
            var prompt = CacheQuestion.Of(phrase.Prompt).Question;
            var answers = (phrase.Answers ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToList();
            if (prompt.Length == 0 || answers.Count == 0)
            {
                errors.Add($"phrase {index}: {(prompt.Length == 0 ? "no prompt" : "no answer")}");
                continue;
            }
            var model = string.IsNullOrWhiteSpace(phrase.Model) ? SemanticCache.Any : phrase.Model.Trim();
            var context = !string.IsNullOrWhiteSpace(phrase.Context) ? phrase.Context.Trim()
                : !string.IsNullOrWhiteSpace(phrase.System) ? SemanticCache.ContextKey(LlmRequest.ContextPartsFor(phrase.System))
                : SemanticCache.Any;
            var (embedder, vector) = await gateway.EmbedAsync(prompt, cancellationToken);
            var signature = EntityExtractor.Signature(gateway.Extractor.Extract(prompt));
            foreach (var answer in answers)
            {
                if (gateway.Cache.Contains(model, context, prompt, answer))
                {
                    skipped++;
                    continue;
                }
                gateway.Cache.Add(new CacheEntry
                {
                    Model = model, Context = context, System = phrase.System, Prompt = prompt, Answer = answer, EntitySignature = signature,
                    Embedder = embedder.Name, Vector = vector
                });
                added++;
            }
        }
        Console.WriteLine($"[cache] import: {document.Phrases.Count} phrase(s), {added} answer(s) added, {skipped} already kept");
        return new Result(document.Phrases.Count, added, skipped, errors);
    }
}
