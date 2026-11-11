using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using KAONOMI.Chat.Views;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Writes the calculator tape (<c>/calculator</c>) as a Word (.docx) document: a title, the session lines
/// (<c>── date ──</c>) as small headings, the calculations in a monospace font, <c>#</c> / <c>##</c> headings, and the
/// pictures of the image lines (graphs, dropped pictures) embedded at their width in the tape.
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
        CancellationToken cancellationToken = default)
    {
        var lines = tape.Replace("\r\n", "\n").Split('\n');
        var pictures = new Dictionary<string, Picture?>();
        foreach (var line in lines)
            if (JournalMarkdown.ImageLine.Match(line) is { Success: true } image)
            {
                var path = JournalMarkdown.LocalPath(image.Groups["url"].Value);
                if (!pictures.ContainsKey(path))
                    pictures[path] = await loadPicture(path);
            }

        // OpenXml needs a readable, seekable stream; the streams of the save dialogs often are write-only.
        using var buffer = new MemoryStream();
        var count = Build(buffer, lines, pictures);
        buffer.Position = 0;
        await buffer.CopyToAsync(stream, cancellationToken);
        return count;
    }

    private static int Build(Stream stream, string[] lines, IReadOnlyDictionary<string, Picture?> pictures)
    {
        using var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var body = new Body();
        mainPart.Document = new Document(body);

        body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun("KAONOMI · Calculator tape", bold: true, size: 36)));
        body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun($"Exported {DateTime.Now:yyyy-MM-dd HH:mm}", color: "808080", size: 18)));

        var embedded = 0;
        uint pictureId = 1;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (JournalMarkdown.ImageLine.Match(line) is { Success: true } image)
            {
                var path = JournalMarkdown.LocalPath(image.Groups["url"].Value);
                var width = int.TryParse(image.Groups["width"].Value, out var w) && w > 0 ? w : JournalMarkdown.DefaultImageWidth;
                if (pictures.GetValueOrDefault(path) is { } picture)
                {
                    var part = mainPart.AddImagePart(ImagePartType.Png);
                    using (var png = new MemoryStream(picture.Png))
                        part.FeedData(png);
                    width = Math.Min(width, MaxWidth);
                    var height = (int)Math.Round(width * (double)picture.Height / Math.Max(1, picture.Width));
                    body.Append(new Paragraph(new Run(Drawing(mainPart.GetIdOfPart(part), pictureId++, image.Groups["alt"].Value, width, height))));
                    embedded++;
                }
                else
                {
                    body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun($"[picture not found: {path}]", italic: true, color: "C00000", size: 18)));
                }
            }
            else if (trimmed.StartsWith("──", StringComparison.Ordinal))
                body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun(trimmed.Trim('─', ' '), bold: true, color: "B26A00", size: 22)));
            else if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun(trimmed[3..], bold: true, size: 28)));
            else if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun(trimmed[2..], bold: true, size: 32)));
            else
                body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun(line, bold: trimmed.StartsWith("📈", StringComparison.Ordinal), font: "Consolas", size: 20), null, "0"));
        }

        mainPart.Document.Save();
        return embedded;
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
