using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KAONOMI.Chat.Models;

/// <summary>Preformatted style of a paragraph of the tape (as the styles of Word).</summary>
public enum TapeParagraphKind
{
    Normal,
    Title,
    Subtitle,
    Heading1,
    Heading2,
    Heading3
}

/// <summary>Alignment of a paragraph of the tape.</summary>
public enum TapeAlignment
{
    Left,
    Center,
    Right
}

/// <summary>
/// Formatting of a piece of text of the tape. Null font, size or color = the default of the tape (its monospace font,
/// 13 px, dark gray). <see cref="Link"/> makes the text a clickable link to that address.
/// </summary>
public sealed record TapeStyle(
    string? Font = null,
    double? Size = null,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strike = false,
    string? Color = null,
    string? Link = null)
{
    /// <summary>No formatting: the default of the tape.</summary>
    public static readonly TapeStyle Plain = new();

    /// <summary>Style of the session lines (<c>── date ──</c>) of the calculator: bold, amber.</summary>
    public static readonly TapeStyle Session = new(Bold: true, Color: "#B26A00");

    /// <summary>True when something other than bold, italic, underline and strikethrough is set.</summary>
    public bool HasSpan => Font is not null || Size is not null || Color is not null;

    /// <summary>
    /// <paramref name="address"/> as a link: trimmed, with <c>https://</c> added to a web address typed without it
    /// (<c>www.kaonomi.net</c>); null when empty.
    /// </summary>
    public static string? NormalizeLink(string? address)
    {
        var link = address?.Trim();
        if (string.IsNullOrEmpty(link))
            return null;
        var hasScheme = Regex.IsMatch(link, @"^[A-Za-z][A-Za-z0-9+.-]*:") && !Regex.IsMatch(link, @"^[A-Za-z]:[\\/]");
        var isPath = link.StartsWith('/') || link.StartsWith('~') || link.StartsWith('\\') || Regex.IsMatch(link, @"^[A-Za-z]:[\\/]");
        return hasScheme || isPath ? link : "https://" + link;
    }
}

/// <summary>A piece of text of a paragraph, in one style.</summary>
public sealed class TapeRun(string text, TapeStyle style)
{
    public string Text { get; set; } = text;

    public TapeStyle Style { get; set; } = style;
}

/// <summary>A block of the tape: a paragraph of text or a picture.</summary>
public abstract class TapeBlock
{
    /// <summary>Number of caret positions inside the block: the characters of a paragraph, 1 for a picture.</summary>
    public abstract int Length { get; }

    public abstract TapeBlock Clone();

    /// <summary>
    /// The line(s) of the file the block was read from, kept by a document read with <c>keepSource</c>: written back as
    /// they were while the block is unchanged (see <see cref="TapeDocument.KeepSource"/>).
    /// </summary>
    public string? Source { get; set; }

    /// <summary>The block as it was read, written in the format of the tape (to know whether it changed since).</summary>
    public string? SourceCanonical { get; set; }

    /// <summary><paramref name="copy"/> with the source lines of this block.</summary>
    protected T WithSource<T>(T copy) where T : TapeBlock
    {
        copy.Source = Source;
        copy.SourceCanonical = SourceCanonical;
        return copy;
    }
}

/// <summary>A picture of the tape (<c>![alt|width](path)</c>), shown at <see cref="Width"/> pixels at most.</summary>
public sealed class TapeImage(string alt, string url, int width) : TapeBlock
{
    public string Alt { get; } = alt;

    /// <summary>The link as written (<c>&lt;path&gt;</c> when it holds spaces).</summary>
    public string Url { get; } = url;

    public int Width { get; set; } = width;

    /// <summary>Left, centered or right in the width of the page; written as <c>&lt;p align="…"&gt;</c> around the image.</summary>
    public TapeAlignment Alignment { get; set; }

    public override int Length => 1;

    public override TapeBlock Clone() => WithSource(new TapeImage(Alt, Url, Width) { Alignment = Alignment });
}

