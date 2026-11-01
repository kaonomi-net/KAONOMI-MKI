using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Presets saved from <c>/remote</c> before the presets went to the search engine, in <c>presets.json</c> next to the
/// executable: read once, and written to the search engine the first time it has no preset (<see cref="PresetStore"/>);
/// the saves and deletions go to the search engine now. A custom preset may not take the name of a built-in one.
/// </summary>
public static class CustomPresetStore
{
    /// <summary>
    /// presets.json, next to config.yml; another chat (the Godot one) points it at the file of this chat to share its
    /// presets.
    /// </summary>
    public static string FilePath
    {
        get => _filePath;
        set
        {
            _filePath = value;
            _presets = null;
        }
    }

    private static string _filePath = Path.Combine(AppContext.BaseDirectory, "presets.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static List<ChatPreset>? _presets;

    /// <summary>The saved presets, in the order they were first saved.</summary>
    public static IReadOnlyList<ChatPreset> Presets => _presets ??= Load();

    /// <summary>True when <paramref name="name"/> is the name of a built-in preset (it cannot be overwritten).</summary>
    public static bool IsBuiltInName(string name) =>
        ChatPreset.BuiltIn.Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds <paramref name="preset"/>, or replaces the saved preset of the same name, and writes presets.json.</summary>
    public static void Save(ChatPreset preset)
    {
        if (IsBuiltInName(preset.Name))
            throw new InvalidOperationException($"{preset.Name} is a built-in preset: choose another name.");

        var presets = Presets.ToList();
        var index = presets.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            presets[index] = preset;
        else
            presets.Add(preset);
        Write(presets);
    }

    /// <summary>Removes the saved preset named <paramref name="name"/> and writes presets.json; false when there is none.</summary>
    public static bool Delete(string name)
    {
        var presets = Presets.ToList();
        if (presets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
            return false;
        Write(presets);
        return true;
    }

    private static void Write(List<ChatPreset> presets)
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(presets, Options), new UTF8Encoding(false));
        _presets = presets;
    }

    private static List<ChatPreset> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];
            var presets = JsonSerializer.Deserialize<List<ChatPreset>>(File.ReadAllText(FilePath), Options) ?? [];
            return presets.Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Voice) && !IsBuiltInName(p.Name)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ActivityLog.Error("Presets", $"{FilePath} not read: no custom preset", ex);
            return [];
        }
    }
}
