using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A track of the list: its file and labels; <see cref="IsCurrent"/> marks the one being played.</summary>
public partial class MusicTrackViewModel : ViewModelBase
{
    public MusicTrackViewModel(string path, long size, int number)
    {
        Path = path;
        Size = size;
        Number = number;
    }

    /// <summary>Full path of the file.</summary>
    public string Path { get; }

    /// <summary>Size of the file, in bytes.</summary>
    public long Size { get; }

    /// <summary>Position in the folder (1, 2, 3…).</summary>
    public int Number { get; }

    /// <summary>File name without its extension.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Format, in upper case (MP3, M4A…).</summary>
    public string Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();

    /// <summary>Size of the file, readable.</summary>
    public string SizeLabel => PictureLibrary.FormatSize(Size);

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>Repeat modes of the player.</summary>
public enum MusicRepeat
{
    Off,
    All,
    One
}

/// <summary>
/// Music player of <c>/music</c> (<c>/nmusic</c> in its own window): browses the folders of <c>music.directory</c>
/// (config.yml, the Music folder of the system by default), plays their tracks one after the other (shuffle, repeat),
/// with seek, volume, loudness level and the tags and cover of the track (see <see cref="MusicEngine"/>).
/// </summary>
public partial class MusicPlayerViewModel : ViewModelBase, INoteContextProvider, IDisposable
{
    private readonly MusicEngine _engine = new();
    private readonly Random _random = new();

    /// <summary>Tracks played in order (the folder's tracks when the play started), and the index of the current one.</summary>
    private List<MusicTrackViewModel> _queue = [];
    private int _queueIndex = -1;

    /// <summary>Every track of the folder (the list shows the ones matching the search).</summary>
    private List<MusicTrackViewModel> _allTracks = [];

    /// <summary>True once the engine has started.</summary>
    private bool _engineReady;

    /// <summary>Design-time constructor (the Music folder of the system).</summary>
    public MusicPlayerViewModel() : this(Models.ChatSettings.SystemMusicDirectory)
    {
    }

    /// <summary>Opens the player at <paramref name="rootDirectory"/> and starts the audio engine.</summary>
    public MusicPlayerViewModel(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        _currentDirectory = RootDirectory;
        _engine.Loaded += duration => Dispatcher.UIThread.Post(() => OnLoaded(duration));
        _engine.Position += (position, duration, playing, level) => Dispatcher.UIThread.Post(() => OnPosition(position, duration, playing, level));
        _engine.Ended += () => Dispatcher.UIThread.Post(OnEnded);
        _engine.Info += info => Dispatcher.UIThread.Post(() => OnInfo(info));
        _engine.Error += message => Dispatcher.UIThread.Post(() =>
        {
            IsError = true;
            Status = message;
        });
        Load();
        _ = StartEngineAsync();
    }

