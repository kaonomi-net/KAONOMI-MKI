using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Text of a model answer without its formatting (Markdown): code fences, headings, bold / italic / strike markers,
/// inline code, links and images, block quotes, list bullets, tables, horizontal rules and HTML tags are removed;
/// the words, the code itself and the numbers of numbered lists are kept.
/// </summary>
public static partial class PlainText
{
    /// <summary>The plain text of <paramref name="markdown"/>.</summary>
    public static string FromMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var lines = new List<string>();
        var inCode = false;
        foreach (var rawLine in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            // Code blocks: the fences go, the code stays as it is.
            if (rawLine.TrimStart().StartsWith("```", StringComparison.Ordinal) || rawLine.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                lines.Add(rawLine);
                continue;
            }

            var line = rawLine.Trim();
            if (HorizontalRule().IsMatch(line) || TableSeparator().IsMatch(line))
                continue;

            line = Heading().Replace(line, string.Empty);
            line = Quote().Replace(line, string.Empty);
            line = Bullet().Replace(line, string.Empty);

            // Tables: cells separated by spaces.
            if (line.StartsWith('|') && line.EndsWith('|') && line.Length > 1)
                line = string.Join("  ", line.Trim('|').Split('|').Select(cell => cell.Trim()));

            line = Image().Replace(line, "$1");
            line = Link().Replace(line, "$1");
            line = HtmlTag().Replace(line, string.Empty);
            line = InlineCode().Replace(line, "$1");
            line = Emphasis().Replace(line, "$2");
            line = Underscores().Replace(line, "$2");
            line = Strike().Replace(line, "$1");
            line = line.Replace("\\*", "*").Replace("\\_", "_").Replace("\\#", "#");

            lines.Add(line);
        }

        // At most one empty line in a row, none at the start or the end.
        var text = string.Join("\n", lines);
        text = BlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(>\s?)+")]
    private static partial Regex Quote();

    /// <summary>"- ", "* ", "+ " bullets (numbered lists are kept).</summary>
    [GeneratedRegex(@"^[-*+]\s+(\[[ xX]\]\s+)?")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^([-*_]\s*){3,}$")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    /// <summary>***bold italic***, **bold**, *italic*.</summary>
    [GeneratedRegex(@"(\*{1,3})(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex Emphasis();

    /// <summary>__bold__, _italic_ (not inside words such as snake_case).</summary>
    [GeneratedRegex(@"(?<![\w])(_{1,3})(?=\S)(.+?)(?<=\S)\1(?![\w])")]
    private static partial Regex Underscores();

    [GeneratedRegex(@"~~(.+?)~~")]
    private static partial Regex Strike();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
