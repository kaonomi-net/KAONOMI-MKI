using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Entry of the journal of <c>/diary</c> (<c>Diary</c> on the server side, <c>/diary</c> APIs). The text is Markdown:
/// headings, bold, italic, underline (<c>&lt;u&gt;</c>), links, code and local images with their width.
/// </summary>
public sealed class DiaryEntry
{
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>Left out of the body when null so that the server generates the id of a new entry.</summary>
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

    /// <summary>The entry, in Markdown.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>The feeling of the day.</summary>
    [JsonPropertyName("feeling")]
    public string? Feeling { get; set; }

    /// <summary>Soft delete: true once deleted (left out of the searches).</summary>
    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }
}
