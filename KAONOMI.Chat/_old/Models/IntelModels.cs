using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Document of the <c>/intel</c> APIs (<c>Intel</c> on the server side).
/// </summary>
public sealed class IntelEntry
{
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>Left out of the body when null so that the server generates the id of a new document.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    /// <summary>Creation date of the intel (set by the server when missing).</summary>
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("relatedEntities")]
    public List<string>? RelatedEntities { get; set; }

    [JsonPropertyName("excludedEntities")]
    public List<string>? ExcludedEntities { get; set; }

    [JsonPropertyName("sentEvents")]
    public List<string>? SentEvents { get; set; }

    [JsonPropertyName("questions")]
    public List<string>? Questions { get; set; }

    [JsonPropertyName("whenStatement")]
    public string? WhenStatement { get; set; }

    [JsonPropertyName("whereStatement")]
    public string? WhereStatement { get; set; }

    [JsonPropertyName("withWhatStatement")]
    public string? WithWhatStatement { get; set; }

    [JsonPropertyName("otherInstructions")]
    public string? OtherInstructions { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Left out of the body when null (the server then sets now). A DateTime, not a DateTimeOffset: a date without
    /// offset sent by the server (<c>0001-01-01T00:00:00</c>) cannot be held by a DateTimeOffset east of UTC.
    /// </summary>
    [JsonPropertyName("startDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? EndDate { get; set; }
}
