using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>Activity log entry sent to <c>PUT /log</c> (<c>Log</c> on the server side).</summary>
public sealed class LogEntry
{
    /// <summary>Only value for a successful action or a plain event.</summary>
    public const string Info = "INFO";

    /// <summary>Only value for a failed action.</summary>
    public const string Error = "ERROR";

    [JsonPropertyName("appName")]
    public string AppName { get; set; } = "KAONOMI.Chat";

    /// <summary>The project the document is attached to (the id of a <c>Project</c>, chosen by its name in the header of the window), or null.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("machineName")]
    public string MachineName { get; set; } = Environment.MachineName;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = Environment.UserName;

    [JsonPropertyName("comment")]
    /// <summary>What happened: "Area | action: details".</summary>
    public string Comment { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    /// <summary><see cref="Info"/> or <see cref="Error"/>.</summary>
    public string Type { get; set; } = Info;
}