/// <summary>
/// A block of code of the tape, shown with the syntax coloring of its language; written in the file as a Markdown
/// fenced block (<c>```python</c> … <c>```</c>).
/// </summary>
public sealed class TapeCode(string language, string code) : TapeBlock
{
    /// <summary>Language of the code (python, csharp, javascript…; empty: plain text).</summary>
    public string Language { get; set; } = language;

    /// <summary>The code, its lines separated by <c>\n</c>.</summary>
    public string Code { get; set; } = code;

    public override int Length => 1;

    public override TapeBlock Clone() => WithSource(new TapeCode(Language, Code));
}

/// <summary>A paragraph of the tape: runs of styled text on one line of the file.</summary>
public sealed class TapeParagraph : TapeBlock
{
    public List<TapeRun> Runs { get; } = [];

    public TapeAlignment Alignment { get; set; }

    /// <summary>Preformatted style of the paragraph: its text takes the size, weight and color of that style.</summary>
    public TapeParagraphKind Kind { get; set; }

    /// <summary>
    /// The formatting of the text of a paragraph of <paramref name="kind"/>: a Title, Subtitle or heading is larger (and
    /// bold, and colored); the formatting of the text itself comes first.
    /// </summary>
    public static TapeStyle KindStyle(TapeParagraphKind kind) => kind switch
    {
        TapeParagraphKind.Title => new TapeStyle(Size: 28, Bold: true, Color: "#111827"),
        TapeParagraphKind.Subtitle => new TapeStyle(Size: 17, Color: "#6B7280"),
        TapeParagraphKind.Heading1 => new TapeStyle(Size: 22, Bold: true, Color: "#1E3A8A"),
        TapeParagraphKind.Heading2 => new TapeStyle(Size: 18, Bold: true, Color: "#1E40AF"),
        TapeParagraphKind.Heading3 => new TapeStyle(Size: 15, Bold: true, Color: "#374151"),
        _ => TapeStyle.Plain
    };

    /// <summary>Space above a paragraph of <paramref name="kind"/> (none for the first block of the tape), in pixels.</summary>
    public static double SpaceBefore(TapeParagraphKind kind) => kind switch
    {
        TapeParagraphKind.Title or TapeParagraphKind.Heading1 => 10,
        TapeParagraphKind.Heading2 => 8,
        TapeParagraphKind.Heading3 => 6,
        TapeParagraphKind.Subtitle => 2,
        _ => 0
    };

    /// <summary><paramref name="style"/> of a run as it shows in this paragraph (its own formatting over the one of <see cref="Kind"/>).</summary>
    public TapeStyle Effective(TapeStyle style)
    {
        if (Kind == TapeParagraphKind.Normal)
            return style;
        var kind = KindStyle(Kind);
        return style with
        {
            Font = style.Font ?? kind.Font,
            Size = style.Size ?? kind.Size,
            Bold = style.Bold || kind.Bold,
            Italic = style.Italic || kind.Italic,
            Color = style.Color ?? kind.Color
        };
    }

    /// <summary>The text of the paragraph, without its formatting.</summary>
    public string Text => string.Concat(Runs.Select(r => r.Text));

    /// <summary>Style of the character at <paramref name="index"/> (the one after the caret at that offset).</summary>
    public TapeStyle StyleOfCharacter(int index) => StyleAt(Math.Clamp(index + 1, 1, Math.Max(1, Length)));

    /// <summary>
    /// The characters around <paramref name="index"/> that make one link (neighbor characters with the same link), or
    /// null when that character is not in a link.
    /// </summary>
    public (int Start, int End)? LinkRange(int index)
    {
        if (index < 0 || index >= Length || StyleOfCharacter(index).Link is not { } link)
            return null;
        var position = 0;
        int? start = null;
        foreach (var run in Runs)
        {
            var end = position + run.Text.Length;
            if (run.Style.Link == link && run.Text.Length > 0)
                start ??= position;
            else if (start is not null && position > index)
                return (start.Value, position);
            else
                start = null;
            position = end;
        }
        return start is null ? null : (start.Value, position);
    }

    public override int Length => Runs.Sum(r => r.Text.Length);

