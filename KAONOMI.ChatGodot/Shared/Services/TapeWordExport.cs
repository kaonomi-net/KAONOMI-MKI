using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Views;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Writes the calculator tape (<c>/work</c>) as a Word (.docx) document, as it is shown in its editor: a title,
/// then each paragraph with its alignment and the formatting of its text (font, size, bold, italic, underline,
/// strikethrough, color; Consolas 10 pt for the text without a font of its own), and the pictures (graphs, dropped
/// pictures) embedded at their width in the tape.
/// </summary>
public static class TapeWordExport
{
    /// <summary>A picture ready for Word: PNG bytes and its size in pixels.</summary>
    public sealed record Picture(byte[] Png, int Width, int Height);

    /// <summary>Widest picture on the page (pixels at 96 DPI: 16 cm, the text width of an A4 page with 2.5 cm margins).</summary>
    private const int MaxWidth = 605;

    /// <summary>English Metric Units per pixel at 96 DPI.</summary>
    private const long EmuPerPixel = 9525;

    /// <summary>
    /// Writes <paramref name="tape"/> to <paramref name="stream"/>; <paramref name="loadPicture"/> gives the PNG of a
    /// local picture (null when it cannot be read: its path is written instead). Returns the number of pictures embedded.
    /// </summary>
    public static async Task<int> WriteAsync(Stream stream, string tape, Func<string, Task<Picture?>> loadPicture,
        CancellationToken cancellationToken = default, string title = "KAONOMI · Calculator tape")
    {
        var document = TapeDocument.Parse(tape);
        var pictures = new Dictionary<string, Picture?>();
        foreach (var image in document.Blocks.OfType<TapeImage>())
        {
            var path = JournalMarkdown.LocalPath(image.Url);
            if (!pictures.ContainsKey(path))
                pictures[path] = await loadPicture(path);
        }

        // OpenXml needs a readable, seekable stream; the streams of the save dialogs often are write-only.
        using var buffer = new MemoryStream();
        var count = Build(buffer, document, pictures, title);
        buffer.Position = 0;
        await buffer.CopyToAsync(stream, cancellationToken);
        return count;
    }

