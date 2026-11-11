using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A recording of the project, in the list.</summary>
public partial class RecorderItemViewModel : ViewModelBase
{
    public RecorderItemViewModel(string path, RecorderInfo info)
    {
        Path = path;
        Info = info;
    }

    /// <summary>The WAV file.</summary>
    public string Path { get; }

    public RecorderInfo Info { get; }

    public string Name => string.IsNullOrWhiteSpace(Info.Title) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Info.Title;

    public string Details =>
        $"{Info.Date.ToLocalTime():g} · {RecorderInfo.Clock(Info.Seconds)} · {Info.Annotations.Count} note(s)"
        + (Info.Samples.Count > 0 ? $" · {Info.Samples.Count} sample(s)" : string.Empty);

    public void Changed()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Details));
    }
}

/// <summary>
/// /recorder: an audio recorder. The microphone is listened to as soon as the window is open (its level, its spectrum
/// and the formants of the voice are shown by the view from <see cref="Recorder"/>); ● REC writes it into a WAV file of
/// <c>~/kaonomi/audio/&lt;project&gt;</c>, the project being typed in the window.
/// <para>
/// A recording is described by a document (<see cref="RecorderInfo"/>): what it is, the notes on its moments, each
/// manipulation with its annotation, its named selections and the samples saved from it. As soon as it is recorded,
/// and again at each change, this document is written beside the recording (<c>name.json</c>) and in the search
/// engine of the server (<c>POST /audiorecord/update</c>, index <c>audiorecord</c>).
/// </para>
/// <para>
/// Once a recording is ended (or selected in the list), its spectrogram and its waveform are computed from its file.
/// A part of it can be selected (<see cref="SelectionStart"/>, <see cref="SelectionEnd"/>), listened to once or in a
/// loop with the filters (<see cref="PlaySelection"/>: a gain, a compressor, a band kept or taken out, an equalizer),
/// named, saved as a sample with the filters applied, cut out, or kept alone. Each manipulation first opens a popup
/// for its annotation (<see cref="AskAsync"/>; Cancel gives it up) and is kept in the document with it, the date and
/// the user.
/// </para>
/// </summary>
public partial class RecorderWindowViewModel : ViewModelBase, IDisposable, IProjectDocument
{
    /// <summary>
    /// The project the recording selected is attached to (the id of a project, chosen by its name in the header of the
    /// window), or null; its document is saved at once. Not <see cref="Project"/>, the folder the recordings go to.
    /// </summary>
    public string? ProjectId
    {
        get => SelectedRecording?.Info.ProjectId;
        set
        {
            if (IsRecording || SelectedRecording is not { } item || item.Info.ProjectId == value)
                return;
            item.Info.ProjectId = value;
            SaveInfo(item.Path, item.Info);
            OnPropertyChanged();
        }
    }

    /// <summary>True when a recording is selected, and none is running.</summary>
    public bool HasProjectDocument => SelectedRecording is not null && !IsRecording;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The folder of every project: <c>~/kaonomi/audio</c>.</summary>
    public static string Root => Path.Combine(ChatSettings.KaonomiHome, "audio");

    /// <summary>Where the project typed last is remembered.</summary>
    private static string LastProjectFile => Path.Combine(Root, "recorder-project.txt");

    private readonly KaonomiAssistantService? _assistant;
    private WavPlayer? _player;
    private RecorderInfo? _recording;
    private string? _recordingPath;
    private bool _disposed;

    /// <summary>Design-time constructor, and the one of a chat without a server: nothing goes to the search engine.</summary>
    public RecorderWindowViewModel() : this(null)
    {
    }

    /// <summary>The documents of the recordings are also written in the search engine of <paramref name="assistant"/>.</summary>
    public RecorderWindowViewModel(KaonomiAssistantService? assistant)
    {
        _assistant = assistant;
        try
        {
            if (File.Exists(LastProjectFile))
                _project = File.ReadAllText(LastProjectFile).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The default project.
        }
        if (string.IsNullOrWhiteSpace(_project))
            _project = "default";
        LoadRecordings();
    }

    /// <summary>The microphone: listened to, analysed, recorded.</summary>
    public AudioRecorder Recorder { get; } = new();

    /// <summary>The recordings of the project, the newest first.</summary>
    public ObservableCollection<RecorderItemViewModel> Recordings { get; } = [];

    /// <summary>The annotations of the recording running, else of the recording selected, by their time.</summary>
    public ObservableCollection<RecorderAnnotation> Annotations { get; } = [];

    /// <summary>The manipulations of the recording selected (recorded, cut, trim, restore, named selection, sample), the newest first.</summary>
    public ObservableCollection<RecorderAction> History { get; } = [];

    private void ShowHistory(RecorderInfo? info)
    {
        History.Clear();
        foreach (var action in (info?.Actions ?? []).AsEnumerable().Reverse())
            History.Add(action);
    }

    /// <summary>
    /// Asks the user a text in a popup (title, question, initial value, several lines); null when cancelled. Set by
    /// the view; without a view, every manipulation is done with an empty annotation.
    /// </summary>
    public Func<string, string, string, bool, Task<string?>> AskAsync { get; set; } = (_, _, initial, _) => Task.FromResult<string?>(initial);

    /// <summary>The project: the folder of <c>~/kaonomi/audio</c> the recordings go to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Folder))]
    private string _project = string.Empty;

    /// <summary>The title of the next recording (and of the recording selected, once it is).</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private RecorderItemViewModel? _selectedRecording;

