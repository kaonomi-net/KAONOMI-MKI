using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.Models;

/// <summary>
/// The presets of the avatar stored in the search engine (<c>/preset</c> of the Kaonomi server), read at the start of
/// the chat and after each save or deletion (<see cref="Changed"/>). config.yml keeps only the default preset
/// (<see cref="ChatPreset.BuiltIn"/>); the first time the server has no preset, the presets the chat knows (the
/// built-in ones of the application and those of presets.json, saved from /remote before) are written to it.
/// </summary>
public static class PresetStore
{
    private static List<PresetEntry> _entries = [];
    private static List<ChatPreset> _presets = [];

    /// <summary>True once the server has answered (else the presets of the application stand in).</summary>
    public static bool Loaded { get; private set; }

    /// <summary>The documents of the presets, newest first.</summary>
    public static IReadOnlyList<PresetEntry> Entries => _entries;

    /// <summary>The presets of the server as the chat applies them (<see cref="Loaded"/>).</summary>
    public static IReadOnlyList<ChatPreset> Presets => _presets;

    /// <summary>Raised after the presets have been read again (on any thread).</summary>
    public static event Action? Changed;

    /// <summary>The document of the preset named <paramref name="name"/>, or null.</summary>
    public static PresetEntry? Find(string name) =>
        _entries.FirstOrDefault(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the presets of the server (the newest 300); when it has none, writes the presets the chat knows to it
    /// first. Nothing changes when the server does not answer (the status of the chat says so).
    /// </summary>
    public static Task<string?> LoadAsync(KaonomiAssistantService server)
    {
        // One read at a time: the chat and a window asking together share it (the seeding must happen once).
        lock (Gate)
        {
            if (_loading is { IsCompleted: false } running)
                return running;
            return _loading = LoadCoreAsync(server);
        }
    }

    private static readonly object Gate = new();
    private static Task<string?>? _loading;

    private static async Task<string?> LoadCoreAsync(KaonomiAssistantService server)
    {
        try
        {
            using var timeout = Timeout();
            var entries = await server.GetLastPresetsAsync(300, timeout.Token);
            if (entries.Count == 0)
            {
                // The first start with the search engine: the presets of the application and of presets.json go to it.
                var seeds = ChatPreset.Defaults.Concat(CustomPresetStore.Presets)
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                foreach (var seed in seeds)
                    await server.CreatePresetAsync(PresetEntry.From(seed), timeout.Token);
                ActivityLog.Info("Presets", $"{seeds.Count} preset(s) written to the search engine (first start)");
                entries = await server.GetLastPresetsAsync(300, timeout.Token);
            }
            Set(entries);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            ActivityLog.Error("Presets", "The presets of the search engine could not be read", ex);
            return ex.Message;
        }
    }

    /// <summary>Saves <paramref name="entry"/> (a new preset without id, else a new version) and reads the presets again.</summary>
    public static async Task<ServiceReturn> SaveAsync(KaonomiAssistantService server, PresetEntry entry)
    {
        entry.AppName ??= "KAONOMI.Chat";
        entry.MachineName ??= Environment.MachineName;
        entry.UserName ??= Environment.UserName;
        using var timeout = Timeout();
        var result = entry.Id is null ? await server.CreatePresetAsync(entry, timeout.Token) : await server.UpdatePresetAsync(entry, timeout.Token);
        if (result.ReturnedId is not (null or "-1"))
            Set(await server.GetLastPresetsAsync(300, timeout.Token));
        return result;
    }

    /// <summary>Marks the preset <paramref name="id"/> deleted and reads the presets again.</summary>
    public static async Task<ServiceReturn> DeleteAsync(KaonomiAssistantService server, string id)
    {
        using var timeout = Timeout();
        var result = await server.DeletePresetAsync(id, timeout.Token);
        if (result.ReturnedId is not (null or "-1"))
            Set(await server.GetLastPresetsAsync(300, timeout.Token));
        return result;
    }

    /// <summary>The calls to the server give up after 30 s (the HttpClient of the chat has no limit of its own).</summary>
    private static System.Threading.CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(30));

    private static void Set(IReadOnlyList<PresetEntry> entries)
    {
        // One preset per name (the newest), in the order of their names.
        _entries = entries.Where(e => !e.IsDeleted && !string.IsNullOrWhiteSpace(e.Name))
            .GroupBy(e => e.Name.Trim().ToUpperInvariant()).Select(g => g.OrderByDescending(e => e.Date).First())
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _presets = _entries.Select(e => e.ToChatPreset()).ToList();
        Loaded = true;
        Changed?.Invoke();
    }
}
