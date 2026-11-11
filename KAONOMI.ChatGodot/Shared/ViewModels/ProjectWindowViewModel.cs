using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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

/// <summary>One goal of a project, edited in its own box.</summary>
public partial class ProjectGoalViewModel : ViewModelBase
{
    public ProjectGoalViewModel(string text = "") => _text = text;

    [ObservableProperty]
    private string _text;
}

/// <summary>
/// Editable copy of a project (<c>Project</c> on the server side): its name, its description, its goals, its tags and
/// a comment; the id and the date of the original document are kept on save.
/// </summary>
public partial class ProjectEntryViewModel : ViewModelBase
{
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly HashSet<string> EditableProperties =
        [nameof(Name), nameof(Description), nameof(Comment), nameof(Tags), nameof(StartDate), nameof(EndDate)];

    private readonly KaonomiAssistantService _assistant;
    private ProjectEntry _entry;
    private bool _isLoading;

    public ProjectEntryViewModel(ProjectEntry entry, KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _entry = entry;
        Goals.CollectionChanged += OnGoalsChanged;
        Load(entry);
        if (IsNew)
        {
            IsDirty = true;
            Status = "Unsaved new project";
        }
    }

    /// <summary>Blank project, created on the server (<c>PUT /project</c>) on first save.</summary>
    public static ProjectEntryViewModel CreateNew(KaonomiAssistantService assistant) => new(new ProjectEntry
    {
        Date = DateTimeOffset.Now,
        StartDate = new DateTimeOffset(DateTime.Today),
        AppName = "KAONOMI.Chat",
        MachineName = Environment.MachineName,
        UserName = Environment.UserName
    }, assistant);

    /// <summary>Server document id, or null before the first save.</summary>
    public string? Id => _entry.Id;

    public bool IsNew => _entry.Id is null;

    public string IdLabel => IsNew ? "New project · id assigned on save" : $"Id: {_entry.Id}";

    public string DateLabel => _entry.Date?.ToLocalTime().ToString("g") ?? "Unknown date";

