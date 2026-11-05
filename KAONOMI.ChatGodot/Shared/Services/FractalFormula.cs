using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The formula of an escape-time fractal of /fractal (<c>z^2 + c</c>), checked and translated into an expression of
/// the shading language of Godot, every value a complex number (a <c>vec2</c>). The shader
/// (<c>FractalShader</c>) gives it the helpers it calls (<c>cmul</c>, <c>cdiv</c>, <c>csin</c>…).
/// <para>Grammar: numbers (<c>1.5</c>, <c>2e-3</c>), <c>i</c>, <c>pi</c>, <c>e</c>; the variables z, c, zp (the z
/// before), a, b (two parameters), p (the pixel), n (the iteration); + - * / ^ (and ² ³ after a value), parentheses,
/// a number or a closing parenthesis directly before a value multiplies it (<c>3z^2</c>); the functions of
/// <see cref="Functions"/>.</para>
/// </summary>
public static class FractalFormula
{
    /// <summary>The result: the expression, or the error and where it is (a position in the formula).</summary>
    public sealed record Result(string? Glsl, string? Error, int Position)
    {
        public bool Ok => Glsl is not null;
    }

    /// <summary>The variables of a formula and their names in the shader.</summary>
    public static IReadOnlyDictionary<string, string> Variables { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["z"] = "z",
        ["c"] = "c",
        ["zp"] = "zp",
        ["a"] = "u_a",
        ["b"] = "u_b",
        ["p"] = "px",
        ["n"] = "vec2(n, 0.0)"
    };

