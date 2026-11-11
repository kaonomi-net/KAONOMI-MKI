using System.Text.RegularExpressions;

namespace KAONOMI.LLMGateway.Cache;

/// <summary>
/// The question of a prompt, as the cache compares it. The chat does not send the question alone: it addresses it to
/// its preset ("KAONOMI please how do I…") and, when it sends the previous exchanges, writes them before it
/// ("Discussions précédentes : … Nouvelle question de l'utilisateur : …"). Compared as a whole, such a prompt is never
/// close to a phrase written by hand or imported ("how do I…"), and its name is taken for an entity. So the cache
/// keeps and compares the question alone; the previous exchanges go into the context (an answer the model gave after
/// them is given back after the same ones only, while a phrase imported for every context answers anywhere).
/// </summary>
public static partial class CacheQuestion
{
    private const string Discussions = "Discussions précédentes";
    private const string NewQuestion = "Nouvelle question de l'utilisateur :";

    /// <summary>A name in upper case then "please": "KAONOMI please ", "JARVIS please ", "HAL 9000 please ".</summary>
    [GeneratedRegex(@"^\s*[\p{Lu}\p{N}][\p{Lu}\p{N} _\-'.]{1,40}\s+please\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Addressed();

    /// <summary>
    /// The question of <paramref name="prompt"/> (without the name it is addressed to), and the previous exchanges
    /// written before it (null when there are none).
    /// </summary>
    public static (string Question, string? History) Of(string? prompt)
    {
        var text = prompt ?? "";
        string? history = null;
        if (text.TrimStart().StartsWith(Discussions, StringComparison.Ordinal))
        {
            var at = text.LastIndexOf(NewQuestion, StringComparison.Ordinal);
            if (at >= 0)
            {
                history = text[..at].Trim();
                text = text[(at + NewQuestion.Length)..];
            }
        }
        var question = Addressed().Replace(text, "", 1).Trim();
        // A prompt that is only a name and "please" stays as it is.
        return (question.Length == 0 ? text.Trim() : question, history);
    }
}
