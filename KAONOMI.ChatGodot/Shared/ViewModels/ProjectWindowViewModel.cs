using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// Kanban of the tasks (<c>/project</c>, <c>/nproject</c> in its own window): one column per status (todo, coding,
/// test, check, deploy, archived), each task in a card with its title and its type (bug, change, agent, infra). The tasks
/// still in the backlog (or with another status) get a BACKLOG column first, shown only when there are some.
/// </summary>
public partial class ProjectWindowViewModel : ViewModelBase, INoteContextProvider
{
    /// <summary>Most tasks loaded (the newest ones).</summary>
    private const int MaxTasks = 1000;

    /// <summary>The statuses of the board, in their order.</summary>
    public static readonly string[] Statuses = ["todo", "coding", "test", "check", "deploy", "archived"];

    private const string Backlog = "backlog";

    private readonly KaonomiAssistantService? _assistant;

    /// <summary>For the designer.</summary>
    public ProjectWindowViewModel() : this(null)
    {
    }

    /// <summary>Loads the tasks with <paramref name="assistant"/>.</summary>
    public ProjectWindowViewModel(KaonomiAssistantService? assistant)
    {
        _assistant = assistant;
        Show([]);
        if (assistant is not null)
            _ = RefreshAsync(CancellationToken.None);
    }

    /// <summary>Columns of the board.</summary>
    public ObservableCollection<KanbanColumnViewModel> Columns { get; } = [];

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>Loads the tasks again (the newest ones, without the deleted ones).</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_assistant is null)
            return;
        IsError = false;
        Status = "Loading the tasks…";
        try
        {
            var tasks = await _assistant.GetLastTasksAsync(MaxTasks, cancellationToken);
            Show(tasks);
            Status = tasks.Count == 0
                ? "No task yet."
                : $"{tasks.Count} task(s){(tasks.Count >= MaxTasks ? $" (the {MaxTasks} newest)" : "")} · loaded at {DateTime.Now:HH:mm:ss}";
            ActivityLog.Info("Project", $"Kanban loaded: {tasks.Count} task(s)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            IsError = true;
            Status = $"Tasks not loaded: {ex.Message}";
            ActivityLog.Error("Project", "Kanban not loaded (/task/last)", ex);
        }
    }

    /// <summary>
    /// Moves <paramref name="card"/> to <paramref name="target"/>: the card changes column at once, and the task gets the
    /// status of the column (<c>POST /task/update</c>); when the server refuses, the card goes back to its column.
    /// </summary>
    public async Task MoveAsync(KanbanCardViewModel card, KanbanColumnViewModel target)
    {
        var source = Columns.FirstOrDefault(c => c.Cards.Contains(card));
        if (_assistant is null || source is null || ReferenceEquals(source, target))
            return;

        var previous = card.Task.Status;
        source.Cards.Remove(card);
        target.Cards.Insert(0, card);
        card.Task.Status = target.Status;
        IsError = false;
        Status = $"Moving \"{card.Title}\" to {target.Title}…";
        try
        {
            var result = await _assistant.UpdateTaskAsync(card.Task);
            if (!result.Success || result.ReturnedId == "-1")
                throw new HttpRequestException(result.Message);
            Status = $"\"{card.Title}\" moved from {source.Title} to {target.Title}.";
            ActivityLog.Info("Project", $"Task moved: {card.Title} ({card.Task.Id}) {previous} -> {target.Status}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            card.Task.Status = previous;
            target.Cards.Remove(card);
            source.Cards.Insert(0, card);
            IsError = true;
            Status = $"\"{card.Title}\" not moved: {ex.Message}";
            ActivityLog.Error("Project", $"Task {card.Task.Id} not moved to {target.Status}", ex);
        }
    }

    /// <summary>Editor of the task of <paramref name="card"/> (null without a server).</summary>
    public TaskEditorViewModel? Edit(KanbanCardViewModel card) =>
        _assistant is null ? null : new TaskEditorViewModel(card.Task, _assistant);

    /// <summary>Reloads the board after a task was saved or deleted (Elasticsearch shows it to the searches about a second later).</summary>
    public async Task ReloadAfterChangeAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await RefreshAsync(CancellationToken.None);
    }

    // ---- New task ----

    /// <summary>Types offered for a new task.</summary>
    public IReadOnlyList<string> Types { get; } = ["bug", "change", "agent", "infra"];

    /// <summary>Statuses offered for a new task (the backlog, then the columns of the board).</summary>
    public IReadOnlyList<string> NewStatuses { get; } = [Backlog, .. Statuses];

    /// <summary>True while the form of a new task is shown.</summary>
    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveNewCommand))]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newDescription = string.Empty;

    [ObservableProperty]
    private string _newType = "change";

    [ObservableProperty]
    private string _newTaskStatus = "todo";

    [ObservableProperty]
    private DateTime? _newStart;

    [ObservableProperty]
    private DateTime? _newEnd;

    [ObservableProperty]
    private string _newSecurity = string.Empty;

    [ObservableProperty]
    private string _newLegal = string.Empty;

    [ObservableProperty]
    private string _newData = string.Empty;

    [ObservableProperty]
    private string _newComment = string.Empty;

    /// <summary>Shows an empty form for a new task (type change, status todo, starting today).</summary>
    [RelayCommand]
    private void NewTask()
    {
        NewName = NewDescription = NewSecurity = NewLegal = NewData = NewComment = string.Empty;
        NewType = "change";
        NewTaskStatus = "todo";
        NewStart = DateTime.Today;
        NewEnd = null;
        IsCreating = true;
    }

    /// <summary>Closes the form without saving.</summary>
    [RelayCommand]
    private void CancelNew() => IsCreating = false;

    private bool CanSaveNew() => _assistant is not null && !string.IsNullOrWhiteSpace(NewName);

    /// <summary>Creates the task (<c>PUT /task</c>), closes the form and reloads the board.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveNew))]
    private async Task SaveNewAsync(CancellationToken cancellationToken)
    {
        if (NewEnd is { } end && NewStart is { } start && end < start)
        {
            IsError = true;
            Status = "The end date is before the start date.";
            return;
        }

        var task = new TaskEntry
        {
            AppName = "KAONOMI.Chat",
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            Name = NewName.Trim(),
            Description = NewDescription.Trim(),
            Type = NewType,
            Status = NewTaskStatus,
            StartTime = NewStart is { } s ? DateTime.SpecifyKind(s.Date, DateTimeKind.Utc) : null,
            EndTime = NewEnd is { } e ? DateTime.SpecifyKind(e.Date, DateTimeKind.Utc) : null,
            SecurityConstraints = NewSecurity.Trim(),
            LegalConstraints = NewLegal.Trim(),
            DataConstraints = NewData.Trim(),
            Comment = NewComment.Trim()
        };
        IsError = false;
        Status = $"Creating \"{task.Name}\"…";
        try
        {
            var result = await _assistant!.CreateTaskAsync(task, cancellationToken);
            if (!result.Success || result.ReturnedId == "-1")
            {
                IsError = true;
                Status = $"Task not created: {result.Message}";
                return;
            }
            ActivityLog.Info("Project", $"Task created: {task.Name} ({task.Type}, {task.Status}, id {result.ReturnedId})");
            IsCreating = false;
            await ReloadAfterChangeAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            IsError = true;
            Status = $"Task not created: {ex.Message}";
            ActivityLog.Error("Project", "Task not created (PUT /task)", ex);
        }
    }

    /// <summary>Puts <paramref name="tasks"/> in the columns of their status (newest first in each column).</summary>
    public void Show(IEnumerable<TaskEntry> tasks)
    {
        var list = tasks.Where(t => !t.IsDeleted).ToList();
        var byStatus = list.ToLookup(t => Statuses.Contains(t.Status?.Trim().ToLowerInvariant()) ? t.Status!.Trim().ToLowerInvariant() : Backlog);

        Columns.Clear();
        if (byStatus[Backlog].Any())
            Columns.Add(new KanbanColumnViewModel(Backlog, byStatus[Backlog]));
        foreach (var status in Statuses)
            Columns.Add(new KanbanColumnViewModel(status, byStatus[status]));
    }

    /// <inheritdoc />
    public string NoteWindowName => "Project";

    /// <inheritdoc />
    public object? GetNoteContext() => new { columns = Columns.Select(c => new { c.Title, c.Count }) };
}

