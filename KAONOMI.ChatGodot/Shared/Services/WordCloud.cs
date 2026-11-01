using System;
using System.Collections.Generic;
using System.Linq;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Terms of a word cloud: the most frequent terms of a set of texts (counted with <see cref="TermCounter"/>), without
/// the French, English, German and Dutch stop words (articles, pronouns, auxiliaries, prepositions…), numbers and terms shorter than
/// three letters.
/// </summary>
public static class WordCloud
{
    /// <summary>The <paramref name="count"/> most frequent terms of <paramref name="texts"/>, most frequent first.</summary>
    public static IReadOnlyList<(string Term, int Count)> TopTerms(IEnumerable<string?> texts, int count)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var text in texts)
            foreach (var (term, occurrences) in TermCounter.Count(text))
                if (term.Length >= 3 && !char.IsDigit(term[0]) && !StopWords.Contains(term))
                    totals[term] = totals.GetValueOrDefault(term) + occurrences;
        return totals.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal)
            .Take(count).Select(t => (t.Key, t.Value)).ToList();
    }

    /// <summary>Words too common to say anything about the texts (lower case, accents kept).</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // French
        "les", "des", "une", "est", "son", "sa", "ses", "aux", "par", "pour", "dans", "sur", "sous", "avec", "sans", "chez",
        "entre", "vers", "contre", "depuis", "pendant", "avant", "après", "comme", "mais", "donc", "car", "que", "qui",
        "quoi", "dont", "où", "quand", "quel", "quelle", "quels", "quelles", "lequel", "laquelle", "lesquels", "cette",
        "cet", "ces", "ceux", "celle", "celles", "celui", "ceci", "cela", "ça", "leur", "leurs", "notre", "nos", "votre",
        "vos", "mon", "ton", "mes", "tes", "nous", "vous", "ils", "elles", "elle", "lui", "eux", "moi", "toi", "soi",
        "même", "mêmes", "aussi", "très", "plus", "moins", "peu", "beaucoup", "trop", "tout", "tous", "toute", "toutes",
        "rien", "chaque", "autre", "autres", "encore", "déjà", "ainsi", "alors", "puis", "ensuite", "enfin", "bien",
        "non", "oui", "pas", "personne", "jamais", "toujours", "souvent", "parfois", "ici", "être", "été", "étant",
        "suis", "es", "sommes", "êtes", "sont", "était", "étaient", "sera", "seront", "serait", "soit", "avoir", "ayant",
        "ai", "as", "avons", "avez", "ont", "avait", "avaient", "aura", "auront", "aurait", "eut", "faire", "fait",
        "font", "faut", "peut", "peuvent", "pouvez", "pouvoir", "doit", "doivent", "selon", "lors", "afin", "chacun",
        "plusieurs", "certains", "certaines", "quelque", "quelques", "via", "etc", "voici", "voilà", "ceux-ci",
        "celle-ci", "celui-ci", "c'est", "est-ce", "n'est", "qu'il", "qu'elle", "s'il", "jusqu", "lorsque", "puisque",
        "parce", "tant", "tel", "telle", "tels", "telles", "vôtre", "notamment", "également", "comment",
        "pourquoi", "combien", "voulez", "veux", "veut", "existe", "exemple", "exemples", "vais", "vas", "allons", "allez",
        "vont", "peux", "pouvons", "pourrait", "pourriez", "dois", "devez", "aller",
        // English
        "the", "and", "for", "are", "but", "not", "you", "your", "yours", "all", "any", "can", "had", "her", "was", "one",
        "our", "out", "has", "have", "him", "his", "how", "its", "may", "new", "now", "old", "see", "two", "way", "who",
        "did", "get", "let", "say", "she", "too", "use", "that", "with", "this", "from", "they", "them", "then", "than",
        "there", "their", "these", "those", "what", "when", "where", "which", "while", "will", "would", "could", "should",
        "been", "being", "were", "into", "onto", "about", "after", "before", "also", "more", "most", "some", "such",
        "only", "other", "each", "very", "just", "here", "over", "under", "does", "doing", "done", "make", "made", "many",
        "much", "because", "between", "through", "during", "without", "within", "why", "whom", "whose", "yes",
        // German
        "die", "der", "das", "den", "dem", "des", "und", "ist", "sind", "ein", "eine", "einen", "einem", "einer", "eines",
        "nicht", "mit", "für", "von", "auf", "aus", "bei", "nach", "über", "auch", "wie", "was", "wer", "wird", "werden",
        "kann", "können", "sich", "sie", "ihr", "ihre", "wir", "ich", "dass", "oder", "aber", "wenn", "noch", "nur",
        "sehr", "mehr", "hat", "haben", "sein", "zum", "zur", "vom", "beim", "diese", "dieser", "dieses",
        // Dutch
        "het", "een", "van", "voor", "met", "niet", "zijn", "wordt", "worden", "ook", "dat", "dit", "deze", "die",
        "aan", "als", "bij", "door", "naar", "nog", "maar", "wat", "hoe", "kan", "kunnen", "hun", "zij", "wij", "jij",
        "heeft", "hebben", "meer", "zeer", "uit", "over", "tot", "om",
    };
}
