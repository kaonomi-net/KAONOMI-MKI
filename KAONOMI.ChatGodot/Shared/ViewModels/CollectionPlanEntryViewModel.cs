using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

/// <summary>Editable copy of a collection plan; the id and date of the original document are kept on save.</summary>
public partial class CollectionPlanEntryViewModel : ViewModelBase
{
    private static readonly HashSet<string> EditableProperties =
    [
        nameof(AppName), nameof(MachineName), nameof(UserName), nameof(Comment), nameof(Name), nameof(Description),
        nameof(Project), nameof(Instructions), nameof(IsActive), nameof(ProjectId)
    ];

    /// <summary>The project the plan is attached to (the id of a project, chosen by its name in the header of the window), or null.</summary>
    [ObservableProperty]
    private string? _projectId;

    private readonly KaonomiAssistantService _assistant;
    private CollectionPlanEntry _entry;
    private bool _isLoading;

    /// <summary>Loads server data into editable fields and tracks dirty state.</summary>
    public CollectionPlanEntryViewModel(CollectionPlanEntry entry, KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _entry = entry;
        Websites.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Summary));
            WebsiteChanged();
        };
        Load(entry);

        if (IsNew)
        {
            IsDirty = true;
            Status = "Unsaved new collection plan";
        }
    }

    /// <summary>Blank, inactive collection plan, created on the server (<c>PUT /collectionplan</c>) on first save.</summary>
    public static CollectionPlanEntryViewModel CreateNew(KaonomiAssistantService assistant) => new(new CollectionPlanEntry
    {
        Date = DateTimeOffset.Now,
        AppName = "KAONOMI.Chat",
        MachineName = Environment.MachineName,
        UserName = Environment.UserName
    }, assistant);

    /// <summary>Copy of the values currently displayed, without id, created on the server (<c>PUT /collectionplan</c>) on first save.</summary>
    public CollectionPlanEntryViewModel Clone()
    {
        var copy = ToEntry();
        copy.Id = null;
        copy.Date = DateTimeOffset.Now;
        copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? copy.Name : copy.Name + " (copy)";
        return new CollectionPlanEntryViewModel(copy, _assistant);
    }

    /// <summary>Server document id, or null before first save.</summary>
    public string? Id => _entry.Id;

    /// <summary>True when the row has not yet been persisted.</summary>
    public bool IsNew => _entry.Id is null;

    /// <summary>Human-readable id status for the detail panel.</summary>
    public string IdLabel => IsNew ? "New collection plan · id assigned on save" : $"Id: {_entry.Id}";

    /// <summary>Formatted local date for list display.</summary>
    public string DateLabel => _entry.Date?.ToLocalTime().ToString("g") ?? "Unknown date";

    /// <summary>Title of the list row.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? "(untitled)" : Name;

    /// <summary>Compact summary line for list rows: date, project and websites.</summary>
    public string Summary => Join(" · ", DateLabel, Project, Websites.Count == 0 ? null : $"{Websites.Count} website(s)");

    /// <summary>The websites of the plan, each with its URLs.</summary>
    public ObservableCollection<CollectionPlanWebsiteViewModel> Websites { get; } = [];

    /// <summary>Adds a blank website with one blank URL.</summary>
    public void AddWebsite()
    {
        var website = new CollectionPlanWebsiteViewModel(new WebsiteEntry(), WebsiteChanged);
        website.AddUrl();
        Websites.Add(website);
    }

    public void RemoveWebsite(CollectionPlanWebsiteViewModel website) => Websites.Remove(website);

    /// <summary>A website (or one of its URLs) changed: the plan has unsaved changes.</summary>
    private void WebsiteChanged()
    {
        if (_isLoading)
            return;
        IsDirty = true;
        IsError = false;
        Status = "Unsaved changes";
    }

    /// <summary>True when a non-empty description is set.</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    [ObservableProperty]
    private string _appName = string.Empty;

    [ObservableProperty]
    private string _machineName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string _description = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _project = string.Empty;

    [ObservableProperty]
    private string _instructions = string.Empty;

    /// <summary>True when the web scraper runs this plan.</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private bool _isDirty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

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

    /// <summary>Creates or updates the collection plan on the server.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var updated = ToEntry();
        var isNew = IsNew;
        IsError = false;
        Status = isNew ? "Creating…" : "Saving…";

        try
        {
            var result = isNew
                ? await _assistant.CreateCollectionPlanAsync(updated, cancellationToken)
                : await _assistant.UpdateCollectionPlanAsync(updated, cancellationToken);
            if (!result.IsSuccess)
            {
                IsError = true;
                Status = result.Message ?? "The Kaonomi server rejected the save.";
                ActivityLog.Error("CollectionPlan", $"{(isNew ? "Create" : "Update")} collection plan {_entry.Id ?? "(new)"} rejected: {Status}");
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
                ActivityLog.Error("CollectionPlan", "Collection plan created, but the server did not return an id");
            else
                ActivityLog.Info("CollectionPlan", $"Collection plan {(isNew ? "created" : "updated")}: {_entry.Id} ({Name})");
        }
        catch (OperationCanceledException)
        {
            Status = "Save cancelled";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("CollectionPlan", "Collection plan not saved: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("CollectionPlan", "Collection plan not saved", ex);
        }
    }

    /// <summary>Discards edits and reloads the last saved server snapshot.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Revert()
    {
        Load(_entry);
        IsDirty = IsNew;
        Status = "Changes discarded";
    }

    private void Load(CollectionPlanEntry entry)
    {
        _isLoading = true;
        try
        {
            AppName = entry.AppName ?? string.Empty;
            MachineName = entry.MachineName ?? string.Empty;
            UserName = entry.UserName ?? string.Empty;
            Comment = entry.Comment ?? string.Empty;
            Name = entry.Name ?? string.Empty;
            Description = entry.Description ?? string.Empty;
            Project = entry.Project ?? string.Empty;
            Instructions = entry.Instructions ?? string.Empty;
            IsActive = entry.IsActive;
            ProjectId = entry.ProjectId;
            Websites.Clear();
            foreach (var website in entry.Websites ?? [])
                Websites.Add(new CollectionPlanWebsiteViewModel(website, WebsiteChanged));
            IsDirty = false;
            IsError = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Copy of the values currently edited (also used as note context).</summary>
    internal CollectionPlanEntry ToEntry() => new()
    {
        Id = _entry.Id,
        Date = _entry.Date,
        IsDeleted = _entry.IsDeleted,
        AppName = AppName,
        ProjectId = ProjectId,
        MachineName = MachineName,
        UserName = UserName,
        Comment = Comment,
        Name = Name.Trim(),
        Description = Description,
        Project = Project.Trim(),
        Instructions = Instructions,
        IsActive = IsActive,
        Websites = Websites.Select(w => w.ToEntry()).ToList()
    };

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
