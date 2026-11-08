using System.Globalization;
using System.Text;
using KAONOMI.Core.Models;

namespace KAONOMI.Server.Ner
{
    /// <summary>
    /// The names of the ontology as a dictionary: every entity of it (not a class, not deleted, with a type), by its
    /// title and its synonyms, found in a text whatever the case and the accents ("jerome fortias" is "Jérôme Fortias"),
    /// the longest title first ("Banque de France" before "France"), on whole words only. Built from the ontology given,
    /// again whenever it is given anew.
    /// <para>
    /// Three rules keep the common words of the language from being taken for entities:
    /// a <b>sigle</b> (a name in capitals of six letters or less: DES, IA, UE) is found only written in capitals, so
    /// « des » is not the algorithm DES; a <b>word of the language</b> alone (des, est, les, the…, see
    /// <see cref="CommonWords"/>) is never an entity; a <b>common noun</b> alone (a one-word name written without
    /// capital in the ontology: « rôle », « prix », « candidats ») is kept only for the concrete types (materials,
    /// software, hardware: « lithium », « bitcoin »); and a one-word name of a general type written with a capital
    /// (« Concurrence », « Commission ») is found only written with a capital. The names of several words are always
    /// kept. The elided article or pronoun is not part of a word (« l'IA » holds « IA », « d'Ursula » « Ursula »).
    /// </para>
    /// </summary>
    public sealed class OntologyGazetteer
    {
        /// <summary>A title holds at most so many words.</summary>
        private const int MaxWords = 8;

        /// <summary>A name in capitals of at most so many letters is a sigle, found only written in capitals.</summary>
        private const int MaxSigleLength = 6;

        /// <summary>
        /// The concrete types, for which a common noun alone is an entity (« lithium », « bitcoin », « drones »); for every
        /// other type it would be found in every text.
        /// </summary>
        private static readonly HashSet<string> ConcreteTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "MATERIAL", "SOFTWARE", "HARDWARE"
        };

