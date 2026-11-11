using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// The editor of the presets of the avatar (<c>/preset</c>, <c>/presets</c>): the presets stored in the search engine
/// (<see cref="PresetStore"/>) on the left (searched on their name, description, voice, animation and personality), the
/// one chosen on the right: its name, description, French voice, 3D animation, the effects of the voice (pitch, speed,
/// robotic effect, vibrato, assistant sheen, horror treatment) and its personality (the system prompt). NEW starts an
/// empty preset, SAVE writes it to the search engine (a new version of a stored one), DELETE marks it deleted, APPLY
/// gives its voice, effects, animation and personality to the running chat (and config.yml, for the next start), its
/// system prompt becoming the system prompt of the chat (the SYSTEM PROMPT box).
/// config.yml keeps only the default preset: it is listed as such and edited in <c>/config</c>.
/// </summary>
public partial class PresetWindowViewModel : ViewModelBase, INoteContextProvider, IUnsavedChanges, IProjectDocument
{
    /// <summary>The project of the preset edited (the id of a project, chosen by its name in the header of the window), or null; written by SAVE.</summary>
    [ObservableProperty]
    private string? _projectId;

    /// <summary>A preset is always being edited: the one chosen, or a new one.</summary>
    public bool HasProjectDocument => true;

    private readonly MainViewModel? _chat;
    private readonly KaonomiAssistantService? _server;
    /// <summary>True while the fields are filled from a preset: no "not saved" for each of them.</summary>
    private bool _loading;

    /// <summary>For the designer.</summary>
    public PresetWindowViewModel() : this(null, null)
    {
    }

    public PresetWindowViewModel(MainViewModel? chat, KaonomiAssistantService? server)
    {
        _chat = chat;
        _server = server;
        foreach (var voice in VoiceCatalog.Installed(VoicesDirectory))
            InstalledVoices.Add(voice);
        PresetStore.Changed += OnStoreChanged;
        Fill();
        if (!PresetStore.Loaded && _server is not null)
            _ = RefreshAsync();
    }

    /// <summary>Stops following the store (the window is closed).</summary>
    public void Detach() => PresetStore.Changed -= OnStoreChanged;

    private void OnStoreChanged() => Fill();

    // ---- The list ----

    /// <summary>The presets of the search engine meeting <see cref="Search"/>, by name.</summary>
    public ObservableCollection<PresetEntry> Presets { get; } = [];

    /// <summary>A few words looked for in the name, description, voice, animation and personality of the presets.</summary>
    [ObservableProperty]
    private string _search = string.Empty;

    partial void OnSearchChanged(string value) => Fill();

    /// <summary>The default preset(s) of config.yml, applied at the start of the chat (edited in /config).</summary>
    public string DefaultInfo => "Default preset of config.yml: " + string.Join(", ", ChatPreset.BuiltIn.Select(p => p.Name)) + " (edited in /config)";

    /// <summary>The preset chosen in the list (null: a new preset is being written).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private PresetEntry? _selected;

    partial void OnSelectedChanged(PresetEntry? value)
    {
        if (value is not null && !_quiet)
            Load(value);
    }

    /// <summary>True while the preset chosen is replaced by its copy read again from the server: the fields stay as they are.</summary>
    private bool _quiet;

    /// <summary>What is edited: the name of the preset chosen, or "New preset".</summary>
    public string Title => Selected is null ? "New preset" : Selected.Name;

    /// <summary>The list from the store and the search, the preset chosen kept (by its id) when it is still there.</summary>
    private void Fill()
    {
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Meets(PresetEntry e) => words.All(w =>
            e.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Description.Contains(w, StringComparison.OrdinalIgnoreCase)
            || e.Voice.Contains(w, StringComparison.OrdinalIgnoreCase) || e.Animation.Contains(w, StringComparison.OrdinalIgnoreCase)
            || e.Persona.Contains(w, StringComparison.OrdinalIgnoreCase));
        var chosenId = Selected?.Id;
        Presets.Clear();
        foreach (var entry in PresetStore.Entries.Where(Meets))
            Presets.Add(entry);
        if (chosenId is not null && Presets.FirstOrDefault(e => e.Id == chosenId) is { } again && !ReferenceEquals(again, Selected))
        {
            // The same preset read again from the server: the fields follow unless they were being changed.
            if (!IsDirty)
                Selected = again;
            else
                SetSelectedQuietly(again);
        }
    }

