using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// A regex of the regex assistant saved as JSON (folder <c>regexAssistant.directory</c> in config.yml): the regex with
/// the extraction goal and the example lines it was generated from.
/// </summary>
public sealed class SavedRegex
{
    /// <summary>What the regex extracts.</summary>
    [JsonPropertyName("goal")]
    public string Goal { get; set; } = string.Empty;

    /// <summary>Example lines, one per item.</summary>
    [JsonPropertyName("examples")]
    public List<string> Examples { get; set; } = [];

    /// <summary>The .NET regular expression.</summary>
    [JsonPropertyName("regex")]
    public string Regex { get; set; } = string.Empty;

    /// <summary>When the file was saved.</summary>
    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }
}
