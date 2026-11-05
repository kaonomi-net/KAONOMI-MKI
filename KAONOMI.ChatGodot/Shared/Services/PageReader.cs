using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>A piece of information a page gives: a short title, a summary, and its kind (fact, news, event…).</summary>
public sealed record PageInformation(string Title, string Summary, string Kind);

/// <summary>What the vision model read on the pictures of a page: its language, all its texts, each piece of information.</summary>
public sealed record PageReading(string Language, string Text, IReadOnlyList<PageInformation> Informations, IReadOnlyList<string> Pictures, TimeSpan Took);

/// <summary>
/// Reads a web page from its pictures (as a browser draws it, screen by screen) with a vision model (mistral-small3.2
/// by default, <c>visionmodel</c> of config.yml): each screen on its own, first every text it shows, in its reading
/// order and as written, then each piece of information it gives (on a page listing articles: each article),
/// summarised one by one in the language of the chat; the screens are then put together. A screen the model cannot
/// read (it loops on a page of similar lines) is asked again more freely, then for its information only.
/// </summary>
public static class PageReader
{
    /// <summary>The longest one screen may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);

    /// <summary>The kinds of information.</summary>
    public static readonly string[] Kinds = ["fact", "news", "event", "figure", "announcement", "opinion", "person", "organisation", "other"];

    public static string SystemPrompt(string language, bool withText) =>
        "You read the picture of one screen of a web page. "
        + (withText
            ? "First transcribe all the texts you can read in it, in their reading order, exactly as written, in their own language (no translation), each text once; "
              + "leave out only the menus, the buttons, the cookie banners and the advertising; "
              + "put a line break (\\n) between the paragraphs, the headings and the items of the lists. Then "
            : "Do not transcribe the texts. ")
        + "List each piece of information the screen gives (a fact, a news item, an event, a figure, an announcement, an opinion, a person, an organisation…), "
        + "one by one, each with a short title and a summary of one or two sentences, written in " + language + ". "
        + "On a page that lists articles, each article is a piece of information: its headline as the title, and as the summary what its standfirst says, "
        + "with its date and its author when they are shown. "
        + "Answer with a JSON object: {\"language\": the language of the page, "
        + (withText ? "\"text\": the transcription, " : string.Empty)
        + $"\"informations\": [{{\"title\", \"summary\", \"kind\": one of {string.Join(", ", Kinds)}}}]}}.";

    private static JsonNode Schema(bool withText)
    {
        var properties = new JsonObject { ["language"] = new JsonObject { ["type"] = "string" } };
        if (withText)
            properties["text"] = new JsonObject { ["type"] = "string" };
        properties["informations"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["type"] = "string" },
                    ["summary"] = new JsonObject { ["type"] = "string" },
                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Kinds.Select(k => (JsonNode)k).ToArray()) }
                },
                ["required"] = new JsonArray("title", "summary", "kind")
            }
        };
        var required = new JsonArray("language", "informations");
        if (withText)
            required.Add("text");
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    /// <summary>
    /// Reads the page <paramref name="page"/> from its <paramref name="pictures"/> (its screens, from its top), one
    /// screen after the other; <paramref name="log"/> is told how each screen went.
    /// </summary>
    public static async Task<PageReading> ReadAsync(OllamaModelService ollama, string model, string language, ScrapedPage page,
        IReadOnlyList<string> pictures, CancellationToken cancellationToken, Action<string>? log = null)
    {
        var started = DateTime.Now;
        var texts = new List<string>();
        var informations = new List<PageInformation>();
        var languages = new List<string>();
        Exception? failure = null;
        var read = 0;
        for (var k = 0; k < pictures.Count; k++)
        {
            var image = await File.ReadAllBytesAsync(pictures[k], cancellationToken);
            var screen = await ReadScreenAsync(ollama, model, language, page, image, k + 1, pictures.Count, cancellationToken);
            if (screen.Error is not null)
            {
                failure = screen.Error;
                log?.Invoke($"   screen {k + 1}/{pictures.Count}: not read ({screen.Error.Message})");
                continue;
            }
            read++;
            if (screen.Language.Length > 0)
                languages.Add(screen.Language);
            if (screen.Text.Length > 0)
                texts.Add(screen.Text);
            informations.AddRange(screen.Informations);
            log?.Invoke($"   screen {k + 1}/{pictures.Count}: {screen.Informations.Count} piece(s) of information{(screen.Text.Length > 0 ? $", {screen.Text.Length:N0} characters" : ", information only")}");
        }
        if (read == 0)
            throw failure ?? new InvalidOperationException("No screen could be read");
        // A piece of information cut between two screens is given twice: once is kept.
        var unique = informations.GroupBy(i => Key(i.Title)).Select(g => g.First()).ToList();
        var pageLanguage = languages.GroupBy(l => l, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? string.Empty;
        return new PageReading(pageLanguage, string.Join("\n", texts), unique, pictures, DateTime.Now - started);
    }

    /// <summary>A title reduced to its letters and digits (no accents, case, punctuation, apostrophes of any kind), its start only.</summary>
    private static string Key(string title)
    {
        var letters = new string(WebScraper.Fold(title).Where(char.IsLetterOrDigit).ToArray());
        return letters.Length > 48 ? letters[..48] : letters;
    }

    private sealed record Screen(string Language, string Text, List<PageInformation> Informations, Exception? Error);

    /// <summary>One screen: asked as it should be, then more freely (it looped), then for its information only.</summary>
    private static async Task<Screen> ReadScreenAsync(OllamaModelService ollama, string model, string language, ScrapedPage page,
        byte[] image, int number, int count, CancellationToken cancellationToken)
    {
        var prompt = $"Screen {number} of {count} (from the top) of the page {page.Url}" + (page.Title.Length > 0 ? $" (title: {page.Title})" : string.Empty)
            + (page.Keywords.Count > 0 ? $". The keywords of the collection plan: {string.Join(", ", page.Keywords)}." : ".");
        Exception? last = null;
        foreach (var (withText, temperature, penalty) in new[] { (true, 0.1, 1.05), (true, 0.5, 1.15), (false, 0.3, 1.1) })
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                var json = await ollama.ChatJsonWithImagesAsync(model, SystemPrompt(language, withText), prompt, [image], Schema(withText),
                    8192, timeout.Token, temperature, withText ? 6000 : 3000, penalty);
                return Parse(json);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                last = ex is OperationCanceledException ? new TimeoutException($"no answer within {Timeout.TotalMinutes:0} minutes") : ex;
                // Ollama unreachable: no use asking again.
                if (ex is HttpRequestException { StatusCode: null })
                    break;
            }
        }
        return new Screen(string.Empty, string.Empty, [], last);
    }

    private static Screen Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        static string Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : string.Empty;
        var informations = new List<PageInformation>();
        if (root.TryGetProperty("informations", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
                if (Text(item, "title") is { Length: > 0 } title)
                    informations.Add(new PageInformation(title, Text(item, "summary"), Text(item, "kind") is { Length: > 0 } kind ? kind : "other"));
        return new Screen(Text(root, "language"), Text(root, "text"), informations, null);
    }
}
