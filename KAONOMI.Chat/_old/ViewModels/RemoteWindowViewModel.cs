using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// Remote control of the chatbot (<c>/remote</c>, <c>/nremote</c>): a text it says as its answer (shown, read aloud,
/// the 3D animation following the voice); the voice, its effects, the system prompt of the preset (its personality) and
/// the 3D animation, applied live to the running chat; saving them as a new preset (presets.json, and config.yml so that
/// they stay at the next start); the Piper voices: the installed ones (try, delete) and the catalog (download, try).
/// The text is also a script, saved and opened as a .txt file of <c>~/kaonomi/script</c>: Run says it paragraph by
/// paragraph, and a line <c>CHANGE:&lt;preset&gt;</c> switches to that preset (an unknown preset keeps the current one).
/// </summary>
public partial class RemoteWindowViewModel : ViewModelBase, INoteContextProvider
{
    /// <summary>Delay after the last change of a setting before it is applied to the chat (a slider moves in many steps).</summary>
    private static readonly TimeSpan ApplyDelay = TimeSpan.FromMilliseconds(300);

    private readonly MainViewModel? _chat;
    private readonly DispatcherTimer _apply = new() { Interval = ApplyDelay };

    /// <summary>True while the fields are filled from a preset: no live apply for each of them.</summary>
    private bool _loading;

    /// <summary>Voice of the last try (<see cref="TestVoice"/>), stopped by the next one.</summary>
    private SpeechService? _test;

    /// <summary>For the designer.</summary>
    public RemoteWindowViewModel() : this(null)
    {
    }

    /// <summary>Starts from the voice, effects, animation and personality in use in <paramref name="chat"/>.</summary>
    public RemoteWindowViewModel(MainViewModel? chat)
    {
        _chat = chat;
        _apply.Tick += (_, _) =>
        {
            _apply.Stop();
            ApplyToChat();
        };
        RefreshInstalled();
        Load(chat?.CurrentPreset ?? ChatPreset.BuiltIn[0]);
        PresetName = string.Empty;
        _ = LoadCatalogAsync();
    }

    // ---- Text said by the chatbot ----

    /// <summary>Text the chatbot says as its answer.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SayCommand))]
    private string _text = string.Empty;

    private bool CanSay() => _chat is not null && !string.IsNullOrWhiteSpace(Text);

    partial void OnTextChanged(string value) => RunScriptCommand.NotifyCanExecuteChanged();

    /// <summary>The chatbot says <see cref="Text"/> (shown in the chat, read aloud, the 3D animation following it).</summary>
    [RelayCommand(CanExecute = nameof(CanSay))]
    private async Task SayAsync()
    {
        ApplyNow();
        await _chat!.SayAsync(Text.Trim());
    }

    /// <summary>Stops the voice (the answer said, and the try of a voice) and the script being run.</summary>
    [RelayCommand]
    private void Stop()
    {
        _run?.Cancel();
        _chat?.StopSpeaking();
        _test?.Stop();
    }

    // ---- Scripts ----

    /// <summary>Folder of the scripts: <c>~/kaonomi/script</c>.</summary>
    public string ScriptDirectory { get; } = Path.Combine(ChatSettings.KaonomiHome, "script");

    /// <summary>The script file opened or saved last (null for a new text).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScriptName))]
    private string? _scriptPath;

    /// <summary>Name of the script file, for the title of the text.</summary>
    public string ScriptName => ScriptPath is null ? "new script" : Path.GetFileName(ScriptPath);

    /// <summary>Opens the script <paramref name="path"/> into the text.</summary>
    public void OpenScript(string path)
    {
        try
        {
            Text = File.ReadAllText(path);
            ScriptPath = path;
            SetStatus($"Script {Path.GetFileName(path)} opened.", error: false);
            ActivityLog.Info("Remote", $"Script opened: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Script not opened: {ex.Message}", error: true);
        }
    }

