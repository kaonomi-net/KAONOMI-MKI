using System;
using System.Collections.ObjectModel;
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
/// Search, creation, cloning, edition and deletion of intels (<c>/intel</c>, <c>/nintel</c> in its own window). The
/// search looks in every text field; an empty search lists the newest intels (shown when the window opens).
/// </summary>
public partial class IntelWindowViewModel : ViewModelBase, INoteContextProvider, IProjectDocument
{
    /// <summary>The project of the intel selected (shown and chosen by its name in the header of the window).</summary>
    public string? ProjectId
    {
        get => SelectedResult?.ProjectId;
        set
        {
            if (SelectedResult is { } selected && selected.ProjectId != value)
            {
                selected.ProjectId = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasProjectDocument => SelectedResult is not null;

    partial void OnSelectedResultChanged(IntelEntryViewModel? value)
    {
        OnPropertyChanged(nameof(ProjectId));
        OnPropertyChanged(nameof(HasProjectDocument));
    }

    private const int PageSize = 50;

    private readonly KaonomiAssistantService _assistant;

    /// <summary>Design-time constructor using default server URL from settings.</summary>
    public IntelWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl), load: false)
    {
    }

    /// <summary>Uses the given assistant client for the intel API calls; lists the newest intels.</summary>
    public IntelWindowViewModel(KaonomiAssistantService assistant, bool load = true)
    {
        _assistant = assistant;
        _transfer = new DocumentTransfer<IntelEntry>
        {
            Plural = "intels",
            LogArea = "Intel",
            // The server leaves the deleted intels out of /intel/last.
            LoadAll = (size, ct) => _assistant.GetLastIntelsAsync(size, ct),
            Id = e => e.Id,
            Create = (e, ct) => _assistant.CreateIntelAsync(e, ct),
            Save = (e, ct) => _assistant.UpdateIntelAsync(e, ct),
            Confirm = (message, label) => ConfirmAsync(message, label),
            OpenTextFile = () => OpenTextFileAsync(),
            PickFolder = title => PickFolderAsync(title),
            Report = (status, isError) => { Status = status; IsError = isError; }
        };
        if (load)
            _ = SearchAsync(CancellationToken.None);
    }

    private readonly DocumentTransfer<IntelEntry> _transfer;

    /// <summary>Asks for a JSON file to import and returns its text, or null when cancelled; set by the view.</summary>
    public Func<Task<string?>> OpenTextFileAsync { get; set; } = () => Task.FromResult<string?>(null);

    /// <summary>Asks for a folder (argument: the dialog title), or null when cancelled; set by the view.</summary>
    public Func<string, Task<string?>> PickFolderAsync { get; set; } = _ => Task.FromResult<string?>(null);

    /// <summary>Exports every intel of the server (the deleted ones excepted) to a folder: one JSON file per intel.</summary>
    [RelayCommand]
    private Task ExportAllAsync(CancellationToken cancellationToken) => _transfer.ExportAllAsync(cancellationToken);

    /// <summary>Imports the intels of a JSON file (one intel or an array, as the seed files), then lists the latest ones.</summary>
    [RelayCommand]
    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        if (await _transfer.ImportFileAsync(cancellationToken))
            await ReloadKeepingStatusAsync();
    }

    /// <summary>Imports the intels of a folder (an "Export all", one file per intel), then lists the latest ones.</summary>
    [RelayCommand]
    private async Task ImportFolderAsync(CancellationToken cancellationToken)
    {
        if (await _transfer.ImportFolderAsync(cancellationToken))
            await ReloadKeepingStatusAsync();
    }

    /// <summary>Runs the current search again to show the imported intels, keeping the import status.</summary>
    private async Task ReloadKeepingStatusAsync()
    {
        var (status, isError) = (Status, IsError);
        await SearchAsync(CancellationToken.None);
        (Status, IsError) = (status, isError);
    }

    /// <summary>Search hits and locally created rows bound to the results list.</summary>
    public ObservableCollection<IntelEntryViewModel> Results { get; } = [];

    /// <summary>True when at least one result row is present.</summary>
    public bool HasResults => Results.Count > 0;

