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
/// the 3D animation, applied live to the running chat; saving them as a new preset (the search engine, /preset, and
/// config.yml so that they stay at the next start); the Piper voices: the installed ones (try, delete) and the catalog (download, try).
/// The text is also a script, saved and opened as a .txt file of <c>~/kaonomi/script</c>: Run says it paragraph by
/// paragraph, a line <c>CHANGE:&lt;preset&gt;</c> switches to that preset (an unknown preset keeps the current one) and a
/// line <c>FULLSCREEN:ON</c> / <c>FULLSCREEN:OFF</c> puts the chat in full screen or back, and a line
/// <c>IMAGE:&lt;file&gt;</c> shows that picture over the animation for 20 seconds.
/// </summary>
public partial class RemoteWindowViewModel : ViewModelBase, INoteContextProvider, IDisposable
{
    /// <summary>The window closed: the script being run, the live apply and the voice being tried stop (the chat goes on as it is).</summary>
    public void Dispose()
    {
        _run?.Cancel();
        _apply.Stop();
        _test?.Stop();
        _test = null;
    }

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

    /// <summary>When checked, Run script waits 5 seconds (a countdown in the status) before the script starts.</summary>
    [ObservableProperty]
    private bool _delayStart;

    /// <summary>The wait of <see cref="DelayStart"/>, in seconds.</summary>
    public const int StartDelaySeconds = 5;

