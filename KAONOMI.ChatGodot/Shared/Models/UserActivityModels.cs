using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Document of the <c>/useractivity</c> APIs (<c>UserActivities</c> on the server side, index useractivities): an activity
/// of the journal, a SAVE done in a window.
/// </summary>
public sealed class UserActivityEntry
{
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>The project the document is attached to (the id of a <c>Project</c>, chosen by its name in the header of the window), or null.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    /// <summary>Left out of the body when null so that the server generates the id of a new document.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    /// <summary>What was done: "SAVE".</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "SAVE";

    /// <summary>The name (title) of the window.</summary>
    [JsonPropertyName("windowName")]
    public string WindowName { get; set; } = string.Empty;

    /// <summary>The link of the window: its command (/intel, /mindmap…) or its key.</summary>
    [JsonPropertyName("link")]
    public string Link { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }
}
