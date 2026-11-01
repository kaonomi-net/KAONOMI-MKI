using System.Linq;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KAONOMI.ChatGodot;

/// <summary>
/// Markdown of an answer as the BBCode of a Godot <c>RichTextLabel</c>, in the colors of the chat: headings in cyan,
/// bold, italic, strikethrough, inline code and blocks of code on a dark background, lists, quotes, links and tables.
/// </summary>
public static class MarkdownBbcode
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().UseAutoLinks().Build();

    // Colors of the current theme.
    private static string Heading => "#" + Eve.Eve.TextBright.ToHtml(false);
    private static string CodeColor => "#" + Eve.Eve.Amber.ToHtml(false);
    private static string CodeBackground => "#0A0E12";
    private static string QuoteColor => "#" + Eve.Eve.Muted.ToHtml(false);
    private static string LinkColor => "#" + Eve.Eve.Cyan.ToHtml(false);

    /// <summary>The BBCode of <paramref name="markdown"/>.</summary>
    public static string Convert(string markdown)
    {
        var document = Markdown.Parse(markdown ?? string.Empty, Pipeline);
        var text = new StringBuilder();
        foreach (var block in document)
            WriteBlock(block, text, 0);
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>Text with the square brackets of BBCode escaped.</summary>
    public static string Escape(string text) => text.Replace("[", "[lb]");

    private static void WriteBlock(Block block, StringBuilder text, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var size = heading.Level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 };
                text.Append($"[font_size={size}][b][color={Heading}]");
                WriteInlines(heading.Inline, text);
                text.Append("[/color][/b][/font_size]\n\n");
                break;
            case ParagraphBlock paragraph:
                WriteInlines(paragraph.Inline, text);
                text.Append(depth > 0 ? "\n" : "\n\n");
                break;
            case FencedCodeBlock or CodeBlock:
                var code = ((LeafBlock)block).Lines.ToString().TrimEnd();
                var language = block is FencedCodeBlock fenced ? fenced.Info : null;
                if (!string.IsNullOrWhiteSpace(language))
                    text.Append($"[color={QuoteColor}][font_size=11]{Escape(language!.ToUpperInvariant())}[/font_size][/color]\n");
                text.Append($"[bgcolor={CodeBackground}][color={CodeColor}][code]{Escape(code)}[/code][/color][/bgcolor]\n\n");
                break;
            case ListBlock list:
                var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    text.Append(new string(' ', depth * 3));
                    text.Append(list.IsOrdered ? $"[color={Heading}]{number++}.[/color] " : $"[color={Heading}]▸[/color] ");
                    foreach (var child in item)
                        WriteBlock(child, text, depth + 1);
                }
                if (depth == 0)
                    text.Append('\n');
                break;
            case QuoteBlock quote:
                text.Append($"[color={QuoteColor}][i]");
                foreach (var child in quote)
                {
                    text.Append("┃ ");
                    WriteBlock(child, text, depth + 1);
                }
                text.Append("[/i][/color]\n");
                break;
            case Table table:
                WriteTable(table, text);
                break;
            case ThematicBreakBlock:
                text.Append($"[color={Heading}]────────────────────────[/color]\n\n");
                break;
            case HtmlBlock html:
                text.Append(Escape(html.Lines.ToString())).Append("\n\n");
                break;
            case ContainerBlock container:
                foreach (var child in container)
                    WriteBlock(child, text, depth);
                break;
        }
    }

    /// <summary>A table as aligned monospace columns, its header in cyan.</summary>
    private static void WriteTable(Table table, StringBuilder text)
    {
        var rows = table.OfType<TableRow>().Select(row => row.OfType<TableCell>().Select(cell =>
        {
            var content = new StringBuilder();
            foreach (var paragraph in cell.OfType<ParagraphBlock>())
                WritePlain(paragraph.Inline, content);
            return content.ToString().Trim();
        }).ToList()).ToList();
        if (rows.Count == 0)
            return;
        var columns = rows.Max(r => r.Count);
        var widths = Enumerable.Range(0, columns).Select(c => rows.Max(r => c < r.Count ? r[c].Length : 0)).ToList();
        text.Append("[code]");
        for (var i = 0; i < rows.Count; i++)
        {
            var line = string.Join(" │ ", Enumerable.Range(0, columns).Select(c => (c < rows[i].Count ? rows[i][c] : string.Empty).PadRight(widths[c])));
            text.Append(i == 0 ? $"[color={Heading}]{Escape(line)}[/color]\n" : Escape(line) + "\n");
            if (i == 0)
                text.Append($"[color={Heading}]{string.Join("─┼─", widths.Select(w => new string('─', w)))}[/color]\n");
        }
        text.Append("[/code]\n");
    }

    private static void WriteInlines(ContainerInline? inlines, StringBuilder text)
    {
        if (inlines is null)
            return;
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(Escape(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    text.Append($"[bgcolor={CodeBackground}][color={CodeColor}][code]{Escape(code.Content)}[/code][/color][/bgcolor]");
                    break;
                case EmphasisInline emphasis:
                    var (open, close) = emphasis.DelimiterChar == '~'
                        ? ("[s]", "[/s]")
                        : emphasis.DelimiterCount >= 2 ? ("[b]", "[/b]") : ("[i]", "[/i]");
                    text.Append(open);
                    WriteInlines(emphasis, text);
                    text.Append(close);
                    break;
                case LinkInline { IsImage: true } image:
                    text.Append($"[color={LinkColor}]🖼 ");
                    WriteInlines(image, text);
                    text.Append("[/color]");
                    break;
                case LinkInline link:
                    text.Append($"[url={Escape(link.Url ?? string.Empty)}][color={LinkColor}][u]");
                    WriteInlines(link, text);
                    text.Append("[/u][/color][/url]");
                    break;
                case AutolinkInline auto:
                    text.Append($"[url={Escape(auto.Url)}][color={LinkColor}][u]{Escape(auto.Url)}[/u][/color][/url]");
                    break;
                case LineBreakInline:
                    text.Append('\n');
                    break;
                case HtmlInline html:
                    text.Append(Escape(html.Tag));
                    break;
                case ContainerInline container:
                    WriteInlines(container, text);
                    break;
            }
        }
    }

    /// <summary>The text of inlines without formatting (table cells).</summary>
    private static void WritePlain(ContainerInline? inlines, StringBuilder text)
    {
        if (inlines is null)
            return;
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case ContainerInline container:
                    WritePlain(container, text);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
            }
        }
    }
}
