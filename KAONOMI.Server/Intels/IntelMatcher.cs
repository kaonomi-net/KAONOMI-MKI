using System.Globalization;
using System.Text;
using KAONOMI.Core;
using KAONOMI.Core.Data;
using KAONOMI.Core.Models;

namespace KAONOMI.Server.Intels
{
    /// <summary>An intel that matched a text: its score, the included entities found and those missing.</summary>
    public sealed record IntelMatch(Intel Intel, double Score, List<IntelEntityMatch> Found, List<IntelEntityMatch> Missing);

    /// <summary>What the intels gave for a text: the matches, the intels looked at and why the others did not match.</summary>
    public sealed class IntelMatchResult
    {
        public List<IntelMatch> matches { get; } = [];

        /// <summary>One line per intel that did not match (its score, an excluded entity found…), for the logs.</summary>
        public List<string> notMatched { get; } = [];

        public List<string> warnings { get; } = [];

        public int intels { get; set; }
    }

    /// <summary>
    /// The intels of the search engine (PUT /intel) applied to a text. An intel matches when none of its excluded
    /// entities is in the text and its score reaches its trigger level: the score is the weights of its included
    /// entities found in the text over the weights of all its included entities, in percent (a weight that is not a
    /// number, or none, counts 1). An entity is found when its words are in the text as whole words, case and accents
    /// ignored; for a fuzzy intel each word may also differ by a typo (one letter, two from eight letters; never for a
    /// word of three letters or less, and the first letter always the same). The intels are read again every five minutes.
    /// </summary>
    public sealed class IntelMatcher
    {
        private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);
        private static readonly object Gate = new();
        private static IntelMatcher? _shared;

        private readonly Config _config;
        private List<Intel> _intels = [];
        private string? _error;
        private DateTime _read = DateTime.MinValue;

        private IntelMatcher(Config config) => _config = config;

        /// <summary>The matcher of the server, made at the first call.</summary>
        public static IntelMatcher Get(Config config)
        {
            lock (Gate)
                return _shared ??= new IntelMatcher(config);
        }

        /// <summary>The intels that match <paramref name="text"/>.</summary>
        public IntelMatchResult Match(string? text)
        {
            var result = new IntelMatchResult();
            var (intels, error) = Intels();
            result.intels = intels.Count;
            if (error is not null)
                result.warnings.Add(error);
            if (string.IsNullOrWhiteSpace(text))
                return result;

            var tokens = Tokens(text);
            foreach (var intel in intels)
            {
                var included = (intel.IncludedEntities ?? []).Where(e => !string.IsNullOrWhiteSpace(e?.EntityName)).ToList();
                if (included.Count == 0)
                    continue;
                var name = string.IsNullOrWhiteSpace(intel.title) ? intel.id : intel.title;

                // An excluded entity in the text: the intel does not match, whatever its score.
                var excluded = (intel.ExcludedEntities ?? []).Where(e => !string.IsNullOrWhiteSpace(e?.EntityName))
                    .Select(e => (e.EntityName, Text: Find(tokens, text, e.EntityName, intel.isFuzzy)))
                    .FirstOrDefault(e => e.Text is not null);
                if (excluded.Text is not null)
                {
                    result.notMatched.Add($"{name}: excluded entity '{excluded.EntityName}' found ('{excluded.Text}')");
                    continue;
                }

                var found = new List<IntelEntityMatch>();
                var missing = new List<IntelEntityMatch>();
                foreach (var entity in included)
                {
                    var match = new IntelEntityMatch { entityName = entity.EntityName.Trim(), weight = Weight(entity.EntityWeight) };
                    match.text = Find(tokens, text, entity.EntityName, intel.isFuzzy);
                    (match.text is null ? missing : found).Add(match);
                }
                var total = found.Sum(e => e.weight) + missing.Sum(e => e.weight);
                var score = total <= 0 ? (found.Count > 0 ? 100 : 0) : Math.Round(100 * found.Sum(e => e.weight) / total, 1);
                if (found.Count > 0 && score >= intel.TriggerLevel)
                    result.matches.Add(new IntelMatch(intel, score, found, missing));
                else
                    result.notMatched.Add($"{name}: score {score} % < {intel.TriggerLevel} %" + (found.Count == 0 ? " (no entity found)" : string.Empty));
            }
            return result;
        }

        /// <summary>The weight of an entity: its number (a comma as decimal point is read too), or 1 when it is none or not a number.</summary>
        private static double Weight(string? weight) =>
            double.TryParse((weight ?? string.Empty).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0
                ? value
                : 1;

        // ── Finding an entity in the text ──

        /// <summary>A word of the text: its folded form (upper case, no accent), and where it is.</summary>
        private readonly record struct Token(string Folded, int Start, int End);

        /// <summary>The words of <paramref name="text"/> (letters and digits).</summary>
        private static List<Token> Tokens(string text)
        {
            var tokens = new List<Token>();
            var start = -1;
            for (var i = 0; i <= text.Length; i++)
            {
                var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
                if (inWord && start < 0)
                    start = i;
                else if (!inWord && start >= 0)
                {
                    tokens.Add(new Token(Fold(text[start..i]), start, i));
                    start = -1;
                }
            }
            return tokens;
        }

        /// <summary>What is written in the text for the entity <paramref name="name"/> (its words in a row), or null.</summary>
        private static string? Find(List<Token> tokens, string text, string name, bool fuzzy)
        {
            var words = Tokens(name).Select(t => t.Folded).ToList();
            if (words.Count == 0 || words.Count > tokens.Count)
                return null;
            for (var i = 0; i + words.Count <= tokens.Count; i++)
            {
                var all = true;
                for (var j = 0; j < words.Count && all; j++)
                    all = Same(tokens[i + j].Folded, words[j], fuzzy);
                if (all)
                    return text[tokens[i].Start..tokens[i + words.Count - 1].End];
            }
            return null;
        }

        /// <summary>True when two folded words are the same, or close enough for a fuzzy intel.</summary>
        private static bool Same(string a, string b, bool fuzzy)
        {
            if (a == b)
                return true;
            // A typo keeps the first letter: without it, "production" would be "traduction" (two letters apart).
            if (!fuzzy || Math.Min(a.Length, b.Length) <= 3 || a[0] != b[0])
                return false;
            var allowed = Math.Max(a.Length, b.Length) >= 8 ? 2 : 1;
            return Math.Abs(a.Length - b.Length) <= allowed && Distance(a, b) <= allowed;
        }

        /// <summary>The number of letters to change, add or remove to go from one word to the other.</summary>
        private static int Distance(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
                previous[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                (previous, current) = (current, previous);
            }
            return previous[b.Length];
        }

        /// <summary>The word in upper case, without its accents.</summary>
        private static string Fold(string word)
        {
            var builder = new StringBuilder(word.Length);
            foreach (var c in word.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    builder.Append(char.ToUpperInvariant(c));
            return builder.ToString();
        }

        /// <summary>The intels not deleted, read again when older than five minutes (kept as they were when the search engine does not answer).</summary>
        private (List<Intel>, string?) Intels()
        {
            lock (Gate)
            {
                if (DateTime.UtcNow - _read > Refresh)
                {
                    _read = DateTime.UtcNow;
                    try
                    {
                        _intels = new SessionManager(_config).IntelsLast(ElasticRepository.MaxResults).Where(i => !i.isDeleted).ToList();
                        _error = null;
                    }
                    catch (Exception ex)
                    {
                        _error = $"The intels could not be read: {ex.Message}";
                    }
                }
                return (_intels, _error);
            }
        }
    }
}
