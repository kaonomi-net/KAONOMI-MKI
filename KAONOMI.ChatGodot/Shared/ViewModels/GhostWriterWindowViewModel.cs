using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// The ghost writer (<c>/write</c>): a writing in four tabs, its general information (title, description, goals,
/// audience, author, style), its actors, its locations and its chapters; a double-click on a chapter opens the text
/// area where its text is written. The writing is saved in the search engine through the Kaonomi server (the
/// <c>ghostwriter</c> index, its id the slug of its title, a new version at each save), where the writings are searched
/// (<see cref="SearchCommand"/>) and opened again.
/// </summary>
public sealed partial class GhostWriterWindowViewModel : ViewModelBase, IDisposable, IUnsavedChanges, IProjectDocument
{
    /// <summary>The project the writing is attached to (the id of a project, chosen by its name in the header of the window), or null; saved with it, and with the entries of its journal.</summary>
    [ObservableProperty]
    private string? _projectId;

    /// <summary>The window always holds a writing.</summary>
    public bool HasProjectDocument => true;

    private readonly KaonomiAssistantService _server;
    /// <summary>True while a writing is being loaded: the values set then are not changes.</summary>
    private bool _loading;

    /// <summary>For the designer.</summary>
    public GhostWriterWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    public GhostWriterWindowViewModel(KaonomiAssistantService server, OllamaModelService? ollama = null, Func<string?>? model = null,
        string? speechModel = null, Func<string>? speechLanguage = null)
    {
        _server = server;
        _speechModel = speechModel;
        _speechLanguage = speechLanguage ?? (() => "fr");
        // The model of the dictation made ready in the background: the first words then come as fast as the next ones.
        if (!string.IsNullOrWhiteSpace(speechModel))
        {
            _dictation = new SpeechRecognitionService(speechModel);
            _dictationWarmUp = _dictation.WarmUpAsync(_speechLanguage());
        }
        Assistant = new WritingAssistant(ollama, model, server, PicturesDirectory, "GhostWriter", "the chapter");
        Actors.CollectionChanged += Watch;
        Locations.CollectionChanged += Watch;
        Chapters.CollectionChanged += Watch;
        Status = "A new writing: fill its general information, then its actors, its locations and its chapters (double-click a chapter to write it)";
        _ = SearchAsync();
    }

    // ── Dictation: hold the 🎤 of a text, speak, release: the words are typed in it ──

    private readonly string? _speechModel;
    private readonly Func<string> _speechLanguage;
    private SpeechRecognitionService? _dictation;
    private Task<bool>? _dictationStart;
    /// <summary>The model loaded and run once on silence when the window opens (see <see cref="SpeechRecognitionService.WarmUpAsync"/>).</summary>
    private Task? _dictationWarmUp;

    /// <summary>True while a 🎤 is held (the microphone records).</summary>
    [ObservableProperty]
    private bool _isDictating;

    /// <summary>True while the speech is turned into text.</summary>
    [ObservableProperty]
    private bool _isTranscribing;

    /// <summary>The transcriptions made while the 🎤 is held (the words shown as they are said).</summary>
    private Task? _partials;

    /// <summary>
    /// Starts recording the microphone (a 🎤 is pressed); opened off the UI thread, as the voice input of the chat.
    /// While it records, the speech so far is transcribed again and again and given to <paramref name="partial"/>
    /// (from a background thread), so that the words show as they are said.
    /// </summary>
    public void StartDictation(Action<string>? partial = null)
    {
        if (IsDictating || IsTranscribing || _dictationStart is { IsCompleted: false })
            return;
        if (string.IsNullOrWhiteSpace(_speechModel))
        {
            SetStatus("Dictation: no Whisper model (speech.model in config.yml)", true);
            return;
        }
        _dictation ??= new SpeechRecognitionService(_speechModel);
        if (!_dictation.HasModel)
        {
            SetStatus($"Dictation: Whisper model not found ({_dictation.ModelPath}); set speech.model in config.yml", true);
            return;
        }
        var speech = _dictation;
        IsDictating = true;
        SetStatus($"🎤 Listening ({_speechLanguage().ToUpperInvariant()})… release the button to write the words", false);
        _dictationStart = OpenMicrophoneAsync(speech);
        if (partial is not null)
            _partials = StreamAsync(speech, _dictationStart, partial);
    }

    /// <summary>While the 🎤 is held: the speech so far transcribed again (one pass right after the other).</summary>
    private async Task StreamAsync(SpeechRecognitionService speech, Task<bool> started, Action<string> partial)
    {
        if (!await started)
            return;
        // Not at the same time as the warm-up: one transcription at a time with the same model.
        if (_dictationWarmUp is { } warmUp)
            await warmUp;
        var shown = string.Empty;
        while (IsDictating)
        {
            // A short breath between the passes (a pass on too little speech returns at once).
            await Task.Delay(150);
            if (!IsDictating)
                break;
            try
            {
                var text = await speech.TranscribeSoFarAsync(_speechLanguage());
                if (IsDictating && text.Length > 0 && text != shown)
                {
                    shown = text;
                    partial(text);
                }
            }
            catch (Exception ex)
            {
                ActivityLog.Error("GhostWriter", "Dictation (words so far): " + ex.Message);
                return;
            }
        }
    }

    private async Task<bool> OpenMicrophoneAsync(SpeechRecognitionService speech)
    {
        try
        {
            await Task.Run(speech.Start).WaitAsync(TimeSpan.FromSeconds(6));
            return true;
        }
        catch (Exception ex)
        {
            // Given up: the microphone, if it opens later, is closed at once (it never records unseen).
            speech.Cancel();
            IsDictating = false;
            SetStatus("Dictation: " + (ex is TimeoutException ? "the microphone does not open (is it allowed?)" : ex.Message), true);
            ActivityLog.Error("GhostWriter", "Dictation microphone: " + ex.Message);
            return false;
        }
    }

    /// <summary>Stops recording (the 🎤 is released) and returns the words spoken; empty when nothing was heard.</summary>
    public async Task<string> StopDictationAsync()
    {
        if (_dictationStart is not { } start || _dictation is not { } speech)
            return string.Empty;
        _dictationStart = null;
        if (!await start)
            return string.Empty;
        IsDictating = false;
        IsTranscribing = true;
        SetStatus("🎤 Recognising the speech…", false);
        try
        {
            // The pass on the words so far ends first: one transcription at a time with the same model.
            if (_partials is { } partials)
            {
                _partials = null;
                await partials;
            }
            if (_dictationWarmUp is { } warmUp)
                await warmUp;
            var text = await speech.StopAndTranscribeAsync(_speechLanguage());
            SetStatus(text.Length == 0 ? "🎤 Nothing heard: hold the button while speaking" : "🎤 " + text, false);
            if (text.Length > 0)
                Journal(WriterJournalActions.Dictation, generated: text, comment: $"Whisper ({_speechLanguage()})");
            return text;
        }
        catch (Exception ex)
        {
            SetStatus("Dictation: " + ex.Message, true);
            ActivityLog.Error("GhostWriter", "Dictation: " + ex.Message);
            return string.Empty;
        }
        finally
        {
            IsTranscribing = false;
        }
    }

