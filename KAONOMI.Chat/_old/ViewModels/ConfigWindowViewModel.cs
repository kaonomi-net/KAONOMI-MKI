using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// Editor of config.yml (<c>/config</c>, <c>/nconfig</c>): SAVE keeps a copy of the previous version as
/// <c>config-YYYYMMDD.config</c> next to it, then writes the edited text; RELOAD reads the file again. The settings are
/// read at startup, so a saved change applies the next time the chat starts, except the presets: choosing one in the
/// Presets drop-down writes its voice and animation into the text, saves the file and applies them to the running chat.
/// </summary>
public partial class ConfigWindowViewModel : ViewModelBase, INoteContextProvider
{
    /// <summary>The config.yml read by the chat, next to the executable.</summary>
    public string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "config.yml");

    /// <summary>Text being edited.</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>True when <see cref="Text"/> differs from the file.</summary>
    [ObservableProperty]
    private bool _isModified;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>Text of the file as last read or saved.</summary>
    private string _savedText = string.Empty;

    /// <summary>Called with a preset once it is saved, to apply it to the running chat.</summary>
    private readonly Action<ChatPreset>? _applyPreset;

    /// <summary>Reads config.yml.</summary>
    public ConfigWindowViewModel() : this(null)
    {
    }

    /// <summary>Reads config.yml; <paramref name="applyPreset"/> applies a chosen preset to the running chat.</summary>
    public ConfigWindowViewModel(Action<ChatPreset>? applyPreset)
    {
        _applyPreset = applyPreset;
        Reload();
    }

    /// <summary>Voice and animation presets of the drop-down.</summary>
    public IReadOnlyList<ChatPreset> Presets => ChatPreset.All;

    /// <summary>Preset last chosen in the drop-down (none until one is chosen).</summary>
    [ObservableProperty]
    private ChatPreset? _selectedPreset;

    /// <summary>Writes the chosen preset into the text, saves config.yml and applies the preset to the running chat.</summary>
    partial void OnSelectedPresetChanged(ChatPreset? value)
    {
        if (value is null)
            return;

        Text = value.ApplyTo(Text);
        if (!TrySave())
            return;

        _applyPreset?.Invoke(value);
        SetStatus($"Preset {value.Name} applied and saved: {value.Description}.", error: false);
        ActivityLog.Info("Config", $"Preset {value.Name} applied ({value.Voice}, pitch {value.Pitch}, speed {value.Speed}, robot {value.Robot}, vibrato {value.Vibrato}, assistant {value.Assistant}, horror {value.Horror}, animation {value.Animation})");
    }

    partial void OnTextChanged(string value) => IsModified = value != _savedText;

    /// <summary>Reads config.yml again (the changes not saved are dropped).</summary>
    [RelayCommand]
    private void Reload()
    {
        try
        {
            _savedText = File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
            Text = _savedText;
            IsModified = false;
            SetStatus($"Loaded {FilePath}", error: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Not loaded: {ex.Message}", error: true);
        }
    }

    /// <summary>
    /// Checks that the text is valid YAML, copies the current file to <c>config-YYYYMMDD.config</c>, then writes the
    /// edited text to config.yml.
    /// </summary>
    [RelayCommand]
    private void Save() => TrySave();

    /// <summary>Validates and saves the text (see <see cref="Save"/>); false when it is not saved.</summary>
    private bool TrySave()
    {
        try
        {
            new YamlStream().Load(new StringReader(Text));
        }
        catch (YamlException ex)
        {
            SetStatus($"Not saved, invalid YAML at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}", error: true);
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(FilePath)!;
            var backup = Path.Combine(directory, $"config-{DateTime.Now:yyyyMMdd}.config");
            if (File.Exists(FilePath))
                File.Copy(FilePath, backup, overwrite: true);

            File.WriteAllText(FilePath, Text, new UTF8Encoding(false));
            _savedText = Text;
            IsModified = false;
            SetStatus($"Saved; previous version kept as {Path.GetFileName(backup)}. Restart the chat to apply the changes.", error: false);
            ActivityLog.Info("Config", $"config.yml saved ({Text.Length} chars), previous version in {Path.GetFileName(backup)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Not saved: {ex.Message}", error: true);
            ActivityLog.Error("Config", "config.yml not saved", ex);
            return false;
        }
    }

    private void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }

    /// <inheritdoc />
    public string NoteWindowName => "Configuration";

    /// <inheritdoc />
    public object? GetNoteContext() => new { file = FilePath, modified = IsModified };
}
