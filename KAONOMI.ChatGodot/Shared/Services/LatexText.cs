using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The LaTeX formulas of a Markdown answer (<c>\( … \)</c>, <c>\[ … \]</c>, <c>$ … $</c>, <c>$$ … $$</c>) written as plain
/// text with Unicode symbols (π, r², √(x), a/b, ≈, ×…), for the Markdown renderer, which does not draw LaTeX.
/// </summary>
public static class LatexText
{
    /// <summary><paramref name="markdown"/> with its formulas as Unicode text (display formulas on their own line).</summary>
    public static string Convert(string markdown)
    {
        var text = DisplayMath.Replace(markdown, m => "\n\n" + Formula(m.Groups["f"].Value) + "\n\n");
        text = InlineMath.Replace(text, m => Formula(m.Groups["f"].Value));
        text = DollarMath.Replace(text, m => Formula(m.Groups["f"].Value));
        return Regex.Replace(text, @"\n[ \t]*\n(?:[ \t]*\n)+", "\n\n");
    }

    /// <summary>A LaTeX formula as Unicode text.</summary>
    public static string Formula(string latex)
    {
        var f = latex.Trim();
        // \frac{a}{b} and \sqrt{x} first (their arguments may hold other commands, converted after).
        for (var i = 0; i < 5 && (f.Contains(@"\frac") || f.Contains(@"\dfrac") || f.Contains(@"\sqrt")); i++)
        {
            f = Fraction.Replace(f, m => $"{Group(m.Groups["a"].Value)}/{Group(m.Groups["b"].Value)}");
            f = Root.Replace(f, m => $"√{Group(m.Groups["x"].Value)}");
        }
        foreach (var (command, symbol) in Symbols)
            f = Regex.Replace(f, Regex.Escape(command) + @"(?![A-Za-z])", symbol);
        f = Superscript.Replace(f, m => Sup(m.Groups["x"].Success ? m.Groups["x"].Value : m.Groups["y"].Value));
        f = Subscript.Replace(f, m => Sub(m.Groups["x"].Success ? m.Groups["x"].Value : m.Groups["y"].Value));
        f = Regex.Replace(f, @"\\(left|right|displaystyle|mathrm|text|operatorname|mathbf|mathit)\b", string.Empty);
        f = f.Replace(@"\,", " ").Replace(@"\;", " ").Replace(@"\!", string.Empty).Replace(@"\ ", " ").Replace("~", " ");
        f = f.Replace("{", string.Empty).Replace("}", string.Empty);
        return Regex.Replace(f, @"\s{2,}", " ").Trim();
    }

    /// <summary>An argument in parentheses when it is more than one symbol: (a+b)/2, √(x+1).</summary>
    private static string Group(string value)
    {
        var v = value.Trim();
        return Regex.IsMatch(v, @"^[\w.,π]+$") ? v : $"({v})";
    }

    private static string Sup(string value)
    {
        var chars = new System.Text.StringBuilder();
        foreach (var c in value)
        {
            if (!SuperscriptChars.TryGetValue(c, out var s))
                return "^" + (value.Length > 1 ? $"({value})" : value);
            chars.Append(s);
        }
        return chars.ToString();
    }

    private static string Sub(string value)
    {
        var chars = new System.Text.StringBuilder();
        foreach (var c in value)
        {
            if (!SubscriptChars.TryGetValue(c, out var s))
                return "_" + (value.Length > 1 ? $"({value})" : value);
            chars.Append(s);
        }
        return chars.ToString();
    }

    private static readonly Dictionary<char, char> SuperscriptChars = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴', ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸',
        ['9'] = '⁹', ['+'] = '⁺', ['-'] = '⁻', ['−'] = '⁻', ['n'] = 'ⁿ', ['i'] = 'ⁱ', ['('] = '⁽', [')'] = '⁾',
        ['x'] = 'ˣ', ['y'] = 'ʸ', ['a'] = 'ᵃ', ['b'] = 'ᵇ', ['k'] = 'ᵏ', ['m'] = 'ᵐ', ['t'] = 'ᵗ'
    };

    private static readonly Dictionary<char, char> SubscriptChars = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄', ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈',
        ['9'] = '₉', ['+'] = '₊', ['-'] = '₋', ['n'] = 'ₙ', ['i'] = 'ᵢ', ['x'] = 'ₓ'
    };

    private static readonly (string Command, string Symbol)[] Symbols =
    [
        (@"\times", "×"), (@"\cdot", "·"), (@"\div", "÷"), (@"\pm", "±"), (@"\mp", "∓"), (@"\approx", "≈"),
        (@"\simeq", "≃"), (@"\sim", "∼"), (@"\neq", "≠"), (@"\ne", "≠"), (@"\leq", "≤"), (@"\le", "≤"),
        (@"\geq", "≥"), (@"\ge", "≥"), (@"\infty", "∞"), (@"\pi", "π"), (@"\Pi", "Π"), (@"\alpha", "α"),
        (@"\beta", "β"), (@"\gamma", "γ"), (@"\delta", "δ"), (@"\Delta", "Δ"), (@"\epsilon", "ε"),
        (@"\varepsilon", "ε"), (@"\theta", "θ"), (@"\lambda", "λ"), (@"\mu", "μ"), (@"\sigma", "σ"),
        (@"\Sigma", "Σ"), (@"\sum", "∑"), (@"\prod", "∏"), (@"\int", "∫"), (@"\partial", "∂"), (@"\omega", "ω"),
        (@"\Omega", "Ω"), (@"\phi", "φ"), (@"\varphi", "φ"), (@"\rho", "ρ"), (@"\tau", "τ"), (@"\rightarrow", "→"),
        (@"\to", "→"), (@"\Rightarrow", "⇒"), (@"\Leftrightarrow", "⇔"), (@"\leftarrow", "←"), (@"\in", "∈"),
        (@"\notin", "∉"), (@"\subset", "⊂"), (@"\cup", "∪"), (@"\cap", "∩"), (@"\forall", "∀"), (@"\exists", "∃"),
        (@"\degree", "°"), (@"\circ", "°"), (@"\%", "%"), (@"\ldots", "…"), (@"\dots", "…"), (@"\cdots", "⋯")
    ];

    private static readonly Regex DisplayMath = new(@"\\\[(?<f>.+?)\\\]|\$\$(?<f>.+?)\$\$", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InlineMath = new(@"\\\((?<f>.+?)\\\)", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>$…$ holding a LaTeX command or ^ / _ (not a price such as $5 or a lone dollar).</summary>
    private static readonly Regex DollarMath = new(@"(?<![\w$\\])\$(?<f>[^$\n]*?(?:\\[A-Za-z]+|[\^_])[^$\n]*?)\$(?![\w$])", RegexOptions.Compiled);

    private static readonly Regex Fraction = new(@"\\d?frac\s*\{(?<a>(?:[^{}]|\{[^{}]*\})*)\}\s*\{(?<b>(?:[^{}]|\{[^{}]*\})*)\}", RegexOptions.Compiled);
    private static readonly Regex Root = new(@"\\sqrt\s*\{(?<x>(?:[^{}]|\{[^{}]*\})*)\}", RegexOptions.Compiled);
    private static readonly Regex Superscript = new(@"\^\{(?<x>[^{}]*)\}|\^(?<y>[^\s{}\\])", RegexOptions.Compiled);
    private static readonly Regex Subscript = new(@"_\{(?<x>[^{}]*)\}|_(?<y>[^\s{}\\])", RegexOptions.Compiled);
}
