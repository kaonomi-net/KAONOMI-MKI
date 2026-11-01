using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Ticker message returned by <c>/messageflow/last</c> (<c>MessageFlow</c> on the server side).
/// </summary>
public sealed class MessageFlowEntry
{
    [JsonPropertyName("id")]
    /// <summary>Document identifier.</summary>
    public string? Id { get; set; }

    [JsonPropertyName("date")]
    /// <summary>
    /// Message timestamp when known. A DateTime, not a DateTimeOffset: a message stored without a date comes back as
    /// 0001-01-01, which is out of DateTimeOffset range in a positive UTC offset and made the whole ticker fail.
    /// </summary>
    public DateTime? Date { get; set; }

    [JsonPropertyName("text")]
    /// <summary>Text shown in the ticker.</summary>
    public string? Text { get; set; }

    [JsonPropertyName("priority")]
    /// <summary>0 = low, 1 = medium, 2 = high.</summary>
    public int Priority { get; set; }

    [JsonPropertyName("color")]
    /// <summary>Display color: <c>red</c>, <c>green</c>, anything else is shown in white.</summary>
    public string? Color { get; set; }

    [JsonPropertyName("url")]
    /// <summary>Optional link for more information.</summary>
    public string? Url { get; set; }
}