    public override TapeBlock Clone()
    {
        var copy = new TapeParagraph { Alignment = Alignment, Kind = Kind };
        copy.Runs.AddRange(Runs.Select(r => new TapeRun(r.Text, r.Style)));
        return WithSource(copy);
    }

    /// <summary>
    /// Style of the text typed at <paramref name="offset"/>: the one of the character before it (the first character at
    /// the start of the paragraph).
    /// </summary>
    public TapeStyle StyleAt(int offset)
    {
        var position = 0;
        foreach (var run in Runs)
        {
            if (offset == 0 || offset > position && offset <= position + run.Text.Length)
                return run.Style;
            position += run.Text.Length;
        }
        return Runs.Count > 0 ? Runs[^1].Style : TapeStyle.Plain;
    }

    /// <summary>Inserts <paramref name="text"/> in <paramref name="style"/> at <paramref name="offset"/>.</summary>
    public void Insert(int offset, string text, TapeStyle style)
    {
        if (text.Length == 0)
            return;
        Split(offset);
        var index = RunIndexAt(offset);
        Runs.Insert(index, new TapeRun(text, style));
        Normalize();
    }

    /// <summary>Removes the characters from <paramref name="start"/> to <paramref name="end"/>.</summary>
    public void Delete(int start, int end)
    {
        if (end <= start)
            return;
        Split(start);
        Split(end);
        Runs.RemoveRange(RunIndexAt(start), RunIndexAt(end) - RunIndexAt(start));
        Normalize();
    }

    /// <summary>Changes the style of the characters from <paramref name="start"/> to <paramref name="end"/>.</summary>
    public void Restyle(int start, int end, Func<TapeStyle, TapeStyle> change)
    {
        if (end <= start)
            return;
        Split(start);
        Split(end);
        for (var i = RunIndexAt(start); i < RunIndexAt(end); i++)
            Runs[i].Style = change(Runs[i].Style);
        Normalize();
    }

    /// <summary>The characters from <paramref name="start"/> to <paramref name="end"/>, with their styles.</summary>
    public TapeParagraph Slice(int start, int end)
    {
        var copy = (TapeParagraph)Clone();
        copy.Delete(end, copy.Length);
        copy.Delete(0, start);
        return copy;
    }

    /// <summary>Cuts the paragraph at <paramref name="offset"/>; the text after it is returned as a new paragraph.</summary>
    public TapeParagraph SplitOff(int offset)
    {
        var tail = Slice(offset, Length);
        // An empty line keeps the style of the text before it, for the text typed there.
        if (tail.Runs.Count == 0)
            tail.Runs.Add(new TapeRun(string.Empty, StyleAt(offset)));
        Delete(offset, Length);
        return tail;
    }

    /// <summary>Adds the runs of <paramref name="other"/> at the end of this paragraph.</summary>
    public void Append(TapeParagraph other)
    {
        Runs.AddRange(other.Runs.Select(r => new TapeRun(r.Text, r.Style)));
        Normalize();
    }

    /// <summary>Makes a run boundary at <paramref name="offset"/>.</summary>
    private void Split(int offset)
    {
        var position = 0;
        for (var i = 0; i < Runs.Count; i++)
        {
            var run = Runs[i];
            if (offset > position && offset < position + run.Text.Length)
            {
                var cut = offset - position;
                Runs.Insert(i + 1, new TapeRun(run.Text[cut..], run.Style));
                run.Text = run.Text[..cut];
                return;
            }
            position += run.Text.Length;
        }
    }

    /// <summary>Index of the first run starting at or after <paramref name="offset"/> (a run boundary).</summary>
    private int RunIndexAt(int offset)
    {
        var position = 0;
        for (var i = 0; i < Runs.Count; i++)
        {
            if (position >= offset)
                return i;
            position += Runs[i].Text.Length;
        }
        return Runs.Count;
    }