    private void SetSelectedQuietly(PresetEntry entry)
    {
        _quiet = true;
        try
        {
            Selected = entry;
        }
        finally
        {
            _quiet = false;
        }
    }

    /// <summary>Reads the presets of the search engine again.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_server is null)
        {
            SetStatus("No Kaonomi server: the presets cannot be read.", true);
            return;
        }
        IsBusy = true;
        try
        {
            var error = await PresetStore.LoadAsync(_server);
            SetStatus(error is null ? $"{PresetStore.Entries.Count} preset(s) in the search engine." : "The presets could not be read: " + error, error is not null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- The preset edited ----

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>The French Piper voices installed (tts.voicesDirectory), and the voice of the preset when it is not among them.</summary>
    public ObservableCollection<string> InstalledVoices { get; } = [];

    [ObservableProperty]
    private string? _voice;

    [ObservableProperty]
    private double _pitch = 1;

    [ObservableProperty]
    private double _speed = 1;

    [ObservableProperty]
    private double _robot;

    [ObservableProperty]
    private double _vibrato;

    [ObservableProperty]
    private double _assistant;

    [ObservableProperty]
    private double _horror;

    /// <summary>The 3D animations of the chat.</summary>
    public IReadOnlyList<SceneAnimation> Animations { get; } = Enum.GetValues<SceneAnimation>();

    [ObservableProperty]
    private SceneAnimation _animation = SceneAnimation.Mari;

    /// <summary>The personality: the system prompt added to every request; empty for none.</summary>
    [ObservableProperty]
    private string _persona = string.Empty;

    /// <summary>True when a field changed since the preset was read or saved.</summary>
    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>Folder of the Piper voices (<c>tts.voicesDirectory</c>).</summary>
    public string VoicesDirectory => _chat?.VoicesDirectory ?? string.Empty;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading)
            return;
        if (e.PropertyName is nameof(Name) or nameof(Description) or nameof(Voice) or nameof(Pitch) or nameof(Speed) or nameof(Robot)
            or nameof(Vibrato) or nameof(Assistant) or nameof(Horror) or nameof(Persona) or nameof(Animation) or nameof(ProjectId))
            IsDirty = true;
    }

    private void Load(PresetEntry entry)
    {
        _loading = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(entry.Voice) && !InstalledVoices.Contains(entry.Voice))
                InstalledVoices.Add(entry.Voice);
            Name = entry.Name;
            Description = entry.Description;
            Voice = entry.Voice;
            Pitch = Math.Clamp(entry.Pitch, 0.5, 2);
            Speed = Math.Clamp(entry.Speed, 0.25, 4);
            Robot = Math.Clamp(entry.Robot, 0, 1);
            Vibrato = Math.Clamp(entry.Vibrato, 0, 1);
            Assistant = Math.Clamp(entry.Assistant, 0, 1);
            Horror = Math.Clamp(entry.Horror, 0, 1);
            Animation = Enum.TryParse<SceneAnimation>(entry.Animation, ignoreCase: true, out var animation) ? animation : SceneAnimation.Mari;
            Persona = entry.Persona;
            ProjectId = entry.ProjectId;
            IsDirty = false;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The fields as a preset of the chat (for APPLY).</summary>
    private ChatPreset BuildChatPreset() =>
        new(Name.Trim().ToUpperInvariant(), Description.Trim(), Voice ?? string.Empty, Math.Round(Pitch, 2), Math.Round(Speed, 2), Math.Round(Robot, 2), Animation,
            Math.Round(Vibrato, 2), Math.Round(Assistant, 2), Math.Round(Horror, 2), Persona.Trim());

    /// <summary>An empty preset to write (the voice, effects and animation in use in the chat as a start).</summary>
    [RelayCommand]
    private void New()
    {
        Selected = null;
        var start = _chat?.CurrentPreset;
        _loading = true;
        try
        {
            Name = string.Empty;
            Description = string.Empty;
            ProjectId = null;
            if (start is not null)
            {
                if (!string.IsNullOrWhiteSpace(start.Voice) && !InstalledVoices.Contains(start.Voice))
                    InstalledVoices.Add(start.Voice);
                Voice = start.Voice;
                Pitch = start.Pitch;
                Speed = start.Speed;
                Robot = start.Robot;
                Vibrato = start.Vibrato;
                Assistant = start.Assistant;
                Horror = start.Horror;
                Animation = start.Animation;
                Persona = start.Persona;
            }
            IsDirty = false;
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(Title));
        SetStatus("New preset: give it a name, choose its voice and animation, then SAVE.", false);
    }

