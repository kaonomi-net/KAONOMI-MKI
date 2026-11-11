using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.Themes;

namespace KAONOMI.Chat.ViewModels;

/// <summary>Main chat screen: model list, conversation, and slash commands for tool windows.</summary>
public partial class MainViewModel : ViewModelBase, INoteContextProvider
{
    private readonly OllamaModelService _ollama;
    private readonly KaonomiAssistantService _assistant;
    private readonly ChatSettings _settings;
    /// <summary>Voice reading the answers; replaced when a preset changes the voice (see <see cref="ApplyPreset"/>).</summary>
    private SpeechService _speech;

    /// <summary>Creates a view model with default settings.</summary>
    public MainViewModel() : this(new ChatSettings())
    {
    }

    /// <summary>Creates a view model wired to the given configuration and server URLs.</summary>
    public MainViewModel(ChatSettings settings)
    {
        _settings = settings;
        _voiceLanguage = settings.SpeechLanguage;
        _ollama = new OllamaModelService(settings.OllamaUrl, settings.LlmProvider);
        _assistant = new KaonomiAssistantService(settings.ServerUrl);
        // The projects, by name, for the selector in the header of the windows of documents.
        ProjectCatalog.Initialize(_assistant);
        _selectedModel = settings.Model;
        // systemprompt.txt (created from config.json the first time), so that a manual change of the file is taken.
        _systemPrompt = SystemPromptStore.Load(settings.SystemPrompt);
        Models.Add(settings.Model);
        ActivityLog.Configure(_assistant);
        KaonomiAssistantService.Shared = _assistant;
        // The presets of the search engine: read at the start (the picker of the chat follows), and after each save.
        PresetStore.Changed += () =>
        {
            OnPropertyChanged(nameof(Presets));
            _choosingPreset = true;
            SelectedPreset = MatchingPreset();
            _choosingPreset = false;
            ForgetCopiedPresetPrompt();
            OnPropertyChanged(nameof(PresetPromptNote));
        };
        _ = PresetStore.LoadAsync(_assistant);
        foreach (var relocated in settings.RelocatedDirectories)
            ActivityLog.Info("Settings", $"Path adapted to this system: {relocated}");

        _isChatOptionsVisible = settings.ChatOptionsVisible;
        _choosingPreset = true;
        _selectedPreset = MatchingPreset();
        _choosingPreset = false;
        ForgetCopiedPresetPrompt();
        // The voice reading the answers speaks the language of the voice input.
        _settings.Language = settings.SpeechLanguage.ToUpperInvariant();
        _speech = CreateSpeech();
    }

    /// <summary>
    /// A new voice input language: the answers are read with a voice of the same language (<c>tts.voices</c>, else a
    /// voice of this language installed in the voices folder), and their spoken summary is in it too.
    /// </summary>
    partial void OnVoiceLanguageChanged(string value)
    {
        var language = value.ToUpperInvariant();
        if (string.Equals(_settings.Language, language, StringComparison.OrdinalIgnoreCase) || _speech is null)
            return;
        _settings.Language = language;
        RecreateSpeech();
        var voice = _settings.ResolvedTtsVoice is { } path ? System.IO.Path.GetFileNameWithoutExtension(path) : null;
        Status = voice is null
            ? $"Voice input in {SpeechLanguage.Name(language)}: no {SpeechLanguage.Name(language)} voice to read the answers (tts.voices in config.yml)"
            : $"Voice input and voice in {SpeechLanguage.Name(language)}: {voice}";
        ActivityLog.Info("Speech", $"Language {language}: voice {voice ?? "(none)"}");
    }

    /// <summary>The voice reading the answers made again from the settings (the answer being read is stopped).</summary>
    private void RecreateSpeech()
    {
        StopSpeaking();
        var previous = _speech;
        previous.SpeakingChanged -= OnSpeechSpeakingChanged;
        previous.Dispose();
        SpeakingChanged?.Invoke(false);
        _speech = CreateSpeech();
    }

    /// <summary>Answers read aloud (tts in config.yml), with the voice of the configured language.</summary>
    private SpeechService CreateSpeech()
    {
        var settings = _settings;
        var speech = new SpeechService(settings.TtsEnabled ? settings.ResolvedTtsPiperPath : null, settings.ResolvedTtsVoice,
            settings.ResolvedTtsSpeaker, settings.TtsSpeed, settings.TtsPitch, settings.TtsRobot, settings.TtsVibrato, settings.TtsAssistant, settings.TtsHorror);
        if (settings.TtsEnabled && !speech.IsAvailable)
            ActivityLog.Error("Speech", $"Text to speech off: Piper ({settings.ResolvedTtsPiperPath}) or the {settings.Language} voice ({settings.ResolvedTtsVoice ?? "none"}) not found");
        speech.SpeakingChanged += OnSpeechSpeakingChanged;
        return speech;
    }

    private void OnSpeechSpeakingChanged(bool speaking) => SpeakingChanged?.Invoke(speaking);

    /// <summary>
    /// Applies a voice and animation preset of the <c>/config</c> window to the running chat (config.yml is already
    /// saved): the voice of <see cref="ChatPreset.VoiceLanguage"/>, its pitch, speed, robotic effect, vibrato, assistant sheen and horror treatment, and the 3D
    /// animation. The answer being read is stopped.
    /// </summary>
    public void ApplyPreset(ChatPreset preset)
    {
        _settings.TtsVoices[ChatPreset.VoiceLanguage] = preset.Voice;
        _settings.TtsPitch = Math.Clamp(preset.Pitch, 0.5, 2);
        _settings.TtsSpeed = Math.Clamp(preset.Speed, 0.25, 4);
        _settings.TtsRobot = Math.Clamp(preset.Robot, 0, 1);
        _settings.TtsVibrato = Math.Clamp(preset.Vibrato, 0, 1);
        _settings.TtsAssistant = Math.Clamp(preset.Assistant, 0, 1);
        _settings.TtsHorror = Math.Clamp(preset.Horror, 0, 1);
        _settings.Animation = preset.Animation;
        _settings.Persona = preset.Persona;

        RecreateSpeech();
        OnPropertyChanged(nameof(Animation));
        OnPropertyChanged(nameof(PresetPromptNote));
        // A preset applied from /config or /remote shows in the picker of the chat too.
        if (!ReferenceEquals(SelectedPreset, preset))
        {
            _choosingPreset = true;
            SelectedPreset = ChatPreset.All.Contains(preset) ? preset : null;
            _choosingPreset = false;
            OnPropertyChanged(nameof(PresetPromptNote));
        }
    }

    /// <summary>
    /// True when the Model, Preset and History zone of the chat is shown (⚙ in the header hides or shows it; kept in
    /// config.yml, <c>chatoptions</c>).
    /// </summary>
    [ObservableProperty]
    private bool _isChatOptionsVisible = true;

    partial void OnIsChatOptionsVisibleChanged(bool value)
    {
        _settings.ChatOptionsVisible = value;
        ChatSettings.SaveChatOptions(value);
    }

    /// <summary>Shows or hides the Model, Preset and History zone.</summary>
    [RelayCommand]
    private void ToggleChatOptions() => IsChatOptionsVisible = !IsChatOptionsVisible;

    // ---- The preset picker of the chat ----

    /// <summary>The presets of the picker of the chat: the ones of config.yml, then the ones saved from /remote.</summary>
    public IReadOnlyList<ChatPreset> Presets => ChatPreset.All;

    /// <summary>
    /// The preset chosen in the picker of the chat (at start, the one matching the voice, the animation and the
    /// personality in use; none when they match no preset). Choosing one applies it at once and keeps it in config.yml.
    /// </summary>
    [ObservableProperty]
    private ChatPreset? _selectedPreset;

    private bool _choosingPreset;

