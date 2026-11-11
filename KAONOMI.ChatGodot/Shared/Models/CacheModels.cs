using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Body of the searches of the server (<c>POST …/search</c>, <c>…/last</c>; <c>SearchCriterias</c> on the server side).
/// </summary>
public sealed class CacheSearchRequest
{
    [JsonPropertyName("size")]
    /// <summary>Maximum number of hits to return.</summary>
    public int Size { get; init; }

    [JsonPropertyName("term")]
    /// <summary>Search text.</summary>
    public required string Term { get; init; }

    [JsonPropertyName("fuzzy")]
    /// <summary>
    /// Loose search: a document matches when its text holds at least one word of the term with a typo or as the start
    /// of a word, accents ignored. Asked when the plain search found nothing.
    /// </summary>
    public bool Fuzzy { get; init; }
}

/// <summary>
/// Answer of a save or a delete on the server (<c>ServiceReturn</c> on the server side).
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
