using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// Editor of a task, opened by a double-click on its card in the kanban (<c>/kanban</c>): Save writes it
/// (<c>POST /task/update</c>), Delete marks it deleted (<c>DELETE /task/{id}</c>), both close the popup.
/// </summary>
public partial class TaskEditorViewModel : ViewModelBase, INoteContextProvider, IProjectDocument
{
    /// <summary>The project of the task (shown and chosen by its name in the header of the window); written with the task by Save.</summary>
    [ObservableProperty]
    private string? _projectId;

    public bool HasProjectDocument => true;

    /// <inheritdoc />
    public string NoteWindowName => "Task";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        id = Id,
        name = Name,
        description = Description,
        type = Type,
        status = TaskStatus,
        start = Start,
        end = End
    };

    private readonly KaonomiAssistantService? _assistant;
    private readonly TaskEntry _task;

    /// <summary>For the designer.</summary>
    public TaskEditorViewModel() : this(new TaskEntry { Name = "Task", Type = "change", Status = "todo" }, null)
    {
    }

    /// <summary>Edits a copy of the fields of <paramref name="task"/>; nothing changes until Save.</summary>
    public TaskEditorViewModel(TaskEntry task, KaonomiAssistantService? assistant)
    {
        _task = task;
        _assistant = assistant;
        _name = task.Name ?? string.Empty;
        _description = task.Description ?? string.Empty;
        _type = string.IsNullOrWhiteSpace(task.Type) ? "change" : task.Type.Trim().ToLowerInvariant();
        _taskStatus = string.IsNullOrWhiteSpace(task.Status) ? "backlog" : task.Status.Trim().ToLowerInvariant();
        _start = DayOf(task.StartTime);
        _end = DayOf(task.EndTime);
        _security = task.SecurityConstraints ?? string.Empty;
        _legal = task.LegalConstraints ?? string.Empty;
        _data = task.DataConstraints ?? string.Empty;
        _comment = task.Comment ?? string.Empty;
        _projectId = task.ProjectId;
    }

    /// <summary>Asks the user to confirm (message, confirm label); set by the view.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    /// <summary>Closes the popup; true when the task was saved or deleted (the board is then reloaded).</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Types offered (the type of the task first when it is another one).</summary>
    public IReadOnlyList<string> Types => Union(["bug", "change", "agent", "infra"], Type);

    /// <summary>Statuses offered (the status of the task first when it is another one).</summary>
    public IReadOnlyList<string> Statuses => Union(["backlog", .. KanbanWindowViewModel.Statuses], TaskStatus);

    /// <summary>When the task was created (who and where are kept but never shown).</summary>
    public string Origin => _task.Date?.LocalDateTime.ToString("g") ?? string.Empty;

    /// <summary>Id of the task.</summary>
    public string Id => _task.Id ?? string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private string _type;

    [ObservableProperty]
    private string _taskStatus;

    [ObservableProperty]
    private DateTime? _start;

    [ObservableProperty]
    private DateTime? _end;

    [ObservableProperty]
    private string _security;

    [ObservableProperty]
    private string _legal;

    [ObservableProperty]
    private string _data;

    [ObservableProperty]
    private string _comment;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    private bool CanSave() => _assistant is not null && !string.IsNullOrWhiteSpace(Name);

    /// <summary>Saves the task (<c>POST /task/update</c>) and closes the popup.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (End is { } end && Start is { } start && end < start)
        {
            Fail("The end date is before the start date.");
            return;
        }

        _task.Name = Name.Trim();
        _task.Description = Description.Trim();
        _task.Type = Type;
        _task.Status = TaskStatus;
        _task.StartTime = Utc(Start);
        _task.EndTime = Utc(End);
        _task.SecurityConstraints = Security.Trim();
        _task.LegalConstraints = Legal.Trim();
        _task.DataConstraints = Data.Trim();
        _task.Comment = Comment.Trim();
        _task.ProjectId = ProjectId;
        IsError = false;
        Status = "Saving…";
        try
        {
            var result = await _assistant!.UpdateTaskAsync(_task, cancellationToken);
            if (!result.Success || result.ReturnedId == "-1")
            {
                Fail($"Not saved: {result.Message}");
                return;
            }
            ActivityLog.Info("Project", $"Task updated: {_task.Name} ({_task.Type}, {_task.Status}, id {_task.Id})");
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Fail($"Not saved: {ex.Message}");
            ActivityLog.Error("Project", $"Task {_task.Id} not updated (POST /task/update)", ex);
        }
    }

    /// <summary>Marks the task deleted (<c>isDeleted = true</c>, after a confirmation) and closes the popup.</summary>
    [RelayCommand]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (_assistant is null || string.IsNullOrEmpty(_task.Id))
            return;
        if (!await ConfirmAsync($"Delete the task \"{_task.Name}\"? It is only marked deleted and can be restored.", "Delete"))
            return;
        try
        {
            var result = await _assistant.DeleteTaskAsync(_task.Id, cancellationToken);
            if (!result.Success || result.ReturnedId == "-1")
            {
                Fail($"Not deleted: {result.Message}");
                return;
            }
            ActivityLog.Info("Project", $"Task deleted (isDeleted = true): {_task.Name} (id {_task.Id})");
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Fail($"Not deleted: {ex.Message}");
            ActivityLog.Error("Project", $"Task {_task.Id} not deleted (DELETE /task)", ex);
        }
    }

    /// <summary>Closes the popup without saving.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    private void Fail(string message)
    {
        IsError = true;
        Status = message;
    }

    private static IReadOnlyList<string> Union(IReadOnlyList<string> values, string current) =>
        values.Contains(current) ? values : [current, .. values];

    /// <summary>The day of <paramref name="value"/> in local time; null for none (or the default date of the server).</summary>
    private static DateTime? DayOf(DateTime? value) =>
        value is { } date && date.Year > 1
            // A day stored at midnight UTC (how the days are saved) is that day everywhere; a date with a time is a
            // moment, read in local time.
            ? (date.Kind == DateTimeKind.Utc && date.TimeOfDay == TimeSpan.Zero ? date.Date : (date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date).Date)
            : null;

    /// <summary>The day, as midnight UTC (how the tasks store their dates).</summary>
    private static DateTime? Utc(DateTime? day) =>
        day is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null;
}