    /// <summary>The line of the list under the name: date, user, how many goals.</summary>
    public string Summary => string.Join(" · ", new[] { PeriodLabel, _entry.UserName, GoalsLabel }.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>From its start to its end, as typed: « 2026-11-11 → 2026-12-31 », or « from 2026-11-11 » while it has no end.</summary>
    public string PeriodLabel => string.IsNullOrWhiteSpace(EndDate) ? $"from {StartDate.Trim()}" : $"{StartDate.Trim()} → {EndDate.Trim()}";

    public string GoalsLabel => $"{GoalTexts.Count} goal(s)";

    public string Headline => string.IsNullOrWhiteSpace(Name) ? "(no name)" : Name.Trim();

    public string DescriptionPreview => (Description ?? string.Empty).Replace('\n', ' ').Trim();

    public bool HasDescription => DescriptionPreview.Length > 0;

    /// <summary>The goals (one box each; the empty ones are not saved).</summary>
    public ObservableCollection<ProjectGoalViewModel> Goals { get; } = [];

    /// <summary>The goals that are filled, in their order.</summary>
    public IReadOnlyList<string> GoalTexts => Goals.Select(g => g.Text.Trim()).Where(t => t.Length > 0).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Headline))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DescriptionPreview), nameof(HasDescription))]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _comment = string.Empty;

    /// <summary>The day the project starts, as <c>yyyy-MM-dd</c>: today for a new project.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodLabel), nameof(Summary))]
    private string _startDate = DateTime.Today.ToString(DateFormat);

    /// <summary>The day the project ends, as <c>yyyy-MM-dd</c>; empty: no end.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodLabel), nameof(Summary))]
    private string _endDate = string.Empty;

    /// <summary>A day typed as <c>yyyy-MM-dd</c> (or as the dates are written here), at midnight; null when it is not one.</summary>
    private static DateTimeOffset? ParseDay(string text) =>
        DateTime.TryParseExact(text.Trim(), DateFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day)
        || DateTime.TryParse(text.Trim(), System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out day)
            ? new DateTimeOffset(day.Date)
            : null;

    /// <summary>The tags, one per line; left empty, the server finds the declared ones in the texts on save.</summary>
    [ObservableProperty]
    private string _tags = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSaving;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>Raised once the project is saved (the list shows it at its place).</summary>
    public event Action<ProjectEntryViewModel>? Saved;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_isLoading && e.PropertyName is { } name && EditableProperties.Contains(name))
            MarkDirty();
    }

    private void MarkDirty()
    {
        IsDirty = true;
        IsError = false;
        Status = IsNew ? "Unsaved new project" : "Unsaved changes";
    }

    private void OnGoalsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var goal in e.OldItems?.OfType<ProjectGoalViewModel>() ?? [])
            goal.PropertyChanged -= OnGoalChanged;
        foreach (var goal in e.NewItems?.OfType<ProjectGoalViewModel>() ?? [])
            goal.PropertyChanged += OnGoalChanged;
        GoalsChanged();
    }

    private void OnGoalChanged(object? sender, PropertyChangedEventArgs e) => GoalsChanged();

    private void GoalsChanged()
    {
        OnPropertyChanged(nameof(GoalsLabel));
        OnPropertyChanged(nameof(Summary));
        if (!_isLoading)
            MarkDirty();
    }

    private void Load(ProjectEntry entry)
    {
        _isLoading = true;
        try
        {
            _entry = entry;
            Name = entry.Name ?? string.Empty;
            Description = entry.Description ?? string.Empty;
            Comment = entry.Comment ?? string.Empty;
            Tags = string.Join("\n", entry.Tags ?? []);
            StartDate = (entry.StartDate?.ToLocalTime().DateTime ?? DateTime.Today).ToString(DateFormat);
            EndDate = entry.EndDate?.ToLocalTime().DateTime.ToString(DateFormat) ?? string.Empty;
            foreach (var goal in Goals)
                goal.PropertyChanged -= OnGoalChanged;
            Goals.Clear();
            foreach (var goal in entry.Goals ?? [])
                Goals.Add(new ProjectGoalViewModel(goal));
            // Always a box to type the first goal in.
            if (Goals.Count == 0)
                Goals.Add(new ProjectGoalViewModel());
        }
        finally
        {
            _isLoading = false;
        }
        IsDirty = false;
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(IdLabel));
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>The project as it is typed now.</summary>
    public ProjectEntry ToEntry() => new()
    {
        Id = _entry.Id,
        Date = _entry.Date,
        AppName = _entry.AppName ?? "KAONOMI.Chat",
        MachineName = _entry.MachineName ?? Environment.MachineName,
        UserName = _entry.UserName ?? Environment.UserName,
        IsDeleted = _entry.IsDeleted,
        Name = Name.Trim(),
        Description = Description.Trim(),
        Comment = Comment.Trim(),
        Goals = GoalTexts.ToList(),
        StartDate = ParseDay(StartDate) ?? new DateTimeOffset(DateTime.Today),
        EndDate = string.IsNullOrWhiteSpace(EndDate) ? null : ParseDay(EndDate),
        Tags = Tags.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList()
    };

    [RelayCommand]
    private void AddGoal() => Goals.Add(new ProjectGoalViewModel());

    [RelayCommand]
    private void RemoveGoal(ProjectGoalViewModel? goal)
    {
        if (goal is null)
            return;
        Goals.Remove(goal);
        if (Goals.Count == 0)
            Goals.Add(new ProjectGoalViewModel());
    }

    /// <summary>Gives the changes up: the project as it was saved last (a new one: empty again).</summary>
    [RelayCommand]
    private void Revert()
    {
        Load(_entry);
        IsError = false;
        if (IsNew)
        {
            IsDirty = true;
            Status = "Unsaved new project";
        }
        else
            Status = "Changes discarded";
    }

    private bool CanSave() => !IsSaving;

    /// <summary>
    /// Saves the project: <c>PUT /project</c> the first time, <c>POST /project/update</c> afterwards; then it is read
    /// back from the server (its id, and the tags the server found when none was typed).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            IsError = true;
            Status = "A project needs a name";
            return;
        }
        if (ParseDay(StartDate) is not { } start)
        {
            IsError = true;
            Status = $"The start date is not a date ({DateFormat}, for example {DateTime.Today.ToString(DateFormat)})";
            return;
        }
        if (!string.IsNullOrWhiteSpace(EndDate))
        {
            if (ParseDay(EndDate) is not { } end)
            {
                IsError = true;
                Status = $"The end date is not a date ({DateFormat}); leave it empty for a project without an end";
                return;
            }
            if (end < start)
            {
                IsError = true;
                Status = "The end of the project cannot be before its start";
                return;
            }
        }
        IsSaving = true;
        IsError = false;
        Status = "Saving…";
        try
        {
            var entry = ToEntry();
            var result = IsNew
                ? await _assistant.CreateProjectAsync(entry, cancellationToken)
                : await _assistant.UpdateProjectAsync(entry, cancellationToken);
            if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.ReturnedId) || result.ReturnedId == "-1")
            {
                IsError = true;
                Status = $"Not saved: {result.Message}";
                return;
            }
            entry.Id = result.ReturnedId;
            Load(await _assistant.GetProjectAsync(result.ReturnedId, cancellationToken) ?? entry);
            Status = $"Saved {DateTime.Now:HH:mm:ss}";
            ActivityLog.Info("Project", $"Project « {Name} » saved ({_entry.Id})");
            Saved?.Invoke(this);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            IsError = true;
            Status = $"Not saved: {ex.Message}";
            ActivityLog.Error("Project", "The project could not be saved", ex);
        }
        finally
        {
            IsSaving = false;
        }
    }
}

