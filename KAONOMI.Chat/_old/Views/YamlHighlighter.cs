using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace KAONOMI.Chat.Views;

/// <summary>
/// Colors YAML text for the config editor: comments, keys, list dashes and colons, and values by kind (booleans,
/// numbers, quoted strings, URLs, other text). One palette for light themes, one for dark themes.
/// </summary>
internal static partial class YamlHighlighter
{
    private sealed record Palette(IBrush Key, IBrush Punctuation, IBrush Comment, IBrush Text, IBrush Number, IBrush Boolean, IBrush Url, IBrush Plain);

    private static readonly Palette Light = new(
        Brush("#0B5E83"), Brush("#B18D1D"), Brush("#6A737D"), Brush("#A3501E"),
        Brush("#098658"), Brush("#7C3AED"), Brush("#1A8FA8"), Brush("#212529"));

    private static readonly Palette Dark = new(
        Brush("#7DD3FC"), Brush("#E6B450"), Brush("#8B949E"), Brush("#E3A27A"),
        Brush("#B5CEA8"), Brush("#C4A7FF"), Brush("#4FD1E8"), Brush("#E6E6E6"));

    /// <summary>Fills <paramref name="inlines"/> with the colored runs of <paramref name="yaml"/>.</summary>
    public static void Highlight(InlineCollection inlines, string yaml, bool dark)
    {
        var palette = dark ? Dark : Light;
        inlines.Clear();
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                inlines.Add(new LineBreak());
            AddLine(inlines, lines[i], palette);
        }
    }

    private static void AddLine(InlineCollection inlines, string line, Palette palette)
    {
        if (CommentLine().IsMatch(line))
        {
            inlines.Add(Run(line, palette.Comment, italic: true));
            return;
        }

        if (KeyLine().Match(line) is { Success: true } key)
        {
            inlines.Add(Run(key.Groups["indent"].Value, palette.Plain));
            inlines.Add(Run(key.Groups["dash"].Value, palette.Punctuation));
            inlines.Add(Run(key.Groups["key"].Value, palette.Key));
            inlines.Add(Run(key.Groups["colon"].Value, palette.Punctuation));
            AddValue(inlines, key.Groups["rest"].Value, palette);
            return;
        }

        if (ListLine().Match(line) is { Success: true } item)
        {
            inlines.Add(Run(item.Groups["indent"].Value, palette.Plain));
            inlines.Add(Run(item.Groups["dash"].Value, palette.Punctuation));
            AddValue(inlines, item.Groups["rest"].Value, palette);
            return;
        }

        inlines.Add(Run(line, palette.Plain));
    }

    /// <summary>A value, possibly followed by a comment (" # …").</summary>
    private static void AddValue(InlineCollection inlines, string rest, Palette palette)
    {
        var comment = TrailingComment().Match(rest);
        var value = comment.Success ? rest[..comment.Index] : rest;
        var trimmed = value.Trim();

        var brush = trimmed.Length == 0 ? palette.Plain
            : BooleanValue().IsMatch(trimmed) ? palette.Boolean
            : NumberValue().IsMatch(trimmed) ? palette.Number
            : trimmed.StartsWith("http://") || trimmed.StartsWith("https://") ? palette.Url
            : palette.Text;
        inlines.Add(Run(value, brush));
        if (comment.Success)
            inlines.Add(Run(rest[comment.Index..], palette.Comment, italic: true));
    }

    private static Run Run(string text, IBrush brush, bool italic = false) => new(text)
    {
        Foreground = brush,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal
    };

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    [GeneratedRegex(@"^\s*#")]
    private static partial Regex CommentLine();

    [GeneratedRegex(@"^(?<indent>\s*)(?<dash>-\s+)?(?<key>[\w.\-]+)(?<colon>\s*:)(?<rest>(\s.*)?)$")]
    private static partial Regex KeyLine();

    [GeneratedRegex(@"^(?<indent>\s*)(?<dash>-\s+)(?<rest>.*)$")]
    private static partial Regex ListLine();

    [GeneratedRegex(@"\s#.*$")]
    private static partial Regex TrailingComment();

    [GeneratedRegex(@"^(true|false|yes|no|on|off|null|~)$", RegexOptions.IgnoreCase)]
    private static partial Regex BooleanValue();

    [GeneratedRegex(@"^[-+]?\d+(\.\d+)?$")]
    private static partial Regex NumberValue();
}
