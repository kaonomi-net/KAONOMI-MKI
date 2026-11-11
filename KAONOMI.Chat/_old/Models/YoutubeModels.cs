using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Document of the <c>/youtubevideo</c> APIs (<c>YoutubeVideo</c> on the server side).
/// </summary>
public sealed class YoutubeVideoEntry
{
    [JsonPropertyName("appName")]
    /// <summary>Application that saved the video.</summary>
    public string? AppName { get; set; }

    /// <summary>Left out of the body when null so that the server generates the id of a new document.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    /// <summary>Date the video was added (set by the server when missing).</summary>
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    /// <summary>Machine on which the video was added.</summary>
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    /// <summary>User who added the video.</summary>
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    /// <summary>Free-text note about the video.</summary>
    public string? Comment { get; set; }

    [JsonPropertyName("videoId")]
    /// <summary>YouTube id of the video (11 characters, the <c>v=</c> of its address).</summary>
    public string? VideoId { get; set; }

    [JsonPropertyName("title")]
    /// <summary>Title of the video.</summary>
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    /// <summary>Description of the video.</summary>
    public string? Description { get; set; }

    [JsonPropertyName("url")]
    /// <summary>Address of the video.</summary>
    public string? Url { get; set; }
}