    /// <summary>Writes the preset to the search engine: a new document, or a new version of the one chosen.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        var name = Name.Trim().ToUpperInvariant();
        if (name.Length == 0)
        {
            SetStatus("Give the preset a name.", true);
            return;
        }
        if (string.IsNullOrWhiteSpace(Voice))
        {
            SetStatus("Choose a voice.", true);
            return;
        }
        if (CustomPresetStore.IsBuiltInName(name) && Selected is null)
        {
            SetStatus($"{name} is the default preset of config.yml: edit it in /config, or choose another name.", true);
            return;
        }
        if (_server is null)
        {
            SetStatus("No Kaonomi server: the preset cannot be saved.", true);
            return;
        }
        // Another stored preset of that name: it is the one overwritten (one preset per name).
        var entry = Selected ?? PresetStore.Find(name) ?? new PresetEntry();
        entry.Name = name;
        entry.Description = Description.Trim();
        entry.Voice = Voice!.Trim();
        entry.Pitch = Math.Round(Pitch, 2);
        entry.Speed = Math.Round(Speed, 2);
        entry.Robot = Math.Round(Robot, 2);
        entry.Vibrato = Math.Round(Vibrato, 2);
        entry.Assistant = Math.Round(Assistant, 2);
        entry.Horror = Math.Round(Horror, 2);
        entry.Animation = Animation.ToString().ToUpperInvariant();
        entry.Persona = Persona.Trim();
        entry.ProjectId = ProjectId;
        IsBusy = true;
        try
        {
            var result = await PresetStore.SaveAsync(_server, entry);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("The preset was not saved: " + (result.Message ?? "no answer"), true);
                return;
            }
            IsDirty = false;
            if (PresetStore.Entries.FirstOrDefault(e => e.Id == result.ReturnedId) is { } saved)
                SetSelectedQuietly(saved);
            SetStatus($"Preset {name} saved in the search engine ({result.ReturnedId}).", false);
            ActivityLog.Info("Presets", $"Preset {name} saved: {entry.Voice}, animation {entry.Animation}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The preset was not saved: " + ex.Message, true);
            ActivityLog.Error("Presets", $"Preset {name} not saved", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDelete() => Selected is { Id: not null };

    /// <summary>Marks the preset chosen deleted (it stays in the search engine, out of the lists).</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Selected is not { Id: { } id } entry || _server is null)
            return;
        IsBusy = true;
        try
        {
            var result = await PresetStore.DeleteAsync(_server, id);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("The preset was not deleted: " + (result.Message ?? "no answer"), true);
                return;
            }
            SetStatus($"Preset {entry.Name} deleted.", false);
            ActivityLog.Info("Presets", $"Preset {entry.Name} marked deleted ({id})");
            New();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The preset was not deleted: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Gives the voice, effects, animation and personality of the fields to the running chat, and to config.yml for the next start.</summary>
    [RelayCommand]
    private void Apply()
    {
        if (_chat is null)
        {
            SetStatus("No chat to apply the preset to.", true);
            return;
        }
        if (string.IsNullOrWhiteSpace(Voice))
        {
            SetStatus("Choose a voice.", true);
            return;
        }
        var preset = BuildChatPreset();
        _chat.ApplyPreset(preset);
        // The system prompt of the preset is sent after the one of the chat while the preset is in use.
        var promptNote = preset.Persona.Length > 0 ? ", its system prompt sent with the one of the chat added after it" : ", no system prompt of its own";
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.yml");
            if (File.Exists(path))
                File.WriteAllText(path, preset.ApplyTo(File.ReadAllText(path)), new System.Text.UTF8Encoding(false));
            SetStatus($"Applied: {preset.Voice}, pitch {preset.Pitch:0.00}, speed {preset.Speed:0.00}, animation {preset.Animation.ToString().ToUpperInvariant()}{promptNote} (kept in config.yml for the next start).", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Applied, but config.yml was not written: {ex.Message}", true);
        }
        ActivityLog.Info("Presets", $"Preset {(preset.Name.Length == 0 ? "(unnamed)" : preset.Name)} applied to the chat");
    }

    private void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }

    /// <inheritdoc />
    public string NoteWindowName => "Presets";

    /// <inheritdoc />
    public object? GetNoteContext() => new { preset = Name, voice = Voice, animation = Animation.ToString(), presetCount = Presets.Count };
}
