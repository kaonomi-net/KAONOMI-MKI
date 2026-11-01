using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Cuts an answer into the pieces shown one after the other by the simulated streaming, each with the time it takes to
/// say it aloud: a word lasts its number of syllables times the syllable duration, punctuation adds a pause (short
/// after , ; :, longer after . ! ? and at the end of a line or paragraph). Code blocks are not read: they appear line
/// by line, quickly.
/// </summary>
public static partial class ReadingPace
{
    /// <summary>Delay between two lines of a code block (code is not read aloud).</summary>
    private static readonly TimeSpan CodeLineDelay = TimeSpan.FromMilliseconds(35);

    private const string Vowels = "aeiouyàâäáãåéèêëíìîïóòôöõúùûüœæ";

    /// <summary>
    /// Pieces of <paramref name="text"/> (their concatenation is the whole text) and the pause after each one, for a
    /// reading speed of <paramref name="syllable"/> per syllable.
    /// </summary>
    public static IEnumerable<(string Piece, TimeSpan Delay)> Split(string text, TimeSpan syllable)
    {
        var lines = text.Split('\n');
        var inCode = false;
        for (var l = 0; l < lines.Length; l++)
        {
            var line = lines[l];
            var newLine = l < lines.Length - 1 ? "\n" : string.Empty;
            var isFence = line.TrimStart().StartsWith("```", StringComparison.Ordinal);

            if (inCode || isFence)
            {
                if (isFence)
                    inCode = !inCode;
                yield return (line + newLine, CodeLineDelay);
                continue;
            }

            var words = WordPattern().Matches(line).Select(m => m.Value).ToList();
            var leading = line.Length - line.TrimStart().Length;
            if (words.Count == 0)
            {
                // Empty line: paragraph break.
                yield return (line + newLine, newLine.Length > 0 ? syllable * 2 : TimeSpan.Zero);
                continue;
            }

            for (var w = 0; w < words.Count; w++)
            {
                var piece = (w == 0 ? line[..leading] : string.Empty) + words[w] + (w == words.Count - 1 ? newLine : string.Empty);
                var delay = syllable * Syllables(words[w]) + Pause(words[w], syllable);
                if (w == words.Count - 1 && newLine.Length > 0)
                    delay += syllable * 1.5;
                yield return (piece, delay);
            }
        }
    }

    /// <summary>Estimated number of spoken syllables of a word (vowel groups; digits, symbols and URLs approximated).</summary>
    internal static double Syllables(string word)
    {
        var core = word.Trim().Trim('*', '_', '`', '#', '>', '|', '"', '\'', '(', ')', '[', ']', '«', '»', '.', ',', ';', ':', '!', '?');
        if (core.Length == 0)
            return 0;

        if (core.Contains("://", StringComparison.Ordinal))
            return Math.Min(8, core.Length / 6.0);

        var lower = core.ToLowerInvariant();
        var digits = lower.Count(char.IsDigit);
        var groups = 0;
        var previousVowel = false;
        foreach (var c in lower)
        {
            var vowel = Vowels.Contains(c);
            if (vowel && !previousVowel)
                groups++;
            previousVowel = vowel;
        }

        // Silent final e / es / ent (French) or e (English): "phrase", "mots", "parlent", "make".
        if (groups > 1 && (lower.EndsWith('e') || lower.EndsWith("es", StringComparison.Ordinal) || lower.EndsWith("ent", StringComparison.Ordinal)))
            groups--;

        // Digits are read one by one (bounded), symbols-only tokens are short.
        var syllables = groups + Math.Min(6, digits);
        return syllables == 0 ? 0.5 : syllables;
    }

    /// <summary>Pause after the punctuation that ends a word.</summary>
    private static TimeSpan Pause(string word, TimeSpan syllable)
    {
        var end = word.TrimEnd().TrimEnd('*', '_', '`', '"', '\'', ')', ']', '»');
        if (end.Length == 0)
            return TimeSpan.Zero;
        return end[^1] switch
        {
            '.' or '!' or '?' or '…' => syllable * 2.5,
            ',' or ';' or ':' => syllable * 1.2,
            _ => TimeSpan.Zero
        };
    }

    /// <summary>A word with the spaces that follow it.</summary>
    [GeneratedRegex(@"\S+\s*")]
    private static partial Regex WordPattern();
}