    /// <summary>Joins the neighbor runs of the same style and drops the empty ones (one is kept for an empty line).</summary>
    public void Normalize()
    {
        for (var i = Runs.Count - 1; i > 0; i--)
            if (Runs[i].Style == Runs[i - 1].Style)
            {
                Runs[i - 1].Text += Runs[i].Text;
                Runs.RemoveAt(i);
            }
        if (Runs.Count > 1)
            Runs.RemoveAll(r => r.Text.Length == 0);
        if (Runs.Count == 0)
            Runs.Add(new TapeRun(string.Empty, TapeStyle.Plain));
    }
}

/// <summary>
/// The calculator tape as a document: its paragraphs (one per line of <c>calculator-tape.txt</c>) and pictures. The file
/// stays Markdown: <c>**bold**</c>, <c>*italic*</c>, <c>&lt;u&gt;</c>, <c>&lt;s&gt;</c>,
/// <c>&lt;span style="font-family:…;font-size:…px;color:#…"&gt;</c> for the font, size and color, and
/// <c>&lt;p align="center|right"&gt;</c> for the alignment; the older lines (<c># title</c>, <c>`code`</c>, session
/// lines) are read too.
/// </summary>
public sealed class TapeDocument
{
    public List<TapeBlock> Blocks { get; } = [];

    /// <summary>
    /// True for a document read with <c>keepSource</c> (the journal): each block keeps the line(s) it was read from and
    /// writes them back as they were while it is unchanged, so that the Markdown the tape does not write itself (<c>_italic_</c>,
    /// <c>2 * 3</c>, <c>a &lt; b</c>, a picture without its width…) is never rewritten; a changed block is written in the
    /// format of the tape, its inline code as <c>`code`</c>.
    /// </summary>
    public bool KeepSource { get; private set; }

    /// <summary>Reads the text of the tape.</summary>
    public static TapeDocument Parse(string text) => Parse(text, keepSource: false);