    /// <summary>A line that switches to a preset: <c>CHANGE:&lt;name&gt;</c>.</summary>
    [GeneratedRegex(@"^\s*CHANGE\s*:\s*(?<name>.*?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ChangeLine();

    /// <summary>A line that puts the chat in full screen or back: <c>FULLSCREEN:ON</c> / <c>FULLSCREEN:OFF</c>.</summary>
    [GeneratedRegex(@"^\s*FULLSCREEN\s*:\s*(?<state>ON|OFF)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FullScreenLine();

    /// <summary>A line that shows a picture over the animation: <c>IMAGE:&lt;file&gt;</c>.</summary>
    [GeneratedRegex(@"^\s*IMAGE\s*:\s*(?<file>.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageLine();

    /// <summary>The paragraphs of a script: blocks of lines separated by blank lines.</summary>
    [GeneratedRegex(@"\r?\n[ \t]*\r?\n")]
    private static partial Regex ParagraphBreak();

    private bool CanRunScript() => _chat is not null && !IsRunning && !string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// Says the text paragraph by paragraph, each one once the previous one has been said. A line <c>CHANGE:NAME</c>
    /// switches to the preset NAME (voice, effects, animation, personality) before the lines after it; when there is
    /// no such preset, the current one stays. A line <c>FULLSCREEN:ON</c> puts the chat in full screen (its voice mode),
    /// <c>FULLSCREEN:OFF</c> brings it back to its usual mode.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunScript))]
    private async Task RunScriptAsync()
    {
        if (_chat is null)
            return;
        ApplyNow();
        var run = _run = new CancellationTokenSource();
        _scriptProblems.Clear();
        IsRunning = true;
        var paragraphs = ParagraphBreak().Split(Text.Replace("\r\n", "\n")).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        ActivityLog.Info("Remote", $"Script run: {ScriptName}, {paragraphs.Count} paragraph(s)");
        // True when the last thing done was saying a text (an instruction after it waits for the end of the reading).
        var afterSpeech = false;
        try
        {
            // The delay before the presentation starts (time to get in place): a countdown in the status, Stop ends it.
            if (DelayStart)
                for (var left = StartDelaySeconds; left > 0; left--)
                {
                    SetStatus($"Script {ScriptName} starts in {left} s…", error: false);
                    await Task.Delay(1000, run.Token);
                }
            for (var i = 0; i < paragraphs.Count; i++)
            {
                var spoken = new List<string>();
                foreach (var line in paragraphs[i].Split('\n'))
                {
                    if (ChangeLine().Match(line) is { Success: true } change)
                    {
                        await SayAndWaitAsync(spoken, i, paragraphs.Count, run.Token);
                        SwitchPreset(change.Groups["name"].Value);
                        afterSpeech = false;
                    }
                    else if (ImageLine().Match(line) is { Success: true } image)
                    {
                        // The picture comes once the text before it has been read to the end, after a short pause.
                        if (await SayAndWaitAsync(spoken, i, paragraphs.Count, run.Token) || afterSpeech)
                            await Task.Delay(PauseBeforeImage, run.Token);
                        ShowImage(image.Groups["file"].Value);
                        afterSpeech = false;
                    }
                    else if (FullScreenLine().Match(line) is { Success: true } fullScreen)
                    {
                        await SayAndWaitAsync(spoken, i, paragraphs.Count, run.Token);
                        // A picture shown keeps the full screen (or the chat) until it has gone and the animation is back.
                        await WaitForImageAsync(run.Token);
                        SetFullScreen(fullScreen.Groups["state"].Value.Equals("ON", StringComparison.OrdinalIgnoreCase));
                        afterSpeech = false;
                    }
                    else
                    {
                        spoken.Add(line);
                    }
                }
                afterSpeech |= await SayAndWaitAsync(spoken, i, paragraphs.Count, run.Token);
            }
            var problems = _scriptProblems.ToList();
            SetStatus($"Script {ScriptName} done ({paragraphs.Count} paragraph(s))."
                      + (problems.Count == 0 ? string.Empty : $" {problems.Count} problem(s): " + string.Join(" · ", problems)), error: problems.Count > 0);
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

    /// <summary>The longest wait for a picture of <c>IMAGE:</c> to end (it lasts about 25 s).</summary>
    private static readonly TimeSpan ImageWaitLimit = TimeSpan.FromSeconds(40);

    /// <summary>Waits while a picture of <c>IMAGE:</c> is shown (until the animation is back at its place).</summary>
    private async Task WaitForImageAsync(CancellationToken cancellationToken)
    {
        if (_chat is not { IsShowingImage: true })
            return;
        SetStatus($"Script {ScriptName}: waiting for the picture to end…", error: false);
        var limit = DateTime.UtcNow + ImageWaitLimit;
        while (_chat.IsShowingImage && DateTime.UtcNow < limit)
            await Task.Delay(200, cancellationToken);
    }

    /// <summary>Pause between the end of the voice and a picture of <c>IMAGE:</c>, so that the reading is clearly over.</summary>
    private static readonly TimeSpan PauseBeforeImage = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Says the lines gathered (when there are some) and waits until the voice has finished (the chatbot is silent for
    /// a moment: no answer read, no spoken summary prepared); empties them. False when there was nothing to say.
    /// </summary>
    private async Task<bool> SayAndWaitAsync(List<string> lines, int paragraph, int count, CancellationToken cancellationToken)
    {
        var text = string.Join("\n", lines).Trim();
        lines.Clear();
        if (text.Length == 0)
            return false;
        cancellationToken.ThrowIfCancellationRequested();
        SetStatus($"Script {ScriptName}: paragraph {paragraph + 1}/{count}…", error: false);
        await _chat!.SayAsync(text);
        // Piper takes a moment before the voice starts, then the voice is read sentence by sentence.
        await Task.Delay(300, cancellationToken);
        // Silent for 3 checks in a row (450 ms): a gap between two sentences of the voice does not end the wait.
        for (var silent = 0; silent < 3;)
        {
            silent = _chat.IsSpeakingOrPreparing ? 0 : silent + 1;
            await Task.Delay(150, cancellationToken);
        }
        return true;
    }

    /// <summary>
    /// The picture of a line <c>IMAGE:&lt;file&gt;</c>: a full path, a path from the home folder (<c>~/…</c>), else a
    /// file of <c>Pictures/kaonomi/presentation</c> (where the pictures of the presentations are kept), of the folder of
    /// the scripts (<c>~/kaonomi/script</c>), of <c>~/kaonomi</c>, of <c>Pictures/kaonomi</c> or of Pictures; null when
    /// it is in none of them. Quotes around the name are ignored.
    /// </summary>
    public string? ResolveImage(string file)
    {
        var name = file.Trim().Trim('"', '\'');
        if (name.Length == 0)
            return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (name.StartsWith('~'))
            name = Path.Combine(home, name.TrimStart('~', '/', '\\'));
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? name : null;
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return new[] { Path.Combine(pictures, "kaonomi", "presentation"), ScriptDirectory, ChatSettings.KaonomiHome, Path.Combine(pictures, "kaonomi"), pictures }
            .Select(folder => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Shows the picture of a line <c>IMAGE:&lt;file&gt;</c> over the animation of the chatbot (the script goes on).</summary>
    private void ShowImage(string file)
    {
        if (ResolveImage(file) is not { } path)
        {
            SetStatus($"IMAGE:{file}: no such picture (a full path, ~/…, or a file of Pictures/kaonomi/presentation, {ScriptDirectory}, ~/kaonomi, Pictures/kaonomi or Pictures).", error: true);
            return;
        }
        var handled = _chat!.RequestImageShow(path);
        SetStatus(handled ? $"IMAGE:{Path.GetFileName(path)} shown for 20 s." : $"IMAGE:{file}: this chat cannot show pictures over its animation.", error: !handled);
        ActivityLog.Info("Remote", $"Script {ScriptName}: IMAGE:{path}{(handled ? string.Empty : " (not handled)")}");
    }

    /// <summary>Puts the chat in full screen (the voice mode) or back to its usual mode, for a line <c>FULLSCREEN:ON|OFF</c>.</summary>
    private void SetFullScreen(bool on)
    {
        var handled = _chat!.RequestFullScreen(on);
        SetStatus(handled
            ? $"FULLSCREEN:{(on ? "ON" : "OFF")}: the chat {(on ? "in full screen" : "back to its usual mode")}."
            : $"FULLSCREEN:{(on ? "ON" : "OFF")}: this chat has no full screen to switch to.", error: !handled);
        ActivityLog.Info("Remote", $"Script {ScriptName}: FULLSCREEN:{(on ? "ON" : "OFF")}{(handled ? string.Empty : " (not handled)")}");
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
    private async Task SavePresetAsync()
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
        if (KaonomiAssistantService.Shared is not { } server)
        {
            SetStatus("No Kaonomi server: the preset cannot be saved.", error: true);
            return;
        }
        try
        {
            // The search engine keeps the preset (a saved preset of the same name gets a new version), config.yml the settings in use.
            var entry = PresetStore.Find(name) ?? PresetEntry.From(preset);
            var fresh = PresetEntry.From(preset);
            (entry.Description, entry.Voice, entry.Pitch, entry.Speed, entry.Robot, entry.Vibrato, entry.Assistant, entry.Horror, entry.Animation, entry.Persona) =
                (fresh.Description, fresh.Voice, fresh.Pitch, fresh.Speed, fresh.Robot, fresh.Vibrato, fresh.Assistant, fresh.Horror, fresh.Animation, fresh.Persona);
            var result = await PresetStore.SaveAsync(server, entry);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus($"Preset not saved: {result.Message}", error: true);
                return;
            }
            WriteConfig(preset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or TaskCanceledException)
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
        SetStatus($"Preset {name} saved in the search engine and written into config.yml (kept at the next start).", error: false);
        ActivityLog.Info("Remote", $"Preset {name} saved: {description}");
    }

    private bool CanDeletePreset() => SelectedPreset is { IsCustom: true };

    /// <summary>Deletes the saved preset chosen in the drop-down (the built-in presets cannot be deleted).</summary>
    [RelayCommand(CanExecute = nameof(CanDeletePreset))]
    private async Task DeletePresetAsync()
    {
        if (SelectedPreset is not { IsCustom: true } preset)
            return;
        if (KaonomiAssistantService.Shared is not { } server || PresetStore.Find(preset.Name) is not { Id: { } id })
        {
            SetStatus("This preset is not in the search engine: it cannot be deleted here.", error: true);
            return;
        }
        try
        {
            var result = await PresetStore.DeleteAsync(server, id);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus($"Preset not deleted: {result.Message}", error: true);
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
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
        // The problems of a script stay in its last status (an unknown preset or picture…).
        if (error && IsRunning)
            _scriptProblems.Add(text);
    }

    /// <summary>The problems met by the script running (shown with its last status).</summary>
    private readonly List<string> _scriptProblems = [];

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