        /// <summary>
        /// The general types, for which a one-word name with a capital (« Concurrence », « Commission ») is found only
        /// written with a capital: « la concurrence » is a word, « la Commission » the institution.
        /// </summary>
        private static readonly HashSet<string> GeneralTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "DOMAIN", "PROFESSION", "EVENT", "LAW", "ORGANISATION", "ORG", "NORP", "LOCATION", "LOC", "PROGRAMME", "MISC", "PORTFOLIO", "PRODUCT"
        };

        /// <summary>
        /// Words of the language (French and English: articles, pronouns, prepositions, conjunctions, frequent verbs and
        /// adverbs) that are never an entity alone, whatever the ontology holds (« est » is not the East, « des » not DES).
        /// </summary>
        public static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
        {
            // French
            "le", "la", "les", "un", "une", "des", "du", "de", "au", "aux", "et", "ou", "ni", "mais", "donc", "or", "car",
            "est", "sont", "etait", "etaient", "ete", "etre", "sera", "seront", "serait", "avoir", "ont", "avait", "aura",
            "fait", "faire", "font", "peut", "peuvent", "doit", "doivent", "va", "vont", "dit", "dire", "voir", "mis",
            "il", "elle", "ils", "elles", "on", "nous", "vous", "je", "tu", "lui", "leur", "leurs", "eux", "moi", "toi",
            "ce", "cet", "cette", "ces", "ceci", "cela", "ca", "se", "sa", "son", "ses", "mon", "ma", "mes", "ton", "ta", "tes",
            "notre", "nos", "votre", "vos", "qui", "que", "quoi", "dont", "quel", "quelle", "quels", "quelles", "lequel",
            "en", "dans", "par", "pour", "sur", "sous", "avec", "sans", "entre", "vers", "chez", "contre", "selon", "depuis",
            "pendant", "avant", "apres", "lors", "durant", "parmi", "malgre", "via", "hors", "jusqu",
            "plus", "moins", "tres", "trop", "peu", "tout", "tous", "toute", "toutes", "aussi", "bien", "meme", "memes",
            "autre", "autres", "comme", "si", "ne", "pas", "non", "oui", "deja", "encore", "toujours", "jamais", "ici",
            "alors", "ainsi", "puis", "enfin", "cependant", "pourtant", "notamment", "egalement", "seulement", "certains",
            "certaines", "chaque", "plusieurs", "aucun", "aucune", "nouveau", "nouvelle", "nouveaux", "nouvelles", "grand",
            "grande", "grands", "grandes", "premier", "premiere", "dernier", "derniere", "cas", "fois", "part", "lieu",
            "sein", "ans", "an", "annee", "annees", "jour", "jours", "mois", "partie", "fin", "debut", "suite", "place",
            // English
            "the", "a", "an", "of", "to", "in", "on", "at", "by", "for", "with", "from", "and", "or", "but", "not", "no",
            "is", "are", "was", "were", "be", "been", "being", "has", "have", "had", "will", "would", "can", "could",
            "should", "may", "might", "must", "do", "does", "did", "this", "that", "these", "those", "it", "its", "as",
            "he", "she", "they", "we", "you", "his", "her", "their", "our", "your", "who", "which", "what", "when",
            "where", "how", "all", "any", "some", "more", "most", "other", "into", "over", "after", "before", "about",
            "new", "first", "last", "also", "only", "than", "then", "there", "here", "so", "if", "up", "out"
        };

        /// <summary>The names by their key (words without accents, in lower case, one space between them).</summary>
        private readonly Dictionary<string, List<Name>> _names = new(StringComparer.Ordinal);

        /// <summary>A name of the ontology: its type, its entity, its title, and whether it is a sigle.</summary>
        private sealed record Name(string Type, string Id, string Title, bool IsSigle, bool NeedsCapital);

        public OntologyGazetteer(IEnumerable<Ontology> ontology)
        {
            foreach (var entity in ontology)
            {
                if (entity.isDeleted || entity.isOntologyClass || string.IsNullOrWhiteSpace(entity.EntityType) || string.IsNullOrWhiteSpace(entity.name))
                    continue;
                var type = entity.EntityType.Trim().ToUpperInvariant();
                foreach (var name in (entity.synonyms ?? []).Prepend(entity.name))
                {
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    var key = Key(name);
                    // Too short or only digits: it would be found everywhere.
                    if (key.Length < 3 || key.All(c => char.IsDigit(c) || c == ' '))
                        continue;

                    var oneWord = !key.Contains(' ');
                    var isSigle = IsSigle(name);
                    if (oneWord && !isSigle)
                    {
                        // A word of the language, or a common noun of a general type: found in every text.
                        if (CommonWords.Contains(key))
                            continue;
                        if (!name.Any(char.IsUpper) && !ConcreteTypes.Contains(type))
                            continue;
                    }

                    if (!_names.TryGetValue(key, out var list))
                        _names[key] = list = [];
                    var needsCapital = oneWord && !isSigle && GeneralTypes.Contains(type) && char.IsUpper(name.Trim()[0]);
                    if (!list.Any(n => n.Id == entity.id && n.IsSigle == isSigle))
                        list.Add(new Name(type, entity.id, entity.name.Trim(), isSigle, needsCapital));
                }
                Count++;
            }
            Types = _names.Values.SelectMany(v => v).Select(v => v.Type).Distinct().OrderBy(t => t).ToList();
        }

        /// <summary>The entities of the ontology the dictionary holds.</summary>
        public int Count { get; }

        /// <summary>The types of those entities.</summary>
        public IReadOnlyList<string> Types { get; }

        /// <summary>The names of the ontology found in <paramref title="text"/>, in the order of the text.</summary>
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
                    var start = words[i].Start;
                    // The word whole first (« L'Oréal »), then without its elided article or pronoun (« l'Union » → « Union »).
                    if (!_names.TryGetValue(key, out var candidates) && words[i].Elision > 0)
                    {
                        key = key[words[i].Elision..];
                        start += words[i].Elision;
                        _names.TryGetValue(key, out candidates);
                    }
                    if (candidates is null)
                        continue;
                    var end = words[i + n - 1].End;
                    var written = text[start..end];
                    // A sigle is found only written in capitals: « des » is not DES, « ia » in a word list is not IA.
                    // A one-word name of a general type with a capital is found only written with a capital.
                    var entity = candidates.FirstOrDefault(c =>
                        (!c.IsSigle || !written.Any(char.IsLower)) && (!c.NeedsCapital || char.IsUpper(written[0])));
                    if (entity is null)
                        continue;
                    found.Add(new NerEntity { text = written, type = entity.Type, start = start, end = end, score = 1, source = "ontology", ontologyId = entity.Id, ontologyName = entity.Title });
                    matched = n;
                }
                i += Math.Max(1, matched);
            }
            return found;
        }

        /// <summary>A name in capitals (digits allowed) of at most <see cref="MaxSigleLength"/> letters, one word: DES, IA, UE, G7.</summary>
        private static bool IsSigle(string name)
        {
            var trimmed = name.Trim();
            var letters = trimmed.Count(char.IsLetter);
            return letters > 0 && letters <= MaxSigleLength && !trimmed.Any(char.IsWhiteSpace) && !trimmed.Any(char.IsLower);
        }

        /// <summary>The words of a text (letters and digits, with the - and ' inside a word) and where they are.</summary>
        private static List<(string Key, int Start, int End, int Elision)> Words(string text)
        {
            var words = new List<(string, int, int, int)>();
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
                words.Add((Fold(text[start..i]), start, i, ElisionLength(text, start, i)));
            }
            return words;
        }

        private static readonly string[] Elisions = { "jusqu", "lorsqu", "puisqu", "quoiqu", "qu", "l", "d", "j", "n", "s", "c", "m", "t" };

        /// <summary>The length of an elision at the start of the word text[start..end] (« l' », « qu' »…), or 0.</summary>
        private static int ElisionLength(string text, int start, int end)
        {
            foreach (var elision in Elisions)
            {
                var apostrophe = start + elision.Length;
                if (apostrophe + 1 < end && text[apostrophe] is '\'' or '’'
                    && string.Compare(text, start, elision, 0, elision.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    return elision.Length + 1;
            }
            return 0;
        }

        /// <summary>The key of a title: its words without accents, in lower case, one space between them.</summary>
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
