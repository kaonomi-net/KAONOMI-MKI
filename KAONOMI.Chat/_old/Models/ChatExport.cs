using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>Snapshot of the conversation written by the JSON and Word exports.</summary>
public sealed class ChatExport
{
    [JsonPropertyName("exportedAt")]
    /// <summary>Time of the export.</summary>
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.Now;

    [JsonPropertyName("model")]
    /// <summary>Model selected when the export was made.</summary>
    public string? Model { get; init; }

    [JsonPropertyName("serverUrl")]
    /// <summary>Kaonomi server that produced the answers.</summary>
    public string? ServerUrl { get; init; }

    [JsonPropertyName("systemPrompt")]
    /// <summary>System prompt in use, if any.</summary>
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("messages")]
    /// <summary>Messages in conversation order.</summary>
    public required IReadOnlyList<ChatExportMessage> Messages { get; init; }
}

/// <summary>One message of an exported conversation.</summary>
public sealed class ChatExportMessage
{
    [JsonPropertyName("role")]
    /// <summary><c>user</c> or <c>assistant</c>.</summary>
    public required string Role { get; init; }

    [JsonPropertyName("author")]
    /// <summary>Display name shown in the chat.</summary>
    public required string Author { get; init; }

    [JsonPropertyName("timestamp")]
    /// <summary>Local time the message was created.</summary>
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("content")]
    /// <summary>Message text (Markdown for assistant answers).</summary>
    public required string Content { get; init; }

    [JsonPropertyName("stats")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    /// <summary>Model, token and timing stats when available.</summary>
    public string? Stats { get; init; }

    [JsonPropertyName("isError")]
    /// <summary>True when the message is an error reported in place of an answer.</summary>
    public bool IsError { get; init; }
}
