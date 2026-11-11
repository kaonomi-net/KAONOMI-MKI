using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KAONOMI.LLMGateway.Entities;

/// <summary>
/// Finds the entities of a prompt with rules, without a model (a fraction of a millisecond): code (fenced blocks, lines
/// of code, stack traces) and the languages of programming, formulas, URLs, e-mails, IP addresses, dates and times,
/// amounts, percentages, phone numbers, numbers, file names, hashtags and mentions, people (a title before a title),
/// proper nouns, the names of the gazetteers of the settings (ORG, PRODUCT… any type), and the language of the prompt
/// (fr or en), and whether it holds a long and complex sentence (COMPLEX: many words with "si", "et", "if", "and"…).
/// An entity covers its text: a number inside a date or an amount is not a number again.
/// </summary>
public sealed partial class EntityExtractor
{
    private readonly List<(string Type, string Name, Regex Pattern)> _gazetteer = [];

    public EntityExtractor(IReadOnlyDictionary<string, List<string>>? gazetteers = null)
    {
        foreach (var (type, names) in gazetteers ?? new Dictionary<string, List<string>>())
            foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                _gazetteer.Add((type.Trim().ToUpperInvariant(), name.Trim(),
                    new Regex($@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(name.Trim())}(?![\p{{L}}\p{{N}}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
    }

    /// <summary>The types of the gazetteers.</summary>
    public IEnumerable<string> GazetteerTypes => _gazetteer.Select(g => g.Type).Distinct();

    /// <summary>The entities of <paramref title="text"/>, in the order of the text (the language last).</summary>
    public IReadOnlyList<Entity> Extract(string? text)
    {
        var found = new List<Entity>();
        if (string.IsNullOrWhiteSpace(text))
            return found;
        var covered = new bool[text.Length];

        // Code first: what is inside a block of code is not a date, a number or a title.
        foreach (Match m in FencedCode().Matches(text))
        {
            var language = m.Groups["lang"].Value.Trim().ToLowerInvariant();
            Add(found, covered, EntityTypes.Code, language.Length > 0 ? language : "code", m.Value, m.Index, m.Length, force: true);
        }
        foreach (Match m in StackTrace().Matches(text))
            Add(found, covered, EntityTypes.Code, "stacktrace", m.Value, m.Index, m.Length);
        foreach (Match m in CodeLine().Matches(text))
            Add(found, covered, EntityTypes.Code, GuessLanguage(m.Value), m.Value.Trim(), m.Index, m.Length);
        foreach (Match m in InlineCode().Matches(text))
            if (LooksLikeCode(m.Groups[1].Value))
                Add(found, covered, EntityTypes.Code, "inline", m.Value, m.Index, m.Length);

        foreach (Match m in Url().Matches(text))
            Add(found, covered, EntityTypes.Url, m.Value.TrimEnd('.', ',', ')', ';'), m.Value.TrimEnd('.', ',', ')', ';'), m.Index, m.Value.TrimEnd('.', ',', ')', ';').Length);
        foreach (Match m in Email().Matches(text))
            Add(found, covered, EntityTypes.Email, m.Value.ToLowerInvariant(), m.Value, m.Index, m.Length);
        foreach (Match m in Ip().Matches(text))
            if (m.Value.Split('.').All(part => int.Parse(part, CultureInfo.InvariantCulture) <= 255))
                Add(found, covered, EntityTypes.Ip, m.Value, m.Value, m.Index, m.Length);
        foreach (Match m in FileName().Matches(text))
            Add(found, covered, EntityTypes.File, m.Value, m.Value, m.Index, m.Length);
        foreach (Match m in Phone().Matches(text))
            if (m.Value.Count(char.IsDigit) is >= 9 and <= 15)
                Add(found, covered, EntityTypes.Phone, new string(m.Value.Where(c => char.IsDigit(c) || c == '+').ToArray()), m.Value, m.Index, m.Length);
        foreach (var pattern in new[] { IsoDate(), SlashDate(), WordDate() })
            foreach (Match m in pattern.Matches(text))
                Add(found, covered, EntityTypes.Date, m.Value, m.Value, m.Index, m.Length);
        foreach (Match m in Time().Matches(text))
            Add(found, covered, EntityTypes.Time, m.Value, m.Value, m.Index, m.Length);
        foreach (var pattern in new[] { MoneyBefore(), MoneyAfter() })
            foreach (Match m in pattern.Matches(text))
                Add(found, covered, EntityTypes.Money, m.Value, m.Value, m.Index, m.Length);
        foreach (Match m in Percent().Matches(text))
            Add(found, covered, EntityTypes.Percent, m.Value.Replace(" ", ""), m.Value, m.Index, m.Length);
        foreach (Match m in MathExpression().Matches(text))
            Add(found, covered, EntityTypes.Math, m.Value.Trim(), m.Value.Trim(), m.Index, m.Length);
        foreach (Match m in MathWord().Matches(text))
            Add(found, covered, EntityTypes.Math, m.Value.ToLowerInvariant(), m.Value, m.Index, m.Length);
        foreach (Match m in Hashtag().Matches(text))
            Add(found, covered, EntityTypes.Hashtag, m.Value, m.Value, m.Index, m.Length);
        foreach (Match m in Mention().Matches(text))
            Add(found, covered, EntityTypes.Mention, m.Value, m.Value, m.Index, m.Length);

        foreach (var (type, name, pattern) in _gazetteer)
            foreach (Match m in pattern.Matches(text))
                Add(found, covered, type, name, m.Value, m.Index, m.Length);
        foreach (Match m in ProgrammingLanguage().Matches(text))
            Add(found, covered, EntityTypes.ProgrammingLanguage, Canonical(m.Value), m.Value, m.Index, m.Length);
        foreach (Match m in TitledPerson().Matches(text))
            Add(found, covered, EntityTypes.Person, m.Groups["name"].Value, m.Value, m.Index, m.Length);
        foreach (Match m in ProperNoun().Matches(text))
            if (IsProperNoun(text, m))
                Add(found, covered, EntityTypes.ProperNoun, m.Value, m.Value, m.Index, m.Length);

        foreach (Match m in Number().Matches(text))
            Add(found, covered, EntityTypes.Number, m.Value.Replace(" ", "").Replace(',', '.'), m.Value, m.Index, m.Length);

        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (ComplexSentence(text) is { } complex)
            found.Add(new Entity(EntityTypes.Complex, "long", complex, 0, 0));
        if (DetectLanguage(text) is { } spoken)
            found.Add(new Entity(EntityTypes.Language, spoken, spoken, 0, 0));
        return found;
    }

    /// <summary>
    /// The entities that must be the same in two prompts for a cached answer (sorted TYPE:value, the values without
    /// case nor accents; a block of code by its whole text).
    /// </summary>
    public static string Signature(IEnumerable<Entity> entities) =>
        string.Join("|", entities.Where(e => EntityTypes.Guarded(e.Type))
            .Select(e => $"{e.Type}:{Normalize(e.Type == EntityTypes.Code ? e.Text : e.Value)}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>Lower case, without accents, the spaces collapsed.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space)
                builder.Append(' ');
            space = false;
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static void Add(List<Entity> found, bool[] covered, string type, string value, string text, int start, int length, bool force = false)
    {
        if (length <= 0)
            return;
        if (!force)
            for (var i = start; i < start + length && i < covered.Length; i++)
                if (covered[i])
                    return;
        for (var i = start; i < start + length && i < covered.Length; i++)
            covered[i] = true;
        found.Add(new Entity(type, value, text, start, length));
    }

    private static bool LooksLikeCode(string text) =>
        text.Length > 1 && (text.IndexOfAny(['(', ')', '{', '}', '[', ']', ';', '=', '<', '>', '.', '_', '/', '$']) >= 0 || text.Contains("::"));

    /// <summary>The language of a line of code, from its words.</summary>
    private static string GuessLanguage(string line)
    {
        var l = line.Trim();
        if (Regex.IsMatch(l, @"^(SELECT|INSERT|UPDATE|DELETE|CREATE|ALTER|WITH)\b", RegexOptions.IgnoreCase)) return "sql";
        if (Regex.IsMatch(l, @"^(def|import|from|class)\b.*:\s*$|^def\s|^from\s+\S+\s+import\s|print\(")) return "python";
        if (Regex.IsMatch(l, @"\b(public|private|internal|protected)\b.*\b(class|void|static|async|record)\b|\bvar\s+\w+\s*=|using\s+System|namespace\s+\w")) return "csharp";
        if (Regex.IsMatch(l, @"\b(function|const|let)\b|=>|console\.log")) return "javascript";
        if (Regex.IsMatch(l, @"^#include|std::")) return "cpp";
        if (Regex.IsMatch(l, @"^<\/?[a-z][\w-]*[\s>]")) return "html";
        if (Regex.IsMatch(l, @"^\$\s|^(sudo|cd|ls|git|npm|dotnet|pip|brew|curl)\s")) return "shell";
        return "code";
    }

    private static string Canonical(string language) => language.ToLowerInvariant() switch
    {
        "c#" or "csharp" or "c sharp" => "csharp",
        "c++" or "cpp" => "cpp",
        "js" or "javascript" => "javascript",
        "ts" or "typescript" => "typescript",
        "golang" or "go" => "go",
        var other => other
    };

    private static readonly HashSet<string> CommonCapitalised = new(StringComparer.OrdinalIgnoreCase)
    {
        "I", "Le", "La", "Les", "Un", "Une", "Des", "Je", "Tu", "Il", "Elle", "On", "Nous", "Vous", "Ils", "Elles", "Ce", "Cette",
        "The", "A", "An", "This", "That", "These", "Those", "It", "We", "You", "They", "He", "She", "My", "Your", "Our",
        "What", "Who", "Where", "When", "Why", "How", "Which", "Quel", "Quelle", "Quels", "Quelles", "Qui", "Que", "Quoi",
        "Où", "Quand", "Pourquoi", "Comment", "Combien", "Est", "Peux", "Pouvez", "Can", "Could", "Would", "Should", "Is",
        "Are", "Do", "Does", "Did", "Please", "Merci", "Bonjour", "Hello", "Hi", "Salut", "Oui", "Non", "Yes", "No"
    };

    /// <summary>
    /// A word with a capital, not at the start of a sentence (or at the start, when another one follows: "New York
    /// is…"), and not a word of every sentence (I, The, Le…).
    /// </summary>
    private static bool IsProperNoun(string text, Match m)
    {
        var words = m.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.All(CommonCapitalised.Contains))
            return false;
        var before = m.Index - 1;
        while (before >= 0 && char.IsWhiteSpace(text[before]) && text[before] != '\n')
            before--;
        var sentenceStart = before < 0 || text[before] is '.' or '!' or '?' or '\n' or ':' or '"' or '«' or '(' or '-' or '*';
        if (!sentenceStart)
            return true;
        return words.Length > 1 && !CommonCapitalised.Contains(words[0]);
    }

    private static readonly HashSet<string> French = new(StringComparer.OrdinalIgnoreCase)
    {
        "le", "la", "les", "un", "une", "des", "du", "de", "et", "est", "en", "que", "qui", "pour", "dans", "pas", "sur", "avec",
        "je", "tu", "il", "elle", "nous", "vous", "ils", "ce", "cette", "mais", "ou", "où", "quel", "quelle", "comment",
        "pourquoi", "combien", "au", "aux", "son", "sa", "ses", "mon", "ma", "mes", "très", "plus", "moi", "fait", "être"
    };

    private static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "is", "are", "of", "to", "in", "that", "it", "for", "on", "with", "as", "was", "be", "this",
        "what", "which", "who", "how", "why", "when", "where", "can", "could", "would", "should", "do", "does", "you", "i",
        "my", "your", "me", "please", "about", "from", "not", "have", "has", "will"
    };

    /// <summary>The words a sentence must have to be a long one.</summary>
    public const int ComplexMinWords = 18;

    /// <summary>
    /// The first long and complex sentence of the prompt (outside its code), null when it has none: at least
    /// <see cref="ComplexMinWords"/> words, with a condition or a reason ("si", "sinon", "donc", "parce que", "if",
    /// "because"…) and one more link, of that kind or a coordination ("et", "ou", "mais", "and", "or", "but").
    /// "Bonjour, comment vas-tu et que fais-tu ?" is not one; a long list joined by "et" alone is not one either.
    /// </summary>
    public static string? ComplexSentence(string text)
    {
        var prose = FencedCode().Replace(text, " ");
        foreach (var part in SentenceEnd().Split(prose))
        {
            var sentence = part.Trim();
            if (sentence.Length == 0 || Word().Matches(sentence).Count < ComplexMinWords)
                continue;
            var conditions = Condition().Matches(sentence).Count;
            if (conditions >= 1 && conditions + Coordination().Matches(sentence).Count >= 2)
                return sentence;
        }
        return null;
    }

    /// <summary>fr or en, from the small words of the prompt (outside its code); null when it cannot tell.</summary>
    public static string? DetectLanguage(string text)
    {
        var prose = FencedCode().Replace(text, " ");
        int fr = 0, en = 0;
        foreach (Match word in Word().Matches(prose))
        {
            if (French.Contains(word.Value)) fr++;
            if (English.Contains(word.Value)) en++;
        }
        if (prose.IndexOfAny(['é', 'è', 'ê', 'à', 'ç', 'ù', 'ô', 'î']) >= 0)
            fr++;
        if (fr == en)
            return null;
        return fr > en ? "fr" : "en";
    }

    [GeneratedRegex(@"```[ \t]*(?<lang>[\w#+\-.]*)[^\n]*\n[\s\S]*?(```|$)")]
    private static partial Regex FencedCode();

    [GeneratedRegex(@"`([^`\n]{2,200})`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"(?m)^(Traceback \(most recent call last\):[\s\S]*?(?=\n\s*\n|\z)|[ \t]*at [\w.<>`$]+\(.*\)( in .*:line \d+)?[ \t]*$|^\w+(\.\w+)*(Exception|Error): .+$)")]
    private static partial Regex StackTrace();

    [GeneratedRegex(@"(?m)^[ \t]*(?:(?:public|private|internal|protected|static|async)\s+[\w<>\[\],\s]+\(.*\)\s*\{?|(?:def|class)\s+\w+.*:|(?:function|const|let|var)\s+\w+\s*(?:=|\().*|#include\s*[<""].*|import\s+[\w.]+(?:\s+as\s+\w+)?;?|from\s+[\w.]+\s+import\s+.+|using\s+[\w.]+;|(?:SELECT|INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+.+\b(?:FROM|SET|VALUES|WHERE)\b.*|.*\)\s*\{\s*|.*;[ \t]*(?://.*)?|\s*\}\s*(?:else.*)?|<\/?[a-z][\w-]*(?:\s+[\w-]+=""[^""]*"")*\s*\/?>.*)[ \t]*$")]
    private static partial Regex CodeLine();

    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s<>""'`]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"\b[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+\b")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ip();

    [GeneratedRegex(@"(?<![\w/\\.])(?:[\w\-]+[/\\])*[\w\-]+\.(?:cs|csproj|sln|slnx|py|ipynb|js|mjs|ts|tsx|jsx|json|ya?ml|xml|md|txt|log|csv|tsv|xlsx?|docx?|pptx?|pdf|png|jpe?g|gif|svg|webp|mp3|wav|mp4|html?|css|scss|sql|sh|ps1|bat|go|rs|java|kt|swift|cpp|hpp|c|h|rb|php|gd|tscn|onnx|gguf|safetensors|zip|tar|gz)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileName();

    [GeneratedRegex(@"(?<![\w+])(?:\+\d{1,3}[\s.\-]?)?(?:\(\d{1,4}\)[\s.\-]?)?\d{1,4}(?:[\s.\-]\d{2,4}){2,5}(?![\w])")]
    private static partial Regex Phone();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2})?(?:Z|[+\-]\d{2}:?\d{2})?)?\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"\b\d{1,2}[/.]\d{1,2}[/.]\d{2,4}\b")]
    private static partial Regex SlashDate();

    [GeneratedRegex(@"\b(?:\d{1,2}(?:er)?\s+(?:janvier|février|fevrier|mars|avril|mai|juin|juillet|août|aout|septembre|octobre|novembre|décembre|decembre|january|february|march|april|may|june|july|august|september|october|november|december)(?:\s+\d{4})?|(?:january|february|march|april|may|june|july|august|september|october|november|december)\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s+\d{4})?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WordDate();

    [GeneratedRegex(@"\b(?:[01]?\d|2[0-3])(?::|h)[0-5]\d(?::[0-5]\d)?\b")]
    private static partial Regex Time();

    [GeneratedRegex(@"[$€£¥]\s?\d[\d\s,.]*\d?(?:\s?(?:k|M|bn|millions?|milliards?))?")]
    private static partial Regex MoneyBefore();

    [GeneratedRegex(@"\b\d[\d\s,.]*\s?(?:k|M|millions?|milliards?)?\s?(?:€|\$|£|¥|EUR|USD|GBP|CHF|JPY|euros?|dollars?|livres?)(?![\w])", RegexOptions.IgnoreCase)]
    private static partial Regex MoneyAfter();

    [GeneratedRegex(@"\b\d+(?:[.,]\d+)?\s?%")]
    private static partial Regex Percent();

    [GeneratedRegex(@"(?:[(√]?\s*-?\d+(?:[.,]\d+)?\s*[)²³]?\s*(?:[-+*/^×÷=<>≤≥]|\*\*)\s*)+[(√]?\s*-?\d+(?:[.,]\d+)?\s*[)²³]?|\b\d*[a-z]\s*[\^²³]\s*\d*\s*[-+]\s*[\d\w]+|\\(?:frac|sqrt|int|sum|lim)\b|[∫∑√∂∞≈≠]")]
    private static partial Regex MathExpression();

    [GeneratedRegex(@"\b(?:équations?|equations?|dérivées?|derivatives?|intégrales?|integrals?|matrices?|matrix|théorème|theorem|probabilités?|probability|logarithmes?|logarithms?|racine carrée|square root|factorielle|factorial|calcule[rz]?|calculate|compute|résous|résoudre|solve)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MathWord();

    [GeneratedRegex(@"(?<![\w&])#[\p{L}_][\p{L}\p{N}_]+")]
    private static partial Regex Hashtag();

    [GeneratedRegex(@"(?<![\w.])@[\w][\w.\-]*\w")]
    private static partial Regex Mention();

    [GeneratedRegex(@"(?<![\w#+])(?:C#|C\+\+|F#|Python|JavaScript|TypeScript|Java|Kotlin|Swift|Rust|Golang|Go(?=\s+(?:code|program|lang|language|module|routine))|Ruby|PHP|Perl|Scala|Haskell|Elixir|Erlang|Lua|Dart|SQL|PowerShell|Bash|GDScript|Objective-C|Assembly|COBOL|Fortran|MATLAB|Julia|HTML|CSS|YAML|JSON|regex|LINQ|\.NET|ASP\.NET|React|Angular|Vue|Node\.js|Django|Flask|Spring)(?![\w#+])", RegexOptions.IgnoreCase)]
    private static partial Regex ProgrammingLanguage();

    [GeneratedRegex(@"\b(?:M\.|Mr\.?|Mrs\.?|Ms\.?|Mme|Mlle|Dr\.?|Pr\.?|Prof\.?|Sir|Madame|Monsieur|Docteur|Maître)\s+(?<name>\p{Lu}[\p{L}'\-]+(?:\s+\p{Lu}[\p{L}'\-]+)*)")]
    private static partial Regex TitledPerson();

    [GeneratedRegex(@"\b\p{Lu}[\p{Ll}'\-]+(?:\s+(?:de|du|des|van|von|da|di|le|la|of|the)?\s?\p{Lu}[\p{Ll}'\-]+)*\b|\b\p{Lu}{2,}[\p{Lu}\d]*\b")]
    private static partial Regex ProperNoun();

    [GeneratedRegex(@"(?<![\w.,])-?\d+(?:[.,]\d+)?(?![\w])")]
    private static partial Regex Number();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<=[.!?…])\s+|\n+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?:si|s['’]ils?|sinon|lorsque|lorsqu|quand|parce\s+qu|puisqu|puisque|car|donc|alors|bien\s+qu|tandis\s+qu|à\s+condition|pourvu\s+qu|sauf\s+si|if|unless|otherwise|when|whenever|because|since|then|therefore|although|though|whereas|while|provided\s+that)(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase)]
    private static partial Regex Condition();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?:et|ou|mais|ni|and|or|but|nor)(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase)]
    private static partial Regex Coordination();
}
