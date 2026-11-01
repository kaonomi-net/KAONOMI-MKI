using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// The wiki of Kaonomi (<c>/wiki</c>): its pages stored in the search engine (the <c>wiki</c> index of the server),
/// listed and searched on the left, the page open edited with the rich editor of the journal and of /work (formatting,
/// pictures, links, code). A link to another page is <c>wiki:slug</c>: following it opens that page, or starts it when
/// it does not exist yet. ◀ goes back to the pages seen before, ⌂ to the home page (<see cref="HomeSlug"/>), and the
/// pages that link to the one open are listed under the pages. SAVE creates the page or saves a new version of it,
/// DELETE marks it deleted. The pictures inserted are copied into Pictures/kaonomi/wiki.
/// </summary>
public sealed partial class WikiWindowViewModel : ViewModelBase, ITapeAssistant, INoteContextProvider, IDisposable, IUnsavedChanges
{
    /// <summary>The slug of the home page of the wiki.</summary>
    public const string HomeSlug = "kaonomi";

    private readonly KaonomiAssistantService _server;
    private readonly WritingAssistant _writing;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _images = new();
    private readonly Stack<string> _history = new();
    /// <summary>True while the fields are filled from a page: no "not saved" for each of them.</summary>
    private bool _loading;

    /// <summary>For the designer.</summary>
    public WikiWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl), ChatSettings.SystemPicturesDirectory, null, null)
    {
    }

    public WikiWindowViewModel(KaonomiAssistantService server, string picturesDirectory, OllamaModelService? ollama, Func<string?>? model)
    {
        _server = server;
        PicturesDirectory = picturesDirectory;
        _writing = new WritingAssistant(ollama, model, server, WikiPicturesDirectory, "Wiki", "the wiki page");
        _ = InitializeAsync();
    }

    /// <summary>Folder of the pictures of the wiki: the pictures inserted are copied there (Pictures/kaonomi/wiki).</summary>
    public static string WikiPicturesDirectory => Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "wiki");

    /// <summary>Folder the picture picker opens in.</summary>
    public string PicturesDirectory { get; }

    /// <summary>Asks the user (a message and the label of the button); false when not answered.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    /// <summary>Opens a picture in the image analysis window (a double-click on it); null: the viewer of the system.</summary>
    public Func<string, ImageAnalysisViewModel>? CreateAnalysis { get; set; }

    // ── The pages ──

    /// <summary>The pages of the wiki (the ones changed last, or the ones found), on the left.</summary>
    public ObservableCollection<WikiPageEntry> Pages { get; } = [];

    /// <summary>The pages that link to the one open.</summary>
    public ObservableCollection<WikiPageEntry> Backlinks { get; } = [];

    /// <summary>A few words looked for in the title, content, summary, tags and author of the pages; empty: the pages changed last.</summary>
    [ObservableProperty]
    private string _search = string.Empty;

    // ── The page open ──

    /// <summary>The slug of the page open (null for a new page not saved yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private string? _pageId;

    /// <summary>True for a page not saved yet.</summary>
    public bool IsNew => PageId is null;

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>The page, in Markdown (the text of the editor).</summary>
    [ObservableProperty]
    private string _content = string.Empty;

    /// <summary>The tags, separated by spaces or commas (#TOPIC, !ALERT, ?QUESTION, &amp;LINK).</summary>
    [ObservableProperty]
    private string _tags = string.Empty;

    /// <summary>Its version, its dates and its author, under the title.</summary>
    [ObservableProperty]
    private string _info = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is nameof(Title) or nameof(Content) or nameof(Tags))
            IsDirty = true;
    }

    private async Task InitializeAsync()
    {
        await SearchAsync();
        await OpenPageAsync(HomeSlug, remember: false);
    }

    /// <summary>The pages changed last (empty search), or the pages found.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        try
        {
            var term = Search.Trim();
            var pages = term.Length == 0 ? await _server.GetLastWikiPagesAsync(300) : await _server.SearchWikiPagesAsync(term, 100);
            Pages.Clear();
            foreach (var page in term.Length == 0 ? pages.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase) : pages.AsEnumerable())
                Pages.Add(page);
            if (term.Length > 0)
                SetStatus($"{Pages.Count} page(s) found for \"{term}\"", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The pages could not be read: " + ex.Message, true);
        }
    }

    /// <summary>
    /// Opens the page <paramref name="slugOrTitle"/> (a slug, or a title made a slug); a page that does not exist yet is
    /// started with that title. The page open before is remembered for ◀ when <paramref name="remember"/>.
    /// </summary>
    public async Task OpenPageAsync(string slugOrTitle, bool remember = true)
    {
        if (IsDirty && !await ConfirmAsync($"The page \"{Title}\" is not saved. Open the other one anyway?", "Open"))
            return;
        var text = Uri.UnescapeDataString(slugOrTitle.Trim());
        if (text.StartsWith(WikiPageEntry.LinkScheme, StringComparison.OrdinalIgnoreCase))
            text = text[WikiPageEntry.LinkScheme.Length..];
        var slug = WikiPageEntry.Slug(text);
        if (remember && PageId is { } current && current != slug)
            _history.Push(current);
        CanGoBack = _history.Count > 0;
        IsBusy = true;
        try
        {
            var page = await _server.GetWikiPageAsync(slug);
            if (page is null)
            {
                // A page linked but not written yet (or the home page of an empty wiki): started, saved by SAVE.
                Load(null, slug == HomeSlug ? "Kaonomi" : text.Any(char.IsUpper) || text.Contains(' ') ? text : WikiPageEntry.TitleOf(slug),
                    slug == HomeSlug ? HomeTemplate : string.Empty);
                SetStatus(slug == HomeSlug ? "The wiki has no home page yet: write it, then SAVE" : $"The page \"{Title}\" does not exist yet: write it, then SAVE", false);
                Backlinks.Clear();
                await LoadBacklinksAsync(slug);
                return;
            }
            Load(page, page.Title, page.Content);
            SetStatus($"Page \"{page.Title}\" (version {page.Version})", false);
            await LoadBacklinksAsync(slug);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The page could not be read: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenEntryAsync(WikiPageEntry? page) => page?.Id is { } id ? OpenPageAsync(id) : Task.CompletedTask;

    private async Task LoadBacklinksAsync(string slug)
    {
        try
        {
            var pages = await _server.GetWikiBacklinksAsync(slug);
            Backlinks.Clear();
            foreach (var page in pages)
                Backlinks.Add(page);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Backlinks.Clear();
        }
    }

    private void Load(WikiPageEntry? page, string title, string content)
    {
        _loading = true;
        try
        {
            PageId = page?.Id;
            Title = title;
            Content = content;
            Tags = page is null ? string.Empty : string.Join(' ', page.Tags);
            Info = page is null
                ? "new page · not saved"
                : $"version {page.Version} · {page.DateLabel}" + (page.Author.Length > 0 ? $" · {page.Author}" : string.Empty)
                  + (page.Created is { } created ? $" · created {created.ToLocalTime():yyyy-MM-dd}" : string.Empty);
            IsDirty = false;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>The home page offered when the wiki has none.</summary>
    private const string HomeTemplate =
        "# Kaonomi\n\nBienvenue dans le wiki de Kaonomi.\n\n" +
        "## Pages\n\n" +
        "- [Le serveur](wiki:le-serveur)\n" +
        "- [Le chat](wiki:le-chat)\n" +
        "- [Les agents](wiki:les-agents)\n\n" +
        "Un lien vers une page qui n'existe pas encore la commence : clique dessus, écris-la, puis SAVE.\n";

    /// <summary>A new page.</summary>
    [RelayCommand]
    private async Task NewPageAsync()
    {
        if (IsDirty && !await ConfirmAsync($"The page \"{Title}\" is not saved. Start a new one anyway?", "New page"))
            return;
        if (PageId is { } current)
            _history.Push(current);
        CanGoBack = _history.Count > 0;
        Load(null, "New page", string.Empty);
        Backlinks.Clear();
        SetStatus("New page: give it a title, write it, then SAVE (its address is the slug of its title)", false);
    }

    /// <summary>Creates the page, or saves a new version of it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        var title = Title.Trim();
        if (title.Length == 0)
        {
            SetStatus("Give the page a title", true);
            return;
        }
        var entry = new WikiPageEntry
        {
            Id = PageId ?? WikiPageEntry.Slug(title),
            AppName = "KAONOMI.Chat",
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            Author = Environment.UserName,
            Title = title,
            Content = Content,
            Tags = Tags.Split([' ', ',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        };
        IsBusy = true;
        try
        {
            var result = PageId is null ? await _server.CreateWikiPageAsync(entry) : await _server.UpdateWikiPageAsync(entry);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("The page was not saved: " + (result.Message ?? "no answer"), true);
                return;
            }
            var saved = await _server.GetWikiPageAsync(result.ReturnedId);
            if (saved is not null)
                Load(saved, saved.Title, Content);
            else
            {
                PageId = result.ReturnedId;
                IsDirty = false;
            }
            SetStatus($"Page \"{title}\" saved ({result.Message})", false);
            ActivityLog.Info("Wiki", $"Page {result.ReturnedId} saved ({Content.Length} chars)");
            await SearchAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The page was not saved: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDelete() => PageId is not null;

    /// <summary>Marks the page deleted (it stays in the search engine, out of the wiki), then opens the home page.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (PageId is not { } id || !await ConfirmAsync($"Delete the page \"{Title}\"? It leaves the wiki (kept, marked deleted, in the search engine).", "Delete"))
            return;
        try
        {
            var result = await _server.DeleteWikiPageAsync(id);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("The page was not deleted: " + (result.Message ?? "no answer"), true);
                return;
            }
            ActivityLog.Info("Wiki", $"Page {id} marked deleted");
            IsDirty = false;
            await SearchAsync();
            await OpenPageAsync(id == HomeSlug ? HomeSlug : _history.Count > 0 ? _history.Pop() : HomeSlug, remember: false);
            CanGoBack = _history.Count > 0;
            SetStatus($"Page \"{id}\" deleted", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The page was not deleted: " + ex.Message, true);
        }
    }

    /// <summary>Back to the page seen before.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task BackAsync()
    {
        if (_history.Count == 0)
            return;
        var previous = _history.Peek();
        if (IsDirty && !await ConfirmAsync($"The page \"{Title}\" is not saved. Go back anyway?", "Back"))
            return;
        _history.Pop();
        IsDirty = false;
        await OpenPageAsync(previous, remember: false);
        CanGoBack = _history.Count > 0;
    }

    /// <summary>The home page of the wiki.</summary>
    [RelayCommand]
    private Task HomeAsync() => OpenPageAsync(HomeSlug);

    // ── Pictures ──

    /// <summary>
    /// Copies the pictures <paramref name="files"/> into the folder of the wiki (a new name when one is taken) and
    /// returns their new paths, to insert in the page.
    /// </summary>
    public List<string> CopyPictures(IEnumerable<string> files)
    {
        Directory.CreateDirectory(WikiPicturesDirectory);
        var copies = new List<string>();
        foreach (var file in files)
        {
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(file)) == Path.GetFullPath(WikiPicturesDirectory))
                {
                    copies.Add(file);
                    continue;
                }
                var name = Path.GetFileNameWithoutExtension(file);
                var extension = Path.GetExtension(file);
                var target = Path.Combine(WikiPicturesDirectory, name + extension);
                for (var i = 2; File.Exists(target); i++)
                    target = Path.Combine(WikiPicturesDirectory, $"{name}-{i}{extension}");
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
            "Tu es un relecteur de documentation technique exigeant et bienveillant. Réponds en Markdown, dans la langue du texte évalué, avec des titres courts.",
            "Analyse cette page de wiki et critique-la (exactitude, clarté, structure, liens manquants). Puis suggère des pistes d'amélioration, sans la réécrire.",
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
    public string NoteWindowName => "Wiki";

    /// <inheritdoc />
    public object? GetNoteContext() => new { page = PageId, title = Title, pages = Pages.Count };

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var image in _images.Values)
            if (image.IsCompletedSuccessfully)
                image.Result?.Dispose();
        _images.Clear();
    }
}
