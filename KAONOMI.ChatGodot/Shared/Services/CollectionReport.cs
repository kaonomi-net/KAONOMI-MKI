using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace KAONOMI.Chat.Services;

/// <summary>One prompt of a collection run and its completion (a <c>.completion.json</c> file and a report item).</summary>
public sealed class CollectionItem
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = string.Empty;
    [JsonPropertyName("promptWords")] public int PromptWords { get; set; }
    [JsonPropertyName("completion")] public string Completion { get; set; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
    [JsonPropertyName("date")] public DateTimeOffset Date { get; set; }
    [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; set; }
    [JsonPropertyName("stats")] public string? Stats { get; set; }
    [JsonPropertyName("isError")] public bool IsError { get; set; }

    /// <summary>Language of the collection (FR, EN, DE, NL) when its folder name ends with "-FR", "-EN", "-DE" or "-NL"; left out otherwise.</summary>
    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }
}

/// <summary>A prompt in the report: the prompt only, with the name of the completion file written for it.</summary>
public sealed class CollectionReportItem
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = string.Empty;
    [JsonPropertyName("promptWords")] public int PromptWords { get; set; }

    /// <summary>Name (without folder) of the <c>.completion.json</c> file holding the completion.</summary>
    [JsonPropertyName("completionFile")] public string CompletionFile { get; set; } = string.Empty;
}

/// <summary>A topic of the prompts, with the number of prompts about it.</summary>
public sealed class CollectionTopic
{
    [JsonPropertyName("topic")] public string Topic { get; set; } = string.Empty;
    [JsonPropertyName("promptCount")] public int PromptCount { get; set; }
}

/// <summary>The <c>.rapport.json</c> of a prompt collection run.</summary>
public sealed class CollectionRunReport
{
    [JsonPropertyName("collection")] public string Collection { get; set; } = string.Empty;
    [JsonPropertyName("collectionFolder")] public string CollectionFolder { get; set; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;

    /// <summary>Language of the collection (FR, EN, DE, NL) when its folder name ends with "-FR", "-EN", "-DE" or "-NL"; left out otherwise.</summary>
    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }

    [JsonPropertyName("date")] public DateTimeOffset Date { get; set; }
    [JsonPropertyName("endDate")] public DateTimeOffset EndDate { get; set; }
    [JsonPropertyName("executionTimeSeconds")] public double ExecutionTimeSeconds { get; set; }
    [JsonPropertyName("executionTime")] public string ExecutionTime { get; set; } = string.Empty;
    [JsonPropertyName("machineName")] public string MachineName { get; set; } = Environment.MachineName;
    [JsonPropertyName("userName")] public string UserName { get; set; } = Environment.UserName;
    [JsonPropertyName("promptCount")] public int PromptCount { get; set; }
    [JsonPropertyName("sentCount")] public int SentCount { get; set; }
    [JsonPropertyName("skippedCount")] public int SkippedCount { get; set; }
    [JsonPropertyName("stopped")] public bool Stopped { get; set; }
    [JsonPropertyName("totalPromptWords")] public int TotalPromptWords { get; set; }
    [JsonPropertyName("topicsSummary")] public string TopicsSummary { get; set; } = string.Empty;
    [JsonPropertyName("topics")] public List<CollectionTopic> Topics { get; set; } = [];
    /// <summary>The prompts, each with the name of its completion file (the completions are in those files).</summary>
    [JsonPropertyName("items")] public List<CollectionReportItem> Items { get; set; } = [];
}

