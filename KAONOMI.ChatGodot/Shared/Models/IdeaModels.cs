using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Document of the <c>/idea</c> APIs (<c>Idea</c> on the server side).
/// </summary>
public sealed class IdeaEntry
{
    [JsonPropertyName("appName")]
    /// <summary>Application that created the idea.</summary>
    public string? AppName { get; set; }

    /// <summary>Left out of the body when null so that the server generates the id of a new document.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    /// <summary>Creation date of the idea (set by the server when missing).</summary>
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    /// <summary>Machine on which the idea was written.</summary>
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    /// <summary>Author of the idea.</summary>
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    /// <summary>Free-text note about the idea.</summary>
    public string? Comment { get; set; }

    [JsonPropertyName("ideaTitle")]
    /// <summary>Short title of the idea.</summary>
    public string? IdeaTitle { get; set; }

    [JsonPropertyName("ideaDescription")]
    /// <summary>Full description of the idea.</summary>
    public string? IdeaDescription { get; set; }

    [JsonPropertyName("category")]
    /// <summary>Category used to group ideas.</summary>
    public string? Category { get; set; }

    [JsonPropertyName("tags")]
    /// <summary>Tags used to classify or filter ideas.</summary>
    public List<string>? Tags { get; set; }
}