    /// <summary>Asks the user to confirm (message, confirm label); set by the view.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand), nameof(DeleteCommand))]
    private IntelEntryViewModel? _selectedResult;

    [ObservableProperty]
    private string _status = "Loading the latest intels…";

    [ObservableProperty]
    private bool _isError;

    /// <summary>Inserts a new unsaved intel at the top of the list.</summary>
    [RelayCommand]
    private void New()
    {
        var entry = IntelEntryViewModel.CreateNew(_assistant);
        Results.Insert(0, entry);
        SelectedResult = entry;
        OnPropertyChanged(nameof(HasResults));
        ActivityLog.Info("Intel", "New intel started (unsaved)");
    }

    private bool CanClone() => SelectedResult is not null;

    /// <summary>Duplicates the selected intel as a new unsaved one.</summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private void Clone()
    {
        var source = SelectedResult!;
        var clone = source.Clone();
        Results.Insert(Results.IndexOf(source) + 1, clone);
        SelectedResult = clone;
        clone.Status = "Unsaved clone";
        OnPropertyChanged(nameof(HasResults));
        ActivityLog.Info("Intel", $"Intel cloned from {source.Id ?? "(new)"} (unsaved)");
    }

    private bool CanDelete() => SelectedResult is not null;

    /// <summary>
    /// Deletes the selected intel after a confirmation: on the server it is only marked deleted (<c>isDeleted</c>), and
    /// it leaves the list; a new intel not saved yet is just removed from the list.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        var entry = SelectedResult!;
        if (!await ConfirmAsync($"Delete the intel \"{entry.Title}\"?{(entry.IsNew ? "" : " It is only marked deleted on the server.")}", "Delete"))
            return;

        if (!entry.IsNew)
        {
            try
            {
                var result = await _assistant.DeleteIntelAsync(entry.Id!, cancellationToken);
                if (!result.IsSuccess)
                {
                    IsError = true;
                    Status = result.Message ?? "The Kaonomi server rejected the deletion.";
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                IsError = true;
                Status = $"Intel not deleted: {ex.Message}";
                ActivityLog.Error("Intel", $"Intel {entry.Id} not deleted", ex);
                return;
            }
        }

        var index = Results.IndexOf(entry);
        Results.Remove(entry);
        SelectedResult = Results.Count == 0 ? null : Results[Math.Min(index, Results.Count - 1)];
        OnPropertyChanged(nameof(HasResults));
        IsError = false;
        Status = $"Intel \"{entry.Title}\" deleted";
        ActivityLog.Info("Intel", $"Intel deleted (isDeleted = true): {entry.Id ?? "(unsaved)"} ({entry.Title})");
    }

    /// <summary>
    /// Searches the intels matching <see cref="Term"/> in all their text fields; an empty term (or <c>*</c>) lists
    /// the newest intels.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var term = Term.Trim();
        var isLast = term.Length == 0 || term == "*";
        IsError = false;
        Status = isLast ? "Loading the latest intels…" : $"Searching for \"{term}\"…";

        try
        {
            var entries = isLast
                ? await _assistant.GetLastIntelsAsync(PageSize, cancellationToken)
                : await _assistant.SearchIntelsAsync(term, PageSize, cancellationToken);

            Results.Clear();
            foreach (var entry in entries)
                Results.Add(new IntelEntryViewModel(entry, _assistant));

            SelectedResult = Results.FirstOrDefault();
            Status = isLast
                ? Results.Count == 0 ? "No intel yet: New creates one" : $"{Results.Count} latest intel(s)"
                : Results.Count == 0 ? $"No results for \"{term}\"" : $"{Results.Count} result(s) for \"{term}\"";
            ActivityLog.Info("Intel", isLast ? $"Latest intels: {Results.Count}" : $"Search \"{term}\": {Results.Count} result(s)");
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Intel", $"Search \"{term}\" failed: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Intel", $"Search \"{term}\" failed", ex);
        }
        finally
        {
            OnPropertyChanged(nameof(HasResults));
        }
    }

    /// <inheritdoc />
    public string NoteWindowName => "Intel";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        searchTerm = Term,
        resultCount = Results.Count,
        selected = SelectedResult?.ToEntry()
    };
}
