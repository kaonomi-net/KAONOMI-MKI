using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

/// <summary>An emotion of the journal: its emoticon, and the name saved in the <c>feeling</c> field.</summary>
public partial class JournalFeeling(string emoji, string name) : ObservableObject
{
    /// <summary>The emoticon shown on the button.</summary>
    public string Emoji { get; } = emoji;

    /// <summary>The emotion saved in <c>feeling</c> (Happy, Sad…).</summary>
    public string Name { get; } = name;

    /// <summary>True for the emotion of the entry.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The emotions of the journal, in the order of the buttons.</summary>
    public static IReadOnlyList<(string Emoji, string Name)> All { get; } =
    [
        ("😊", "Happy"), ("😢", "Sad"), ("😰", "Stressed"), ("😠", "Angry"), ("🤔", "Thoughtful"), ("🤢", "Disgusted"), ("😐", "Neutral")
    ];

    /// <summary>The emoticon of a saved emotion (empty for an unknown one).</summary>
    public static string EmojiOf(string? name) =>
        All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).Emoji ?? string.Empty;
}

/// <summary>A previous entry of the history list.</summary>
public sealed class JournalHistoryItem(DiaryEntry entry)
{
    public DiaryEntry Entry { get; } = entry;

    /// <summary>Date of the entry, long.</summary>
    public string DateLabel => Entry.Date?.ToLocalTime().ToString("dddd d MMMM yyyy · HH:mm") ?? "No date";

    /// <summary>First words of the entry, without its Markdown marks ("🔒 Encrypted entry" when it is encrypted).</summary>
    public string Preview
    {
        get
        {
            if (JournalCrypto.IsEncrypted(Entry.Text))
                return "🔒 Encrypted entry: type its password to read it";
            // Pictures, links, HTML tags (<u>, <span style…>, <p class…>) and Markdown marks removed, entities decoded.
            var text = System.Text.RegularExpressions.Regex.Replace(Entry.Text ?? string.Empty, @"!\[[^\]]*\]\([^)]*\)|<[^>\n]+>|\\(?=[\\*`\[\]!#])|[#*_`>\[\]]|\([^)]*\)", " ");
            text = text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > 120 ? text[..120] + "…" : text;
        }
    }

    /// <summary>Feeling of the entry: its emoticon and name (Happy, Sad…).</summary>
    public string Feeling => Entry.Feeling is { Length: > 0 } feeling
        ? $"{JournalFeeling.EmojiOf(feeling)} {feeling}".Trim()
        : string.Empty;
}

/// <summary>
/// Journal of <c>/diary</c> or <c>/journal</c> (<c>/ndiary</c> in its own window): writes a new entry in Markdown (headings,
/// bold, italic, underline, links, code, local pictures that can be resized; in the Avalonia chat edited as it is shown,
/// with the editor and the ribbon of <c>/work</c>, see <see cref="ITapeAssistant"/>), and saves it with
/// <c>PUT /diary</c> (<c>POST /diary/update</c> when it is saved again). With a password of at least 12 characters, the
/// entry is encrypted with AES-256 before it leaves the chat (<see cref="JournalCrypto"/>); an encrypted entry is opened
/// locked and unlocked with its password. The previous entries stay hidden unless the history is shown (search on their
/// text, open to read or edit, delete).
/// </summary>
public partial class JournalWindowViewModel : ViewModelBase, INoteContextProvider, IDisposable, ITapeAssistant, IUnsavedChanges
{
    private readonly KaonomiAssistantService _assistant;

    /// <summary>Pictures decoded for the preview, by path and width bucket (released with the window).</summary>
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _images = new();

