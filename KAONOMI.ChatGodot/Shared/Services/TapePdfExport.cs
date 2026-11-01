using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Views;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Writes the tape of <c>/work</c> as a PDF document (A4), as it is shown in its editor, like the Word export: a title
/// and the date, then each paragraph with its alignment, its style (title, subtitle, headings with the space above them)
/// and the formatting of its text (font, size, bold, italic, underline, strikethrough, color; a monospace 10 pt for the
/// text without a font of its own), the links (clickable), the pictures at their width in the tape (at most the width of
/// the page) and the blocks of code in the colors of their language on a light gray sheet; the pages are numbered.
/// QuestPDF draws it (Community license: free for a non-profit project).
/// </summary>
public static class TapePdfExport
{
    /// <summary>Widest picture on the page, in points (the text width of an A4 page with 2.5 cm margins).</summary>
    private const float MaxWidth = 453;

    /// <summary>Points per pixel of the editor (96 DPI).</summary>
    private const float PointsPerPixel = 0.75f;

    /// <summary>The monospace fonts of the text without a font of its own, the first installed one being used.</summary>
    private static readonly string[] MonoFonts = [.. Installed(["Consolas", "Menlo", "Cascadia Mono", "Courier New", "DejaVu Sans Mono"]), .. Emoji];

    /// <summary>The font of the text when none is chosen: Arial (the blocks of code keep <see cref="MonoFonts"/>).</summary>
    private static readonly string[] TextFonts = [.. Installed(["Arial", "Helvetica Neue", "Liberation Sans", "DejaVu Sans"]), .. Emoji];

    /// <summary>The fonts of the title.</summary>
    private static readonly string[] TitleFonts = [.. Installed(["Helvetica Neue", "Arial", "Segoe UI", "DejaVu Sans"]), .. Emoji];

    /// <summary>The emoji of the text (📈 of the graphs…), after the font of the text.</summary>
    private static string[] Emoji => InstalledOnly(["Apple Color Emoji", "Segoe UI Emoji", "Noto Color Emoji"]);

    private static string[] InstalledOnly(string[] names)
    {
        var families = SkiaSharp.SKFontManager.Default.FontFamilies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Where(families.Contains).ToArray();
    }

    /// <summary>
    /// The families of <paramref name="names"/> installed on this machine (QuestPDF refuses a family it cannot find),
    /// in their order; the first one when none is.
    /// </summary>
    private static string[] Installed(string[] names)
    {
        var families = SkiaSharp.SKFontManager.Default.FontFamilies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = names.Where(families.Contains).ToArray();
        return installed.Length > 0 ? installed : [names[0]];
    }

