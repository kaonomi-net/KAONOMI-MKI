using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The title of an end of a relation (the object of the window it was drawn from): read again from the server for the
/// objects it stores (for an entity of the ontology its name, for an image analysis its title), else found in the data
/// of the window when the link was made.
/// </summary>
public static class RelationTitles
{
    /// <summary>Windows showing an entity of the ontology (the Entity window and the ontology tree).</summary>
    private static bool IsOntology(string? windowName) =>
        windowName is not null && (windowName.Equals("Entity", StringComparison.OrdinalIgnoreCase)
                                   || windowName.Equals("Ontology", StringComparison.OrdinalIgnoreCase));

    private static bool IsImageAnalysis(string? windowName) =>
        string.Equals(windowName, "Image analysis", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The current title of the object of an end: for an entity of the ontology its name and for an image analysis
    /// its title (else its file name), read from the server; for the other windows, or when the server cannot tell,
    /// the title found in <paramref name="context"/>. Null when there is none.
    /// </summary>
    public static async Task<string?> ResolveAsync(string? windowName, string? objectId, string? context,
        KaonomiAssistantService? server, CancellationToken cancellationToken = default)
    {
        if (server is not null && !string.IsNullOrWhiteSpace(objectId))
        {
            try
            {
                if (IsOntology(windowName) && await server.GetOntologyAsync(objectId, cancellationToken) is { } entity
                    && !string.IsNullOrWhiteSpace(entity.Name))
                    return entity.Name.Trim();
                if (IsImageAnalysis(windowName) && await server.GetImageAnalysisAsync(objectId, cancellationToken) is { } analysis
                    && (analysis.Title ?? analysis.FileName) is { Length: > 0 } title)
                    return title.Trim();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                ActivityLog.Error("Relation", $"Title of {windowName} {objectId} not read: {ex.Message}");
            }
        }
        return FromContext(context);
    }

    /// <summary>Keys of the title of an object in the data of its window, the first found wins.</summary>
    private static readonly string[] TitleKeys = ["title", "name", "fileName", "prompt", "imagePath", "url"];

    /// <summary>
    /// The title in the data of a window (<paramref name="context"/>, as saved with the link): a title (or name, file
    /// name, prompt…) at the top of its data, else in its selected object, else in another object of its data (e.g.
    /// the node of an entity); null when there is none.
    /// </summary>
    public static string? FromContext(string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
            return null;
        try
        {
            if (JsonNode.Parse(context)?["data"] is not JsonObject data)
                return null;
            string? Find(JsonObject o)
            {
                foreach (var key in TitleKeys)
                    if (o.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v
                        && v.ToString().Trim() is { Length: > 0 } text)
                        return text.Length > 80 ? text[..80] + "…" : text;
                return null;
            }
            static bool Selected(string key) => key.StartsWith("selected", StringComparison.OrdinalIgnoreCase);
            return Find(data) ?? data.Where(p => Selected(p.Key)).Concat(data.Where(p => !Selected(p.Key)))
                .Select(p => p.Value as JsonObject).OfType<JsonObject>().Select(Find).FirstOrDefault(t => t is not null);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
