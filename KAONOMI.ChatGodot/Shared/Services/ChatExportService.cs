using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>Writes a conversation snapshot as indented JSON or as a Word (.docx) document.</summary>
public static class ChatExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Keep accents and quotes readable in the file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Serializes the conversation to JSON.</summary>
    public static Task WriteJsonAsync(Stream stream, ChatExport export, CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeAsync(stream, export, JsonOptions, cancellationToken);

    /// <summary>
    /// Writes the conversation as a .docx: a title, then one header per message followed by its text.
    /// Markdown is kept as text, except fenced code blocks which are rendered in a monospace font.
    /// The package is built in memory first: OpenXml needs a readable, seekable stream, while
    /// save-dialog streams are typically write-only.
    /// </summary>
    public static async Task WriteWordAsync(Stream stream, ChatExport export, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        BuildWord(buffer, export);

        buffer.Position = 0;
        await buffer.CopyToAsync(stream, cancellationToken);
    }

    private static void BuildWord(Stream stream, ChatExport export)
    {
        using var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        var body = new Body();
        mainPart.Document = new Document(body);

        body.Append(MakeParagraph(MakeRun("Kaonomi Chat", bold: true, size: 36)));
        body.Append(MakeParagraph(MakeRun(
            $"Exported {export.ExportedAt:yyyy-MM-dd HH:mm} · model {export.Model ?? "?"} · {export.ServerUrl}",
            color: "808080", size: 18)));

        if (!string.IsNullOrWhiteSpace(export.SystemPrompt))
        {
            body.Append(MakeParagraph(MakeRun("System prompt", bold: true, color: "808080")));
            AppendContent(body, export.SystemPrompt);
        }

        foreach (var message in export.Messages)
        {
            var color = message.IsError ? "C00000" : message.Role == "user" ? "1F5FBF" : "2E7D32";
            body.Append(MakeParagraph(
                MakeRun(message.Author, bold: true, color: color, size: 24),
                MakeRun($"   {message.Timestamp:yyyy-MM-dd HH:mm:ss}", color: "808080", size: 18)));

            AppendContent(body, message.Content);

            if (!string.IsNullOrEmpty(message.Stats))
                body.Append(MakeParagraph(MakeRun(message.Stats, italic: true, color: "808080", size: 18)));
        }

        mainPart.Document.Save();
    }

    internal static void AppendContent(Body body, string content)
    {
        var inCode = false;
        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            body.Append(inCode
                ? MakeParagraph(MakeRun(rawLine, font: "Consolas", size: 18), shading: "F2F2F2", spacingAfter: "0")
                : MakeParagraph(MakeRun(rawLine)));
        }
    }

    internal static Paragraph MakeParagraph(params Run[] runs) => MakeParagraph(runs, null, null);

    internal static Paragraph MakeParagraph(Run run, string? shading, string? spacingAfter) =>
        MakeParagraph([run], shading, spacingAfter);

    internal static Paragraph MakeParagraph(Run[] runs, string? shading, string? spacingAfter)
    {
        var properties = new ParagraphProperties();
        if (shading is not null)
            properties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = shading });
        if (spacingAfter is not null)
            properties.Append(new SpacingBetweenLines { After = spacingAfter });

        var paragraph = new Paragraph(properties);
        paragraph.Append(runs);
        return paragraph;
    }

    internal static Run MakeRun(
        string text,
        bool bold = false,
        bool italic = false,
        string? color = null,
        int? size = null,
        string? font = null)
    {
        var properties = new RunProperties();
        if (font is not null)
            properties.Append(new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font });
        if (bold)
            properties.Append(new Bold());
        if (italic)
            properties.Append(new Italic());
        if (color is not null)
            properties.Append(new Color { Val = color });
        if (size is not null)
            properties.Append(new FontSize { Val = size.Value.ToString() }); // half-points

        return new Run(properties, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }
}
