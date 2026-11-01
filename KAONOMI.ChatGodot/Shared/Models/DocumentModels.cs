using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Document of the <c>/document</c> APIs (<c>Document</c> on the server side): a titled text kept in versions (the
/// calculator tape saved with 💾).
/// </summary>
public sealed class DocumentEntry
{
    [JsonPropertyName("appName")]
    /// <summary>Application that saved the document.</summary>
    public string? AppName { get; set; }

    /// <summary>Left out of the body when null so that the server generates the id of a new document.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    /// <summary>Date of the last save.</summary>
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    /// <summary>Machine of the last save.</summary>
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    /// <summary>User of the last save.</summary>
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    /// <summary>Free-text note about the document.</summary>
    public string? Comment { get; set; }

    [JsonPropertyName("title")]
    /// <summary>Title of the document.</summary>
    public string? Title { get; set; }

    [JsonPropertyName("author")]
    /// <summary>Author of the document.</summary>
    public string? Author { get; set; }

    [JsonPropertyName("category")]
    /// <summary>Kind of document: math, text, physic, webpage.</summary>
    public string? Category { get; set; }

    [JsonPropertyName("description")]
    /// <summary>Short description (written by the LLM when the tape is saved).</summary>
    public string? Description { get; set; }

    [JsonPropertyName("contents")]
    /// <summary>Every saved version of the text, the latest with the highest <see cref="DocumentContent.Version"/>.</summary>
    public List<DocumentContent>? Contents { get; set; }

    [JsonPropertyName("securityClassification")]
    /// <summary>Security classification (public, internal, confidential, secret…).</summary>
    public string? SecurityClassification { get; set; }

    [JsonPropertyName("isDeleted")]
    /// <summary>True when the document was deleted (soft delete).</summary>
    public bool IsDeleted { get; set; }

    /// <summary>The latest version of the text, or null when there is none.</summary>
    [JsonIgnore]
    public DocumentContent? Latest => Contents?.Where(c => c is not null).MaxBy(c => c.Version);
}

/// <summary>A version of the text of a document.</summary>
public sealed class DocumentContent
{
    [JsonPropertyName("content")]
    /// <summary>The text of this version (the tape, in its Markdown format).</summary>
    public string? Content { get; set; }

    [JsonPropertyName("version")]
    /// <summary>Number of the version: 1 for the first save, +1 at each save.</summary>
    public int Version { get; set; }
}