    /// <summary>Reads the text of the tape; with <paramref name="keepSource"/>, the unchanged blocks are written back as they were read.</summary>
    public static TapeDocument Parse(string text, bool keepSource)
    {
        var document = new TapeDocument { KeepSource = keepSource };
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        // The newline at the end of the last line is not an empty paragraph.
        if (lines.Count > 1 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        for (var i = 0; i < lines.Count; i++)
        {
            // A fenced block of code: its lines up to the closing fence (or the end of the tape).
            if (CodeFence.Match(lines[i]) is { Success: true } fence)
            {
                var code = new List<string>();
                var end = i + 1;
                // Only "```" at the start of a line closes it (a line of code " ```" was saved with a space in front).
                while (end < lines.Count && lines[end].TrimEnd() != "```")
                    code.Add(lines[end++]);
                var block = new TapeCode(fence.Groups["language"].Value, string.Join('\n', code));
                // Kept only when closed: an open block written back would take in the lines added after it.
                if (keepSource && end < lines.Count)
                    Keep(block, string.Join('\n', lines.Skip(i).Take(end - i + 1)));
                document.Blocks.Add(block);
                i = end;
                continue;
            }
            var parsed = ParseLine(lines[i]);
            if (keepSource)
                Keep(parsed, lines[i]);
            document.Blocks.Add(parsed);
        }
        if (document.Blocks.Count == 0)
            document.Blocks.Add(EmptyParagraph());
        return document;
    }

    /// <summary>Keeps <paramref name="source"/> on <paramref name="block"/>, with the block as the tape writes it.</summary>
    private static void Keep(TapeBlock block, string source)
    {
        block.Source = source;
        block.SourceCanonical = SerializeBlock(block, markdownCode: true);
    }

    /// <summary>The line(s) of a block of a <see cref="KeepSource"/> document: its source while unchanged.</summary>
    private static string SerializeKept(TapeBlock block)
    {
        var canonical = SerializeBlock(block, markdownCode: true);
        if (block.Source is { } source && canonical == block.SourceCanonical)
            return source;
        return block is TapeParagraph ? Lighten(canonical) : canonical;
    }

    /// <summary>
    /// <paramref name="line"/> (a paragraph in the format of the tape) with the escapes it does not need removed, for a
    /// Markdown that reads as it was typed: <c>\* item</c> → <c>* item</c>, <c>\#### x</c> → <c>#### x</c>,
    /// <c>C:\\Users</c> → <c>C:\Users</c>. Each one is removed only when the line still reads the same without it.
    /// </summary>
    private static string Lighten(string line)
    {
        if (!line.Contains('\\') || line.Length > 4000)
            return line;
        var canonical = SerializeBlock(ParseLine(line), markdownCode: true);
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] != '\\')
                continue;
            var candidate = line.Remove(i, 1);
            if (SerializeBlock(ParseLine(candidate), markdownCode: true) == canonical)
                line = candidate;
            else
                i++; // The escaped character is kept with its backslash.
        }
        return line;
    }

    /// <summary>An empty paragraph.</summary>
    public static TapeParagraph EmptyParagraph(TapeStyle? style = null)
    {
        var paragraph = new TapeParagraph();
        paragraph.Runs.Add(new TapeRun(string.Empty, style ?? TapeStyle.Plain));
        return paragraph;
    }

    /// <summary>The text of the tape: one line per block, a newline at the end (none when the tape is empty).</summary>
    public string Serialize()
    {
        if (Blocks.Count == 1 && Blocks[0] is TapeParagraph { Length: 0 })
            return string.Empty;
        var text = new StringBuilder();
        foreach (var block in Blocks)
            text.Append(KeepSource ? SerializeKept(block) : SerializeBlock(block)).Append('\n');
        return text.ToString();
    }

    /// <summary>One line of the file holding <paramref name="text"/> as it is (its formatting characters escaped).</summary>
    public static string PlainLine(string text)
    {
        var paragraph = EmptyParagraph();
        paragraph.Insert(0, text.Replace("\r", string.Empty).Replace("\n", " "), TapeStyle.Plain);
        return SerializeBlock(paragraph);
    }

    /// <summary>One line of the file for <paramref name="block"/>.</summary>
    public static string SerializeBlock(TapeBlock block) => SerializeBlock(block, markdownCode: false);

    /// <summary>
    /// One line of the file for <paramref name="block"/>; with <paramref name="markdownCode"/>, the inline code (text in
    /// the code font only) is written <c>`code`</c> instead of a span of that font.
    /// </summary>
    public static string SerializeBlock(TapeBlock block, bool markdownCode)
    {
        if (block is TapeImage image)
            return image.Alignment == TapeAlignment.Left
                ? $"![{image.Alt}|{image.Width}]({image.Url})"
                : $"<p align=\"{image.Alignment.ToString().ToLowerInvariant()}\">![{image.Alt}|{image.Width}]({image.Url})</p>";
        // A line of code that is itself a fence would close the block: it gets a space in front.
        if (block is TapeCode code)
            return $"```{code.Language}\n{string.Join('\n', code.Code.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim() == "```" ? " " + l : l))}\n```";

        var paragraph = (TapeParagraph)block;
        var text = new StringBuilder();
        var runs = paragraph.Runs.Where(r => r.Text.Length > 0).ToList();
        for (var i = 0; i < runs.Count;)
        {
            // Neighbor runs of the same link are one link: [formatted text](address).
            if (runs[i].Style.Link is { } link)
            {
                text.Append('[');
                while (i < runs.Count && runs[i].Style.Link == link)
                    text.Append(SerializeRun(runs[i++], markdownCode));
                text.Append("](").Append(link.Contains(' ') || link.Contains(')') || link.Contains('(') ? $"<{link}>" : link).Append(')');
            }
            else
                text.Append(SerializeRun(runs[i++], markdownCode));
        }
        // Text that would be read back as a picture or a heading.
        if (text.Length > 0 && text[0] is '!' or '#')
            text.Insert(0, '\\');
        // Headings as in Markdown (# ## ###); Title and Subtitle, and the alignment, as attributes of <p>.
        text.Insert(0, paragraph.Kind switch
        {
            TapeParagraphKind.Heading1 => "# ",
            TapeParagraphKind.Heading2 => "## ",
            TapeParagraphKind.Heading3 => "### ",
            _ => string.Empty
        });
        var attributes = new StringBuilder();
        if (paragraph.Kind is TapeParagraphKind.Title or TapeParagraphKind.Subtitle)
            attributes.Append($" class=\"{paragraph.Kind.ToString().ToLowerInvariant()}\"");
        if (paragraph.Alignment != TapeAlignment.Left)
            attributes.Append($" align=\"{paragraph.Alignment.ToString().ToLowerInvariant()}\"");
        return attributes.Length > 0 ? $"<p{attributes}>{text}</p>" : text.ToString();
    }

    private static string SerializeRun(TapeRun run, bool markdownCode)
    {
        var style = run.Style;
        // Inline code: `code` (read back in the code font).
        if (markdownCode && style.Font == CodeFont && style.Size is null && style.Color is null)
            style = style with { Font = null };
        else
            markdownCode = false;
        var text = markdownCode ? "`" + EscapeCode(run.Text) + "`" : Escape(run.Text);
        if (style.Bold && style.Italic)
            text = $"***{text}***";
        else if (style.Bold)
            text = $"**{text}**";
        else if (style.Italic)
            text = $"*{text}*";
        if (style.Strike)
            text = $"<s>{text}</s>";
        if (style.Underline)
            text = $"<u>{text}</u>";
        if (style.HasSpan)
        {
            var css = new List<string>();
            if (style.Font is not null)
                css.Add($"font-family:'{style.Font.Replace("'", string.Empty)}'");
            if (style.Size is { } size)
                css.Add($"font-size:{size.ToString("0.#", CultureInfo.InvariantCulture)}px");
            if (style.Color is not null)
                css.Add($"color:{style.Color}");
            text = $"<span style=\"{string.Join(';', css)}\">{text}</span>";
        }
        return text;
    }

    /// <summary>The characters that would be read as formatting, escaped.</summary>
    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
            escaped.Append(c switch
            {
                '\\' => "\\\\",
                '*' => "\\*",
                '`' => "\\`",
                '[' => "\\[",
                ']' => "\\]",
                '&' => "&amp;",
                '<' => "&lt;",
                _ => c.ToString()
            });
        return escaped.ToString();
    }

    /// <summary>The characters of inline code that would be read as an escape or an entity (or close it), escaped.</summary>
    private static string EscapeCode(string text) =>
        Regex.Replace(text.Replace("\\", "\\\\").Replace("`", "\\`"), "&(?=amp;|lt;|gt;)", "&amp;");

    /// <summary>The alignment written in <c>align="…"</c>.</summary>
    private static TapeAlignment ParseAlignment(string value) => value.ToLowerInvariant() switch
    {
        "center" => TapeAlignment.Center,
        "right" => TapeAlignment.Right,
        _ => TapeAlignment.Left
    };

    private static TapeBlock ParseLine(string line)
    {
        if (ImageLine.Match(line) is { Success: true } image)
            return new TapeImage(image.Groups["alt"].Value, image.Groups["url"].Value,
                int.TryParse(image.Groups["width"].Value, out var width) && width > 0 ? width : DefaultImageWidth);
        // A centered or right-aligned image: <p align="center">![…](…)</p>.
        if (ParagraphTag.Match(line) is { Success: true } aligned && ImageLine.Match(aligned.Groups["text"].Value) is { Success: true } inner)
        {
            var picture = new TapeImage(inner.Groups["alt"].Value, inner.Groups["url"].Value,
                int.TryParse(inner.Groups["width"].Value, out var pictureWidth) && pictureWidth > 0 ? pictureWidth : DefaultImageWidth);
            foreach (Match attribute in TagAttribute.Matches(aligned.Groups["attributes"].Value))
                if (attribute.Groups["name"].Value.Equals("align", StringComparison.OrdinalIgnoreCase))
                    picture.Alignment = ParseAlignment(attribute.Groups["value"].Value);
            return picture;
        }

        var paragraph = new TapeParagraph();
        var content = line;
        if (ParagraphTag.Match(line) is { Success: true } tag)
        {
            foreach (Match attribute in TagAttribute.Matches(tag.Groups["attributes"].Value))
            {
                var value = attribute.Groups["value"].Value.ToLowerInvariant();
                switch (attribute.Groups["name"].Value.ToLowerInvariant())
                {
                    case "align":
                        paragraph.Alignment = ParseAlignment(value);
                        break;
                    case "class":
                        paragraph.Kind = value switch
                        {
                            "title" => TapeParagraphKind.Title,
                            "subtitle" => TapeParagraphKind.Subtitle,
                            _ => paragraph.Kind
                        };
                        break;
                }
            }
            content = tag.Groups["text"].Value;
        }

        // Markdown headings, and the session lines of the calculator.
        var baseStyle = TapeStyle.Plain;
        if (content.StartsWith("### ", StringComparison.Ordinal))
            (paragraph.Kind, content) = (TapeParagraphKind.Heading3, content[4..]);
        else if (content.StartsWith("## ", StringComparison.Ordinal))
            (paragraph.Kind, content) = (TapeParagraphKind.Heading2, content[3..]);
        else if (content.StartsWith("# ", StringComparison.Ordinal))
            (paragraph.Kind, content) = (TapeParagraphKind.Heading1, content[2..]);
        else if (content.TrimStart().StartsWith("──", StringComparison.Ordinal) && !content.Contains('<') && !content.Contains('*'))
            baseStyle = TapeStyle.Session;

        ParseInline(content, baseStyle, paragraph.Runs);
        paragraph.Normalize();
        return paragraph;
    }

    /// <summary>Reads the inline formatting of <paramref name="text"/> into <paramref name="runs"/>.</summary>
    private static void ParseInline(string text, TapeStyle baseStyle, List<TapeRun> runs)
    {
        var styles = new Stack<TapeStyle>();
        styles.Push(baseStyle);
        // Markers open and closed as toggles: ***, **, *, ` (a marker without its closing one is plain text).
        bool bold = false, italic = false, code = false;
        var current = new StringBuilder();

        TapeStyle Style()
        {
            var style = styles.Peek();
            return style with
            {
                Bold = style.Bold || bold,
                Italic = style.Italic || italic,
                Font = code ? CodeFont : style.Font
            };
        }

        void Flush()
        {
            if (current.Length > 0)
                runs.Add(new TapeRun(current.ToString(), Style()));
            current.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var rest = text.AsSpan(i);
            // A backslash escapes the characters the tape escapes; before any other character it is a backslash (C:\Users).
            if (rest[0] == '\\' && rest.Length > 1 && Escapable.Contains(rest[1]))
            {
                current.Append(rest[1]);
                i += 2;
            }
            else if (rest.StartsWith("&amp;"))
            {
                current.Append('&');
                i += 5;
            }
            else if (rest.StartsWith("&lt;"))
            {
                current.Append('<');
                i += 4;
            }
            else if (rest.StartsWith("&gt;"))
            {
                current.Append('>');
                i += 4;
            }
            else if (!code && SpanOpen.Match(text, i) is { Success: true } span && span.Index == i)
            {
                Flush();
                styles.Push(ApplyCss(styles.Peek(), span.Groups["css"].Value));
                i += span.Length;
            }
            else if (!code && rest.StartsWith("</span>") && styles.Count > 1)
            {
                Flush();
                styles.Pop();
                i += 7;
            }
            else if (!code && (rest.StartsWith("<u>") || rest.StartsWith("<s>")))
            {
                Flush();
                styles.Push(rest[1] == 'u' ? styles.Peek() with { Underline = true } : styles.Peek() with { Strike = true });
                i += 3;
            }
            else if (!code && (rest.StartsWith("</u>") || rest.StartsWith("</s>")) && styles.Count > 1)
            {
                Flush();
                styles.Pop();
                i += 4;
            }
            else if (!code && rest[0] == '[' && InlineLink.Match(text, i) is { Success: true } link && link.Index == i)
            {
                // [text](address): the text, with its own formatting, is a link.
                Flush();
                var linkRuns = new List<TapeRun>();
                var address = link.Groups["url"].Value.Trim().TrimStart('<').TrimEnd('>');
                ParseInline(link.Groups["text"].Value, Style() with { Link = address }, linkRuns);
                runs.AddRange(linkRuns);
                i += link.Length;
            }
            else if (rest[0] == '`' && (code || HasCloser(text, i + 1, "`")))
            {
                Flush();
                code = !code;
                i += 1;
            }
            else if (!code && rest.StartsWith("***") && (bold && italic || HasCloser(text, i + 3, "***")))
            {
                Flush();
                (bold, italic) = (!bold, !italic);
                i += 3;
            }
            else if (!code && rest.StartsWith("**") && (bold || HasCloser(text, i + 2, "**")))
            {
                Flush();
                bold = !bold;
                i += 2;
            }
            else if (!code && rest[0] == '*' && (italic || HasCloser(text, i + 1, "*")))
            {
                Flush();
                italic = !italic;
                i += 1;
            }
            else
            {
                current.Append(rest[0]);
                i += 1;
            }
        }
        Flush();
    }

    /// <summary>True when <paramref name="marker"/> appears (not escaped) after <paramref name="from"/>.</summary>
    private static bool HasCloser(string text, int from, string marker)
    {
        for (var i = from; i <= text.Length - marker.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }
            if (string.CompareOrdinal(text, i, marker, 0, marker.Length) == 0)
                return true;
        }
        return false;
    }

    /// <summary><paramref name="style"/> with the font, size and color of a <c>style="…"</c> attribute.</summary>
    private static TapeStyle ApplyCss(TapeStyle style, string css)
    {
        foreach (var declaration in css.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = declaration[..colon].Trim().ToLowerInvariant();
            var value = declaration[(colon + 1)..].Trim().Trim('\'', '"');
            switch (name)
            {
                case "font-family" when value.Length > 0:
                    style = style with { Font = value };
                    break;
                case "font-size" when double.TryParse(value.Replace("px", string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size > 0:
                    style = style with { Size = size };
                    break;
                case "color" when HexColor.IsMatch(value):
                    style = style with { Color = value.ToUpperInvariant() };
                    break;
                case "font-weight" when value is "bold" or "700":
                    style = style with { Bold = true };
                    break;
                case "font-style" when value == "italic":
                    style = style with { Italic = true };
                    break;
            }
        }
        return style;
    }

    /// <summary>The characters a backslash escapes: the ones <see cref="Escape"/> escapes, and <c>!</c> and <c>#</c> at the start of a line.</summary>
    private const string Escapable = "\\*`[]!#";

    /// <summary>Font of the <c>`code`</c> of the older lines.</summary>
    private const string CodeFont = "Cascadia Mono, Menlo, Consolas, monospace";

    /// <summary>Width of a picture without a width in its line.</summary>
    public const int DefaultImageWidth = 480;

    /// <summary>The opening fence of a block of code: <c>```</c> and its language.</summary>
    private static readonly Regex CodeFence = new(@"^```(?<language>[A-Za-z0-9#+_.-]*)\s*$", RegexOptions.Compiled);

    private static readonly Regex ImageLine = new(
        @"^[ \t]*!\[(?<alt>[^\]|]*)(\|(?<width>\d+))?\]\((?<url><[^>\n]+>|[^)\s]+)\)[ \t]*$", RegexOptions.Compiled);

    /// <summary>A paragraph with attributes: <c>&lt;p class="title" align="center"&gt;…&lt;/p&gt;</c>.</summary>
    private static readonly Regex ParagraphTag = new(@"^<p(?<attributes>(?:\s+[A-Za-z]+=""[^""]*"")+)>(?<text>.*)</p>$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TagAttribute = new(@"(?<name>[A-Za-z]+)=""(?<value>[^""]*)""", RegexOptions.Compiled);

    /// <summary>A Markdown link <c>[text](address)</c> (not a picture), its text possibly formatted.</summary>
    private static readonly Regex InlineLink = new(@"\G\[(?<text>(?:\\.|[^\]\\])+)\]\((?<url><[^>\n]+>|[^)\s]+)\)", RegexOptions.Compiled);

    private static readonly Regex SpanOpen = new(@"\G<span style=""(?<css>[^""]*)"">", RegexOptions.Compiled);

    private static readonly Regex HexColor = new(@"^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);
}
