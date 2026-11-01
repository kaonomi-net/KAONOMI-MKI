using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Document returned by <c>/symbolicpattern/search</c> (<c>SymbolicPattern</c> on the server side).
/// </summary>
public sealed class SymbolicPatternEntry
{
    [JsonPropertyName("appName")]
    /// <summary>Application that created or owns this pattern document.</summary>
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
    /// <summary>Machine name recorded with the pattern.</summary>
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    /// <summary>User name recorded with the pattern.</summary>
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    /// <summary>Free-text note about this symbolic pattern.</summary>
    public string? Comment { get; set; }

    [JsonPropertyName("regexPatterns")]
    /// <summary>Regular expressions matched by the symbolic engine.</summary>
    public List<string>? RegexPatterns { get; set; }

    [JsonPropertyName("symbol")]
    /// <summary>Symbol name associated with the pattern.</summary>
    public string? Symbol { get; set; }

    [JsonPropertyName("event")]
    /// <summary>Optional event identifier triggered by the pattern.</summary>
    public string? Event { get; set; }

    [JsonPropertyName("tags")]
    /// <summary>Tags used to classify or filter patterns.</summary>
    public List<string>? Tags { get; set; }

    [JsonPropertyName("isDeleted")]
    /// <summary>Soft-delete flag when the document should be hidden.</summary>
    public bool IsDeleted { get; set; }
}
