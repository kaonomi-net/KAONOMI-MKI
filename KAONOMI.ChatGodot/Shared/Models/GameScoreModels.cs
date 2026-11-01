using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>Document of the <c>/gamescore</c> APIs (<c>GameScore</c> on the server side): the score of a finished game.</summary>
public sealed class GameScoreEntry
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

    /// <summary>The comment of the player on the game.</summary>
    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("game")]
    public string Game { get; set; } = "game1";

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("lines")]
    public int Lines { get; set; }

    [JsonPropertyName("durationSeconds")]
    public double DurationSeconds { get; set; }

    /// <summary>"stopped" (the player gave up) or "blocked" (no move was left).</summary>
    [JsonPropertyName("end")]
    public string End { get; set; } = string.Empty;

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }
}
