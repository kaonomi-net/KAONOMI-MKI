using System;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KAONOMI.Chat.ViewModels;

/// <summary>Implemented by the view model of every window that offers "Add a note".</summary>
public interface INoteContextProvider
{
    /// <summary>Name of the window, first item of the note context.</summary>
    string NoteWindowName { get; }

    /// <summary>The data the user is working on in the window (serialised to JSON), or null.</summary>
    object? GetNoteContext();
}

/// <summary>Builds the anonymous JSON context of a note.</summary>
public static class NoteContext
{
    /// <summary>Longest text value kept in the context (code, answers, bodies are truncated).</summary>
    private const int MaxTextLength = 4000;

    /// <summary>Fields that could identify the note's author; removed wherever they appear.</summary>
    private static readonly string[] IdentityFields = ["userName", "machineName"];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Returns <c>{ "window": …, "data": … }</c> as indented JSON text, without user or machine names and with long
    /// texts truncated.
    /// </summary>
    public static string Build(INoteContextProvider? provider, string fallbackWindowName)
    {
        var context = new JsonObject { ["window"] = provider?.NoteWindowName ?? fallbackWindowName };
        try
        {
            if (provider?.GetNoteContext() is { } data && JsonSerializer.SerializeToNode(data, Options) is { } node)
            {
                Clean(node);
                context["data"] = node;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            context["data"] = $"(context unavailable: {ex.Message})";
        }

        return context.ToJsonString(Options);
    }

    private static void Clean(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (IdentityFields.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
                        obj.Remove(name);
                    else if (obj[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > MaxTextLength)
                        obj[name] = text[..MaxTextLength] + "… (truncated)";
                    else if (obj[name] is { } child)
                        Clean(child);
                }
                break;
            case JsonArray array:
                foreach (var item in array.OfType<JsonNode>())
                    Clean(item);
                break;
        }
    }
}
