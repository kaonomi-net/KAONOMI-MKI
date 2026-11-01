using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Views;
using Markdig;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Publishes a writing of <c>/write</c> (the PUBLISH tab): the whole writing as one document, its author, its summary
/// (when asked), then each chapter (its number and title, its text, its footnotes with their links), exported as a
/// Word document, a PDF (both with the exports of the editor of /work), a web page (HTML, its pictures inside it) or a
/// post for Blogger (the HTML of the post, its pictures left to upload in Blogger).
/// </summary>
public static partial class GhostWriterPublisher
{
    /// <summary>The words of the document in the language of the writing (fr, de, else English).</summary>
    private sealed record Words(string Summary, string Notes, string By);

    private static Words WordsOf(string language) => language.ToLowerInvariant() switch
    {
        "fr" => new("Résumé", "Notes", "par"),
        "de" => new("Zusammenfassung", "Anmerkungen", "von"),
        _ => new("Summary", "Notes", "by")
    };

    private const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹";

    private static string Superscript(int number) =>
        new(number.ToString(CultureInfo.InvariantCulture).Select(d => Superscripts[d - '0']).ToArray());

    /// <summary>
    /// The writing in the format of the editor (Markdown, one paragraph per line): its author, its summary (when
    /// <paramref name="withSummary"/>), each chapter as a heading, its text and its notes. The footnote marks are digits
    /// in superscript, or (<paramref name="html"/>) links to the notes and back.
    /// </summary>
    public static string Document(GhostWriterEntry writing, bool withSummary, string language, bool html)
    {
        var words = WordsOf(language);
        var text = new StringBuilder();
        if (writing.Author.Trim().Length > 0)
            text.Append("<p class=\"subtitle\">").Append(Escape($"{words.By} {writing.Author.Trim()}")).Append("</p>\n\n");
        if (withSummary && writing.Summary.Trim().Length > 0)
        {
            text.Append("# ").Append(words.Summary).Append("\n\n");
            text.Append(writing.Summary.Trim()).Append("\n\n");
        }
        foreach (var chapter in writing.Chapters.OrderBy(c => c.Position))
        {
            text.Append("# ").Append(chapter.Position).Append(". ").Append(Escape(chapter.Title.Trim())).Append("\n\n");
            text.Append(Marks(chapter, html)).Append("\n\n");
            var notes = chapter.Footnotes.OrderBy(n => n.Number).ToList();
            if (notes.Count == 0)
                continue;
            text.Append("### ").Append(words.Notes).Append("\n\n");
            foreach (var note in notes)
                text.Append(Note(chapter, note, html)).Append("\n\n");
        }
        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>The text of <paramref name="chapter"/>, each footnote link <c>[words](note:N)</c> made the words and the mark of the note.</summary>
    private static string Marks(WriterChapter chapter, bool html) =>
        NoteLink().Replace(chapter.Text.Trim(), m =>
        {
            var number = int.Parse(m.Groups["number"].Value, CultureInfo.InvariantCulture);
            return m.Groups["words"].Value + (html
                ? $"<sup id=\"ref-{chapter.Position}-{number}\"><a href=\"#note-{chapter.Position}-{number}\">{number}</a></sup>"
                : Superscript(number));
        });

    /// <summary>A note at the end of its chapter: its mark, its text, its links (and, in HTML, the way back to its words).</summary>
    private static string Note(WriterChapter chapter, WriterFootnote note, bool html)
    {
        var links = note.Links.Where(l => l.Url.Trim().Length > 0).ToList();
        // A link of a note is a link in the export only when it is a web or mail address; else its title alone.
        static bool IsWeb(string url) => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";
        static string Name(WriterFootnoteLink l) => l.Title.Length > 0 ? l.Title : l.Url;
        if (!html)
        {
            var line = $"{Superscript(note.Number)} {note.Text.Trim()}";
            if (links.Count > 0)
                line += " — " + string.Join(" · ", links.Select(l => IsWeb(l.Url) ? $"[{Escape(Name(l))}]({l.Url.Trim()})" : Escape(Name(l))));
            return line;
        }
        var id = $"{chapter.Position}-{note.Number}";
        var block = new StringBuilder($"<p class=\"footnote\" id=\"note-{id}\"><sup>{note.Number}</sup> {WebUtility.HtmlEncode(note.Text.Trim())}");
        if (links.Count > 0)
            block.Append(" — ").Append(string.Join(" · ", links.Select(l => IsWeb(l.Url)
                ? $"<a href=\"{WebUtility.HtmlEncode(l.Url.Trim())}\">{WebUtility.HtmlEncode(Name(l))}</a>"
                : WebUtility.HtmlEncode(Name(l)))));
        block.Append($" <a class=\"back\" href=\"#ref-{id}\">↩</a></p>");
        return block.ToString();
    }

    /// <summary>Text that would be read as a heading or a picture at the start of a line, escaped.</summary>
    private static string Escape(string text) => text.Length > 0 && text[0] is '#' or '!' ? "\\" + text : text;

    // ── Word and PDF (the exports of the editor of /work) ──

    /// <summary>The writing as a Word document; returns the number of pictures embedded.</summary>
    public static Task<int> WordAsync(Stream stream, GhostWriterEntry writing, bool withSummary, string language, CancellationToken cancellationToken = default) =>
        TapeWordExport.WriteAsync(stream, Document(writing, withSummary, language, html: false), LoadPngAsync, cancellationToken, Title(writing));

    /// <summary>The writing as a PDF document; returns the number of pictures embedded.</summary>
    public static Task<int> PdfAsync(Stream stream, GhostWriterEntry writing, bool withSummary, string language, CancellationToken cancellationToken = default) =>
        TapePdfExport.WriteAsync(stream, Document(writing, withSummary, language, html: false), LoadPngAsync, Title(writing), cancellationToken);

    private static string Title(GhostWriterEntry writing) => writing.Title.Trim().Length > 0 ? writing.Title.Trim() : "Untitled writing";

    /// <summary>A local picture as PNG for the exports (null when it cannot be read).</summary>
    public static async Task<TapeWordExport.Picture?> LoadPngAsync(string path)
    {
        using var bitmap = await PictureLibrary.DecodeAsync(path, 1600, CancellationToken.None);
        if (bitmap is null)
            return null;
        using var png = new MemoryStream();
#pragma warning disable CS0618
        bitmap.Save(png);
#pragma warning restore CS0618
        return new(png.ToArray(), bitmap.PixelSize.Width, bitmap.PixelSize.Height);
    }

    // ── HTML and Blogger ──

    /// <summary>
    /// The writing as a web page of its own (a light page to read, its pictures inside it as data, the notes linked to
    /// their words and back).
    /// </summary>
    public static string Html(GhostWriterEntry writing, bool withSummary, string language)
    {
        var title = WebUtility.HtmlEncode(Title(writing));
        return $$"""
            <!DOCTYPE html>
            <html lang="{{WebUtility.HtmlEncode(language)}}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="generator" content="KAONOMI ghost writer">
            <title>{{title}}</title>
            <style>
            body { max-width: 46rem; margin: 3rem auto; padding: 0 1.25rem; font: 18px/1.65 Georgia, "Times New Roman", serif; color: #1f2328; background: #fffdf8; }
            h1.title { font-size: 2.4rem; line-height: 1.2; margin-bottom: .25rem; }
            .subtitle { color: #6b7280; font-style: italic; margin-top: 0; }
            h1 { font-size: 1.7rem; margin-top: 3rem; border-bottom: 1px solid #e5e7eb; padding-bottom: .3rem; }
            h3 { font-size: 1rem; text-transform: uppercase; letter-spacing: .08em; color: #6b7280; margin-top: 2rem; }
            img { max-width: 100%; height: auto; }
            a { color: #2563eb; }
            sup a { text-decoration: none; }
            .footnote { font-size: .9rem; color: #374151; margin: .4rem 0; }
            .footnote .back { text-decoration: none; }
            pre { background: #f6f8fa; padding: .8rem; overflow-x: auto; font-size: .85rem; }
            </style>
            </head>
            <body>
            <h1 class="title">{{title}}</h1>
            {{Body(writing, withSummary, language, embedPictures: true)}}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// The writing as the HTML of a Blogger post (to paste in the HTML view of the post, its title in the title of the
    /// post): no page around it, the styles in the elements; the local pictures cannot be in a post, so each one is
    /// named in its place, to upload in Blogger. Returns the HTML and the pictures to upload.
    /// </summary>
    public static (string Html, IReadOnlyList<string> Pictures) Blogger(GhostWriterEntry writing, bool withSummary, string language)
    {
        var pictures = new List<string>();
        var body = Body(writing, withSummary, language, embedPictures: false, pictures);
        body = body.Replace("<p class=\"subtitle\">", "<p style=\"color:#6b7280;font-style:italic\">")
            .Replace("<p class=\"footnote\"", "<p style=\"font-size:90%;color:#374151\"");
        return (body, pictures);
    }

    /// <summary>The body of the HTML: the document turned from Markdown, its pictures as data or named in their place.</summary>
    private static string Body(GhostWriterEntry writing, bool withSummary, string language, bool embedPictures, List<string>? missing = null)
    {
        var markdown = Paragraphs(Document(writing, withSummary, language, html: true));
        markdown = Picture().Replace(markdown, m =>
        {
            var path = JournalMarkdown.LocalPath(m.Groups["url"].Value);
            var alt = WebUtility.HtmlEncode(m.Groups["alt"].Value);
            var width = m.Groups["width"].Success ? $" width=\"{m.Groups["width"].Value}\"" : string.Empty;
            if (Uri.TryCreate(m.Groups["url"].Value, UriKind.Absolute, out var uri) && !uri.IsFile)
                return uri.Scheme is "http" or "https" ? $"<img src=\"{WebUtility.HtmlEncode(uri.ToString())}\" alt=\"{alt}\"{width}>" : $"<em>[📷 {alt}]</em>";
            // Only a picture of the machine is put in the page (never another file named as a picture).
            if (embedPictures && PictureExtensions.Contains(Path.GetExtension(path)) && File.Exists(path))
                return $"<img src=\"data:{Mime(path)};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}\" alt=\"{alt}\"{width}>";
            missing?.Add(path);
            return $"<em>[📷 {WebUtility.HtmlEncode(Path.GetFileName(path))}]</em>";
        });
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        return Sanitize(Markdown.ToHtml(markdown, pipeline).Trim());
    }

    /// <summary>The pictures put in a web page (their data inside it).</summary>
    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    /// <summary>
    /// The HTML of a page made safe to publish: the text of the chapters may come from the server, an agent or the
    /// model, and Markdown lets HTML through. Gone: the elements that run or load something (script, style, iframe,
    /// object, embed, form…), the attributes of events (onclick…), the addresses that run something (javascript:,
    /// vbscript:, file:, data: but the pictures). Kept: the HTML of the editor (alignment, title, notes).
    /// </summary>
    public static string Sanitize(string html)
    {
        html = ActiveElement().Replace(html, string.Empty);
        html = ActiveTag().Replace(html, string.Empty);
        html = EventAttribute().Replace(html, string.Empty);
        html = UnsafeStyle().Replace(html, string.Empty);
        return UrlAttribute().Replace(html, m =>
        {
            var value = WebUtility.HtmlDecode(m.Groups["value"].Value).Trim();
            // Spaces and control characters hidden in a scheme ("java\tscript:") are not counted.
            var scheme = new string(value.TakeWhile(c => c != ':').Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray()).ToLowerInvariant();
            var safe = !value.Contains(':') || value.StartsWith('#') || value.StartsWith('/')
                       || scheme is "http" or "https" or "mailto"
                       || (scheme == "data" && m.Groups["name"].Value.Equals("src", StringComparison.OrdinalIgnoreCase)
                           && System.Text.RegularExpressions.Regex.IsMatch(value, @"^data:image/(png|jpe?g|gif|webp|bmp);base64,", RegexOptions.IgnoreCase));
            return safe ? m.Value : $"{m.Groups["name"].Value}=\"#\"";
        });
    }

    [GeneratedRegex(@"<(script|style|iframe|frame|frameset|object|embed|applet|form|input|button|textarea|select|link|meta|base|svg|math|template)\b[^>]*>[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveElement();

    [GeneratedRegex(@"</?(script|style|iframe|frame|frameset|object|embed|applet|form|input|button|textarea|select|link|meta|base|svg|math|template)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveTag();

    [GeneratedRegex(@"\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttribute();

    [GeneratedRegex(@"\s+style\s*=\s*(""[^""]*(expression|javascript|url\s*\()[^""]*""|'[^']*(expression|javascript|url\s*\()[^']*')", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeStyle();

    [GeneratedRegex(@"(?<name>href|src|action|formaction|xlink:href|poster|background)\s*=\s*(""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex UrlAttribute();

    /// <summary>
    /// The format of the editor (a paragraph per line) for a Markdown reader (a blank line between paragraphs): the
    /// lines are kept apart, but not inside the blocks of code.
    /// </summary>
    private static string Paragraphs(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new StringBuilder();
        var inCode = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inCode = !inCode;
            result.Append(line).Append(inCode || line.TrimStart().StartsWith("```", StringComparison.Ordinal) ? "\n" : "\n\n");
        }
        return result.ToString();
    }

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "image/png"
    };

    /// <summary>A footnote link of the editor: <c>[words](note:N)</c>.</summary>
    [GeneratedRegex(@"\[(?<words>(?:[^\[\]]|\[[^\]]*\])*)\]\(<?note:(?<number>\d+)>?\)")]
    private static partial Regex NoteLink();

    /// <summary>A picture of the editor: <c>![alt|width](address)</c>.</summary>
    [GeneratedRegex(@"!\[(?<alt>[^\]|]*)(?:\|(?<width>\d+))?\]\((?<url>[^)\s]+)\)")]
    private static partial Regex Picture();
}
