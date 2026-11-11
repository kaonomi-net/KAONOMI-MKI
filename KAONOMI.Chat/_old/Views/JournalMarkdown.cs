using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace KAONOMI.Chat.Views;

/// <summary>What the preview of the journal needs from its window: images, links and the resizing of an image.</summary>
public sealed class JournalRenderOptions
{
    /// <summary>Decodes a local picture for the preview (null when it cannot be read).</summary>
    public required Func<string, Task<Bitmap?>> LoadImage { get; init; }

    /// <summary>Opens a link (web address or local file).</summary>
    public required Action<string> OpenLink { get; init; }

    /// <summary>An image was resized in the preview: its index among the image lines, and its new width in pixels.</summary>
    public Action<int, int>? ImageResized { get; init; }
}

/// <summary>
/// Markdown of the journal (<c>/diary</c>), rendered for its preview:
/// <list type="bullet">
/// <item>blocks: <c># H1</c>, <c>## H2</c> (and <c>###</c>), paragraphs (a line break is kept), <c>- </c> bullets,
/// <c>&gt; </c> quotes, <c>```</c> code blocks, and image lines <c>![alt|width](path)</c> (local path or file:// link; a
/// path with spaces between &lt; &gt;);</item>
/// <item>inline: <c>**bold**</c>, <c>*italic*</c> or <c>_italic_</c>, <c>***both***</c>, <c>&lt;u&gt;underline&lt;/u&gt;</c>,
/// <c>`code`</c> and <c>[text](link)</c>.</item>
/// </list>
/// The images can be resized with the grip of their bottom right corner: the width is written back in the Markdown.
/// </summary>
public static class JournalMarkdown
{
    /// <summary>A line holding only an image: <c>![alt|width](path)</c>, the width being optional.</summary>
    public static readonly Regex ImageLine = new(
        @"^[ \t]*!\[(?<alt>[^\]|]*)(\|(?<width>\d+))?\]\((?<url><[^>\n]+>|[^)\s]+)\)[ \t]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Inline formats, the first one found wins (their content is parsed again, except code).</summary>
    private static readonly Regex InlinePattern = new(
        @"`(?<code>[^`\n]+)`" +
        @"|\[(?<ltext>[^\]\n]+)\]\((?<lurl><[^>\n]+>|[^)\s]+)\)" +
        @"|\*\*\*(?<bolditalic>.+?)\*\*\*" +
        @"|\*\*(?<bold>.+?)\*\*" +
        @"|<u>(?<under>.+?)</u>" +
        @"|\*(?<italic>[^*\n]+)\*" +
        @"|(?<![\w])_(?<italic2>[^_\n]+)_(?![\w])",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Width of an image without a width in its Markdown.</summary>
    public const int DefaultImageWidth = 480;

    /// <summary>Monospaced fonts of the code.</summary>
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Menlo, Consolas, monospace");

    /// <summary>The local file of a link of an image: &lt; &gt; removed, file:// decoded.</summary>
    public static string LocalPath(string url)
    {
        var path = url.Trim().TrimStart('<').TrimEnd('>');
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var uri))
            return uri.LocalPath;
        return path;
    }

    /// <summary>Markdown of an image line for <paramref name="path"/> (between &lt; &gt; when it holds spaces).</summary>
    public static string ImageMarkdown(string path, int width)
    {
        var alt = System.IO.Path.GetFileNameWithoutExtension(path).Replace("]", string.Empty).Replace("|", string.Empty);
        var link = path.Contains(' ') || path.Contains(')') ? $"<{path}>" : path;
        return $"![{alt}|{width}]({link})";
    }

    /// <summary>
    /// <paramref name="markdown"/> with the width of its image line number <paramref name="index"/> (0 = the first one)
    /// set to <paramref name="width"/>.
    /// </summary>
    public static string SetImageWidth(string markdown, int index, int width)
    {
        var count = 0;
        return ImageLine.Replace(markdown, match =>
        {
            if (count++ != index)
                return match.Value;
            var indent = match.Value[..(match.Value.Length - match.Value.TrimStart().Length)];
            return $"{indent}![{match.Groups["alt"].Value}|{width}]({match.Groups["url"].Value})";
        });
    }

    /// <summary>The rendered Markdown: a column of blocks.</summary>
    public static Control Render(string markdown, JournalRenderOptions options)
    {
        var panel = new StackPanel { Spacing = 8 };
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var imageIndex = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            panel.Children.Add(TextBlock(paragraph, options, 14, FontWeight.Normal));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
                    code.Append(code.Length > 0 ? "\n" : string.Empty).Append(lines[i]);
                panel.Children.Add(CodeBlock(code.ToString()));
                continue;
            }

