using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Evaluates the expressions of the <c>/work</c> window (a scientific calculator):
/// <list type="bullet">
/// <item>numbers (<c>.</c> or <c>,</c> as decimal separator), the constants <c>π</c> (or <c>pi</c>) and <c>e</c>, and
/// <c>Ans</c> (the last result);</item>
/// <item><c>+ − × ÷</c> (also <c>- * /</c>), <c>mod</c> (remainder), <c>^</c> (power, right associative), unary minus;</item>
/// <item>postfix <c>%</c> (hundredth of the value before it) and <c>!</c> (factorial of a whole number up to 170);</item>
/// <item><c>√</c> (square root of the value after it) and the functions <c>sin cos tan asin acos atan sinh cosh tanh
/// ln log log2 exp sqrt cbrt abs</c>, with or without parentheses (<c>sin(30)</c>, <c>sin 30</c>);</item>
/// <item>parentheses (a missing closing one is accepted) and implicit multiplication (<c>2π</c>, <c>3(4+1)</c>,
/// <c>2sin(30)</c>).</item>
/// </list>
/// Angles of the trigonometric functions are in degrees or radians (<c>degrees</c>). In the graph, <c>x</c> is the
/// variable (<see cref="EvaluateAt"/>).
/// </summary>
public static class CalculatorEngine
{
    /// <summary>
    /// The value of <paramref name="expression"/>, angles in degrees when <paramref name="degrees"/>,
    /// <paramref name="ans"/> as <c>Ans</c>; an exception with a short message when it is invalid.
    /// </summary>
    public static double Evaluate(string expression, bool degrees = false, double ans = 0) =>
        Evaluate(expression, degrees, ans, x: null);

    /// <summary>
    /// The value of a function of <c>x</c> (graph of the calculator) at <paramref name="x"/>; NaN where it is not defined
    /// (division by zero, √ of a negative number…). Angles in radians unless <paramref name="degrees"/>.
    /// </summary>
    public static double EvaluateAt(string expression, double x, bool degrees = false)
    {
        try
        {
            return Evaluate(expression, degrees, 0, x);
        }
        catch (Exception ex) when (ex is ArithmeticException or FormatException or OverflowException)
        {
            return double.NaN;
        }
    }

    /// <summary>
    /// Null when <paramref name="expression"/> is a valid function of <c>x</c>, else the syntax error (values where the
    /// function is not defined are not errors).
    /// </summary>
    public static string? CheckFunction(string expression)
    {
        foreach (var x in new[] { 0.5, 1.7, -2.3, 0 })
        {
            try
            {
                Evaluate(expression, false, 0, x);
                return null;
            }
            catch (FormatException ex)
            {
                return ex.Message;
            }
            catch (ArithmeticException)
            {
                // Not defined there: try another x.
            }
        }
        return null;
    }

    private static double Evaluate(string expression, bool degrees, double ans, double? x)
    {
        var parser = new Parser(expression, degrees, ans, x);
        var value = parser.ParseExpression();
        parser.SkipSpaces();
        if (!parser.AtEnd)
            throw new FormatException($"Unexpected \"{parser.Current}\"");
        if (double.IsNaN(value))
            throw new ArithmeticException("Not a number");
        if (double.IsInfinity(value))
            throw new ArithmeticException("Too large");
        return value;
    }

