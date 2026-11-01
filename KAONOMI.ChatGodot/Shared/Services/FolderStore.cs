using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Services;

/// <summary>A folder of windows (📁+ in the header of a window): its name, what it is about and its security level.</summary>
public sealed class WindowFolder
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Security level, as the classification of documents: public, internal, confidential or secret.</summary>
    [JsonPropertyName("securityClassification")]
    public string SecurityClassification { get; set; } = "internal";

    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; }

    /// <summary>The windows added to the folder (not saved: counted from its files).</summary>
    [JsonIgnore]
    public int WindowCount { get; set; }

    public override string ToString() => Name;
}

/// <summary>A window kept in a folder: its title, when it was added, its data (JSON) and its picture (PNG), if any.</summary>
public sealed class FolderItem
{
    public string WindowName { get; init; } = string.Empty;

    public DateTime Added { get; init; }

    /// <summary>Path of the JSON file of the window.</summary>
    public string DataPath { get; init; } = string.Empty;

    /// <summary>Path of its picture, or null when there is none.</summary>
    public string? PicturePath { get; init; }

    /// <summary>The JSON of the window (its name and data), as it was saved.</summary>
    public string Data { get; init; } = string.Empty;
}

/// <summary>
/// The folders of windows, in <c>~/kaonomi/folders</c> (out of the build, which a clean would erase): one directory per folder,
/// with its <c>folder.json</c> and, for each window added, a JSON file (its title and data) and a PNG picture of it.
/// </summary>
public static class FolderStore
{
    private const string FolderFile = "folder.json";

    /// <summary>The security levels a folder can have, from the lowest.</summary>
    public static IReadOnlyList<string> SecurityLevels { get; } = ["public", "internal", "confidential", "secret"];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Directory of the folders.</summary>
    public static string Root => Path.Combine(KAONOMI.Chat.Models.ChatSettings.KaonomiHome, "folders");

    /// <summary>The folders, by name; a folder whose file cannot be read is left out.</summary>
    public static List<WindowFolder> List()
    {
        var folders = new List<WindowFolder>();
        if (!Directory.Exists(Root))
            return folders;
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            try
            {
                var file = Path.Combine(directory, FolderFile);
                if (!File.Exists(file) || JsonSerializer.Deserialize<WindowFolder>(File.ReadAllText(file), Options) is not { } folder)
                    continue;
                folder.Id = Path.GetFileName(directory);
                folder.WindowCount = Directory.EnumerateFiles(directory, "*.json").Count(f => !f.EndsWith(FolderFile, StringComparison.OrdinalIgnoreCase));
                folders.Add(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                ActivityLog.Error("Folders", $"{directory} not read", ex);
            }
        }
        return folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Creates the folder <paramref name="name"/>; its directory takes a name made of it (made unique).</summary>
    public static WindowFolder Create(string name, string description, string securityClassification)
    {
        var id = Slug(name);
        var directory = Path.Combine(Root, id);
        for (var i = 2; Directory.Exists(directory); i++)
            directory = Path.Combine(Root, $"{id}-{i}");
        Directory.CreateDirectory(directory);
        var folder = new WindowFolder
        {
            Id = Path.GetFileName(directory),
            Name = name.Trim(),
            Description = description.Trim(),
            SecurityClassification = securityClassification,
            Created = DateTimeOffset.Now
        };
        File.WriteAllText(Path.Combine(directory, FolderFile), JsonSerializer.Serialize(folder, Options));
        return folder;
    }

    /// <summary>
    /// Adds a window to <paramref name="folder"/>: <paramref name="context"/> (the JSON of its title and data) and,
    /// when given, its <paramref name="picture"/> (PNG). Returns the path of the JSON file written.
    /// </summary>
    public static string AddWindow(WindowFolder folder, string windowTitle, string context, byte[]? picture)
    {
        var directory = Path.Combine(Root, folder.Id);
        Directory.CreateDirectory(directory);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Slug(windowTitle)}";
        var path = Path.Combine(directory, name + ".json");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(directory, $"{name}-{i}.json");
        File.WriteAllText(path, context);
        if (picture is { Length: > 0 })
            File.WriteAllBytes(Path.ChangeExtension(path, ".png"), picture);
        folder.WindowCount++;
        return path;
    }

    /// <summary>The windows of <paramref name="folder"/>, in the order they were added.</summary>
    public static List<FolderItem> Items(WindowFolder folder)
    {
        var items = new List<FolderItem>();
        var directory = Path.Combine(Root, folder.Id);
        if (!Directory.Exists(directory))
            return items;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Where(f => !f.EndsWith(FolderFile, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            try
            {
                var data = File.ReadAllText(file);
                var name = Path.GetFileNameWithoutExtension(file);
                try
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(data)?["window"]?.GetValue<string>() is { Length: > 0 } window)
                        name = window;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                }
                var picture = Path.ChangeExtension(file, ".png");
                items.Add(new FolderItem
                {
                    WindowName = name,
                    Added = AddedOf(file),
                    DataPath = file,
                    PicturePath = File.Exists(picture) ? picture : null,
                    Data = data
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ActivityLog.Error("Folders", $"{file} not read", ex);
            }
        }
        return items;
    }

    /// <summary>When a window was added: the date at the start of its file name, else the date of the file.</summary>
    private static DateTime AddedOf(string file) =>
        Path.GetFileName(file) is { Length: >= 15 } name
        && DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var added)
            ? added
            : File.GetCreationTime(file);

    /// <summary>A name for a file: letters and digits without accents, lower case, joined by dashes.</summary>
    private static string Slug(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
            else if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark
                     && builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "window" : slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }
}
