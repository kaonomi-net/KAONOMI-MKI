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

    /// <summary>One of <see cref="Categories"/> (empty: none).</summary>
    [JsonPropertyName("categories")]
    public string? Category { get; set; }

    /// <summary>The categories offered for an intel.</summary>
    public static readonly string[] Categories =
    [
        "what", "who", "when", "where", "why", "how", "with whom", "what else", "what next", "what if", "what then",
        "what now", "question", "observation", "insight", "idea", "suggestion", "recommendation", "warning", "alert",
        "signal", "action", "event", "opportunity", "threat", "risk", "challenge", "problem", "solution"
    ];

    [JsonPropertyName("triggers")]
    public List<string>? Triggers { get; set; }

    [JsonPropertyName("relatedEntities")]
    public List<string>? RelatedEntities { get; set; }

    [JsonPropertyName("excludedEntities")]
    public List<string>? ExcludedEntities { get; set; }

    [JsonPropertyName("events")]
    public List<string>? Events { get; set; }

    [JsonPropertyName("signal")]
    public string? Signal { get; set; }

    /// <summary>#TOPIC, !ALERT, ?QUESTION, &amp;LINK (normalised by the server on save).</summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("instruction")]
    public string? Instruction { get; set; }

    [JsonPropertyName("actions")]
    public List<string>? Actions { get; set; }

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
