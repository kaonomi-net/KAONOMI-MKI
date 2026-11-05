namespace KAONOMI.ImmuneSystem.Office;

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

/// <summary>
/// Source code written as text in a Word document or a PowerPoint presentation: lines in a monospaced font or a code
/// style, that look like code (keywords, braces, semicolons, operators). Three lines in a row make a block.
/// </summary>
public static class CodeText
{
    public sealed record Line(string Text, bool Monospace, bool CodeStyle);

    private static readonly HashSet<string> MonospaceFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Consolas", "Courier", "Courier New", "Lucida Console", "Lucida Sans Typewriter", "Menlo", "Monaco", "Cascadia Code",
        "Cascadia Mono", "Source Code Pro", "Fira Code", "Fira Mono", "JetBrains Mono", "SF Mono", "DejaVu Sans Mono",
        "Roboto Mono", "Ubuntu Mono", "Andale Mono", "Inconsolata", "IBM Plex Mono", "Liberation Mono", "OCR A Extended",
    };

    private static readonly Regex CodeStyle = new(@"code|preformat|HTMLpr|listing|monospace|snippet", RegexOptions.IgnoreCase);

    private static readonly Regex Keyword = new(
        @"^(?:(?:public|private|protected|internal|static|async|export|abstract|override|sealed)\s|class\s+\w|interface\s+\w|def\s+\w+\s*\(|function\b|func\s+\w|fn\s+\w|import\s+[\w.{*]|from\s+[\w.]+\s+import\b|#include\b|#define\b|using\s+[\w.]+\s*;|package\s+[\w.]+|namespace\s+\w|(?:var|let|const|val|int|string|bool|void|double|float)\s+\w+\s*[=:;(]|return\b|if\s*\(|}?\s*else\b|for\s*\(|foreach\s*\(|while\s*\(|switch\s*\(|try\s*{|catch\s*\(|SELECT\s.+\sFROM\s|INSERT\s+INTO\s|UPDATE\s+\w+\s+SET\s|DELETE\s+FROM\s|CREATE\s+(?:TABLE|INDEX|VIEW|PROCEDURE)\s|(?:Private\s+|Public\s+)?Sub\s+\w+\s*\(|End\s+(?:Sub|Function|If)\b|Dim\s+\w+|echo\s|sudo\s|npm\s|pip\s|git\s|docker\s|kubectl\s|curl\s|\$\s+\w|<\?php|<!DOCTYPE|<html|<script|<div\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Symbols = new(@"(?:=>|==|!=|&&|\|\||::|->|\+=|-=|\$\(|\)\s*\{|\[\]|</\w+>)", RegexOptions.CultureInvariant);
    private static readonly Regex EndsLikeCode = new(@"[;{}]\s*$|\)\s*:\s*$|^\s*}\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Call = new(@"\b[\w.]+\([^()]*\)\s*;?\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Braces = new(@"^[{}()\[\];,]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Comment = new(@"^\s*(?://|/\*|\*/|#\s|--\s|')", RegexOptions.CultureInvariant);

    private static readonly (string Language, Regex Pattern)[] Languages =
    [
        ("C#", new(@"\busing System\b|\bnamespace\s+\w|\bpublic\s+(?:sealed\s+|static\s+)?class\b|\bvar\s+\w+\s*=\s*new\b|\basync Task\b")),
        ("Java", new(@"\bpublic static void main\b|\bSystem\.out\.print|\bimport java\.")),
        ("Python", new(@"^\s*def\s+\w+\(.*\)\s*:|^\s*import\s+\w+\s*$|^\s*from\s+[\w.]+\s+import\b|\bself\.\w|print\(", RegexOptions.Multiline)),
        ("JavaScript / TypeScript", new(@"\bfunction\s+\w+\s*\(|\bconst\s+\w+\s*=|\blet\s+\w+\s*=|=>\s*\{|\bconsole\.log\b|\brequire\(")),
        ("C / C++", new(@"#include\s*<|\bint\s+main\s*\(|\bstd::|\bprintf\s*\(")),
        ("SQL", new(@"\bSELECT\b[\s\S]+?\bFROM\b|\bINSERT\s+INTO\b|\bCREATE\s+TABLE\b", RegexOptions.IgnoreCase)),
        ("VBA", new(@"\bEnd Sub\b|\bDim\s+\w+\s+As\b|\bSub\s+\w+\s*\(")),
        ("Shell", new(@"^\s*(?:sudo|echo|export|cd|ls|chmod|curl|apt|brew)\s|^\s*\$\s", RegexOptions.Multiline)),
        ("HTML / XML", new(@"<(?:html|div|span|script|body|head|\?xml)\b", RegexOptions.IgnoreCase)),
        ("PowerShell", new(@"\b(?:Get|Set|New|Remove|Invoke)-\w+|\$PSVersionTable")),
    ];

    /// <summary>The lines of a Word part (document.xml, header, footer…).</summary>
    public static List<Line> Word(Stream xml) => Read(xml, "p", "t", "tab", "br", "rFonts", "pStyle", w: true);

    /// <summary>The lines of a PowerPoint slide.</summary>
    public static List<Line> Slide(Stream xml) => Read(xml, "p", "t", "tab", "br", "latin", null, w: false);

    private static List<Line> Read(Stream xml, string paragraph, string text, string tab, string br, string font, string? style, bool w)
    {
        var lines = new List<Line>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = false, XmlResolver = null };
        using var reader = XmlReader.Create(xml, settings);
        var sb = new StringBuilder();
        int mono = 0, total = 0;
        bool runMono = false, codeStyle = false, inText = false;

        void Flush()
        {
            var isMono = total > 0 && mono * 2 >= total;
            foreach (var l in sb.ToString().Split('\n'))
                lines.Add(new Line(l, isMono, codeStyle));
            sb.Clear();
            mono = total = 0;
            codeStyle = false;
        }

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var name = reader.LocalName;
                    if (name == paragraph)
                    {
                        sb.Clear();
                        mono = total = 0;
                        codeStyle = false;
                        if (reader.IsEmptyElement)
                            lines.Add(new Line("", false, false));
                    }
                    else if (name is "r")
                    {
                        runMono = false;
                    }
                    else if (name == font)
                    {
                        var face = w ? reader.GetAttribute("ascii", reader.NamespaceURI) ?? reader.GetAttribute("hAnsi", reader.NamespaceURI) : reader.GetAttribute("typeface");
                        runMono = face is not null && MonospaceFonts.Contains(face);
                    }
                    else if (style is not null && name == style && reader.GetAttribute("val", reader.NamespaceURI) is { } s && CodeStyle.IsMatch(s))
                    {
                        codeStyle = true;
                    }
                    else if (name == text)
                    {
                        inText = !reader.IsEmptyElement;
                    }
                    else if (name == tab)
                    {
                        sb.Append('\t');
                    }
                    else if (name == br)
                    {
                        sb.Append('\n');
                    }
                    break;
                case XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace when inText:
                    sb.Append(reader.Value);
                    total += reader.Value.Length;
                    if (runMono)
                        mono += reader.Value.Length;
                    break;
                case XmlNodeType.EndElement:
                    if (reader.LocalName == text)
                        inText = false;
                    else if (reader.LocalName == paragraph)
                        Flush();
                    break;
            }
        }
        return lines;
    }

    private static bool IsCode(Line line)
    {
        var t = line.Text.Trim();
        if (Braces.IsMatch(t))
            return line.Monospace || line.CodeStyle || t.Length > 1;
        if (t.Length < 2)
            return false;
        var score = 0;
        if (line.Monospace)
            score += 2;
        if (line.CodeStyle)
            score += 2;
        if (Keyword.IsMatch(t))
            score += 2;
        if (Symbols.IsMatch(t))
            score++;
        if (EndsLikeCode.IsMatch(t))
            score++;
        if (Call.IsMatch(t))
            score++;
        if (Comment.IsMatch(t) && (line.Monospace || line.CodeStyle))
            score++;
        // A sentence of prose is not code, even in Courier.
        if (t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 12 && t.EndsWith('.') && !t.Contains(';'))
            score -= 3;
        return score >= 3;
    }

    /// <summary>The code blocks of the lines, as one finding, or null when there are none.</summary>
    public static CodeFinding? Find(IReadOnlyList<Line> lines, string location, int maxCode, bool noCode)
    {
        var blocks = new List<List<string>>();
        List<string>? current = null;
        var blanks = 0;
        foreach (var line in lines)
        {
            if (line.Text.Trim().Length == 0)
            {
                if (current is not null && ++blanks <= 1 && (line.Monospace || line.CodeStyle))
                    current.Add("");
                else if (blanks > 1)
                    current = null;
                continue;
            }
            blanks = 0;
            if (IsCode(line))
            {
                if (current is null)
                    blocks.Add(current = []);
                current.Add(line.Text.TrimEnd());
            }
            else
            {
                current = null;
            }
        }
        foreach (var b in blocks)
            while (b.Count > 0 && b[^1].Length == 0)
                b.RemoveAt(b.Count - 1);
        blocks.RemoveAll(b => b.Count(l => l.Length > 0) < 3);
        if (blocks.Count == 0)
            return null;

        var code = string.Join("\n\n", blocks.Select(b => string.Join("\n", b)));
        var languages = Languages.Where(l => l.Pattern.IsMatch(code)).Select(l => l.Language).ToList();
        var count = blocks.Sum(b => b.Count(l => l.Length > 0));
        return new CodeFinding
        {
            Kind = "code-text",
            Severity = Severity.Info,
            Location = location,
            Description = $"{blocks.Count} block(s) of source code written as text ({count} lines)" +
                (languages.Count > 0 ? $": {string.Join(", ", languages)}" : ""),
            Lines = count,
            Iocs = CodeAnalysis.Iocs(code) is { Count: > 0 } iocs ? iocs : null,
            Code = noCode ? null : Cut(code, maxCode),
        };
    }

    public static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "\n…";
}