    /// <summary>The functions of one complex number and their helpers in the shader, with what they do.</summary>
    public static IReadOnlyDictionary<string, (string Glsl, string Help)> Functions { get; } = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["sin"] = ("csin", "sine"),
        ["cos"] = ("ccos", "cosine"),
        ["tan"] = ("ctan", "tangent"),
        ["sinh"] = ("csinh", "hyperbolic sine"),
        ["cosh"] = ("ccosh", "hyperbolic cosine"),
        ["tanh"] = ("ctanh", "hyperbolic tangent"),
        ["exp"] = ("cexp", "e to the power"),
        ["log"] = ("clog", "natural logarithm"),
        ["ln"] = ("clog", "natural logarithm"),
        ["sqrt"] = ("csqrt", "square root"),
        ["conj"] = ("cconj", "conjugate: x - iy"),
        ["fold"] = ("cfold", "|x| + i|y| (the Burning Ship)"),
        ["abs"] = ("cabs", "modulus |z|, a real"),
        ["norm"] = ("cnorm", "|z|², a real"),
        ["arg"] = ("carg", "angle of z, a real"),
        ["re"] = ("cre", "real part"),
        ["im"] = ("cim", "imaginary part, as a real"),
        ["inv"] = ("cinv", "1 / z"),
        ["flip"] = ("cflip", "y + ix (swaps the parts)")
    };

    private enum Kind { Number, Name, Op, End }

    private readonly record struct Token(Kind Kind, string Text, int Position, double Value = 0);

    /// <summary>A part of the expression: its code, and its value when it is a real number known at once.</summary>
    private readonly record struct Node(string Code, double? Real = null);

    /// <summary>Checks <paramref name="formula"/> and translates it.</summary>
    public static Result Compile(string? formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return new Result(null, "The formula is empty: write the next z, for example z^2 + c", 0);
        try
        {
            var parser = new Parser(Tokenize(formula));
            var node = parser.Expression();
            parser.ExpectEnd();
            return new Result(node.Code, null, -1);
        }
        catch (FormulaException ex)
        {
            return new Result(null, ex.Message, ex.Position);
        }
    }

    private sealed class FormulaException(string message, int position) : Exception(message)
    {
        public int Position { get; } = position;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }
            if (char.IsDigit(ch) || (ch == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                    i++;
                // An exponent only when digits follow (2e-3); "2e" alone is 2 × e.
                if (i < text.Length && text[i] is 'e' or 'E')
                {
                    var j = i + 1;
                    if (j < text.Length && text[j] is '+' or '-')
                        j++;
                    if (j < text.Length && char.IsDigit(text[j]))
                    {
                        i = j;
                        while (i < text.Length && char.IsDigit(text[i]))
                            i++;
                    }
                }
                var literal = text[start..i];
                if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    throw new FormulaException($"'{literal}' is not a number", start);
                tokens.Add(new Token(Kind.Number, literal, start, value));
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(new Token(Kind.Name, text[start..i], start));
                continue;
            }
            switch (ch)
            {
                case '+' or '-' or '*' or '/' or '^' or '(' or ')' or ',':
                    tokens.Add(new Token(Kind.Op, ch.ToString(), i));
                    break;
                case '×' or '·':
                    tokens.Add(new Token(Kind.Op, "*", i));
                    break;
                case '÷':
                    tokens.Add(new Token(Kind.Op, "/", i));
                    break;
                case '−':
                    tokens.Add(new Token(Kind.Op, "-", i));
                    break;
                case '²':
                    tokens.Add(new Token(Kind.Op, "²", i));
                    break;
                case '³':
                    tokens.Add(new Token(Kind.Op, "³", i));
                    break;
                default:
                    throw new FormulaException($"'{ch}' is not allowed", i);
            }
            i++;
        }
        tokens.Add(new Token(Kind.End, string.Empty, text.Length));
        return tokens;
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _at;

        private Token Peek => tokens[_at];

        private Token Next() => tokens[_at++];

        private bool IsOp(string op) => Peek.Kind == Kind.Op && Peek.Text == op;

        public void ExpectEnd()
        {
            if (Peek.Kind != Kind.End)
                throw new FormulaException(Peek.Text == ")" ? "A ')' has no '(' before it" : $"'{Peek.Text}' is not expected here", Peek.Position);
        }

        // expression := term (('+' | '-') term)*
        public Node Expression()
        {
            var left = Term();
            while (IsOp("+") || IsOp("-"))
            {
                var op = Next().Text;
                var right = Term();
                left = left.Real is { } x && right.Real is { } y
                    ? Real(op == "+" ? x + y : x - y)
                    : new Node($"({left.Code} {op} {right.Code})");
            }
            return left;
        }

        // term := unary (('*' | '/' | implicit) unary)*
        private Node Term()
        {
            var left = Unary();
            while (true)
            {
                string op;
                if (IsOp("*") || IsOp("/"))
                    op = Next().Text;
                else if (Peek.Kind is Kind.Number or Kind.Name || IsOp("("))
                    op = "*";
                else
                    return left;
                var right = Unary();
                left = op == "*" ? Multiply(left, right) : Divide(left, right);
            }
        }

        // unary := ('-' | '+') unary | power
        private Node Unary()
        {
            if (IsOp("-"))
            {
                Next();
                var value = Unary();
                return value.Real is { } x ? Real(-x) : new Node($"(-{value.Code})");
            }
            if (IsOp("+"))
            {
                Next();
                return Unary();
            }
            return Power();
        }

        // power := atom ('^' unary | '²' | '³')?   (right-associative: z^2^3 = z^(2^3))
        private Node Power()
        {
            var value = Atom();
            if (IsOp("²") || IsOp("³"))
                return Raise(value, Real(Next().Text == "²" ? 2 : 3));
            if (!IsOp("^"))
                return value;
            Next();
            return Raise(value, Unary());
        }

        private Node Atom()
        {
            var token = Next();
            switch (token.Kind)
            {
                case Kind.Number:
                    return Real(token.Value);
                case Kind.Op when token.Text == "(":
                {
                    var inner = Expression();
                    if (!IsOp(")"))
                        throw new FormulaException("A ')' is missing", Peek.Position);
                    Next();
                    return inner;
                }
                case Kind.Name:
                    return Name(token);
                case Kind.End:
                    throw new FormulaException("The formula stops too early: a value is missing", token.Position);
                default:
                    throw new FormulaException($"A value is expected here, not '{token.Text}'", token.Position);
            }
        }

        private Node Name(Token token)
        {
            var name = token.Text;
            switch (name)
            {
                case "i":
                    return new Node("vec2(0.0, 1.0)");
                case "pi":
                    return Real(Math.PI);
                case "e":
                    return Real(Math.E);
            }
            if (Variables.TryGetValue(name, out var variable))
                return new Node(variable);
            if (name == "pow")
            {
                var (args, _) = Arguments(token, 2);
                return Raise(args[0], args[1]);
            }
            if (Functions.TryGetValue(name, out var function))
            {
                var (args, _) = Arguments(token, 1);
                return new Node($"{function.Glsl}({args[0].Code})");
            }
            throw new FormulaException($"'{name}' is unknown: the variables are z, c, zp, a, b, p, n and the constants i, pi, e", token.Position);
        }

        private (List<Node> Args, int Count) Arguments(Token function, int count)
        {
            if (!IsOp("("))
                throw new FormulaException($"{function.Text} needs its argument in parentheses: {function.Text}(z)", Peek.Position);
            Next();
            var args = new List<Node> { Expression() };
            while (IsOp(","))
            {
                Next();
                args.Add(Expression());
            }
            if (!IsOp(")"))
                throw new FormulaException("A ')' is missing", Peek.Position);
            Next();
            if (args.Count != count)
                throw new FormulaException($"{function.Text} takes {count} argument(s), not {args.Count}", function.Position);
            return (args, count);
        }

        private static Node Real(double value) => new($"vec2({Format(value)}, 0.0)", value);

        private static Node Multiply(Node left, Node right) => (left.Real, right.Real) switch
        {
            ({ } x, { } y) => Real(x * y),
            ({ } x, null) => new Node($"({Format(x)} * {right.Code})"),
            (null, { } y) => new Node($"({left.Code} * {Format(y)})"),
            _ => new Node($"cmul({left.Code}, {right.Code})")
        };

        private static Node Divide(Node left, Node right) => (left.Real, right.Real) switch
        {
            ({ } x, { } y) when y != 0 => Real(x / y),
            (_, { } y) when y != 0 => new Node($"({left.Code} / {Format(y)})"),
            _ => new Node($"cdiv({left.Code}, {right.Code})")
        };

        /// <summary>A power: repeated products for a small whole exponent, the polar form for a real one, else exp(w log z).</summary>
        private static Node Raise(Node value, Node exponent)
        {
            if (exponent.Real is not { } k)
                return new Node($"cpowc({value.Code}, {exponent.Code})");
            if (value.Real is { } x && (x > 0 || k == Math.Floor(k)))
                return Real(Math.Pow(x, k));
            if (k == Math.Floor(k) && Math.Abs(k) <= 32)
            {
                var n = (int)Math.Abs(k);
                var positive = n switch
                {
                    0 => "vec2(1.0, 0.0)",
                    1 => value.Code,
                    2 => $"csqr({value.Code})",
                    _ => $"cpown({value.Code}, {n})"
                };
                return new Node(k < 0 ? $"cinv({positive})" : positive);
            }
            return new Node($"cpowr({value.Code}, {Format(k)})");
        }
    }

    /// <summary>A number as the shading language writes it (always with a dot).</summary>
    public static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "0.0";
        var text = value.ToString("0.0#########", CultureInfo.InvariantCulture);
        return text;
    }

    /// <summary>The help of the formulas, for the window: variables, operators and functions.</summary>
    public static string Help()
    {
        var help = new StringBuilder();
        help.AppendLine("**Variables** · z (the value iterated) · c (the pixel, or the constant of a Julia set) · zp (the z before) · a, b (two parameters) · p (the pixel) · n (the iteration)");
        help.AppendLine();
        help.AppendLine("**Constants** · i · pi · e · numbers (1.5, 2e-3)");
        help.AppendLine();
        help.AppendLine("**Operators** · + - * / ^ ( ) · z² z³ · 3z^2 means 3*z^2 · pow(z, w)");
        help.AppendLine();
        help.Append("**Functions** · ");
        var first = true;
        foreach (var (name, (_, text)) in Functions)
        {
            if (name == "ln")
                continue;
            help.Append(first ? string.Empty : " · ").Append(name).Append(" (").Append(text).Append(')');
            first = false;
        }
        help.AppendLine();
        return help.ToString();
    }
}