/// <summary>A column of the kanban: a status and its tasks; cards are dropped on it to give them its status.</summary>
public sealed partial class KanbanColumnViewModel : ObservableObject
{
    /// <summary>The column of <paramref name="status"/> with <paramref name="tasks"/>, newest first.</summary>
    public KanbanColumnViewModel(string status, IEnumerable<TaskEntry> tasks)
    {
        Status = status;
        Cards = new(tasks.OrderByDescending(t => t.Date ?? DateTimeOffset.MinValue).Select(t => new KanbanCardViewModel(t)));
        Cards.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(IsEmpty));
        };
        Accent = new SolidColorBrush(Color.Parse(status switch
        {
            "todo" => "#8B8D98",
            "coding" => "#3E63DD",
            "test" => "#F76B15",
            "check" => "#8E4EC6",
            "deploy" => "#30A46C",
            "archived" => "#5C6370",
            _ => "#B0B4BA"
        }));
    }

    /// <summary>The status of the column (lower case, as stored in the tasks).</summary>
    public string Status { get; }

    /// <summary>The status, in capitals.</summary>
    public string Title => Status.ToUpperInvariant();

    /// <summary>The cards of the column.</summary>
    public ObservableCollection<KanbanCardViewModel> Cards { get; }

    /// <summary>Number of tasks of the column.</summary>
    public int Count => Cards.Count;

    /// <summary>True when the column has no task.</summary>
    public bool IsEmpty => Cards.Count == 0;

    /// <summary>Color of the top line of the column.</summary>
    public IBrush Accent { get; }

    /// <summary>True while a card is dragged over the column (it is highlighted).</summary>
    [ObservableProperty]
    private bool _isDropTarget;
}

/// <summary>A card of the kanban: the title and the type of a task.</summary>
public sealed class KanbanCardViewModel(TaskEntry task)
{
    /// <summary>The task.</summary>
    public TaskEntry Task { get; } = task;

    /// <summary>Title of the task.</summary>
    public string Title => string.IsNullOrWhiteSpace(Task.Name) ? "(untitled)" : Task.Name.Trim();

    /// <summary>Type of the task (bug, change, agent, infra), in capitals; "—" when it has none.</summary>
    public string Type => string.IsNullOrWhiteSpace(Task.Type) ? "—" : Task.Type.Trim().ToUpperInvariant();

    /// <summary>Description, shown as a tooltip.</summary>
    public string? Description => string.IsNullOrWhiteSpace(Task.Description) ? null : Task.Description.Trim();

    /// <summary>Color of the type badge.</summary>
    public IBrush TypeBrush { get; } = new SolidColorBrush(Color.Parse(task.Type?.Trim().ToLowerInvariant() switch
    {
        "bug" => "#E5484D",
        "change" => "#3E63DD",
        "agent" => "#8E4EC6",
        "infra" => "#F76B15",
        _ => "#8B8D98"
    }));
}
