using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KAONOMI.Chat.Models;

/// <summary>
/// <c>CHANGE:NAME</c> in an answer of the chat (MARI, HAL, JARVIS, MUSUKO… : the name of a preset, or of a 3D
/// animation): the chat switches to that preset (its voice, its animation and its personality), as when it is chosen in
/// its picker. The last one of the answer wins; a name that matches nothing is ignored.
/// </summary>
public static partial class PresetSwitch
{
    /// <summary>CHANGE in capitals, a colon (spaces allowed around it), then the name, alone or in Markdown (**, `).</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])CHANGE\s*:\s*([\p{L}\p{N}_-]+)")]
    private static partial Regex Marker();

    /// <summary>
    /// The preset asked by the last <c>CHANGE:NAME</c> of <paramref name="content"/>: the preset of that name (case
    /// ignored), else the first one showing the 3D animation of that name; null when there is none or nothing matches.
    /// </summary>
    public static ChatPreset? Find(string? content, IReadOnlyList<ChatPreset> presets)
    {
        if (string.IsNullOrEmpty(content))
            return null;
        foreach (var match in Marker().Matches(content).Reverse())
        {
            var name = match.Groups[1].Value.Trim('-', '_');
            var preset = presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset is null && Enum.TryParse<SceneAnimation>(name, ignoreCase: true, out var animation) && animation != SceneAnimation.None
                && Enum.IsDefined(animation) && !int.TryParse(name, out _))
                preset = presets.FirstOrDefault(p => p.Animation == animation);
            if (preset is not null)
                return preset;
        }
        return null;
    }

    /// <summary>
    /// <paramref name="content"/> without its <c>CHANGE:NAME</c> (and the Markdown around them), for the voice: the
    /// switch is done, it is not read aloud.
    /// </summary>
    public static string Strip(string content) =>
        Marker().IsMatch(content) ? MarkerWithMarkup().Replace(content, "").Trim() : content;

    [GeneratedRegex(@"[*_`]*(?<![\p{L}\p{N}_])CHANGE\s*:\s*[\p{L}\p{N}_-]+[*_`]*[ \t]*")]
    private static partial Regex MarkerWithMarkup();
}
