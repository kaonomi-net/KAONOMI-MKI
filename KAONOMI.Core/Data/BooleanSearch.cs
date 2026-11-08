using System.Text.RegularExpressions;

namespace KAONOMI.Core.Data
{
    /// <summary>
    /// A search expression with logical operators: <c>and</c>, <c>or</c> (in any case) and parentheses, e.g.
    /// <c>recette and (sauce or soupe)</c>. <c>and</c> binds tighter than <c>or</c>; the words between two operators make
    /// one term (searched as today: all its words, fuzzy, or as a part of the text); a "quoted text" is one term even
    /// with <c>and</c> / <c>or</c> inside. A missing closing parenthesis is accepted, an extra one ignored.
    /// </summary>
    public abstract record BooleanSearch
    {
        /// <summary>One term to find.</summary>
        public sealed record Term(string Text) : BooleanSearch;

        /// <summary>All the parts must match.</summary>
        public sealed record And(IReadOnlyList<BooleanSearch> Parts) : BooleanSearch;

        /// <summary>At least one of the parts must match.</summary>
        public sealed record Or(IReadOnlyList<BooleanSearch> Parts) : BooleanSearch;

        private static readonly Regex Tokens = new("\"(?<quoted>[^\"]*)\"?|(?<paren>[()])|(?<word>[^\\s()\"]+)", RegexOptions.Compiled);

        /// <summary>True when <paramref title="text"/> uses an operator or a parenthesis (otherwise it is one plain term).</summary>
        public static bool HasOperators(string? text) =>
            !string.IsNullOrWhiteSpace(text) && Tokens.Matches(text).Any(m => m.Groups["paren"].Success || IsOperator(m.Groups["word"].Value));

        /// <summary>The expression of <paramref title="text"/> (null when it has no term at all).</summary>
        public static BooleanSearch? Parse(string? text)
        {
            var tokens = Tokens.Matches(text ?? string.Empty).Select(m =>
                m.Groups["quoted"].Success ? (Kind: "term", Text: m.Groups["quoted"].Value.Trim())
                : m.Groups["paren"].Success ? (Kind: m.Value, Text: m.Value)
                : IsOperator(m.Value) ? (Kind: m.Value.ToLowerInvariant(), Text: m.Value)
                : (Kind: "word", Text: m.Value)).ToList();
            var position = 0;
            return ParseOr(tokens, ref position);
        }

        private static bool IsOperator(string word) =>
            word.Equals("and", StringComparison.OrdinalIgnoreCase) || word.Equals("or", StringComparison.OrdinalIgnoreCase);

        // or-expression: and-expressions separated by "or".
        private static BooleanSearch? ParseOr(List<(string Kind, string Text)> tokens, ref int position)
        {
            var parts = new List<BooleanSearch>();
            while (position < tokens.Count)
            {
                if (ParseAnd(tokens, ref position) is { } part)
                    parts.Add(part);
                if (position < tokens.Count && tokens[position].Kind == "or")
                    position++;
                else if (position < tokens.Count && tokens[position].Kind == ")")
                    break;
            }
            return parts.Count switch { 0 => null, 1 => parts[0], _ => new Or(parts) };
        }

        // and-expression: primaries separated by "and".
        private static BooleanSearch? ParseAnd(List<(string Kind, string Text)> tokens, ref int position)
        {
            var parts = new List<BooleanSearch>();
            while (position < tokens.Count)
            {
                if (ParsePrimary(tokens, ref position) is { } part)
                    parts.Add(part);
                if (position < tokens.Count && tokens[position].Kind == "and")
                    position++;
                else
                    break;
            }
            return parts.Count switch { 0 => null, 1 => parts[0], _ => new And(parts) };
        }

        // primary: a parenthesized expression, or the words up to the next operator or parenthesis (one term).
        private static BooleanSearch? ParsePrimary(List<(string Kind, string Text)> tokens, ref int position)
        {
            if (position >= tokens.Count)
                return null;
            switch (tokens[position].Kind)
            {
                case "(":
                    position++;
                    var inner = ParseOr(tokens, ref position);
                    if (position < tokens.Count && tokens[position].Kind == ")")
                        position++;
                    return inner;
                case ")":
                    // An extra closing parenthesis at the top level: skipped.
                    position++;
                    return null;
                case "and" or "or":
                    return null;
            }
            var words = new List<string>();
            while (position < tokens.Count && tokens[position].Kind is "word" or "term")
                words.Add(tokens[position++].Text);
            var text = string.Join(' ', words.Where(w => w.Length > 0));
            return text.Length == 0 ? null : new Term(text);
        }

        /// <inheritdoc />
        public override string ToString() => this switch
        {
            Term t => $"\"{t.Text}\"",
            And a => "(" + string.Join(" AND ", a.Parts) + ")",
            Or o => "(" + string.Join(" OR ", o.Parts) + ")",
            _ => base.ToString()!
        };
    }
}
