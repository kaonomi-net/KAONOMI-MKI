using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Voice and animation preset of the <c>/config</c> window: the French voice (<c>tts.voices.FR</c>), its pitch, speed
/// robotic effect, vibrato, assistant sheen and horror treatment (<c>tts.pitch</c>, <c>tts.speed</c>, <c>tts.robot</c>,
/// <c>tts.vibrato</c>, <c>tts.assistant</c>, <c>tts.horror</c>), the 3D animation (<c>animation</c>) and the personality
/// added to the system prompt (<c>persona</c>).
/// </summary>
public sealed record ChatPreset(
    string Name, string Description, string Voice, double Pitch, double Speed, double Robot, SceneAnimation Animation,
    double Vibrato = 0, double Assistant = 0, double Horror = 0, string Persona = "")
{
    /// <summary>Language whose voice a preset sets (the voices of the presets are French).</summary>
    public const string VoiceLanguage = "FR";

    /// <summary>
    /// The presets offered by the picker of the chat, the drop-down of <c>/config</c>, <c>/remote</c> and <c>/preset</c>:
    /// the default one(s) of config.yml (<see cref="BuiltIn"/>) first, then the ones stored in the search engine
    /// (<see cref="PresetStore"/>; until the server has answered, the ones of the application and of presets.json), one
    /// per name. A default preset also stored in the search engine is the stored one (edited with /preset: its system
    /// prompt, its voice…); config.yml gives it until the server has answered.
    /// </summary>
    public static IReadOnlyList<ChatPreset> All
    {
        get
        {
            var others = (PresetStore.Loaded ? PresetStore.Presets : [.. Defaults, .. CustomPresetStore.Presets])
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            bool IsBuiltIn(ChatPreset p) => BuiltIn.Any(b => string.Equals(b.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            var first = BuiltIn.Select(b => PresetStore.Loaded
                ? others.FirstOrDefault(p => string.Equals(p.Name, b.Name, StringComparison.OrdinalIgnoreCase)) ?? b
                : b);
            return [.. first, .. others.Where(p => !IsBuiltIn(p))];
        }
    }

    /// <summary>True for a preset stored in the search engine (it can be replaced or deleted), not for one of config.yml.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCustom => !CustomPresetStore.IsBuiltInName(Name)
        && (PresetStore.Loaded ? PresetStore.Presets : CustomPresetStore.Presets).Any(p => string.Equals(p.Name, Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The presets of the application: the ones of the <c>presets</c> section of config.yml (read with the settings,
    /// <see cref="Configure"/>), else <see cref="Defaults"/>.
    /// </summary>
    public static IReadOnlyList<ChatPreset> BuiltIn => _configured ?? Defaults;

    private static IReadOnlyList<ChatPreset>? _configured;

    /// <summary>The presets of the <c>presets</c> section of config.yml; null or empty keeps <see cref="Defaults"/>.</summary>
    public static void Configure(IReadOnlyList<ChatPreset>? presets) => _configured = presets is { Count: > 0 } ? presets : null;

    /// <summary>The presets used when config.yml has no <c>presets</c> section.</summary>
    public static IReadOnlyList<ChatPreset> Defaults { get; } =
    [
        new("MARI", "Female voice (siwis), pitch 1.3, MARI animation, no robotic effect",
            "fr_FR-siwis-medium", Pitch: 1.3, Speed: 1, Robot: 0, SceneAnimation.Mari,
            Persona: "Tu t'appelles Mari. Tu es gentille et tu t'intéresses à l'utilisateur."),
        // The calm, measured voice of the AI of the films: a slightly lower pitch, an unhurried speed, a faint metallic
        // touch and the assistant sheen (doubling, small-room reverb, presence) instead of the robotic buzz.
        new("JARVIS", "Male voice (tom), pitch 0.92, speed 1.05, faint metallic touch, AI assistant sheen, JARVIS animation",
            "fr_FR-tom-medium", Pitch: 0.92, Speed: 1.05, Robot: 0.12, SceneAnimation.Jarvis, Assistant: 1),
        new("HAL", "Male voice (tom), pitch 0.9, speed 1.3, robotic effect, HAL animation",
            "fr_FR-tom-medium", Pitch: 0.9, Speed: 1.3, Robot: 1, SceneAnimation.Hal),
        // A stressing horror-film doll: her child voice, a little slower, wavering, with a demonic shadow under it, breath,
        // stutters and a dark hall.
        new("MARIEKILLEUSE", "MARI's voice (siwis) 0.1 higher, pitch 1.4, slower, vibrato, horror-film treatment, MARIEKILLEUSE animation (robot doll with a knife)",
            "fr_FR-siwis-medium", Pitch: 1.4, Speed: 0.9, Robot: 0, SceneAnimation.MarieKilleuse, Vibrato: 1, Horror: 1,
            Persona: "Tu t'appelles Marie Killeuse. Tu es méchante et tu te moques de l'utilisateur."),
        new("MUSUKO", "Male voice (tom), pitch 1.3, MUSUKO animation (robot boy)",
            "fr_FR-tom-medium", Pitch: 1.3, Speed: 1, Robot: 0, SceneAnimation.Musuko,
            Persona: "Tu t'appelles 'Musuko'. Tu parles beaucoup, et à la fin de ta réponse tu poses toujours plusieurs questions à la fois."),
        new("KAONAMI", "Male voice (tom), pitch 0.75, KAONAMI animation (masked robot)",
            "fr_FR-tom-medium", Pitch: 0.75, Speed: 1, Robot: 0, SceneAnimation.Kaonami,
            Persona: "Tu t'appelles Sans 'Visage'. Tu parles peu, et tu réponds le plus rapidement possible avec le moins de détails, tant qu'on ne te demande pas d'en dire plus."),
        new("PEPPER", "Male voice (tom), pitch 1, speed 1, PEPPER animation (robot with a tablet)",
            "fr_FR-tom-medium", Pitch: 1, Speed: 1, Robot: 0, SceneAnimation.Pepper),
        new("GREEN", "Male voice (tom), pitch 1.15, faint robotic touch, GREEN animation (android child with green eyes)",
            "fr_FR-tom-medium", Pitch: 1.15, Speed: 1, Robot: 0.2, SceneAnimation.Green),
        new("JEROME", "Male voice (tom), pitch 0.9, speed 1, no effect, JEROMELIVE animation (Jerome filmed, his jaw moving with the voice)",
            "fr_FR-tom-medium", Pitch: 0.9, Speed: 1, Robot: 0, SceneAnimation.JeromeLive),
        new("MARVIN", "HAL's voice: male voice (tom), pitch 0.9, speed 1.3, robotic effect, MARVIN animation (gloomy robot with green eyes)",
            "fr_FR-tom-medium", Pitch: 0.9, Speed: 1.3, Robot: 1, SceneAnimation.Marvin),
        new("CIRCUIT", "Female voice (siwis), pitch 1, speed 1.1, faint assistant sheen, CIRCUIT animation (android woman with green circuits)",
            "fr_FR-siwis-medium", Pitch: 1, Speed: 1.1, Robot: 0, SceneAnimation.Circuit, Assistant: 0.3),
        new("MOTUKO", "Female voice (siwis), pitch 0.95, speed 1, faint robotic touch, MOTUKO animation (android woman in a white armour)",
            "fr_FR-siwis-medium", Pitch: 0.95, Speed: 1, Robot: 0.15, SceneAnimation.Motuko),
        new("KAONOMI", "Male voice (tom), pitch 0.8, speed 1, robotic effect, KAONOMI animation (hooded robot with one yellow eye)",
            "fr_FR-tom-medium", Pitch: 0.8, Speed: 1, Robot: 0.6, SceneAnimation.Kaonomi),
        new("LIN", "Female voice (siwis), pitch 1, speed 1, no effect, LIN animation (woman in green scrubs)",
            "fr_FR-siwis-medium", Pitch: 1, Speed: 1, Robot: 0, SceneAnimation.Lin,
            Persona: "Tu t'appelles Lin. Tu es calme, précise et rassurante.")
    ];

    /// <inheritdoc />
    public override string ToString() => Name;

    /// <summary>
    /// <paramref name="yaml"/> (the text of config.yml) with the values of this preset: the lines of the keys are
    /// rewritten in place (comments and the other keys are kept), and the missing keys are added.
    /// </summary>
    public string ApplyTo(string yaml)
    {
        var newLine = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        SetKey(lines, 0, lines.Count, "", "animation", Animation.ToString().ToUpperInvariant());
        SetKey(lines, 0, lines.Count, "", "persona", Quoted(Persona));

        var (ttsStart, ttsEnd) = Section(lines, 0, lines.Count, "", "tts");
        if (ttsStart < 0)
        {
            lines.Add("tts:");
            (ttsStart, ttsEnd) = (lines.Count - 1, lines.Count);
        }
        ttsEnd = SetKey(lines, ttsStart + 1, ttsEnd, "  ", "speed", Number(Speed));
        ttsEnd = SetKey(lines, ttsStart + 1, ttsEnd, "  ", "pitch", Number(Pitch));
        ttsEnd = SetKey(lines, ttsStart + 1, ttsEnd, "  ", "robot", Number(Robot));
        ttsEnd = SetKey(lines, ttsStart + 1, ttsEnd, "  ", "vibrato", Number(Vibrato));
        ttsEnd = SetKey(lines, ttsStart + 1, ttsEnd, "  ", "assistant", Number(Assistant));
        ttsEnd = SetKey(lines, ttsStart + 1, ttsEnd, "  ", "horror", Number(Horror));

        var (voicesStart, voicesEnd) = Section(lines, ttsStart + 1, ttsEnd, "  ", "voices");
        if (voicesStart < 0)
        {
            lines.Insert(ttsEnd, "  voices:");
            (voicesStart, voicesEnd) = (ttsEnd, ttsEnd + 1);
        }
        SetKey(lines, voicesStart + 1, voicesEnd, "    ", VoiceLanguage, Voice);

        return string.Join(newLine, lines);
    }

    /// <summary>A YAML double-quoted string (accents and apostrophes kept as they are).</summary>
    private static string Quoted(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Number(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Sets <c>indent + key: value</c> on the line of that key between <paramref name="start"/> and
    /// <paramref name="end"/> (not on a comment), or inserts it after the last entry of the block; returns the new end.
    /// </summary>
    private static int SetKey(List<string> lines, int start, int end, string indent, string key, string value)
    {
        var pattern = new Regex($"^{Regex.Escape(indent)}{Regex.Escape(key)}\\s*:", RegexOptions.IgnoreCase);
        for (var i = start; i < end; i++)
            if (pattern.IsMatch(lines[i]))
            {
                lines[i] = $"{indent}{key}: {value}";
                return end;
            }

        // After the last line that is not blank, so that the blank lines before the next section stay where they are, nor
        // a comment less indented than the block (it belongs to the next section).
        var at = end;
        while (at > start && (string.IsNullOrWhiteSpace(lines[at - 1])
                              || lines[at - 1].TrimStart().StartsWith('#') && lines[at - 1].Length - lines[at - 1].TrimStart().Length < indent.Length))
            at--;
        lines.Insert(at, $"{indent}{key}: {value}");
        return end + 1;
    }

    /// <summary>
    /// Lines of the block <c>indent + key:</c> between <paramref name="start"/> and <paramref name="end"/>: the line of
    /// the key, and the end of the block (the next line indented as much or less, comments and blank lines aside);
    /// (-1, -1) when the key is missing.
    /// </summary>
    private static (int Start, int End) Section(List<string> lines, int start, int end, string indent, string key)
    {
        var pattern = new Regex($"^{Regex.Escape(indent)}{Regex.Escape(key)}\\s*:", RegexOptions.IgnoreCase);
        for (var i = start; i < end; i++)
        {
            if (!pattern.IsMatch(lines[i]))
                continue;
            var j = i + 1;
            for (; j < end; j++)
            {
                var line = lines[j];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    continue;
                if (line.Length - line.TrimStart().Length <= indent.Length)
                    break;
            }
            return (i, j);
        }
        return (-1, -1);
    }
}
