using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Views;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The calculator tape as one JSON file that carries its pictures: on export, each picture line of the content
/// (<c>![name|width](path)</c>) gets its picture as a <c>data:</c> address in base 64; on import, these pictures are
/// written as files into the pictures folder of the calculator (Pictures/kaonomi/maths by default) and the lines link
/// to them again.
/// </summary>
public static class TapeJson
{
    /// <summary>Value of <see cref="TapeExport.Format"/>, checked on import.</summary>
    public const string Format = "kaonomi-calculator-tape";

    /// <summary>The JSON file: what it is, when it was written, the title of the tape and its content (pictures inside).</summary>
    public sealed class TapeExport
    {
        /// <summary><see cref="TapeJson.Format"/> (no default: a JSON file without it is not a tape).</summary>
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("exportedAt")] public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
        [JsonPropertyName("title")] public string? Title { get; set; }

        /// <summary>The tape in its Markdown format, its pictures as <c>data:image/…;base64,…</c> addresses.</summary>
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Picture types by extension, for the <c>data:</c> addresses (and back on import).</summary>
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".heic"] = "image/heic", [".heif"] = "image/heif",
        [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".svg"] = "image/svg+xml"
    };

    /// <summary>
    /// The JSON of <paramref name="tape"/> titled <paramref name="title"/>, its pictures in base 64. Returns the JSON,
    /// the number of pictures embedded and the paths of the pictures that could not be read (their line keeps its path).
    /// </summary>
    public static (string Json, int Embedded, IReadOnlyList<string> Missing) Export(string tape, string? title)
    {
        var document = TapeDocument.Parse(tape);
        var embedded = 0;
        var missing = new List<string>();
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            if (document.Blocks[i] is not TapeImage image || image.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = JournalMarkdown.LocalPath(image.Url);
            try
            {
                var mime = MimeTypes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
                var data = $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
                document.Blocks[i] = new TapeImage(image.Alt, data, image.Width) { Alignment = image.Alignment };
                embedded++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                missing.Add(path);
            }
        }
        var export = new TapeExport { Format = Format, Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(), Content = document.Serialize() };
        return (JsonSerializer.Serialize(export, Options), embedded, missing);
    }

    /// <summary>
    /// Reads an exported tape: its pictures in base 64 are written into <paramref name="directory"/> (named after the
    /// picture, with a number when the name is taken) and their lines link to these files. Returns the content for the
    /// tape, its title and the number of pictures written.
    /// </summary>
    public static (string Content, string? Title, int Pictures) Import(string json, string directory)
    {
        TapeExport? export;
        try
        {
            export = JsonSerializer.Deserialize<TapeExport>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"This file is not a JSON file of a tape: {ex.Message}", ex);
        }
        if (export is null || export.Format != Format)
            throw new InvalidDataException("This JSON file is not a tape exported by the calculator (\"format\": \"" + Format + "\").");

        var document = TapeDocument.Parse(export.Content ?? string.Empty);
        var written = 0;
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            if (document.Blocks[i] is not TapeImage image || !image.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            var comma = image.Url.IndexOf(',');
            var header = comma > 5 ? image.Url[5..comma] : string.Empty;
            if (comma < 0 || !header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                continue;
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(image.Url[(comma + 1)..]);
            }
            catch (FormatException)
            {
                continue;
            }
            var mime = header[..^";base64".Length];
            var extension = MimeTypes.FirstOrDefault(m => m.Value.Equals(mime, StringComparison.OrdinalIgnoreCase)).Key ?? ".png";
            Directory.CreateDirectory(directory);
            var name = string.Concat((string.IsNullOrWhiteSpace(image.Alt) ? "picture" : image.Alt).Split(Path.GetInvalidFileNameChars()));
            var path = UniquePath(directory, name, extension);
            File.WriteAllBytes(path, bytes);
            // The line links to the new file (between < > when the path holds spaces), at the same width.
            var linked = (TapeImage)TapeDocument.Parse(JournalMarkdown.ImageMarkdown(path, image.Width)).Blocks[0];
            linked.Alignment = image.Alignment;
            document.Blocks[i] = linked;
            written++;
        }
        return (document.Serialize(), export.Title, written);
    }

    /// <summary>A free path for <paramref name="name"/> in <paramref name="directory"/> (name-2.png… when it is taken).</summary>
    private static string UniquePath(string directory, string name, string extension)
    {
        var path = Path.Combine(directory, name + extension);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(directory, $"{name}-{i}{extension}");
        return path;
    }
}
