using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Application activity log sent to the Kaonomi server (<c>PUT /log</c>), type INFO or ERROR only.
/// Calls never block nor throw: entries are queued and sent one by one in the background, in order;
/// an entry that cannot be sent is dropped (and only written to the debug output).
/// </summary>
public static class ActivityLog
{
    /// <summary>Longest text kept in a log comment (prompts and answers are truncated).</summary>
    private const int MaxCommentLength = 1000;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private static readonly Channel<LogEntry> Queue =
        Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    private static KaonomiAssistantService? _client;
    private static Task? _sender;

    /// <summary>Starts sending to the given server; entries logged before are kept and sent first.</summary>
    public static void Configure(KaonomiAssistantService client)
    {
        _client = client;
        _sender ??= Task.Run(SendLoopAsync);
    }

    /// <summary>Logs a successful action or a plain event.</summary>
    public static void Info(string area, string message) => Write(LogEntry.Info, area, message);

    /// <summary>Logs a failed action.</summary>
    public static void Error(string area, string message) => Write(LogEntry.Error, area, message);

    /// <summary>Logs a failed action with its exception message.</summary>
    public static void Error(string area, string message, Exception exception) =>
        Write(LogEntry.Error, area, $"{message}: {exception.GetType().Name}: {exception.Message}");

    /// <summary>Shortens user text (prompts, answers) to keep log entries small.</summary>
    public static string Excerpt(string? text, int length = 200)
    {
        var flat = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return flat.Length <= length ? flat : flat[..length] + "…";
    }

    /// <summary>Waits (at most <paramref name="timeout"/>) for queued entries to be sent, e.g. before exit.</summary>
    public static void Flush(TimeSpan timeout)
    {
        Queue.Writer.TryComplete();
        try
        {
            _sender?.Wait(timeout);
        }
        catch (AggregateException)
        {
            // Already reported by the send loop.
        }
    }

    private static void Write(string type, string area, string message)
    {
        var comment = $"{area} | {message}";
        if (comment.Length > MaxCommentLength)
            comment = comment[..MaxCommentLength] + "…";

        Debug.WriteLine($"[{type}] {comment}");
        Queue.Writer.TryWrite(new LogEntry { Type = type, Comment = comment });
    }

    private static async Task SendLoopAsync()
    {
        await foreach (var entry in Queue.Reader.ReadAllAsync())
        {
            if (_client is null)
                continue;

            try
            {
                using var timeout = new CancellationTokenSource(SendTimeout);
                await _client.SaveLogAsync(entry, timeout.Token);
            }
            catch (Exception ex)
            {
                // Never log the failure of a log: it would loop while the server is down.
                Debug.WriteLine($"Log not sent ({ex.Message}): {entry.Comment}");
            }
        }
    }
}