/// <summary>
/// /project: the projects of the server (a name, a description, goals, tags). A search on the left (empty: the newest
/// ones) with the projects found; the one selected is edited on the right. NEW starts an empty one, SAVE writes it
/// (<c>PUT /project</c>, then <c>POST /project/update</c>), DELETE marks it deleted on the server after a
/// confirmation (<c>DELETE /project/{id}</c>): it is never erased, only left out of the lists.
/// </summary>
public partial class ProjectWindowViewModel : ViewModelBase
{
    private const int PageSize = 100;
    private readonly KaonomiAssistantService? _assistant;

    /// <summary>Design-time constructor: no server.</summary>
    public ProjectWindowViewModel() : this(null)
    {
    }

    public ProjectWindowViewModel(KaonomiAssistantService? assistant)
    {
        _assistant = assistant;
        if (assistant is null)
        {
            Status = "No Kaonomi server: the projects cannot be loaded";
            IsError = true;
        }
        else
            SearchCommand.Execute(null);
    }

    /// <summary>The projects found, the newest first; a new one not saved yet stays at the top.</summary>
    public ObservableCollection<ProjectEntryViewModel> Results { get; } = [];

    /// <summary>Asks a confirmation in a popup (title, question, the word of the button); set by the view.</summary>
    public Func<string, string, string, Task<bool>> ConfirmAsync { get; set; } = (_, _, _) => Task.FromResult(true);

    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private ProjectEntryViewModel? _selectedResult;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    private void Report(string status, bool isError)
    {
        Status = status;
        IsError = isError;
    }

    private ProjectEntryViewModel Wrap(ProjectEntry entry)
    {
        var item = new ProjectEntryViewModel(entry, _assistant!);
        item.Saved += saved =>
        {
            Report($"Project « {saved.Headline} » saved", false);
            // The selector of the windows of documents shows the project under its name of now.
            _ = ProjectCatalog.RefreshAsync();
        };
        return item;
    }

    /// <summary>Searches the projects (name, description, goals, tags, comment); empty: the newest ones.</summary>
    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        if (_assistant is null)
            return;
        var term = Term.Trim();
        Report("Searching…", false);
        try
        {
            var found = await _assistant.SearchProjectsAsync(term.Length == 0 ? "*" : term, PageSize, cancellationToken);
            var selected = SelectedResult?.Id;
            // What is being typed and not saved is not lost by a search.
            var unsaved = Results.Where(r => r.IsNew || r.IsDirty).ToList();
            Results.Clear();
            foreach (var item in unsaved)
                Results.Add(item);
            foreach (var entry in found.Where(e => unsaved.All(u => u.Id != e.Id)))
                Results.Add(Wrap(entry));
            SelectedResult = Results.FirstOrDefault(r => r.Id is not null && r.Id == selected) ?? SelectedResult switch
            {
                { } current when Results.Contains(current) => current,
                _ => Results.FirstOrDefault()
            };
            Report(found.Count == 0 ? (term.Length == 0 ? "No project yet: NEW creates the first one" : $"No project for « {term} »")
                : $"{found.Count} project(s)" + (term.Length == 0 ? string.Empty : $" for « {term} »"), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Report($"The projects could not be loaded: {ex.Message}", true);
            ActivityLog.Error("Project", "The projects could not be loaded", ex);
        }
    }

    /// <summary>Starts a new project, selected at the top of the list.</summary>
    [RelayCommand]
    private void New()
    {
        if (_assistant is null)
            return;
        var item = Wrap(new ProjectEntry
        {
            Date = DateTimeOffset.Now,
            StartDate = new DateTimeOffset(DateTime.Today),
            AppName = "KAONOMI.Chat",
            MachineName = Environment.MachineName,
            UserName = Environment.UserName
        });
        Results.Insert(0, item);
        SelectedResult = item;
        Report("New project: type its name, then SAVE", false);
    }

    private bool CanDelete() => SelectedResult is not null;

    /// <summary>
    /// Deletes the project selected after a confirmation: one never saved just leaves the list; a saved one is marked
    /// deleted on the server (it is not erased) and leaves the list.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (_assistant is null || SelectedResult is not { } item)
            return;
        if (!await ConfirmAsync("Delete the project", $"Delete the project « {item.Headline} »?"
                + (item.IsNew ? " It was never saved." : " It is marked deleted on the server and leaves the lists."), "DELETE"))
            return;
        try
        {
            if (!item.IsNew)
            {
                var result = await _assistant.DeleteProjectAsync(item.Id!, cancellationToken);
                if (!result.IsSuccess)
                {
                    Report($"Not deleted: {result.Message}", true);
                    return;
                }
                ActivityLog.Info("Project", $"Project « {item.Headline} » deleted ({item.Id})");
                _ = ProjectCatalog.RefreshAsync();
            }
            var index = Results.IndexOf(item);
            Results.Remove(item);
            SelectedResult = Results.Count == 0 ? null : Results[Math.Clamp(index, 0, Results.Count - 1)];
            Report($"Project « {item.Headline} » deleted", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Report($"Not deleted: {ex.Message}", true);
            ActivityLog.Error("Project", "The project could not be deleted", ex);
        }
    }
}