    partial void OnSelectedPresetChanged(ChatPreset? value)
    {
        if (value is null || _choosingPreset)
            return;
        ApplyPreset(value);
        // Kept for the next start, as from /config: config.yml is rewritten with its values (comments and other keys kept).
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "config.yml");
            if (System.IO.File.Exists(path))
                System.IO.File.WriteAllText(path, value.ApplyTo(System.IO.File.ReadAllText(path)), new System.Text.UTF8Encoding(false));
            Status = $"Preset {value.Name}: {value.Description}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = $"Preset {value.Name} applied, but config.yml was not saved: {ex.Message}";
        }
        ActivityLog.Info("Settings", $"Preset {value.Name} chosen in the chat ({value.Voice}, animation {value.Animation})");
    }

    /// <summary>
    /// Switches to the preset asked by <c>CHANGE:NAME</c> in an answer (<see cref="PresetSwitch"/>), as when it is chosen
    /// in the picker of the chat (applied, kept in config.yml); nothing when the answer asks none or the preset is in use.
    /// </summary>
    private void SwitchPresetFrom(string content)
    {
        if (PresetSwitch.Find(content, ChatPreset.All) is not { } preset)
            return;
        if (SelectedPreset is { } current && string.Equals(current.Name, preset.Name, StringComparison.OrdinalIgnoreCase)
            && current.Animation == _settings.Animation)
            return;
        ActivityLog.Info("Settings", $"Preset {preset.Name} asked by the answer (CHANGE:{preset.Name})");
        SelectedPreset = preset;
    }

    /// <summary>The preset matching the voice, the animation and the personality in use, or null.</summary>
    private ChatPreset? MatchingPreset()
    {
        var voice = _settings.TtsVoices.TryGetValue(ChatPreset.VoiceLanguage, out var v) ? v : "";
        return ChatPreset.All.FirstOrDefault(p => p.Animation == _settings.Animation
                                                  && string.Equals(p.Voice, voice, StringComparison.OrdinalIgnoreCase)
                                                  && string.Equals(p.Persona.Trim(), (_settings.Persona ?? "").Trim(), StringComparison.Ordinal))
               ?? ChatPreset.All.FirstOrDefault(p => p.Animation == _settings.Animation
                                                     && string.Equals(p.Voice, voice, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The voice, its effects, the 3D animation and the personality in use, as a preset named "current" (what
    /// <c>/remote</c> starts from).
    /// </summary>
    public ChatPreset CurrentPreset => new("current", "",
        _settings.TtsVoices.TryGetValue(ChatPreset.VoiceLanguage, out var voice) ? voice : "", _settings.TtsPitch, _settings.TtsSpeed,
        _settings.TtsRobot, _settings.Animation, _settings.TtsVibrato, _settings.TtsAssistant, _settings.TtsHorror, _settings.Persona);

    /// <summary>Folder of the Piper voices (<c>tts.voicesDirectory</c>).</summary>
    public string VoicesDirectory => ChatSettings.ResolveDirectory(_settings.TtsVoicesDirectory);

    /// <summary>The Piper executable.</summary>
    public string PiperPath => _settings.ResolvedTtsPiperPath;

    /// <summary>
    /// Raised to put the chat in full screen (true: the voice mode, the animation alone on the whole screen) or back to
    /// its usual mode (false); asked by a script of <c>/remote</c> (<c>FULLSCREEN:ON</c> / <c>FULLSCREEN:OFF</c>).
    /// </summary>
    public event Action<bool>? FullScreenRequested;

    /// <summary>
    /// Raised to show a picture (its full path) over the 3D animation of the chatbot for a while; asked by a script of
    /// <c>/remote</c> (<c>IMAGE:&lt;file&gt;</c>).
    /// </summary>
    public event Action<string>? ImageShowRequested;

    /// <summary>
    /// True while a picture of <see cref="ImageShowRequested"/> is shown, until the animation is back at its place (set
    /// by the window of the chat); a script of <c>/remote</c> waits for it before leaving or entering the full screen.
    /// </summary>
    public bool IsShowingImage { get; set; }

    /// <summary>Asks the window of the chat to show the picture <paramref name="path"/> over its animation; false when no window handles it.</summary>
    public bool RequestImageShow(string path)
    {
        if (ImageShowRequested is not { } handler)
            return false;
        handler(path);
        return true;
    }

    /// <summary>Asks the window of the chat for its full screen (<paramref name="on"/>) or its usual mode; false when no window handles it.</summary>
    public bool RequestFullScreen(bool on)
    {
        if (FullScreenRequested is not { } handler)
            return false;
        handler(on);
        return true;
    }

    /// <summary>
    /// Says <paramref name="text"/> as an answer of the chatbot (<c>/remote</c>): shown in the transcript like an answer,
    /// read aloud with the voice in use, the 3D animation following it. The answer being read is interrupted.
    /// </summary>
    public async Task SayAsync(string text)
    {
        StopSpeaking();
        var answer = new ChatMessageViewModel(ChatRole.Assistant) { IsPending = true };
        Messages.Add(answer);
        OnPropertyChanged(nameof(HasNoMessages));
        ActivityLog.Info("Remote", $"Said ({text.Length} chars): {ActivityLog.Excerpt(text)}");
        _ = SpeakAnswerAsync(text);
        try
        {
            await RevealAsync(answer, text, CancellationToken.None);
        }
        finally
        {
            answer.IsPending = false;
        }
    }

    /// <summary>Stops reading the current answer aloud (and the spoken summary being prepared).</summary>
    public void StopSpeaking()
    {
        _speechPreparation?.Cancel();
        _speech.Stop();
    }

    /// <summary>Spoken summary being asked to Ollama for the last answer; cancelled by a new prompt.</summary>
    private CancellationTokenSource? _speechPreparation;

    /// <summary>True while an answer is read aloud, or while its spoken summary is being prepared.</summary>
    public bool IsSpeakingOrPreparing => _speech.IsActive || _speechPreparation is { IsCancellationRequested: false };

    /// <summary>Block quotes of a Markdown answer (lines starting with "&gt;").</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s{0,3}>", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex QuoteLine();

    /// <summary>
    /// Reads <paramref name="content"/> aloud. An answer with code or quotes is not read as it is: Ollama (the model of
    /// the chat, called directly) gives a short spoken summary in the configured language, followed by "the code is
    /// attached" when there is code. When Ollama fails, the answer is read without its code blocks.
    /// </summary>
    /// <summary>Says <paramref name="text"/> with the voice of the avatar, as it is (the label of an alarm ringing).</summary>
    public void Say(string text)
    {
        if (_speech.IsAvailable)
            _speech.Speak(text);
    }

    private async Task SpeakAnswerAsync(string content, bool? withCode = null)
    {
        _speechPreparation?.Cancel();
        if (!_speech.IsAvailable)
            return;

        var hasCode = withCode ?? CodeBlockExtractor.Extract(content).Count > 0;
        if (!hasCode && !QuoteLine().IsMatch(content))
        {
            _speech.Speak(content);
            return;
        }

        var preparation = new CancellationTokenSource();
        _speechPreparation = preparation;
        string spoken;
        try
        {
            spoken = await SummarizeForSpeechAsync(content, preparation.Token);
            ActivityLog.Info("Speech", $"Spoken summary ({spoken.Length} chars): {ActivityLog.Excerpt(spoken)}");
        }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested)
        {
            return;
        }
        // Whatever fails (the model unreachable, an answer that is not the JSON asked): the answer is still read, and
        // the preparation never stays pending (the mouth of the animation would wait for it for ever).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ActivityLog.Error("Speech", "Spoken summary failed: the answer is read without its code", ex);
            spoken = content;
        }

        var cancelled = preparation.IsCancellationRequested;
        if (ReferenceEquals(_speechPreparation, preparation))
            _speechPreparation = null;
        if (cancelled)
            return;
        _speech.Speak(hasCode ? $"{spoken}\n\n{SpeechLanguage.CodeAttached(_settings.Language)}" : spoken);
    }

    /// <summary>Short spoken version of an answer with code or quotes, asked directly to Ollama (JSON output).</summary>
    private async Task<string> SummarizeForSpeechAsync(string content, CancellationToken cancellationToken)
    {
        var system =
            "You turn the written answer of a chatbot into what a voice assistant says aloud. "
            + $"Write in {SpeechLanguage.Name(_settings.Language)}, in 1 to 3 short spoken sentences that summarize the answer. "
            + "Never read code, commands, file paths or quotations word for word: say in a few words what they are for. "
            + "Do not say that code is attached or shown. No Markdown, no lists, no emojis. "
            + "Answer with a JSON object: {\"speech\": the sentences}.";
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""
            {"type":"object","properties":{"speech":{"type":"string"}},"required":["speech"]}
            """)!;

        var json = await _ollama.ChatJsonAsync(SelectedModel ?? _settings.Model, system, content, schema, cancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("speech", out var speech) && speech.GetString() is { Length: > 0 } text
            ? text
            : throw new System.Text.Json.JsonException("No \"speech\" in the answer of Ollama.");
    }

    /// <summary>Loudness of the voice reading the answer, from 0 (silence or nothing read) to 1; any thread.</summary>
    public float SpeechLevel => _speech.Level;

    /// <summary>Raised (on a background thread) when an answer starts (true) or stops (false) being read aloud.</summary>
    public event Action<bool>? SpeakingChanged;

    /// <summary>Messages shown in the chat transcript.</summary>
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    /// <summary>Model names available from the local Ollama instance.</summary>
    public ObservableCollection<string> Models { get; } = [];

    /// <summary>Configured Ollama base URL for status display.</summary>
    public string OllamaUrl => _ollama.BaseAddress.ToString();

    /// <summary>Configured Kaonomi server base URL for status display.</summary>
    public string ServerUrl => _assistant.BaseAddress.ToString();

    /// <summary>True when the transcript is empty (welcome state).</summary>
    public bool HasNoMessages => Messages.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = string.Empty;

    // ---- Voice input: hold the microphone button, speak, release: the text is sent ----

    private SpeechRecognitionService? _voiceInput;
    private Task<bool>? _speechStart;

    /// <summary>Languages of the voice input (fr, de, en).</summary>
    public IReadOnlyList<string> VoiceLanguages { get; } = ChatSettings.SpeechLanguages;

    /// <summary>Language spoken to the microphone (French by default: <c>speech.language</c> in config.yml).</summary>
    [ObservableProperty]
    private string _voiceLanguage = "fr";

    /// <summary>True while the microphone records (the button is held).</summary>
    [ObservableProperty]
    private bool _isListening;

    /// <summary>True while the speech is turned into text.</summary>
    [ObservableProperty]
    private bool _isTranscribing;

    /// <summary>True from the moment a prompt is sent until its completion comes back (the waiting animation).</summary>
    [ObservableProperty]
    private bool _isAwaitingAnswer;

    /// <summary>
    /// Starts recording the microphone (the button is pressed). The microphone is opened off the UI thread: macOS may
    /// hold it until the application that launched the chat is allowed to use it.
    /// </summary>
    public void StartListening()
    {
        if (IsListening || IsTranscribing || _speechStart is { IsCompleted: false })
            return;
        _voiceInput ??= new SpeechRecognitionService(_settings.SpeechModel);
        if (!_voiceInput.HasModel)
        {
            Status = $"Voice input: Whisper model not found ({_voiceInput.ModelPath}); set speech.model in config.yml";
            return;
        }
        var speech = _voiceInput;
        IsListening = true;
        Status = $"🎤 Listening ({VoiceLanguage.ToUpperInvariant()})… release the button to send";
        _speechStart = OpenMicrophoneAsync(speech);
    }

    private async Task<bool> OpenMicrophoneAsync(SpeechRecognitionService speech)
    {
        try
        {
            await Task.Run(speech.Start).WaitAsync(TimeSpan.FromSeconds(6));
            return true;
        }
        catch (TimeoutException)
        {
            // Given up: the microphone, if it opens later, is closed at once (it never records unseen).
            speech.Cancel();
            IsListening = false;
            Status = "Voice input: the microphone does not open; allow it for the application that launched the chat (System Settings › Privacy & Security › Microphone)";
            ActivityLog.Error("Speech", "The microphone did not open within 6 s (microphone permission?)");
        }
        catch (Exception ex)
        {
            IsListening = false;
            Status = "Voice input: " + ex.Message;
            ActivityLog.Error("Speech", "Microphone: " + ex.Message);
        }
        return false;
    }

    /// <summary>
    /// Stops recording (the button is released), turns the speech into text, puts it in the message box and sends it.
    /// </summary>
    public async Task StopListeningAsync()
    {
        if (_speechStart is not { } start || _voiceInput is not { } speech)
            return;
        _speechStart = null;
        if (!await start)
            return;
        IsListening = false;
        IsTranscribing = true;
        Status = "🎤 Recognising the speech…";
        try
        {
            var text = await speech.StopAndTranscribeAsync(VoiceLanguage);
            if (text.Length == 0)
            {
                Status = "🎤 Nothing heard: hold the button while speaking";
                return;
            }
            Input = text;
            Status = "🎤 " + text;
            if (SendCommand.CanExecute(null))
                SendCommand.Execute(null);
        }
        catch (Exception ex)
        {
            Status = "Voice input: " + ex.Message;
            ActivityLog.Error("Speech", "Recognition: " + ex.Message);
        }
        finally
        {
            IsTranscribing = false;
        }
    }

    /// <summary>Releases the microphone and the Whisper model (the chat closes).</summary>
    public void DisposeSpeech()
    {
        _voiceInput?.Dispose();
        _voiceInput = null;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string? _selectedModel;

    /// <summary>True while the Kaonomi REST server answers (<see cref="CheckServerAsync"/>): prompts can be sent.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isServerReachable;

    /// <summary>Time the Kaonomi server has to answer the reachability check.</summary>
    private static readonly TimeSpan ServerCheckTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Checks that the Kaonomi REST server answers; Send stays disabled (except for "/" commands) while it does not.</summary>
    public async Task CheckServerAsync()
    {
        var reachable = await _assistant.IsReachableAsync(ServerCheckTimeout);
        if (reachable == IsServerReachable)
            return;
        IsServerReachable = reachable;
        if (reachable)
            ActivityLog.Info("Chat", $"Kaonomi server reachable at {ServerUrl}: Send enabled");
        else
            ActivityLog.Error("Chat", $"Kaonomi server unreachable at {ServerUrl}: Send disabled", new HttpRequestException("No answer"));
    }

    partial void OnSelectedModelChanged(string? oldValue, string? newValue)
    {
        if (!string.IsNullOrEmpty(newValue) && newValue != oldValue)
            ActivityLog.Info("Chat", $"Model selected: {newValue}");
    }

    [ObservableProperty]
    private string _systemPrompt;

    /// <summary>Pending write of systemprompt.txt, replaced at each keystroke.</summary>
    private CancellationTokenSource? _systemPromptSave;

    /// <summary>Delay after the last change of the system prompt before it is written to systemprompt.txt.</summary>
    private static readonly TimeSpan SystemPromptSaveDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Each change of the system prompt is written to systemprompt.txt, once the typing pauses.</summary>
    partial void OnSystemPromptChanged(string value)
    {
        _systemPromptSave?.Cancel();
        var save = _systemPromptSave = new CancellationTokenSource();
        _ = Task.Delay(SystemPromptSaveDelay, save.Token).ContinueWith(task =>
        {
            // The text as it is now (not the one of this keystroke: a later one may have been typed meanwhile).
            if (!task.IsCanceled)
                SystemPromptStore.Save(SystemPrompt);
            save.Dispose();
        }, TaskScheduler.Default);
    }

    /// <summary>Writes the system prompt to systemprompt.txt now (when the application closes).</summary>
    public void SaveSystemPrompt()
    {
        _systemPromptSave?.Cancel();
        SystemPromptStore.Save(SystemPrompt);
    }

    /// <summary>
    /// The system prompt of the chat holding the system prompt of a preset, as APPLY of /preset used to copy it there:
    /// it is given back its usual text (systemPrompt of config.json), since the system prompt of the preset in use is
    /// sent before it anyway (<see cref="FullSystemPrompt"/>), and another preset would otherwise get both.
    /// </summary>
    private void ForgetCopiedPresetPrompt()
    {
        var current = SystemPrompt?.Trim() ?? string.Empty;
        if (current.Length == 0 || ChatPreset.All.FirstOrDefault(p => p.Persona.Trim().Length > 0 && p.Persona.Trim() == current) is not { } copied)
            return;
        var usual = _settings.SystemPrompt?.Trim() ?? string.Empty;
        SystemPrompt = ChatPreset.All.Any(p => p.Persona.Trim() == usual) ? string.Empty : usual;
        SaveSystemPrompt();
        ActivityLog.Info("Settings", $"System prompt of the chat given back its usual text: it held the system prompt of the preset {copied.Name}, sent with that preset only");
    }

    /// <summary>What is sent before the system prompt of the chat: the system prompt of the preset in use (below the SYSTEM PROMPT box).</summary>
    public string PresetPromptNote
    {
        get
        {
            var name = SelectedPreset?.Name ?? "in use";
            var persona = ActivePersona;
            if (persona.Length == 0)
                return $"The preset {name} has no system prompt of its own (/preset to give it one): this one is sent alone.";
            var line = persona.ReplaceLineEndings(" ");
            return $"Sent before it, the system prompt of the preset {name}: {(line.Length > 220 ? line[..220] + "…" : line)}";
        }
    }

    /// <summary>
    /// The system prompt of the preset in use: the one of the preset chosen as it is now (an edit in /preset counts at
    /// once), else the personality kept in config.yml (a voice and an animation set by hand in /remote).
    /// </summary>
    private string ActivePersona => (SelectedPreset?.Persona ?? _settings.Persona ?? string.Empty).Trim();

    [ObservableProperty]
    private bool _isSystemPromptVisible;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _status = "Connecting to the model server…";

    /// <summary>Maximum number of inputs kept for Up / Down recall.</summary>
    private const int InputHistoryLimit = 200;

    /// <summary>Everything sent from the input box (prompts and commands), oldest first.</summary>
    private readonly List<string> _inputHistory = [];

    /// <summary>Position while browsing the history; -1 when not browsing.</summary>
    private int _historyIndex = -1;

    /// <summary>Text being typed when browsing started, restored after the newest entry.</summary>
    private string _historyDraft = string.Empty;

    /// <summary>Shows the previous (older) input. Returns false when there is nothing older.</summary>
    public bool RecallPreviousInput()
    {
        if (_inputHistory.Count == 0 || _historyIndex == 0)
            return false;

        if (_historyIndex < 0)
        {
            _historyDraft = Input;
            _historyIndex = _inputHistory.Count;
        }

        Input = _inputHistory[--_historyIndex];
        return true;
    }

    /// <summary>Shows the next (newer) input, then the draft that was being typed. Returns false when not browsing.</summary>
    public bool RecallNextInput()
    {
        if (_historyIndex < 0)
            return false;

        if (++_historyIndex >= _inputHistory.Count)
        {
            _historyIndex = -1;
            Input = _historyDraft;
        }
        else
        {
            Input = _inputHistory[_historyIndex];
        }
        return true;
    }

    /// <summary>Adds a sent input to the history (consecutive duplicates are stored once) and stops browsing.</summary>
    private void RememberInput(string text)
    {
        if (_inputHistory.Count == 0 || _inputHistory[^1] != text)
            _inputHistory.Add(text);
        if (_inputHistory.Count > InputHistoryLimit)
            _inputHistory.RemoveAt(0);

        _historyIndex = -1;
        _historyDraft = string.Empty;
    }

    /// <summary>Runs a <c>/</c> command as if typed in the input box (systray menu); ignored while an answer is awaited.</summary>
    public void RunCommand(string command)
    {
        // A tool (the Neocom, a window restored) opens at once, whatever the chat is doing (an answer awaited or
        // revealed): it does not go through the prompt, which is busy then.
        var text = command.Trim();
        if (TryParseDetachedCommand(text, out var detached) && OpenTool(detached, detached: true))
        {
            ActivityLog.Info("Chat", $"Command {text}");
            return;
        }
        if (IsToolCommand(text) && OpenTool(text, detached: false))
        {
            ActivityLog.Info("Chat", $"Command {text}");
            return;
        }
        if (SendCommand.IsRunning)
        {
            Status = $"The chat is answering: {text} will have to wait";
            return;
        }
        Input = command;
        if (SendCommand.CanExecute(null))
            SendCommand.Execute(null);
    }

    /// <summary>Reloads the Ollama model list and updates connection status.</summary>
    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        Status = $"Connecting to {_ollama.ProviderName}…";
        try
        {
            var models = await _ollama.ListModelsAsync();
            var current = SelectedModel;

            Models.Clear();
            foreach (var model in models)
                Models.Add(model);

            SelectedModel = PickModel(models, current) ?? PickModel(models, _settings.Model) ?? models.FirstOrDefault();
            IsConnected = true;
            Status = models.Count == 0
                ? _ollama.IsOpenAi
                    ? "LM Studio connected, but no model is listed (download one in LM Studio)"
                    : "Ollama connected, but no model is installed (ollama pull <model>)"
                : $"{_ollama.ProviderName} connected · {models.Count} model(s)";
            ActivityLog.Info("Chat", $"{_ollama.ProviderName} models loaded from {OllamaUrl}: {models.Count} ({string.Join(", ", models)})");
        }
        // Run from a command (Execute): any exception left would end the process. A non-JSON answer (proxy, wrong URL)
        // is NotSupportedException / JsonException, not HttpRequestException.
        catch (Exception ex)
        {
            IsConnected = false;
            Status = $"{_ollama.ProviderName} unreachable at {OllamaUrl}";
            ActivityLog.Error("Chat", $"{_ollama.ProviderName} unreachable at {OllamaUrl}", ex);
        }
    }

    /// <summary>Messages shown in the scrolling ticker band above the conversation.</summary>
    public ObservableCollection<TickerItemViewModel> TickerItems { get; } = [];

    [ObservableProperty]
    private bool _hasTickerItems;

    /// <summary>Last ticker refresh outcome (null before the first one), so only changes are logged.</summary>
    private bool? _tickerState;

    /// <summary>The texts of the ticker as they are shown, to tell when a refresh brings nothing new.</summary>
    private string? _tickerSignature;

    /// <summary>Reloads the ticker from <c>/messageflow/last</c>; keeps the previous items when the server is unreachable.</summary>
    [RelayCommand]
    private async Task RefreshTickerAsync()
    {
        try
        {
            // The shared client has no timeout; bound this background call so a stalled server cannot pile up requests.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var entries = await _assistant.GetLastMessageFlowsAsync("*", 100, timeout.Token);

            // Refreshed every minute and most often the same: the list (and the band that shows it) is rebuilt only
            // when a message changed.
            var shownEntries = entries.Where(e => !string.IsNullOrWhiteSpace(e.Text)).ToList();
            var signature = string.Join('\u001F', shownEntries.Select(e => e.Text));
            if (signature != _tickerSignature || TickerItems.Count != shownEntries.Count)
            {
                _tickerSignature = signature;
                TickerItems.Clear();
                foreach (var entry in shownEntries)
                    TickerItems.Add(new TickerItemViewModel(entry));
            }

            // Refreshed every minute: log the first load and the recovery after a failure, not every refresh.
            if (_tickerState != true)
                ActivityLog.Info("Ticker", $"Ticker loaded: {TickerItems.Count} message(s)");
            _tickerState = true;
        }
        // Run from a timer (Execute): any exception left would end the process.
        catch (Exception ex)
        {
            Debug.WriteLine($"Ticker refresh failed: {ex.Message}");
            if (_tickerState != false)
                ActivityLog.Error("Ticker", "Ticker refresh failed (/messageflow/last)", ex);
            _tickerState = false;
        }

        HasTickerItems = TickerItems.Count > 0;
    }

    private const string CacheCommand = "/cache";
    /// <summary>
    /// The id of the conversation shown, sent with each prompt: the server keeps the context of the conversation
    /// under it (the older exchanges more and more compressed). A new one at each start and each new conversation.
    /// </summary>
    private string _conversationId = Guid.NewGuid().ToString("N");

    private const string SearchCommand = "/search";
    private const string JsonCommand = "/json";
    private const string SymbolCommand = "/symbol";
    private const string OntologyCommand = "/ontology";
    private const string IdeaCommand = "/idea";
    private const string CalculatorCommand = "/work";
    private const string PictureCommand = "/picture";
    private const string PicturesCommand = "/pictures";
    private const string MusicCommand = "/music";
    private const string RecorderCommand = "/recorder";
    private const string DiaryCommand = "/diary";
    private const string JournalCommand = "/journal";
    private const string YoutubeCommand = "/youtube";
    private const string FooocusCommand = "/fooocus";
    private const string CompletionCommand = "/completion";
    private const string SystemCommand = "/system";
    private const string LogCommand = "/log";
    private const string LogsCommand = "/logs";
    private const string TokenCommand = "/token";
    private const string TokensCommand = "/tokens";
    private const string ConfigCommand = "/config";
    private const string RemoteCommand = "/remote";
    private const string PresetCommand = "/preset";
    private const string PresetsCommand = "/presets";
    private const string WikiCommand = "/wiki";
    private const string InterviewCommand = "/interview";
    private const string CvCommand = "/cv";
    private const string BpmnCommand = "/bpmnai";
    private const string InfraCommand = "/infra";
    private const string MindmapCommand = "/mindmap";
    private const string WriteCommand = "/write";
    private const string ConsoleCommand = "/cmd";
    private const string ProcessCommand = "/process";
    private const string ProcessesCommand = "/processes";
    private const string GatewayCommand = "/gateway";
    private const string BnpCommand = "/bnp";
    private const string KanbanCommand = "/kanban";
    private const string ProjectCommand = "/project";
    private const string IntelCommand = "/intel";
    private const string CollectionPlanCommand = "/collectionplan";
    private const string Game1Command = "/game:solitaire";
    private const string FractalCommand = "/fractal";
    private const string ChessCommand = "/game:chess";
    private const string GoCommand = "/game:go";
    private const string ImmuneCommand = "/immune";
    private const string AlarmCommand = "/alarm";
    private const string AlarmsCommand = "/alarms";
    private const string CalendarCommand = "/calendar";
    private const string IqCommand = "/iq";
    private const string ActivityCommand = "/activity";
    private const string GetThemesCommand = "/getthemes";
    private const string FullChatCommand = "/fullchat";
    private const string SetupCommand = "/setup";
    private const string ThinkCommand = "/think";
    private const string WindowsCommand = "/windows";
    private const string MosaicCommand = "/mosaic";
    private const string AudioSettingsCommand = "/audiosettings";
    private const string CloseAllCommand = "/closeall";
    private const string RelationCommand = "/relation";
    private const string FoldersCommand = "/folders";
    private const string FolderCommand = "/folder";
    private const string HelpCommand = "/help";
    private const string HelpShortCommand = "/?";
    private const string ThemePrefix = "/theme:";

    /// <summary><c>/n</c> instead of <c>/</c> before a tool command (<c>/ncache</c>, <c>/nlog</c>…) opens it in its own window.</summary>
    private const string DetachedPrefix = "/n";

    /// <summary>Delay between the "Bye" answer and the shutdown, so the user can read it or cancel.</summary>
    private const int ExitDelaySeconds = 10;

    private static bool IsCommand(string text, string command) =>
        string.Equals(text.Trim(), command, StringComparison.OrdinalIgnoreCase);

    /// <summary>Answer to a "/" prompt that is not a known command.</summary>
    private const string UnsupportedCommandAnswer = "unsupported command!";

    /// <summary>
    /// Any prompt starting with "/" is a command, handled locally: a known one (<c>/ontology</c>, <c>/json</c>…) or an
    /// unsupported one. It is never sent to the completion service.
    /// </summary>
    private static bool IsLocalCommand(string text) => text.TrimStart().StartsWith('/');

    private static bool IsKnownCommand(string text) =>
        IsToolCommand(text) || TryParseDetachedCommand(text, out _)
        || IsCommand(text, GetThemesCommand) || IsCommand(text, FullChatCommand) || IsCommand(text, DetachedPrefix + FullChatCommand[1..]) || IsCommand(text, SetupCommand) || IsCommand(text, ThinkCommand) || IsCommand(text, WindowsCommand) || IsCommand(text, MosaicCommand) || IsCommand(text, AudioSettingsCommand) || IsCommand(text, CloseAllCommand) || IsHelpCommand(text) || IsExitCommand(text) || IsCommand(text, KillAllCommand) || IsCommand(text, BnpCommand) || TryParseThemeCommand(text, out _);

    /// <summary>Commands that open a tool view (a tab of the workspace window, or its own window with <c>/n</c>).</summary>
    private static bool IsToolCommand(string text) =>
        IsCommand(text, CacheCommand) || IsCommand(text, SearchCommand) || IsCommand(text, JsonCommand) || IsCommand(text, SymbolCommand)
        || IsCommand(text, OntologyCommand) || IsCommand(text, IdeaCommand) || IsCommand(text, CalculatorCommand) || IsCommand(text, PictureCommand) || IsCommand(text, PicturesCommand) || IsCommand(text, MusicCommand) || IsCommand(text, RecorderCommand) || IsCommand(text, DiaryCommand) || IsCommand(text, JournalCommand) || IsCommand(text, YoutubeCommand) || IsCommand(text, FooocusCommand) || IsCommand(text, CompletionCommand) || IsCommand(text, SystemCommand) || IsCommand(text, LogCommand) || IsCommand(text, LogsCommand) || IsCommand(text, TokenCommand) || IsCommand(text, TokensCommand)
        || IsCommand(text, ConfigCommand) || IsCommand(text, RemoteCommand) || IsCommand(text, KanbanCommand) || IsCommand(text, ProjectCommand) || IsCommand(text, IntelCommand) || IsCommand(text, CollectionPlanCommand) || IsCommand(text, Game1Command) || IsCommand(text, FractalCommand) || IsCommand(text, ChessCommand) || IsCommand(text, GoCommand) || IsCommand(text, ImmuneCommand) || IsCommand(text, AlarmCommand) || IsCommand(text, AlarmsCommand) || IsCommand(text, CalendarCommand) || IsCommand(text, IqCommand) || IsCommand(text, ActivityCommand)
        || IsCommand(text, RelationCommand) || IsCommand(text, FoldersCommand) || IsCommand(text, FolderCommand) || IsCommand(text, PresetCommand) || IsCommand(text, PresetsCommand)
        || IsCommand(text, ProcessCommand) || IsCommand(text, ProcessesCommand) || IsCommand(text, GatewayCommand) || IsCommand(text, WikiCommand) || IsCommand(text, InterviewCommand) || IsCommand(text, CvCommand) || IsCommand(text, BpmnCommand) || IsCommand(text, InfraCommand) || IsCommand(text, MindmapCommand) || IsCommand(text, WriteCommand) || IsCommand(text, ConsoleCommand);

    /// <summary>The most tools one answer opens: an answer that lists the commands must not open them all.</summary>
    private const int MaxCommandsPerAnswer = 3;

    /// <summary>A word starting with "/" that is not part of a path or a URL (nothing but a space or punctuation before it).</summary>
    private static readonly System.Text.RegularExpressions.Regex CommandInText =
        new(@"(?<![\w/:.\\])/[A-Za-z?][\w:]*", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Runs the commands of tools written in an answer of the assistant (« ouvre /system », « regarde dans
    /// /ontology »): each one opens its window, as if the user had typed it; returns the commands run. Only the
    /// commands that open a tool are run (<see cref="IsToolCommand"/>): never /bye, /killall, /closeall or a theme.
    /// The same command once, <see cref="MaxCommandsPerAnswer"/> at most; the code blocks of the answer are not read.
    /// </summary>
    private List<string> RunCommandsOf(string answer)
    {
        var opened = new List<string>();
        // Code is not prose: a path or an option written there is not a command of the chat.
        var prose = System.Text.RegularExpressions.Regex.Replace(answer, "```.*?```", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match match in CommandInText.Matches(prose))
        {
            var command = match.Value.TrimEnd(':').ToLowerInvariant();
            if (opened.Contains(command) || !IsToolCommand(command))
                continue;
            if (opened.Count >= MaxCommandsPerAnswer)
                break;
            if (OpenTool(command, detached: false))
            {
                opened.Add(command);
                ActivityLog.Info("Chat", $"Command {command} found in the answer: run");
            }
        }
        return opened;
    }

    /// <summary>Recognises <c>/n&lt;tool&gt;</c> (e.g. <c>/njson</c>) and returns the tool command (<c>/json</c>).</summary>
    private static bool TryParseDetachedCommand(string text, out string command)
    {
        var trimmed = text.Trim();
        command = "/" + (trimmed.Length > DetachedPrefix.Length ? trimmed[DetachedPrefix.Length..] : "");
        return trimmed.StartsWith(DetachedPrefix, StringComparison.OrdinalIgnoreCase) && IsToolCommand(command);
    }

    private static bool IsHelpCommand(string text) => IsCommand(text, HelpCommand) || IsCommand(text, HelpShortCommand);

    private static bool IsExitCommand(string text) =>
        IsCommand(text, "/bye") || IsCommand(text, "/quit") || IsCommand(text, "/exit");

    private const string KillAllCommand = "/killall";

    /// <summary>Recognises <c>/theme:&lt;name&gt;</c> for every known theme name (see <see cref="AppTheme.TryParse"/>).</summary>
    private static bool TryParseThemeCommand(string text, out AppThemeKind kind)
    {
        kind = AppThemeKind.Default;
        var trimmed = text.Trim();
        return trimmed.StartsWith(ThemePrefix, StringComparison.OrdinalIgnoreCase)
               && AppTheme.TryParse(trimmed[ThemePrefix.Length..], out kind);
    }

    // Prompts need a model and the Kaonomi server; "/" commands are handled locally and stay available (/bye…).
    private bool CanSend() =>
        !string.IsNullOrWhiteSpace(Input)
        && (IsLocalCommand(Input) || (!string.IsNullOrWhiteSpace(SelectedModel) && IsServerReachable));

    /// <summary>Sends user input to the assistant or opens a slash-command tool window.</summary>
    [RelayCommand(CanExecute = nameof(CanSend), IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        var prompt = Input.Trim();
        Input = string.Empty;
        RememberInput(prompt);
        // Whatever is typed next, the answer being read aloud is interrupted.
        StopSpeaking();

        if (IsLocalCommand(prompt))
        {
            if (!IsKnownCommand(prompt))
            {
                // Never sent to the completion service.
                AddLocalExchange(prompt, UnsupportedCommandAnswer);
                ActivityLog.Info("Chat", $"Unsupported command {ActivityLog.Excerpt(prompt)}");
                return;
            }

            ActivityLog.Info("Chat", $"Command {prompt}");
        }

        // "/n<tool>": the tool opens in its own window instead of a tab.
        var detached = TryParseDetachedCommand(prompt, out var toolCommand);
        var command = detached ? toolCommand : prompt;

        // Commands open windows and build view models (folders, registry, screens): a failure there stays in the
        // chat; left to the command, it would end the process.
        try
        {
            if (OpenTool(command, detached))
                return;

            if (TryParseThemeCommand(prompt, out var theme))
            {
                ApplyTheme(theme);
                return;
            }

            if (IsCommand(prompt, GetThemesCommand))
            {
                ShowThemes(prompt);
                return;
            }

            if (IsCommand(prompt, BnpCommand))
            {
                await PresentAsync(prompt);
                return;
            }

            if (IsHelpCommand(prompt))
            {
                ShowHelp(prompt);
                return;
            }

            if (IsCommand(prompt, FullChatCommand) || IsCommand(prompt, DetachedPrefix + FullChatCommand[1..]))
            {
                // /nfullchat: straight in a window of the system (the Godot chat).
                FullChatRequested?.Invoke(IsCommand(prompt, DetachedPrefix + FullChatCommand[1..]));
                return;
            }

            if (IsCommand(prompt, SetupCommand))
            {
                if (SetupRequested is null)
                    Status = "/setup is only in the Godot chat";
                else
                    SetupRequested.Invoke();
                return;
            }

            if (IsCommand(prompt, ThinkCommand))
            {
                if (ThinkRequested is null)
                    Status = "/think is only in the Godot chat";
                else
                    ThinkRequested.Invoke();
                return;
            }

            if (IsCommand(prompt, MosaicCommand))
            {
                if (MosaicRequested is null)
                    Status = "/mosaic is only in the Godot chat";
                else
                    MosaicRequested.Invoke();
                return;
            }

            if (IsCommand(prompt, WindowsCommand))
            {
                if (WindowsRequested is null)
                    Status = "/windows is only in the Godot chat";
                else
                    WindowsRequested.Invoke();
                return;
            }

            if (IsCommand(prompt, AudioSettingsCommand))
            {
                if (AudioSettingsRequested is null)
                    Status = "/audiosettings is only in the Godot chat";
                else
                    AudioSettingsRequested.Invoke();
                return;
            }

            if (IsCommand(prompt, CloseAllCommand))
            {
                if (CloseAllRequested is null)
                    Status = "/closeall is only in the Godot chat";
                else
                {
                    CloseAllRequested.Invoke();
                    Status = "All the windows closed";
                    ActivityLog.Info("Chat", "/closeall: all the windows closed");
                }
                return;
            }

            if (IsCommand(prompt, KillAllCommand))
            {
                await KillAllAsync(prompt, cancellationToken);
                return;
            }

            if (IsExitCommand(prompt))
            {
                await ExitAsync(prompt, cancellationToken);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddLocalExchange(prompt, $"Command failed: {ex.Message}");
            ActivityLog.Error("Chat", $"Command {ActivityLog.Excerpt(prompt)} failed", ex);
            return;
        }

        var model = SelectedModel!;

        Messages.Add(new ChatMessageViewModel(ChatRole.User, prompt));
        var history = BuildHistory();

        var answer = new ChatMessageViewModel(ChatRole.Assistant) { IsPending = true };
        Messages.Add(answer);
        OnPropertyChanged(nameof(HasNoMessages));
        ActivityLog.Info("Chat", $"Prompt sent to {model} ({prompt.Length} chars): {ActivityLog.Excerpt(prompt)}");

        await AnswerAsync(model, history, answer, cancellationToken);
    }

    /// <summary>
    /// Asks <paramref name="model"/> the request <paramref name="history"/> through the Kaonomi server and shows its
    /// answer in the pending bubble <paramref name="answer"/> (read aloud, revealed, its stats and code).
    /// </summary>
    private async Task AnswerAsync(string model, List<AssistantMessage> history, ChatMessageViewModel answer, CancellationToken cancellationToken, bool regenerate = false)
    {
        answer.Request = history;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            IsAwaitingAnswer = true;
            AssistantResponse response;
            try
            {
                // The prompts of a batch are answered alone: they are not a conversation.
                response = await _assistant.SendAsync(model, history, cancellationToken, IsBatchRunning ? null : _conversationId, regenerate);
            }
            finally
            {
                IsAwaitingAnswer = false;
            }
            var content = response.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrEmpty(content))
            {
                answer.IsError = true;
                answer.Content = "The Kaonomi server returned no answer. " +
                                 "The agent sequence probably failed: check the server logs.";
                ActivityLog.Error("Chat", $"Empty answer from the Kaonomi server ({model}, {stopwatch.Elapsed.TotalSeconds:0.0} s)");
                return;
            }

            // The model gave no answer (the server says so): its message is shown as an error, not as an answer.
            if (string.Equals(response.Choices?.FirstOrDefault()?.FinishReason, "error", StringComparison.OrdinalIgnoreCase))
            {
                answer.IsError = true;
                answer.Content = content;
                ActivityLog.Error("Chat", $"The model gave no answer ({model}, {stopwatch.Elapsed.TotalSeconds:0.0} s): {ActivityLog.Excerpt(content)}");
                return;
            }

            answer.Model = string.IsNullOrEmpty(response.Model) ? model : response.Model;
            var stats = FormatStats(response, model, stopwatch.Elapsed);
            // CHANGE:MARI (or another preset or 3D animation) in the answer: the chat switches to it before reading it,
            // so that the answer is said with the new voice; the marker itself is not read.
            SwitchPresetFrom(content);
            // Read aloud while it is shown (not during a prompt collection).
            var spoken = PresetSwitch.Strip(content);
            // The code of the answer, found once (its Markdown parsed once): for the voice and for the answer.
            var snippets = CodeBlockExtractor.Extract(content);
            if (!IsBatchRunning && spoken.Length > 0)
                _ = SpeakAnswerAsync(spoken, snippets.Count > 0);
            await RevealAsync(answer, content, cancellationToken);
            answer.Stats = stats;
            answer.CodeSnippets = snippets;
            // A command of a tool written in the answer (/system, /ontology…) is run once the answer is shown.
            if (!IsBatchRunning && RunCommandsOf(content) is { Count: > 0 } opened)
                answer.Stats += " · opened " + string.Join(", ", opened);
            ActivityLog.Info("Chat", $"Answer received ({answer.Stats}, {content.Length} chars): {ActivityLog.Excerpt(content)}");

            // No code window during a prompt collection (the code stays in the answer and can still be opened from it).
            if (answer.HasCode && !IsBatchRunning)
            {
                ShowCode(answer);
                // HTML code is also opened in the default web browser.
                HtmlPreview.Open(answer.CodeSnippets);
            }
        }
        catch (OperationCanceledException)
        {
            answer.Stats = "Request cancelled";
            ActivityLog.Info("Chat", $"Prompt cancelled after {stopwatch.Elapsed.TotalSeconds:0.0} s");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            answer.IsError = true;
            answer.Content = $"Kaonomi server unreachable at {ServerUrl} ({ex.Message})";
            ActivityLog.Error("Chat", $"Kaonomi server unreachable at {ServerUrl}", ex);
        }
        catch (Exception ex)
        {
            answer.IsError = true;
            answer.Content = ex.Message;
            ActivityLog.Error("Chat", $"Prompt failed ({model})", ex);
        }
        finally
        {
            answer.IsPending = false;
        }
    }

    /// <summary>The gateway in front of the models (llmGatewayUrl): told of the disliked answers.</summary>
    private LlmGatewayClient? _gateway;

    /// <summary>
    /// A thumb down on <paramref name="disliked"/>: KAONOMI.LLMGateway takes that interaction out of its cache and,
    /// when its default model gave the answer, names the model to ask again (routing.escalationModel): the same
    /// request is then sent to it, and its answer shown under the disliked one. Nothing happens when the gateway is
    /// not running, or when the answer came from another model. An answer is asked again once.
    /// </summary>
    public async Task RetryDislikedAsync(ChatMessageViewModel disliked)
    {
        if (!disliked.CanEvaluate || disliked.AskedAgain || disliked.Request is not { Count: > 0 } request)
            return;
        var prompt = request.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
        disliked.AskedAgain = true;
        try
        {
            GatewayDislikeResult? result;
            try
            {
                _gateway ??= new LlmGatewayClient(_settings.LlmGatewayUrl);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                result = await _gateway.DislikeAsync(prompt, disliked.Content, disliked.Model, timeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or NotSupportedException)
            {
                disliked.AskedAgain = false;
                ActivityLog.Info("Chat", $"Dislike not sent to the LLM gateway at {_settings.LlmGatewayUrl} ({ex.Message}): the answer is not asked again");
                return;
            }

            ActivityLog.Info("Chat", $"Dislike sent to the LLM gateway: {result?.Removed ?? 0} answer(s) out of its cache · {result?.Reason}");
            if (result?.EscalationModel is not { Length: > 0 } model)
            {
                disliked.AskedAgain = false;
                return;
            }

            var index = Messages.IndexOf(disliked);
            if (index < 0)
                return;
            var answer = new ChatMessageViewModel(ChatRole.Assistant) { IsPending = true, AskedAgain = false };
            Messages.Insert(index + 1, answer);
            Status = $"👎 {result.Model}: asked again to {model}";
            ActivityLog.Info("Chat", $"Disliked answer of {result.Model} asked again to {model}: {ActivityLog.Excerpt(prompt)}");
            await AnswerAsync(model, request.ToList(), answer, CancellationToken.None, regenerate: true);
            // The new answer cannot be sent back to the default model, and is not asked again either.
            if (answer.HasStats && !answer.IsError)
                answer.Stats += " · asked again after 👎";
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Chat", "Disliked answer not asked again", ex);
        }
    }

    /// <summary>Number of non-empty lines shown progressively before the rest of the answer is shown at once.</summary>
    private const int StreamedLines = 5;

    /// <summary>
    /// True while a prompt collection runs: its answers are shown at once, without the simulated streaming.
    /// </summary>
    public bool IsBatchRunning { get; set; }

    /// <summary>Saves a prompt and its completion on the Kaonomi server (<c>PUT /completion</c>).</summary>
    public Task<ServiceReturn> SaveCompletionAsync(CompletionEntry entry, CancellationToken cancellationToken = default) =>
        _assistant.SaveCompletionAsync(entry, cancellationToken);

    /// <summary>The model selected in the chat window (for the prompt collection report).</summary>
    public string? CurrentModel => SelectedModel;

    /// <summary>The last answer of the conversation (the completion of the prompt just sent), or null.</summary>
    public ChatMessageViewModel? LastAnswer => Messages.LastOrDefault(m => m.IsAssistant);

    /// <summary>
    /// Summary of the topics of <paramref name="prompts"/>, asked directly to the Ollama model selected in the chat
    /// (JSON output: a short summary and the topics with the number of prompts about each).
    /// </summary>
    public async Task<(string Summary, List<CollectionTopic> Topics)> SummarizeTopicsAsync(
        IReadOnlyList<string> prompts, CancellationToken cancellationToken)
    {
        const string system =
            "You analyse a list of prompts sent to an assistant. Group them into the main topics they are about and "
            + "count the prompts of each topic. Write in the language of the prompts. Answer with a JSON object: "
            + "\"summary\" (a short paragraph describing the subjects covered) and \"topics\" (array of "
            + "{\"topic\": name, \"promptCount\": number}), most frequent topic first.";
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""
            {"type":"object","properties":{
              "summary":{"type":"string"},
              "topics":{"type":"array","items":{"type":"object","properties":{
                "topic":{"type":"string"},"promptCount":{"type":"integer"}},"required":["topic","promptCount"]}}},
             "required":["summary","topics"]}
            """)!;
        var user = "Prompts (one per line):\n" + string.Join("\n", prompts.Select((p, i) => $"{i + 1}. {p.ReplaceLineEndings(" ")}"));

        var json = await _ollama.ChatJsonAsync(SelectedModel ?? _settings.Model, system, user, schema, cancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString() ?? string.Empty : string.Empty;
        var topics = new List<CollectionTopic>();
        if (root.TryGetProperty("topics", out var array) && array.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var topic in array.EnumerateArray())
            {
                if (topic.TryGetProperty("topic", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String)
                    topics.Add(new CollectionTopic
                    {
                        Topic = name.GetString() ?? string.Empty,
                        PromptCount = topic.TryGetProperty("promptCount", out var count) && count.TryGetInt32(out var n) ? n : 0
                    });
            }
        }
        return (summary, topics);
    }

    /// <summary>Set while an answer is being revealed; cancelled by <see cref="SkipStreaming"/>.</summary>
    private CancellationTokenSource? _skipStreaming;

    /// <summary>
    /// Shows the rest of the answer being revealed at once (Enter during the simulated streaming).
    /// Returns false when no answer is being revealed.
    /// </summary>
    public bool SkipStreaming()
    {
        if (_skipStreaming is not { IsCancellationRequested: false } skip)
            return false;
        skip.Cancel();
        return true;
    }

    /// <summary>
    /// Simulated streaming: shows <paramref name="content"/> word by word, each word staying the time it takes to say it
    /// (see <see cref="ReadingPace"/>); code blocks appear at once. Once <see cref="StreamedLines"/> non-empty lines are
    /// shown (code lines included), or on Cancel, the rest of the answer is shown at once. Without streaming
    /// (config.yml), the whole answer is shown immediately. Enter (<see cref="SkipStreaming"/>) shows the rest at once too.
    /// </summary>
    private async Task RevealAsync(ChatMessageViewModel answer, string content, CancellationToken cancellationToken)
    {
        if (!_settings.StreamingEnabled || IsBatchRunning)
        {
            answer.Content = content;
            return;
        }

        // streaming.speed divides the reading pace: 2 = twice as fast.
        var syllable = TimeSpan.FromMilliseconds(_settings.StreamingSyllableMs / Math.Clamp(_settings.StreamingSpeed, 0.1, 20));
        var shown = new System.Text.StringBuilder(content.Length);
        using var skip = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, skip.Token);
        _skipStreaming = skip;
        // While it is revealed the view only appends the words (no Markdown of the whole answer at each word: a long
        // answer would hold the interface, and every window with it).
        answer.IsRevealing = true;
        try
        {
            // The non-empty lines shown in full, counted as the words come (not by reading the whole text again at each word).
            var completed = 0;
            var lineHasText = false;
            foreach (var (piece, delay) in ReadingPace.Split(content, syllable))
            {
                shown.Append(piece);
                foreach (var c in piece)
                {
                    if (c == '\n')
                    {
                        if (lineHasText)
                            completed++;
                        lineHasText = false;
                    }
                    else if (!char.IsWhiteSpace(c))
                        lineHasText = true;
                }
                if (completed >= StreamedLines)
                    break;

                answer.Content = shown.ToString();
                // An answer read aloud moves the mouth with its sound only: Piper (and the spoken summary of an answer
                // with code) takes seconds before the voice starts, the words would move it long before.
                if (!IsSpeakingOrPreparing)
                    WordStreamed?.Invoke(piece);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancel or Enter during the display: the answer is complete, show it all.
        }
        finally
        {
            _skipStreaming = null;
        }

        answer.Content = content;
        answer.IsRevealing = false;
    }

    /// <summary>
    /// Keeps a new entity type (created by the ontology Auto fill) for the next ontology windows of this session.
    /// config.yml is not changed.
    /// </summary>
    private void AddEntityType(string type)
    {
        if (!_settings.EntityTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            _settings.EntityTypes.Add(type);
    }

    /// <summary>Switches every window to the given theme (<c>/theme:&lt;name&gt;</c>); it is saved to config.yml on exit.</summary>
    private void ApplyTheme(AppThemeKind kind)
    {
        AppTheme.Apply(kind);
        Status = $"Theme: {AppTheme.ToName(kind)}";
    }

    /// <summary>Answers <c>/getthemes</c> in the chat with the list of themes (local bubbles, not sent to the model).</summary>
    private void ShowThemes(string prompt)
    {
        var lines = AppTheme.All.Select(t =>
        {
            var name = AppTheme.ToName(t.Kind);
            var current = t.Kind == AppTheme.Current ? " *(current)*" : string.Empty;
            return $"- `{name}`: {t.Description}{current}";
        });

        var answer = $"**{AppTheme.All.Count} themes available**\n\n{string.Join("\n", lines)}\n\n"
                     + "Type `/theme:<themename>` to apply one, e.g. `/theme:matrix`. The last theme used is restored at startup.";

        AddLocalExchange(prompt, answer);
    }

    /// <summary>Answers <c>/help</c> or <c>/?</c> in the chat with every slash command (local bubbles, not sent to the model).</summary>
    private void ShowHelp(string prompt)
    {
        const string answer =
            "**Commands**\n\n"
            + "- `/help` or `/?`: shows this list\n"
            + "- `/json`: opens the HTTP JSON client\n"
            + "- `/cache`: opens the cache builder window (add and edit prompts with their completions; the model writes variations and evaluates every pair; PUBLISH writes every pair in the cache; TRANSLATE makes the document in another language; TO SEMANTIC CACHE adds the new pairs to the cache of the LLM gateway)\n"
            + "- `/symbol`: opens the symbolic pattern search / edit window\n"
            + "- `/ontology`: opens the ontology management window (node tree)\n"
            + "- `/diary` or `/journal`: opens the journal to write a new entry (Markdown editor: H1, H2, bold, italic, underline, code, links, pictures of the Pictures folder resized in the live preview; the previous entries stay hidden unless shown)\n"
            + "- `/completion`: searches the completions of Kaonomi (prompt and completion; empty: the latest ones) and shows a word cloud of the most frequent terms of the results (click a term to search it)\n"
            + "- `/relation`: the relations drawn between windows (red string of the link icons), searched and shown as a graph laid out by forces: an element per window and object (the same object of the same type is one element, its titles put together), a link per relation with its title; a tab of the workspace (`/nrelation`: its own window)\n"
            + "- `/fooocus`: creates images from a prompt with Fooocus (styles, performance, size, number, seed, model), saved in the Pictures folder, kaonomi/fooocus; the gallery shows them, Reuse puts the settings of an image back\n"
            + "- `/youtube`: YouTube videos of Kaonomi: paste the address of a video, its title and description are read from YouTube, add it; search the saved videos (title, description) and open them in the browser\n"
            + "- `/recorder`: opens the audio recorder (Godot chat only): the microphone recorded into ~/kaonomi/audio/<project>, with a VU meter, a spectrum analyser, the real-time formant analysis of the voice, and annotations on the moments of the recording\n"
            + "- `/music`: opens the music player (folders of music.directory in config.yml, the Music folder of the system by default; play, pause, seek, next / previous, shuffle, repeat, volume, cover and tags)\n"
            + "- `/picture` or `/pictures`: opens the picture library (folders and thumbnails of pictures.directory in config.yml, the Pictures folder of the system by default; preview, open, show in folder, rename, move to the trash)\n"
            + "- `/work`: opens a scientific calculator (keys or keyboard: + − × ÷ ^ % √ ! mod, sin cos tan and their inverses in degrees or radians, sinh cosh tanh, ln log log₂ eˣ 10ˣ ∛ |x|, π e Ans, parentheses; Enter for the result, history of the calculations)\n"
            + "- `/idea`: opens the idea window (list, search, create, edit and delete ideas); `/nidea` opens it in its own window\n"
            + "- `/token` or `/tokens`: opens the tokens dashboard (tokens and models consumed over the last hour)\n"
            + "- `/log` or `/logs`: opens the logs dashboard (last 10,000 logs of every application: key figures, activity, applications, errors, searchable table)\n"
            + "- `/system`: opens the system window (CPU, GPU, VRAM and memory activity with graphs, processor and graphics card models, drives)\n"
            + "- `/config`: opens the editor of config.yml (SAVE keeps the previous version as config-YYYYMMDD.config, RELOAD reads the file again; changes apply at the next start); its Presets drop-down (MARI, JARVIS, HAL, MARIEKILLEUSE, MUSUKO, KAONAMI, PEPPER, GREEN, JEROME, MARVIN, CIRCUIT, MOTUKO, KAONOMI, LIN and the presets saved from `/remote`) switches the voice and the animation at once\n"
            + "- `/intel`: search (in all the fields), create, clone, edit and delete the intels\n"
            + "- `/collectionplan`: search (in all the fields), create, clone, edit, activate and delete the collection plans of the web scraper; `/ncollectionplan` opens it in its own window\n"
            + "- `/activity`: the journal of the activities (each SAVE done in a window), with a search; OPEN opens the window again\n"
            + "- `/game:solitaire`: the morpion solitaire: add a point, draw a line of 4 cells through it (horizontal, vertical or diagonal), +1 per line, never over another line\n"
            + "- `/game:chess`: a game of chess against the LLM: you play White on the board (click a piece then its square, or drag it), the model of the chat (or the one chosen in the window) plays Black from the position and the list of its legal moves; every rule applies (castling, en passant, promotion, check, checkmate, stalemate, the fifty-move rule, threefold repetition, too little material); the list of every move in algebraic notation on the side, UNDO, RESIGN, PGN (copied); `/ngame:chess` opens it in its own window\n"
            + "- `/game:go`: a game of go against the LLM: you play Black (click a point), the model of the chat (or the one chosen in the window) plays White from the board and the list of its legal points; 9×9, 13×13 or 19×19, Chinese rules (captures, no suicide, no repeated position, area scoring, komi 7.5); PASS, two passes end the play, then click the dead groups and COUNT; the list of every move on the side, UNDO, RESIGN, SGF (copied); `/ngame:go` opens it in its own window\n"
            + "- `/iq`: IQ tests: number series, matrices of shapes, words (analogies, odd one out, in the language of the chat), memory of digits, each of 10 questions harder and harder with 60 s each, or the full test (24 questions); at the end the score by test, an indicative IQ (not a measured one) and the review of every question with its explanation; the results are kept\n"
            + "- `/calendar`: the calendar: the month (◀ ▶, TODAY), each day with its events and the alarms of /alarm; a click selects a day, a double-click adds an event; each event with its title, day, start and end (or the whole day), repeat (daily, weekly, monthly, yearly), reminder (rung by the chat as the alarms, with the title said by the avatar), colour and notes\n"
            + "- `/alarm` (or `/alarms`): the alarm clock: several alarms, each with its time, its label, once on a day or weekly on the days ticked (every day, Mon–Fri, weekend; one week in 1 to 4), its ringtone (Bell, Digital, Chime, Rising; ▶ TEST), its snooze and its label said by the avatar; they ring even with the window closed: STOP (Enter) or SNOOZE (S)\n"
            + "- `/immune`: the immune system: runs KAONOMI.ImmuneSystem, which lookson the machine (your home folder, the whole machine or one folder) for the images generated or edited by an AI (what they declare: Content Credentials, IPTC, the parameters of Stable Diffusion, ComfyUI, Midjourney…) and for the code in Office files (VBA and Excel 4.0 macros, DDE, OLE objects, Power Query, code written as text…), and reads the browser histories (Chrome, Edge, Brave, Arc, Firefox, Safari…) for the AI services visited (ChatGPT, Claude, Gemini, Midjourney, Suno… counted per month, no address kept) and the images and Office files downloaded; an immune cell shows the scan live, then the JSON report is shown with its key figures and charts, the AI images (thumbnails, clues, prompt) and the Office code; the reports are kept in ~/kaonomi/immune-system/reports; `/nimmune` opens it in its own window\n"
            + "- `/fractal`: the fractal generator: the classic fractals as examples (Mandelbrot and its valleys, Julia sets, Burning Ship, Tricorn, Multibrot, Newton, Phoenix, Magnet, a sine Julia set; the Koch snowflake, Sierpinski, the dragon, Hilbert, Peano and Gosper curves, plants; the Barnsley fern, Sierpinski carpet, maple leaf…), drawn live (ZOOM + / ZOOM −, ⇧ + drag: zoom on the square drawn, wheel: zoom, drag: move, right click: the Julia set of a point); make your own: a formula of z and c (`z^3 + c`, `sin(z) + c`, `fold(z)^2 + c`…) drawn by the GPU, an L-system (axiom, rules, angle, depth) or affine maps (IFS), with palettes; SAVE keeps them in ~/kaonomi/fractals, PNG in Pictures/kaonomi/fractal; `/nfractal` opens it in its own window\n"
            + "- `/folders` or `/folder`: the folders made with 📁+ in the header of a window (name, description, security level), searched as you type; a double-click on a folder opens every window kept in it (its picture and its data)\n"
            + "- `/project`: the projects: a name, a description, goals and tags; a search, NEW, SAVE, DELETE (marked deleted on the server, never erased)\n"
            + "- `/kanban`: kanban of the tasks: a column per status (todo, coding, test, check, deploy, archived; backlog first when there are some), each task in a card with its title and its type (bug, change, agent, infra)\n"
            + "- `/bnp`: the chatbot answers \"Let's go\" and the presentation Pictures/kaonomi/presentation/Digitribe-AI.pptx starts in full screen (PowerPoint, else Keynote)\n"
            + "- `/interview`: the interviews, stored in the search engine (searched on the left): the person interviewed (last name, first name, address, email, phone, passport or identity card number), the notes written with the editor of /work, and the recordings: ⏺ Record writes the microphone into a WAV file of ~/kaonomi/audio/interview; when it stops, Whisper transcribes it, a paragraph for what is said between two silences of a second or more, each paragraph stored in the search engine (found by /search); 🎙 Live writes the paragraphs while the person speaks; 📝 Transcribe does a recording again, ⤵ Insert puts the transcription in the notes; `/ninterview` opens it in its own window\n"
            + "- `/cv`: the CVs, stored in the search engine (searched on the left): the person, a summary, the experiences, the educations, the skills and the creations, with the CV as it reads beside; ✨ has the model of the chat write a text from the CV (the summary, a description, the skills; ↶ puts the former text back); ⧉ CLONE copies the CV into a new one and, in a popup, asks the adaptation to make: the model rewrites the copy for it (the employers, the schools and the dates stay); 📄 WORD and 📕 PDF export the CV in a modern look (Documents/kaonomi/cv); `/ncv` opens it in its own window\n"
            + "- `/wiki`: the wiki of Kaonomi: its pages stored in the search engine (searched on the left, the pages linking to the one open under them), each written with the editor of the journal (formatting, pictures copied into Pictures/kaonomi/wiki, links, code); a link to another page is wiki:slug (Insert › Wiki), following it opens the page or starts it; ◀ back, ⌂ home, SAVE (a new version each time), DELETE; `/nwiki` opens it in its own window\n"
            + "- `/bpmnai`: AIMN (from BPMN), the editor of diagrams of AI processes in the BPMN 2.0 notation, with the basic elements (start events, plain, message, timer or EventCollector; end events; task, user task, agent, virtual agent, sub-process; exclusive, parallel and inclusive gateways; data object, SearchEngine, Graphbase, Database, Vector Database, RAG, LLM, MODEL, REST API, MCP, text annotation) joined by sequence flows and associations: an element dragged from the palette is placed where it is dropped (or the palette then a click), the → handle of the selection dragged onto another joins them, double-click renames, right click for the menu, ⌫ deletes, ⌘Z undoes; saved as BPMN 2.0 XML (with its diagram interchange) in ~/kaonomi/bpmn, opened from any .bpmn file, PNG (in Pictures/kaonomi/bpmn) and COPY XML; a comment is written with the diagram, and ☁ SAVE puts it (XML and comment) in the search engine (the bpmn index, a new version each time), where the diagrams are searched and opened again (found by `/search` too); `/nbpmnai` opens it in its own window\n"
            + "- `/infra`: the editor of infrastructure diagrams, the window of /bpmnai with in its palette an LLM on the cloud, the LLM Gateway, a DGX Spark, an NVIDIA Jetson Nano, a PC, a Mac Studio, a MacBook Pro, a Mac mini, a server, a rack server, a Raspberry Pi, an Arduino, the Pepper robot, a tablet, a smartphone, a camera, a Wi-Fi camera, a microphone, a sensor (and text annotations), joined by links (named after the network or the protocol); same gestures as /bpmnai; saved as BPMN 2.0 XML in ~/kaonomi/infra, PNG in Pictures/kaonomi/infra, and in the search engine (the infra index, a new version each time; found by `/search` too); `/ninfra` opens it in its own window\n"
            + "- `/mindmap`: the mind mapping window, with the window of /bpmnai as its model: a central topic (the title of the map) and its branches, each topic with a text, a note and, on the branches, a colour; Tab or the + handle adds a child, Enter a sibling, double-click or F2 renames, a topic dragged onto another moves under it, LAYOUT lays the tree out again, ⌫ deletes the branch, ⌘Z undoes; saved as FreeMind XML (.mm, read by FreeMind, Freeplane, XMind) in ~/kaonomi/mindmap, opened from any .mm file, PNG (in Pictures/kaonomi/mindmap) and COPY XML; a comment is written with the map, and ☁ SAVE puts it (XML and comment) in the search engine (the mindmap index, a new version each time), where the maps are searched and opened again (found by `/search` too); `/nmindmap` opens it in its own window\n"
            + "- `/write`: the ghost writer, opened at full size, in four tabs: GENERAL (title, author, style, description, goals, audience, and the writings of the search engine), ACTORS (name, description, how they speak and think, motivation, goal, secret, weakness, strength, IQ, EQ, main character, Asperger, dyslexic), LOCATIONS (name, geolocation, description, history, culture, picture and wiki URLs) and CHAPTERS (title, description, perception goal, ambiance, ▲ ▼ to order them) and PUBLISH (a summary of the writing generated by the model of the chat, and the exports: MS Word, PDF, HTML, Blogger); a double-click on a chapter (or ✍ WRITE) opens the text area where its text is written (Esc or ✓ DONE closes it); ☁ SAVE puts the writing in the search engine (the ghostwriter index, a new version each time), where the writings are searched and opened again; `/nwrite` opens it in its own window\n"
            + "- `/cmd`: a console in a window: the shell of the system (cmd.exe) started in the home folder; a line typed runs at its prompt (↑ ↓ the lines typed before), STOP or Ctrl+C kills what runs and starts a new shell, CLEAR empties the screen; the shell is killed when the window closes; the programs that need a terminal of their own (a full-screen editor, a password prompt) do not work there; `/ncmd` opens it in its own window\n"
            + "- `/killall`: at once, no countdown: stops every application of Kaonomi all together (the server, the agents and the Fooocus API killed; the two engines asked to stop, killed after 3 s; Ollama stays) and closes the chat\n"
            + "- `/gateway`: KAONOMI.LLMGateway, the gateway in front of Ollama (llmGatewayUrl in config.yml, http://localhost:11435): its semantic cache (on, entity guard, embedding model, similarity, TTL; the cached answers, CLEAR CACHE), its router of the models by the entities of the prompt (the rules: entities any / all / none, model, priority), the last requests (model asked → chosen, HIT / MISS), and ANALYZE, what it would do with a prompt; it is started from `/process`\n"
            + "- `/process` or `/processes`: the processes of KAONOMI (the two engines, the server, the agents, the Fooocus API, Ollama): their state every 3 seconds, START, STOP and RESTART for each, START CORE (the engines then the server), STOP ALL; a process started there writes its output in ~/kaonomi/logs\n"
            + "- `/preset` or `/presets`: the editor of the presets of the avatar (voice and its effects, 3D animation, system prompt), stored in the search engine (config.yml keeps only the default preset): NEW, SAVE, DELETE, APPLY to the chat; `/npreset` opens it in its own window\n"
            + "- `/remote`: remote control of the chatbot: type a text that it says as its answer (voice and 3D animation), change the voice, its effects, the system prompt of the preset and the 3D animation live, save them as a new preset, and download, try or delete Piper voices\n"
            + "- `/n` instead of `/` before one of the commands above (`/ncache`, `/nlog`, `/nsystem`…): opens it in its own window instead of a tab\n"
            + "- `/fullchat`: the chat as in its full screen, in a window of the desktop: the 3D animation of the preset and the microphone (hold it to speak); the window moves, resizes and closes like the others\n"
            + "- `/setup` (Godot chat): the dependencies of the chat checked (speech to text, text to speech, the 3D animations, the services) with their installation; shown at the first start\n"
            + "- `/audiosettings` (Godot chat): a popup to choose the output (the voice and the sounds of the chat) and the microphone (the voice input), among the devices of the system; kept for the next start\n"
            + "- `/search`: a loose search of a few words in the indices of KAONOMI at once (cache, completion, dialog, diary, document, idea, imageanalysis, intel, ontology, relation, taskitem, youtubevideo): a typo or the start of a word is enough; the hits grouped by index, the selected one shown as JSON\n"
            + "- `/closeall`: closes every window open on the desktop\n"
            + "- `/mosaic` (Godot chat): every window comes to the front and they share the whole desktop side by side, as a grid (a window smaller than its content scrolls)\n"
            + "- `/windows` (Godot chat): every window turns on a ring in 3D, from the back to the front in perspective; a click on one brings it to the front, the wheel or ← → turn the ring, Escape (or /windows again) puts them back\n"
            + "- `/think` (Godot chat): the screen goes black and the last hour appears: a cloud of the words of the logs and completions, the pictures touched in the hour as thumbnails; a key or a click brings the chat back\n"
            + "- `/getthemes`: lists every theme\n"
            + "- `/theme:<themename>`: applies a theme to every window, e.g. `/theme:matrix` (restored at startup)\n"
            + "- `/bye`, `/quit` or `/exit`: says bye, and after 10 seconds (Cancel to stay) stops the search engine, then the Mongo engine (byeStopsEngines in config.yml), then closes the application\n\n"
            + "Any other text starting with `/` is answered \"unsupported command!\" and never sent to the model; "
            + "anything else is sent to the model. Enter sends, Shift+Enter inserts a new line, Esc stops the voice reading the answer.";

        AddLocalExchange(prompt, answer);
    }

    /// <summary>Adds the command and its answer as local bubbles: shown and exported, never sent to the model.</summary>
    /// <summary>
    /// Answers <c>/bye</c>, <c>/quit</c> or <c>/exit</c> with "Bye", counts down in the status bar, then asks the view
    /// to shut the application down. The Cancel button (the send command is still running) aborts the exit.
    /// </summary>
    /// <summary>
    /// <c>/bnp</c>: the chatbot answers "Let's go" (voice and animation) and the presentation
    /// Pictures/kaonomi/presentation/Digitribe-AI.pptx starts in full screen (<see cref="SlideShow"/>).
    /// </summary>
    private async Task PresentAsync(string prompt)
    {
        var path = SlideShow.BnpPresentation;
        if (!System.IO.File.Exists(path))
        {
            AddLocalExchange(prompt, $"The presentation {path} does not exist.");
            return;
        }
        Messages.Add(new ChatMessageViewModel(ChatRole.User, prompt) { IsLocal = true });
        var said = SayAsync("Let's go");
        Status = "Starting the presentation…";
        var error = await SlideShow.StartAsync(path);
        await said;
        Status = error ?? $"Presentation {System.IO.Path.GetFileName(path)} started";
        if (error is null)
            ActivityLog.Info("Chat", $"Presentation {path} started");
        else
        {
            AddLocalExchange(prompt, error);
            ActivityLog.Error("Chat", error);
        }
    }

    /// <summary>
    /// <c>/killall</c>: at once and as fast as it can, every application of Kaonomi is stopped, all together (the
    /// server, the agents and the Fooocus API killed; the two engines asked to stop, killed after 3 s; Ollama, an
    /// application of the system, stays), then the chat closes. No countdown.
    /// </summary>
    private async Task KillAllAsync(string prompt, CancellationToken cancellationToken)
    {
        AddLocalExchange(prompt, "Stopping every application of Kaonomi, then closing the chat.");
        Status = "/killall · stopping everything…";
        ActivityLog.Info("Chat", "/killall: every application of Kaonomi is stopped, then the chat closes");
        // The log of the chat goes to the server: what is left of it is sent before the server goes (1 s at most).
        ActivityLog.Flush(TimeSpan.FromSeconds(1));
        using (var processes = new ProcessWindowViewModel(_settings))
        {
            try
            {
                await processes.KillEverythingAsync();
                Status = "/killall · " + processes.Status;
            }
            catch (Exception ex)
            {
                Status = "/killall · not everything stopped: " + ex.Message;
            }
        }
        ExitRequested?.Invoke();
    }

    private async Task ExitAsync(string prompt, CancellationToken cancellationToken)
    {
        AddLocalExchange(prompt, "Bye");
        var previousStatus = Status;
        try
        {
            // From 10 to 1, a second each.
            for (var remaining = ExitDelaySeconds; remaining >= 1; remaining--)
            {
                Status = $"Closing in {remaining} s… (Cancel to stay)";
                ExitCountdown?.Invoke(remaining);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            if (_settings.ByeStopsEngines)
            {
                // The engines of KAONOMI first: the search engine, then the Mongo engine; the chat closes after.
                Status = "Stopping the search engine…";
                ActivityLog.Info("Chat", await EngineShutdown.StopSearchEngineAsync(_settings.SearchEngineUrl, cancellationToken));
                Status = "Stopping the Mongo engine…";
                ActivityLog.Info("Chat", await EngineShutdown.StopMongoEngineAsync(_settings.MongoEngineAddress, cancellationToken));
            }

            ExitRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            ExitCountdown?.Invoke(null);
            Status = previousStatus;
            AddLocalExchange("Cancel", "Exit cancelled.");
            ActivityLog.Info("Chat", "Exit cancelled during the countdown");
        }
    }

    /// <summary>
    /// Prepares the like / dislike popup for an answer: the prompt is the user message the answer replied to,
    /// the comment records the answer's stats (model, tokens, duration).
    /// </summary>
    public EvaluationDialogViewModel CreateEvaluation(ChatMessageViewModel answer, bool isNegative)
    {
        var index = Messages.IndexOf(answer);
        var prompt = Messages.Take(Math.Max(index, 0)).LastOrDefault(m => m.IsUser && !m.IsLocal)?.Content ?? string.Empty;

        return new EvaluationDialogViewModel(_assistant, new EvaluationEntry
        {
            AppName = "KAONOMI.Chat",
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            Comment = answer.Stats,
            Prompt = prompt,
            Completion = answer.Content,
            IsNegativeFeedback = isNegative
        });
    }

    private void AddLocalExchange(string prompt, string answer)
    {
        Messages.Add(new ChatMessageViewModel(ChatRole.User, prompt) { IsLocal = true });
        Messages.Add(new ChatMessageViewModel(ChatRole.Assistant, answer) { IsLocal = true });
        OnPropertyChanged(nameof(HasNoMessages));
    }

    /// <summary>Clears the transcript and cancels an in-flight request.</summary>
    [RelayCommand]
    private void NewConversation()
    {
        // A new conversation for the server too: its context starts empty.
        _conversationId = Guid.NewGuid().ToString("N");
        if (SendCommand.IsRunning)
            SendCommand.Cancel();

        ActivityLog.Info("Chat", $"New conversation ({Messages.Count} message(s) cleared)");
        StopSpeaking();
        Messages.Clear();
        OnPropertyChanged(nameof(HasNoMessages));
    }

    /// <summary>Snapshot of the conversation for the JSON and Word exports (pending answers are skipped).</summary>
    public ChatExport BuildExport() => new()
    {
        Model = SelectedModel,
        ServerUrl = ServerUrl,
        SystemPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? null : SystemPrompt.Trim(),
        Messages = Messages
            .Where(m => !m.IsPending)
            .Select(m => new ChatExportMessage
            {
                Role = m.ApiRole,
                Author = m.Author,
                Timestamp = m.Timestamp,
                Content = m.Content,
                Stats = m.Stats,
                IsError = m.IsError
            })
            .ToList()
    };

    /// <summary>
    /// Raised for each word shown by the simulated streaming (moves the mouth of the 3D animation), unless the answer
    /// is read aloud: the mouth then follows the voice.
    /// </summary>
    public event Action<string>? WordStreamed;

    /// <summary>Raised when code blocks must be displayed in a dedicated window.</summary>
    public event Action<CodeWindowViewModel>? CodeWindowRequested;

    /// <summary>Raised by <c>/search</c> (a tab of the workspace) or <c>/nsearch</c> (its own window): the search in every index.</summary>
    public event Action<SearchWindowViewModel, bool>? SearchWindowRequested;

    /// <summary>The client of the Kaonomi server (for the hosts that open a document found by the search).</summary>
    public KaonomiAssistantService Assistant => _assistant;

    /// <summary>The analysis of the image at <paramref name="path"/>, as the picture tools make it.</summary>
    public ImageAnalysisViewModel CreateImageAnalysis(string path) => new(path, _assistant, _ollama, _settings.VisionModel);

    /// <summary>Raised when the user types the <c>/json</c> command.</summary>
    public event Action<JsonClientWindowViewModel, bool>? JsonClientWindowRequested;

    /// <summary>Raised when the user types the <c>/symbol</c> command.</summary>
    public event Action<SymbolicPatternWindowViewModel, bool>? SymbolicPatternWindowRequested;

    /// <summary>Raised when the user types the <c>/cache</c> command.</summary>
    public event Action<CacheWindowViewModel, bool>? CacheWindowRequested;

    /// <summary>Raised when the user types the <c>/ontology</c> command.</summary>
    public event Action<OntologyWindowViewModel, bool>? OntologyWindowRequested;

    /// <summary>Raised when the user types <c>/diary</c> or <c>/journal</c> (<c>/ndiary</c>, <c>/njournal</c>: in its own window).</summary>
    public event Action<JournalWindowViewModel, bool>? JournalWindowRequested;

    /// <summary>Raised when the user types <c>/completion</c> (<c>/ncompletion</c>: in its own window).</summary>
    public event Action<CompletionWindowViewModel, bool>? CompletionWindowRequested;

    /// <summary>Raised when the user types <c>/fooocus</c> (<c>/nfooocus</c>: in its own window).</summary>
    public event Action<FooocusWindowViewModel, bool>? FooocusWindowRequested;

    /// <summary>Raised when the user types <c>/youtube</c> (<c>/nyoutube</c>: in its own window).</summary>
    public event Action<YoutubeWindowViewModel, bool>? YoutubeWindowRequested;

    /// <summary>Raised when the user types <c>/music</c> (<c>/nmusic</c>: in its own window).</summary>
    public event Action<MusicPlayerViewModel, bool>? MusicWindowRequested;

    /// <summary>Raised when the user types the <c>/project</c> command (the projects; the kanban of the tasks is <c>/kanban</c>).</summary>
    public event Action<ProjectWindowViewModel, bool>? ProjectWindowRequested;

    /// <summary>Raised when the user types the <c>/recorder</c> command (the view exists in the Godot chat only).</summary>
    public event Action<RecorderWindowViewModel, bool>? RecorderWindowRequested;

    /// <summary>Raised when the user types <c>/picture</c> or <c>/pictures</c> (<c>/npictures</c>: in its own window).</summary>
    public event Action<PictureLibraryViewModel, bool>? PictureWindowRequested;

    /// <summary>Raised when the user types the <c>/work</c> command (<c>/nwork</c>: in its own window).</summary>
    public event Action<CalculatorViewModel, bool>? CalculatorWindowRequested;

    /// <summary>Raised when the user types the <c>/idea</c> command (<c>/nidea</c>: in its own window).</summary>
    public event Action<IdeaWindowViewModel, bool>? IdeaWindowRequested;

    /// <summary>3D animation of the Kaonomi band (<c>animation</c> in config.yml, or the last preset applied).</summary>
    public SceneAnimation Animation => _settings.Animation;

    /// <summary>Folder where the "Save prompt" / "Load prompt" buttons write and read prompts (config.yml).</summary>
    public string PromptDirectory => _settings.ResolvedPromptDirectory;

    /// <summary>True when a prompt loaded with "Load prompt" is sent at once (<c>prompts.directprompt</c> in config.yml).</summary>
    public bool DirectPrompt => _settings.DirectPrompt;

    /// <summary>
    /// Opens the system window (<c>/system</c>, and automatically when the chat opens); its measures are sent to
    /// <c>PUT /systemlog</c> while it is open; <paramref name="detached"/> (<c>/nsystem</c>) opens it in its own window.
    /// </summary>
    public void OpenSystemWindow(bool detached = false) => SystemWindowRequested?.Invoke(new SystemWindowViewModel(_assistant), detached);

    /// <summary>Raised when the user types <c>/token</c> or <c>/tokens</c>.</summary>
    public event Action<TokenDashboardViewModel, bool>? TokenDashboardWindowRequested;

    /// <summary>Raised when the user types <c>/config</c> (or <c>/nconfig</c>): the editor of config.yml.</summary>
    public event Action<ConfigWindowViewModel, bool>? ConfigWindowRequested;

    /// <summary>Raised when the user types <c>/folders</c> or <c>/folder</c>: the folders of windows.</summary>
    public event Action<FoldersWindowViewModel, bool>? FoldersWindowRequested;

    /// <summary>Raised when the user types the <c>/intel</c> command.</summary>
    public event Action<IntelWindowViewModel, bool>? IntelWindowRequested;

    /// <summary>Raised when the user types the <c>/collectionplan</c> command.</summary>
    public event Action<CollectionPlanWindowViewModel, bool>? CollectionPlanWindowRequested;

    /// <summary>Raised when the user types the <c>/game:solitaire</c> command: the morpion solitaire.</summary>
    public event Action<Game1WindowViewModel, bool>? Game1WindowRequested;

    /// <summary>Raised when the user types the <c>/fractal</c> command: the fractal generator.</summary>
    public event Action<FractalWindowViewModel, bool>? FractalWindowRequested;

    /// <summary>Raised when the user types the <c>/game:chess</c> command: a game of chess against the LLM.</summary>
    public event Action<ChessWindowViewModel, bool>? ChessWindowRequested;

    /// <summary>Raised when the user types the <c>/game:go</c> command: a game of go against the LLM.</summary>
    public event Action<GoWindowViewModel, bool>? GoWindowRequested;

    /// <summary>Raised when the user types the <c>/immune</c> command: the scan for AI images and Office code.</summary>
    public event Action<ImmuneWindowViewModel, bool>? ImmuneWindowRequested;

    /// <summary>Raised when the user types the <c>/alarm</c> command: the alarm clock.</summary>
    public event Action<AlarmWindowViewModel, bool>? AlarmWindowRequested;

    /// <summary>Raised when the user types the <c>/calendar</c> command: the calendar.</summary>
    public event Action<CalendarWindowViewModel, bool>? CalendarWindowRequested;

    /// <summary>Raised when the user types the <c>/iq</c> command: the IQ tests.</summary>
    public event Action<IqWindowViewModel, bool>? IqWindowRequested;

    /// <summary>Raised when the user types the <c>/activity</c> command: the journal of the activities.</summary>
    public event Action<ActivityWindowViewModel, bool>? ActivityWindowRequested;

    /// <summary>Raised when the user types the <c>/kanban</c> command.</summary>
    public event Action<KanbanWindowViewModel, bool>? KanbanWindowRequested;

    /// <summary>Raised when the user types the <c>/remote</c> command.</summary>
    public event Action<RemoteWindowViewModel, bool>? RemoteWindowRequested;

    /// <summary>Raised by <c>/preset</c> (<c>/presets</c>): the editor of the presets stored in the search engine.</summary>
    public event Action<PresetWindowViewModel, bool>? PresetWindowRequested;

    /// <summary>Raised by <c>/wiki</c>: the wiki of Kaonomi, its pages stored in the search engine.</summary>
    public event Action<WikiWindowViewModel, bool>? WikiWindowRequested;

    /// <summary>Raised by <c>/interview</c>: the interviews, recorded as WAV and transcribed paragraph by paragraph into the search engine.</summary>
    public event Action<InterviewWindowViewModel, bool>? InterviewWindowRequested;

    /// <summary>Raised by <c>/cv</c>: the CVs, written with the help of the model of the chat, cloned and rewritten for a purpose.</summary>
    public event Action<CvWindowViewModel, bool>? CvWindowRequested;

    /// <summary>Raised by <c>/bpmnai</c>: AIMN (from BPMN), the editor of diagrams of AI processes in the BPMN 2.0 notation.</summary>
    public event Action<BpmnWindowViewModel, bool>? BpmnWindowRequested;

    /// <summary>Raised by <c>/infra</c>: the editor of infrastructure diagrams (the editor of /bpmnai with the machines and devices).</summary>
    public event Action<InfraWindowViewModel, bool>? InfraWindowRequested;

    /// <summary>Raised by <c>/mindmap</c>: the mind mapping window.</summary>
    public event Action<MindmapWindowViewModel, bool>? MindmapWindowRequested;

    /// <summary>Raised by <c>/write</c>: the ghost writer (general information, actors, locations, chapters).</summary>
    public event Action<GhostWriterWindowViewModel, bool>? GhostWriterWindowRequested;

    /// <summary>Raised by <c>/cmd</c>: a console (cmd.exe) in a window.</summary>
    public event Action<ConsoleWindowViewModel, bool>? ConsoleWindowRequested;

    /// <summary>Raised by <c>/process</c> (<c>/processes</c>): the processes of KAONOMI, their state, START / STOP / RESTART.</summary>
    public event Action<ProcessWindowViewModel, bool>? ProcessWindowRequested;

    /// <summary>Raised by <c>/gateway</c>: the window that drives KAONOMI.LLMGateway.</summary>
    public event Action<GatewayWindowViewModel, bool>? GatewayWindowRequested;

    /// <summary>Raised when the user types <c>/log</c> or <c>/logs</c>.</summary>
    public event Action<LogDashboardViewModel, bool>? LogDashboardWindowRequested;

    /// <summary>Raised when the user types the <c>/system</c> command or when the chat opens.</summary>
    public event Action<SystemWindowViewModel, bool>? SystemWindowRequested;

    /// <summary>Raised when the user types <c>/fullchat</c>.</summary>
    public event Action<bool>? FullChatRequested;

    /// <summary>Raised when the user types <c>/setup</c>: the screen of the dependencies of the chat (the Godot chat).</summary>
    public event Action? SetupRequested;

    /// <summary>Raised when the user types <c>/think</c>: the black screen of the last hour (the Godot chat).</summary>
    public event Action? ThinkRequested;

    /// <summary>Raised when the user types <c>/windows</c>: the windows turning on a ring in 3D (the Godot chat).</summary>
    public event Action? WindowsRequested;

    /// <summary>Raised when the user types <c>/mosaic</c>: every window side by side over the desktop (the Godot chat).</summary>
    public event Action? MosaicRequested;

    /// <summary>Raised when the user types <c>/audiosettings</c>: the popup to choose the output and the microphone (the Godot chat).</summary>
    public event Action? AudioSettingsRequested;

    /// <summary>Raised when the user types <c>/closeall</c>: every window of the desktop closes (the Godot chat).</summary>
    public event Action? CloseAllRequested;

    /// <summary>The settings of the chat (for the hosts).</summary>
    public ChatSettings Settings => _settings;

    /// <summary>Raised by <c>/relation</c> (a tab of the workspace) or <c>/nrelation</c> (its own window): the relations view.</summary>
    public event Action<RelationGraphViewModel, bool>? RelationGraphRequested;

    /// <summary>
    /// Raised each second of the countdown of <c>/bye</c>, <c>/quit</c> or <c>/exit</c>: the seconds left, from 10 to 1;
    /// null when it is cancelled.
    /// </summary>
    public event Action<int?>? ExitCountdown;

    /// <summary>Raised when <c>/bye</c>, <c>/quit</c> or <c>/exit</c> has finished its countdown.</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// Opens the tool of <paramref name="command"/> (<c>/json</c>, <c>/log</c>…): in a tab of the workspace, or in its
    /// own window when <paramref name="detached"/>. Returns false when it is not a tool command.
    /// </summary>
    public bool OpenTool(string command, bool detached)
    {
        if (IsCommand(command, SearchCommand))
        {
            SearchWindowRequested?.Invoke(new SearchWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, JsonCommand))
        {
            JsonClientWindowRequested?.Invoke(new JsonClientWindowViewModel(_settings), detached);
            return true;
        }

        if (IsCommand(command, CacheCommand))
        {
            CacheWindowRequested?.Invoke(new CacheWindowViewModel(_assistant, _ollama, () => SelectedModel, _settings.LlmGatewayUrl), detached);
            return true;
        }

        if (IsCommand(command, SymbolCommand))
        {
            // The regex assistant uses the model currently selected in this window.
            SymbolicPatternWindowRequested?.Invoke(new SymbolicPatternWindowViewModel(_assistant, _ollama, () => SelectedModel, _settings.ResolvedRegexDirectory), detached);
            return true;
        }

        if (IsCommand(command, OntologyCommand))
        {
            // The ontology "Auto" fill uses the model currently selected in this window.
            OntologyWindowRequested?.Invoke(new OntologyWindowViewModel(
                _assistant, _ollama, () => SelectedModel, _settings.EntityTypes, AddEntityType), detached);
            return true;
        }

        if (IsCommand(command, DiaryCommand) || IsCommand(command, JournalCommand))
        {
            // The editor of the journal is the one of /work: its right-click menu uses the model selected in the chat, and a
            // double-click on a picture opens it in the image analysis window.
            JournalWindowRequested?.Invoke(new JournalWindowViewModel(_assistant, _settings.ResolvedPicturesDirectory, _ollama, () => SelectedModel ?? _settings.Model)
            {
                CreateAnalysis = path => new ImageAnalysisViewModel(path, _assistant, _ollama, _settings.VisionModel)
            }, detached);
            return true;
        }

        if (IsCommand(command, CompletionCommand))
        {
            CompletionWindowRequested?.Invoke(new CompletionWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, RelationCommand))
        {
            RelationGraphRequested?.Invoke(new RelationGraphViewModel(_assistant)
            {
                // A double-click on an element opens its picture or its entity.
                CreateAnalysis = path => new ImageAnalysisViewModel(path, _assistant, _ollama, _settings.VisionModel),
                CreateOntology = () => new OntologyWindowViewModel(_assistant, _ollama, () => SelectedModel, _settings.EntityTypes, AddEntityType)
            }, detached);
            return true;
        }

        if (IsCommand(command, FooocusCommand))
        {
            FooocusWindowRequested?.Invoke(new FooocusWindowViewModel(_assistant)
            {
                // A click on the image opens it in the image analysis window, as a double-click in /pictures.
                CreateAnalysis = path => new ImageAnalysisViewModel(path, _assistant, _ollama, _settings.VisionModel)
            }, detached);
            return true;
        }

        if (IsCommand(command, YoutubeCommand))
        {
            YoutubeWindowRequested?.Invoke(new YoutubeWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, RecorderCommand))
        {
            // The microphone is opened by the view once it is shown: a front end without the view opens nothing.
            if (RecorderWindowRequested is null)
            {
                Status = "/recorder (the audio recorder) is only in KAONOMI.ChatGodot";
                return true;
            }
            RecorderWindowRequested.Invoke(new RecorderWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, MusicCommand))
        {
            MusicWindowRequested?.Invoke(new MusicPlayerViewModel(_settings.ResolvedMusicDirectory), detached);
            return true;
        }

        if (IsCommand(command, PictureCommand) || IsCommand(command, PicturesCommand))
        {
            PictureWindowRequested?.Invoke(new PictureLibraryViewModel(_settings.ResolvedPicturesDirectory)
            {
                CreateAnalysis = path => new ImageAnalysisViewModel(path, _assistant, _ollama, _settings.VisionModel)
            }, detached);
            return true;
        }

        if (IsCommand(command, CalculatorCommand))
        {
            CalculatorWindowRequested?.Invoke(new CalculatorViewModel(_settings.ResolvedCalculatorGraphDirectory, _assistant, _ollama, () => SelectedModel ?? _settings.Model, _settings.VisionModel)
            {
                // A double-click on a picture of the tape opens it in the image analysis window, as in /pictures.
                CreateAnalysis = path => new ImageAnalysisViewModel(path, _assistant, _ollama, _settings.VisionModel)
            }, detached);
            return true;
        }

        if (IsCommand(command, IdeaCommand))
        {
            IdeaWindowRequested?.Invoke(new IdeaWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, TokenCommand) || IsCommand(command, TokensCommand))
        {
            TokenDashboardWindowRequested?.Invoke(new TokenDashboardViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, LogCommand) || IsCommand(command, LogsCommand))
        {
            LogDashboardWindowRequested?.Invoke(new LogDashboardViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, SystemCommand))
        {
            OpenSystemWindow(detached);
            return true;
        }

        if (IsCommand(command, ConfigCommand))
        {
            ConfigWindowRequested?.Invoke(new ConfigWindowViewModel(ApplyPreset), detached);
            return true;
        }

        if (IsCommand(command, FoldersCommand) || IsCommand(command, FolderCommand))
        {
            FoldersWindowRequested?.Invoke(new FoldersWindowViewModel(), detached);
            return true;
        }

        if (IsCommand(command, IntelCommand))
        {
            IntelWindowRequested?.Invoke(new IntelWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, CollectionPlanCommand))
        {
            // RUN writes its analysis with the model selected in the chat.
            CollectionPlanWindowRequested?.Invoke(new CollectionPlanWindowViewModel(_assistant, ollama: _ollama, model: () => SelectedModel ?? _settings.Model, language: _settings.Language, visionModel: _settings.VisionModel), detached);
            return true;
        }

        if (IsCommand(command, Game1Command))
        {
            Game1WindowRequested?.Invoke(new Game1WindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, FractalCommand))
        {
            FractalWindowRequested?.Invoke(new FractalWindowViewModel(), detached);
            return true;
        }

        if (IsCommand(command, ChessCommand))
        {
            // Black is played by the model selected in the chat (it can be changed in the window).
            ChessWindowRequested?.Invoke(new ChessWindowViewModel(_ollama, Models, SelectedModel ?? _settings.Model, _settings.Language), detached);
            return true;
        }

        if (IsCommand(command, GoCommand))
        {
            // White is played by the model selected in the chat (it can be changed in the window).
            GoWindowRequested?.Invoke(new GoWindowViewModel(_ollama, Models, SelectedModel ?? _settings.Model, _settings.Language), detached);
            return true;
        }

        if (IsCommand(command, ImmuneCommand))
        {
            ImmuneWindowRequested?.Invoke(new ImmuneWindowViewModel(), detached);
            return true;
        }

        if (IsCommand(command, AlarmCommand) || IsCommand(command, AlarmsCommand))
        {
            AlarmWindowRequested?.Invoke(new AlarmWindowViewModel(), detached);
            return true;
        }

        if (IsCommand(command, CalendarCommand))
        {
            CalendarWindowRequested?.Invoke(new CalendarWindowViewModel(), detached);
            return true;
        }

        if (IsCommand(command, IqCommand))
        {
            // The questions of words in the language of the chat.
            IqWindowRequested?.Invoke(new IqWindowViewModel(VoiceLanguage), detached);
            return true;
        }

        if (IsCommand(command, ActivityCommand))
        {
            ActivityWindowRequested?.Invoke(new ActivityWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, ProjectCommand))
        {
            ProjectWindowRequested?.Invoke(new ProjectWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, KanbanCommand))
        {
            KanbanWindowRequested?.Invoke(new KanbanWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, RemoteCommand))
        {
            RemoteWindowRequested?.Invoke(new RemoteWindowViewModel(this), detached);
            return true;
        }

        if (IsCommand(command, PresetCommand) || IsCommand(command, PresetsCommand))
        {
            PresetWindowRequested?.Invoke(new PresetWindowViewModel(this, _assistant), detached);
            return true;
        }

        if (IsCommand(command, WikiCommand))
        {
            // The editor of the wiki is the one of the journal: the model of the chat for its AI tools, the image
            // analysis window for a double-click on a picture.
            WikiWindowRequested?.Invoke(new WikiWindowViewModel(_assistant, _settings.ResolvedPicturesDirectory, _ollama, () => SelectedModel ?? _settings.Model)
            {
                CreateAnalysis = path => new ImageAnalysisViewModel(path, _assistant, _ollama, _settings.VisionModel)
            }, detached);
            return true;
        }

        if (IsCommand(command, ProcessCommand) || IsCommand(command, ProcessesCommand))
        {
            ProcessWindowRequested?.Invoke(new ProcessWindowViewModel(_settings), detached);
            return true;
        }

        if (IsCommand(command, GatewayCommand))
        {
            GatewayWindowRequested?.Invoke(new GatewayWindowViewModel(_settings.LlmGatewayUrl), detached);
            return true;
        }

        if (IsCommand(command, InfraCommand))
        {
            InfraWindowRequested?.Invoke(new InfraWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, BpmnCommand))
        {
            BpmnWindowRequested?.Invoke(new BpmnWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, MindmapCommand))
        {
            MindmapWindowRequested?.Invoke(new MindmapWindowViewModel(_assistant), detached);
            return true;
        }

        if (IsCommand(command, ConsoleCommand))
        {
            ConsoleWindowRequested?.Invoke(new ConsoleWindowViewModel(), detached);
            return true;
        }

        if (IsCommand(command, CvCommand))
        {
            CvWindowRequested?.Invoke(new CvWindowViewModel(_assistant, _ollama, () => SelectedModel ?? _settings.Model, () => VoiceLanguage), detached);
            return true;
        }

        if (IsCommand(command, InterviewCommand))
        {
            // The notes are written with the editor of /work; Whisper (the model and the language of the voice input) transcribes the recordings.
            InterviewWindowRequested?.Invoke(new InterviewWindowViewModel(_assistant, _settings.ResolvedPicturesDirectory, _ollama, () => SelectedModel ?? _settings.Model,
                _settings.SpeechModel, () => VoiceLanguage), detached);
            return true;
        }

        if (IsCommand(command, WriteCommand))
        {
            // The chapters are written with the editor of the journal: the model of the chat for its AI tools.
            GhostWriterWindowRequested?.Invoke(new GhostWriterWindowViewModel(_assistant, _ollama, () => SelectedModel ?? _settings.Model,
                _settings.SpeechModel, () => VoiceLanguage), detached);
            return true;
        }

        return false;
    }

    /// <summary>Opens the code viewer for the given message's snippets.</summary>
    [RelayCommand]
    private void ShowCode(ChatMessageViewModel? message)
    {
        if (message is not { HasCode: true })
            return;

        ActivityLog.Info("Chat", $"Code window opened ({message.CodeSnippets.Count} block(s))");
        CodeWindowRequested?.Invoke(new CodeWindowViewModel(message.CodeSnippets));
    }

    /// <summary>Shows or hides the system prompt editor panel.</summary>
    [RelayCommand]
    private void ToggleSystemPrompt() => IsSystemPromptVisible = !IsSystemPromptVisible;

    /// <summary>
    /// The system prompt sent: the system prompt of the preset in use (set in /preset, <see cref="ActivePersona"/>),
    /// whichever way the preset was chosen (the picker, APPLY of /preset, /remote, /config, CHANGE:NAME in an answer),
    /// then the system prompt of the chat added to it (the SYSTEM PROMPT box, systemprompt.txt: what every preset
    /// shares); null when both are empty.
    /// </summary>
    private string? FullSystemPrompt()
    {
        var system = SystemPrompt?.Trim() ?? string.Empty;
        var persona = ActivePersona;
        // The system prompt of the preset already in the one of the chat (an old APPLY of /preset put it there) is not repeated.
        if (persona.Length > 0 && system.Contains(persona, StringComparison.Ordinal))
            persona = string.Empty;
        var parts = new[] { persona, system }.Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private List<AssistantMessage> BuildHistory()
    {
        var history = new List<AssistantMessage>();
        if (FullSystemPrompt() is { } system)
            history.Add(new AssistantMessage("system", system));

        // An answer disliked and asked again is left out: the answer of the other model stands for it.
        var exchanges = Messages.Where(m => !m.IsError && !m.IsLocal && !m.AskedAgain && !string.IsNullOrEmpty(m.Content)).ToList();
        var prompt = exchanges.LastOrDefault(m => m.IsUser);
        if (prompt is null)
            return history;

        // Prompt collection: each prompt is sent on its own (system prompt + the prompt), without the previous
        // exchanges. Otherwise every request would carry the whole run (360 messages after 180 prompts), making the
        // dialog documents heavier at each step on the server and exceeding the context of the model.
        var previous = IsBatchRunning ? new List<ChatMessageViewModel>() : LastExchanges(exchanges.Take(exchanges.LastIndexOf(prompt)).ToList());
        history.Add(new AssistantMessage("user", WithPreviousDiscussions(previous, AddressedToPreset(prompt.Content))));
        return history;
    }

    /// <summary>
    /// The prompt addressed to the preset in use: its name and "please" before it ("JARVIS please …"), typed or said
    /// (the speech recognised is sent the same way); the prompt alone when the voice and the animation in use match no
    /// preset. The chat shows the prompt as it was typed.
    /// </summary>
    private string AddressedToPreset(string prompt) =>
        SelectedPreset?.Name.Trim() is { Length: > 0 } name ? $"{name} please {prompt}" : prompt;

    /// <summary>Choices of the drop-down above the chat: 0 to 20 previous exchanges.</summary>
    public IReadOnlyList<int> HistoryExchangeChoices { get; } = Enumerable.Range(0, ChatSettings.MaxHistoryExchanges + 1).ToList();

    /// <summary>
    /// Number of previous exchanges (a prompt and its answer) written into the prompt (drop-down above the chat;
    /// <c>historyExchanges</c> in config.yml, written back at each change). 0 sends the prompt alone.
    /// </summary>
    public int HistoryExchanges
    {
        get => _settings.HistoryExchanges;
        set
        {
            var count = Math.Clamp(value, 0, ChatSettings.MaxHistoryExchanges);
            if (count == _settings.HistoryExchanges)
                return;
            _settings.HistoryExchanges = count;
            OnPropertyChanged();
            ChatSettings.SaveHistoryExchanges(count);
            ActivityLog.Info("Chat", $"Previous exchanges in the prompt: {count}");
        }
    }

    /// <summary>The messages of the last <see cref="HistoryExchanges"/> prompts and their answers.</summary>
    private List<ChatMessageViewModel> LastExchanges(List<ChatMessageViewModel> messages)
    {
        var count = HistoryExchanges;
        if (count == 0)
            return [];
        var prompts = messages.Select((m, i) => (m, i)).Where(x => x.m.IsUser).Select(x => x.i).ToList();
        return prompts.Count <= count
            ? messages
            : messages.Skip(prompts[^count]).ToList();
    }

    /// <summary>
    /// The prompt preceded by the previous exchanges, each one saying who said it, under the instruction
    /// "Discussions précédentes"; the prompt alone when there is none.
    /// </summary>
    private static string WithPreviousDiscussions(IReadOnlyList<ChatMessageViewModel> previous, string prompt)
    {
        if (previous.Count == 0)
            return prompt;

        var text = new StringBuilder("Discussions précédentes :\n\n");
        foreach (var message in previous)
            text.Append(message.IsUser ? "Utilisateur : " : "Kaonomi : ").Append(message.Content.Trim()).Append("\n\n");
        text.Append("Nouvelle question de l'utilisateur :\n").Append(prompt);
        return text.ToString();
    }

    private static string? PickModel(IReadOnlyList<string> models, string? wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted))
            return null;

        return models.FirstOrDefault(m => string.Equals(m, wanted, StringComparison.OrdinalIgnoreCase))
               ?? models.FirstOrDefault(m => m.StartsWith(wanted + ":", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatStats(AssistantResponse response, string requestedModel, TimeSpan elapsed)
    {
        var model = string.IsNullOrEmpty(response.Model) ? requestedModel : response.Model;
        var usage = response.Usage;
        var tokens = usage is null ? "" : $" · {usage.PromptTokens} + {usage.CompletionTokens} tokens";

        return $"{model}{tokens} · {elapsed.TotalSeconds:0.0} s";
    }

    /// <inheritdoc />
    public string NoteWindowName => "Kaonomi Chat";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        model = SelectedModel,
        theme = AppTheme.ToName(AppTheme.Current),
        systemPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? null : SystemPrompt,
        messageCount = Messages.Count,
        lastMessages = Messages.TakeLast(10).Select(m => new { role = m.ApiRole, content = m.Content, stats = m.Stats, isError = m.IsError })
    };
}