    /// <summary>Saves the text as the script <paramref name="path"/> (a .txt file).</summary>
    public void SaveScript(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Text, new UTF8Encoding(false));
            ScriptPath = path;
            SetStatus($"Script saved: {path}", error: false);
            ActivityLog.Info("Remote", $"Script saved: {path} ({Text.Length} chars)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Script not saved: {ex.Message}", error: true);
        }
    }

    /// <summary>The script being run (cancelled by Stop).</summary>
    private CancellationTokenSource? _run;

    /// <summary>True while the script is run.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunScriptCommand))]
    private bool _isRunning;

    /// <summary>A line that switches to a preset: <c>CHANGE:&lt;name&gt;</c>.</summary>
    [GeneratedRegex(@"^\s*CHANGE\s*:\s*(?<name>.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ChangeLine();

    /// <summary>The paragraphs of a script: blocks of lines separated by blank lines.</summary>
    [GeneratedRegex(@"\r?\n[ \t]*\r?\n")]
    private static partial Regex ParagraphBreak();

    private bool CanRunScript() => _chat is not null && !IsRunning && !string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// Says the text paragraph by paragraph, each one once the previous one has been said. A line <c>CHANGE:NAME</c>
    /// switches to the preset NAME (voice, effects, animation, personality) before the lines after it; when there is
    /// no such preset, the current one stays.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunScript))]
    private async Task RunScriptAsync()
    {
        if (_chat is null)
            return;
        ApplyNow();
        var run = _run = new CancellationTokenSource();
        IsRunning = true;
        var paragraphs = ParagraphBreak().Split(Text.Replace("\r\n", "\n")).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        ActivityLog.Info("Remote", $"Script run: {ScriptName}, {paragraphs.Count} paragraph(s)");
        try
        {
            for (var i = 0; i < paragraphs.Count; i++)
            {
                var spoken = new List<string>();
                foreach (var line in paragraphs[i].Split('\n'))
                {
                    if (ChangeLine().Match(line) is { Success: true } change)
                    {
                        await SayAndWaitAsync(spoken, i, paragraphs.Count, run.Token);
                        SwitchPreset(change.Groups["name"].Value);
                    }
                    else
                    {
                        spoken.Add(line);
                    }
                }
                await SayAndWaitAsync(spoken, i, paragraphs.Count, run.Token);
            }
            SetStatus($"Script {ScriptName} done ({paragraphs.Count} paragraph(s)).", error: false);
        }
        catch (OperationCanceledException)
        {
            SetStatus($"Script {ScriptName} stopped.", error: false);
        }
        finally
        {
            IsRunning = false;
            if (ReferenceEquals(_run, run))
                _run = null;
        }
    }

    /// <summary>Says the lines gathered (when there are some) and waits until the voice has finished; empties them.</summary>
    private async Task SayAndWaitAsync(List<string> lines, int paragraph, int count, CancellationToken cancellationToken)
    {
        var text = string.Join("\n", lines).Trim();
        lines.Clear();
        if (text.Length == 0)
            return;
        cancellationToken.ThrowIfCancellationRequested();
        SetStatus($"Script {ScriptName}: paragraph {paragraph + 1}/{count}…", error: false);
        await _chat!.SayAsync(text);
        // Piper takes a moment before the voice starts, then the voice is read sentence by sentence.
        await Task.Delay(300, cancellationToken);
        while (_chat.IsSpeakingOrPreparing)
            await Task.Delay(150, cancellationToken);
    }

    /// <summary>Switches to the preset <paramref name="name"/> (any case); an unknown name keeps the current settings.</summary>
    private void SwitchPreset(string name)
    {
        var preset = ChatPreset.All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            SetStatus($"CHANGE:{name}: no such preset, the current one stays.", error: true);
            ActivityLog.Info("Remote", $"Script: unknown preset {name}, current settings kept");
            return;
        }
        _switching = true;
        try
        {
            SelectedPreset = Presets.FirstOrDefault(p => p.Name == preset.Name);
        }
        finally
        {
            _switching = false;
        }
        Load(preset);
        _apply.Stop();
        _chat!.ApplyPreset(preset);
        ActivityLog.Info("Remote", $"Script: preset {preset.Name}");
    }

    /// <summary>True while a script selects a preset (it is applied by the script, not by the drop-down).</summary>
    private bool _switching;

    // ---- Voice, effects, system prompt, animation ----

    /// <summary>Voices installed in the voices folder.</summary>
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

    /// <summary>System prompt of the preset (its personality), added to the system prompt of the chat.</summary>
    [ObservableProperty]
    private string _persona = string.Empty;

    /// <summary>The 3D animations.</summary>
    public IReadOnlyList<SceneAnimation> Animations { get; } = Enum.GetValues<SceneAnimation>();

    [ObservableProperty]
    private SceneAnimation _animation;

    /// <summary>Any change of a setting is applied to the chat once the changes pause.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || _chat is null)
            return;
        if (e.PropertyName is nameof(Voice) or nameof(Pitch) or nameof(Speed) or nameof(Robot) or nameof(Vibrato)
            or nameof(Assistant) or nameof(Horror) or nameof(Persona) or nameof(Animation))
        {
            _apply.Stop();
            _apply.Start();
        }
    }

    /// <summary>The settings of the window as a preset.</summary>
    private ChatPreset Build(string name, string description) =>
        new(name, description, Voice ?? "", Math.Round(Pitch, 2), Math.Round(Speed, 2), Math.Round(Robot, 2), Animation,
            Math.Round(Vibrato, 2), Math.Round(Assistant, 2), Math.Round(Horror, 2), Persona.Trim());

    /// <summary>Applies a pending change now (before the chatbot speaks).</summary>
    private void ApplyNow()
    {
        if (!_apply.IsEnabled)
            return;
        _apply.Stop();
        ApplyToChat();
    }

    private void ApplyToChat()
    {
        if (_chat is null || string.IsNullOrWhiteSpace(Voice))
            return;
        _chat.ApplyPreset(Build("remote", ""));
        SetStatus($"Applied: {Voice}, pitch {Pitch:0.00}, speed {Speed:0.00}, animation {Animation.ToString().ToUpperInvariant()}.", error: false);
    }

    /// <summary>Fills the fields from <paramref name="preset"/> (no live apply for each field).</summary>
    private void Load(ChatPreset preset)
    {
        _loading = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(preset.Voice) && !InstalledVoices.Contains(preset.Voice))
                InstalledVoices.Add(preset.Voice);
            Voice = preset.Voice;
            Pitch = preset.Pitch;
            Speed = preset.Speed;
            Robot = preset.Robot;
            Vibrato = preset.Vibrato;
            Assistant = preset.Assistant;
            Horror = preset.Horror;
            Persona = preset.Persona;
            Animation = preset.Animation;
            PresetName = preset.IsCustom ? preset.Name : string.Empty;
            PresetDescription = preset.IsCustom ? preset.Description : string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- Presets ----

    /// <summary>The built-in presets, then the saved ones.</summary>
    public ObservableCollection<ChatPreset> Presets { get; } = new(ChatPreset.All);

    /// <summary>Preset chosen in the drop-down: its settings fill the window and are applied to the chat.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeletePresetCommand))]
    private ChatPreset? _selectedPreset;

    partial void OnSelectedPresetChanged(ChatPreset? value)
    {
        if (value is null || _switching)
            return;
        Load(value);
        ApplyToChat();
        SetStatus($"Preset {value.Name} loaded and applied (not saved in config.yml).", error: false);
    }

    /// <summary>Name of the preset to save.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePresetCommand))]
    private string _presetName = string.Empty;

    /// <summary>Description of the preset to save (one is written from the settings when it is empty).</summary>
    [ObservableProperty]
    private string _presetDescription = string.Empty;

    private bool CanSavePreset() => !string.IsNullOrWhiteSpace(PresetName);

    /// <summary>
    /// Saves the settings as a preset (presets.json; a saved preset of the same name is replaced) and writes them into
    /// config.yml (the previous version kept as config-YYYYMMDD.config), so that they are the ones of the next start.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        var name = PresetName.Trim().ToUpperInvariant();
        if (CustomPresetStore.IsBuiltInName(name))
        {
            SetStatus($"{name} is a built-in preset: choose another name.", error: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(Voice))
        {
            SetStatus("Choose a voice first.", error: true);
            return;
        }

        var description = string.IsNullOrWhiteSpace(PresetDescription)
            ? $"Voice {Voice}, pitch {Pitch:0.##}, speed {Speed:0.##}, robot {Robot:0.##}, vibrato {Vibrato:0.##}, assistant {Assistant:0.##}, horror {Horror:0.##}, {Animation.ToString().ToUpperInvariant()} animation"
            : PresetDescription.Trim();
        var preset = Build(name, description);
        try
        {
            CustomPresetStore.Save(preset);
            WriteConfig(preset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"Preset not saved: {ex.Message}", error: true);
            ActivityLog.Error("Remote", $"Preset {name} not saved", ex);
            return;
        }

        _chat?.ApplyPreset(preset);
        Presets.Clear();
        foreach (var item in ChatPreset.All)
            Presets.Add(item);
        _loading = true;
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == name);
        _loading = false;
        PresetName = name;
        SetStatus($"Preset {name} saved in presets.json and written into config.yml (kept at the next start).", error: false);
        ActivityLog.Info("Remote", $"Preset {name} saved: {description}");
    }

    private bool CanDeletePreset() => SelectedPreset is { IsCustom: true };

    /// <summary>Deletes the saved preset chosen in the drop-down (the built-in presets cannot be deleted).</summary>
    [RelayCommand(CanExecute = nameof(CanDeletePreset))]
    private void DeletePreset()
    {
        if (SelectedPreset is not { IsCustom: true } preset)
            return;
        try
        {
            CustomPresetStore.Delete(preset.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Preset not deleted: {ex.Message}", error: true);
            return;
        }
        Presets.Remove(preset);
        SelectedPreset = null;
        SetStatus($"Preset {preset.Name} deleted.", error: false);
        ActivityLog.Info("Remote", $"Preset {preset.Name} deleted");
    }

    /// <summary>Writes <paramref name="preset"/> into config.yml, the previous version kept as config-YYYYMMDD.config.</summary>
    private static void WriteConfig(ChatPreset preset)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.yml");
        var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        if (File.Exists(path))
            File.Copy(path, Path.Combine(AppContext.BaseDirectory, $"config-{DateTime.Now:yyyyMMdd}.config"), overwrite: true);
        File.WriteAllText(path, preset.ApplyTo(text), new UTF8Encoding(false));
    }

    // ---- Voices ----

    /// <summary>Folder of the Piper voices.</summary>
    public string VoicesDirectory => _chat?.VoicesDirectory ?? string.Empty;

    private void RefreshInstalled()
    {
        var installed = VoiceCatalog.Installed(VoicesDirectory);
        foreach (var voice in InstalledVoices.Where(v => !installed.Contains(v)).ToList())
            if (voice != Voice)
                InstalledVoices.Remove(voice);
        foreach (var voice in installed.Where(v => !InstalledVoices.Contains(v)))
            InstalledVoices.Add(voice);
        foreach (var item in Catalog)
            item.IsInstalled = installed.Contains(item.Voice.Key);
    }

    /// <summary>
    /// Says a sentence in the language of the voice <paramref name="key"/>, with the effects of the window, without
    /// changing the voice of the chat (nor moving the animation).
    /// </summary>
    [RelayCommand]
    private void TestVoice(string? key)
    {
        key ??= Voice;
        if (string.IsNullOrWhiteSpace(key) || _chat is null)
            return;
        var model = Path.Combine(VoicesDirectory, key + ".onnx");
        _chat.StopSpeaking();
        _test?.Dispose();
        _test = new SpeechService(_chat.PiperPath, model, null, Speed, Pitch, Robot, Vibrato, Assistant, Horror);
        if (!_test.IsAvailable)
        {
            SetStatus($"{key} cannot be tried: Piper ({_chat.PiperPath}) or the voice is missing.", error: true);
            return;
        }
        _test.Speak(VoiceCatalog.Sample(VoiceCatalog.LanguageOf(key)));
        SetStatus($"Trying {key}…", error: false);
    }

    /// <summary>Deletes an installed voice (not the one in use).</summary>
    [RelayCommand]
    private void DeleteVoice(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        if (key == Voice || key == _chat?.CurrentPreset.Voice)
        {
            SetStatus($"{key} is the voice in use: choose another one before deleting it.", error: true);
            return;
        }
        try
        {
            VoiceCatalog.Delete(VoicesDirectory, key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"{key} not deleted: {ex.Message}", error: true);
            return;
        }
        RefreshInstalled();
        SetStatus($"Voice {key} deleted.", error: false);
        ActivityLog.Info("Remote", $"Voice {key} deleted from {VoicesDirectory}");
    }

    /// <summary>Every voice of the catalog.</summary>
    private List<CatalogVoiceViewModel> _catalog = [];

    private IEnumerable<CatalogVoiceViewModel> Catalog => _catalog;

    /// <summary>Voices of the catalog matching <see cref="CatalogFilter"/>.</summary>
    public ObservableCollection<CatalogVoiceViewModel> CatalogVoices { get; } = [];

    /// <summary>Filter of the catalog: words found in the name, language, country or quality (e.g. "fr", "english high").</summary>
    [ObservableProperty]
    private string _catalogFilter = "fr_";

    partial void OnCatalogFilterChanged(string value) => FilterCatalog();

    [ObservableProperty]
    private bool _isCatalogLoading;

    /// <summary>Downloads the catalog of the Piper voices (the copy kept in the voices folder when offline).</summary>
    [RelayCommand]
    private async Task LoadCatalogAsync()
    {
        if (_chat is null || IsCatalogLoading)
            return;
        IsCatalogLoading = true;
        try
        {
            var voices = await VoiceCatalog.LoadAsync(VoicesDirectory, CancellationToken.None);
            var installed = VoiceCatalog.Installed(VoicesDirectory);
            _catalog = voices.Select(v => new CatalogVoiceViewModel(v) { IsInstalled = installed.Contains(v.Key) }).ToList();
            FilterCatalog();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            SetStatus($"Voice catalog not loaded: {ex.Message}", error: true);
            ActivityLog.Error("Remote", "Voice catalog not loaded", ex);
        }
        finally
        {
            IsCatalogLoading = false;
        }
    }

    private void FilterCatalog()
    {
        var words = CatalogFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CatalogVoices.Clear();
        foreach (var item in _catalog.Where(item => words.All(word =>
                     item.Voice.Key.Contains(word, StringComparison.OrdinalIgnoreCase)
                     || item.Voice.LanguageName.Contains(word, StringComparison.OrdinalIgnoreCase))))
            CatalogVoices.Add(item);
    }

    /// <summary>Downloads a voice of the catalog into the voices folder, then tries it.</summary>
    [RelayCommand]
    private async Task DownloadVoiceAsync(CatalogVoiceViewModel? item)
    {
        if (item is null || item.IsDownloading || _chat is null)
            return;
        item.IsDownloading = true;
        item.Progress = 0;
        SetStatus($"Downloading {item.Voice.Key} ({item.Voice.Size})…", error: false);
        try
        {
            await VoiceCatalog.DownloadAsync(item.Voice, VoicesDirectory, new Progress<double>(p => item.Progress = p * 100), CancellationToken.None);
            RefreshInstalled();
            SetStatus($"Voice {item.Voice.Key} downloaded into {VoicesDirectory}.", error: false);
            ActivityLog.Info("Remote", $"Voice {item.Voice.Key} downloaded ({item.Voice.Size})");
            TestVoice(item.Voice.Key);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            SetStatus($"{item.Voice.Key} not downloaded: {ex.Message}", error: true);
            ActivityLog.Error("Remote", $"Voice {item.Voice.Key} not downloaded", ex);
        }
        finally
        {
            item.IsDownloading = false;
        }
    }

    // ---- Status ----

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    private void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }

    /// <inheritdoc />
    public string NoteWindowName => "Remote";

    /// <inheritdoc />
    public object? GetNoteContext() => new { voice = Voice, pitch = Pitch, speed = Speed, animation = Animation.ToString(), preset = SelectedPreset?.Name };
}

/// <summary>A voice of the Piper catalog in <c>/remote</c>: installed or not, and its download.</summary>
public partial class CatalogVoiceViewModel(PiperVoice voice) : ObservableObject
{
    /// <summary>The voice.</summary>
    public PiperVoice Voice { get; } = voice;

    /// <summary>Speakers, quality and size, for display.</summary>
    public string Details => $"{Voice.LanguageName} · {Voice.Quality} · {(Voice.Speakers > 1 ? $"{Voice.Speakers} speakers · " : "")}{Voice.Size}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloading;

    /// <summary>Share of the model downloaded, from 0 to 100.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>True when the voice can be downloaded (not installed, not being downloaded).</summary>
    public bool CanDownload => !IsInstalled && !IsDownloading;
}