    /// <summary>Design-time constructor.</summary>
    public JournalWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl), ChatSettings.SystemPicturesDirectory)
    {
    }

    /// <summary>Uses <paramref name="assistant"/> for the <c>/diary</c> APIs; pictures are picked in <paramref name="picturesDirectory"/>.</summary>
    public JournalWindowViewModel(KaonomiAssistantService assistant, string picturesDirectory)
        : this(assistant, picturesDirectory, null, null)
    {
    }

    /// <summary>
    /// Uses <paramref name="assistant"/> for the <c>/diary</c> APIs (and Fooocus); pictures are picked in
    /// <paramref name="picturesDirectory"/>; the selected text is corrected, rewritten, evaluated and translated by the
    /// model of <paramref name="model"/> through <paramref name="ollama"/>.
    /// </summary>
    public JournalWindowViewModel(KaonomiAssistantService assistant, string picturesDirectory, OllamaModelService? ollama,
        Func<string?>? model)
    {
        _assistant = assistant;
        PicturesDirectory = picturesDirectory;
        _writing = new WritingAssistant(ollama, model, assistant, JournalPicturesDirectory, "Journal", "the journal entry");
    }

    /// <summary>The AI tools of the editor (the ones of the tape of /work).</summary>
    private readonly WritingAssistant _writing;

    /// <summary>
    /// Folder of the pictures the journal makes itself: generated with Fooocus, or cropped (Pictures/kaonomi/journal); the
    /// pictures inserted or dropped are linked where they are.
    /// </summary>
    public static string JournalPicturesDirectory => System.IO.Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "journal");

    /// <summary>Opens a picture of the entry in the image analysis window (a double-click on it); null: the viewer of the system.</summary>
    public Func<string, ImageAnalysisViewModel>? CreateAnalysis { get; set; }

    /// <summary>Prompt of 🧐 Evaluate (right click on selected text of the entry), followed by the text.</summary>
    public const string EvaluationPrompt =
        "Analyse ce texte et critique le, sur le fond et sur la forme (clarté, style, structure). Puis suggère des pistes d'amélioration, sans le réécrire.";

    /// <inheritdoc />
    public string? CurrentModelName => _writing.CurrentModelName;

    /// <inheritdoc />
    public bool CanEvaluate => _writing.CanAsk;

    /// <inheritdoc />
    public bool CanGenerateImages => _writing.CanGenerateImages;

    /// <inheritdoc />
    public Task<string> EvaluateAsync(string text, CancellationToken cancellationToken) =>
        _writing.EvaluateAsync(text,
            "Tu es un relecteur exigeant et bienveillant. Réponds en Markdown, dans la langue du texte évalué, avec des titres courts.",
            EvaluationPrompt, cancellationToken);

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

    /// <summary>Folder the image picker opens in (<c>pictures.directory</c> of config.yml).</summary>
    public string PicturesDirectory { get; }

    /// <summary>The emotions offered as emoticon buttons; the name of the chosen one is saved in <c>feeling</c>.</summary>
    public IReadOnlyList<JournalFeeling> Feelings { get; } =
        JournalFeeling.All.Select(f => new JournalFeeling(f.Emoji, f.Name)).ToList();

    /// <summary>Chooses an emotion (choosing it again clears it).</summary>
    [RelayCommand]
    private void ChooseFeeling(JournalFeeling? feeling)
    {
        if (feeling is null)
            return;
        Feeling = string.Equals(Feeling, feeling.Name, StringComparison.OrdinalIgnoreCase) ? string.Empty : feeling.Name;
    }

    /// <summary>Label of the chosen emotion, next to the buttons.</summary>
    public string FeelingLabel => Feeling.Length == 0 ? "How do you feel?" : Feeling;

    /// <summary>The entry being written, in Markdown.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _text = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _feeling = string.Empty;

    /// <summary>Id of the entry being edited (null for a new one, until it is saved).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeLabel), nameof(IsEditingSaved))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private string? _entryId;

    /// <summary>Date of the entry being edited.</summary>
    private DateTimeOffset _entryDate = DateTimeOffset.Now;

    /// <summary>True when the text differs from the last save.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isDirty;

    /// <summary>"New entry" or the date of the entry being edited.</summary>
    public string ModeLabel => EntryId is null
        ? $"New entry · {_entryDate.ToLocalTime():dddd d MMMM yyyy · HH:mm}"
        : $"Entry of {_entryDate.ToLocalTime():dddd d MMMM yyyy · HH:mm}";

    /// <summary>True when the entry shown is saved on the server.</summary>
    public bool IsEditingSaved => EntryId is not null;

    /// <summary>
    /// Password of the entry (never saved): empty, the entry is saved as it is; from 12 characters, it is encrypted with
    /// AES-256; an encrypted entry is unlocked with it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordInfo), nameof(IsPasswordValid), nameof(WillEncrypt))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(UnlockCommand))]
    private string _password = string.Empty;

    /// <summary>True when the password is long enough (or empty: no encryption).</summary>
    public bool IsPasswordValid => Password.Length == 0 || Password.Length >= JournalCrypto.MinPasswordLength;

    /// <summary>True when the entry will be encrypted when saved.</summary>
    public bool WillEncrypt => Password.Length >= JournalCrypto.MinPasswordLength;

    /// <summary>State of the password, next to its field.</summary>
    public string PasswordInfo => Password.Length == 0
        ? "Not encrypted"
        : WillEncrypt
            ? "🔒 Encrypted with AES-256 when saved"
            : $"{Password.Length}/{JournalCrypto.MinPasswordLength} characters";

    /// <summary>True while an encrypted entry is opened but not unlocked (its text is hidden).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(UnlockCommand))]
    private bool _isLocked;

    /// <summary>The encrypted text of the locked entry.</summary>
    private string? _lockedText;

    partial void OnPasswordChanged(string value)
    {
        // A saved entry is saved again with the new password (or without encryption).
        if (!IsLocked && !string.IsNullOrWhiteSpace(Text) && EntryId is not null)
            IsDirty = true;
    }

    private bool CanUnlock() => IsLocked && Password.Length > 0;

    /// <summary>Decrypts the locked entry with the password.</summary>
    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        if (_lockedText is not { } encrypted)
            return;
        var password = Password;
        Status = "Unlocking…";
        try
        {
            var text = await Task.Run(() => JournalCrypto.Decrypt(encrypted, password));
            IsLocked = false;
            _lockedText = null;
            Text = text;
            IsDirty = false;
            IsError = false;
            Status = "Entry unlocked";
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            IsError = true;
            Status = ex is FormatException ? ex.Message : "Wrong password: the entry stays locked";
        }
    }

    /// <summary>Shows the previous entries (hidden by default).</summary>
    [ObservableProperty]
    private bool _showHistory;

    /// <summary>Previous entries (last ones, or the search results).</summary>
    public ObservableCollection<JournalHistoryItem> History { get; } = [];

    [ObservableProperty]
    private string _historySearch = string.Empty;

    [ObservableProperty]
    private string _status = "Write your entry; select text to format it";

    [ObservableProperty]
    private bool _isError;

    /// <summary>Asks the user to confirm (message, confirm label); set by the view.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    partial void OnTextChanged(string value) => IsDirty = true;

    partial void OnFeelingChanged(string value)
    {
        IsDirty = true;
        foreach (var feeling in Feelings)
            feeling.IsSelected = string.Equals(feeling.Name, value, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(FeelingLabel));
    }

    partial void OnShowHistoryChanged(bool value)
    {
        if (value && History.Count == 0)
            SearchHistoryCommand.Execute(null);
    }

    private bool CanSave() => IsDirty && !IsLocked && !string.IsNullOrWhiteSpace(Text) && IsPasswordValid;

    /// <summary>Saves the entry: created the first time (<c>PUT /diary</c>), updated afterwards.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var isNew = EntryId is null;
        var encrypted = WillEncrypt;
        IsError = false;
        Status = encrypted ? "Encrypting and saving…" : "Saving…";
        try
        {
            var text = Text;
            var password = Password;
            var entry = new DiaryEntry
            {
                Id = EntryId,
                Date = _entryDate,
                AppName = "KAONOMI.Chat",
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                // Encrypted here: the server (and Elasticsearch) only see the ciphertext.
                Text = encrypted ? await Task.Run(() => JournalCrypto.Encrypt(text, password), cancellationToken) : text,
                Feeling = string.IsNullOrWhiteSpace(Feeling) ? null : Feeling.Trim()
            };
            var result = isNew
                ? await _assistant.CreateDiaryAsync(entry, cancellationToken)
                : await _assistant.UpdateDiaryAsync(entry, cancellationToken);
            if (!result.IsSuccess || string.IsNullOrEmpty(result.ReturnedId))
            {
                IsError = true;
                Status = result.Message ?? "The Kaonomi server rejected the entry";
                return;
            }
            EntryId = result.ReturnedId;
            IsDirty = false;
            Status = $"{(isNew ? "Entry saved" : "Entry updated")}{(encrypted ? " (encrypted with AES-256)" : string.Empty)} · {DateTime.Now:T}";
            ActivityLog.Info("Journal", $"Entry {(isNew ? "created" : "updated")}: {EntryId}");
            if (ShowHistory)
                SearchHistoryCommand.Execute(null);
        }
        catch (OperationCanceledException)
        {
            Status = "Save cancelled";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Journal", "Entry not saved", ex);
        }
    }

    /// <summary>Starts a new, empty entry (asks first when the current one is not saved).</summary>
    [RelayCommand]
    private async Task NewEntryAsync()
    {
        if (IsDirty && !string.IsNullOrWhiteSpace(Text)
            && !await ConfirmAsync("The current entry is not saved. Start a new one anyway?", "New entry"))
            return;
        EntryId = null;
        _entryDate = DateTimeOffset.Now;
        IsLocked = false;
        _lockedText = null;
        Text = string.Empty;
        Feeling = string.Empty;
        IsDirty = false;
        OnPropertyChanged(nameof(ModeLabel));
        Status = "New entry";
    }

    /// <summary>Lists the previous entries: the last ones, or those whose text matches the search.</summary>
    [RelayCommand]
    private async Task SearchHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var term = HistorySearch.Trim();
            var entries = term.Length == 0
                ? await _assistant.GetLastDiaryAsync(100, cancellationToken)
                : await _assistant.SearchDiaryAsync(term, 100, cancellationToken);
            History.Clear();
            foreach (var entry in entries)
                History.Add(new JournalHistoryItem(entry));
            IsError = false;
            Status = term.Length == 0 ? $"{History.Count} previous entr{(History.Count == 1 ? "y" : "ies")}" : $"{History.Count} entr{(History.Count == 1 ? "y" : "ies")} match \"{term}\"";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            IsError = true;
            Status = $"Previous entries not read: {ex.Message}";
        }
    }

    /// <summary>Opens a previous entry in the editor (to read it or change it).</summary>
    [RelayCommand]
    private async Task OpenEntryAsync(JournalHistoryItem? item)
    {
        if (item is null)
            return;
        if (IsDirty && !string.IsNullOrWhiteSpace(Text) && !string.Equals(item.Entry.Id, EntryId, StringComparison.Ordinal)
            && !await ConfirmAsync("The current entry is not saved. Open the other one anyway?", "Open"))
            return;
        EntryId = item.Entry.Id;
        _entryDate = item.Entry.Date ?? DateTimeOffset.Now;
        Feeling = item.Entry.Feeling ?? string.Empty;
        OnPropertyChanged(nameof(ModeLabel));
        if (JournalCrypto.IsEncrypted(item.Entry.Text))
        {
            // Opened locked; unlocked at once when the password typed is the right one.
            _lockedText = item.Entry.Text;
            IsLocked = true;
            Text = string.Empty;
            IsDirty = false;
            Status = "🔒 Encrypted entry: type its password and press Unlock";
            if (Password.Length > 0)
                await UnlockAsync();
            return;
        }
        IsLocked = false;
        _lockedText = null;
        Text = item.Entry.Text ?? string.Empty;
        IsDirty = false;
        Status = $"Entry of {_entryDate.ToLocalTime():g} opened";
    }

    private bool CanDelete() => EntryId is not null;

    /// <summary>Deletes the entry shown (soft delete on the server: it is only marked deleted), then starts a new one.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (EntryId is not { } id || !await ConfirmAsync("Delete this entry of the journal?", "Delete"))
            return;
        try
        {
            var result = await _assistant.DeleteDiaryAsync(id, cancellationToken);
            if (!result.IsSuccess)
            {
                IsError = true;
                Status = result.Message ?? "Not deleted";
                return;
            }
            IsDirty = false;
            await NewEntryAsync();
            Status = "Entry deleted";
            ActivityLog.Info("Journal", $"Entry deleted: {id}");
            if (ShowHistory)
                SearchHistoryCommand.Execute(null);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            IsError = true;
            Status = $"Not deleted: {ex.Message}";
        }
    }

    /// <summary>Width written back after an image was resized in the preview.</summary>
    public void SetImageWidth(int index, int width) => Text = Views.JournalMarkdown.SetImageWidth(Text, index, width);

    /// <summary>A picture for the preview (decoded once, at most 1600 pixels wide).</summary>
    public Task<Bitmap?> LoadImageAsync(string path) =>
        _images.GetOrAdd(path, p => PictureLibrary.DecodeAsync(p, 1600, CancellationToken.None));

    /// <inheritdoc />
    public string NoteWindowName => "Journal";

    /// <inheritdoc />
    public object? GetNoteContext() => new { id = EntryId, editing = EntryId, feeling = Feeling, encrypted = WillEncrypt || IsLocked };

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var image in _images.Values)
            if (image.IsCompletedSuccessfully)
                image.Result?.Dispose();
        _images.Clear();
    }
}
