using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using KAONOMI.LLMGateway.Entities;

namespace KAONOMI.LLMGateway.Cache;

/// <summary>Turns a text into a vector of unit length, to compare prompts by their cosine.</summary>
public interface IEmbedder
{
    /// <summary>The name kept with each entry: entries made by another embedder are never compared.</summary>
    string Name { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// A local embedding of the words, without a model: the words (without case nor accents), the pairs of words and the
/// trigrams of letters hashed into 1024 dimensions. It finds the same question asked with other words around it, a
/// typo, another order; not a synonym (an embedding model of Ollama does that).
/// </summary>
public sealed partial class LexicalEmbedder : IEmbedder
{
    private const int Dimensions = 1024;

    public string Name => "lexical-1024";

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) => Task.FromResult(Embed(text));

    public static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        var words = Word().Matches(EntityExtractor.Normalize(text)).Select(m => m.Value).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var weight = StopWords.Contains(word) ? 0.25f : 1f;
            AddFeature(vector, "w:" + word, weight);
            if (i + 1 < words.Count)
                AddFeature(vector, "b:" + word + " " + words[i + 1], 0.5f * weight);
            var padded = $"^{word}$";
            for (var j = 0; j + 3 <= padded.Length; j++)
                AddFeature(vector, "t:" + padded.Substring(j, 3), 0.25f * weight);
        }
        Normalize(vector);
        return vector;
    }

    private static void AddFeature(float[] vector, string feature, float weight)
    {
        // FNV-1a, stable from one run to the next (string.GetHashCode is not).
        var hash = 2166136261u;
        foreach (var c in feature)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        var index = (int)(hash % Dimensions);
        vector[index] += (hash & 0x80000000) != 0 ? -weight : weight;
    }

    internal static void Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm <= 0)
            return;
        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
    }

    private static readonly HashSet<string> StopWords =
    [
        "le", "la", "les", "un", "une", "des", "du", "de", "d", "l", "et", "est", "en", "que", "qu", "qui", "pour", "dans", "sur",
        "avec", "je", "j", "tu", "il", "elle", "nous", "vous", "ce", "c", "cette", "me", "m", "te", "se", "s", "a", "au", "aux",
        "the", "a", "an", "and", "is", "are", "of", "to", "in", "that", "it", "for", "on", "with", "as", "be", "this", "do",
        "does", "i", "you", "me", "my", "please", "can", "could", "would", "s"
    ];

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();
}

/// <summary>
/// An embedding model of LM Studio, through the API of OpenAI (<c>POST /v1/embeddings</c>), the vector made of unit
/// length. <paramref name="upstream"/> is the address without /v1.
/// </summary>
public sealed class OpenAiEmbedder(HttpClient http, string upstream, string model) : IEmbedder
{
    public string Name => "openai:" + model;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"{upstream.TrimEnd('/')}/v1/embeddings", new { model, input = text }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var first = document.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var vector = new float[first.GetArrayLength()];
        var i = 0;
        foreach (var value in first.EnumerateArray())
            vector[i++] = value.GetSingle();
        LexicalEmbedder.Normalize(vector);
        return vector;
    }
}

/// <summary>An embedding model of Ollama (<c>POST /api/embed</c>), the vector made of unit length.</summary>
public sealed class OllamaEmbedder(HttpClient http, string upstream, string model) : IEmbedder
{
    public string Name => "ollama:" + model;

    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"{upstream.TrimEnd('/')}/api/embed", new { model, input = text, truncate = true }, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var first = document.RootElement.GetProperty("embeddings")[0];
        var vector = new float[first.GetArrayLength()];
        var i = 0;
        foreach (var value in first.EnumerateArray())
            vector[i++] = value.GetSingle();
        LexicalEmbedder.Normalize(vector);
        return vector;
    }
}
