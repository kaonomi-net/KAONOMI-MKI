using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.UI.Desk;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KAONOMI.UI.Chat;

/// <summary>
/// Markdown shown with Avalonia controls (Markdig): headings, paragraphs, bold, italic, inline code, links, lists,
/// quotes, code blocks, tables, rules. The text can be selected and copied.
/// </summary>
public static class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static Control Build(string markdown, double size = 13)
    {
        var panel = new StackPanel { Spacing = 6 };
        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(markdown ?? string.Empty, Pipeline);
        }
        catch (Exception)
        {
            panel.Children.Add(Plain(markdown ?? string.Empty, size));
            return panel;
        }
        foreach (var block in document)
            if (Block(block, size, 0) is { } control)
                panel.Children.Add(control);
        return panel;
    }

    /// <summary>A plain selectable text (a message of the user, an answer being revealed).</summary>
    public static SelectableTextBlock Plain(string text, double size = 13, IBrush? color = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color ?? Ui.Text,
        TextWrapping = TextWrapping.Wrap
    };

    private static Control? Block(Block block, double size, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
            {
                var text = Inlines(heading.Inline, size + Math.Max(0, 7 - heading.Level * 1.5));
                text.FontWeight = FontWeight.SemiBold;
                text.Foreground = Ui.TextBright;
                text.Margin = new Thickness(0, 4, 0, 0);
                return text;
            }
            case ParagraphBlock paragraph:
                return Inlines(paragraph.Inline, size);
            case FencedCodeBlock or CodeBlock:
            {
                var code = (LeafBlock)block;
                var lines = string.Join("\n", code.Lines.Lines.Take(code.Lines.Count).Select(l => l.ToString()));
                var language = (block as FencedCodeBlock)?.Info;
                var content = new StackPanel { Spacing = 2 };
                var header = Ui.Row(6, Ui.Caption(string.IsNullOrWhiteSpace(language) ? "code" : language!),
                    new Button { Content = "⧉ copy", Classes = { "small" }, Command = null });
                ((Button)header.Children[1]).Click += (_, _) => Dialogs.Copy(lines);
                content.Children.Add(header);
                content.Children.Add(new SelectableTextBlock { Text = lines, FontFamily = Ui.Mono, FontSize = size - 1, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap });
                return Ui.Box(content, 8, Ui.Field);
            }
            case QuoteBlock quote:
            {
                var inner = new StackPanel { Spacing = 4 };
                foreach (var child in quote)
                    if (Block(child, size, depth) is { } c)
                        inner.Children.Add(c);
                return new Border { Child = inner, BorderBrush = Ui.Warm, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2) };
            }
            case ListBlock list:
            {
                var items = new StackPanel { Spacing = 3, Margin = new Thickness(depth == 0 ? 4 : 14, 0, 0, 0) };
                var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var bullet = list.IsOrdered ? $"{number++}." : "•";
                    var body = new StackPanel { Spacing = 3 };
                    foreach (var child in item)
                        if (Block(child, size, depth + 1) is { } c)
                            body.Children.Add(c);
                    var row = new DockPanel();
                    var mark = new TextBlock { Text = bullet, Foreground = Ui.Accent, FontSize = size, Margin = new Thickness(0, 0, 6, 0), MinWidth = 14 };
                    DockPanel.SetDock(mark, Dock.Left);
                    row.Children.Add(mark);
                    row.Children.Add(body);
                    items.Children.Add(row);
                }
                return items;
            }
            case ThematicBreakBlock:
                return new Border { Height = 1, Background = Ui.Edge, Margin = new Thickness(0, 4) };
            case Table table:
                return TableOf(table, size);
            case HtmlBlock html:
                return Plain(html.Lines.ToString(), size, Ui.Muted);
            case ContainerBlock container:
            {
                var inner = new StackPanel { Spacing = 4 };
                foreach (var child in container)
                    if (Block(child, size, depth) is { } c)
                        inner.Children.Add(c);
                return inner;
            }
            case LeafBlock leaf when leaf.Inline is not null:
                return Inlines(leaf.Inline, size);
            default:
                return null;
        }
    }

    private static Control TableOf(Table table, double size)
    {
        var grid = new Grid();
        var rows = table.OfType<TableRow>().ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < rows[r].Count; c++)
            {
                var cell = (TableCell)rows[r][c];
                var text = cell.FirstOrDefault() is ParagraphBlock p ? Inlines(p.Inline, size - 1) : new SelectableTextBlock();
                if (rows[r].IsHeader)
                    text.FontWeight = FontWeight.SemiBold;
                var border = new Border { Child = text, Padding = new Thickness(6, 3), BorderBrush = Ui.Edge, BorderThickness = new Thickness(0, 0, 1, 1), Background = rows[r].IsHeader ? Ui.Header : null };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return new ScrollViewer { Content = new Border { Child = grid, BorderBrush = Ui.Edge, BorderThickness = new Thickness(1, 1, 0, 0) }, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private static SelectableTextBlock Inlines(ContainerInline? container, double size)
    {
        var text = new SelectableTextBlock { FontSize = size, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap, Inlines = new InlineCollection() };
        if (container is not null)
            foreach (var inline in container)
                Add(text.Inlines!, inline, FontWeight.Normal, FontStyle.Normal, size);
        return text;
    }

    private static void Add(InlineCollection target, Markdig.Syntax.Inlines.Inline inline, FontWeight weight, FontStyle style, double size)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()) { FontWeight = weight, FontStyle = style });
                break;
            case CodeInline code:
                target.Add(new Run(code.Content) { FontFamily = Ui.Mono, Foreground = Ui.Warm, FontSize = size - 1 });
                break;
            case LineBreakInline:
                target.Add(new LineBreak());
                break;
            case EmphasisInline emphasis:
            {
                var bold = emphasis.DelimiterCount >= 2 ? FontWeight.Bold : weight;
                var italic = emphasis.DelimiterCount == 1 || emphasis.DelimiterCount == 3 ? FontStyle.Italic : style;
                foreach (var child in emphasis)
                    Add(target, child, bold, italic, size);
                break;
            }
            case LinkInline link:
            {
                var label = string.Concat(link.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
                target.Add(new Run(link.IsImage ? $"[image: {label}]" : label.Length > 0 ? label : link.Url ?? string.Empty)
                    { Foreground = Ui.Accent, TextDecorations = TextDecorations.Underline });
                break;
            }
            case AutolinkInline auto:
                target.Add(new Run(auto.Url) { Foreground = Ui.Accent, TextDecorations = TextDecorations.Underline });
                break;
            case HtmlInline html:
                target.Add(new Run(html.Tag) { Foreground = Ui.Muted });
                break;
            case ContainerInline container:
                foreach (var child in container)
                    Add(target, child, weight, style, size);
                break;
            default:
                target.Add(new Run(inline.ToString()));
                break;
        }
    }
}