    /// <summary>Text of a result: up to 12 significant digits, no exponent from 10⁻⁹ to 10¹⁵, <c>.</c> as decimal separator.</summary>
    public static string Format(double value)
    {
        var rounded = double.Parse(value.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (rounded == 0)
            return "0";
        return Math.Abs(rounded) is >= 1e15 or < 1e-9
            ? rounded.ToString("G12", CultureInfo.InvariantCulture)
            : rounded.ToString("0.####################", CultureInfo.InvariantCulture);   // its 12 digits, without exponent
    }

    /// <summary>
    /// Recursive descent parser: expression → term (± term)*, term → unary ((×|÷|mod|implicit) unary)*,
    /// unary → (−|+|√)* power, power → postfix (^ unary)?, postfix → primary (%|!)*, primary → number | constant |
    /// function argument | ( expression ).
    /// </summary>
    private sealed class Parser(string text, bool degrees, double ans, double? x)
    {
        private int _position;

        public bool AtEnd => _position >= text.Length;

        public char Current => text[_position];

        public void SkipSpaces()
        {
            while (!AtEnd && char.IsWhiteSpace(Current))
                _position++;
        }

        private bool Accept(params char[] symbols)
        {
            SkipSpaces();
            if (AtEnd || Array.IndexOf(symbols, Current) < 0)
                return false;
            _position++;
            return true;
        }

        /// <summary>Accepts the word <paramref name="word"/> when it is the whole next identifier.</summary>
        private bool AcceptWord(string word)
        {
            SkipSpaces();
            var start = _position;
            if (PeekIdentifier() is { } identifier && string.Equals(identifier, word, StringComparison.OrdinalIgnoreCase))
                return true;
            _position = start;
            return false;
        }

        /// <summary>
        /// Reads the identifier at the current position (letters, and the 2 of log2), or π; null when there is none.
        /// Digits are not part of a name, so that <c>sin30</c> is sin(30).
        /// </summary>
        private string? PeekIdentifier()
        {
            if (AtEnd)
                return null;
            if (Current == 'π')
            {
                _position++;
                return "π";
            }
            if (!char.IsLetter(Current))
                return null;
            var start = _position;
            while (!AtEnd && char.IsLetter(Current))
                _position++;
            if (!AtEnd && Current == '2' && string.Equals(text[start.._position], "log", StringComparison.OrdinalIgnoreCase))
                _position++;
            return text[start.._position];
        }

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                if (Accept('+'))
                    value += ParseTerm();
                else if (Accept('-', '−'))
                    value -= ParseTerm();
                else
                    return value;
            }
        }

        private double ParseTerm()
        {
            var value = ParseUnary();
            while (true)
            {
                if (Accept('*', '×'))
                    value *= ParseUnary();
                else if (Accept('/', '÷'))
                {
                    var divisor = ParseUnary();
                    if (divisor == 0)
                        throw new DivideByZeroException("Division by zero");
                    value /= divisor;
                }
                else if (AcceptWord("mod"))
                {
                    var divisor = ParseUnary();
                    if (divisor == 0)
                        throw new DivideByZeroException("Division by zero");
                    value -= divisor * Math.Floor(value / divisor);   // same sign as the divisor, as on calculators
                }
                else if (StartsOperand())
                    value *= ParseUnary();   // implicit multiplication: 2π, 3(4+1), 2sin(30)
                else
                    return value;
            }
        }

        /// <summary>True when an operand starts here without an operator before it (implicit multiplication).</summary>
        private bool StartsOperand()
        {
            SkipSpaces();
            if (AtEnd)
                return false;
            if (Current is '(' or 'π' or '√' || char.IsDigit(Current))
                return true;
            if (!char.IsLetter(Current))
                return false;
            var start = _position;
            var isMod = string.Equals(PeekIdentifier(), "mod", StringComparison.OrdinalIgnoreCase);
            _position = start;
            return !isMod;
        }

        // Unary minus and √ apply to a whole power: -2^2 = -4, √3^2 = 3.
        private double ParseUnary()
        {
            if (Accept('-', '−'))
                return -ParseUnary();
            if (Accept('+'))
                return ParseUnary();
            if (Accept('√'))
                return Sqrt(ParseUnary());
            return ParsePower();
        }

        // Right associative, the exponent may be negative: 2^3^2 = 2^9, 2^-1 = 0.5.
        private double ParsePower()
        {
            var value = ParsePostfix();
            return Accept('^') ? Math.Pow(value, ParseUnary()) : value;
        }

