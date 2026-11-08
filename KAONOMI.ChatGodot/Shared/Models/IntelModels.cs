using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Document of the <c>/intel</c> APIs (<c>Intel</c> on the server side): a rule "if … then …". If: the entities that
/// must be there (each with a weight) and those that must not, the level (0 to 100 %) at which it triggers, whether
/// the match is fuzzy, and the instructions. Then: the places, the times, the related entities and the questions.
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

    /// <summary>The title of the intel.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    // ── If ──

    /// <summary>The entities that must be there, each with its weight.</summary>
    [JsonPropertyName("includedEntities")]
    public List<IntelEntityWeight>? IncludedEntities { get; set; }

    /// <summary>The entities that must not be there, each with its weight.</summary>
    [JsonPropertyName("excludedEntities")]
    public List<IntelEntityWeight>? ExcludedEntities { get; set; }

    /// <summary>The level at which the intel triggers, 0 to 100 %.</summary>
    [JsonPropertyName("triggerLevel")]
    public int TriggerLevel { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    /// <summary>True when the entities may match loosely (a typo, a close form).</summary>
    [JsonPropertyName("isFuzzy")]
    public bool IsFuzzy { get; set; }

    // ── Then ──

    [JsonPropertyName("locations")]
    public List<string>? Locations { get; set; }

    [JsonPropertyName("times")]
    public List<string>? Times { get; set; }

    [JsonPropertyName("relatedEntities")]
    public List<string>? RelatedEntities { get; set; }

    [JsonPropertyName("questions")]
    public List<string>? Questions { get; set; }

    // ── The metadata of every document ──

    /// <summary>The tags: #TOPIC, upper case, '-' for the spaces (normalised by the server on save).</summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>The events: @EVENT, upper case, '-' for the spaces.</summary>
    [JsonPropertyName("events")]
    public List<string>? Events { get; set; }

    /// <summary>The signals: !SIGNAL, upper case, '-' for the spaces.</summary>
    [JsonPropertyName("signals")]
    public List<string>? Signals { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }
}

/// <summary>An entity of an intel and its weight (<c>EntityWithWeight</c> on the server side).</summary>
public sealed class IntelEntityWeight
{
    [JsonPropertyName("entityName")]
    public string EntityName { get; set; } = string.Empty;

    /// <summary>The weight as typed (the server keeps it as text).</summary>
    [JsonPropertyName("entityWeight")]
    public string? EntityWeight { get; set; }
}
