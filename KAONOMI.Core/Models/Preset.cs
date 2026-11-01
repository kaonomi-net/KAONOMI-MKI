using System;
using System.Collections.Generic;

namespace KAONOMI.Core.Models;

/// <summary>
/// A preset of the avatar of the chat (<c>/preset</c>): its French Piper voice and the effects of the voice (pitch, speed,
/// robotic effect, vibrato, assistant sheen, horror treatment), its 3D animation and its personality (the text added to
/// the system prompt). Stored in the search engine (the <c>preset</c> index); config.yml keeps only the default one.
/// Never erased (<see cref="ISoftDeletable"/>); the tags follow <see cref="TagRules"/>.
/// </summary>
public class Preset : EntityBase, ISoftDeletable, ITagged
{
    /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
    public bool isDeleted { get; set; } = false;

    /// <summary>The name of the preset (upper case, as the presets of config.yml: MARI, JARVIS…).</summary>
    public string name { get; set; } = string.Empty;
    public string description { get; set; } = string.Empty;
    /// <summary>The French Piper voice (a voice of tts.voicesDirectory, e.g. fr_FR-siwis-medium).</summary>
    public string voice { get; set; } = string.Empty;
    /// <summary>The pitch of the voice, 0.5 to 2.</summary>
    public double pitch { get; set; } = 1;
    /// <summary>The speed of the voice, 0.25 to 4.</summary>
    public double speed { get; set; } = 1;
    /// <summary>The robotic effect, 0 to 1.</summary>
    public double robot { get; set; }
    /// <summary>The vibrato, 0 to 1.</summary>
    public double vibrato { get; set; }
    /// <summary>The AI assistant sheen, 0 to 1.</summary>
    public double assistant { get; set; }
    /// <summary>The horror-film treatment, 0 to 1.</summary>
    public double horror { get; set; }
    /// <summary>The 3D animation (MARI, JARVIS, HAL… or NONE, as the <c>animation</c> key of config.yml).</summary>
    public string animation { get; set; } = "MARI";
    /// <summary>The personality: the text added to the system prompt (the <c>persona</c> key of config.yml); empty for none.</summary>
    public string persona { get; set; } = string.Empty;
    /// <summary>The tags: #TOPIC, !ALERT, ?QUESTION, &amp;LINK, in upper case, no space (<see cref="TagRules"/>).</summary>
    public List<string> Tags { get; set; } = new List<string>();

    /// <summary>The name in upper case without spaces around, the effects within their ranges, the animation in upper case.</summary>
    public void Normalize()
    {
        name = (name ?? string.Empty).Trim().ToUpperInvariant();
        description = (description ?? string.Empty).Trim();
        voice = (voice ?? string.Empty).Trim();
        pitch = Math.Clamp(pitch, 0.5, 2);
        speed = Math.Clamp(speed, 0.25, 4);
        robot = Math.Clamp(robot, 0, 1);
        vibrato = Math.Clamp(vibrato, 0, 1);
        assistant = Math.Clamp(assistant, 0, 1);
        horror = Math.Clamp(horror, 0, 1);
        animation = string.IsNullOrWhiteSpace(animation) ? "MARI" : animation.Trim().ToUpperInvariant();
        persona = (persona ?? string.Empty).Trim();
    }
}