        private double ParsePostfix()
        {
            var value = ParsePrimary();
            while (true)
            {
                if (Accept('%'))
                    value /= 100;
                else if (Accept('!'))
                    value = Factorial(value);
                else
                    return value;
            }
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (AtEnd)
                throw new FormatException("Incomplete expression");
            if (Accept('('))
                return ParseParenthesized();

            if (PeekIdentifier() is { } identifier)
                return Identifier(identifier);

            var start = _position;
            while (!AtEnd && (char.IsDigit(Current) || Current is '.' or ','))
                _position++;
            if (_position == start)
                throw new FormatException($"Unexpected \"{Current}\"");
            var number = text[start.._position].Replace(',', '.');
            if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result))
                throw new FormatException($"Invalid number \"{number}\"");
            return result;
        }

        /// <summary>After "(": the expression and its ")"; a missing ")" at the end is accepted, as on most calculators.</summary>
        private double ParseParenthesized()
        {
            var value = ParseExpression();
            if (!Accept(')') && !AtEnd)
                throw new FormatException("Missing \")\"");
            return value;
        }

        /// <summary>Value of a constant, or of a function applied to its argument (in parentheses or not).</summary>
        /// <summary>Names of the functions, constants and variables.</summary>
        private static readonly HashSet<string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "π", "pi", "e", "ans", "x", "mod", "sin", "cos", "tan", "asin", "acos", "atan", "sinh", "cosh", "tanh",
            "ln", "log", "log2", "exp", "sqrt", "cbrt", "abs"
        };

        /// <summary>Constants and variables that may be glued to what follows: xsin(x) = x·sin(x), ex = e·x, pix = π·x.</summary>
        private static readonly string[] GluedPrefixes = ["ans", "pi", "x", "e"];

        private double Identifier(string name)
        {
            // An unknown name made of a constant or x glued to the rest: only the prefix is taken here, the rest is
            // read next as an implicit multiplication.
            if (!KnownNames.Contains(name)
                && GluedPrefixes.FirstOrDefault(p => name.Length > p.Length && name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } prefix)
            {
                _position -= name.Length - prefix.Length;
                name = prefix;
            }

            switch (name.ToLowerInvariant())
            {
                case "π" or "pi":
                    return Math.PI;
                case "e":
                    return Math.E;
                case "ans":
                    return ans;
                case "x":
                    return x ?? throw new FormatException("x is the variable of the graph");
            }

            var argument = Accept('(') ? ParseParenthesized() : ParseUnary();
            return name.ToLowerInvariant() switch
            {
                "sin" => Trig(Math.Sin(ToRadians(argument))),
                "cos" => Trig(Math.Cos(ToRadians(argument))),
                "tan" => Tan(argument),
                "asin" => FromRadians(Math.Asin(InUnitRange(argument, "asin"))),
                "acos" => FromRadians(Math.Acos(InUnitRange(argument, "acos"))),
                "atan" => FromRadians(Math.Atan(argument)),
                "sinh" => Math.Sinh(argument),
                "cosh" => Math.Cosh(argument),
                "tanh" => Math.Tanh(argument),
                "ln" => Log(argument, Math.Log),
                "log" => Log(argument, Math.Log10),
                "log2" => Log(argument, Math.Log2),
                "exp" => Math.Exp(argument),
                "sqrt" => Sqrt(argument),
                "cbrt" => Math.Cbrt(argument),
                "abs" => Math.Abs(argument),
                _ => throw new FormatException($"Unknown function \"{name}\"")
            };
        }

        private double ToRadians(double angle) => degrees ? angle * Math.PI / 180 : angle;

        private double FromRadians(double angle) => degrees ? angle * 180 / Math.PI : angle;

        /// <summary>Rounds away the tiny error of sin and cos: sin(180°) = 0, not 1.2E-16.</summary>
        private static double Trig(double value) => Math.Round(value, 15);

        private double Tan(double angle)
        {
            if (degrees && Math.Abs(Math.IEEERemainder(angle - 90, 180)) < 1e-12)
                throw new ArithmeticException("tan is undefined at 90° + k×180°");
            return Trig(Math.Tan(ToRadians(angle)));
        }

        private static double InUnitRange(double value, string name) => value is >= -1 and <= 1
            ? value
            : throw new ArithmeticException($"{name} needs a value from -1 to 1");

        private static double Log(double value, Func<double, double> log) => value > 0
            ? log(value)
            : throw new ArithmeticException("Logarithm of a number that is not positive");

        private static double Sqrt(double value) => value >= 0
            ? Math.Sqrt(value)
            : throw new ArithmeticException("Square root of a negative number");

        private static double Factorial(double value)
        {
            if (value < 0 || value > 170 || value != Math.Floor(value))
                throw new ArithmeticException("Factorial of a whole number from 0 to 170 only");
            double result = 1;
            for (var i = 2; i <= (int)value; i++)
                result *= i;
            return result;
        }
    }
}