/// <summary>
/// Outputs of a prompt collection run, written in a child folder of the collection named from the date and the model
/// (<c>20261009-143000-gpt-oss-20b</c>): one <c>NNN-name.completion.json</c> per prompt, then
/// <c>name.rapport.json</c> and the Word document built from it, <c>name.rapport.docx</c>.
/// </summary>
public static class CollectionReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Number of words of a text: the parts separated by spaces (or other blanks), empty parts excluded.</summary>
    public static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Languages recognised at the end of a collection folder name ("…-FR", "…-EN", "…-DE", "…-NL").</summary>
    private static readonly string[] Languages = ["FR", "EN", "DE", "NL"];

    /// <summary>The language of a collection folder named "…-FR", "…-EN", "…-DE" or "…-NL" (any case), in upper case; null otherwise.</summary>
    public static string? LanguageOf(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        var dash = name.LastIndexOf('-');
        if (dash < 0)
            return null;
        var suffix = name[(dash + 1)..].ToUpperInvariant();
        return Languages.Contains(suffix) ? suffix : null;
    }

    /// <summary>Child folder of a run: date and time, then the model (characters not allowed in a path replaced).</summary>
    public static string RunFolderName(DateTimeOffset start, string model) =>
        $"{start:yyyyMMdd-HHmmss}-{Safe(model)}";

    /// <summary>Child folder of the runs of <paramref name="model"/> on <paramref name="collection"/>: <c>&lt;model&gt;-&lt;collection&gt;</c> (characters not allowed in a path replaced).</summary>
    public static string RunFolderName(string model, string collection) =>
        $"{Safe(model)}-{Safe(collection)}";

    /// <summary>
    /// The name of the collection of <paramref name="folder"/>: its folder name without the language at its end
    /// ("Empathie-et-politesse-FR" gives "Empathie-et-politesse"; a folder without a language keeps its whole name).
    /// </summary>
    public static string CollectionNameOf(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return LanguageOf(folder) is null ? name : name[..name.LastIndexOf('-')];
    }

    /// <summary>Writes the completion of one prompt: <c>NNN-name.completion.json</c>.</summary>
    public static async Task<string> SaveCompletionAsync(string folder, CollectionItem item, CancellationToken cancellationToken = default)
    {
        var stem = Path.GetFileName(item.File);
        if (stem.EndsWith(PromptCollection.Extension, StringComparison.OrdinalIgnoreCase))
            stem = stem[..^PromptCollection.Extension.Length];
        // The run number is put first, unless the prompt file name already starts with it ("001-…").
        var prefix = $"{item.Index:000}-";
        var name = stem.StartsWith(prefix, StringComparison.Ordinal) ? stem : prefix + stem;
        var path = Path.Combine(folder, $"{Safe(name)}.completion.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, item, JsonOptions, cancellationToken);
        return path;
    }

    /// <summary>Writes <c>name.rapport.json</c> in the run folder and returns its path.</summary>
    public static async Task<string> WriteReportAsync(string folder, CollectionRunReport report, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(folder, $"{Safe(report.Collection)}.rapport.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken);
        return path;
    }

    /// <summary>
    /// Reads a <c>.rapport.json</c> and writes the Word document next to it (<c>name.rapport.docx</c>): summary of the
    /// run, topics, then every prompt with the name of its completion file. Returns the path of the document.
    /// </summary>
    public static string WriteWordFromReport(string reportPath)
    {
        var report = JsonSerializer.Deserialize<CollectionRunReport>(File.ReadAllText(reportPath), JsonOptions)
                     ?? throw new JsonException($"{reportPath} is empty.");
        var docxPath = reportPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? reportPath[..^".json".Length] + ".docx"
            : reportPath + ".docx";

        using var buffer = new MemoryStream();
        using (var document = WordprocessingDocument.Create(buffer, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = document.AddMainDocumentPart();
            var body = new Body();
            mainPart.Document = new Document(body);

            body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun($"Prompt collection report · {report.Collection}", bold: true, size: 36)));
            foreach (var (label, value) in new[]
                     {
                         ("Date", $"{report.Date:yyyy-MM-dd HH:mm:ss} → {report.EndDate:HH:mm:ss}"),
                         ("Execution time", report.ExecutionTime),
                         ("Model", report.Model),
                         ("Language", report.Language ?? "—"),
                         ("Machine", report.MachineName),
                         ("User", report.UserName),
                         ("Prompts", $"{report.PromptCount} in the collection, {report.SentCount} sent"
                                     + (report.SkippedCount > 0 ? $", {report.SkippedCount} skipped" : string.Empty)
                                     + (report.Stopped ? " (stopped with Esc)" : string.Empty)),
                         ("Words in the prompts", report.TotalPromptWords.ToString()),
                         ("Collection folder", report.CollectionFolder)
                     })
            {
                body.Append(ChatExportService.MakeParagraph(
                    ChatExportService.MakeRun(label + ": ", bold: true, color: "404040"),
                    ChatExportService.MakeRun(value)));
            }

            body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun("Topics", bold: true, size: 30, color: "1F5FBF")));
            if (!string.IsNullOrWhiteSpace(report.TopicsSummary))
                ChatExportService.AppendContent(body, report.TopicsSummary);
            foreach (var topic in report.Topics)
                body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun($"• {topic.Topic} ({topic.PromptCount} prompt(s))")));

            body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun("Prompts", bold: true, size: 30, color: "1F5FBF")));
            foreach (var item in report.Items)
            {
                body.Append(ChatExportService.MakeParagraph(
                    ChatExportService.MakeRun($"{item.Index}. ", bold: true, color: "1F5FBF", size: 22),
                    ChatExportService.MakeRun(item.Prompt, size: 22)));
                body.Append(ChatExportService.MakeParagraph(ChatExportService.MakeRun(
                    $"{item.PromptWords} word(s) · completion: {item.CompletionFile}",
                    italic: true, color: "808080", size: 18)));
            }

            mainPart.Document.Save();
        }

        File.WriteAllBytes(docxPath, buffer.ToArray());
        return docxPath;
    }

    /// <summary>A name usable in a path: characters not allowed in file names (and ':') replaced by '-'.</summary>
    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) || c == ':' ? '-' : c).ToArray()).Trim();
        return safe.Length == 0 ? "unknown" : safe;
    }
}
