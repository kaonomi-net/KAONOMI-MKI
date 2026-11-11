using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Anonymous note sent to <c>PUT /whitenote</c> (<c>WhiteNote</c> on the server side). No id, date, user or machine is
/// sent: the server generates the id, sets the user and machine to "anonymous" and blurs the date.
/// </summary>
public sealed class WhiteNoteEntry
{
    [JsonPropertyName("appName")]
    public string AppName { get; set; } = "KAONOMI.Chat";

    [JsonPropertyName("text")]
    /// <summary>The note typed by the user.</summary>
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("context")]
    /// <summary>Window name and the data being worked on, as indented JSON text (identities removed).</summary>
    public string Context { get; set; } = string.Empty;

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }
}
