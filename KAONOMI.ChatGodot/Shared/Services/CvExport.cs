using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using KAONOMI.Chat.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace KAONOMI.Chat.Services;

/// <summary>
/// A CV of <c>/cv</c> as a file, in a modern look. The PDF (QuestPDF) has a dark side column on every page (the
/// person, the contact, the skills, the educations) and the main column beside it (the summary, the experiences, the
/// creations). The Word document (Open XML) says the same in one column under a dark band with the person, so that it
/// stays easy to edit: headings in the accent colour, the dates at the right of each line.
/// </summary>
public static class CvExport
{
    private const string Dark = "#0F172A";
    private const string DarkSoft = "#1E293B";
    private const string OnDark = "#E2E8F0";
    private const string OnDarkMuted = "#94A3B8";
    private const string Accent = "#0284C7";
    private const string AccentOnDark = "#38BDF8";
    private const string Ink = "#0F172A";
    private const string Body = "#334155";
    private const string Muted = "#64748B";
    private const string Line = "#E2E8F0";

    /// <summary>Where the CVs are exported by default: Documents/kaonomi/cv.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "kaonomi", "cv");

    /// <summary>A name of file for the CV (without extension).</summary>
    public static string FileName(CvEntry cv)
    {
        var name = string.Join("-", new[] { "cv", cv.LastName, cv.FirstName, cv.Title }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var clean = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (clean.Contains("--", StringComparison.Ordinal))
            clean = clean.Replace("--", "-", StringComparison.Ordinal);
        clean = clean.Trim('-');
        return clean.Length > 80 ? clean[..80].Trim('-') : clean.Length > 0 ? clean : "cv";
    }

    // ── The photo ──

    /// <summary>
    /// The photo <paramref name="path"/> as a round portrait: upright (the orientation a phone wrote in it), cut to a
    /// square around its upper middle (where a face is), <paramref name="size"/> pixels wide, in a disc on a
    /// transparent ground (PNG). Null when there is no file or when it is not a picture that can be read.
    /// </summary>
    public static byte[]? Portrait(string? path, int size = 480)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is null)
                return null;
            using var decoded = SKBitmap.Decode(codec);
            if (decoded is null || decoded.Width == 0 || decoded.Height == 0)
                return null;
            var degrees = codec.EncodedOrigin switch
            {
                SKEncodedOrigin.RightTop => 90,
                SKEncodedOrigin.BottomRight => 180,
                SKEncodedOrigin.LeftBottom => 270,
                _ => 0
            };
            var turned = degrees is 90 or 270;
            int width = turned ? decoded.Height : decoded.Width, height = turned ? decoded.Width : decoded.Height;
            var side = Math.Min(width, height);
            // The square: in the middle of a wide picture, in the upper part of a tall one.
            float left = (width - side) / 2f, top = (height - side) * 0.3f;
            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            using var disc = new SKPath();
            disc.AddCircle(size / 2f, size / 2f, size / 2f);
            canvas.ClipPath(disc, SKClipOperation.Intersect, antialias: true);
            var scale = size / (float)side;
            canvas.Scale(scale);
            canvas.Translate(-left, -top);
            // The picture upright in a width × height frame.
            canvas.Translate(width / 2f, height / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-decoded.Width / 2f, -decoded.Height / 2f);
            using var image = SKImage.FromBitmap(decoded);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch (Exception)
        {
            // Not a picture, or one this machine cannot decode.
            return null;
        }
    }

    // ── What both files say ──

    private static string Person(CvEntry cv) => (cv.FirstName + " " + cv.LastName).Trim() is { Length: > 0 } name ? name : cv.Title.Length > 0 ? cv.Title : "CV";

    private static string Headline(CvEntry cv) => (cv.FirstName + " " + cv.LastName).Trim().Length > 0 ? cv.Title : string.Empty;

    private static string Address(CvEntry cv) =>
        string.Join(", ", new[] { (cv.AddressNumber + " " + cv.AddressStreet).Trim(), (cv.AddressZip + " " + cv.AddressCity).Trim(), cv.AddressCountry }.Where(p => p.Length > 0));

    private static List<string> Contact(CvEntry cv) =>
        new[] { cv.Email, cv.Phone, cv.LinkedInProfile, Address(cv) }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

    /// <summary>The words of the file, in the language of the CV.</summary>
    private sealed record Labels(string Contact, string Profile, string Skills, string Experience, string Education, string Creations, string Today);

    private static readonly Dictionary<string, Labels> Words = new()
    {
        ["fr"] = new("Contact", "Profil", "Compétences", "Expérience", "Formation", "Réalisations", "aujourd'hui"),
        ["en"] = new("Contact", "Profile", "Skills", "Experience", "Education", "Creations", "present"),
        ["de"] = new("Kontakt", "Profil", "Kenntnisse", "Berufserfahrung", "Ausbildung", "Projekte", "heute")
    };

    private static readonly Dictionary<string, string[]> Common = new()
    {
        ["fr"] = ["le", "la", "les", "des", "et", "du", "une", "un", "pour", "dans", "avec", "en", "au", "sur", "d'un", "d'une", "à"],
        ["en"] = ["the", "and", "of", "to", "in", "for", "with", "a", "an", "on", "at", "from", "by"],
        ["de"] = ["der", "die", "das", "und", "für", "mit", "von", "zu", "im", "ein", "eine", "den", "auf", "bei"]
    };

    /// <summary>
    /// The language the CV is written in (fr, en or de), from the commonest words of its texts;
    /// <paramref name="fallback"/> (the language of the chat) when it has too little text to tell.
    /// </summary>
    public static string LanguageOf(CvEntry cv, string? fallback)
    {
        var text = string.Join(" ", new[] { cv.Summary }.Concat(cv.Experiences.Select(e => e.Description)).Concat(cv.Educations.Select(e => e.Description))
            .Concat(cv.Creations.Select(c => c.Description)));
        var words = new string(text.ToLowerInvariant().Select(c => char.IsLetter(c) || c == '\'' ? c : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var best = Common.Select(pair => (Language: pair.Key, Count: words.Count(pair.Value.Contains))).OrderByDescending(x => x.Count).ToList();
        if (best[0].Count >= 3 && best[0].Count > best[1].Count)
            return best[0].Language;
        fallback = (fallback ?? string.Empty).ToLowerInvariant();
        return Words.ContainsKey(fallback) ? fallback : "en";
    }

    private static string Date(DateTime? date) => date is { Year: > 1 } d ? d is { Month: 1, Day: 1, Second: 1 } ? d.ToString("yyyy") : d.ToString("MM/yyyy") : string.Empty;

    /// <summary>A period as the file writes it: <c>03/2019 – 01/2022</c>, <c>2016 – aujourd'hui</c>.</summary>
    private static string Period(DateTime? start, DateTime? end, Labels labels)
    {
        var from = Date(start);
        var to = Date(end);
        if (from.Length == 0 && to.Length == 0)
            return string.Empty;
        return from.Length == 0 ? to : $"{from} – {(to.Length > 0 ? to : labels.Today)}";
    }

    /// <summary>The lines of a description: (true, text) for a point of a list ("- …"), (false, text) for a plain line.</summary>
    private static List<(bool Bullet, string Text)> Lines(string? description)
    {
        var lines = new List<(bool, string)>();
        foreach (var raw in (description ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bullet = raw.Length > 1 && raw[0] is '-' or '•' or '*' or '–' && raw.Length > 1 && char.IsWhiteSpace(raw[1]);
            lines.Add((bullet, bullet ? raw[1..].Trim() : raw));
        }
        return lines;
    }

    // ── PDF ──

    private static string[] Fonts(params string[] names)
    {
        var families = SkiaSharp.SKFontManager.Default.FontFamilies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = names.Where(families.Contains).ToArray();
        return installed.Length > 0 ? installed : [names[0]];
    }

    /// <summary>The CV as a PDF (A4); its headings in the language of the CV (<paramref name="language"/> when it cannot be told).</summary>
    public static byte[] Pdf(CvEntry cv, string? language = null)
    {
        var labels = Words[LanguageOf(cv, language)];
        var portrait = Portrait(cv.Photo);
        // The licence and the folders of fonts of QuestPDF are set there.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(TapePdfExport).TypeHandle);
        var fonts = Fonts("Segoe UI", "Helvetica Neue", "Inter", "Noto Sans", "Arial").Concat(Fonts("Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji")).Distinct().ToArray();
        const float side = 188;
        return QuestPDF.Fluent.Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(0);
            page.DefaultTextStyle(style => style.FontFamily(fonts).FontSize(9.5f).FontColor(Body).LineHeight(1.35f));
            // The side column, on every page.
            page.Background().Row(row =>
            {
                row.ConstantItem(side).Background(Dark);
                row.RelativeItem().Background(Colors.White);
            });
            page.Content().Row(row =>
            {
                row.ConstantItem(side).PaddingHorizontal(20).PaddingVertical(30).Column(column =>
                {
                    column.Spacing(4);
                    if (portrait is not null)
                        column.Item().PaddingBottom(12).Width(104).Height(104).Image(portrait);
                    column.Item().Text(Person(cv)).FontSize(21).Bold().FontColor(Colors.White).LineHeight(1.1f);
                    if (Headline(cv).Length > 0)
                        column.Item().PaddingTop(2).Text(Headline(cv)).FontSize(10.5f).FontColor(AccentOnDark);
                    column.Item().PaddingTop(10).Width(34).Height(3).Background(AccentOnDark);

                    var contact = Contact(cv);
                    if (contact.Count > 0)
                    {
                        SideHeading(column, labels.Contact);
                        foreach (var line in contact)
                            column.Item().Text(line).FontSize(8.5f).FontColor(OnDark);
                    }
                    if (cv.Skills.Count > 0)
                    {
                        SideHeading(column, labels.Skills);
                        column.Item().Inlined(inlined =>
                        {
                            inlined.Spacing(4);
                            inlined.VerticalSpacing(4);
                            foreach (var skill in cv.Skills)
                                inlined.Item().Background(DarkSoft).PaddingHorizontal(6).PaddingVertical(2).Text(skill).FontSize(8).FontColor(OnDark);
                        });
                    }
                    if (cv.Educations.Count > 0)
                    {
                        SideHeading(column, labels.Education);
                        foreach (var e in cv.Educations)
                        {
                            var degree = string.Join(", ", new[] { e.Degree, e.FieldOfStudy }.Where(p => p.Length > 0));
                            column.Item().PaddingTop(3).Text(degree.Length > 0 ? degree : e.Institution).FontSize(9).SemiBold().FontColor(Colors.White);
                            if (degree.Length > 0 && e.Institution.Length > 0)
                                column.Item().Text(e.Institution).FontSize(8.5f).FontColor(OnDark);
                            if (Period(e.StartDate, e.EndDate, labels) is { Length: > 0 } period)
                                column.Item().Text(period).FontSize(8).FontColor(OnDarkMuted);
                            if (e.Description.Length > 0)
                                column.Item().Text(e.Description).FontSize(8).FontColor(OnDarkMuted);
                        }
                    }
                });

                row.RelativeItem().PaddingHorizontal(30).PaddingVertical(32).Column(column =>
                {
                    column.Spacing(5);
                    if (cv.Summary.Length > 0)
                    {
                        MainHeading(column, labels.Profile, first: true);
                        column.Item().Text(cv.Summary).FontSize(10).FontColor(Body);
                    }
                    // Each entry is one block: when little room is left at the foot of a page it starts on the next one, its
                    // heading with it.
                    string? heading = cv.Experiences.Count > 0 ? labels.Experience : null;
                    var first = cv.Summary.Length == 0;
                    foreach (var e in cv.Experiences)
                    {
                        var title = heading;
                        var top = first;
                        heading = null;
                        first = false;
                        column.Item().EnsureSpace(title is null ? 80 : 120).Column(entry =>
                        {
                            entry.Spacing(5);
                            if (title is not null)
                                MainHeading(entry, title, top);
                            entry.Item().PaddingTop(5).Row(head =>
                            {
                                head.RelativeItem().Text(e.Position.Length > 0 ? e.Position : e.Company).FontSize(11).SemiBold().FontColor(Ink);
                                head.AutoItem().AlignBottom().Text(Period(e.StartDate, e.EndDate, labels)).FontSize(8.5f).FontColor(Muted);
                            });
                            if (e.Position.Length > 0 && e.Company.Length > 0)
                                entry.Item().Text(e.Company).FontSize(9.5f).SemiBold().FontColor(Accent);
                            Description(entry, e.Description);
                            if (e.Technologies.Count > 0)
                                entry.Item().PaddingTop(1).Text(string.Join("  ·  ", e.Technologies)).FontSize(8.5f).FontColor(Muted);
                        });
                    }
                    heading = cv.Creations.Count > 0 ? labels.Creations : null;
                    first = cv.Summary.Length == 0 && cv.Experiences.Count == 0;
                    foreach (var c in cv.Creations)
                    {
                        var title = heading;
                        var top = first;
                        heading = null;
                        first = false;
                        column.Item().EnsureSpace(title is null ? 60 : 100).Column(entry =>
                        {
                            entry.Spacing(5);
                            if (title is not null)
                                MainHeading(entry, title, top);
                            entry.Item().PaddingTop(5).Text(c.Title.Length > 0 ? c.Title : c.Url).FontSize(10.5f).SemiBold().FontColor(Ink);
                            Description(entry, c.Description);
                            if (c.Url.Length > 0 && c.Title.Length > 0)
                                entry.Item().Text(c.Url).FontSize(8.5f).FontColor(Accent);
                        });
                    }
                });
            });
        })).GeneratePdf();
    }

    private static void SideHeading(ColumnDescriptor column, string text) =>
        column.Item().PaddingTop(18).PaddingBottom(2).Text(text.ToUpperInvariant()).FontSize(8.5f).Bold().FontColor(AccentOnDark).LetterSpacing(0.14f);

    private static void MainHeading(ColumnDescriptor column, string text, bool first) =>
        column.Item().PaddingTop(first ? 0 : 16).PaddingBottom(2).BorderBottom(1).BorderColor(Line).PaddingBottom(4)
            .Text(text.ToUpperInvariant()).FontSize(10).Bold().FontColor(Accent).LetterSpacing(0.14f);

    private static void Description(ColumnDescriptor column, string description)
    {
        foreach (var (bullet, text) in Lines(description))
        {
            if (!bullet)
            {
                column.Item().Text(text);
                continue;
            }
            column.Item().Row(row =>
            {
                row.ConstantItem(11).Text("•").FontColor(Accent);
                row.RelativeItem().Text(text);
            });
        }
    }

    // ── Word ──

    private const string WordFont = "Segoe UI";

    private static string Hex(string color) => color.TrimStart('#');

    private static Run WordRun(string text, double size, string color, bool bold = false, bool caps = false, int spacing = 0)
    {
        var properties = new RunProperties(new RunFonts { Ascii = WordFont, HighAnsi = WordFont, ComplexScript = WordFont });
        if (bold)
            properties.Append(new Bold());
        if (caps)
            properties.Append(new Caps());
        properties.Append(new DocumentFormat.OpenXml.Wordprocessing.Color { Val = Hex(color) });
        if (spacing != 0)
            properties.Append(new Spacing { Val = spacing });
        properties.Append(new FontSize { Val = ((int)Math.Round(size * 2)).ToString() });
        return new Run(properties, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Paragraph WordParagraph(int before, int after, params OpenXmlElement[] content)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = before.ToString(), After = after.ToString(), Line = "264", LineRule = LineSpacingRuleValues.Auto }));
        paragraph.Append(content);
        return paragraph;
    }

    private static Paragraph WordHeading(string text)
    {
        var paragraph = WordParagraph(320, 100, WordRun(text, 10.5, Accent, bold: true, caps: true, spacing: 30));
        paragraph.ParagraphProperties!.Append(new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 3, Color = Hex(Line) }));
        paragraph.ParagraphProperties.Append(new KeepNext());
        return paragraph;
    }

    /// <summary>A line with <paramref name="left"/> then <paramref name="right"/> at the right edge of the text.</summary>
    private static Paragraph WordSplitLine(int before, Run left, Run right, int width)
    {
        var paragraph = WordParagraph(before, 0, left, new Run(new TabChar()), right);
        paragraph.ParagraphProperties!.Append(new Tabs(new TabStop { Val = TabStopValues.Right, Position = width }));
        paragraph.ParagraphProperties.Append(new KeepNext());
        return paragraph;
    }

    private static IEnumerable<Paragraph> WordDescription(string description)
    {
        foreach (var (bullet, text) in Lines(description))
        {
            if (!bullet)
            {
                yield return WordParagraph(40, 0, WordRun(text, 10, Body));
                continue;
            }
            var paragraph = WordParagraph(30, 0, WordRun("•", 10, Accent), new Run(new TabChar()), WordRun(text, 10, Body));
            paragraph.ParagraphProperties!.Append(new Indentation { Left = "284", Hanging = "284" });
            yield return paragraph;
        }
    }

    /// <summary>An inline square picture, <paramref name="pixels"/> wide.</summary>
    private static Drawing WordPicture(string relationshipId, int pixels)
    {
        long size = pixels * 9525L;
        return new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = size, Cy = size },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = 1U, Name = "Photo" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 1U, Name = "photo.png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = size, Cy = size }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U
            });
    }

    /// <summary>The CV as a Word document (A4), written to <paramref name="stream"/>; its headings in the language of the CV (<paramref name="language"/> when it cannot be told).</summary>
    public static void Word(Stream stream, CvEntry cv, string? language = null)
    {
        var labels = Words[LanguageOf(cv, language)];
        const int pageWidth = 11906, pageHeight = 16838, margin = 1020;
        const int width = pageWidth - 2 * margin;
        using var buffer = new MemoryStream();
        using (var document = WordprocessingDocument.Create(buffer, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new Body();

            // The band of the person: a table of one dark cell, as wide as the text.
            var cell = new TableCell(new TableCellProperties(
                new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = width.ToString() },
                new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = Hex(Dark) },
                new TableCellMargin(new TopMargin { Width = "340", Type = TableWidthUnitValues.Dxa }, new LeftMargin { Width = "400", Type = TableWidthUnitValues.Dxa },
                    new BottomMargin { Width = "340", Type = TableWidthUnitValues.Dxa }, new RightMargin { Width = "400", Type = TableWidthUnitValues.Dxa })));
            cell.Append(WordParagraph(0, 0, WordRun(Person(cv), 26, "#FFFFFF", bold: true)));
            if (Headline(cv).Length > 0)
                cell.Append(WordParagraph(40, 0, WordRun(Headline(cv), 13, AccentOnDark)));
            var contact = Contact(cv);
            if (contact.Count > 0)
                cell.Append(WordParagraph(160, 0, WordRun(string.Join("   ·   ", contact), 9, OnDark)));
            var band = new TableRow();
            var grid = new TableGrid();
            if (Portrait(cv.Photo) is { } portrait)
            {
                const int photoWidth = 2100, photoPixels = 104;
                var part = main.AddImagePart(ImagePartType.Png);
                using (var png = new MemoryStream(portrait))
                    part.FeedData(png);
                var photoCell = new TableCell(new TableCellProperties(
                    new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = photoWidth.ToString() },
                    new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = Hex(Dark) },
                    new TableCellMargin(new TopMargin { Width = "340", Type = TableWidthUnitValues.Dxa }, new LeftMargin { Width = "400", Type = TableWidthUnitValues.Dxa },
                        new BottomMargin { Width = "340", Type = TableWidthUnitValues.Dxa }, new RightMargin { Width = "0", Type = TableWidthUnitValues.Dxa }),
                    new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }));
                photoCell.Append(WordParagraph(0, 0, new Run(WordPicture(main.GetIdOfPart(part), photoPixels))));
                band.Append(photoCell);
                grid.Append(new GridColumn { Width = photoWidth.ToString() });
                cell.TableCellProperties!.TableCellWidth!.Width = (width - photoWidth).ToString();
                cell.TableCellProperties.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
                grid.Append(new GridColumn { Width = (width - photoWidth).ToString() });
            }
            else
                grid.Append(new GridColumn { Width = width.ToString() });
            band.Append(cell);
            body.Append(new Table(
                new TableProperties(new TableWidth { Type = TableWidthUnitValues.Dxa, Width = width.ToString() }, new TableLayout { Type = TableLayoutValues.Fixed }),
                grid,
                band));

            if (cv.Summary.Length > 0)
            {
                body.Append(WordHeading(labels.Profile));
                foreach (var line in cv.Summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    body.Append(WordParagraph(40, 0, WordRun(line, 10.5, Body)));
            }
            if (cv.Skills.Count > 0)
            {
                body.Append(WordHeading(labels.Skills));
                var runs = new List<OpenXmlElement>();
                for (var i = 0; i < cv.Skills.Count; i++)
                {
                    if (i > 0)
                        runs.Add(WordRun("   ·   ", 10, Accent, bold: true));
                    runs.Add(WordRun(cv.Skills[i], 10, Ink));
                }
                body.Append(WordParagraph(40, 0, runs.ToArray()));
            }
            if (cv.Experiences.Count > 0)
            {
                body.Append(WordHeading(labels.Experience));
                foreach (var e in cv.Experiences)
                {
                    body.Append(WordSplitLine(160, WordRun(e.Position.Length > 0 ? e.Position : e.Company, 11.5, Ink, bold: true),
                        WordRun(Period(e.StartDate, e.EndDate, labels), 9, Muted), width));
                    if (e.Position.Length > 0 && e.Company.Length > 0)
                        body.Append(WordParagraph(0, 20, WordRun(e.Company, 10, Accent, bold: true)));
                    body.Append(WordDescription(e.Description));
                    if (e.Technologies.Count > 0)
                        body.Append(WordParagraph(50, 0, WordRun(string.Join("  ·  ", e.Technologies), 9, Muted)));
                }
            }
            if (cv.Educations.Count > 0)
            {
                body.Append(WordHeading(labels.Education));
                foreach (var e in cv.Educations)
                {
                    var degree = string.Join(", ", new[] { e.Degree, e.FieldOfStudy }.Where(p => p.Length > 0));
                    body.Append(WordSplitLine(140, WordRun(degree.Length > 0 ? degree : e.Institution, 11, Ink, bold: true),
                        WordRun(Period(e.StartDate, e.EndDate, labels), 9, Muted), width));
                    if (degree.Length > 0 && e.Institution.Length > 0)
                        body.Append(WordParagraph(0, 20, WordRun(e.Institution, 10, Accent, bold: true)));
                    body.Append(WordDescription(e.Description));
                }
            }
            if (cv.Creations.Count > 0)
            {
                body.Append(WordHeading(labels.Creations));
                foreach (var c in cv.Creations)
                {
                    var head = WordParagraph(140, 0, WordRun(c.Title.Length > 0 ? c.Title : c.Url, 11, Ink, bold: true));
                    head.ParagraphProperties!.Append(new KeepNext());
                    body.Append(head);
                    foreach (var line in WordDescription(c.Description))
                    {
                        // Its link is not left alone at the top of the next page.
                        if (c.Url.Length > 0 && c.Title.Length > 0)
                            line.ParagraphProperties!.Append(new KeepNext());
                        body.Append(line);
                    }
                    if (c.Url.Length > 0 && c.Title.Length > 0)
                        body.Append(WordParagraph(30, 0, WordRun(c.Url, 9, Accent)));
                }
            }

            body.Append(new SectionProperties(
                new DocumentFormat.OpenXml.Wordprocessing.PageSize { Width = pageWidth, Height = pageHeight },
                new PageMargin { Top = margin, Bottom = margin, Left = margin, Right = margin, Header = 500, Footer = 500, Gutter = 0 }));
            // The properties of a paragraph in the order Word wants them.
            foreach (var properties in body.Descendants<ParagraphProperties>().ToList())
            {
                var ordered = properties.ChildElements.OrderBy(child => child switch
                {
                    KeepNext => 0,
                    ParagraphBorders => 1,
                    Tabs => 2,
                    SpacingBetweenLines => 3,
                    Indentation => 4,
                    _ => 5
                }).ToList();
                properties.RemoveAllChildren();
                properties.Append(ordered);
            }
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(body);
            main.Document.Save();
        }
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }
}
