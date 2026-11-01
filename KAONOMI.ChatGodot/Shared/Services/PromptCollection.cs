using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Prompt files (<c>*.prompt.json</c>), written by "Save prompt", read by "Load prompt" and run as a folder by
/// "Prompt collection" (every <c>*.prompt.json</c> file of the folder, not its sub-folders, in file name order).
/// A file holds <c>{"prompt": "…"}</c>; <c>content</c> or <c>text</c> are accepted instead of <c>prompt</c>, and so is
/// a plain JSON string.
/// </summary>
public static class PromptCollection
{
    /// <summary>File pattern of the prompts of a collection.</summary>
    public const string Pattern = "*.prompt.json";

    /// <summary>Extension of a prompt file.</summary>
    public const string Extension = ".prompt.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly string[] PromptFields = ["prompt", "content", "text"];

    /// <summary>The prompt files of <paramref name="folder"/>, sorted by name.</summary>
    public static IReadOnlyList<string> Find(string folder) =>
        Directory.EnumerateFiles(folder, Pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Content of a prompt file: <c>{"prompt": "…"}</c>.</summary>
    public static string Serialize(string prompt) => JsonSerializer.Serialize(new { prompt }, WriteOptions);

    /// <summary>
    /// <paramref name="path"/> with the <c>.prompt.json</c> extension (a <c>.json</c>, <c>.txt</c> or <c>.md</c>
    /// extension given in the save dialog is replaced).
    /// </summary>
    public static string WithExtension(string path)
    {
        if (path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            return path;
        var extension = Path.GetExtension(path);
        var stem = extension.ToLowerInvariant() is ".json" or ".txt" or ".md" ? path[..^extension.Length] : path;
        return stem + Extension;
    }

    /// <summary>The prompt of a file, or null when it has none (empty, invalid JSON, no prompt field).</summary>
    public static string? Read(string file)
    {
        try
        {
            return Parse(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The <c>"topic"</c> of a prompt file (e.g. "philosophie"), or null when it has none.</summary>
    public static string? ReadTopic(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var topic = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject()
                    .FirstOrDefault(p => p.Name.Equals("topic", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    .Value.GetString()
                : null;
            return string.IsNullOrWhiteSpace(topic) ? null : topic.Trim();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The prompt of a prompt file's content, or null when it has none.</summary>
    public static string? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;

            var prompt = root.ValueKind switch
            {
                JsonValueKind.String => root.GetString(),
                JsonValueKind.Object => root.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String
                                && PromptFields.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(p => Array.FindIndex(PromptFields, f => f.Equals(p.Name, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Value.GetString())
                    .FirstOrDefault(),
                _ => null
            };
            return string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
