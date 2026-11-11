using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// A link between windows of the chat, for the <c>/relation</c> APIs (<c>Relation</c> on the server side, index
/// <c>relation</c>): its category ("interdata") and its versions, oldest first, in one document. A save sends the
/// relation (its id when it is stored) with the new version only: the server adds it with the next number.
/// </summary>
public sealed class RelationEntry
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>The project the document is attached to (the id of a <c>Project</c>, chosen by its name in the header of the window), or null.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = "interdata";

    /// <summary>The versions, oldest first: the last one is the current one.</summary>
    [JsonPropertyName("versions")]
    public List<RelationVersionEntry> Versions { get; set; } = [];

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>The current version (the last one); an empty one when the relation has none.</summary>
    [JsonIgnore]
    public RelationVersionEntry Current => Versions.LastOrDefault() ?? new RelationVersionEntry();
}

/// <summary>A version of a relation: its number, date and author, its nature, title, description, probability and ends.</summary>
public sealed class RelationVersionEntry
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("nature")]
    public string? Nature { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>-100 (absolutely impossible) to 100 (absolutely certain); 0: to evaluate.</summary>
    [JsonPropertyName("probability")]
    public int Probability { get; set; }

    /// <summary>Optional date from which the link holds.</summary>
    [JsonPropertyName("dateFrom")]
    public DateTime? DateFrom { get; set; }

    /// <summary>Optional date until which the link holds.</summary>
    [JsonPropertyName("dateTo")]
    public DateTime? DateTo { get; set; }

    [JsonPropertyName("source")]
    public RelationEndEntry Source { get; set; } = new();

    /// <summary>The windows the string goes to, in its order.</summary>
    [JsonPropertyName("destinations")]
    public List<RelationEndEntry> Destinations { get; set; } = [];
}

/// <summary>One end of a relation: the window, the id of its object, what the user wrote about it, its data (JSON).</summary>
public sealed class RelationEndEntry
{
    [JsonPropertyName("windowName")]
    public string? WindowName { get; set; }

    [JsonPropertyName("objectId")]
    public string? ObjectId { get; set; }

    /// <summary>Title of the object (for an entity of the ontology its name), updated by Refresh.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("information")]
    public string? Information { get; set; }

    [JsonPropertyName("context")]
    public string? Context { get; set; }
}
