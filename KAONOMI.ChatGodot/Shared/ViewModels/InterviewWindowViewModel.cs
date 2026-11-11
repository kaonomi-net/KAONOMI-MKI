using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// The interviews (<c>/interview</c>): each one stored in the search engine (the <c>interview</c> index of the
/// server), listed and searched on the left; the one open has the person interviewed (name, address, email, phone,
/// passport or identity card) and its notes, written with the rich editor of /work. An interview is recorded from
/// the microphone into WAV files of <c>~/kaonomi/audio/interview</c> (<see cref="InterviewRecorder"/>). When the
/// recording stops, Whisper turns it into text, a paragraph for what is said between two silences of a second or more
/// (<see cref="SilenceSegmenter"/>); with <see cref="LiveTranscription"/> the paragraphs come while the person
/// speaks. Each paragraph is a document of the <c>interviewparagraph</c> index, found by <c>/search</c>.
/// </summary>
/// <remarks>The entities (the NER of the server, at each save) and the deep text analysis are in InterviewWindowViewModel.Analysis.cs.</remarks>
public sealed partial class InterviewWindowViewModel : ViewModelBase, ITapeAssistant, INoteContextProvider, IDisposable, IUnsavedChanges, IProjectDocument
{
    /// <summary>
    /// The project of the interview open (chosen by its name in the header of the window), or null; saved with the
    /// interview, and with the paragraphs of its transcription.
    /// </summary>
    [ObservableProperty]
    private string? _projectId;

    /// <summary>An interview is always open (a new one when none was chosen).</summary>
    public bool HasProjectDocument => true;

    /// <summary>The paragraphs shown for the recording <paramref name="file"/>, by rank, with the project of the interview: what is stored for it.</summary>
    private List<InterviewParagraphEntry> TranscriptOf(string file)
    {
        var paragraphs = Paragraphs.Where(p => p.AudioFile == file).OrderBy(p => p.Index).ToList();
        foreach (var paragraph in paragraphs)
            paragraph.ProjectId = ProjectId;
        return paragraphs;
    }

