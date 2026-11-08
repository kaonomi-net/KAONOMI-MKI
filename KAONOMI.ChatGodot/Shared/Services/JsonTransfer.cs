using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Export and import of documents as JSON, shared by the windows that offer them (intels, symbolic patterns): a folder
/// with one file per document (<c>&lt;id&gt;.json</c>), or a file holding one document or an array of them.
/// </summary>
public static class JsonTransfer
{
    /// <summary>Indented, accents kept readable.</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Field names read whatever their case.</summary>
    public static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Writes each item in its own file of <paramref name="folder"/>, named after its id (characters not allowed in a
    /// file name become <c>_</c>; a second item with the same id, or none, gets a suffix instead of overwriting the
    /// first). A file of the same name already in the folder is overwritten. Returns how many files were written.
    /// </summary>
    public static async Task<int> ExportFolderAsync<T>(IEnumerable<T> items, Func<T, string?> id, string folder,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var invalid = Path.GetInvalidFileNameChars();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var written = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseName = string.IsNullOrWhiteSpace(id(item)) ? "item" : id(item)!;
            baseName = new string(baseName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            var name = baseName;
            for (var n = 2; !used.Add(name); n++)
                name = $"{baseName}-{n}";

            var json = JsonSerializer.Serialize(item, WriteOptions);
            await File.WriteAllTextAsync(Path.Combine(folder, name + ".json"), json, new UTF8Encoding(false), cancellationToken);
            progress?.Report(++written);
        }
        return written;
    }

    /// <summary>The documents of a JSON text: one object, or the objects of an array. Throws <see cref="JsonException"/> when invalid.</summary>
    public static List<JsonObject> ParseDocuments(string text) => JsonNode.Parse(text) switch
    {
        JsonArray array => array.OfType<JsonObject>().ToList(),
        JsonObject single => [single],
        _ => []
    };

    /// <summary>
    /// The documents of the <c>*.json</c> files of <paramref name="folder"/> (each file: one document or an array),
    /// with the number of files that could not be read or parsed.
    /// </summary>
    public static async Task<(List<JsonObject> Documents, int Files, int BadFiles)> ReadFolderAsync(string folder,
        CancellationToken cancellationToken = default)
    {
        var documents = new List<JsonObject>();
        var bad = 0;
        var files = Directory.GetFiles(folder, "*.json");
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var found = ParseDocuments(await File.ReadAllTextAsync(file, cancellationToken));
                if (found.Count == 0)
                    bad++;
                documents.AddRange(found);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                bad++;
            }
        }
        return (documents, files.Length, bad);
    }

    /// <summary>A document read from a file as <typeparamref name="T"/>, or null when it does not fit.</summary>
    public static T? ToEntry<T>(JsonObject document) where T : class
    {
        try
        {
            return document.Deserialize<T>(ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
