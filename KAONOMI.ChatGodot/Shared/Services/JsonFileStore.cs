using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Files of the JSON client: call results in the configured result directory,
/// saved requests in the configured collection directory.
/// </summary>
public sealed class JsonFileStore
{
    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Uses result and collection paths from chat settings.</summary>
    public JsonFileStore(ChatSettings settings)
        : this(settings.ResolvedJsonResultDirectory, settings.ResolvedJsonCollectionDirectory)
    {
    }

    /// <summary>Uses explicit result and collection directory paths.</summary>
    public JsonFileStore(string resultDirectory, string collectionDirectory)
    {
        ResultDirectory = ChatSettings.ResolveDirectory(resultDirectory);
        CollectionDirectory = ChatSettings.ResolveDirectory(collectionDirectory);
    }

    /// <summary>Folder where HTTP responses are written.</summary>
    public string ResultDirectory { get; }

    /// <summary>Folder where saved JSON requests are stored.</summary>
    public string CollectionDirectory { get; }

    /// <summary>Saves the response as is when it is JSON, otherwise as a JSON string.</summary>
    public async Task<string> SaveResultAsync(string method, string url, string content)
    {
        var json = TryFormat(content, out var formatted)
            ? formatted
            : JsonSerializer.Serialize(content, IndentedOptions);

        return await WriteAsync(ResultDirectory, method, url, json);
    }

    /// <summary>Serializes and saves a request preset under the collection directory.</summary>
    public async Task<string> SaveQueryAsync(JsonQuery query)
    {
        var json = JsonSerializer.Serialize(query, IndentedOptions);
        return await WriteAsync(CollectionDirectory, query.Method, query.Url, json);
    }

    /// <summary>Reads a saved request from a JSON file on disk.</summary>
    public static async Task<JsonQuery> LoadQueryAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<JsonQuery>(stream, new JsonSerializerOptions
               {
                   PropertyNameCaseInsensitive = true,
                   ReadCommentHandling = JsonCommentHandling.Skip,
                   AllowTrailingCommas = true
               })
               ?? throw new InvalidDataException("The file does not contain a request.");
    }

    /// <summary>Pretty-prints JSON text when parsing succeeds.</summary>
    public static bool TryFormat(string text, out string formatted)
    {
        formatted = text;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            var node = JsonNode.Parse(text);
            formatted = node?.ToJsonString(IndentedOptions) ?? "null";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string> WriteAsync(string directory, string method, string url, string json)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{method.ToUpperInvariant()}-{Slug(url)}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private static string Slug(string url)
    {
        var text = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host + uri.AbsolutePath
            : url;

        var invalid = Path.GetInvalidFileNameChars();
        var slug = new string(text.Select(c => invalid.Contains(c) || c is '/' or '.' or ' ' ? '-' : c).ToArray())
            .Trim('-');

        return slug.Length > 60 ? slug[..60] : slug;
    }
}
