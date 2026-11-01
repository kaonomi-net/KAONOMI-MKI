using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using KAONOMI.Chat.Services;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;
using GodotDictionary = Godot.Collections.Dictionary;
using TextMate = KAONOMI.Chat.Services.CodeHighlighter;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// The code editors of the dashboards: a Godot <see cref="CodeEdit"/> in the mono font of the chat, with the syntax
/// colours of the language (the TextMate grammars of <see cref="TextMate"/>, their VS Code Light+ colours turned into
/// the matching Dark+ ones for the dark windows), YAML (port of the Avalonia <c>YamlHighlighter</c>) or JSON.
/// </summary>
internal static class CodeEditors
{
    /// <summary>A code editor (line numbers, no wrap, mono), bound to nothing.</summary>
    public static CodeEdit Editor(bool editable = true, bool lineNumbers = true)
    {
        var edit = new CodeEdit
        {
            Editable = editable,
            GuttersDrawLineNumbers = lineNumbers,
            WrapMode = TextEdit.LineWrappingMode.None,
            ScrollPastEndOfFile = false,
            HighlightCurrentLine = editable,
            DrawTabs = false,
            IndentUseSpaces = true,
            IndentSize = 2,
            AutoBraceCompletionEnabled = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 120)
        };
        edit.AddThemeFontOverride("font", EveStyle.Mono);
        edit.AddThemeFontSizeOverride("font_size", 13);
        edit.AddThemeColorOverride("font_color", EveStyle.Text);
        edit.AddThemeColorOverride("font_readonly_color", EveStyle.Text);
        edit.AddThemeColorOverride("line_number_color", EveStyle.Muted with { A = 0.6f });
        edit.AddThemeColorOverride("current_line_color", Eve.Eve.Raised(0.035f));
        edit.AddThemeColorOverride("selection_color", EveStyle.Cyan with { A = 0.3f });
        edit.AddThemeColorOverride("caret_color", EveStyle.Cyan);
        return edit;
    }

    /// <summary>The colouring of JSON: a light Godot highlighter (it stays fast on responses of several megabytes).</summary>
    public static Godot.CodeHighlighter Json()
    {
        var highlighter = new Godot.CodeHighlighter
        {
            NumberColor = Color.FromHtml("#B5CEA8"),
            SymbolColor = Color.FromHtml("#E6B450"),
            FunctionColor = EveStyle.Text,
            MemberVariableColor = EveStyle.Text
        };
        highlighter.AddColorRegion("\"", "\"", Color.FromHtml("#CE9178"), false);
        foreach (var keyword in new[] { "true", "false", "null" })
            highlighter.AddKeywordColor(keyword, Color.FromHtml("#C4A7FF"));
        return highlighter;
    }

    /// <summary>A Light+ colour of the TextMate theme turned into a colour readable on the dark windows.</summary>
    public static Color ForDark(string light)
    {
        if (DarkPlus.TryGetValue(light.ToUpperInvariant(), out var mapped))
            return Color.FromHtml(mapped);
        var color = Color.FromHtml(light);
        var luminance = 0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B;
        return luminance < 0.55f ? color.Lightened(0.55f - luminance + 0.25f) : color;
    }

    /// <summary>The VS Code Light+ colours and their Dark+ counterparts.</summary>
    private static readonly Dictionary<string, string> DarkPlus = new()
    {
        ["#0000FF"] = "#569CD6", ["#001080"] = "#9CDCFE", ["#795E26"] = "#DCDCAA", ["#A31515"] = "#CE9178",
        ["#008000"] = "#6A9955", ["#098658"] = "#B5CEA8", ["#267F99"] = "#4EC9B0", ["#AF00DB"] = "#C586C0",
        ["#800000"] = "#569CD6", ["#E50000"] = "#9CDCFE", ["#FF0000"] = "#9CDCFE", ["#0070C1"] = "#4FC1FF",
        ["#811F3F"] = "#D16969", ["#EE0000"] = "#D7BA7D", ["#000080"] = "#569CD6", ["#0451A5"] = "#CE9178",
        ["#CD3131"] = "#F44747", ["#3030C0"] = "#9CDCFE", ["#16825D"] = "#B5CEA8", ["#800080"] = "#C586C0",
        ["#383838"] = "#D4D4D4", ["#6A737D"] = "#8B949E", ["#000000"] = "#D4D4D4"
    };

    /// <summary>BBCode of <paramref name="code"/> in the colours of <paramref name="language"/> (for a RichTextLabel).</summary>
    public static string Bbcode(string? language, string code)
    {
        var spans = TextMate.Highlight(language, code);
        var text = new StringBuilder("[code]");
        var at = 0;
        foreach (var span in spans)
        {
            if (span.Start < at || span.Start + span.Length > code.Length)
                continue;
            text.Append(Escape(code[at..span.Start]));
            var piece = Escape(code.Substring(span.Start, span.Length));
            if (span.Bold)
                piece = $"[b]{piece}[/b]";
            if (span.Italic)
                piece = $"[i]{piece}[/i]";
            text.Append($"[color=#{ForDark(span.Color).ToHtml(false)}]{piece}[/color]");
            at = span.Start + span.Length;
        }
        text.Append(Escape(code[at..])).Append("[/code]");
        return text.ToString();
    }

    private static string Escape(string text) => text.Replace("[", "[lb]");
}

