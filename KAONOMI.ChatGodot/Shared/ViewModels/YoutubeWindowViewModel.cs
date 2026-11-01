using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A YouTube video saved in Kaonomi, in the list of <c>/youtube</c>.</summary>
public partial class YoutubeVideoItemViewModel : ObservableObject
{
    /// <summary>The saved document.</summary>
    public YoutubeVideoEntry Entry { get; }

    /// <summary>Wraps a saved video; its thumbnail is downloaded in the background.</summary>
    public YoutubeVideoItemViewModel(YoutubeVideoEntry entry)
    {
        Entry = entry;
        if (VideoId is { } id)
            _ = LoadThumbnailAsync(id);
    }

    /// <summary>The YouTube id of the video (from its id field, or read from its address).</summary>
    public string? VideoId => string.IsNullOrWhiteSpace(Entry.VideoId) ? YoutubeInfo.VideoId(Entry.Url) : Entry.VideoId;

    /// <summary>Title shown in the list.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Entry.Title) ? Entry.Url ?? "(untitled video)" : Entry.Title!;

    /// <summary>Date added, shown under the title.</summary>
    public string Summary => Entry.Date is { } date ? $"Added {date.ToLocalTime():yyyy-MM-dd HH:mm}" : string.Empty;

    /// <summary>The address opened by ▶ Open.</summary>
    public string? OpenUrl => VideoId is { } id ? YoutubeInfo.WatchUrl(id) : Entry.Url;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    private async Task LoadThumbnailAsync(string id)
    {
        if (await YoutubeInfo.ThumbnailAsync(id, CancellationToken.None) is { } bytes)
            Thumbnail = YoutubeWindowViewModel.DecodeImage(bytes);
    }
}