    /// <summary>
    /// After a save: the paragraphs stored that still have another project than the interview are stored again with
    /// its project. Only the recordings whose paragraphs shown are all stored ones (a transcription cancelled is not
    /// stored by this).
    /// </summary>
    private async Task StoreParagraphProjectsAsync()
    {
        foreach (var file in Paragraphs.Where(p => p.ProjectId != ProjectId).Select(p => p.AudioFile).Distinct().ToList())
        {
            if (Paragraphs.Any(p => p.AudioFile == file && string.IsNullOrEmpty(p.Id)))
                continue;
            try
            {
                var result = await _server.SaveInterviewTranscriptAsync(new InterviewTranscriptRequest
                {
                    InterviewId = InterviewId,
                    AudioFile = file,
                    Paragraphs = TranscriptOf(file)
                });
                if (result.ReturnedId is null or "-1")
                    ActivityLog.Error("Interview", $"Project of the paragraphs of {file} not stored: {result.Message ?? "no answer"}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                ActivityLog.Error("Interview", $"Project of the paragraphs of {file} not stored: {ex.Message}");
            }
        }
    }

    private readonly KaonomiAssistantService _server;
    private readonly WritingAssistant _writing;
    private readonly OllamaModelService? _ollama;
    private readonly Func<string?>? _model;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _images = new();
    private readonly string? _speechModel;
    private readonly Func<string> _speechLanguage;
    private readonly InterviewRecorder _recorder = new();
    private SpeechRecognitionService? _speech;
    /// <summary>The model loaded and run once on silence, before the first words (one transcription at a time with it).</summary>
    private Task? _warmUp;
    /// <summary>The segments of the recording running, waiting for the live transcription.</summary>
    private Channel<SpeechSegment>? _live;
    private Task? _liveLoop;
    /// <summary>True while the live transcription has heard the whole recording running: nothing is left to transcribe at its end.</summary>
    private volatile bool _liveWhole;
    private string? _recordingFile;
    private DateTimeOffset _recordingStarted;
    private CancellationTokenSource? _transcription;
    /// <summary>True while the fields are filled from an interview: no "not saved" for each of them.</summary>
    private bool _loading;
    private bool _disposed;

    /// <summary>For the designer.</summary>
    public InterviewWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl), ChatSettings.SystemPicturesDirectory, null, null, null, null)
    {
    }

    public InterviewWindowViewModel(KaonomiAssistantService server, string picturesDirectory, OllamaModelService? ollama, Func<string?>? model,
        string? speechModel, Func<string>? speechLanguage)
    {
        _server = server;
        _ollama = ollama;
        _model = model;
        PicturesDirectory = picturesDirectory;
        _speechModel = speechModel;
        _speechLanguage = speechLanguage ?? (() => "fr");
        _writing = new WritingAssistant(ollama, model, server, InterviewPicturesDirectory, "Interview", "the interview");
        _recorder.SegmentReady += OnSegment;
        RefreshAudioSources();
        Load(null);
        Status = "A new interview: fill in the person, then ⏺ Record (the recordings go to " + InterviewRecorder.Folder + ")";
        _ = SearchAsync();
    }

    /// <summary>Folder of the pictures of the interviews: the pictures inserted are copied there (Pictures/kaonomi/interview).</summary>
    public static string InterviewPicturesDirectory => Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "interview");

    /// <summary>Folder of the recordings (<c>~/kaonomi/audio/interview</c>).</summary>
    public static string AudioDirectory => InterviewRecorder.Folder;

    /// <summary>Folder the picture picker opens in.</summary>
    public string PicturesDirectory { get; }

    /// <summary>Asks the user (a message and the label of the button); false when not answered.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    /// <summary>The kinds of document of identity.</summary>
    public IReadOnlyList<string> DocumentTypes { get; } = InterviewEntry.DocumentTypes;

    // ── The interviews ──

    /// <summary>The interviews changed last, or the ones found, on the left.</summary>
    public ObservableCollection<InterviewEntry> Interviews { get; } = [];

    /// <summary>A few words looked for in the person, the notes and the tags of the interviews; empty: the ones changed last.</summary>
    [ObservableProperty]
    private string _search = string.Empty;

    // ── The interview open ──

    /// <summary>The id of the interview open (given when it is started, so that its recordings and paragraphs can name it).</summary>
    [ObservableProperty]
    private string _interviewId = Guid.NewGuid().ToString();

    /// <summary>True for an interview not saved yet.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private bool _isNew = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PersonName))]
    private string _firstName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PersonName))]
    private string _lastName = string.Empty;

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _phone = string.Empty;

    /// <summary>Passport or Identity card.</summary>
    [ObservableProperty]
    private string _documentType = InterviewEntry.DocumentTypes[0];

    /// <summary>The number of the passport or of the identity card.</summary>
    [ObservableProperty]
    private string _documentNumber = string.Empty;

    /// <summary>The notes of the interview, in Markdown (the text of the editor).</summary>
    [ObservableProperty]
    private string _content = string.Empty;

    /// <summary>Its version and its dates, under the person.</summary>
    [ObservableProperty]
    private string _info = string.Empty;

    /// <summary>The name of the person ("" when none is given yet).</summary>
    public string PersonName => (LastName.Trim() + " " + FirstName.Trim()).Trim();

    /// <summary>The recordings of the interview open, in the order they were made.</summary>
    public ObservableCollection<InterviewRecordingEntry> Recordings { get; } = [];

    /// <summary>The paragraphs of its transcription, by recording then by rank.</summary>
    public ObservableCollection<InterviewParagraphEntry> Paragraphs { get; } = [];

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    // ── Recording and transcription ──

    /// <summary>The first audio source of the list: the microphone of the chat (the one of <c>/audiosettings</c>, else the default one of the system).</summary>
    public const string ChatMicrophone = "Microphone of the chat (/audiosettings)";

    /// <summary>The audio sources an interview can be recorded from: <see cref="ChatMicrophone"/>, then the inputs of the system.</summary>
    public ObservableCollection<string> AudioSources { get; } = [];

    /// <summary>The audio source of the next recording (one of <see cref="AudioSources"/>); kept for this window only, the microphone of the chat is not changed.</summary>
    [ObservableProperty]
    private string? _audioSource = ChatMicrophone;

    /// <summary>Reads the audio sources of the system again (a microphone plugged in since); the one chosen is kept when it is still there.</summary>
    [RelayCommand]
    private void RefreshAudioSources()
    {
        var chosen = AudioSource;
        IReadOnlyList<string> inputs;
        try
        {
            inputs = AudioDevices.Inputs();
        }
        catch (Exception ex)
        {
            inputs = [];
            SetStatus("The audio sources could not be read: " + ex.Message, true);
            ActivityLog.Error("Interview", "Audio sources: " + ex.Message);
        }
        _loadingSources = true;
        try
        {
            AudioSources.Clear();
            AudioSources.Add(ChatMicrophone);
            foreach (var input in inputs)
                AudioSources.Add(input);
            // Set again after the list was emptied (a drop-down bound to it lost its choice meanwhile).
            AudioSource = null;
            AudioSource = chosen is not null && AudioSources.Contains(chosen) ? chosen : ChatMicrophone;
        }
        finally
        {
            _loadingSources = false;
        }
    }

    partial void OnAudioSourceChanged(string? value)
    {
        if (IsRecording && value is not null && !_loadingSources)
            SetStatus($"Audio source \"{value}\": used from the next recording (the one running keeps {_recorder.SourceName})", false);
    }

    /// <summary>True while the list of the audio sources is read again.</summary>
    private bool _loadingSources;

    /// <summary>True while the microphone is recorded.</summary>
    [ObservableProperty]
    private bool _isRecording;

    /// <summary>How long the recording running is (00:01:23).</summary>
    [ObservableProperty]
    private string _recordingTime = "00:00:00";

    /// <summary>The level of the microphone while it records, from 0 to 1.</summary>
    [ObservableProperty]
    private double _level;

    /// <summary>The live transcription: each paragraph is written as soon as the silence that ends it is heard.</summary>
    [ObservableProperty]
    private bool _liveTranscription;

    /// <summary>True while a recording is turned into text (after it stops, or with Transcribe).</summary>
    [ObservableProperty]
    private bool _isTranscribing;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is nameof(FirstName) or nameof(LastName) or nameof(Address) or nameof(Email) or nameof(Phone)
                or nameof(DocumentType) or nameof(DocumentNumber) or nameof(Content) or nameof(ProjectId))
            IsDirty = true;
    }

    /// <summary>The language of the transcription: the one of the voice of the chat (fr, de, en…).</summary>
    private string Language => _speechLanguage();

    // ── Search, open, new, save, delete ──

    /// <summary>The interviews changed last (empty search), or the ones found.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        try
        {
            var term = Search.Trim();
            var found = term.Length == 0 ? await _server.GetLastInterviewsAsync(300) : await _server.SearchInterviewsAsync(term, 100);
            Interviews.Clear();
            foreach (var interview in found)
                Interviews.Add(interview);
            if (term.Length > 0)
                SetStatus($"{Interviews.Count} interview(s) found for \"{term}\"", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The interviews could not be read: " + ex.Message, true);
        }
    }

    /// <summary>True (with the reason in the status) while the interview open cannot be left: it is being recorded or transcribed.</summary>
    private bool Held(string what)
    {
        if (IsAnalyzing)
        {
            SetStatus($"The deep text analysis is running: wait for it (or cancel it), then {what}", true);
            return true;
        }
        if (!IsRecording && !IsTranscribing)
            return false;
        SetStatus(IsRecording ? $"Stop the recording first (⏹), then {what}" : $"The transcription is running: wait for it (or cancel it), then {what}", true);
        return true;
    }

    /// <summary>Opens the interview <paramref name="id"/> with its recordings and the paragraphs of its transcription.</summary>
    public async Task OpenInterviewAsync(string id)
    {
        if (Held("open another interview"))
            return;
        if (IsDirty && !await ConfirmAsync($"The interview \"{Label}\" is not saved. Open the other one anyway?", "Open"))
            return;
        IsBusy = true;
        try
        {
            var interview = await _server.GetInterviewAsync(id);
            if (interview is null)
            {
                SetStatus("This interview does not exist any more", true);
                return;
            }
            Load(interview);
            await LoadParagraphsAsync();
            // The results of its analyses, in view as soon as it is open.
            RefreshResults(reveal: true);
            SetStatus($"Interview \"{Label}\" (version {interview.Version}) · {Recordings.Count} recording(s), {Paragraphs.Count} paragraph(s)", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The interview could not be read: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The interview of a hit of <c>/search</c>: the hit itself (index <c>interview</c>), or the interview of the
    /// paragraph found (index <c>interviewparagraph</c>); null when it cannot be read.
    /// </summary>
    public static string? InterviewIdOf(SearchHit hit)
    {
        if (hit.Index == "interview")
            return hit.Id;
        try
        {
            using var document = JsonDocument.Parse(hit.Source);
            return document.RootElement.TryGetProperty("interviewId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [RelayCommand]
    private Task OpenEntryAsync(InterviewEntry? interview) => interview?.Id is { } id ? OpenInterviewAsync(id) : Task.CompletedTask;

    private async Task LoadParagraphsAsync()
    {
        Paragraphs.Clear();
        foreach (var paragraph in await _server.GetInterviewParagraphsAsync(InterviewId))
            Paragraphs.Add(paragraph);
    }

    /// <summary>The name of the interview in the messages.</summary>
    private string Label => PersonName.Length > 0 ? PersonName : "Interview";

    private void Load(InterviewEntry? interview)
    {
        _loading = true;
        try
        {
            InterviewId = interview?.Id ?? Guid.NewGuid().ToString();
            IsNew = interview is null;
            ProjectId = interview?.ProjectId;
            FirstName = interview?.FirstName ?? string.Empty;
            LastName = interview?.LastName ?? string.Empty;
            Address = interview?.Address ?? string.Empty;
            Email = interview?.Email ?? string.Empty;
            Phone = interview?.Phone ?? string.Empty;
            DocumentType = interview?.DocumentType is { Length: > 0 } type ? type : InterviewEntry.DocumentTypes[0];
            DocumentNumber = interview?.DocumentNumber ?? string.Empty;
            Content = interview?.Content ?? string.Empty;
            ClosePlayer();
            Recordings.Clear();
            foreach (var recording in interview?.Recordings ?? [])
                Recordings.Add(recording);
            if (interview is null)
                Paragraphs.Clear();
            SetEntities(interview?.Entities);
            _noteEmotions = interview?.NoteEmotions ?? [];
            Analysis = interview?.Analysis ?? string.Empty;
            _analyzedAt = interview?.AnalyzedAt;
            CancelSummary();
            SummaryNote = interview?.SummaryNote ?? string.Empty;
            _summaryNoteAt = interview?.SummaryNoteAt;
            _summaryNoteModel = interview?.SummaryNoteModel ?? string.Empty;
            _summaryNoteSource = interview?.SummaryNoteSource ?? string.Empty;
            SetInfo(interview);
            RefreshResults(reveal: true);
            IsDirty = false;
        }
        finally
        {
            _loading = false;
        }
    }

    private void SetInfo(InterviewEntry? interview) =>
        Info = interview is null
            ? "new interview · not saved"
            : $"version {interview.Version} · {interview.DateLabel}"
              + (interview.Created is { } created ? $" · created {created.ToLocalTime():yyyy-MM-dd}" : string.Empty);

    /// <summary>A new interview.</summary>
    [RelayCommand]
    private async Task NewInterviewAsync()
    {
        if (Held("start a new interview"))
            return;
        if (IsDirty && !await ConfirmAsync($"The interview \"{Label}\" is not saved. Start a new one anyway?", "New interview"))
            return;
        Load(null);
        SetStatus("New interview: fill in the person, then ⏺ Record, or write your notes and SAVE", false);
    }

    /// <summary>Saves the interview (created, or a new version of it).</summary>
    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(quiet: false, summarise: true);

    /// <summary>Saves the interview; <paramref name="quiet"/>: after a recording, the status keeps what the recording said.</summary>
    private async Task<bool> SaveCoreAsync(bool quiet, bool summarise = false)
    {
        var entry = new InterviewEntry
        {
            Id = InterviewId,
            AppName = "KAONOMI.Chat",
            ProjectId = ProjectId,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            Title = PersonName,
            FirstName = FirstName.Trim(),
            LastName = LastName.Trim(),
            Address = Address.Trim(),
            Email = Email.Trim(),
            Phone = Phone.Trim(),
            DocumentType = DocumentType,
            DocumentNumber = DocumentNumber.Trim(),
            Content = Content,
            Language = Language,
            Recordings = Recordings.ToList(),
            // The entities are read again by the server (its NER) at each save; the ones known are sent in case it cannot.
            Entities = Entities.ToList(),
            NoteEmotions = _noteEmotions,
            Analysis = Analysis,
            AnalyzedAt = _analyzedAt,
            SummaryNote = SummaryNote,
            SummaryNoteAt = _summaryNoteAt,
            SummaryNoteModel = _summaryNoteModel,
            SummaryNoteSource = _summaryNoteSource
        };
        IsBusy = true;
        try
        {
            var result = await _server.SaveInterviewAsync(entry);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("The interview was not saved: " + (result.Message ?? "no answer"), true);
                return false;
            }
            IsNew = false;
            IsDirty = false;
            // Read back: its version, and the entities the NER of the server found in it.
            var saved = await _server.GetInterviewAsync(InterviewId);
            SetInfo(saved);
            if (saved is not null)
                SetEntities(saved.Entities);
            RefreshResults();
            // Its paragraphs follow it when its project changed.
            await StoreParagraphProjectsAsync();
            // Its summary note, written after the save (in the background) when its text changed.
            if (summarise)
                _ = UpdateSummaryAsync();
            if (!quiet)
                SetStatus($"Interview \"{Label}\" saved", false);
            ActivityLog.Info("Interview", $"Interview {InterviewId} saved ({Content.Length} chars, {Recordings.Count} recording(s))");
            await SearchAsync();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The interview was not saved: " + ex.Message, true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDelete() => !IsNew;

    /// <summary>Marks the interview and its paragraphs deleted (kept in the search engine; the WAV files stay), then starts a new one.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Held("delete the interview"))
            return;
        if (!await ConfirmAsync($"Delete the interview \"{Label}\"? It leaves the list (kept, marked deleted, in the search engine); its recordings stay in {AudioDirectory}.", "Delete"))
            return;
        try
        {
            var result = await _server.DeleteInterviewAsync(InterviewId);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("The interview was not deleted: " + (result.Message ?? "no answer"), true);
                return;
            }
            ActivityLog.Info("Interview", $"Interview {InterviewId} marked deleted");
            var label = Label;
            Load(null);
            await SearchAsync();
            SetStatus($"Interview \"{label}\" deleted", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The interview was not deleted: " + ex.Message, true);
        }
    }

    // ── Recording ──

    /// <summary>⏺ starts recording the microphone into a new WAV file; ⏹ stops it, then the recording is transcribed and the interview saved.</summary>
    [RelayCommand]
    private Task ToggleRecordingAsync() => IsRecording ? StopRecordingAsync() : StartRecordingAsync();

    private async Task StartRecordingAsync()
    {
        if (IsTranscribing)
        {
            SetStatus("The transcription is running: wait for it (or cancel it) before recording again", true);
            return;
        }
        if (LiveTranscription && !EnsureSpeech())
            LiveTranscription = false;
        // The microphone would record what the player plays.
        ClosePlayer();
        var started = DateTimeOffset.Now;
        var name = $"{WikiPageEntry.Slug(PersonName.Length > 0 ? PersonName : "interview")}-{started:yyyyMMdd-HHmmss}.wav";
        var path = Path.Combine(AudioDirectory, name);
        _live = Channel.CreateUnbounded<SpeechSegment>(new UnboundedChannelOptions { SingleReader = true });
        _liveWhole = LiveTranscription;
        _recordingFile = name;
        _recordingStarted = started;
        // The source chosen in the window; the first of the list is the microphone of the chat.
        var source = AudioSource is { Length: > 0 } chosen && chosen != ChatMicrophone ? chosen : null;
        SetStatus($"Opening {source ?? "the microphone"}…", false);
        try
        {
            // Opened off the UI thread, as the voice input of the chat; given up after a while (a microphone not allowed).
            await Task.Run(() => _recorder.Start(path, source)).WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            // The microphone, if it opens later, is closed at once (it never records unseen).
            _ = Task.Run(_recorder.Cancel);
            _live = null;
            SetStatus("Recording: " + (ex is TimeoutException ? "the microphone does not open (is it allowed?)" : ex.Message), true);
            ActivityLog.Error("Interview", "Recording microphone: " + ex.Message);
            return;
        }
        IsRecording = true;
        // Closing the window while it records asks first.
        IsDirty = true;
        _liveLoop = LiveLoopAsync(_live.Reader, name);
        _ = ClockAsync();
        SetStatus($"⏺ Recording {_recorder.SourceName} into {name}" + (LiveTranscription ? $" · live transcription ({Language.ToUpperInvariant()})" : " · transcribed when it stops"), false);
    }

    /// <summary>While it records: its length and the level of the microphone, four times a second.</summary>
    private async Task ClockAsync()
    {
        while (IsRecording && !_disposed)
        {
            RecordingTime = InterviewParagraphEntry.Clock(_recorder.Elapsed.TotalSeconds);
            Level = Math.Clamp(_recorder.Level, 0, 1);
            await Task.Delay(250);
        }
        Level = 0;
    }

    private async Task StopRecordingAsync()
    {
        if (!IsRecording || _live is not { } live)
            return;
        SetStatus("Closing the recording…", false);
        InterviewRecordingResult? result;
        try
        {
            // The last words, not yet followed by a silence, come as a last segment before Stop returns.
            result = await Task.Run(_recorder.Stop);
        }
        catch (Exception ex)
        {
            result = null;
            SetStatus("Recording: " + ex.Message, true);
            ActivityLog.Error("Interview", "Recording stop: " + ex.Message);
        }
        IsRecording = false;
        live.Writer.TryComplete();
        IsTranscribing = true;
        try
        {
            if (_liveLoop is { } loop)
            {
                if (LiveTranscription)
                    SetStatus("Finishing the live transcription…", false);
                await loop;
            }
        }
        finally
        {
            IsTranscribing = false;
            _live = null;
            _liveLoop = null;
        }
        if (result is null)
            return;
        var recording = new InterviewRecordingEntry
        {
            Path = result.Path,
            FileName = Path.GetFileName(result.Path),
            RecordedAt = _recordingStarted,
            DurationSeconds = result.Seconds,
            SampleRate = result.Rate
        };
        Recordings.Add(recording);
        IsDirty = true;
        var problem = result.Error is null ? string.Empty : $" (the file is incomplete: {result.Error})";
        if (_liveWhole)
            await StoreTranscriptAsync(recording);
        else if (EnsureSpeech())
            await TranscribeRecordingAsync(recording);
        else
            SetStatus($"Recorded {recording.FileName} ({InterviewParagraphEntry.Clock(result.Seconds)}), not transcribed: no Whisper model (speech.model in config.yml){problem}", true);
        // The interview is saved with its new recording, so that it is found again.
        var error = IsError;
        var said = Status;
        if (await SaveCoreAsync(quiet: true, summarise: true) && said.Length > 0)
            SetStatus(said + problem, error || result.Error is not null);
    }

    /// <summary>A segment ended by a silence, from the thread of the recorder: kept for the live transcription, or let go.</summary>
    private void OnSegment(SpeechSegment segment)
    {
        if (LiveTranscription && _live is { } live)
            live.Writer.TryWrite(segment);
        else
            _liveWhole = false;
    }

    partial void OnLiveTranscriptionChanged(bool value)
    {
        // Turned on or off while it records: the live transcription has not heard it all, the file is transcribed at the end.
        if (IsRecording)
            _liveWhole = false;
        if (!value)
            return;
        if (!EnsureSpeech())
        {
            LiveTranscription = false;
            return;
        }
        if (IsRecording)
            SetStatus($"Live transcription on ({Language.ToUpperInvariant()}): the paragraphs come at each silence of a second", false);
    }

    // ── Transcription ──

    /// <summary>True when Whisper can be used (its model is there); else the status says why not.</summary>
    private bool EnsureSpeech()
    {
        if (string.IsNullOrWhiteSpace(_speechModel))
        {
            SetStatus("Transcription: no Whisper model (speech.model in config.yml)", true);
            return false;
        }
        _speech ??= new SpeechRecognitionService(_speechModel);
        if (!_speech.HasModel)
        {
            SetStatus($"Transcription: Whisper model not found ({_speech.ModelPath}); set speech.model in config.yml, or run /setup", true);
            return false;
        }
        _warmUp ??= _speech.WarmUpAsync(Language);
        return true;
    }

    /// <summary>The text of <paramref name="segment"/>; empty when Whisper heard no word in it.</summary>
    private async Task<string> RecogniseAsync(SpeechSegment segment, CancellationToken cancellationToken)
    {
        // Not at the same time as the warm-up: one transcription at a time with the same model.
        if (_warmUp is { } warmUp)
            await warmUp;
        var samples = await Task.Run(() => Wav.ForWhisper(segment), cancellationToken);
        return await _speech!.TranscribeAsync(samples, Language, cancellationToken);
    }

    private InterviewParagraphEntry NewParagraph(string file, int index, string text, SpeechSegment segment) => new()
    {
        AppName = "KAONOMI.Chat",
        ProjectId = ProjectId,
        MachineName = Environment.MachineName,
        UserName = Environment.UserName,
        Date = DateTimeOffset.UtcNow,
        Title = PersonName.Length > 0 ? "Interview · " + PersonName : "Interview",
        InterviewId = InterviewId,
        Person = PersonName,
        AudioFile = file,
        Index = index,
        Text = text,
        StartSeconds = Math.Round(segment.Start, 2),
        EndSeconds = Math.Round(segment.End, 2),
        Language = Language
    };

    /// <summary>The live transcription: each segment of the recording running becomes a paragraph, stored at once.</summary>
    private async Task LiveLoopAsync(ChannelReader<SpeechSegment> segments, string file)
    {
        await foreach (var segment in segments.ReadAllAsync())
        {
            if (_disposed)
                return;
            try
            {
                var text = await RecogniseAsync(segment, CancellationToken.None);
                if (text.Length == 0)
                    continue;
                var paragraph = NewParagraph(file, Paragraphs.Count(p => p.AudioFile == file), text, segment);
                Paragraphs.Add(paragraph);
                var result = await _server.SaveInterviewParagraphAsync(paragraph);
                if (result.ReturnedId is null or "-1")
                    throw new HttpRequestException(result.Message ?? "no answer");
                paragraph.Id = result.ReturnedId;
                if (IsRecording)
                    SetStatus($"⏺ Recording into {file} · live: {Paragraphs.Count(p => p.AudioFile == file)} paragraph(s)", false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
            {
                // The file is transcribed again when the recording stops: nothing is lost.
                _liveWhole = false;
                SetStatus("Live transcription: " + ex.Message, true);
                ActivityLog.Error("Interview", "Live transcription: " + ex.Message);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    /// <summary>📝 Transcribes a recording again (the last one when none is given): its paragraphs replace the ones stored.</summary>
    [RelayCommand]
    private async Task TranscribeAsync(InterviewRecordingEntry? recording)
    {
        if (IsRecording)
        {
            SetStatus("Stop the recording first (⏹): it is transcribed when it stops", true);
            return;
        }
        if (IsTranscribing)
        {
            SetStatus("A transcription is already running", true);
            return;
        }
        recording ??= Recordings.LastOrDefault();
        if (recording is null)
        {
            SetStatus("No recording to transcribe: ⏺ Record first", true);
            return;
        }
        if (!EnsureSpeech())
            return;
        await TranscribeRecordingAsync(recording);
        var error = IsError;
        var said = Status;
        if (await SaveCoreAsync(quiet: true, summarise: true))
            SetStatus(said, error);
    }

    /// <summary>Stops the transcription running (what it wrote so far is shown, not stored).</summary>
    [RelayCommand]
    private void CancelTranscription() => _transcription?.Cancel();

    /// <summary>
    /// Reads the WAV file of <paramref name="recording"/> piece by piece, cuts it at each silence of a second and gives
    /// each segment to Whisper: one paragraph per segment, then the transcription is stored in place of the former one.
    /// </summary>
    private async Task TranscribeRecordingAsync(InterviewRecordingEntry recording)
    {
        if (!File.Exists(recording.Path))
        {
            SetStatus($"The recording is not there any more: {recording.Path}", true);
            return;
        }
        using var cancel = new CancellationTokenSource();
        _transcription = cancel;
        IsTranscribing = true;
        var file = recording.FileName;
        var started = DateTime.Now;
        foreach (var old in Paragraphs.Where(p => p.AudioFile == file).ToList())
            Paragraphs.Remove(old);
        try
        {
            using var reader = await Task.Run(() => new WavReader(recording.Path), cancel.Token);
            var segmenter = new SilenceSegmenter(reader.Rate);
            var buffer = new float[reader.Rate * 5];
            var count = 0;

            async Task AddAsync(SpeechSegment segment)
            {
                var text = await RecogniseAsync(segment, cancel.Token);
                if (text.Length > 0)
                    Paragraphs.Add(NewParagraph(file, count++, text, segment));
            }

            while (true)
            {
                cancel.Token.ThrowIfCancellationRequested();
                var (read, segments) = await Task.Run(() =>
                {
                    var n = reader.Read(buffer);
                    return (n, n > 0 ? segmenter.Add(buffer.AsSpan(0, n)) : []);
                }, cancel.Token);
                if (read == 0)
                    break;
                foreach (var segment in segments)
                    await AddAsync(segment);
                var percent = reader.TotalFrames > 0 ? reader.Position * 100 / reader.TotalFrames : 100;
                SetStatus($"📝 Transcribing {file} ({Language.ToUpperInvariant()}): {percent}% · {count} paragraph(s)", false);
            }
            if (segmenter.Flush() is { } last)
                await AddAsync(last);
            if (recording.DurationSeconds <= 0 && reader.Rate > 0)
                recording.DurationSeconds = reader.TotalFrames / (double)reader.Rate;
            await StoreTranscriptAsync(recording);
            ActivityLog.Info("Interview", $"{file}: {count} paragraph(s) transcribed in {(DateTime.Now - started).TotalSeconds:0} s");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"Transcription of {file} cancelled: the paragraphs shown are not stored (📝 Transcribe starts it again)", true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            SetStatus($"Transcription of {file}: {ex.Message}", true);
            ActivityLog.Error("Interview", $"Transcription of {file}: {ex.Message}");
        }
        finally
        {
            _transcription = null;
            IsTranscribing = false;
        }
    }

    /// <summary>Stores the paragraphs shown for <paramref name="recording"/> in the search engine, in place of the ones stored for it.</summary>
    private async Task StoreTranscriptAsync(InterviewRecordingEntry recording)
    {
        var paragraphs = TranscriptOf(recording.FileName);
        try
        {
            var result = await _server.SaveInterviewTranscriptAsync(new InterviewTranscriptRequest
            {
                InterviewId = InterviewId,
                AudioFile = recording.FileName,
                Paragraphs = paragraphs
            });
            if (result.ReturnedId is null or "-1")
            {
                SetStatus($"The transcription of {recording.FileName} was not stored: " + (result.Message ?? "no answer"), true);
                return;
            }
            // A new entry in place of the former one: the list shows its new state.
            var at = Recordings.IndexOf(recording);
            var done = new InterviewRecordingEntry
            {
                Path = recording.Path,
                FileName = recording.FileName,
                RecordedAt = recording.RecordedAt,
                DurationSeconds = recording.DurationSeconds,
                SampleRate = recording.SampleRate,
                Transcribed = true,
                ParagraphCount = paragraphs.Count,
                Voice = recording.Voice
            };
            if (at >= 0)
                Recordings[at] = done;
            IsDirty = true;
            SetStatus(paragraphs.Count == 0
                ? $"{recording.FileName} ({InterviewParagraphEntry.Clock(recording.DurationSeconds)}): no speech heard"
                : $"{recording.FileName} ({InterviewParagraphEntry.Clock(recording.DurationSeconds)}): {paragraphs.Count} paragraph(s) stored in the search engine", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus($"The transcription of {recording.FileName} was not stored: {ex.Message} (📝 Transcribe stores it again)", true);
        }
    }

    /// <summary>The transcription as Markdown: a heading per recording, then its paragraphs, each after its time.</summary>
    public string TranscriptMarkdown()
    {
        var text = new StringBuilder();
        foreach (var file in Paragraphs.GroupBy(p => p.AudioFile))
        {
            var recording = Recordings.FirstOrDefault(r => r.FileName == file.Key);
            text.Append("## Transcription");
            if (recording is not null)
                text.Append($" · {recording.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            text.Append("\n\n");
            foreach (var paragraph in file.OrderBy(p => p.Index))
                text.Append($"**[{paragraph.TimeLabel}]** {paragraph.Text}\n\n");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>⤵ Puts the transcription at the end of the notes.</summary>
    [RelayCommand]
    private void InsertTranscript()
    {
        if (Paragraphs.Count == 0)
        {
            SetStatus("No transcription yet: ⏺ Record, or 📝 Transcribe a recording", true);
            return;
        }
        var notes = Content.TrimEnd();
        Content = (notes.Length > 0 ? notes + "\n\n" : string.Empty) + TranscriptMarkdown() + "\n";
        SetStatus($"{Paragraphs.Count} paragraph(s) of the transcription added at the end of the notes", false);
    }

    // ── The player of the recordings, in the window ──

    private WavPlayer? _player;

    /// <summary>The recording open in the player (null: the player is closed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayer))]
    [NotifyPropertyChangedFor(nameof(PlayerTitle))]
    private InterviewRecordingEntry? _playingRecording;

    /// <summary>True while the player is shown.</summary>
    public bool HasPlayer => PlayingRecording is not null;

    /// <summary>The file in the player.</summary>
    public string PlayerTitle => PlayingRecording?.FileName ?? string.Empty;

    /// <summary>True while the player plays (false when paused or at the end).</summary>
    [ObservableProperty]
    private bool _isPlaying;

    /// <summary>Where the player is in the recording, in seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayerTime))]
    private double _playerPosition;

    /// <summary>The length of the recording in the player, in seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayerTime))]
    private double _playerDuration;

    /// <summary>The position over the length (00:01:23 / 00:12:00).</summary>
    public string PlayerTime => $"{InterviewParagraphEntry.Clock(PlayerPosition)} / {InterviewParagraphEntry.Clock(PlayerDuration)}";

    /// <summary>The start of the paragraph being played (-1: none), for the transcription to show it.</summary>
    [ObservableProperty]
    private double _playingParagraphStart = -1;

    /// <summary>▶ of a recording: opens it in the player of the window and plays it, from <paramref name="from"/> seconds.</summary>
    private void OpenPlayer(InterviewRecordingEntry recording, double from)
    {
        if (IsRecording)
        {
            SetStatus("Stop the recording first (⏹): the microphone would record what is played", true);
            return;
        }
        try
        {
            if (_player is null || PlayingRecording?.Path != recording.Path)
            {
                ClosePlayer();
                if (!File.Exists(recording.Path))
                {
                    SetStatus($"The recording is not there any more: {recording.Path}", true);
                    return;
                }
                _player = new WavPlayer(recording.Path);
                PlayerDuration = _player.Duration;
                PlayingRecording = recording;
                _ = PlayerClockAsync(_player);
            }
            _player.Seek(from);
            _player.Play();
            PlayerPosition = _player.Position;
            IsPlaying = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            ClosePlayer();
            SetStatus($"{recording.FileName} cannot be played: {ex.Message}", true);
            ActivityLog.Error("Interview", $"Player of {recording.FileName}: {ex.Message}");
        }
    }

    /// <summary>▶ of a recording: plays it in the player of the window, from its start.</summary>
    [RelayCommand]
    private void PlayRecording(InterviewRecordingEntry? recording)
    {
        if (recording is not null)
            OpenPlayer(recording, 0);
    }

    /// <summary>A click on the time of a paragraph: its recording is played from there.</summary>
    [RelayCommand]
    private void PlayParagraph(InterviewParagraphEntry? paragraph)
    {
        if (paragraph is null)
            return;
        if (Recordings.FirstOrDefault(r => r.FileName == paragraph.AudioFile) is not { } recording)
        {
            SetStatus($"The recording of this paragraph ({paragraph.AudioFile}) is not in the interview any more", true);
            return;
        }
        OpenPlayer(recording, paragraph.StartSeconds);
    }

    /// <summary>⏯ of the player: pause, or play again (from the start after the end).</summary>
    [RelayCommand]
    private void TogglePlay()
    {
        if (_player is not { } player)
            return;
        try
        {
            if (player.IsPlaying)
                player.Pause();
            else
                player.Play();
            IsPlaying = player.IsPlaying;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException or ObjectDisposedException)
        {
            SetStatus("Player: " + ex.Message, true);
        }
    }

    /// <summary>Moves the player to <paramref name="seconds"/> (the slider of the player).</summary>
    public void SeekPlayer(double seconds)
    {
        if (_player is not { } player)
            return;
        player.Seek(Math.Clamp(seconds, 0, PlayerDuration));
        PlayerPosition = player.Position;
    }

    /// <summary>⏪ ⏩ of the player: 10 seconds back or forward (the parameter: -10 or 10).</summary>
    [RelayCommand]
    private void SkipPlayer(string? seconds)
    {
        if (double.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var by))
            SeekPlayer(PlayerPosition + by);
    }

    /// <summary>✕ of the player: stops playing and closes it.</summary>
    [RelayCommand]
    private void ClosePlayer()
    {
        var player = _player;
        _player = null;
        try
        {
            player?.Dispose();
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Interview", "Closing the player: " + ex.Message);
        }
        IsPlaying = false;
        PlayingRecording = null;
        PlayerPosition = 0;
        PlayingParagraphStart = -1;
    }

    /// <summary>While <paramref name="player"/> is open: its position, five times a second; it stops by itself at the end.</summary>
    private async Task PlayerClockAsync(WavPlayer player)
    {
        while (ReferenceEquals(_player, player) && !_disposed)
        {
            if (player.Ended && IsPlaying)
            {
                player.Pause();
                IsPlaying = false;
                PlayerPosition = PlayerDuration;
            }
            else if (IsPlaying)
                PlayerPosition = player.Position;
            var file = PlayingRecording?.FileName;
            var position = PlayerPosition;
            PlayingParagraphStart = Paragraphs.LastOrDefault(p => p.AudioFile == file && p.StartSeconds <= position + 0.05 && position < p.EndSeconds + 0.5)?.StartSeconds ?? -1;
            await Task.Delay(200);
        }
    }

    // ── Pictures ──

    /// <summary>
    /// Copies the pictures <paramref name="files"/> into the folder of the interviews (a new name when one is taken)
    /// and returns their new paths, to insert in the notes.
    /// </summary>
    public List<string> CopyPictures(IEnumerable<string> files)
    {
        Directory.CreateDirectory(InterviewPicturesDirectory);
        var copies = new List<string>();
        foreach (var file in files)
        {
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(file)) == Path.GetFullPath(InterviewPicturesDirectory))
                {
                    copies.Add(file);
                    continue;
                }
                var name = Path.GetFileNameWithoutExtension(file);
                var extension = Path.GetExtension(file);
                var target = Path.Combine(InterviewPicturesDirectory, name + extension);
                for (var i = 2; File.Exists(target); i++)
                    target = Path.Combine(InterviewPicturesDirectory, $"{name}-{i}{extension}");
                File.Copy(file, target);
                copies.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus($"{Path.GetFileName(file)} not copied: {ex.Message}", true);
            }
        }
        return copies;
    }

    /// <summary>A picture for the editor (decoded once, at most 1600 pixels wide).</summary>
    public Task<Bitmap?> LoadImageAsync(string path) =>
        _images.GetOrAdd(path, p => PictureLibrary.DecodeAsync(p, 1600, CancellationToken.None));

    // ── The AI tools of the editor (the ones of /work and of the journal) ──

    /// <inheritdoc />
    public string? CurrentModelName => _writing.CurrentModelName;

    /// <inheritdoc />
    public bool CanEvaluate => _writing.CanAsk;

    /// <inheritdoc />
    public bool CanGenerateImages => _writing.CanGenerateImages;

    /// <inheritdoc />
    public Task<string> EvaluateAsync(string text, CancellationToken cancellationToken) =>
        _writing.EvaluateAsync(text,
            "Tu es un analyste qui relit des comptes rendus d'entretien, exigeant et bienveillant. Réponds en Markdown, dans la langue du texte évalué, avec des titres courts.",
            "Analyse ce compte rendu d'entretien : ce qui est établi, ce qui reste flou ou contradictoire, les questions à poser encore. Ne le réécris pas.",
            cancellationToken);

    /// <inheritdoc />
    public Task<string> TranslateAsync(string text, string language, CancellationToken cancellationToken) =>
        _writing.TranslateAsync(text, language, cancellationToken);

    /// <inheritdoc />
    public Task<string> DetectLanguageAsync(string text, CancellationToken cancellationToken) =>
        _writing.DetectLanguageAsync(text, cancellationToken);

    /// <inheritdoc />
    public Task<string> RewriteAsync(string text, string mode, CancellationToken cancellationToken, string? language = null) =>
        _writing.RewriteAsync(text, mode, cancellationToken, language);

    /// <inheritdoc />
    public Task<(string Language, string Code)> GenerateCodeAsync(string prompt, string language, CancellationToken cancellationToken) =>
        _writing.GenerateCodeAsync(prompt, language, cancellationToken);

    /// <inheritdoc />
    public Task<string> GenerateImageAsync(string prompt, string aspectRatio, string performance, CancellationToken cancellationToken) =>
        _writing.GenerateImageAsync(prompt, aspectRatio, performance, cancellationToken);

    private void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }

    /// <inheritdoc />
    public string NoteWindowName => "Interview";

    /// <inheritdoc />
    public object? GetNoteContext() => new { interview = InterviewId, person = PersonName, recordings = Recordings.Count, paragraphs = Paragraphs.Count };

    /// <summary>The window closes: the recording running is stopped (its file closed and kept), the transcription given up.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _recorder.SegmentReady -= OnSegment;
        _transcription?.Cancel();
        _analysisCancel?.Cancel();
        _summaryCancel?.Cancel();
        _live?.Writer.TryComplete();
        try
        {
            _player?.Dispose();
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Interview", "Closing the player: " + ex.Message);
        }
        _player = null;
        try
        {
            _recorder.Dispose();
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Interview", "Closing the recording: " + ex.Message);
        }
        _speech?.Dispose();
        foreach (var image in _images.Values)
            if (image.IsCompletedSuccessfully)
                image.Result?.Dispose();
        _images.Clear();
    }
}
