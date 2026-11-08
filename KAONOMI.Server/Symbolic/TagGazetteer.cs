using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KAONOMI.Core;
using KAONOMI.Core.Models;

namespace KAONOMI.Server.Symbolic
{
    /// <summary>A tag declared in the search engine and found in a text: the tag, where it is declared, what was written.</summary>
    public sealed record RecognizedTag(string Tag, string Source, List<string> MatchedTexts);

    /// <summary>
    /// The tags declared in the indices <c>symbolicpattern</c> and <c>ontology</c> (#IA, #GUERRE, #SALON-IA…), looked for
    /// in a text as a hashtag (<c>#IA</c>) or as words (« IA », « guerre », « salon ia » or « salon-ia »): whole words,
    /// case and accents ignored. The tags are read again every five minutes; a tag of one letter is left out (it would
    /// match everywhere).
    /// </summary>
    public sealed class TagGazetteer
    {
        public const string SourceSymbolicPattern = "symbolicpattern";
        public const string SourceOntology = "ontology";

        private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
        private static readonly object Gate = new();
        private static TagGazetteer? _shared;

        private readonly Config _config;
        private List<(string Tag, string Source, Regex Regex)> _tags = [];
        private string? _error;
        private DateTime _read = DateTime.MinValue;

        private TagGazetteer(Config config) => _config = config;

        /// <summary>The gazetteer of the server, made at the first call.</summary>
        public static TagGazetteer Get(Config config)
        {
            lock (Gate)
                return _shared ??= new TagGazetteer(config);
        }

        /// <summary>The declared tags found in <paramref title="text"/>, and the warnings (the tags could not be read…).</summary>
        public (List<RecognizedTag> Tags, List<string> Warnings, int Declared) Find(string? text)
        {
            var (tags, error) = Tags();
            var warnings = error is null ? new List<string>() : new List<string> { error };
            var found = new List<RecognizedTag>();
            if (string.IsNullOrWhiteSpace(text))
                return (found, warnings, tags.Count);

            // The text without its accents, of the same length, so that the places found are those of the text itself.
            var folded = Fold(text);
            foreach (var (tag, source, regex) in tags)
            {
                try
                {
                    var matches = regex.Matches(folded).Select(m => text.Substring(m.Index, m.Length)).Distinct().ToList();
                    if (matches.Count > 0)
                        found.Add(new RecognizedTag(tag, source, matches));
                }
                catch (RegexMatchTimeoutException)
                {
                    warnings.Add($"Looking for the tag {tag} took too long and was skipped");
                }
            }
            return (found, warnings, tags.Count);
        }

        /// <summary>The declared tags, read again when older than five minutes (kept as they were when the search engine does not answer).</summary>
        private (List<(string, string, Regex)>, string?) Tags()
        {
            lock (Gate)
            {
                if (DateTime.UtcNow - _read > Refresh)
                {
                    _read = DateTime.UtcNow;
                    try
                    {
                        var session = new SessionManager(_config);
                        var sources = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                        void Add(IEnumerable<string>? declared, string source)
                        {
                            foreach (var tag in TagRules.NormalizeTags(declared))
                            {
                                if (!sources.TryGetValue(tag, out var set))
                                    sources[tag] = set = new SortedSet<string>(StringComparer.Ordinal);
                                set.Add(source);
                            }
                        }
                        foreach (var pattern in session.SymbolicPatternsAll())
                            Add(pattern.Tags, SourceSymbolicPattern);
                        foreach (var node in session.OntologiesAll())
                            if (!node.isDeleted)
                                Add(node.Tags, SourceOntology);

                        _tags = sources
                            .Select(s => (Tag: s.Key, Source: string.Join('+', s.Value), Words: Fold(s.Key[1..]).Split('-', StringSplitOptions.RemoveEmptyEntries)))
                            .Where(t => t.Words.Length > 0 && string.Concat(t.Words).Length > 1)
                            .Select(t => (t.Tag, t.Source, new Regex(
                                @"(?<![\w-])#?" + string.Join(@"[\s_-]+", t.Words.Select(Regex.Escape)) + @"(?![\w-])",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout)))
                            .ToList();
                        _error = null;
                    }
                    catch (Exception ex)
                    {
                        _error = $"The declared tags could not be read: {ex.Message}";
                    }
                }
                return (_tags, _error);
            }
        }

        /// <summary>The text with each accented letter replaced by its plain letter, one character for one (the length is kept).</summary>
        private static string Fold(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
                builder.Append(decomposed.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(decomposed[0]) != UnicodeCategory.NonSpacingMark ? decomposed[0] : c);
            }
            return builder.ToString();
        }
    }
}
