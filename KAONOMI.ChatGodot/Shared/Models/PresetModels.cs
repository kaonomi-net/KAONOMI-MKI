using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Document of the <c>/preset</c> APIs (<c>Preset</c> on the server side): a preset of the avatar of the chat, its
/// voice and the effects of the voice, its 3D animation and its personality (the text added to the system prompt).
/// Stored in the search engine; config.yml keeps only the default preset. Never erased: a delete marks it deleted.
/// </summary>
public sealed class PresetEntry
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

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>The French Piper voice (a voice of tts.voicesDirectory).</summary>
    [JsonPropertyName("voice")]
    public string Voice { get; set; } = string.Empty;

    [JsonPropertyName("pitch")]
    public double Pitch { get; set; } = 1;

    [JsonPropertyName("speed")]
    public double Speed { get; set; } = 1;

    [JsonPropertyName("robot")]
    public double Robot { get; set; }

    [JsonPropertyName("vibrato")]
    public double Vibrato { get; set; }

    [JsonPropertyName("assistant")]
    public double Assistant { get; set; }

    [JsonPropertyName("horror")]
    public double Horror { get; set; }

    /// <summary>The 3D animation (MARI, JARVIS, HAL… or NONE).</summary>
    [JsonPropertyName("animation")]
    public string Animation { get; set; } = "MARI";

    /// <summary>The personality: the text added to the system prompt; empty for none.</summary>
    [JsonPropertyName("persona")]
    public string Persona { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>The preset as the chat applies it (an unknown animation gives MARI).</summary>
    public ChatPreset ToChatPreset() =>
        new(Name.Trim().ToUpperInvariant(), Description, Voice,
            Math.Clamp(Pitch, 0.5, 2), Math.Clamp(Speed, 0.25, 4), Math.Clamp(Robot, 0, 1),
            Enum.TryParse<SceneAnimation>(Animation.Trim(), ignoreCase: true, out var animation) ? animation : SceneAnimation.Mari,
            Math.Clamp(Vibrato, 0, 1), Math.Clamp(Assistant, 0, 1), Math.Clamp(Horror, 0, 1), Persona);

    /// <summary>A new document (no id) from a preset of the chat.</summary>
    public static PresetEntry From(ChatPreset preset) => new()
    {
        AppName = "KAONOMI.Chat",
        MachineName = Environment.MachineName,
        UserName = Environment.UserName,
        Name = preset.Name.Trim().ToUpperInvariant(),
        Description = preset.Description,
        Voice = preset.Voice,
        Pitch = preset.Pitch,
        Speed = preset.Speed,
        Robot = preset.Robot,
        Vibrato = preset.Vibrato,
        Assistant = preset.Assistant,
        Horror = preset.Horror,
        Animation = preset.Animation.ToString().ToUpperInvariant(),
        Persona = preset.Persona
    };
}