    /// <summary>Releases the microphone and the Whisper model (the window closes).</summary>
    public void Dispose()
    {
        // The changes not saved stay in their draft (offered back at the next opening).
        FlushDraft();
        _dictation?.Dispose();
        _dictation = null;
    }

    // ── The local draft: the changes not saved, kept on the disk in case the window or the chat closes ──

    /// <summary>A draft on the disk: the writing, its id and version in the search engine (none for a new one), when it was written.</summary>
    public sealed class Draft
    {
        public DateTimeOffset SavedAt { get; set; }
        public string? WritingId { get; set; }
        public int Version { get; set; }
        public GhostWriterEntry Writing { get; set; } = new();

        /// <summary>The file the draft was read from.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Path { get; set; } = string.Empty;
    }

    /// <summary>Where the drafts are kept: ~/kaonomi/writer/drafts (the ones set aside in its "discarded" folder).</summary>
    public static string DraftDirectory => Path.Combine(ChatSettings.KaonomiHome, "writer", "drafts");

    /// <summary>The name of the draft of a writing not saved yet in the search engine (one per window).</summary>
    private readonly string _newDraftName = "new-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The draft file written last by this window (null: none).</summary>
    private string? _draftPath;

    private CancellationTokenSource? _draftDelay;

    /// <summary>The draft file of the writing open now.</summary>
    private string DraftPath => Path.Combine(DraftDirectory, (WritingId is { Length: > 0 } id ? GhostWriterEntry.Slug(id) : _newDraftName) + ".json");

