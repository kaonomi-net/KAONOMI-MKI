using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>An element of a cluster (<c>ClusterMember</c> on the server side).</summary>
public sealed class ClusterMemberEntry
{
    [JsonPropertyName("windowName")]
    public string WindowName { get; set; } = string.Empty;

    [JsonPropertyName("objectId")]
    public string ObjectId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; set; }

    /// <summary>When the element starts to belong to the cluster (optional).</summary>
    [JsonPropertyName("dateFrom")]
    public DateTimeOffset? DateFrom { get; set; }

    /// <summary>When the element stops belonging to the cluster (optional).</summary>
    [JsonPropertyName("dateTo")]
    public DateTimeOffset? DateTo { get; set; }

    /// <summary>How probable the belonging is, 0 to 100 (100: sure).</summary>
    [JsonPropertyName("probability")]
    public int Probability { get; set; } = 100;
}

/// <summary>A point of the drawing of a cluster, in the coordinates of the graph.</summary>
public sealed class ClusterPointEntry
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }
}

/// <summary>Where a cluster was drawn with the pen (<see cref="ClusterEntry.Category"/>).</summary>
public static class ClusterCategories
{
    /// <summary>The graph of relations (/relation).</summary>
    public const string GraphNode = "graphnode";
    /// <summary>A diagram of /bpmnai.</summary>
    public const string Bpmnai = "bpmnai";
    /// <summary>A diagram of /infra.</summary>
    public const string Infra = "infra";
    /// <summary>A map of /mindmap.</summary>
    public const string Mindmap = "mindmap";

    /// <summary>True for a cluster of the graph of relations: graphnode, or none / "relation" (the clusters drawn before the categories).</summary>
    public static bool IsGraph(string? category) => string.IsNullOrEmpty(category) || category is GraphNode or "relation";
}

/// <summary>
/// Document of the <c>/cluster</c> APIs (<c>Cluster</c> on the server side): a group of elements of the graph of
/// relations drawn around with the pen, with a name, a description and tags. Never erased: a delete marks it deleted.
/// </summary>
public sealed class ClusterEntry
{
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

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

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Where the cluster was drawn (<see cref="ClusterCategories"/>).</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = ClusterCategories.GraphNode;

    [JsonPropertyName("members")]
    public List<ClusterMemberEntry> Members { get; set; } = [];

    /// <summary>The drawing of the pen, simplified, in the coordinates of the graph.</summary>
    [JsonPropertyName("path")]
    public List<ClusterPointEntry> Path { get; set; } = [];

    /// <summary>The title of the cluster (besides its name).</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>The tags: #TOPIC, upper case, '-' for the spaces (<see cref="Services.MetaTags"/>).</summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>The events: @EVENT.</summary>
    [JsonPropertyName("events")]
    public List<string> Events { get; set; } = [];

    /// <summary>The signals: !SIGNAL.</summary>
    [JsonPropertyName("signals")]
    public List<string> Signals { get; set; } = [];

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>When the cluster starts to hold (optional).</summary>
    [JsonPropertyName("dateFrom")]
    public DateTimeOffset? DateFrom { get; set; }

    /// <summary>When the cluster stops holding (optional).</summary>
    [JsonPropertyName("dateTo")]
    public DateTimeOffset? DateTo { get; set; }

    /// <summary>How probable the cluster is, 0 to 100 (100: sure).</summary>
    [JsonPropertyName("probability")]
    public int Probability { get; set; } = 100;
}
