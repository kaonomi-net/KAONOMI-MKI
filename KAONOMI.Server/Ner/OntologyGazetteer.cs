using System.Globalization;
using System.Text;
using KAONOMI.Core.Models;

namespace KAONOMI.Server.Ner
{
    /// <summary>
    /// The names of the ontology as a dictionary: every entity of it (not a class, not deleted, with a type), by its
    /// name and its synonyms, found in a text whatever the case and the accents ("jerome fortias" is "Jérôme Fortias"),
    /// the longest name first ("Banque de France" before "France"), on whole words only. Built from the ontology given,
    /// again whenever it is given anew.
    /// </summary>
    public sealed class OntologyGazetteer
    {
        /// <summary>A name holds at most so many words.</summary>
        private const int MaxWords = 8;

        /// <summary>The names by their key (words without accents, in lower case, one space between them).</summary>
        private readonly Dictionary<string, (string Type, string Id, string Name)> _names = new(StringComparer.Ordinal);

        public OntologyGazetteer(IEnumerable<Ontology> ontology)
        {
            foreach (var entity in ontology)
            {
                if (entity.isDeleted || entity.isOntologyClass || string.IsNullOrWhiteSpace(entity.EntityType) || string.IsNullOrWhiteSpace(entity.name))
                    continue;
                var type = entity.EntityType.Trim().ToUpperInvariant();
                foreach (var name in (entity.synonyms ?? []).Prepend(entity.name))
                {
                    var key = Key(name);
                    // Too short or only digits: it would be found everywhere.
                    if (key.Length < 3 || key.All(c => char.IsDigit(c) || c == ' '))
                        continue;
                    _names.TryAdd(key, (type, entity.id, entity.name.Trim()));
                }
                Count++;
            }
            Types = _names.Values.Select(v => v.Type).Distinct().OrderBy(t => t).ToList();
        }

        /// <summary>The entities of the ontology the dictionary holds.</summary>
        public int Count { get; }

        /// <summary>The types of those entities.</summary>
        public IReadOnlyList<string> Types { get; }

        /// <summary>The names of the ontology found in <paramref name="text"/>, in the order of the text.</summary>
        public List<NerEntity> Find(string text)
        {
            var found = new List<NerEntity>();
            if (_names.Count == 0 || string.IsNullOrEmpty(text))
                return found;
            var words = Words(text);
            for (var i = 0; i < words.Count;)
            {
                var matched = 0;
                for (var n = Math.Min(MaxWords, words.Count - i); n >= 1 && matched == 0; n--)
                {
                    var key = string.Join(" ", words.Skip(i).Take(n).Select(w => w.Key));
                    if (!_names.TryGetValue(key, out var entity))
                        continue;
                    var start = words[i].Start;
                    var end = words[i + n - 1].End;
                    found.Add(new NerEntity { text = text[start..end], type = entity.Type, start = start, end = end, score = 1, source = "ontology", ontologyId = entity.Id, ontologyName = entity.Name });
                    matched = n;
                }
                i += Math.Max(1, matched);
            }
            return found;
        }

        /// <summary>The words of a text (letters and digits, with the - and ' inside a word) and where they are.</summary>
        private static List<(string Key, int Start, int End)> Words(string text)
        {
            var words = new List<(string, int, int)>();
            var i = 0;
            while (i < text.Length)
            {
                if (!char.IsLetterOrDigit(text[i]))
                {
                    i++;
                    continue;
                }
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || ((text[i] is '-' or '\'' or '’') && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]))))
                    i++;
                words.Add((Fold(text[start..i]), start, i));
            }
            return words;
        }

        /// <summary>The key of a name: its words without accents, in lower case, one space between them.</summary>
        private static string Key(string name) => string.Join(" ", Words(name).Select(w => w.Key));

        /// <summary>A word without its accents, in lower case ("Jérôme" → "jerome"), the ’ as '.</summary>
        private static string Fold(string word)
        {
            var decomposed = word.Replace('’', '\'').Normalize(NormalizationForm.FormD);
            var folded = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    folded.Append(char.ToLowerInvariant(c));
            return folded.ToString();
        }
    }
}