    static TapePdfExport()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // A character no font has (a rare symbol) is drawn as an empty box rather than stopping the export.
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
        // The fonts of the system: QuestPDF does not look everywhere by itself (on macOS most of them are in
        // /System/Library/Fonts/Supplemental).
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var folder in new[]
                 {
                     "/System/Library/Fonts", "/System/Library/Fonts/Supplemental", "/Library/Fonts", Path.Combine(home, "Library", "Fonts"),
                     Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(home, ".fonts")
                 })
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && !QuestPDF.Settings.FontDiscoveryPaths.Contains(folder))
                QuestPDF.Settings.FontDiscoveryPaths.Add(folder);
    }

    /// <summary>
    /// Writes <paramref name="tape"/> to <paramref name="stream"/> as a PDF; <paramref name="loadPicture"/> gives the PNG
    /// of a local picture (null when it cannot be read: its path is written instead). Returns the number of pictures.
    /// </summary>
    public static async Task<int> WriteAsync(Stream stream, string tape, Func<string, Task<TapeWordExport.Picture?>> loadPicture,
        string title = "KAONOMI · Work", CancellationToken cancellationToken = default)
    {
        var document = TapeDocument.Parse(tape);
        var pictures = new Dictionary<string, TapeWordExport.Picture?>();
        foreach (var image in document.Blocks.OfType<TapeImage>())
        {
            var path = JournalMarkdown.LocalPath(image.Url);
            if (!pictures.ContainsKey(path))
                pictures[path] = await loadPicture(path);
        }
        var embedded = 0;
        var bytes = await Task.Run(() => Build(document, pictures, title, out embedded), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        return embedded;
    }

    private static byte[] Build(TapeDocument tape, IReadOnlyDictionary<string, TapeWordExport.Picture?> pictures, string title, out int embedded)
    {
        var count = 0;
        var pdf = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2.5f, Unit.Centimetre);
            page.DefaultTextStyle(style => style.FontFamily(TextFonts).FontSize(10).FontColor("#24292F"));
            page.Header().PaddingBottom(12).Column(header =>
            {
                header.Item().Text(title).FontFamily(TitleFonts).FontSize(18).Bold().FontColor("#111827");
                header.Item().Text($"Exported {DateTime.Now:yyyy-MM-dd HH:mm}").FontFamily(TitleFonts).FontSize(9).FontColor("#808080");
            });
            page.Footer().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8).FontColor("#9CA3AF"));
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
            page.Content().Column(column =>
            {
                column.Spacing(2);
                foreach (var block in tape.Blocks)
                {
                    switch (block)
                    {
                        case TapeImage image:
                            var path = JournalMarkdown.LocalPath(image.Url);
                            if (pictures.GetValueOrDefault(path) is { } picture)
                            {
                                var width = Math.Min(image.Width * PointsPerPixel, MaxWidth);
                                var item = column.Item().PaddingVertical(4);
                                item = image.Alignment switch
                                {
                                    TapeAlignment.Center => item.AlignCenter(),
                                    TapeAlignment.Right => item.AlignRight(),
                                    _ => item.AlignLeft()
                                };
                                item.Width(width).Image(picture.Png).FitWidth();
                                count++;
                            }
                            else
                                column.Item().Text($"[picture not found: {path}]").Italic().FontSize(9).FontColor("#C00000");
                            break;
                        case TapeCode code:
                            column.Item().PaddingVertical(4).Background("#F6F8FA").Border(0.5f).BorderColor("#E1E4E8").Padding(6)
                                .Text(text => CodeText(text, code));
                            break;
                        case TapeParagraph paragraph:
                            Paragraph(column.Item(), paragraph);
                            break;
                    }
                }
            });
        }));
        // The pages are drawn (and the pictures counted) by GeneratePdf.
        var bytes = pdf.GeneratePdf();
        embedded = count;
        return bytes;
    }

    /// <summary>A paragraph: the space above a title or heading, its alignment, each run in its style.</summary>
    private static void Paragraph(IContainer item, TapeParagraph paragraph)
    {
        var before = (float)TapeParagraph.SpaceBefore(paragraph.Kind) * PointsPerPixel;
        if (before > 0)
            item = item.PaddingTop(before);
        item.Text(text =>
        {
            switch (paragraph.Alignment)
            {
                case TapeAlignment.Center: text.AlignCenter(); break;
                case TapeAlignment.Right: text.AlignRight(); break;
                default: text.AlignLeft(); break;
            }
            var runs = paragraph.Runs.Where(r => r.Text.Length > 0).ToList();
            if (runs.Count == 0)
            {
                // An empty line keeps its height.
                text.Span(" ");
                return;
            }
            foreach (var run in runs)
            {
                var style = paragraph.Effective(run.Style);
                var span = style.Link is { } link && LinkAddress(link) is { } address
                    ? text.Hyperlink(run.Text, address)
                    : text.Span(run.Text);
                Apply(span, style);
            }
        });
    }

    /// <summary>The formatting of <paramref name="style"/> on <paramref name="span"/> (sizes from pixels of the editor to points).</summary>
    private static void Apply(TextSpanDescriptor span, TapeStyle style)
    {
        span.FontFamily(Fonts(style.Font));
        span.FontSize(style.Size is { } size ? (float)size * PointsPerPixel : 10);
        if (style.Bold)
            span.Bold();
        if (style.Italic)
            span.Italic();
        if (style.Strike)
            span.Strikethrough();
        // A link is blue (unless it has a color of its own) and underlined, as in the editor.
        if (style.Underline || style.Link is not null)
            span.Underline();
        if (style.Color is { Length: 7 } color)
            span.FontColor(color);
        else if (style.Link is not null)
            span.FontColor("#2563EB");
    }

    /// <summary>A block of code, colored by the grammar of its language (as in the editor and the Word export).</summary>
    private static void CodeText(TextDescriptor text, TapeCode code)
    {
        text.DefaultTextStyle(style => style.FontFamily(MonoFonts).FontSize(9).FontColor("#24292F"));
        var spans = CodeHighlighter.Highlight(code.Language, code.Code).OrderBy(s => s.Start).ToList();
        var source = code.Code.Replace("\r", string.Empty);
        var position = 0;
        foreach (var span in spans)
        {
            if (span.Start < position || span.Start >= source.Length)
                continue;
            if (span.Start > position)
                text.Span(source[position..span.Start]);
            var length = Math.Min(span.Length, source.Length - span.Start);
            var piece = text.Span(source.Substring(span.Start, length));
            if (span.Color is { Length: 7 } color)
                piece.FontColor(color);
            if (span.Bold)
                piece.Bold();
            if (span.Italic)
                piece.Italic();
            position = span.Start + length;
        }
        if (position < source.Length)
            text.Span(source[position..]);
    }

    /// <summary>The fonts of a style: its own font first (then a monospace one), the monospace fonts for the default.</summary>
    private static string[] Fonts(string? font)
    {
        var first = font?.Split(',')[0].Trim().Trim('\'', '"');
        if (string.IsNullOrEmpty(first))
            return TextFonts;
        if (first.Equals("monospace", StringComparison.OrdinalIgnoreCase))
            return MonoFonts;
        return SkiaSharp.SKFontManager.Default.FontFamilies.Contains(first, StringComparer.OrdinalIgnoreCase) ? [first, .. TextFonts] : TextFonts;
    }

    /// <summary>The address of a link in the PDF: a web address, or a local file as file:///…; null when it cannot be one.</summary>
    private static string? LinkAddress(string link)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && !uri.IsFile)
            return uri.AbsoluteUri;
        var path = link.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), link.TrimStart('~', '/', '\\'))
            : uri?.IsFile == true ? uri.LocalPath : link;
        return Path.IsPathRooted(path) ? new UriBuilder { Scheme = Uri.UriSchemeFile, Host = string.Empty, Path = Path.GetFullPath(path) }.Uri.AbsoluteUri : null;
    }
}