    /// <summary>Root of the music folders (<c>music.directory</c> in config.yml).</summary>
    public string RootDirectory { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    private string _currentDirectory;

    /// <summary>The folders from the root to the current one (clickable path).</summary>
    public ObservableCollection<PictureFolder> Breadcrumbs { get; } = [];

    /// <summary>Sub-folders of the current folder.</summary>
    public ObservableCollection<PictureFolder> Folders { get; } = [];

    /// <summary>Tracks of the current folder matching the search.</summary>
    public ObservableCollection<MusicTrackViewModel> Tracks { get; } = [];

    /// <summary>True when the folder has sub-folders.</summary>
    public bool HasFolders => Folders.Count > 0;

    /// <summary>True when the folder shows nothing.</summary>
    public bool IsEmpty => Folders.Count == 0 && Tracks.Count == 0;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private MusicTrackViewModel? _selectedTrack;

    // Now playing.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack))]
    private MusicTrackViewModel? _currentTrack;

    /// <summary>True when a track is loaded.</summary>
    public bool HasTrack => CurrentTrack is not null;

    [ObservableProperty]
    private string _title = "Nothing playing";

    [ObservableProperty]
    private string _subtitle = "Double-click a track to play it";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArtwork))]
    private Bitmap? _artwork;

    /// <summary>True when the track has a cover.</summary>
    public bool HasArtwork => Artwork is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionLabel))]
    private double _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationLabel))]
    private double _duration;

    /// <summary>Position, m:ss.</summary>
    public string PositionLabel => Time(Position);

    /// <summary>Duration, m:ss.</summary>
    public string DurationLabel => Time(Duration);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    private bool _isPlaying;

    /// <summary>Symbol of the play / pause button.</summary>
    public string PlayPauseLabel => IsPlaying ? "⏸" : "▶";

    /// <summary>Loudness of the track right now, 0 to 1 (level meter).</summary>
    [ObservableProperty]
    private double _level;

    /// <summary>Volume, 0 to 100.</summary>
    [ObservableProperty]
    private double _volume = 80;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleOpacity))]
    private bool _shuffle;

    /// <summary>The shuffle button is dimmed when off.</summary>
    public double ShuffleOpacity => Shuffle ? 1 : 0.45;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatLabel), nameof(RepeatOpacity))]
    private MusicRepeat _repeat = MusicRepeat.All;

    /// <summary>Symbol of the repeat button (🔁 all, 🔂 one).</summary>
    public string RepeatLabel => Repeat == MusicRepeat.One ? "🔂" : "🔁";

    /// <summary>The repeat button is dimmed when off.</summary>
    public double RepeatOpacity => Repeat == MusicRepeat.Off ? 0.45 : 1;

    /// <summary>True while the user drags the position slider (its value is not updated meanwhile).</summary>
    public bool IsSeeking { get; set; }

    /// <summary>True when the engine can seek inside a track.</summary>
    public bool CanSeek => _engine.CanSeek;

    [ObservableProperty]
    private string _status = "Starting the player…";

    [ObservableProperty]
    private bool _isError;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnVolumeChanged(double value) => _engine.SetVolume(value / 100);

    private async Task StartEngineAsync()
    {
        try
        {
            Status = OperatingSystem.IsMacOS() ? "Starting the player (it is compiled the first time, a few seconds)…" : "Starting the player…";
            await _engine.StartAsync();
            _engine.SetVolume(Volume / 100);
            _engineReady = true;
            OnPropertyChanged(nameof(CanSeek));
            Status = $"{_allTracks.Count} track(s) here · player: {_engine.Backend}";
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or IOException
                                       or System.ComponentModel.Win32Exception)
        {
            IsError = true;
            Status = $"No music player: {ex.Message}";
            ActivityLog.Error("Music", "The music player could not start", ex);
        }
    }

    // Folders.

    /// <summary>Opens a sub-folder or a folder of the path.</summary>
    [RelayCommand]
    private void OpenFolder(PictureFolder? folder)
    {
        if (folder is null || !Directory.Exists(folder.Path))
            return;
        CurrentDirectory = folder.Path;
        SearchText = string.Empty;
        Load();
    }

    private bool CanGoUp() => !string.Equals(Path.GetFullPath(CurrentDirectory), RootDirectory, StringComparison.Ordinal);

    /// <summary>Goes to the parent folder (not above the root).</summary>
    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up()
    {
        if (Directory.GetParent(CurrentDirectory) is { } parent)
            OpenFolder(new PictureFolder(parent.Name, parent.FullName));
    }

    /// <summary>Reads the current folder again.</summary>
    [RelayCommand]
    private void Refresh() => Load();

    /// <summary>Opens the current folder in the Finder / Explorer.</summary>
    [RelayCommand]
    private void OpenFolderInSystem()
    {
        try
        {
            PictureLibrary.OpenWithSystem(CurrentDirectory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            IsError = true;
            Status = ex.Message;
        }
    }

    /// <summary>Reads the folder: path, sub-folders (no hidden folders, no packages) and tracks sorted by name.</summary>
    private void Load()
    {
        Folders.Clear();
        Breadcrumbs.Clear();
        IsError = false;

        var crumbs = new List<PictureFolder>();
        for (var dir = new DirectoryInfo(CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            crumbs.Insert(0, new PictureFolder(dir.FullName == RootDirectory && Path.GetFileName(RootDirectory) is { Length: > 0 } root ? root : dir.Name, dir.FullName));
            if (dir.FullName == RootDirectory)
                break;
        }
        foreach (var crumb in crumbs)
            Breadcrumbs.Add(crumb);

        try
        {
            var directory = new DirectoryInfo(CurrentDirectory);
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            foreach (var folder in directory.EnumerateDirectories("*", options)
                         .Where(d => !d.Name.StartsWith('.') && !IsPackage(d.Name))
                         .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                Folders.Add(new PictureFolder(folder.Name, folder.FullName));

            var number = 0;
            _allTracks = directory.EnumerateFiles("*", options)
                .Where(f => !f.Name.StartsWith('.') && MusicEngine.TrackExtensions.Contains(f.Extension))
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new MusicTrackViewModel(f.FullName, f.Length, ++number) { IsCurrent = f.FullName == CurrentTrack?.Path })
                .ToList();
            ActivityLog.Info("Music", $"Folder {CurrentDirectory}: {Folders.Count} folder(s), {_allTracks.Count} track(s)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _allTracks = [];
            IsError = true;
            Status = $"Cannot read {CurrentDirectory}: {ex.Message}";
        }
        ApplyFilter();
        OnPropertyChanged(nameof(HasFolders));
        if (!IsError && _engineReady)
            Status = $"{Folders.Count} folder(s) · {_allTracks.Count} track(s) · {PictureLibrary.FormatSize(_allTracks.Sum(t => t.Size))}";
    }

    /// <summary>macOS packages (Music / GarageBand / Logic libraries and projects).</summary>
    private static bool IsPackage(string name) =>
        new[] { ".musiclibrary", ".band", ".logicx", ".app", ".photoslibrary", ".tvlibrary" }
            .Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>Shows the tracks whose name contains the search.</summary>
    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        Tracks.Clear();
        foreach (var track in term.Length == 0 ? _allTracks : _allTracks.Where(t => t.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            Tracks.Add(track);
        OnPropertyChanged(nameof(IsEmpty));
    }

    // Playback.

    /// <summary>Plays a track; the tracks of the list (as shown) become the queue.</summary>
    [RelayCommand]
    private void PlayTrack(MusicTrackViewModel? track)
    {
        if (track is null || !_engineReady)
            return;
        _queue = Tracks.ToList();
        _queueIndex = _queue.IndexOf(track);
        Start(track);
    }

    /// <summary>Play / pause; with nothing loaded, plays the selected (or first) track.</summary>
    [RelayCommand]
    private void PlayPause()
    {
        if (!_engineReady)
            return;
        if (CurrentTrack is null)
        {
            PlayTrack(SelectedTrack ?? Tracks.FirstOrDefault());
            return;
        }
        if (IsPlaying)
            _engine.Pause();
        else
            _engine.Play();
        IsPlaying = !IsPlaying;
    }

    /// <summary>Stops the track and goes back to its start.</summary>
    [RelayCommand]
    private void Stop()
    {
        _engine.Stop();
        IsPlaying = false;
        Position = 0;
    }

    /// <summary>Next track of the queue (random with shuffle, wraps around with repeat).</summary>
    [RelayCommand]
    private void Next() => Advance(fromEnd: false);

    /// <summary>Back to the start of the track after 3 seconds, else the previous track.</summary>
    [RelayCommand]
    private void Previous()
    {
        if (Position > 3 || _queue.Count == 0)
        {
            SeekTo(0);
            return;
        }
        _queueIndex = (_queueIndex - 1 + _queue.Count) % _queue.Count;
        Start(_queue[_queueIndex]);
    }

    /// <summary>Shuffle on / off.</summary>
    [RelayCommand]
    private void ToggleShuffle() => Shuffle = !Shuffle;

    /// <summary>Repeat: all → one → off.</summary>
    [RelayCommand]
    private void ToggleRepeat() => Repeat = Repeat switch
    {
        MusicRepeat.All => MusicRepeat.One,
        MusicRepeat.One => MusicRepeat.Off,
        _ => MusicRepeat.All
    };

    /// <summary>Goes to <paramref name="seconds"/> in the track (end of a drag of the position slider).</summary>
    public void SeekTo(double seconds)
    {
        if (CurrentTrack is null || !_engine.CanSeek)
            return;
        _engine.Seek(seconds);
        Position = seconds;
    }

    private void Start(MusicTrackViewModel track)
    {
        if (CurrentTrack is { } previous)
            previous.IsCurrent = false;
        CurrentTrack = track;
        track.IsCurrent = true;
        SelectedTrack = track;
        Title = track.Name;
        Subtitle = $"{track.Format} · {track.SizeLabel}";
        var cover = Artwork;
        Artwork = null;
        cover?.Dispose();
        Position = 0;
        Duration = 0;
        IsError = false;
        _engine.Load(track.Path);
        _engine.Play();
        _engine.RequestInfo(track.Path);
        IsPlaying = true;
        Status = $"Playing {track.Name}";
        ActivityLog.Info("Music", $"Playing {track.Path}");
    }

    private void Advance(bool fromEnd)
    {
        if (_queue.Count == 0)
            return;
        if (fromEnd && Repeat == MusicRepeat.One)
        {
            Start(_queue[_queueIndex]);
            return;
        }
        if (Shuffle && _queue.Count > 1)
        {
            int next;
            do next = _random.Next(_queue.Count); while (next == _queueIndex);
            _queueIndex = next;
        }
        else if (_queueIndex + 1 < _queue.Count)
        {
            _queueIndex++;
        }
        else if (Repeat != MusicRepeat.Off || !fromEnd)
        {
            _queueIndex = 0;
        }
        else
        {
            IsPlaying = false;
            Status = "End of the folder";
            return;
        }
        Start(_queue[_queueIndex]);
    }

    private void OnLoaded(double duration) => Duration = duration;

    private void OnPosition(double position, double duration, bool playing, double level)
    {
        if (CurrentTrack is null)
            return;
        if (!IsSeeking)
            Position = Math.Min(position, Math.Max(duration, position));
        if (duration > 0)
            Duration = duration;
        Level = playing ? level : 0;
    }

    private void OnEnded() => Advance(fromEnd: true);

    private void OnInfo(TrackInfo info)
    {
        if (CurrentTrack?.Path != info.Path)
            return;
        if (info.Title is { } title)
            Title = title;
        var parts = new[] { info.Artist, info.Album }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        Subtitle = parts.Count > 0 ? string.Join(" · ", parts) : $"{CurrentTrack.Format} · {CurrentTrack.SizeLabel}";
        if (info.ArtworkPath is { } artwork && File.Exists(artwork))
        {
            try
            {
                using var stream = File.OpenRead(artwork);
                Artwork = Bitmap.DecodeToWidth(stream, 480);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // No cover.
            }
        }
    }

    private static string Time(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            seconds = 0;
        var time = TimeSpan.FromSeconds(Math.Floor(seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    /// <inheritdoc />
    public string NoteWindowName => "Music";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        folder = CurrentDirectory,
        tracks = _allTracks.Count,
        playing = CurrentTrack?.Path,
        title = Title
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _engine.Dispose();
        var cover = Artwork;
        Artwork = null;
        cover?.Dispose();
    }
}
