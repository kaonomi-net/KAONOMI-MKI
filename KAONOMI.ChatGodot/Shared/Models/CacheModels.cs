using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Body of <c>POST /cache/search</c> (<c>SearchCriterias</c> on the server side).
/// </summary>
public sealed class CacheSearchRequest
{
    [JsonPropertyName("size")]
    /// <summary>Maximum number of hits to return.</summary>
    public int Size { get; init; }

    [JsonPropertyName("term")]
    /// <summary>Search text matched against cached dialogs.</summary>
    public required string Term { get; init; }

    [JsonPropertyName("fuzzy")]
    /// <summary>
    /// Loose search: a dialog matches when its prompt holds at least one word of the term with a typo or as the start
    /// of a word, accents ignored. Asked when the plain search found nothing.
    /// </summary>
    public bool Fuzzy { get; init; }
}

/// <summary>
/// Cached dialog returned by <c>/cache/search</c> (<c>Cache</c> on the server side).
/// </summary>
public sealed class CacheEntry
{
    [JsonPropertyName("appName")]
    /// <summary>Application that created or owns this cache document.</summary>
    public string? AppName { get; set; }

    /// <summary>Left out of the body when null so that the server generates the id of a new document.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    /// <summary>Document timestamp when known.</summary>
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    /// <summary>Machine name recorded with the cache entry.</summary>
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    /// <summary>User name recorded with the cache entry.</summary>
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    /// <summary>Free-text note about this cached dialog.</summary>
    public string? Comment { get; set; }

    [JsonPropertyName("prompt")]
    /// <summary>User or system prompt text stored in the cache.</summary>
    public string? Prompt { get; set; }

    [JsonPropertyName("completion")]
    /// <summary>Model completion associated with the prompt.</summary>
    public string? Completion { get; set; }

    [JsonPropertyName("model")]
    /// <summary>Model identifier used for this dialog.</summary>
    public string? Model { get; set; }

    [JsonPropertyName("relatedEntities")]
    /// <summary>Entity identifiers linked to this cache row.</summary>
    public List<string>? RelatedEntities { get; set; }

    [JsonPropertyName("excludedEntities")]
    /// <summary>Entity identifiers explicitly excluded from context.</summary>
    public List<string>? ExcludedEntities { get; set; }

    [JsonPropertyName("isDeleted")]
    /// <summary>Soft-delete flag when the document should be hidden.</summary>
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Answer of <c>POST /cache/update</c> (<c>ServiceReturn</c> on the server side).
/// </summary>
public sealed class ServiceReturn
{
    [JsonPropertyName("message")]
    /// <summary>Human-readable status or error from the server.</summary>
    public string? Message { get; set; }

    /// <summary>Identifier of the saved document, or <c>"-1"</c> when the server failed.</summary>
    [JsonPropertyName("returnedId")]
    public string? ReturnedId { get; set; }

    [JsonPropertyName("success")]
    /// <summary>Server-reported success flag.</summary>
    public bool Success { get; set; } = true;

    [JsonIgnore]
    /// <summary>True when the operation succeeded and a valid id was returned.</summary>
    public bool IsSuccess => Success && ReturnedId != "-1";
}