    private static int Build(Stream stream, TapeDocument tape, IReadOnlyDictionary<string, Picture?> pictures, string title)
    {
        using var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var body = new Body();
        mainPart.Document = new Document(body);

        body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun(title, bold: true, size: 36)));
        body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun($"Exported {DateTime.Now:yyyy-MM-dd HH:mm}", color: "808080", size: 18)));

        var embedded = 0;
        uint pictureId = 1;
        foreach (var block in tape.Blocks)
        {
            if (block is TapeImage image)
            {
                var path = JournalMarkdown.LocalPath(image.Url);
                if (pictures.GetValueOrDefault(path) is { } picture)
                {
                    var part = mainPart.AddImagePart(ImagePartType.Png);
                    using (var png = new MemoryStream(picture.Png))
                        part.FeedData(png);
                    var width = Math.Min(image.Width, MaxWidth);
                    var height = (int)Math.Round(width * (double)picture.Height / Math.Max(1, picture.Width));
                    var pictureParagraph = new Paragraph(new Run(Drawing(mainPart.GetIdOfPart(part), pictureId++, image.Alt, width, height)));
                    if (image.Alignment != TapeAlignment.Left)
                        pictureParagraph.PrependChild(new ParagraphProperties(new Justification
                        {
                            Val = image.Alignment == TapeAlignment.Center ? JustificationValues.Center : JustificationValues.Right
                        }));
                    body.Append(pictureParagraph);
                    embedded++;
                }
                else
                {
                    body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun($"[picture not found: {path}]", italic: true, color: "C00000", size: 18)));
                }
            }
            else if (block is TapeCode code)
                foreach (var line in CodeParagraphs(code))
                    body.Append(line);
            else if (block is TapeParagraph paragraph)
            {
                // Space above a title or heading (pixels to twentieths of a point), its alignment, and the outline level of
                // a heading (the navigation pane of Word lists them).
                var before = (int)Math.Round(TapeParagraph.SpaceBefore(paragraph.Kind) * 15);
                var properties = new ParagraphProperties(new SpacingBetweenLines
                {
                    Before = before.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    After = "0"
                });
                if (paragraph.Alignment != TapeAlignment.Left)
                    properties.Append(new Justification { Val = paragraph.Alignment == TapeAlignment.Center ? JustificationValues.Center : JustificationValues.Right });
                if (paragraph.Kind is TapeParagraphKind.Heading1 or TapeParagraphKind.Heading2 or TapeParagraphKind.Heading3)
                    properties.Append(new OutlineLevel { Val = paragraph.Kind - TapeParagraphKind.Heading1 });
                var word = new Paragraph(properties);
                var runs = paragraph.Runs.Where(r => r.Text.Length > 0).ToList();
                for (var i = 0; i < runs.Count;)
                {
                    // Neighbor runs of one link make one hyperlink of Word.
                    if (runs[i].Style.Link is { } link && LinkUri(link) is { } uri)
                    {
                        var hyperlink = new Hyperlink { Id = mainPart.AddHyperlinkRelationship(uri, true).Id, History = true };
                        while (i < runs.Count && runs[i].Style.Link == link)
                            hyperlink.Append(WordRun(runs[i].Text, paragraph.Effective(runs[i++].Style)));
                        word.Append(hyperlink);
                    }
                    else
                        word.Append(WordRun(runs[i].Text, paragraph.Effective(runs[i++].Style)));
                }
                body.Append(word);
            }
        }

        mainPart.Document.Save();
        return embedded;
    }

    /// <summary>
    /// A Word run of <paramref name="text"/> with the formatting <paramref name="style"/> (its properties in the order of the Word schema: font,
    /// bold, italic, strikethrough, color, size, underline).
    /// </summary>
    private static Run WordRun(string text, TapeStyle style)
    {
        var properties = new RunProperties();
        var font = WordFont(style.Font);
        properties.Append(new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font });
        if (style.Bold)
            properties.Append(new Bold());
        if (style.Italic)
            properties.Append(new Italic());
        if (style.Strike)
            properties.Append(new Strike());
        // A link is blue (unless it has a color of its own) and underlined, as in the editor.
        if (style.Color is { Length: 7 } color)
            properties.Append(new Color { Val = color[1..] });
        else if (style.Link is not null)
            properties.Append(new Color { Val = "2563EB" });
        // Pixels of the editor (96 DPI) to half-points: 13 px = 10 pt.
        var halfPoints = style.Size is { } size ? (int)Math.Round(size * 1.5) : 20;
        properties.Append(new FontSize { Val = halfPoints.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (style.Underline || style.Link is not null)
            properties.Append(new Underline { Val = UnderlineValues.Single });
        return new Run(properties, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    /// <summary>
    /// A block of code as Word paragraphs, one per line: Consolas 9 pt on a light gray background, colored as in the
    /// editor (the TextMate grammar of its language).
    /// </summary>
    private static IEnumerable<Paragraph> CodeParagraphs(TapeCode code)
    {
        var spans = CodeHighlighter.Highlight(code.Language, code.Code);
        var lines = code.Code.Split('\n');
        var offset = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var properties = new ParagraphProperties(
                new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F6F8FA" },
                new SpacingBetweenLines { Before = index == 0 ? "120" : "0", After = index == lines.Length - 1 ? "120" : "0" });
            var paragraph = new Paragraph(properties);
            var position = 0;
            foreach (var span in spans.Where(s => s.Start >= offset && s.Start < offset + line.Length).OrderBy(s => s.Start))
            {
                var start = span.Start - offset;
                var length = Math.Min(span.Length, line.Length - start);
                if (start > position)
                    paragraph.Append(WordRun(line[position..start], CodeStyle(null)));
                paragraph.Append(WordRun(line.Substring(start, length), CodeStyle(span.Color) with { Bold = span.Bold, Italic = span.Italic }));
                position = start + length;
            }
            if (position < line.Length || line.Length == 0)
                paragraph.Append(WordRun(line[position..], CodeStyle(null)));
            yield return paragraph;
            offset += lines[index].Length + 1;
        }
    }

    /// <summary>Style of the code in Word: Consolas, 12 px (9 pt), the color of the grammar (dark gray otherwise).</summary>
    private static TapeStyle CodeStyle(string? color) => new(Font: "Consolas", Size: 12, Color: color ?? "#24292F");

    /// <summary>The address of a link for Word: a web address, or a file (file://); null when it cannot be one.</summary>
    private static Uri? LinkUri(string link)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && !uri.IsFile)
            return uri;
        var path = link.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), link.TrimStart('~', '/', '\\'))
            : uri?.IsFile == true ? uri.LocalPath : link;
        // A file as file:///… (a bare path would be read by Word as relative to the document).
        return Path.IsPathRooted(path) ? new UriBuilder { Scheme = Uri.UriSchemeFile, Host = string.Empty, Path = Path.GetFullPath(path) }.Uri : null;
    }

    /// <summary>The font Word uses for <paramref name="font"/>: the first of a list, Consolas for the default monospace one.</summary>
    private static string WordFont(string? font)
    {
        var first = font?.Split(',')[0].Trim().Trim('\'', '"');
        if (string.IsNullOrEmpty(first))
            return "Arial"; // the text of the tape without a font chosen
        return first.Equals("monospace", StringComparison.OrdinalIgnoreCase) || first.Equals("Cascadia Mono", StringComparison.OrdinalIgnoreCase)
            ? "Consolas"
            : first;
    }

    /// <summary>An inline picture of <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    private static Drawing Drawing(string relationshipId, uint id, string name, int width, int height)
    {
        long cx = width * EmuPerPixel, cy = height * EmuPerPixel;
        return new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = id, Name = string.IsNullOrEmpty(name) ? $"Picture {id}" : name },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = id, Name = $"picture{id}.png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U
            });
    }
}
