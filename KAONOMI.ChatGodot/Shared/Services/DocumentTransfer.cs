using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// "Export all", "Import" (a file) and "Import folder" for a kind of document of the server that has no hierarchy
/// (intels, symbolic patterns). The export writes every document the server returns, one JSON file per document
/// (<c>&lt;id&gt;.json</c>), into a folder chosen by the user. The imports read a file (one document or an array, as the
/// seed files) or every <c>*.json</c> of a folder, then save each document: a document that has an id keeps it (a
/// document with the same id on the server is overwritten, so importing an export again restores it); one without an id
/// is created with a new one. The window gives the dialogs and shows the status.
/// </summary>
public sealed class DocumentTransfer<T> where T : class
{
    /// <summary>Most documents read from the server for one export.</summary>
    public const int MaxDocuments = 10000;

    /// <summary>Plural name shown in the messages ("intels", "symbolic patterns").</summary>
    public required string Plural { get; init; }

    /// <summary>Area of the activity log.</summary>
    public required string LogArea { get; init; }

    /// <summary>Every document of the server (argument: the most to return).</summary>
    public required Func<int, CancellationToken, Task<IReadOnlyList<T>>> LoadAll { get; init; }

    /// <summary>The id of a document, or null.</summary>
    public required Func<T, string?> Id { get; init; }

    /// <summary>Creates a document without id (the server gives it one).</summary>
    public required Func<T, CancellationToken, Task<ServiceReturn>> Create { get; init; }

    /// <summary>Saves a document with its id (creates it, or overwrites the one with this id).</summary>
    public required Func<T, CancellationToken, Task<ServiceReturn>> Save { get; init; }

    /// <summary>Asks to confirm (message, confirm label).</summary>
    public required Func<string, string, Task<bool>> Confirm { get; init; }

    /// <summary>Asks for a JSON file and returns its text, or null when cancelled.</summary>
    public required Func<Task<string?>> OpenTextFile { get; init; }

    /// <summary>Asks for a folder (argument: the dialog title), or null when cancelled.</summary>
    public required Func<string, Task<string?>> PickFolder { get; init; }

    /// <summary>Shows a status (text, is an error).</summary>
    public required Action<string, bool> Report { get; init; }

    /// <summary>Writes every document of the server into a folder chosen by the user, one file per document.</summary>
    public async Task ExportAllAsync(CancellationToken cancellationToken)
    {
        var folder = await PickFolder($"Export all {Plural}: choose the folder");
        if (string.IsNullOrEmpty(folder))
        {
            Done("Export cancelled", false);
            return;
        }

        var written = 0;
        try
        {
            Report($"Loading every {Plural} for the export…", false);
            var documents = await LoadAll(MaxDocuments, cancellationToken);
            var progress = new Progress<int>(count =>
            {
                written = count;
                if (count % 200 == 0)
                    Report($"Exporting {count}/{documents.Count}…", false);
            });
            written = await JsonTransfer.ExportFolderAsync(documents, Id, folder, progress, cancellationToken);
            Done($"Exported {written} {Plural} to {folder}"
                 + (documents.Count >= MaxDocuments ? $" · limit of {MaxDocuments} reached, some may be missing" : string.Empty), false);
        }
        catch (OperationCanceledException)
        {
            Done($"Export cancelled after {written} file(s)", false);
        }
        catch (Exception ex)
        {
            Done($"Export failed after {written} file(s): {ex.Message}", true);
        }
    }

    /// <summary>Imports the documents of a JSON file chosen by the user. True when something was saved.</summary>
    public async Task<bool> ImportFileAsync(CancellationToken cancellationToken)
    {
        string? text;
        try
        {
            text = await OpenTextFile();
        }
        catch (Exception ex)
        {
            Done($"Could not read the file: {ex.Message}", true);
            return false;
        }
        if (text is null)
        {
            Done("Import cancelled", false);
            return false;
        }

        List<JsonObject> documents;
        try
        {
            documents = JsonTransfer.ParseDocuments(text);
        }
        catch (JsonException ex)
        {
            Done($"Invalid JSON file: {ex.Message}", true);
            return false;
        }
        return await ImportAsync(documents, "the file", string.Empty, cancellationToken);
    }

    /// <summary>Imports the documents of every <c>*.json</c> of a folder chosen by the user. True when something was saved.</summary>
    public async Task<bool> ImportFolderAsync(CancellationToken cancellationToken)
    {
        var folder = await PickFolder($"Import {Plural}: choose the folder of an export");
        if (string.IsNullOrEmpty(folder))
        {
            Done("Import cancelled", false);
            return false;
        }

        List<JsonObject> documents;
        int badFiles;
        try
        {
            Report($"Reading {folder}…", false);
            (documents, _, badFiles) = await JsonTransfer.ReadFolderAsync(folder, cancellationToken);
        }
        catch (Exception ex)
        {
            Done($"Could not read the folder: {ex.Message}", true);
            return false;
        }
        return await ImportAsync(documents, folder, badFiles > 0 ? $" · {badFiles} unreadable file(s) skipped" : string.Empty, cancellationToken);
    }

    private async Task<bool> ImportAsync(List<JsonObject> documents, string source, string notes, CancellationToken cancellationToken)
    {
        var entries = documents.Select(JsonTransfer.ToEntry<T>).OfType<T>().ToList();
        var unfit = documents.Count - entries.Count;
        if (unfit > 0)
            notes += $" · {unfit} document(s) not readable as {Plural}";
        if (entries.Count == 0)
        {
            Done($"No {Plural} to import in {source}{notes}", true);
            return false;
        }

        var withId = entries.Count(e => !string.IsNullOrWhiteSpace(Id(e)));
        var question = $"Import {entries.Count} {Plural} from {source}?\n\n"
                       + (withId > 0 ? $"{withId} keep their id: one with the same id on the server is overwritten. " : string.Empty)
                       + (withId < entries.Count ? $"{entries.Count - withId} without id are created with a new one." : string.Empty);
        if (!await Confirm(question.TrimEnd(), "Import"))
        {
            Done("Import cancelled", false);
            return false;
        }

        var saved = 0;
        var failed = new List<string>();
        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = string.IsNullOrWhiteSpace(Id(entry))
                    ? await Create(entry, cancellationToken)
                    : await Save(entry, cancellationToken);
                if (result.IsSuccess)
                    saved++;
                else
                    failed.Add(Id(entry) ?? "(no id)");
                if ((saved + failed.Count) % 50 == 0)
                    Report($"Importing {saved + failed.Count}/{entries.Count}…", false);
            }
            Done($"Imported {saved} {Plural} from {source}"
                 + (failed.Count > 0 ? $" · {failed.Count} rejected by the server ({string.Join(", ", failed.Take(5))}{(failed.Count > 5 ? "…" : "")})" : string.Empty)
                 + notes, failed.Count > 0);
        }
        catch (OperationCanceledException)
        {
            Done($"Import cancelled after {saved} {Plural}", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            Done($"Imported {saved}/{entries.Count} {Plural}; error: {ex.Message}", true);
        }
        return saved > 0;
    }

    private void Done(string status, bool isError)
    {
        Report(status, isError);
        if (isError)
            ActivityLog.Error(LogArea, status);
        else
            ActivityLog.Info(LogArea, status);
    }
}
