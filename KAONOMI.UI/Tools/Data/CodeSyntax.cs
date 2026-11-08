using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.UI.Desk;
using TextMate = KAONOMI.Chat.Services.CodeHighlighter;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// The syntax colours of the code views (port of <c>CodeSyntax</c> of the Godot chat): the TextMate grammars of
/// <see cref="TextMate"/> (their VS Code Light+ colours turned into the matching Dark+ ones), YAML (the palette of the
/// <c>YamlHighlighter</c> of the Avalonia chat) and JSON (keys, strings, numbers, true / false / null), as runs of a
/// selectable text. Above <see cref="MaxLength"/> characters a text stays plain (a response of several megabytes).
/// The code that is typed is a mono text box (its colours in the coloured view next to it, where a tool has one).
/// </summary>
internal static class CodeSyntax
{
    public const int MaxLength = 300_000;

    private static readonly Dictionary<string, IBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    private static IBrush BrushOf(string color)
    {
        if (!Brushes.TryGetValue(color, out var brush))
            Brushes[color] = brush = new SolidColorBrush(Color.Parse(color));
        return brush;
    }

    /// <summary>The VS Code Light+ colours and their Dark+ counterparts.</summary>
    private static readonly Dictionary<string, string> DarkPlus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#0000FF"] = "#569CD6", ["#001080"] = "#9CDCFE", ["#795E26"] = "#DCDCAA", ["#A31515"] = "#CE9178",
        ["#008000"] = "#6A9955", ["#098658"] = "#B5CEA8", ["#267F99"] = "#4EC9B0", ["#AF00DB"] = "#C586C0",
        ["#800000"] = "#569CD6", ["#E50000"] = "#9CDCFE", ["#FF0000"] = "#9CDCFE", ["#0070C1"] = "#4FC1FF",
        ["#811F3F"] = "#D16969", ["#EE0000"] = "#D7BA7D", ["#000080"] = "#569CD6", ["#0451A5"] = "#CE9178",
        ["#CD3131"] = "#F44747", ["#3030C0"] = "#9CDCFE", ["#16825D"] = "#B5CEA8", ["#800080"] = "#C586C0",
        ["#383838"] = "#D4D4D4", ["#6A737D"] = "#8B949E", ["#000000"] = "#D4D4D4"
    };

    /// <summary>A Light+ colour of the TextMate theme turned into a colour readable on the dark windows.</summary>
    public static IBrush ForDark(string light)
    {
        if (DarkPlus.TryGetValue(light, out var mapped))
            return BrushOf(mapped);
        if (!Color.TryParse(light, out var color))
            return Ui.Text;
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
        if (luminance >= 0.55)
            return BrushOf(light);
        var t = Math.Min(1, 0.55 - luminance + 0.25);
        byte Mix(byte c) => (byte)(c + (255 - c) * t);
        return new SolidColorBrush(Color.FromRgb(Mix(color.R), Mix(color.G), Mix(color.B)));
    }

    // ── Runs ──

    /// <summary>The runs of <paramref name="code"/> in the colours of <paramref name="language"/> (plain for none).</summary>
    public static List<Inline> Code(string? language, string code)
    {
        var runs = new List<Inline>();
        if (code.Length > MaxLength || string.IsNullOrWhiteSpace(language))
        {
            runs.Add(new Run(code));
            return runs;
        }
        var at = 0;
        foreach (var span in TextMate.Highlight(language, code))
        {
            if (span.Start < at || span.Start + span.Length > code.Length)
                continue;
            if (span.Start > at)
                runs.Add(new Run(code[at..span.Start]));
            var run = new Run(code.Substring(span.Start, span.Length)) { Foreground = ForDark(span.Color) };
            if (span.Bold)
                run.FontWeight = FontWeight.Bold;
            if (span.Italic)
                run.FontStyle = FontStyle.Italic;
            runs.Add(run);
            at = span.Start + span.Length;
        }
        if (at < code.Length)
            runs.Add(new Run(code[at..]));
        return runs;
    }

    private static readonly IBrush JsonKey = new SolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush JsonString = new SolidColorBrush(Color.Parse("#CE9178"));
    private static readonly IBrush JsonNumber = new SolidColorBrush(Color.Parse("#B5CEA8"));
    private static readonly IBrush JsonSymbol = new SolidColorBrush(Color.Parse("#E6B450"));
    private static readonly IBrush JsonKeyword = new SolidColorBrush(Color.Parse("#C4A7FF"));

    private static readonly Regex JsonToken = new(
        @"(?<string>""(?:[^""\\]|\\.)*"")(?<key>\s*:)?|(?<number>-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|(?<keyword>\btrue\b|\bfalse\b|\bnull\b)|(?<symbol>[{}\[\],:])",
        RegexOptions.Compiled);

    /// <summary>The runs of JSON: keys, strings, numbers, true / false / null, punctuation.</summary>
    public static List<Inline> Json(string json)
    {
        var runs = new List<Inline>();
        if (json.Length > MaxLength)
        {
            runs.Add(new Run(json));
            return runs;
        }
        var at = 0;
        foreach (Match match in JsonToken.Matches(json))
        {
            if (match.Index > at)
                runs.Add(new Run(json[at..match.Index]));
            if (match.Groups["string"].Success)
            {
                var isKey = match.Groups["key"].Success;
                runs.Add(new Run(match.Groups["string"].Value) { Foreground = isKey ? JsonKey : JsonString });
                if (isKey)
                    runs.Add(new Run(match.Groups["key"].Value) { Foreground = JsonSymbol });
            }
            else if (match.Groups["number"].Success)
                runs.Add(new Run(match.Value) { Foreground = JsonNumber });
            else if (match.Groups["keyword"].Success)
                runs.Add(new Run(match.Value) { Foreground = JsonKeyword });
            else
                runs.Add(new Run(match.Value) { Foreground = JsonSymbol });
            at = match.Index + match.Length;
        }
        if (at < json.Length)
            runs.Add(new Run(json[at..]));
        return runs;
    }

    // YAML: the dark palette of the YamlHighlighter of the Avalonia chat.
    private static readonly IBrush YamlKey = new SolidColorBrush(Color.Parse("#7DD3FC"));
    private static readonly IBrush YamlPunctuation = new SolidColorBrush(Color.Parse("#E6B450"));
    private static readonly IBrush YamlComment = new SolidColorBrush(Color.Parse("#8B949E"));
    private static readonly IBrush YamlText = new SolidColorBrush(Color.Parse("#E3A27A"));
    private static readonly IBrush YamlNumber = new SolidColorBrush(Color.Parse("#B5CEA8"));
    private static readonly IBrush YamlBoolean = new SolidColorBrush(Color.Parse("#C4A7FF"));
    private static readonly IBrush YamlUrl = new SolidColorBrush(Color.Parse("#4FD1E8"));
    private static readonly IBrush YamlPlain = new SolidColorBrush(Color.Parse("#E6E6E6"));

    private static readonly Regex CommentLine = new(@"^\s*#", RegexOptions.Compiled);
    private static readonly Regex KeyLine = new(@"^(?<indent>\s*)(?<dash>-\s+)?(?<key>[\w.\-]+)(?<colon>\s*:)(?<rest>(\s.*)?)$", RegexOptions.Compiled);
    private static readonly Regex ListLine = new(@"^(?<indent>\s*)(?<dash>-\s+)(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex TrailingComment = new(@"\s#.*$", RegexOptions.Compiled);
    private static readonly Regex BooleanValue = new(@"^(true|false|yes|no|on|off|null|~)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NumberValue = new(@"^[-+]?\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>The runs of YAML: comments (italic), keys, list dashes and colons, the values by kind.</summary>
    public static List<Inline> Yaml(string yaml)
    {
        var runs = new List<Inline>();
        if (yaml.Length > MaxLength)
        {
            runs.Add(new Run(yaml));
            return runs;
        }
        void Add(string text, IBrush brush, bool italic = false)
        {
            if (text.Length > 0)
                runs.Add(new Run(text) { Foreground = brush, FontStyle = italic ? FontStyle.Italic : FontStyle.Normal });
        }
        void Value(string rest)
        {
            var comment = TrailingComment.Match(rest);
            var value = comment.Success ? rest[..comment.Index] : rest;
            var trimmed = value.Trim();
            var color = trimmed.Length == 0 ? YamlPlain
                : BooleanValue.IsMatch(trimmed) ? YamlBoolean
                : NumberValue.IsMatch(trimmed) ? YamlNumber
                : trimmed.StartsWith("http://") || trimmed.StartsWith("https://") ? YamlUrl
                : YamlText;
            Add(value, color);
            if (comment.Success)
                Add(rest[comment.Index..], YamlComment, true);
        }
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (CommentLine.IsMatch(line))
                Add(line, YamlComment, true);
            else if (KeyLine.Match(line) is { Success: true } key)
            {
                Add(key.Groups["indent"].Value, YamlPlain);
                Add(key.Groups["dash"].Value, YamlPunctuation);
                Add(key.Groups["key"].Value, YamlKey);
                Add(key.Groups["colon"].Value, YamlPunctuation);
                Value(key.Groups["rest"].Value);
            }
            else if (ListLine.Match(line) is { Success: true } item)
            {
                Add(item.Groups["indent"].Value, YamlPlain);
                Add(item.Groups["dash"].Value, YamlPunctuation);
                Value(item.Groups["rest"].Value);
            }
            else
                Add(line, YamlPlain);
            if (i < lines.Length - 1)
                runs.Add(new Run("\n"));
        }
        return runs;
    }

    // ── Controls ──

    /// <summary>
    /// A read-only code view: the runs of <paramref name="runs"/> in a selectable mono text (line numbers on the left
    /// when asked), scrolling both ways (wrapped when <paramref name="wrap"/>).
    /// </summary>
    public static CodeBlock View(bool lineNumbers = true, bool wrap = false) => new(lineNumbers, wrap);

    /// <summary>A code editor: a mono text box that keeps tabs and new lines, no wrap, scrolling both ways.</summary>
    public static TextBox Editor(string? placeholder = null)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Ui.Mono,
            FontSize = 13,
            PlaceholderText = placeholder,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 120,
            CaretBrush = Ui.Accent
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        return box;
    }
}