/// <summary>
/// The syntax colours of a language in a <see cref="CodeEdit"/>, from the TextMate grammars of <see cref="TextMate"/>
/// (computed for the whole text when it changes; above <see cref="MaxLength"/> characters the text stays plain).
/// </summary>
public partial class CodeSyntaxHighlighter : SyntaxHighlighter
{
    private const int MaxLength = 300_000;
    private readonly Dictionary<int, GodotDictionary> _lines = new();
    private bool _dirty = true;
    private TextEdit? _watched;

    public CodeSyntaxHighlighter()
    {
    }

    public CodeSyntaxHighlighter(string? language) => Language = language;

    /// <summary>The language (a TextMate id or a usual alias: cs, py, js…); empty for plain text.</summary>
    public string? Language
    {
        get => _language;
        set
        {
            _language = value;
            _dirty = true;
            ClearHighlightingCache();
        }
    }

    private string? _language;

    public override void _ClearHighlightingCache() => _dirty = true;

    private void Compute(TextEdit edit)
    {
        if (!ReferenceEquals(_watched, edit))
        {
            _watched = edit;
            edit.TextChanged += () => _dirty = true;
            edit.TextSet += () => _dirty = true;
            _dirty = true;
        }
        if (!_dirty)
            return;
        _dirty = false;
        var text = edit.Text;
        _lines.Clear();
        if (text.Length > MaxLength || string.IsNullOrWhiteSpace(Language))
            return;

        // Offsets of the line starts, to cut the spans by line.
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                starts.Add(i + 1);
        var line = 0;
        foreach (var span in TextMate.Highlight(Language, text))
        {
            var start = span.Start;
            var end = span.Start + span.Length;
            while (line + 1 < starts.Count && starts[line + 1] <= start)
                line++;
            var color = CodeEditors.ForDark(span.Color);
            var l = line;
            while (start < end && l < starts.Count)
            {
                var lineEnd = l + 1 < starts.Count ? starts[l + 1] - 1 : text.Length;
                var pieceEnd = Math.Min(end, lineEnd);
                if (pieceEnd > start)
                {
                    if (!_lines.TryGetValue(l, out var columns))
                        _lines[l] = columns = new GodotDictionary();
                    columns[start - starts[l]] = new GodotDictionary { ["color"] = color };
                    var after = pieceEnd - starts[l];
                    if (!columns.ContainsKey(after))
                        columns[after] = new GodotDictionary { ["color"] = EveStyle.Text };
                }
                start = lineEnd + 1;
                l++;
            }
        }
    }

