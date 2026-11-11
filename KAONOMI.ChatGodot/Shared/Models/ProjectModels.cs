using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// A project of <c>/project</c> (<c>Project</c> on the server side, index <c>project</c>): a name, a description,
/// goals and tags.
/// </summary>
public sealed class ProjectEntry
{
    /// <summary>Application that created or owns this document.</summary>
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>Left out of the body when null so that the server generates the id of a new document.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>Document timestamp when known.</summary>
    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    /// <summary>Free-text note about this project.</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>What the project is to reach, one goal each.</summary>
    [JsonPropertyName("goals")]
    public List<string> Goals { get; set; } = [];

    /// <summary>Tags of the project (#TOPIC); left empty, the server finds the declared ones in its texts on save.</summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>The day the project starts (today by default).</summary>
    [JsonPropertyName("startDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartDate { get; set; }

    /// <summary>The day the project ends; null while it has no end.</summary>
    [JsonPropertyName("endDate")]
    public DateTimeOffset? EndDate { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }
}
