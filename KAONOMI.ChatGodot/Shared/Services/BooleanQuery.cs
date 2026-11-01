using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KAONOMI.Chat.Services;

/// <summary>
/// A search expression with logical operators: <c>and</c>, <c>or</c>, <c>not</c> (in any case) and parentheses, e.g.
/// <c>hinton or (lecun and not prix)</c>. <c>not</c> binds tighter than <c>and</c>, which binds tighter than <c>or</c>;
/// the words between two operators make one term (a phrase), and a "quoted text" is one term even with an operator in
/// it. A missing closing parenthesis is accepted, an extra one ignored. Each term is searched on its own
/// (<see cref="Terms"/>), then the expression says whether an element matches (<see cref="Matches"/>).
/// </summary>
public abstract class BooleanQuery
{
    private BooleanQuery()
    {
    }

    /// <summary>The terms of the expression (the texts searched), each once.</summary>
    public IReadOnlyList<string> Terms => Collect().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when an element matches the expression, <paramref name="has"/> telling whether it matches one term.</summary>
    public abstract bool Matches(Func<string, bool> has);

    protected abstract IEnumerable<string> Collect();

    /// <summary>The expression of <paramref name="text"/>, null when it holds no term.</summary>
    public static BooleanQuery? Parse(string? text)
    {
        var tokens = Tokenize(text ?? string.Empty);
        var position = 0;
        return ParseOr(tokens, ref position);
    }

    /// <summary>The expression written back (operators in lower case, phrases quoted when needed).</summary>
    public abstract override string ToString();

    private sealed class Term(string text) : BooleanQuery
    {
        public string Text { get; } = text;
        public override bool Matches(Func<string, bool> has) => has(Text);
        protected override IEnumerable<string> Collect() => [Text];
        public override string ToString() => Text.Contains(' ') ? $"\"{Text}\"" : Text;
    }

    private sealed class And(IReadOnlyList<BooleanQuery> parts) : BooleanQuery
    {
        public override bool Matches(Func<string, bool> has) => parts.All(p => p.Matches(has));
        protected override IEnumerable<string> Collect() => parts.SelectMany(p => p.Collect());
        public override string ToString() => "(" + string.Join(" and ", parts) + ")";
    }

    private sealed class Or(IReadOnlyList<BooleanQuery> parts) : BooleanQuery
    {
        public override bool Matches(Func<string, bool> has) => parts.Any(p => p.Matches(has));
        protected override IEnumerable<string> Collect() => parts.SelectMany(p => p.Collect());
        public override string ToString() => "(" + string.Join(" or ", parts) + ")";
    }

    private sealed class Not(BooleanQuery part) : BooleanQuery
    {
        public override bool Matches(Func<string, bool> has) => !part.Matches(has);
        protected override IEnumerable<string> Collect() => part.Collect();
        public override string ToString() => "not " + part;
    }

    // ── Reading ──

    private enum Kind { Word, Quoted, And, Or, Not, Open, Close }

    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var depth = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c is '(' or ')')
            {
                // A closing parenthesis without its opening one is ignored.
                if (c == '(')
                    depth++;
                else if (depth > 0)
                    depth--;
                else
                {
                    i++;
                    continue;
                }
                tokens.Add(new Token(c == '(' ? Kind.Open : Kind.Close, c.ToString()));
                i++;
                continue;
            }
            if (c is '"' or '«' or '“')
            {
                var close = c == '«' ? '»' : c == '“' ? '”' : '"';
                var end = text.IndexOf(close, i + 1);
                var quoted = end < 0 ? text[(i + 1)..] : text[(i + 1)..end];
                if (quoted.Trim().Length > 0)
                    tokens.Add(new Token(Kind.Quoted, quoted.Trim()));
                i = end < 0 ? text.Length : end + 1;
                continue;
            }
            var word = new StringBuilder();
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or '"' or '«' or '“'))
                word.Append(text[i++]);
            var w = word.ToString();
            tokens.Add(w.ToLowerInvariant() switch
            {
                "and" => new Token(Kind.And, w),
                "or" => new Token(Kind.Or, w),
                "not" => new Token(Kind.Not, w),
                _ => new Token(Kind.Word, w)
            });
        }
        return tokens;
    }

    private static BooleanQuery? ParseOr(List<Token> tokens, ref int position)
    {
        var parts = new List<BooleanQuery>();
        while (true)
        {
            if (ParseAnd(tokens, ref position) is { } part)
                parts.Add(part);
            if (position < tokens.Count && tokens[position].Kind == Kind.Or)
            {
                position++;
                continue;
            }
            break;
        }
        return parts.Count switch { 0 => null, 1 => parts[0], _ => new Or(parts) };
    }

    private static BooleanQuery? ParseAnd(List<Token> tokens, ref int position)
    {
        var parts = new List<BooleanQuery>();
        while (true)
        {
            if (ParseNot(tokens, ref position) is { } part)
                parts.Add(part);
            if (position < tokens.Count && tokens[position].Kind == Kind.And)
            {
                position++;
                continue;
            }
            // Two operands without an operator between them ("a (b or c)", "a \"b c\""): and.
            if (position < tokens.Count && tokens[position].Kind is Kind.Open or Kind.Quoted or Kind.Word or Kind.Not)
                continue;
            break;
        }
        return parts.Count switch { 0 => null, 1 => parts[0], _ => new And(parts) };
    }

    private static BooleanQuery? ParseNot(List<Token> tokens, ref int position)
    {
        var negations = 0;
        while (position < tokens.Count && tokens[position].Kind == Kind.Not)
        {
            negations++;
            position++;
        }
        var operand = ParseOperand(tokens, ref position);
        if (operand is null)
            return null;
        return negations % 2 == 1 ? new Not(operand) : operand;
    }

    /// <summary>A parenthesised expression, or a term: the words up to the next operator or parenthesis, or a quoted text.</summary>
    private static BooleanQuery? ParseOperand(List<Token> tokens, ref int position)
    {
        if (position >= tokens.Count)
            return null;
        var token = tokens[position];
        if (token.Kind == Kind.Open)
        {
            position++;
            var inner = ParseOr(tokens, ref position);
            if (position < tokens.Count && tokens[position].Kind == Kind.Close)
                position++;
            return inner;
        }
        if (token.Kind == Kind.Quoted)
        {
            position++;
            return new Term(token.Text);
        }
        var words = new List<string>();
        while (position < tokens.Count && tokens[position].Kind == Kind.Word)
            words.Add(tokens[position++].Text);
        return words.Count > 0 ? new Term(string.Join(' ', words)) : null;
    }
}
