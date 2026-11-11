using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>Editable copy of an idea; the id, date, user and machine of the original document are kept on save.</summary>
public partial class IdeaEntryViewModel : ViewModelBase
{
    private static readonly HashSet<string> EditableProperties =
    [
        nameof(IdeaTitle), nameof(IdeaDescription), nameof(Category), nameof(Tags), nameof(Events), nameof(Signals), nameof(Comment),
        nameof(ProjectId)
    ];

    /// <summary>The project the idea is attached to (the id of a project, chosen by its name in the header of the window), or null.</summary>
    [ObservableProperty]
    private string? _projectId;

    private readonly KaonomiAssistantService _assistant;
    private IdeaEntry _entry;
    private bool _isLoading;

    /// <summary>Loads server data into editable fields and tracks dirty state.</summary>
    public IdeaEntryViewModel(IdeaEntry entry, KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _entry = entry;
        Load(entry);

        if (IsNew)
        {
            IsDirty = true;
            Status = "Unsaved new idea";
        }
    }

    /// <summary>Blank idea, created on the server (<c>PUT /idea</c>) on first save.</summary>
    public static IdeaEntryViewModel CreateNew(KaonomiAssistantService assistant) => new(new IdeaEntry
    {
        Date = DateTimeOffset.Now,
        AppName = "KAONOMI.Chat",
        MachineName = Environment.MachineName,
        UserName = Environment.UserName
    }, assistant);

    /// <summary>Server document id, or null before first save.</summary>
    public string? Id => _entry.Id;

    /// <summary>True when the idea has not yet been persisted.</summary>
    public bool IsNew => _entry.Id is null;

    /// <summary>Human-readable id status for the detail panel.</summary>
    public string IdLabel => IsNew ? "New idea · id assigned on save" : $"Id: {_entry.Id}";

    /// <summary>Formatted local date for list display.</summary>
    public string DateLabel => _entry.Date?.ToLocalTime().ToString("g") ?? "Unknown date";

    /// <summary>Author and machine of the idea.</summary>
    public string AuthorLabel => Join(" · ", _entry.UserName, _entry.MachineName, _entry.AppName);

    /// <summary>Title shown in the list ("(untitled)" while empty).</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(IdeaTitle) ? "(untitled)" : IdeaTitle;

    /// <summary>Compact summary line for list rows.</summary>
    public string Summary => Join(" · ", DateLabel, Category);

    /// <summary>True when at least one tag line is present.</summary>
    public bool HasTags => !string.IsNullOrWhiteSpace(Tags);

    /// <summary>Comma-separated tags for compact list display.</summary>
    public string TagsPreview => string.Join(", ", SplitLines(Tags));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _ideaTitle = string.Empty;

    [ObservableProperty]
    private string _ideaDescription = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _category = string.Empty;

    /// <summary>One tag per line (#TOPIC; the spaces of a line become '-', see <see cref="MetaTags"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags), nameof(TagsPreview))]
    private string _tags = string.Empty;

    /// <summary>One event per line (@EVENT).</summary>
    [ObservableProperty]
    private string _events = string.Empty;

    /// <summary>One signal per line (!SIGNAL).</summary>
    [ObservableProperty]
    private string _signals = string.Empty;

    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private bool _isDirty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>Raised after a successful save (the window refreshes its category list).</summary>
    public event Action<IdeaEntryViewModel>? Saved;

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (!_isLoading && e.PropertyName is { } name && EditableProperties.Contains(name))
        {
            IsDirty = true;
            IsError = false;
            Status = "Unsaved changes";
        }
    }

    private bool CanSave() => IsDirty;

    /// <summary>Creates or updates the idea on the server.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(IdeaTitle))
        {
            IsError = true;
            Status = "Not saved: the title is required";
            return;
        }

        var updated = ToEntry();
        var isNew = IsNew;
        IsError = false;
        Status = isNew ? "Creating…" : "Saving…";

        try
        {
            var result = isNew
                ? await _assistant.CreateIdeaAsync(updated, cancellationToken)
                : await _assistant.UpdateIdeaAsync(updated, cancellationToken);
            if (!result.IsSuccess)
            {
                IsError = true;
                Status = result.Message ?? "The Kaonomi server rejected the save.";
                ActivityLog.Error("Idea", $"{(isNew ? "Create" : "Update")} idea {_entry.Id ?? "(new)"} rejected: {Status}");
                return;
            }

            if (isNew)
                updated.Id = string.IsNullOrEmpty(result.ReturnedId) ? null : result.ReturnedId;

            _entry = updated;
            IsDirty = IsNew;
            OnPropertyChanged(nameof(Id));
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(IdLabel));
            IsError = IsNew;
            Status = IsNew
                ? "Created, but the server did not return an id"
                : $"{(isNew ? "Created" : "Saved")} · {DateTime.Now:T}";
            if (IsNew)
                ActivityLog.Error("Idea", "Idea created, but the server did not return an id");
            else
            {
                ActivityLog.Info("Idea", $"Idea {(isNew ? "created" : "updated")}: {_entry.Id}");
                Saved?.Invoke(this);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Save cancelled";
            ActivityLog.Info("Idea", "Idea save cancelled");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Idea", "Idea not saved: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Idea", "Idea not saved", ex);
        }
    }

    /// <summary>Discards edits and reloads the last saved server snapshot.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Revert()
    {
        Load(_entry);
        IsDirty = IsNew;
        Status = "Changes discarded";
        ActivityLog.Info("Idea", $"Idea changes discarded: {_entry.Id ?? "(new)"}");
    }

    private void Load(IdeaEntry entry)
    {
        _isLoading = true;
        try
        {
            // The title of the idea, or the title shared by every document when the idea has none.
            IdeaTitle = (string.IsNullOrWhiteSpace(entry.IdeaTitle) ? entry.Title : entry.IdeaTitle) ?? string.Empty;
            IdeaDescription = entry.IdeaDescription ?? string.Empty;
            Category = entry.Category ?? string.Empty;
            Tags = string.Join(Environment.NewLine, entry.Tags ?? []);
            Events = string.Join(Environment.NewLine, entry.Events ?? []);
            Signals = string.Join(Environment.NewLine, entry.Signals ?? []);
            Comment = entry.Comment ?? string.Empty;
            ProjectId = entry.ProjectId;
            IsDirty = false;
            IsError = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Copy of the values currently edited (also used as note context).</summary>
    internal IdeaEntry ToEntry() => new()
    {
        Id = _entry.Id,
        Date = _entry.Date,
        AppName = _entry.AppName,
        ProjectId = ProjectId,
        MachineName = _entry.MachineName,
        UserName = _entry.UserName,
        IdeaTitle = IdeaTitle.Trim(),
        Title = IdeaTitle.Trim(),
        IdeaDescription = IdeaDescription,
        Category = Category.Trim(),
        Tags = MetaTags.Normalize(SplitLines(Tags), MetaTags.Tag),
        Events = MetaTags.Normalize(SplitLines(Events), MetaTags.Event),
        Signals = MetaTags.Normalize(SplitLines(Signals), MetaTags.Signal),
        Comment = Comment
    };

    private static List<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