    public override GodotDictionary _GetLineSyntaxHighlighting(int line)
    {
        if (GetTextEdit() is not { } edit)
            return new GodotDictionary();
        Compute(edit);
        return _lines.TryGetValue(line, out var columns) ? columns : new GodotDictionary();
    }
}

/// <summary>
/// Port of the Avalonia <c>YamlHighlighter</c> (its dark palette) as a Godot syntax highlighter: comments, keys, list
/// dashes and colons, and the values by kind (booleans, numbers, quoted strings, URLs, other text). The italics of the
/// comments are not available in a CodeEdit (colours only).
/// </summary>
public partial class YamlSyntaxHighlighter : SyntaxHighlighter
{
    private static readonly Color Key = Color.FromHtml("#7DD3FC");
    private static readonly Color Punctuation = Color.FromHtml("#E6B450");
    private static readonly Color Comment = Color.FromHtml("#8B949E");
    private static readonly Color TextValue = Color.FromHtml("#E3A27A");
    private static readonly Color Number = Color.FromHtml("#B5CEA8");
    private static readonly Color Boolean = Color.FromHtml("#C4A7FF");
    private static readonly Color Url = Color.FromHtml("#4FD1E8");
    private static readonly Color Plain = Color.FromHtml("#E6E6E6");

    private static readonly Regex CommentLine = new(@"^\s*#", RegexOptions.Compiled);
    private static readonly Regex KeyLine = new(@"^(?<indent>\s*)(?<dash>-\s+)?(?<key>[\w.\-]+)(?<colon>\s*:)(?<rest>(\s.*)?)$", RegexOptions.Compiled);
    private static readonly Regex ListLine = new(@"^(?<indent>\s*)(?<dash>-\s+)(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex TrailingComment = new(@"\s#.*$", RegexOptions.Compiled);
    private static readonly Regex BooleanValue = new(@"^(true|false|yes|no|on|off|null|~)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NumberValue = new(@"^[-+]?\d+(\.\d+)?$", RegexOptions.Compiled);

    public override GodotDictionary _GetLineSyntaxHighlighting(int line)
    {
        var result = new GodotDictionary();
        if (GetTextEdit() is not { } edit)
            return result;
        var text = edit.GetLine(line);
        void At(int column, Color color) => result[column] = new GodotDictionary { ["color"] = color };

        if (CommentLine.IsMatch(text))
        {
            At(0, Comment);
            return result;
        }
        if (KeyLine.Match(text) is { Success: true } key)
        {
            At(0, Plain);
            if (key.Groups["dash"].Success && key.Groups["dash"].Length > 0)
                At(key.Groups["dash"].Index, Punctuation);
            At(key.Groups["key"].Index, Key);
            At(key.Groups["colon"].Index, Punctuation);
            Value(result, text, key.Groups["rest"].Index, key.Groups["rest"].Value);
            return result;
        }
        if (ListLine.Match(text) is { Success: true } item)
        {
            At(0, Plain);
            At(item.Groups["dash"].Index, Punctuation);
            Value(result, text, item.Groups["rest"].Index, item.Groups["rest"].Value);
            return result;
        }
        At(0, Plain);
        return result;
    }

    /// <summary>A value at <paramref name="start"/>, possibly followed by a comment (" # …").</summary>
    private static void Value(GodotDictionary result, string line, int start, string rest)
    {
        var comment = TrailingComment.Match(rest);
        var value = comment.Success ? rest[..comment.Index] : rest;
        var trimmed = value.Trim();
        var color = trimmed.Length == 0 ? Plain
            : BooleanValue.IsMatch(trimmed) ? Boolean
            : NumberValue.IsMatch(trimmed) ? Number
            : trimmed.StartsWith("http://") || trimmed.StartsWith("https://") ? Url
            : TextValue;
        if (rest.Length > 0)
            result[start] = new GodotDictionary { ["color"] = color };
        if (comment.Success)
            result[start + comment.Index] = new GodotDictionary { ["color"] = Comment };
    }
}