/// <summary>
/// YouTube videos of Kaonomi (<c>/youtube</c>, <c>/nyoutube</c> in its own window): the address of a video fills its
/// title and description from YouTube, the video is added with <c>PUT /youtubevideo</c>; the saved videos are listed
/// (newest first, or a search on their title and description) and opened in the browser.
/// </summary>
public partial class YoutubeWindowViewModel : ViewModelBase, INoteContextProvider
{
    /// <inheritdoc />
    public string NoteWindowName => "YouTube";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        searchTerm = Term,
        videoCount = Videos.Count,
        url = Url,
        title = Title,
        editingId = EditingId
    };

    private const int PageSize = 100;

    private readonly KaonomiAssistantService _assistant;

    /// <summary>Cancels the reading of the previous address when a new one is typed.</summary>
    private CancellationTokenSource? _fetch;

    /// <summary>Id of the video whose information was read last, and the title / description it gave.</summary>
    private string? _fetchedId;
    private string? _fetchedTitle;
    private string? _fetchedDescription;

    /// <summary>Design-time constructor using default server URL from settings.</summary>
    public YoutubeWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    /// <summary>Uses the given assistant client for the video API calls; loads the latest videos.</summary>
    public YoutubeWindowViewModel(KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _ = SearchAsync(CancellationToken.None);
    }

    /// <summary>Videos of the last search (or the newest ones).</summary>
    public ObservableCollection<YoutubeVideoItemViewModel> Videos { get; } = [];

    /// <summary>Asks the user to confirm (message, confirm label); set by the view.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    /// <summary>Opens an address in the browser; set to the system default (replaced by the tests).</summary>
    public Action<string> OpenInBrowser { get; set; } = PictureLibrary.OpenWithSystem;

    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    private YoutubeVideoItemViewModel? _selectedVideo;

    /// <summary>Address typed or pasted; its title and description are read from YouTube.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _comment = string.Empty;

    /// <summary>Channel of the video (read from YouTube, not saved).</summary>
    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    /// <summary>Document id of the video being edited (null: a new video).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private string? _editingId;

    [ObservableProperty]
    private bool _isFetching;

    [ObservableProperty]
    private string _status = "Paste the address of a YouTube video";

    [ObservableProperty]
    private bool _isError;

    /// <summary>True while the editor holds a video not saved yet.</summary>
    public bool IsNew => EditingId is null;

    /// <summary>Label of the save button.</summary>
    public string SaveLabel => IsNew ? "Add to Kaonomi" : "Save";

    /// <summary>The id of the video of <see cref="Url"/> (null when it is not a YouTube address).</summary>
    public string? VideoId => YoutubeInfo.VideoId(Url);

    partial void OnUrlChanged(string value)
    {
        var id = YoutubeInfo.VideoId(value);
        if (id is null)
        {
            _fetch?.Cancel();
            IsFetching = false;
            if (value.Trim().Length > 0)
                SetStatus("This is not the address of a YouTube video", error: true);
            Thumbnail = null;
            return;
        }
        if (id != _fetchedId)
            _ = FetchAsync(id);
    }

    partial void OnSelectedVideoChanged(YoutubeVideoItemViewModel? value)
    {
        if (value is null)
            return;
        var entry = value.Entry;
        _fetch?.Cancel();
        _fetchedId = value.VideoId;
        _fetchedTitle = entry.Title;
        _fetchedDescription = entry.Description;
        EditingId = entry.Id;
        Url = entry.Url ?? (value.VideoId is { } id ? YoutubeInfo.WatchUrl(id) : string.Empty);
        Title = entry.Title ?? string.Empty;
        Description = entry.Description ?? string.Empty;
        Comment = entry.Comment ?? string.Empty;
        Author = string.Empty;
        Thumbnail = value.Thumbnail;
        if (value.Thumbnail is null)
            _ = LoadThumbnailAsync(value);
        IsFetching = false;
        SetStatus($"Saved video · {value.Summary}");
    }

    /// <summary>The thumbnail of a saved video selected before its own was downloaded.</summary>
    private async Task LoadThumbnailAsync(YoutubeVideoItemViewModel video)
    {
        if (video.VideoId is { } id && await YoutubeInfo.ThumbnailAsync(id, CancellationToken.None) is { } bytes
            && ReferenceEquals(SelectedVideo, video))
            Thumbnail = DecodeImage(bytes);
    }

    /// <summary>
    /// Reads the title and description of <paramref name="videoId"/> (half a second after the typing pauses); they
    /// replace the fields unless the user changed them.
    /// </summary>
    private async Task FetchAsync(string videoId)
    {
        _fetch?.Cancel();
        var cancellation = _fetch = new CancellationTokenSource();
        var token = cancellation.Token;
        try
        {
            await Task.Delay(500, token);
            IsFetching = true;
            SetStatus("Reading the video on YouTube…");
            var thumbnail = YoutubeInfo.ThumbnailAsync(videoId, token);
            var info = await YoutubeInfo.FetchAsync(videoId, token);
            if (Title.Length == 0 || Title == _fetchedTitle)
                Title = info.Title ?? string.Empty;
            if (Description.Length == 0 || Description == _fetchedDescription)
                Description = info.Description ?? string.Empty;
            Author = info.Author ?? string.Empty;
            _fetchedId = videoId;
            _fetchedTitle = info.Title;
            _fetchedDescription = info.Description;
            Thumbnail = await thumbnail is { } bytes ? DecodeImage(bytes) : null;

            var existing = Videos.FirstOrDefault(v => v.VideoId == videoId && v.Entry.Id != EditingId);
            SetStatus(existing is not null
                ? $"Already in Kaonomi (added {existing.Entry.Date?.ToLocalTime():yyyy-MM-dd}): select it in the list to open it"
                : info.Description is null ? "Title read from YouTube (no description found)" : "Title and description read from YouTube",
                error: existing is not null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (HttpRequestException ex)
        {
            SetStatus(ex.Message, error: true);
            ActivityLog.Error("YouTube", $"Reading video {videoId} failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_fetch, cancellation))
                IsFetching = false;
        }
    }

    /// <summary>Searches the saved videos on their title and description; an empty term lists the newest ones.</summary>
    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var term = Term.Trim();
        var isLast = term.Length == 0 || term == "*";
        try
        {
            var entries = isLast
                ? await _assistant.GetLastYoutubeVideosAsync(PageSize, cancellationToken)
                : await _assistant.SearchYoutubeVideosAsync(term, PageSize, cancellationToken);
            Videos.Clear();
            foreach (var entry in entries)
                Videos.Add(new YoutubeVideoItemViewModel(entry));
            if (!isLast)
                SetStatus(Videos.Count == 0 ? $"No video for \"{term}\"" : $"{Videos.Count} video(s) for \"{term}\"");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            SetStatus($"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})", error: true);
            ActivityLog.Error("YouTube", "Video search failed: server unreachable", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus(ex.Message, error: true);
            ActivityLog.Error("YouTube", "Video search failed", ex);
        }
    }

    private bool CanSave() => VideoId is not null;

    /// <summary>Adds the video to Kaonomi (or saves the changes of a saved one), then lists it.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (VideoId is not { } videoId)
            return;
        var entry = new YoutubeVideoEntry
        {
            Id = EditingId,
            Date = SelectedVideo?.Entry.Id == EditingId ? SelectedVideo?.Entry.Date : null,
            AppName = "KAONOMI.Chat",
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            VideoId = videoId,
            Url = YoutubeInfo.WatchUrl(videoId),
            Title = Title.Trim(),
            Description = Description.Trim(),
            Comment = Comment.Trim().Length == 0 ? null : Comment.Trim()
        };
        try
        {
            var result = IsNew ? await _assistant.CreateYoutubeVideoAsync(entry) : await _assistant.UpdateYoutubeVideoAsync(entry);
            if (!result.Success || result.ReturnedId is null or "-1")
            {
                SetStatus(result.Message ?? "Not saved", error: true);
                return;
            }
            var added = IsNew;
            EditingId = result.ReturnedId;
            ActivityLog.Info("YouTube", $"{(added ? "Added" : "Saved")} video {videoId}: {entry.Title}");
            // Elasticsearch makes a new document searchable about a second later.
            await Task.Delay(1200);
            Term = string.Empty;
            await SearchAsync(CancellationToken.None);
            SelectedVideo = Videos.FirstOrDefault(v => v.Entry.Id == result.ReturnedId);
            SetStatus(added ? "✓ Added to Kaonomi" : "✓ Saved");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            SetStatus(ex is HttpRequestException { StatusCode: null } ? $"Kaonomi server unreachable at {_assistant.BaseAddress}" : ex.Message, error: true);
            ActivityLog.Error("YouTube", $"Saving video {videoId} failed", ex);
        }
    }

    /// <summary>Empties the editor for a new video.</summary>
    [RelayCommand]
    private void New()
    {
        _fetch?.Cancel();
        SelectedVideo = null;
        EditingId = null;
        _fetchedId = _fetchedTitle = _fetchedDescription = null;
        Url = Title = Description = Comment = Author = string.Empty;
        Thumbnail = null;
        SetStatus("Paste the address of a YouTube video");
    }

    private bool CanOpen(YoutubeVideoItemViewModel? video) => video is not null || VideoId is not null;

    /// <summary>Opens the video (the one given, or the one of the editor) in the browser.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open(YoutubeVideoItemViewModel? video)
    {
        var url = video?.OpenUrl ?? (VideoId is { } id ? YoutubeInfo.WatchUrl(id) : null);
        if (url is null)
            return;
        try
        {
            OpenInBrowser(url);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus($"Cannot open {url}: {ex.Message}", error: true);
        }
    }

    /// <summary>Plays a video in a window that shows only the video (<see cref="YoutubePlayer"/>).</summary>
    public Action<string, string?> PlayVideo { get; set; } = YoutubePlayer.Play;

    /// <summary>
    /// Plays the video (the one given, or the one of the editor) in a window that shows only the video: double-click
    /// on a video of the list or on the picture of the editor, or ▶ Play.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Play(YoutubeVideoItemViewModel? video)
    {
        var (id, title) = video is not null ? (video.VideoId, video.DisplayTitle) : (VideoId, Title);
        if (id is null)
        {
            SetStatus("This address is not the one of a YouTube video", error: true);
            return;
        }
        try
        {
            PlayVideo(id, title);
            SetStatus($"▶ Playing {title}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or System.Net.HttpListenerException or System.Net.Sockets.SocketException or ArgumentException)
        {
            SetStatus($"Cannot play the video: {ex.Message}", error: true);
        }
    }

    private bool CanDelete() => EditingId is not null;

    /// <summary>Deletes the saved video of the editor from Kaonomi (after confirmation).</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (EditingId is not { } id || !await ConfirmAsync($"Delete \"{Title}\" from Kaonomi?", "Delete"))
            return;
        try
        {
            var result = await _assistant.DeleteYoutubeVideoAsync(id);
            if (!result.Success)
            {
                SetStatus(result.Message ?? "Not deleted", error: true);
                return;
            }
            if (Videos.FirstOrDefault(v => v.Entry.Id == id) is { } row)
                Videos.Remove(row);
            New();
            SetStatus("Video deleted");
            ActivityLog.Info("YouTube", $"Deleted video {id}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            SetStatus(ex.Message, error: true);
        }
    }

    private void SetStatus(string message, bool error = false)
    {
        Status = message;
        IsError = error;
    }

    /// <summary>A downloaded picture (null when it cannot be decoded).</summary>
    internal static Bitmap? DecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
