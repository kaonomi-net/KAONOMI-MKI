using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>Request saved in the <c>jsoncollection</c> directory by the JSON client.</summary>
public sealed class JsonQuery
{
    [JsonPropertyName("method")]
    /// <summary>HTTP method (GET, POST, or PUT).</summary>
    public string Method { get; set; } = "GET";

    [JsonPropertyName("url")]
    /// <summary>Absolute request URL.</summary>
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    /// <summary>Request body for non-GET calls, often JSON.</summary>
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("savedAt")]
    /// <summary>When this preset was saved to disk.</summary>
    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>Result of one HTTP call from the JSON client window.</summary>
public sealed record JsonClientResponse(int StatusCode, string? ReasonPhrase, string Content, TimeSpan Elapsed)
{
    /// <summary>True when the status code indicates success (2xx).</summary>
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}