    /// <summary>A change: the draft is written 3 seconds after the last one (not at each letter).</summary>
    private void ScheduleDraft()
    {
        _draftDelay?.Cancel();
        _draftDelay = new CancellationTokenSource();
        var token = _draftDelay.Token;
        _ = Task.Delay(TimeSpan.FromSeconds(3), token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                WriteDraft();
        }, CancellationToken.None, TaskContinuationOptions.None,
            SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The draft written now when one is waiting (the window closes).</summary>
    private void FlushDraft()
    {
        if (_draftDelay is null)
            return;
        _draftDelay.Cancel();
        _draftDelay = null;
        WriteDraft();
    }

    /// <summary>Writes the writing, while it has changes not saved, in its draft file (a temporary file renamed: never half written).</summary>
    private void WriteDraft()
    {
        if (!IsDirty)
            return;
        try
        {
            var path = DraftPath;
            Directory.CreateDirectory(DraftDirectory);
            var json = JsonSerializer.Serialize(new Draft { SavedAt = DateTimeOffset.Now, WritingId = WritingId, Version = Version, Writing = Snapshot() });
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
            // The writing got its id (its first save): the draft under the name of a new writing is no longer its draft.
            if (_draftPath is not null && _draftPath != path && File.Exists(_draftPath))
                File.Delete(_draftPath);
            _draftPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ActivityLog.Error("GhostWriter", "The draft could not be written", ex);
        }
    }

    /// <summary>The draft of this window is no longer needed (the writing is saved, or replaced by another one).</summary>
    private void DeleteDraft()
    {
        _draftDelay?.Cancel();
        _draftDelay = null;
        foreach (var path in new[] { _draftPath, DraftPath }.OfType<string>().Distinct())
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ActivityLog.Error("GhostWriter", $"The draft {path} could not be deleted", ex);
            }
        }
        _draftPath = null;
    }

    /// <summary>The drafts on the disk, newest first (for one writing, or all of them when <paramref name="writingId"/> is null).</summary>
    private static List<Draft> Drafts(string? writingId)
    {
        var drafts = new List<Draft>();
        if (!Directory.Exists(DraftDirectory))
            return drafts;
        foreach (var path in Directory.GetFiles(DraftDirectory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Draft>(File.ReadAllText(path)) is { } draft
                    && (writingId is null || string.Equals(draft.WritingId, writingId, StringComparison.OrdinalIgnoreCase)))
                {
                    draft.Path = path;
                    drafts.Add(draft);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                ActivityLog.Error("GhostWriter", $"The draft {path} could not be read", ex);
            }
        }
        return drafts.OrderByDescending(d => d.SavedAt).ToList();
    }

    /// <summary>
    /// Offers back the draft left by a window closed (or a chat stopped) with changes not saved: the newest one, or the
    /// one of <paramref name="writingId"/> when it is newer than <paramref name="savedOn"/> (the version of the search
    /// engine). Restored, the writing is the draft, not saved yet; declined, the draft is set aside (in "discarded").
    /// </summary>
    public async Task OfferDraftAsync(string? writingId = null, DateTimeOffset? savedOn = null)
    {
        var draft = Drafts(writingId).FirstOrDefault(d => d.Path != _draftPath && (savedOn is null || d.SavedAt > savedOn));
        if (draft is null)
            return;
        var title = draft.Writing.Title.Trim().Length > 0 ? draft.Writing.Title.Trim() : "Untitled writing";
        var words = draft.Writing.Chapters.Sum(c => Words(c.Text));
        if (await ConfirmAsync($"A draft of \"{title}\" with changes not saved was kept on {draft.SavedAt.LocalDateTime:yyyy-MM-dd HH:mm} "
                               + $"({draft.Writing.Chapters.Count} chapter(s), {words} word(s)). Restore it?", "Restore"))
        {
            Reset(draft.Writing, draft.WritingId, draft.Version);
            _draftPath = draft.Path;
            _changes++;
            IsDirty = true;
            SetStatus($"Draft of \"{title}\" restored: save it to keep it in the search engine", false);
            ActivityLog.Info("GhostWriter", $"Draft {Path.GetFileName(draft.Path)} restored");
            Journal(WriterJournalActions.DraftRestored, original: FullText(Chapters), comment: $"draft of {draft.SavedAt.LocalDateTime:yyyy-MM-dd HH:mm}");
            return;
        }
        try
        {
            var discarded = Path.Combine(DraftDirectory, "discarded");
            Directory.CreateDirectory(discarded);
            File.Move(draft.Path, Path.Combine(discarded, $"{Path.GetFileNameWithoutExtension(draft.Path)}-{draft.SavedAt:yyyyMMdd-HHmmss}.json"), overwrite: true);
            SetStatus($"Draft of \"{title}\" set aside in {discarded}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("GhostWriter", $"The draft {draft.Path} could not be set aside", ex);
        }
    }

    /// <summary>Where the pictures inserted in the chapters are copied: the Pictures folder of the system, kaonomi/write.</summary>
    public static string PicturesDirectory => Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "write");

    /// <summary>The AI tools of the editor of the chapters (the model of the chat, Fooocus).</summary>
    public WritingAssistant Assistant { get; }

    // ── General information ──

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _goals = string.Empty;

    [ObservableProperty]
    private string _audienceDescription = string.Empty;

    [ObservableProperty]
    private string _author = Environment.UserName;

    [ObservableProperty]
    private string _style = string.Empty;

    // ── Actors, locations, chapters ──

    public ObservableCollection<WriterActor> Actors { get; } = [];
    public ObservableCollection<WriterLocation> Locations { get; } = [];
    public ObservableCollection<WriterChapter> Chapters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedActor))]
    [NotifyCanExecuteChangedFor(nameof(DisqualifyActorCommand))]
    private WriterActor? _selectedActor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedLocation))]
    [NotifyCanExecuteChangedFor(nameof(RemoveLocationCommand))]
    private WriterLocation? _selectedLocation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedChapter))]
    [NotifyCanExecuteChangedFor(nameof(RemoveChapterCommand), nameof(MoveChapterUpCommand), nameof(MoveChapterDownCommand))]
    private WriterChapter? _selectedChapter;

    public bool HasSelectedActor => SelectedActor is not null;
    public bool HasSelectedLocation => SelectedLocation is not null;
    public bool HasSelectedChapter => SelectedChapter is not null;

    // ── State ──

    /// <summary>True when the writing has changes not saved.</summary>
    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>True while the server is called.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The id of the writing in the search engine (the slug of its title), null until it is saved there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WritingInfo), nameof(IsInEngine))]
    [NotifyCanExecuteChangedFor(nameof(DeleteFromServerCommand))]
    private string? _writingId;

    /// <summary>Its version in the search engine (1 at its creation, one more at each save).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WritingInfo))]
    private int _version;

    /// <summary>The search typed for the writings of the search engine (empty: every writing, by title).</summary>
    [ObservableProperty]
    private string _search = string.Empty;

    /// <summary>The writings of the search engine found by the search (or all of them).</summary>
    public ObservableCollection<GhostWriterEntry> Writings { get; } = [];

    public bool IsInEngine => WritingId is not null;

    public string WritingInfo => WritingId is null ? "Not in the search engine yet" : $"{WritingId} · version {Version}";

    /// <summary>What the writing holds, for the status bar.</summary>
    public string Summary =>
        $"{Actors.Count} actor(s), {Locations.Count} location(s), {Chapters.Count} chapter(s), {Chapters.Sum(c => Words(c.Text))} word(s)";

    /// <summary>The count of the words of a text.</summary>
    public static int Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }

    // ── The changes ──

    /// <summary>Every item added is watched: a change of one of its fields is a change of the writing.</summary>
    private void Watch(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (INotifyPropertyChanged item in e.OldItems)
                item.PropertyChanged -= ItemChanged;
        if (e.NewItems is not null)
            foreach (INotifyPropertyChanged item in e.NewItems)
                item.PropertyChanged += ItemChanged;
        Touched();
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e) => Touched();

    /// <summary>A change the items do not tell (a footnote added, changed or removed): the writing has changes.</summary>
    public void MarkDirty() => Touched();

    /// <summary>
    /// The count of the changes made: a save remembers it when it starts, and the writing stays "not saved" when
    /// changes were made while the save was going on (they are not in what was sent).
    /// </summary>
    private long _changes;

    private void Touched()
    {
        if (_loading)
            return;
        _changes++;
        IsDirty = true;
        OnPropertyChanged(nameof(Summary));
        ScheduleDraft();
    }

    // ── The journal: an entry in the search engine for each action that changes the text (it is never erased) ──

    /// <summary>
    /// Adds an entry to the journal of the writing (<see cref="WriterJournal"/>): its title and id (the slug of its title
    /// before its first save), the action, the chapter, the texts given, generated and corrected.
    /// </summary>
    public void Journal(string action, WriterChapter? chapter = null, string original = "", string generated = "", string corrected = "",
        string comment = "", bool usesModel = false)
    {
        var title = Title.Trim();
        WriterJournal.Add(_server, new WriterJournalEntry
        {
            AppName = "KAONOMI.Chat",
            ProjectId = ProjectId,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            WritingId = WritingId ?? GhostWriterEntry.Slug(title),
            WritingTitle = title,
            Action = action,
            Chapter = chapter is null ? string.Empty : $"{chapter.Position}. {chapter.Title}",
            OriginalText = original,
            GeneratedText = generated,
            CorrectedText = corrected,
            Comment = comment,
            Model = usesModel ? Assistant.CurrentModelName ?? string.Empty : string.Empty,
            Happened = DateTimeOffset.Now
        });
    }

    /// <summary>The entries of the journal of the writing (the JOURNAL tab), newest first.</summary>
    public ObservableCollection<WriterJournalEntry> JournalEntries { get; } = [];

    [ObservableProperty]
    private WriterJournalEntry? _selectedJournalEntry;

    /// <summary>How many entries the journal shows, and how many wait for the server.</summary>
    [ObservableProperty]
    private string _journalStatus = "The journal of the writing: REFRESH reads it from the search engine";

    /// <summary>The id of the writing in the journal: its id, or the slug of its title before its first save.</summary>
    public string JournalWritingId => WritingId ?? GhostWriterEntry.Slug(Title.Trim());

    /// <summary>Reads the journal of the writing from the search engine (its 500 last entries).</summary>
    [RelayCommand]
    private async Task RefreshJournalAsync()
    {
        var id = JournalWritingId;
        try
        {
            var entries = await _server.GetWriterJournalAsync(id, 500);
            var selected = SelectedJournalEntry?.Id;
            JournalEntries.Clear();
            foreach (var entry in entries)
                JournalEntries.Add(entry);
            SelectedJournalEntry = JournalEntries.FirstOrDefault(e => e.Id == selected) ?? JournalEntries.FirstOrDefault();
            var waiting = WaitingJournalEntries();
            JournalStatus = $"{JournalEntries.Count} entr{(JournalEntries.Count == 1 ? "y" : "ies")} for \"{id}\""
                            + (waiting > 0 ? $" · {waiting} waiting for the server (sent with the next entry)" : string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            JournalStatus = "The journal could not be read: " + ex.Message;
        }
    }

    /// <summary>The entries of the journal not in the search engine yet (the server did not answer).</summary>
    private static int WaitingJournalEntries()
    {
        try
        {
            return File.Exists(WriterJournal.PendingPath) ? File.ReadLines(WriterJournal.PendingPath).Count(l => l.Trim().Length > 0) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The whole text of the writing (its chapters in Markdown), for the entry of a save.</summary>
    private static string FullText(IEnumerable<WriterChapter> chapters) =>
        string.Join("\n\n", chapters.Select(c => $"# {c.Position}. {c.Title}\n\n{c.Text.Trim()}"));

    /// <summary>True while the save running was started by the autosave (its journal entry says so).</summary>
    private bool _autoSaving;

    /// <summary>Stops watching the items of <paramref name="items"/> (before a Clear, which does not give them).</summary>
    private void Unwatch(System.Collections.IEnumerable items)
    {
        foreach (INotifyPropertyChanged item in items)
            item.PropertyChanged -= ItemChanged;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Title) or nameof(Description) or nameof(Goals) or nameof(AudienceDescription) or nameof(Author) or nameof(Style) or nameof(WritingSummary) or nameof(ProjectId))
            Touched();
    }

    [RelayCommand]
    private void AddActor()
    {
        var actor = new WriterActor { Name = $"Actor {Actors.Count + 1}" };
        Actors.Add(actor);
        SelectedActor = actor;
        SetStatus($"Actor \"{actor.Name}\" added", false);
        Journal(WriterJournalActions.ActorAdded, comment: actor.Name);
    }

    /// <summary>Disqualifies the actor selected: it is not removed, only marked (<see cref="WriterActor.Disqualified"/>).</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedActor))]
    private void DisqualifyActor()
    {
        if (SelectedActor is not { } actor)
            return;
        actor.Disqualified = true;
        Journal(WriterJournalActions.ActorDisqualified, comment: actor.Name);
        SetStatus($"Actor \"{actor.Name}\" disqualified (kept in the writing; untick Disqualified to bring them back)", false);
    }

    /// <summary>Where the pictures of the actors of this writing are kept: Pictures/kaonomi/writer/&lt;the slug of its title&gt;.</summary>
    public string ActorPicturesDirectory =>
        Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "writer", GhostWriterEntry.Slug(WritingId ?? Title));

    /// <summary>Copies <paramref name="file"/> into <see cref="ActorPicturesDirectory"/> (named after the actor) and makes it the picture of <paramref name="actor"/>.</summary>
    public bool SetActorPicture(WriterActor actor, string file)
    {
        try
        {
            var target = ActorPictureTarget(actor, Path.GetExtension(file));
            File.Copy(file, target);
            actor.ImagePath = target;
            SetStatus($"Picture of \"{actor.Name}\": {target}", false);
            Journal(WriterJournalActions.ActorPicture, original: file, generated: target, comment: $"{actor.Name}: picture uploaded");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"The picture was not copied: {ex.Message}", true);
            return false;
        }
    }

    /// <summary>
    /// Generates the picture of <paramref name="actor"/> with Fooocus from <paramref name="prompt"/> (a portrait), moves
    /// it into <see cref="ActorPicturesDirectory"/> and makes it the picture of the actor.
    /// </summary>
    public async Task GenerateActorPictureAsync(WriterActor actor, string prompt)
    {
        IsBusy = true;
        SetStatus($"Fooocus is drawing \"{actor.Name}\"…", false);
        try
        {
            var generated = await Assistant.GenerateImageAsync(prompt, "896*1152", "Speed", CancellationToken.None);
            var target = ActorPictureTarget(actor, Path.GetExtension(generated));
            File.Move(generated, target);
            actor.ImagePath = target;
            SetStatus($"Picture of \"{actor.Name}\" generated: {target}", false);
            Journal(WriterJournalActions.ActorPicture, original: prompt, generated: target, comment: $"{actor.Name}: picture generated with Fooocus");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetStatus("The picture was not generated: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A prompt for the portrait of <paramref name="actor"/>: their name, description and what they are, in the style of the writing.</summary>
    public string ActorPrompt(WriterActor actor)
    {
        var parts = new[] { $"Portrait of {actor.Name}", actor.Description, actor.HisStrength, Style.Length > 0 ? $"style: {Style}" : "" }
            .Select(p => p.Trim()).Where(p => p.Length > 0);
        return string.Join(", ", parts);
    }

    /// <summary>A free file name in <see cref="ActorPicturesDirectory"/> for the picture of <paramref name="actor"/>.</summary>
    private string ActorPictureTarget(WriterActor actor, string extension)
    {
        var directory = ActorPicturesDirectory;
        Directory.CreateDirectory(directory);
        var name = GhostWriterEntry.Slug(actor.Name);
        var target = Path.Combine(directory, name + extension);
        for (var i = 2; File.Exists(target); i++)
            target = Path.Combine(directory, $"{name}-{i}{extension}");
        return target;
    }

    [RelayCommand]
    private void AddLocation()
    {
        var location = new WriterLocation { Name = $"Location {Locations.Count + 1}" };
        Locations.Add(location);
        SelectedLocation = location;
        SetStatus($"Location \"{location.Name}\" added", false);
        Journal(WriterJournalActions.LocationAdded, comment: location.Name);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedLocation))]
    private async Task RemoveLocation()
    {
        if (SelectedLocation is not { } location)
            return;
        if (!await ConfirmAsync($"Remove the location \"{location.Name}\" from the writing? (it is gone once the writing is saved)", "Remove"))
            return;
        var index = Locations.IndexOf(location);
        Locations.Remove(location);
        SelectedLocation = Locations.Count == 0 ? null : Locations[Math.Min(index, Locations.Count - 1)];
        SetStatus($"Location \"{location.Name}\" removed", false);
        Journal(WriterJournalActions.LocationRemoved, original: JsonSerializer.Serialize(location), comment: location.Name);
    }

    [RelayCommand]
    private void AddChapter()
    {
        var chapter = new WriterChapter { Title = $"Chapter {Chapters.Count + 1}", Position = Chapters.Count + 1 };
        // After the chapter selected, else at the end.
        var index = SelectedChapter is { } selected ? Chapters.IndexOf(selected) + 1 : Chapters.Count;
        Chapters.Insert(index, chapter);
        Renumber();
        SelectedChapter = chapter;
        SetStatus($"Chapter \"{chapter.Title}\" added: double-click it to write its text", false);
        Journal(WriterJournalActions.ChapterAdded, chapter);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedChapter))]
    private async Task RemoveChapter()
    {
        if (SelectedChapter is not { } chapter)
            return;
        var words = Words(chapter.Text);
        if (!await ConfirmAsync($"Remove the chapter {chapter.Position}. \"{chapter.Title}\""
                                + (words > 0 ? $" and its text ({words} word(s))" : string.Empty)
                                + (chapter.Footnotes.Count > 0 ? $" and its {chapter.Footnotes.Count} footnote(s)" : string.Empty)
                                + "? (it is gone once the writing is saved)", "Remove"))
            return;
        var index = Chapters.IndexOf(chapter);
        // Its text and its footnotes are in the journal: the chapter can be found back.
        Journal(WriterJournalActions.ChapterRemoved, chapter, original: chapter.Text,
            comment: $"{words} word(s), {chapter.Footnotes.Count} footnote(s)"
                     + (chapter.Footnotes.Count > 0 ? ": " + JsonSerializer.Serialize(chapter.Footnotes) : string.Empty));
        Chapters.Remove(chapter);
        Renumber();
        SelectedChapter = Chapters.Count == 0 ? null : Chapters[Math.Min(index, Chapters.Count - 1)];
        // Its text window closes: what would be typed there would go nowhere.
        ChapterRemoved?.Invoke(chapter);
        SetStatus($"Chapter \"{chapter.Title}\" removed", false);
    }

    /// <summary>Raised when a chapter is removed: its text window closes.</summary>
    public event Action<WriterChapter>? ChapterRemoved;

    /// <summary>Asks the user (a message and the label of the button); false when not answered (no window to ask in).</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    [RelayCommand(CanExecute = nameof(HasSelectedChapter))]
    private void MoveChapterUp() => MoveChapter(-1);

    [RelayCommand(CanExecute = nameof(HasSelectedChapter))]
    private void MoveChapterDown() => MoveChapter(1);

    private void MoveChapter(int by)
    {
        if (SelectedChapter is not { } chapter)
            return;
        var index = Chapters.IndexOf(chapter);
        var target = index + by;
        if (target < 0 || target >= Chapters.Count)
            return;
        Chapters.Move(index, target);
        Renumber();
        Journal(WriterJournalActions.ChapterMoved, chapter, comment: $"from position {index + 1} to {target + 1}");
    }

    /// <summary>The positions of the chapters: their order in the list, from 1.</summary>
    private void Renumber()
    {
        for (var i = 0; i < Chapters.Count; i++)
            Chapters[i].Position = i + 1;
    }

    // ── New, open, save ──

    /// <summary>Raised when the chapters are replaced (a new writing, another one opened): their text windows close.</summary>
    public event Action? ChaptersReplaced;

    private void Reset(GhostWriterEntry entry, string? writingId, int version)
    {
        ChaptersReplaced?.Invoke();
        _loading = true;
        Title = entry.Title;
        Description = entry.Description;
        Goals = entry.Goals;
        AudienceDescription = entry.AudienceDescription;
        Author = entry.Author;
        Style = entry.Style;
        WritingSummary = entry.Summary ?? string.Empty;
        ProjectId = entry.ProjectId;
        // A Clear does not give the items it removes: they are let go first (else they are kept, and a change of one of
        // them would mark the writing opened now as changed).
        Unwatch(Actors);
        Unwatch(Locations);
        Unwatch(Chapters);
        Actors.Clear();
        foreach (var actor in entry.Actors)
            Actors.Add(actor);
        Locations.Clear();
        foreach (var location in entry.Locations)
            Locations.Add(location);
        Chapters.Clear();
        foreach (var chapter in entry.Chapters.OrderBy(c => c.Position))
            Chapters.Add(chapter);
        Renumber();
        SelectedActor = Actors.FirstOrDefault();
        SelectedLocation = Locations.FirstOrDefault();
        SelectedChapter = Chapters.FirstOrDefault();
        WritingId = writingId;
        Version = version;
        _loading = false;
        IsDirty = false;
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Starts an empty writing (the caller asks about the changes not saved first).</summary>
    [RelayCommand]
    private void New()
    {
        // The changes were given up (the view asked): their draft goes too.
        DeleteDraft();
        Reset(new GhostWriterEntry { Author = Environment.UserName }, null, 0);
        SetStatus("New writing: give it a title, then its actors, its locations and its chapters", false);
    }

    /// <summary>
    /// The writing as it is now, a copy of its own (to save it, publish it or keep a draft of it): what is typed after
    /// does not change it, while it is sent or written.
    /// </summary>
    public GhostWriterEntry Snapshot() =>
        JsonSerializer.Deserialize<GhostWriterEntry>(JsonSerializer.Serialize(ToEntry())) ?? ToEntry();

    private GhostWriterEntry ToEntry() => new()
    {
        Id = WritingId ?? GhostWriterEntry.Slug(Title),
        AppName = "KAONOMI.Chat",
        ProjectId = ProjectId,
        MachineName = Environment.MachineName,
        UserName = Environment.UserName,
        Title = Title.Trim(),
        Description = Description.Trim(),
        Goals = Goals.Trim(),
        AudienceDescription = AudienceDescription.Trim(),
        Author = Author.Trim(),
        Style = Style.Trim(),
        Actors = Actors.ToList(),
        Locations = Locations.ToList(),
        Chapters = Chapters.ToList(),
        Summary = WritingSummary.Trim()
    };

    // ── Publish: the summary of the writing ──

    /// <summary>The summary of the whole writing, generated (<see cref="GenerateSummaryAsync"/>) then edited; saved with it.</summary>
    [ObservableProperty]
    private string _writingSummary = string.Empty;

    /// <summary>The lengths of summary offered.</summary>
    public static readonly string[] SummaryLengths = ["Short", "Medium", "Long"];

    /// <summary>The length of the summary to generate (<see cref="SummaryLengths"/>).</summary>
    [ObservableProperty]
    private string _summaryLength = "Medium";

    /// <summary>The language of the writing for the words of the exports (Summary, Notes…): the one of the voice input of the chat.</summary>
    public string Language => _speechLanguage();

    /// <summary>Above this many characters of chapters, each chapter is summarized first, then the summaries together.</summary>
    private const int SummaryInputLimit = 12000;

    /// <summary>
    /// Generates the summary of the writing with the model of the chat, from its title, description, goals and the text
    /// of its chapters (each chapter summarized first when they are long); it replaces <see cref="WritingSummary"/>.
    /// </summary>
    public async Task GenerateSummaryAsync(CancellationToken cancellationToken)
    {
        var chapters = Chapters.Where(c => c.Text.Trim().Length > 0).ToList();
        if (chapters.Count == 0)
            throw new InvalidOperationException("No chapter has a text yet: write one first");
        var length = SummaryLength switch
        {
            "Short" => "un seul paragraphe de 3 à 5 phrases",
            "Long" => "un paragraphe d'introduction, puis un court paragraphe par chapitre",
            _ => "environ 250 à 300 mots, en quelques paragraphes"
        };
        string Chapter(WriterChapter c) => $"## {c.Position}. {c.Title}\n{PlainText(c.Text)}";
        var body = string.Join("\n\n", chapters.Select(Chapter));
        // A long writing: the chapters one by one first (the context of the model is limited).
        if (body.Length > SummaryInputLimit)
        {
            var parts = new List<string>();
            foreach (var chapter in chapters)
            {
                SetStatus($"Summary: chapter {chapter.Position} of {Chapters.Count}…", false);
                var part = await Assistant.EvaluateAsync(PlainText(chapter.Text),
                    "Tu résumes fidèlement un chapitre. Réponds uniquement par le résumé, dans la langue du chapitre, sans titre ni commentaire.",
                    $"Résume ce chapitre « {chapter.Title} » en 5 à 8 phrases : les faits, les personnages, ce qui change.", cancellationToken);
                parts.Add($"## {chapter.Position}. {chapter.Title}\n{part.Trim()}");
            }
            body = string.Join("\n\n", parts);
        }
        SetStatus("Summary: the whole writing…", false);
        var summary = await Assistant.EvaluateAsync(body,
            "Tu es un éditeur qui écrit le résumé d'un livre ou d'un article. Réponds uniquement par le résumé, en Markdown simple " +
            "(paragraphes, sans titre), dans la langue du texte, sans commentaire avant ou après.",
            $"Titre : {Title}\nDescription : {Description}\nObjectifs : {Goals}\nPublic : {AudienceDescription}\n\n" +
            $"Écris le résumé de cet écrit en {length}, pour donner envie de le lire à son public, sans rien inventer.",
            cancellationToken);
        WritingSummary = summary.Trim();
        Journal(WriterJournalActions.Summary, generated: WritingSummary, comment: $"{SummaryLength}, {Words(WritingSummary)} words", usesModel: true);
        SetStatus($"Summary generated ({Words(WritingSummary)} words): edit it if needed, it is saved with the writing", false);
        ActivityLog.Info("GhostWriter", $"Summary generated for {WritingId ?? GhostWriterEntry.Slug(Title)} ({SummaryLength}, {Words(WritingSummary)} words)");
    }

    /// <summary>The text of a chapter for the model: the footnote links reduced to their words.</summary>
    private static string PlainText(string markdown) =>
        System.Text.RegularExpressions.Regex.Replace(markdown, @"\[(?<words>[^\]]*)\]\(<?note:\d+>?\)", "${words}").Trim();

    // ── The search engine (through the Kaonomi server) ──

    /// <summary>Every writing by title (empty search), or the writings found.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        try
        {
            var term = Search.Trim();
            var found = term.Length == 0 ? await _server.GetLastGhostWriterAsync(200) : await _server.SearchGhostWriterAsync(term, 100);
            Writings.Clear();
            foreach (var entry in term.Length == 0 ? found.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase) : found.AsEnumerable())
                Writings.Add(entry);
            if (term.Length > 0)
                SetStatus($"{Writings.Count} writing(s) found for \"{term}\"", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The writings of the search engine could not be read: " + ex.Message, true);
        }
    }

    /// <summary>
    /// Saves the writing in the search engine: a new document the first time (its id the slug of the title; refused
    /// when a writing of that title exists), a new version after.
    /// </summary>
    [RelayCommand]
    private async Task SaveToServerAsync()
    {
        var title = Title.Trim();
        if (title.Length == 0)
        {
            SetStatus("Give the writing a title (General tab): it is its name in the search engine", true);
            return;
        }
        IsBusy = true;
        try
        {
            // What is sent: the writing at this instant, and the count of its changes then.
            var changes = _changes;
            var entry = Snapshot();
            var result = WritingId is null ? await _server.CreateGhostWriterAsync(entry) : await _server.UpdateGhostWriterAsync(entry);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("Not saved in the search engine: " + (result.Message ?? "no answer") + (WritingId is null ? " (open it from the list to save a new version of it, or change the title)" : ""), true);
                return;
            }
            WritingId = result.ReturnedId;
            var saved = await _server.GetGhostWriterAsync(result.ReturnedId);
            Version = saved?.Version ?? Version + 1;
            // Changed meanwhile: what was typed during the save is not in the version saved.
            var changedMeanwhile = _changes != changes;
            IsDirty = changedMeanwhile;
            if (!changedMeanwhile)
                DeleteDraft();
            SetStatus($"Writing \"{title}\" saved in the search engine (version {Version})"
                      + (changedMeanwhile ? ": the changes made during the save are not saved yet" : string.Empty), false);
            ActivityLog.Info("GhostWriter", $"Writing {WritingId} saved in the search engine (version {Version}, {Summary})");
            // The text saved (each save replaces the document of the writing: the journal keeps every version).
            Journal(_autoSaving ? WriterJournalActions.AutoSave : WriterJournalActions.Save, original: FullText(entry.Chapters),
                comment: $"version {Version}, {Summary}");
            await SearchAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            SetStatus("Not saved in the search engine: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
            _autoSaving = false;
        }
    }

    /// <summary>Autosave on in the windows opened after (kept while the chat runs).</summary>
    private static bool _autoSaveDefault;

    /// <summary>The writing saved in the search engine every minute while it has changes (the AUTOSAVE box).</summary>
    [ObservableProperty]
    private bool _autoSave = _autoSaveDefault;

    partial void OnAutoSaveChanged(bool value)
    {
        _autoSaveDefault = value;
        SetStatus(value ? "Autosave on: the writing is saved every minute while it has changes" : "Autosave off", false);
    }

    /// <summary>
    /// The minute of the autosave: saves the writing when <see cref="AutoSave"/> is on and it has changes, not while it
    /// is saved or opened, nor while a dictation is shown (its words are not final yet), nor without a title.
    /// </summary>
    public void AutoSaveTick()
    {
        if (!AutoSave || !IsDirty || IsBusy || IsDictating || IsTranscribing || Title.Trim().Length == 0)
            return;
        ActivityLog.Info("GhostWriter", $"Autosave of {WritingId ?? GhostWriterEntry.Slug(Title)}");
        if (SaveToServerCommand.CanExecute(null))
        {
            _autoSaving = true;
            SaveToServerCommand.Execute(null);
        }
    }

    /// <summary>Opens a writing of the list.</summary>
    [RelayCommand]
    private Task OpenEntryAsync(GhostWriterEntry? entry) => entry?.Id is { } id ? OpenFromServerAsync(id) : Task.CompletedTask;

    /// <summary>Opens the writing <paramref name="id"/> of the search engine.</summary>
    public async Task<bool> OpenFromServerAsync(string id)
    {
        IsBusy = true;
        try
        {
            var entry = await _server.GetGhostWriterAsync(id);
            if (entry is null)
            {
                SetStatus($"No writing {id} in the search engine", true);
                return false;
            }
            // The changes of the writing open before were given up (the view asked): their draft goes too.
            DeleteDraft();
            Reset(entry, entry.Id, entry.Version);
            SetStatus($"Opened from the search engine: {entry.Title} (version {entry.Version}, {Summary})", false);
            ActivityLog.Info("GhostWriter", $"Writing {entry.Id} opened from the search engine (version {entry.Version})");
            // Changes of this writing kept in a draft after its last version: offered back.
            await OfferDraftAsync(entry.Id, entry.Date);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus($"The writing {id} could not be opened: {ex.Message}", true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Marks the writing deleted in the search engine (it is never erased); the window keeps it.</summary>
    [RelayCommand(CanExecute = nameof(IsInEngine))]
    private async Task DeleteFromServerAsync()
    {
        if (WritingId is not { } id)
            return;
        IsBusy = true;
        try
        {
            var result = await _server.DeleteGhostWriterAsync(id);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("Not deleted: " + (result.Message ?? "no answer"), true);
                return;
            }
            SetStatus($"Writing {id} deleted from the search engine (the window keeps it: save it again to put it back)", false);
            Journal(WriterJournalActions.Delete, comment: "marked deleted in the search engine");
            ActivityLog.Info("GhostWriter", $"Writing {id} deleted from the search engine");
            WritingId = null;
            Version = 0;
            IsDirty = true;
            await SearchAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("Not deleted: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>
/// The text window of a chapter of <c>/write</c> (a double-click on the chapter): the chapter and its writing (to save
/// it), written with the editor and the ribbon of <c>/work</c>; its AI tools are those of the writing.
/// </summary>
public sealed class ChapterTextWindowViewModel(GhostWriterWindowViewModel writing, WriterChapter chapter) : ITapeAssistant
{
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _images = new();

    public GhostWriterWindowViewModel Writing { get; } = writing;
    public WriterChapter Chapter { get; } = chapter;

    /// <summary>The picture of a local path, read once.</summary>
    public Task<Bitmap?> LoadImageAsync(string path) =>
        _images.GetOrAdd(path, p => PictureLibrary.DecodeAsync(p, 1600, CancellationToken.None));

    /// <summary>Copies <paramref name="files"/> into <see cref="GhostWriterWindowViewModel.PicturesDirectory"/> (a name taken gets -2, -3…); returns the copies.</summary>
    public List<string> CopyPictures(IEnumerable<string> files)
    {
        var directory = GhostWriterWindowViewModel.PicturesDirectory;
        Directory.CreateDirectory(directory);
        var copies = new List<string>();
        foreach (var file in files)
        {
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(file)) == Path.GetFullPath(directory))
                {
                    copies.Add(file);
                    continue;
                }
                var name = Path.GetFileNameWithoutExtension(file);
                var extension = Path.GetExtension(file);
                var target = Path.Combine(directory, name + extension);
                for (var i = 2; File.Exists(target); i++)
                    target = Path.Combine(directory, $"{name}-{i}{extension}");
                File.Copy(file, target);
                copies.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Writing.SetStatus($"{Path.GetFileName(file)} not copied: {ex.Message}", true);
            }
        }
        return copies;
    }

    // ── Footnotes ──

    /// <summary>A new footnote anchored on <paramref name="anchor"/>, numbered after the others (not added yet).</summary>
    public WriterFootnote NewFootnote(string anchor) => new()
    {
        Number = Chapter.Footnotes.Count == 0 ? 1 : Chapter.Footnotes.Max(n => n.Number) + 1,
        Anchor = anchor.Trim()
    };

    /// <summary>The footnote <paramref name="number"/>, or null.</summary>
    public WriterFootnote? Footnote(int number) => Chapter.Footnotes.FirstOrDefault(n => n.Number == number);

    /// <summary>Adds <paramref name="note"/> to the chapter (its anchor is linked in the text by the view).</summary>
    public void AddFootnote(WriterFootnote note)
    {
        Chapter.Footnotes.Add(note);
        Writing.MarkDirty();
        Writing.Journal(WriterJournalActions.FootnoteAdded, Chapter, original: note.Anchor, generated: NoteText(note), comment: $"note {note.Number}");
    }

    /// <summary>The text of a footnote and its links, for the journal.</summary>
    private static string NoteText(WriterFootnote note) =>
        string.Join(Environment.NewLine, new[] { note.Text.Trim() }.Concat(note.Links.Select(l => $"{l.Title} <{l.Url}>")));

    /// <summary>
    /// Removes <paramref name="note"/> from the chapter and returns <paramref name="text"/> without its anchor link
    /// (the words stay, the link goes).
    /// </summary>
    public string RemoveFootnote(WriterFootnote note, string text)
    {
        Chapter.Footnotes.Remove(note);
        Writing.MarkDirty();
        Writing.Journal(WriterJournalActions.FootnoteRemoved, Chapter, original: NoteText(note), comment: $"note {note.Number} on \"{note.Anchor}\"");
        var link = System.Text.RegularExpressions.Regex.Escape(WriterChapter.NoteLink(note.Number));
        return System.Text.RegularExpressions.Regex.Replace(text, @"\[(?<words>(?:[^\[\]]|\[[^\]]*\])*)\]\(<?" + link + @">?\)", "${words}");
    }

    // ── AI-Check: is the chapter what it was meant to be? ──

    /// <summary>What the chapter is meant to be (its description, perception goal and ambiance), for the prompts.</summary>
    private string Intent() =>
        $"Titre du chapitre : {Chapter.Title}\n" +
        $"Description (ce qui doit s'y passer) : {Or(Chapter.Description)}\n" +
        $"Objectif de perception (ce que le lecteur doit ressentir ou comprendre) : {Or(Chapter.PerceptionGoal)}\n" +
        $"Ambiance : {Or(Chapter.Ambiance)}";

    private static string Or(string text) => string.IsNullOrWhiteSpace(text) ? "(non précisé)" : text.Trim();

    /// <summary>
    /// The AI-Check: the model of the chat says, in Markdown, whether the text of the chapter matches its description,
    /// its perception goal and its ambiance, what is off and how to bring it closer.
    /// </summary>
    public async Task<string> CheckAsync(CancellationToken cancellationToken)
    {
        var text = Chapter.Text;
        var analysis = await Check(text, cancellationToken);
        Writing.Journal(WriterJournalActions.AiCheck, Chapter, original: text, generated: analysis, usesModel: true);
        return analysis;
    }

    private Task<string> Check(string text, CancellationToken cancellationToken) =>
        Writing.Assistant.EvaluateAsync(text,
            "Tu es un éditeur littéraire exigeant et bienveillant. Réponds en Markdown, dans la langue du chapitre, avec des titres courts.",
            "Voici ce que ce chapitre doit être :\n" + Intent() + "\n\n" +
            "Vérifie si le texte ci-dessous s'aligne sur la description, l'objectif de perception et l'ambiance. " +
            "Pour chacun des trois : une note sur 10, ce qui est conforme, ce qui s'en écarte (avec de courtes citations). " +
            "Puis donne les changements les plus utiles pour mieux s'aligner, sans réécrire le texte.",
            cancellationToken);

    /// <summary>
    /// The chapter rewritten to match its description, perception goal and ambiance, the goals and the audience of the
    /// writing (General), following <paramref name="analysis"/> (the AI-Check); the text only, in Markdown.
    /// </summary>
    public async Task<string> RewriteToFitAsync(string analysis, CancellationToken cancellationToken)
    {
        var text = Chapter.Text;
        var rewritten = await RewriteToFit(text, analysis, cancellationToken);
        Writing.Journal(WriterJournalActions.RewriteToFit, Chapter, original: text, corrected: rewritten, comment: "after the AI-Check", usesModel: true);
        return rewritten;
    }

    private Task<string> RewriteToFit(string text, string analysis, CancellationToken cancellationToken) =>
        Writing.Assistant.EvaluateAsync(text,
            "Tu es un écrivain fantôme talentueux. Tu réécris le chapitre qu'on te donne. Réponds uniquement par le texte réécrit, " +
            "en Markdown, dans la langue du chapitre, sans titre ajouté, sans commentaire ni explication avant ou après.",
            "Voici ce que ce chapitre doit être :\n" + Intent() + "\n\n" +
            $"Objectifs de l'écrit entier : {Or(Writing.Goals)}\n" +
            $"Public visé : {Or(Writing.AudienceDescription)}\n" +
            (Writing.Style.Trim().Length > 0 ? $"Style : {Writing.Style.Trim()}\n" : "") + "\n" +
            "Analyse du chapitre actuel :\n" + analysis + "\n\n" +
            "Réécris le texte ci-dessous pour qu'il s'aligne sur la description, l'objectif de perception, l'ambiance, " +
            "les objectifs de l'écrit et son public, en tenant compte de l'analyse. Garde l'histoire, les personnages, " +
            "les faits et la mise en forme Markdown (titres, gras, images), et une longueur proche.",
            cancellationToken);

    /// <inheritdoc />
    public string? CurrentModelName => Writing.Assistant.CurrentModelName;

    /// <inheritdoc />
    public bool CanEvaluate => Writing.Assistant.CanAsk;

    /// <inheritdoc />
    public bool CanGenerateImages => Writing.Assistant.CanGenerateImages;

    /// <inheritdoc />
    public async Task<string> EvaluateAsync(string text, CancellationToken cancellationToken)
    {
        var evaluation = await Writing.Assistant.EvaluateAsync(text,
            "Tu es un éditeur littéraire exigeant et bienveillant. Réponds en Markdown, dans la langue du texte évalué, avec des titres courts.",
            "Analyse ce chapitre et critique-le (rythme, style, cohérence des personnages, tension, clarté). Puis suggère des pistes d'amélioration, sans le réécrire.",
            cancellationToken);
        Writing.Journal(WriterJournalActions.Evaluation, Chapter, original: text, generated: evaluation, usesModel: true);
        return evaluation;
    }

    /// <inheritdoc />
    public async Task<string> TranslateAsync(string text, string language, CancellationToken cancellationToken)
    {
        var translation = await Writing.Assistant.TranslateAsync(text, language, cancellationToken);
        Writing.Journal(WriterJournalActions.Translation, Chapter, original: text, corrected: translation, comment: $"to {language}", usesModel: true);
        return translation;
    }

    /// <inheritdoc />
    public Task<string> DetectLanguageAsync(string text, CancellationToken cancellationToken) =>
        Writing.Assistant.DetectLanguageAsync(text, cancellationToken);

    /// <inheritdoc />
    public async Task<string> RewriteAsync(string text, string mode, CancellationToken cancellationToken, string? language = null)
    {
        var rewritten = await Writing.Assistant.RewriteAsync(text, mode, cancellationToken, language);
        Writing.Journal(WriterJournalActions.Rewrite, Chapter, original: text, corrected: rewritten,
            comment: mode + (language is null ? string.Empty : $" ({language})"), usesModel: true);
        return rewritten;
    }

    /// <inheritdoc />
    public async Task<(string Language, string Code)> GenerateCodeAsync(string prompt, string language, CancellationToken cancellationToken)
    {
        var code = await Writing.Assistant.GenerateCodeAsync(prompt, language, cancellationToken);
        Writing.Journal(WriterJournalActions.CodeGenerated, Chapter, original: prompt, generated: code.Code, comment: code.Language, usesModel: true);
        return code;
    }

    /// <inheritdoc />
    public async Task<string> GenerateImageAsync(string prompt, string aspectRatio, string performance, CancellationToken cancellationToken)
    {
        var path = await Writing.Assistant.GenerateImageAsync(prompt, aspectRatio, performance, cancellationToken);
        Writing.Journal(WriterJournalActions.ImageGenerated, Chapter, original: prompt, generated: path, comment: $"Fooocus {aspectRatio}, {performance}");
        return path;
    }

    /// <summary>The key of the window of <paramref name="chapter"/>: one window per chapter.</summary>
    public static string KeyOf(WriterChapter chapter) => $"{KeyPrefix}{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(chapter)}";

    public const string KeyPrefix = "write-chapter#";
}
