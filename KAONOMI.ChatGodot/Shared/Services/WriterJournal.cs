using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Sends the entries of the journal of the writings (<c>/write</c>) to the search engine, one at a time and in their
/// order. An entry the server does not take (the server stopped, an error) waits in
/// <c>~/kaonomi/writer/journal-pending.jsonl</c> and goes first at the next entry: none is lost, none is sent twice.
/// </summary>
public static class WriterJournal
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The entries waiting for the server, one JSON per line.</summary>
    public static string PendingPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "kaonomi", "writer", "journal-pending.jsonl");

    /// <summary>Raised (on a thread of the pool) once an entry is in the search engine.</summary>
    public static event Action<WriterJournalEntry>? Added;

    /// <summary>Adds <paramref name="entry"/> to the journal, in the background.</summary>
    public static void Add(KaonomiAssistantService server, WriterJournalEntry entry) => _ = AddAsync(server, entry);

    /// <summary>Adds <paramref name="entry"/> to the journal (after the entries waiting): true once the server has it.</summary>
    public static async Task<bool> AddAsync(KaonomiAssistantService server, WriterJournalEntry entry)
    {
        await Gate.WaitAsync();
        try
        {
            if (!await SendPendingAsync(server) || !await SendAsync(server, entry))
            {
                Keep(entry);
                return false;
            }
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> SendAsync(KaonomiAssistantService server, WriterJournalEntry entry)
    {
        try
        {
            var result = await server.AddWriterJournalAsync(entry);
            if (result.ReturnedId is null or "-1")
            {
                ActivityLog.Error("GhostWriter", $"Journal entry {entry.Action} of {entry.WritingId} refused: {result.Message}");
                return false;
            }
            Added?.Invoke(entry);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            ActivityLog.Error("GhostWriter", $"Journal entry {entry.Action} of {entry.WritingId} kept to be sent later: {ex.Message}");
            return false;
        }
    }

    /// <summary>Sends the entries waiting, in order; false when one is still not taken (it and the next ones wait on).</summary>
    private static async Task<bool> SendPendingAsync(KaonomiAssistantService server)
    {
        if (!File.Exists(PendingPath))
            return true;
        List<string> lines;
        try
        {
            lines = File.ReadAllLines(PendingPath).Where(l => l.Trim().Length > 0).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("GhostWriter", $"The journal entries waiting in {PendingPath} could not be read: {ex.Message}");
            return false;
        }

        var sent = 0;
        foreach (var line in lines)
        {
            WriterJournalEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<WriterJournalEntry>(line);
            }
            catch (JsonException)
            {
                entry = null;
            }
            // A line that is not an entry stays in the file (set aside, never dropped).
            if (entry is not null && !await SendAsync(server, entry))
                break;
            sent++;
        }

        try
        {
            var left = lines.Skip(sent).ToList();
            var unreadable = lines.Take(sent).Where(l => !IsEntry(l)).ToList();
            if (unreadable.Count > 0)
                File.AppendAllLines(PendingPath + ".unreadable", unreadable);
            if (left.Count == 0)
                File.Delete(PendingPath);
            else
                Rewrite(left);
            return left.Count == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not rewritten: the entries sent would go again; better twice than lost.
            ActivityLog.Error("GhostWriter", $"The journal entries waiting in {PendingPath} could not be updated: {ex.Message}");
            return false;
        }
    }

    private static bool IsEntry(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<WriterJournalEntry>(line) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Rewrite(List<string> lines)
    {
        var temp = PendingPath + ".tmp";
        File.WriteAllLines(temp, lines);
        File.Move(temp, PendingPath, overwrite: true);
    }

    private static void Keep(WriterJournalEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath)!);
            File.AppendAllText(PendingPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("GhostWriter", $"Journal entry {entry.Action} of {entry.WritingId} lost: {ex.Message}");
        }
    }
}