/// <summary>A read-only view of code in colours, with optional line numbers.</summary>
internal sealed class CodeBlock : Border
{
    private readonly SelectableTextBlock _text;
    private readonly TextBlock? _numbers;

    public CodeBlock(bool lineNumbers, bool wrap)
    {
        _text = new SelectableTextBlock
        {
            FontFamily = Ui.Mono,
            FontSize = 13,
            Foreground = Ui.Text,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Padding = new Thickness(8, 6)
        };
        Control content = _text;
        if (lineNumbers && !wrap)
        {
            _numbers = new TextBlock
            {
                FontFamily = Ui.Mono,
                FontSize = 13,
                Foreground = Ui.Muted,
                TextAlignment = TextAlignment.Right,
                Padding = new Thickness(8, 6, 4, 6),
                MinWidth = 36
            };
            content = Ui.Dock((_numbers, Avalonia.Controls.Dock.Left), (_text, Avalonia.Controls.Dock.Left));
        }
        var scroll = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Child = scroll;
        Background = Ui.Field;
        BorderBrush = Ui.Edge;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(2);
        MinHeight = 80;
    }

    /// <summary>The selectable text (to read its selection).</summary>
    public SelectableTextBlock TextBlock => _text;

    /// <summary>Shows <paramref name="text"/> through <paramref name="colour"/> (its runs).</summary>
    public void Show(string text, Func<string, List<Inline>> colour)
    {
        _text.Inlines ??= new InlineCollection();
        _text.Inlines.Clear();
        _text.Inlines.AddRange(colour(text ?? string.Empty));
        if (_numbers is not null)
        {
            var count = (text ?? string.Empty).Count(c => c == '\n') + 1;
            _numbers.Text = string.Join('\n', Enumerable.Range(1, Math.Min(count, 200_000)));
        }
    }

    public void ShowCode(string? language, string code) => Show(code, c => CodeSyntax.Code(language, c));

    public void ShowJson(string json) => Show(json, CodeSyntax.Json);

    public void ShowYaml(string yaml) => Show(yaml, CodeSyntax.Yaml);
}