            if (ImageLine.Match(line) is { Success: true } image)
            {
                FlushParagraph();
                var width = int.TryParse(image.Groups["width"].Value, out var w) && w > 0 ? w : DefaultImageWidth;
                panel.Children.Add(ImageBlock(LocalPath(image.Groups["url"].Value), image.Groups["alt"].Value, width, imageIndex++, options));
                continue;
            }

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushParagraph();
                panel.Children.Add(Heading(trimmed[2..], 26, options));
            }
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal) || trimmed.StartsWith("### ", StringComparison.Ordinal))
            {
                FlushParagraph();
                panel.Children.Add(Heading(trimmed.TrimStart('#').TrimStart(), 20, options));
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                FlushParagraph();
                var bullet = TextBlock([trimmed[2..]], options, 14, FontWeight.Normal);
                panel.Children.Add(new DockPanel
                {
                    Margin = new Thickness(8, 0, 0, 0),
                    Children = { new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0), [DockPanel.DockProperty] = Dock.Left }, bullet }
                });
            }
            else if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                panel.Children.Add(new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = Brushes.Gray,
                    Padding = new Thickness(10, 2, 0, 2),
                    Opacity = 0.85,
                    Child = TextBlock([trimmed[2..]], options, 14, FontWeight.Normal)
                });
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
            }
            else
            {
                paragraph.Add(line);
            }
        }
        FlushParagraph();
        return panel;
    }

    private static Control Heading(string text, double size, JournalRenderOptions options)
    {
        var block = TextBlock([text], options, size, size > 22 ? FontWeight.Bold : FontWeight.SemiBold);
        block.Margin = new Thickness(0, size > 22 ? 6 : 4, 0, 0);
        return block;
    }

    /// <summary>A paragraph: its lines (kept apart by line breaks) with their inline formats.</summary>
    private static SelectableTextBlock TextBlock(IReadOnlyList<string> lines, JournalRenderOptions options, double size, FontWeight weight)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight };
        var inlines = new InlineCollection();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                inlines.Add(new LineBreak());
            AddInlines(inlines, lines[i], bold: weight >= FontWeight.SemiBold, italic: false, underline: false, options);
        }
        block.Inlines = inlines;
        return block;
    }

    /// <summary>Adds the text with its formats, recursively (bold inside italic…).</summary>
    private static void AddInlines(InlineCollection target, string text, bool bold, bool italic, bool underline, JournalRenderOptions options)
    {
        var position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > position)
                target.Add(StyledRun(text[position..match.Index], bold, italic, underline));
            position = match.Index + match.Length;

            if (match.Groups["code"].Success)
            {
                target.Add(new Run(match.Groups["code"].Value)
                {
                    FontFamily = CodeFont,
                    Background = new SolidColorBrush(Color.Parse("#22888888")),
                    FontWeight = bold ? FontWeight.Bold : FontWeight.Normal
                });
            }
            else if (match.Groups["ltext"].Success)
            {
                var url = match.Groups["lurl"].Value.Trim('<', '>');
                var link = new HyperlinkButton
                {
                    Content = match.Groups["ltext"].Value,
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                ToolTip.SetTip(link, url);
                link.Click += (_, _) => options.OpenLink(url);
                target.Add(new InlineUIContainer(link) { BaselineAlignment = BaselineAlignment.TextBottom });
            }
            else if (match.Groups["bolditalic"].Success)
                AddInlines(target, match.Groups["bolditalic"].Value, true, true, underline, options);
            else if (match.Groups["bold"].Success)
                AddInlines(target, match.Groups["bold"].Value, true, italic, underline, options);
            else if (match.Groups["under"].Success)
                AddInlines(target, match.Groups["under"].Value, bold, italic, true, options);
            else if (match.Groups["italic"].Success)
                AddInlines(target, match.Groups["italic"].Value, bold, true, underline, options);
            else if (match.Groups["italic2"].Success)
                AddInlines(target, match.Groups["italic2"].Value, bold, true, underline, options);
        }
        if (position < text.Length)
            target.Add(StyledRun(text[position..], bold, italic, underline));
    }

    private static Run StyledRun(string text, bool bold, bool italic, bool underline) => new(text)
    {
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        TextDecorations = underline ? TextDecorations.Underline : null
    };

    private static Control CodeBlock(string code) => new Border
    {
        Background = new SolidColorBrush(Color.Parse("#1E1F24")),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 10),
        Child = new SelectableTextBlock
        {
            Text = code,
            FontFamily = CodeFont,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#E6E8EC")),
            TextWrapping = TextWrapping.Wrap
        }
    };

    /// <summary>
    /// A local picture at <paramref name="width"/> pixels, with its name below and a grip in its bottom right corner to
    /// resize it (the new width is reported when the grip is released).
    /// </summary>
    private static Control ImageBlock(string path, string alt, int width, int index, JournalRenderOptions options)
    {
        var image = new Image { Width = width, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        var missing = new TextBlock
        {
            Text = $"Image not found: {path}",
            Foreground = Brushes.IndianRed,
            FontSize = 12,
            IsVisible = false,
            TextWrapping = TextWrapping.Wrap
        };
        var sizeLabel = new TextBlock { Text = $"{width} px", FontSize = 10, Opacity = 0.6, Margin = new Thickness(6, 0, 0, 0) };
        var grip = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.Parse("#CC2EB7D7")),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -7, -7),
            Cursor = new Cursor(StandardCursorType.BottomRightCorner)
        };
        ToolTip.SetTip(grip, "Drag to resize the image");

        var frame = new Panel { HorizontalAlignment = HorizontalAlignment.Left, Children = { image, grip } };

        // Resize: the grip follows the pointer, the width is written back in the Markdown when it is released.
        Point? start = null;
        var startWidth = 0.0;
        grip.PointerPressed += (_, e) =>
        {
            start = e.GetPosition(null);
            startWidth = image.Width;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (start is not { } origin)
                return;
            image.Width = Math.Clamp(Math.Round(startWidth + e.GetPosition(null).X - origin.X), 80, 2400);
            sizeLabel.Text = $"{image.Width:0} px";
        };
        grip.PointerReleased += (_, e) =>
        {
            if (start is null)
                return;
            start = null;
            e.Pointer.Capture(null);
            options.ImageResized?.Invoke(index, (int)image.Width);
        };

        _ = LoadAsync();
        async Task LoadAsync()
        {
            var bitmap = await options.LoadImage(path);
            if (bitmap is null)
            {
                missing.IsVisible = true;
                frame.IsVisible = false;
            }
            else
            {
                image.Source = bitmap;
            }
        }

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                frame,
                missing,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { new TextBlock { Text = alt, FontSize = 11, Opacity = 0.6 }, sizeLabel }
                }
            }
        };
    }
}
