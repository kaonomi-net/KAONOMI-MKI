using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Counts the occurrences of each term of a text. A term is a word made of letters and digits (accents included);
/// hyphenated words stay whole ("pot-au-feu"), apostrophes separate words ("qu'est" → "qu", "est"), numbers are
/// terms too ("2026", "3,14"). Terms are compared in lower case.
/// </summary>
public static partial class TermCounter
{
    /// <summary>Each term of <paramref name="text"/> (lower case) with its number of occurrences, most frequent first.</summary>
    public static IReadOnlyList<(string Term, int Count)> Count(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return Term().Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .GroupBy(term => term, StringComparer.Ordinal)
            .Select(g => (Term: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Term, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The <c>wordCounts</c> of a completion: one entry per term of the prompt (<c>storedIn</c> = PROMPT), then one
    /// per term of the completion (COMPLETION), each with its number of occurrences.
    /// </summary>
    public static List<Models.WordCountByTerm> WordCounts(string? prompt, string? completion) =>
        Count(prompt).Select(t => new Models.WordCountByTerm { Term = t.Term, Count = t.Count, StoredIn = Models.WordCountByTerm.Prompt })
            .Concat(Count(completion).Select(t => new Models.WordCountByTerm { Term = t.Term, Count = t.Count, StoredIn = Models.WordCountByTerm.Completion }))
            .ToList();

    /// <summary>A word (letters, combining marks, digits, inner hyphens) or a number (with an inner . or ,).</summary>
    [GeneratedRegex(@"\p{L}[\p{L}\p{Mn}\p{Nd}]*(?:-[\p{L}\p{Nd}][\p{L}\p{Mn}\p{Nd}]*)*|\p{Nd}+(?:[.,]\p{Nd}+)*")]
    private static partial Regex Term();
}
