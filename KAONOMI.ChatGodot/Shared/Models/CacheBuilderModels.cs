using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Document of <c>/cachebuilder</c> (<c>CacheBuilder</c> on the server side): several wordings of a prompt and the
/// completions that answer them, written by hand.
/// </summary>
public sealed class CacheBuilderEntry
{
    /// <summary>Application that created or owns this document.</summary>
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>The project the document is attached to (the id of a <c>Project</c>, chosen by its name in the header of the window), or null.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

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

    /// <summary>Free-text note about this document.</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Tags of the document (#TOPIC).</summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>Events of the document (@EVENT).</summary>
    [JsonPropertyName("events")]
    public List<string> Events { get; set; } = [];

    /// <summary>Signals of the document (!SIGNAL).</summary>
    [JsonPropertyName("signals")]
    public List<string> Signals { get; set; } = [];

    /// <summary>The wordings of the prompt.</summary>
    [JsonPropertyName("prompts")]
    public List<string>? Prompts { get; set; }

    /// <summary>The completions that answer the prompts.</summary>
    [JsonPropertyName("completions")]
    public List<string>? Completions { get; set; }

    /// <summary>Who wrote the completions: a model, or <c>user</c>.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>The language of the prompts and of the completions: FR, EN, DE…</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("relatedEntities")]
    public List<string>? RelatedEntities { get; set; }

    [JsonPropertyName("excludedEntities")]
    public List<string>? ExcludedEntities { get; set; }

    /// <summary>Soft-delete flag.</summary>
    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }
}