    /// <summary>The note being typed.</summary>
    [ObservableProperty]
    private string _newAnnotation = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordLabel), nameof(CanEditProject), nameof(HasProjectDocument))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    private bool _isPlaying;

    /// <summary>The time recorded so far, or the position played over the length of the recording selected.</summary>
    [ObservableProperty]
    private string _timeLabel = "00:00.0";

    /// <summary>
    /// The spectrogram of the recording selected, computed from its file once it is recorded (never while it records);
    /// null while there is none, and while it is being computed (<see cref="IsAnalysing"/>).
    /// </summary>
    [ObservableProperty]
    private Spectrogram? _spectrogram;

    [ObservableProperty]
    private bool _isAnalysing;

    private CancellationTokenSource? _analysis;

    [ObservableProperty]
    private string _status = "Type the project, then ● REC";

    [ObservableProperty]
    private bool _isError;

    /// <summary>What the search engine holds of the recording selected: saved (when), or why not.</summary>
    [ObservableProperty]
    private string _serverLabel = string.Empty;

    public string RecordLabel => IsRecording ? "■ STOP" : "● REC";

    public string PlayLabel => IsPlaying ? "⏸ PAUSE" : "▶ PLAY";

    /// <summary>The project cannot change while it is recorded into.</summary>
    public bool CanEditProject => !IsRecording;

    /// <summary>The folder of the project: its name without what a folder name cannot hold.</summary>
    public string Folder => Path.Combine(Root, FolderName(Project));

    /// <summary>The name of the folder of a project: trimmed, the characters a file name refuses replaced by « - »; « default » when empty.</summary>
    public static string FolderName(string? project)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string((project ?? string.Empty).Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return name.Length == 0 ? "default" : name;
    }

    /// <summary>A time as <c>mm:ss.d</c>.</summary>
    public static string Clock(double seconds) => RecorderInfo.Clock(seconds);

    /// <summary>Opens the microphone for the meters (called by the view once it is shown); a failure is said in the status.</summary>
    public void Listen()
    {
        try
        {
            Recorder.Listen();
            if (!IsRecording)
                Report($"Listening to {Recorder.SourceName} ({Recorder.Rate} Hz) · recordings in {Folder}", false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PortAudioSharp.PortAudioException or DllNotFoundException or TypeInitializationException)
        {
            Report($"The microphone could not be opened: {ex.Message}", true);
            ActivityLog.Error("Recorder", "The microphone could not be opened", ex);
        }
    }

    partial void OnProjectChanged(string value)
    {
        if (IsRecording)
            return;
        StopPlaying();
        LoadRecordings();
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(LastProjectFile, value.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered.
        }
    }

    // ── The documents of the recordings ──

    /// <summary>The recordings of the folder of the project (a WAV without its JSON file is listed too).</summary>
    private void LoadRecordings()
    {
        Recordings.Clear();
        SelectedRecording = null;
        try
        {
            if (!Directory.Exists(Folder))
                return;
            foreach (var path in Directory.EnumerateFiles(Folder, "*.wav").OrderByDescending(File.GetLastWriteTimeUtc))
                Recordings.Add(new RecorderItemViewModel(path, ReadInfo(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"The folder {Folder} could not be read: {ex.Message}", true);
        }
    }

    private RecorderInfo ReadInfo(string path) => ReadInfoFile(Path.ChangeExtension(path, ".json")) ?? FromWav(path);

    private static RecorderInfo? ReadInfoFile(string sidecar)
    {
        try
        {
            if (File.Exists(sidecar) && JsonSerializer.Deserialize<RecorderInfo>(File.ReadAllText(sidecar), Json) is { } info)
                return info;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Recorder", $"{Path.GetFileName(sidecar)} could not be read", ex);
        }
        return null;
    }

    /// <summary>The document of a recording that has none: what the WAV itself says.</summary>
    private RecorderInfo FromWav(string path)
    {
        var info = new RecorderInfo { Project = Project.Trim(), File = Path.GetFileName(path), Path = path, Date = File.GetLastWriteTime(path) };
        try
        {
            using var reader = new WavReader(path);
            info.Rate = reader.Rate;
            info.Seconds = reader.Rate > 0 ? reader.TotalFrames / (double)reader.Rate : 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Listed with no length.
        }
        return info;
    }

    /// <summary>
    /// Writes the document of a recording beside it and, when the chat has a server, in its search engine: both hold
    /// the same, at each change.
    /// </summary>
    private void SaveInfo(string wavPath, RecorderInfo info)
    {
        info.Path = wavPath;
        info.File = Path.GetFileName(wavPath);
        info.Annotations = info.Annotations.OrderBy(a => a.Time).ToList();
        try
        {
            File.WriteAllText(Path.ChangeExtension(wavPath, ".json"), JsonSerializer.Serialize(info, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"The document of the recording could not be saved: {ex.Message}", true);
            ActivityLog.Error("Recorder", "The document of the recording could not be saved", ex);
        }
        if (ReferenceEquals(SelectedRecording?.Info, info))
            ShowHistory(info);
        _ = PushAsync(info);
    }

    /// <summary>Writes the document in the search engine; a failure is said beside the track and never stops the recorder.</summary>
    private async Task PushAsync(RecorderInfo info)
    {
        if (_assistant is null)
        {
            ServerLabel = "search engine: no server";
            return;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await _assistant.SaveAudioRecordAsync(info, timeout.Token);
            ServerLabel = result.IsSuccess
                ? $"search engine: saved {DateTime.Now:HH:mm:ss} ({info.Actions.Count} action(s), {info.Selections.Count} selection(s), {info.Samples.Count} sample(s))"
                : $"search engine: not saved ({result.Message})";
            if (!result.IsSuccess)
                ActivityLog.Error("Recorder", $"Audio record {info.Id} refused by the server: {result.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            ServerLabel = $"search engine: not saved ({ex.Message}); kept beside the recording";
            ActivityLog.Error("Recorder", $"Audio record {info.Id} not saved in the search engine", ex);
        }
    }

    /// <summary>Adds a manipulation to the document: what was done, the annotation given, now, by the user of the machine.</summary>
    private static void Log(RecorderInfo info, string action, string annotation, string details) =>
        info.Actions.Add(new RecorderAction { Action = action, Annotation = annotation.Trim(), Details = details });

    /// <summary>The popup of a manipulation: its annotation (may be empty); null when it is cancelled, and the manipulation with it.</summary>
    private Task<string?> AskAnnotationAsync(string manipulation, string details) =>
        AskAsync($"Annotation · {manipulation}", $"{details}\n\nAnnotation of this manipulation (kept with it, the date and the user; may be left empty). Cancel gives the manipulation up.", string.Empty, true);

    partial void OnSelectedRecordingChanged(RecorderItemViewModel? value)
    {
        StopPlaying();
        _lastPreview = _lastLoop = false;
        ClearSelection();
        OnPropertyChanged(nameof(Selections));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(ProjectId));
        OnPropertyChanged(nameof(HasProjectDocument));
        ShowHistory(value?.Info);
        if (IsRecording)
            return;
        _ = AnalyseAsync(value);
        ShowAnnotations(value?.Info);
        Title = value?.Info.Title ?? string.Empty;
        TimeLabel = value is null ? "00:00.0" : $"00:00.0 / {Clock(value.Info.Seconds)}";
        ServerLabel = string.Empty;
    }

    /// <summary>Computes the spectrogram of <paramref name="item"/> on the thread pool, and shows it if it is still the one selected.</summary>
    private async Task AnalyseAsync(RecorderItemViewModel? item)
    {
        _analysis?.Cancel();
        Spectrogram = null;
        if (item is null)
        {
            IsAnalysing = false;
            return;
        }
        var analysis = _analysis = new CancellationTokenSource();
        IsAnalysing = true;
        try
        {
            var spectrogram = await Task.Run(() => Spectrogram.Compute(item.Path, analysis.Token), analysis.Token);
            if (!analysis.IsCancellationRequested && ReferenceEquals(SelectedRecording, item))
                Spectrogram = spectrogram;
        }
        catch (OperationCanceledException)
        {
            // Another recording was selected meanwhile.
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Report($"The spectrogram of {Path.GetFileName(item.Path)} could not be computed: {ex.Message}", true);
            ActivityLog.Error("Recorder", "The spectrogram could not be computed", ex);
        }
        finally
        {
            if (ReferenceEquals(_analysis, analysis))
            {
                _analysis = null;
                IsAnalysing = false;
            }
            analysis.Dispose();
        }
    }

    partial void OnTitleChanged(string value)
    {
        // The title of the recording selected is kept as it is typed.
        if (IsRecording || SelectedRecording is not { } selected || selected.Info.Title == value.Trim())
            return;
        selected.Info.Title = value.Trim();
        SaveInfo(selected.Path, selected.Info);
        selected.Changed();
    }

    private void ShowAnnotations(RecorderInfo? info)
    {
        Annotations.Clear();
        foreach (var annotation in (info?.Annotations ?? []).OrderBy(a => a.Time))
            Annotations.Add(annotation);
    }

    // ── Recording ──

    /// <summary>● REC starts a recording in the folder of the project; ■ STOP ends it and selects it.</summary>
    [RelayCommand]
    private void Record()
    {
        if (IsRecording)
        {
            StopRecording();
            return;
        }
        StopPlaying();
        try
        {
            // The spectrogram shown is the one of another recording: it leaves, the new one comes when the recording ends.
            _analysis?.Cancel();
            Spectrogram = null;
            ClearSelection();
            var name = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(Folder, name + ".wav");
            Recorder.StartRecording(path);
            _recording = new RecorderInfo
            {
                Project = Project.Trim(),
                File = Path.GetFileName(path),
                Path = path,
                Title = Title.Trim(),
                Date = DateTimeOffset.Now,
                Rate = Recorder.Rate,
                Source = Recorder.SourceName
            };
            _recordingPath = path;
            ShowAnnotations(_recording);
            IsRecording = true;
            Report($"Recording {Recorder.SourceName} into {path}", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PortAudioSharp.PortAudioException
                                       or DllNotFoundException or TypeInitializationException)
        {
            Report($"The recording could not start: {ex.Message}", true);
            ActivityLog.Error("Recorder", "The recording could not start", ex);
        }
    }

    private void StopRecording()
    {
        var result = Recorder.StopRecording();
        IsRecording = false;
        if (result is null || _recording is not { } info || _recordingPath is not { } path)
            return;
        info.Seconds = result.Seconds;
        // As soon as it is recorded, its document goes beside it and in the search engine.
        Log(info, "recorded", string.Empty, $"{Clock(result.Seconds)} from {info.Source} at {info.Rate} Hz into {path}");
        SaveInfo(path, info);
        var item = new RecorderItemViewModel(path, info);
        Recordings.Insert(0, item);
        _recording = null;
        _recordingPath = null;
        SelectedRecording = item;
        Report(result.Error is null
            ? $"Recorded {Clock(result.Seconds)} in {path} · {info.Annotations.Count} note(s)"
            : $"Recorded {Clock(result.Seconds)} in {path}, but: {result.Error}", result.Error is not null);
    }

    // ── The part selected ──

    /// <summary>The part of the recording selected on the track, in seconds; none when the end is not after the start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private double _selectionStart = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private double _selectionEnd = -1;

    public bool HasSelection => SelectionEnd - SelectionStart >= 0.01;

    /// <summary>Selects a part of the time, on every frequency (a rectangle drawn on the spectrogram is given up).</summary>
    public void Select(double start, double end)
    {
        SelectionStart = Math.Min(start, end);
        SelectionEnd = Math.Max(start, end);
        SetRegion(-1, -1);
    }

    /// <summary>
    /// The band of frequencies of the rectangle drawn on the spectrogram, in Hz; none when the top is not over the
    /// bottom. While there is one, what is listened to and saved is the part selected within this band only: it takes
    /// the place of the low and high cuts of the panel (the gain, the compressor and the equalizer still apply).
    /// </summary>
    public double RegionLowHz { get; private set; } = -1;

    public double RegionHighHz { get; private set; } = -1;

    public bool HasRegion => RegionLowHz >= 0 && RegionHighHz > RegionLowHz;

    /// <summary>Selects a rectangle of the spectrogram: a part of the time and a band of frequencies (at least 50 Hz high).</summary>
    public void SelectRegion(double start, double end, double lowHz, double highHz)
    {
        SelectionStart = Math.Min(start, end);
        SelectionEnd = Math.Max(start, end);
        var low = Math.Max(0, Math.Min(lowHz, highHz));
        SetRegion(low, Math.Max(Math.Max(lowHz, highHz), low + 50));
    }

    private void SetRegion(double lowHz, double highHz)
    {
        if (RegionLowHz == lowHz && RegionHighHz == highHz)
            return;
        RegionLowHz = lowHz;
        RegionHighHz = highHz;
        OnPropertyChanged(nameof(HasRegion));
        FiltersChanged();
    }

    /// <summary>The cuts of the rectangle: none at an edge of the spectrogram (its bottom, or its top at 8 kHz).</summary>
    private double RegionCutLow => RegionLowHz < 30 ? AudioFilterSettings.MinHz : RegionLowHz;

    private double RegionCutHigh => RegionHighHz > Services.Spectrogram.MaxHz - 50 ? AudioFilterSettings.MaxHz : RegionHighHz;

    public void ClearSelection() => Select(-1, -1);

    /// <summary>The named selections of the recording selected.</summary>
    public System.Collections.Generic.IReadOnlyList<RecorderSelection> Selections => SelectedRecording?.Info.Selections ?? [];

    /// <summary>The part selected, or the whole recording when none is: what is listened to and saved.</summary>
    private (double Start, double End) Range()
    {
        var length = Spectrogram?.Seconds ?? SelectedRecording?.Info.Seconds ?? 0;
        return HasSelection ? (Math.Clamp(SelectionStart, 0, length), Math.Clamp(SelectionEnd, 0, length)) : (0, length);
    }

    /// <summary>
    /// Names the part selected: a popup asks its name, another its annotation; it is kept in the document of the
    /// recording (and drawn on the ruler, where a click selects it again).
    /// </summary>
    public async Task NameSelectionAsync()
    {
        if (IsRecording || SelectedRecording is not { } item || !HasSelection)
        {
            Report("Select a part of the track first (drag on it)", true);
            return;
        }
        var (start, end) = Range();
        var name = await AskAsync("Name the selection", $"A name for {Clock(start)} – {Clock(end)} ({end - start:0.00} s):", $"Selection {item.Info.Selections.Count + 1}", false);
        if (string.IsNullOrWhiteSpace(name))
            return;
        var details = $"« {name.Trim()} »: {Clock(start)} – {Clock(end)} ({end - start:0.00} s)";
        if (await AskAnnotationAsync("name the selection", details) is not { } annotation)
            return;
        item.Info.Selections.Add(new RecorderSelection { Name = name.Trim(), Start = Math.Round(start, 3), End = Math.Round(end, 3) });
        Log(item.Info, "name-selection", annotation, details);
        SaveInfo(item.Path, item.Info);
        OnPropertyChanged(nameof(Selections));
        Report($"Selection named {details}", false);
    }

    // ── The filters ──

    [ObservableProperty]
    private double _gainDb;

    [ObservableProperty]
    private bool _compressor;

    [ObservableProperty]
    private double _thresholdDb = -20;

    [ObservableProperty]
    private double _ratio = 4;

    /// <summary>The band kept (or, inverted, taken out): from <see cref="LowHz"/> to <see cref="HighHz"/>.</summary>
    [ObservableProperty]
    private double _lowHz = AudioFilterSettings.MinHz;

    [ObservableProperty]
    private double _highHz = AudioFilterSettings.MaxHz;

    [ObservableProperty]
    private bool _invertBand;

    private readonly double[] _equalizer = new double[AudioFilterSettings.EqualizerHz.Length];

    /// <summary>Changed at each change of a filter: the curve of the equalizer is drawn again.</summary>
    public int FiltersVersion { get; private set; }

    /// <summary>
    /// The filters of the part selected, as they are set now: the band is the one of the rectangle drawn on the
    /// spectrogram when there is one. What a sample is saved with.
    /// </summary>
    public AudioFilterSettings Filters => FiltersFor(region: true);

    /// <summary>
    /// The filters as they are set now. With <paramref name="region"/>, the band of the rectangle drawn on the
    /// spectrogram takes the place of the low and high cuts; without, the cuts of the panel (the whole recording played).
    /// </summary>
    private AudioFilterSettings FiltersFor(bool region)
    {
        region &= HasRegion;
        return new AudioFilterSettings
        {
            GainDb = GainDb, Compressor = Compressor, ThresholdDb = ThresholdDb, Ratio = Ratio,
            LowHz = region ? RegionCutLow : Math.Min(LowHz, HighHz), HighHz = region ? RegionCutHigh : Math.Max(LowHz, HighHz),
            InvertBand = !region && InvertBand, EqualizerDb = (double[])_equalizer.Clone()
        };
    }

    /// <summary>The filters as a sentence; it says when the band is the one of the rectangle (the cuts of the panel then wait).</summary>
    public string FiltersLabel => HasRegion
        ? $"rectangle {RegionLowHz:0}–{RegionHighHz:0} Hz (in place of the low and high cuts) · {Filters.Describe()}"
        : Filters.Describe();

    public double EqualizerBand(int band) => _equalizer[band];

    /// <summary>Sets the gain of a band of the equalizer, in dB.</summary>
    public void SetEqualizerBand(int band, double gainDb)
    {
        gainDb = Math.Clamp(Math.Round(gainDb, 1), -AudioFilterSettings.MaxEqualizerDb, AudioFilterSettings.MaxEqualizerDb);
        if (_equalizer[band] == gainDb)
            return;
        _equalizer[band] = gainDb;
        FiltersChanged();
    }

    private void FiltersChanged()
    {
        FiltersVersion++;
        // What is playing takes the new filters at once.
        _effects?.Update(FiltersFor(_effectsRegion));
        OnPropertyChanged(nameof(FiltersVersion));
        OnPropertyChanged(nameof(FiltersLabel));
    }

    partial void OnGainDbChanged(double value) => FiltersChanged();
    partial void OnCompressorChanged(bool value) => FiltersChanged();
    partial void OnThresholdDbChanged(double value) => FiltersChanged();
    partial void OnRatioChanged(double value) => FiltersChanged();
    partial void OnLowHzChanged(double value) => FiltersChanged();
    partial void OnHighHzChanged(double value) => FiltersChanged();
    partial void OnInvertBandChanged(bool value) => FiltersChanged();

    /// <summary>No filter: the sound as it was recorded.</summary>
    [RelayCommand]
    private void ResetFilters()
    {
        GainDb = 0;
        Compressor = false;
        ThresholdDb = -20;
        Ratio = 4;
        LowHz = AudioFilterSettings.MinHz;
        HighHz = AudioFilterSettings.MaxHz;
        InvertBand = false;
        Array.Clear(_equalizer);
        FiltersChanged();
    }

    // ── Listening again ──

    /// <summary>True while a part of the recording is listened to (the part selected, or the rectangle), not the whole of it.</summary>
    private bool _preview;
    private bool _previewLoop;
    /// <summary>What was listened to last: a part (in a loop or not), or the recording itself.</summary>
    private bool _lastPreview, _lastLoop;
    private double _previewEnd;
    /// <summary>The filters of what is playing, changed while it plays; with the band of the rectangle or not.</summary>
    private AudioEffects.Processor? _effects;
    private bool _effectsRegion;

    /// <summary>Where the recording selected is being played, in seconds; null when it is not open in the player.</summary>
    public double? PlayPosition => _player?.Position;

    /// <summary>
    /// Opens the recording selected in a player that applies the filters while it plays: a filter moved is heard at
    /// once. With <paramref name="region"/>, the band is the one of the rectangle drawn on the spectrogram.
    /// </summary>
    private WavPlayer OpenPlayer(bool region)
    {
        var player = new WavPlayer(SelectedRecording!.Path);
        _effectsRegion = region;
        _effects = new AudioEffects.Processor(player.Rate, FiltersFor(region));
        player.Filter = _effects.Next;
        return player;
    }

    /// <summary>True while the part selected is listened to in a loop.</summary>
    public bool IsLooping => _preview && _previewLoop && _player is not null;

    private bool CanPlay() => SelectedRecording is not null && !IsRecording;

    /// <summary>Plays the recording selected through the filters of the panel (RESET: as it was recorded), or pauses what is playing.</summary>
    [RelayCommand]
    private void Play()
    {
        if (!CanPlay())
        {
            Report(IsRecording ? "Stop the recording before listening" : "Select a recording", true);
            return;
        }
        try
        {
            if (_player is { IsPlaying: true })
            {
                _player.Pause();
                IsPlaying = false;
                return;
            }
            // A part that was listened to, at its end: the recording itself again.
            if (_player is null || _player.Ended)
            {
                StopPlaying();
                _player = OpenPlayer(region: false);
                _lastPreview = false;
            }
            _player.Play();
            IsPlaying = _player.IsPlaying;
            IsError = false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            Report($"The recording could not be played: {ex.Message}", true);
        }
    }

    /// <summary>
    /// Plays the part selected (the whole recording when none is) through the filters, once or, with
    /// <paramref name="loop"/>, again and again without a gap until it is stopped. The filters are applied while it
    /// plays: one moved meanwhile is heard at once.
    /// </summary>
    public void PlaySelection(bool loop)
    {
        if (!CanPlay())
        {
            Report(IsRecording ? "Stop the recording before listening" : "Select a recording", true);
            return;
        }
        var (start, end) = Range();
        if (end - start < 0.01)
            return;
        StopPlaying();
        try
        {
            _player = OpenPlayer(region: true);
            _player.SetRange(start, end, loop);
            _player.Seek(start);
            _player.Play();
            _preview = true;
            _previewLoop = loop;
            _previewEnd = end;
            _lastPreview = true;
            _lastLoop = loop;
            IsPlaying = _player.IsPlaying;
            Report($"{(loop ? "Looping" : "Playing")} {Clock(start)} – {Clock(end)} · {FiltersLabel}", false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or PortAudioSharp.PortAudioException)
        {
            StopPlaying();
            Report($"The selection could not be played: {ex.Message}", true);
        }
    }

    /// <summary>
    /// The space bar: stops what is playing where it is (the recording, or the part listened to, in a loop or not);
    /// again, it goes on from there. When nothing is open any more (the part was heard to its end), what was listened
    /// to last starts again: the part selected with the filters, in a loop if it was, else the recording.
    /// </summary>
    public void TogglePlayback()
    {
        if (!CanPlay())
            return;
        try
        {
            if (_player is { IsPlaying: true })
            {
                _player.Pause();
                IsPlaying = false;
                return;
            }
            if (_player is { Ended: false })
            {
                _player.Play();
                IsPlaying = _player.IsPlaying;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            Report($"The recording could not be played: {ex.Message}", true);
            return;
        }
        if (_lastPreview || HasSelection)
            PlaySelection(_lastLoop);
        else
            Play();
    }

    /// <summary>
    /// A part was just selected (on the track, or a rectangle on the spectrogram) while something plays in a loop: the
    /// loop goes to this part at once, with its band of frequencies when it is a rectangle.
    /// </summary>
    public void FollowSelection()
    {
        if (!_preview || !_previewLoop || _player is not { Ended: false } player || !HasSelection)
            return;
        var (start, end) = Range();
        if (end - start < 0.01)
            return;
        player.SetRange(start, end, loop: true);
        _previewEnd = end;
        _effectsRegion = true;
        _effects?.Update(FiltersFor(true));
        if (player.Position < start || player.Position >= end)
            player.Seek(start);
        Report($"Looping {Clock(start)} – {Clock(end)} · {FiltersLabel}", false);
    }

    /// <summary>Stops what is playing (the recording, or the part listened to in a loop).</summary>
    [RelayCommand]
    private void Stop()
    {
        StopPlaying();
        Refresh();
    }

    /// <summary>Goes to <paramref name="annotation"/> in the recording selected, and plays from there.</summary>
    [RelayCommand]
    private void Jump(RecorderAnnotation? annotation)
    {
        if (annotation is null)
            return;
        Seek(annotation.Time);
        if (_player is { IsPlaying: false })
        {
            _player.Play();
            IsPlaying = _player.IsPlaying;
        }
    }

    /// <summary>Puts the playing of the recording selected at <paramref name="seconds"/> (it goes on playing, or stays paused there).</summary>
    public void Seek(double seconds)
    {
        if (!CanPlay())
            return;
        try
        {
            if (_preview && _player is { Ended: false } playing)
            {
                // A part is being listened to and the playhead is put elsewhere: the playing does not stop, it goes
                // on from there over the whole recording (in a loop if it was), so the filters can still be heard.
                playing.SetRange(0, playing.Duration, _previewLoop);
                _previewEnd = playing.Duration;
                _effectsRegion = false;
                _effects?.Update(FiltersFor(false));
            }
            else if (_player is null || _player.Ended)
            {
                StopPlaying();
                _player = OpenPlayer(region: false);
            }
            _player.Seek(Math.Clamp(seconds, 0, _player.Duration));
            IsPlaying = _player.IsPlaying;
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or PortAudioSharp.PortAudioException)
        {
            Report($"The recording could not be played: {ex.Message}", true);
        }
    }

    private void StopPlaying()
    {
        _player?.Dispose();
        _player = null;
        _effects = null;
        _preview = false;
        _previewLoop = false;
        IsPlaying = false;
    }

    // ── Samples ──

    /// <summary>
    /// Saves the part selected (the whole recording when none is), with the filters applied, as a new WAV file of
    /// <c>&lt;project&gt;/samples</c>. A popup asks its name, another its annotation; the sample (its file, the part it
    /// comes from, its filters, its annotation) is added to the document of the recording.
    /// </summary>
    public async Task SaveSampleAsync()
    {
        if (IsRecording || SelectedRecording is not { } item)
        {
            Report(IsRecording ? "Stop the recording before saving a sample" : "Select a recording", true);
            return;
        }
        var (start, end) = Range();
        if (end - start < 0.01)
        {
            Report("Nothing to save", true);
            return;
        }
        var filters = Filters;
        var what = $"{Clock(start)} – {Clock(end)} ({end - start:0.00} s){(HasRegion ? $", rectangle {RegionLowHz:0}–{RegionHighHz:0} Hz of the spectrogram," : string.Empty)} of {item.Name}, {filters.Describe()}";
        var name = await AskAsync("Save the sample", $"A name for the sample: {what}", $"{item.Name} sample {item.Info.Samples.Count + 1}", false);
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (await AskAnnotationAsync("save a sample", $"« {name.Trim()} »: {what}") is not { } annotation)
            return;
        try
        {
            var file = $"{Path.GetFileNameWithoutExtension(item.Path)}-{DateTime.Now:yyyyMMdd-HHmmss}.wav";
            var target = Path.Combine(Folder, "samples", file);
            var seconds = await Task.Run(() => AudioEffects.Render(item.Path, start, end, filters, target));
            item.Info.Samples.Add(new RecorderSample
            {
                Name = name.Trim(),
                File = file,
                Path = target,
                Start = Math.Round(start, 3),
                End = Math.Round(end, 3),
                Seconds = Math.Round(seconds, 3),
                Annotation = annotation.Trim(),
                Filters = new RecorderFilters
                {
                    GainDb = filters.GainDb, Compressor = filters.Compressor, ThresholdDb = filters.ThresholdDb, Ratio = filters.Ratio,
                    LowHz = filters.LowHz, HighHz = filters.HighHz, InvertBand = filters.InvertBand,
                    EqualizerDb = filters.EqualizerDb.ToList(), EqualizerHz = AudioFilterSettings.EqualizerHz.ToList()
                }
            });
            Log(item.Info, "save-sample", annotation, $"« {name.Trim()} » → {target}: {what}");
            SaveInfo(item.Path, item.Info);
            item.Changed();
            Report($"Sample saved: {target} ({Clock(seconds)})", false);
            ActivityLog.Info("Recorder", $"Sample {file} saved from {Path.GetFileName(item.Path)}: {what}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Report($"The sample could not be saved: {ex.Message}", true);
            ActivityLog.Error("Recorder", "The sample could not be saved", ex);
        }
    }

    // ── Annotations of the moments ──

    /// <summary>
    /// Adds the note typed: on the recording running, at the time recorded so far; else on the recording selected, at
    /// the position played (its start when it is not played).
    /// </summary>
    [RelayCommand]
    private void AddAnnotation()
    {
        var text = NewAnnotation.Trim();
        if (text.Length == 0)
            return;
        RecorderInfo info;
        double time;
        if (IsRecording && _recording is not null)
        {
            info = _recording;
            time = Recorder.Elapsed.TotalSeconds;
        }
        else if (SelectedRecording is { } selected)
        {
            info = selected.Info;
            time = PlayPosition ?? 0;
        }
        else
        {
            Report("Start a recording or select one to annotate it", true);
            return;
        }
        var annotation = new RecorderAnnotation { Time = Math.Round(time, 1), Text = text };
        info.Annotations.Add(annotation);
        // Shown at its place by its time.
        var index = Annotations.TakeWhile(a => a.Time <= annotation.Time).Count();
        Annotations.Insert(index, annotation);
        NewAnnotation = string.Empty;
        // The notes of the recording running are saved with it when it stops.
        if (!IsRecording && SelectedRecording is { } item)
        {
            SaveInfo(item.Path, info);
            item.Changed();
        }
        Report($"Note added at {annotation.TimeLabel}", false);
    }

    [RelayCommand]
    private void RemoveAnnotation(RecorderAnnotation? annotation)
    {
        if (annotation is null)
            return;
        Annotations.Remove(annotation);
        if (IsRecording && _recording is not null)
        {
            _recording.Annotations.Remove(annotation);
            return;
        }
        if (SelectedRecording is not { } item)
            return;
        item.Info.Annotations.Remove(annotation);
        SaveInfo(item.Path, item.Info);
        item.Changed();
    }

    // ── Editing: parts cut out of the recording selected ──

    /// <summary>
    /// Cuts in the recording selected: takes what is between <paramref name="start"/> and <paramref name="end"/>
    /// (seconds) out of it, or with <paramref name="trim"/> keeps only that. A popup first asks the annotation of the
    /// cut (Cancel gives it up). The file is written again; the recording as it was recorded and its notes are kept
    /// beside it before the first cut (<see cref="RestoreAsync"/> brings them back). The notes and the named
    /// selections follow: those after the cut move earlier, the notes inside it go to its start, the selections it
    /// touches are dropped. The spectrogram is computed again.
    /// </summary>
    public async Task EditAsync(bool trim, double start, double end)
    {
        if (IsRecording || SelectedRecording is not { } item)
        {
            Report(IsRecording ? "Stop the recording before cutting in one" : "Select a recording", true);
            return;
        }
        var length = Spectrogram?.Seconds ?? item.Info.Seconds;
        (start, end) = (Math.Clamp(Math.Min(start, end), 0, length), Math.Clamp(Math.Max(start, end), 0, length));
        if (end - start < 0.01)
        {
            Report("Select a part of the track first (drag on it)", true);
            return;
        }
        if (end - start >= length - 0.01)
        {
            Report(trim ? "The selection is the whole recording: nothing to trim" : "The selection is the whole recording: nothing would be left", true);
            return;
        }
        var details = trim
            ? $"keep only {Clock(start)} – {Clock(end)} ({end - start:0.00} s) of {Clock(length)}"
            : $"take {Clock(start)} – {Clock(end)} ({end - start:0.00} s) out of {Clock(length)}";
        if (await AskAnnotationAsync(trim ? "trim" : "cut", details) is not { } note)
            return;

        StopPlaying();
        ClearSelection();
        _analysis?.Cancel();
        Spectrogram = null;
        IsAnalysing = true;
        var info = item.Info;
        try
        {
            // The document as it was before the first cut, beside the one of the recording.
            var sidecar = Path.ChangeExtension(item.Path, ".json");
            if (!WavEditor.HasBackup(item.Path) && File.Exists(sidecar))
                File.Copy(sidecar, sidecar + ".bak", overwrite: true);
            var seconds = await Task.Run(() => trim ? WavEditor.Trim(item.Path, start, end) : WavEditor.Cut(item.Path, start, end));
            double Moved(double time) => trim
                ? Math.Clamp(time, start, end) - start
                : time < start ? time : time < end ? start : time - (end - start);
            foreach (var annotation in info.Annotations)
                annotation.Time = Math.Round(Moved(annotation.Time), 1);
            // A named selection the cut touches no longer means the same sound: dropped; the others follow.
            var dropped = info.Selections.RemoveAll(s => trim ? s.Start < start || s.End > end : s.Start < end && s.End > start);
            foreach (var selection in info.Selections)
                (selection.Start, selection.End) = (Math.Round(Moved(selection.Start), 3), Math.Round(Moved(selection.End), 3));
            info.Seconds = seconds;
            Log(info, trim ? "trim" : "cut", note, $"{details}; {Clock(seconds)} left" + (dropped > 0 ? $"; {dropped} named selection(s) dropped" : string.Empty));
            SaveInfo(item.Path, info);
            item.Changed();
            ShowAnnotations(info);
            OnPropertyChanged(nameof(Selections));
            TimeLabel = $"00:00.0 / {Clock(seconds)}";
            Report($"{(trim ? "Trimmed" : "Cut")}: {details}; {Clock(seconds)} left · ORIGINAL brings the recording back", false);
            ActivityLog.Info("Recorder", $"{(trim ? "Trimmed" : "Cut")} {start:0.00}-{end:0.00} s in {Path.GetFileName(item.Path)}: {seconds:0.0} s left");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Report($"The recording could not be cut: {ex.Message}", true);
            ActivityLog.Error("Recorder", "The recording could not be cut", ex);
        }
        OnPropertyChanged(nameof(CanRestore));
        await AnalyseAsync(item);
    }

    /// <summary>True when the recording selected was cut: the recording as it was recorded can be brought back.</summary>
    public bool CanRestore => SelectedRecording is { } selected && WavEditor.HasBackup(selected.Path);

    /// <summary>
    /// Brings the recording selected back as it was recorded, with its notes and named selections of then: every cut
    /// is undone (after the popup of the annotation). What was done since stays in its document: the manipulations
    /// and the samples.
    /// </summary>
    public async Task RestoreAsync()
    {
        if (IsRecording || SelectedRecording is not { } item)
            return;
        if (!WavEditor.HasBackup(item.Path))
        {
            Report("This recording was never cut", false);
            return;
        }
        if (await AskAnnotationAsync("restore the original", $"bring {item.Name} back as it was recorded: every cut is undone") is not { } note)
            return;
        StopPlaying();
        ClearSelection();
        _analysis?.Cancel();
        Spectrogram = null;
        try
        {
            WavEditor.Restore(item.Path);
            var sidecar = Path.ChangeExtension(item.Path, ".json");
            if (ReadInfoFile(sidecar + ".bak") is { } original)
            {
                item.Info.Annotations = original.Annotations;
                item.Info.Selections = original.Selections;
            }
            File.Delete(sidecar + ".bak");
            using (var reader = new WavReader(item.Path))
                item.Info.Seconds = reader.Rate > 0 ? reader.TotalFrames / (double)reader.Rate : item.Info.Seconds;
            Log(item.Info, "restore", note, $"back as recorded: {Clock(item.Info.Seconds)}");
            SaveInfo(item.Path, item.Info);
            item.Changed();
            ShowAnnotations(item.Info);
            OnPropertyChanged(nameof(Selections));
            TimeLabel = $"00:00.0 / {Clock(item.Info.Seconds)}";
            Report($"The recording is back as it was recorded ({Clock(item.Info.Seconds)})", false);
            ActivityLog.Info("Recorder", $"{Path.GetFileName(item.Path)} restored as recorded");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Report($"The recording could not be restored: {ex.Message}", true);
        }
        OnPropertyChanged(nameof(CanRestore));
        await AnalyseAsync(item);
    }

    /// <summary>Opens the folder of the project in the file explorer of the system.</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Report($"The folder could not be opened: {ex.Message}", true);
        }
    }

    /// <summary>
    /// The time shown, the end of the playing and the loop of the part listened to; read by the view many times a second.
    /// </summary>
    public void Refresh()
    {
        if (IsRecording)
        {
            TimeLabel = Clock(Recorder.Elapsed.TotalSeconds);
            return;
        }
        if (_player is not { } player)
            return;
        if (_preview)
        {
            TimeLabel = $"{Clock(player.Position)} / {Clock(_previewEnd)}{(_previewLoop ? " ⟳" : string.Empty)}";
            // The loop is the one of the player: only a part played once comes to its end.
            if (player.Ended)
                StopPlaying();
            return;
        }
        TimeLabel = $"{Clock(player.Position)} / {Clock(player.Duration)}";
        if (IsPlaying && !player.IsPlaying)
            IsPlaying = false;
    }


    private void Report(string status, bool isError)
    {
        Status = status;
        IsError = isError;
    }

    /// <summary>The window closes: a recording running is ended and kept, the microphone is closed.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (IsRecording)
            StopRecording();
        _analysis?.Cancel();
        StopPlaying();
        Recorder.Dispose();
    }
}
