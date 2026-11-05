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

/// <summary>List, search, creation and edition of ideas (<c>/idea</c>, <c>/nidea</c> in its own window).</summary>
public partial class IdeaWindowViewModel : ViewModelBase, INoteContextProvider
{
    private const int PageSize = 100;

    private readonly KaonomiAssistantService _assistant;

    /// <summary>Design-time constructor using default server URL from settings.</summary>
    public IdeaWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    /// <summary>Uses the given assistant client for the idea API calls.</summary>
    public IdeaWindowViewModel(KaonomiAssistantService assistant)
    {
        _assistant = assistant;
    }

    /// <summary>Ideas of the last search (or the newest ones) and locally created rows.</summary>
    public ObservableCollection<IdeaEntryViewModel> Results { get; } = [];

    /// <summary>Categories of the listed ideas, offered by the category box.</summary>
    public ObservableCollection<string> Categories { get; } = [];

    /// <summary>True when at least one idea is listed.</summary>
    public bool HasResults => Results.Count > 0;

    /// <summary>Asks the user to confirm (message, confirm label); set by the view.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private IdeaEntryViewModel? _selectedResult;

    [ObservableProperty]
    private string _status = "Loading the latest ideas…";

    [ObservableProperty]
    private bool _isError;

    /// <summary>Inserts a new unsaved idea at the top of the list.</summary>
    [RelayCommand]
    private void New()
    {
        var entry = IdeaEntryViewModel.CreateNew(_assistant);
        entry.Saved += OnSaved;
        Results.Insert(0, entry);
        SelectedResult = entry;
        OnPropertyChanged(nameof(HasResults));
        ActivityLog.Info("Idea", "New idea started (unsaved)");
    }

    /// <summary>
    /// Searches the ideas matching <see cref="Term"/> on title, description, category and tags; an empty term lists
    /// the newest ideas.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var term = Term.Trim();
        var isLast = term.Length == 0 || term == "*";
        IsError = false;
        Status = isLast ? "Loading the latest ideas…" : $"Searching for \"{term}\"…";

        try
        {
            var entries = isLast
                ? await _assistant.GetLastIdeasAsync(PageSize, cancellationToken)
                : await _assistant.SearchIdeasAsync(term, PageSize, cancellationToken);

            foreach (var row in Results)
                row.Saved -= OnSaved;
            Results.Clear();
            foreach (var entry in entries)
            {
                var row = new IdeaEntryViewModel(entry, _assistant);
                row.Saved += OnSaved;
                Results.Add(row);
            }

            RefreshCategories();
            SelectedResult = Results.FirstOrDefault();
            Status = isLast
                ? $"{Results.Count} latest idea(s)"
                : Results.Count == 0 ? $"No results for \"{term}\"" : $"{Results.Count} result(s) for \"{term}\"";
            ActivityLog.Info("Idea", isLast ? $"Latest ideas: {Results.Count}" : $"Search \"{term}\": {Results.Count} result(s)");
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Idea", "Idea search failed: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Idea", "Idea search failed", ex);
        }
        finally
        {
            OnPropertyChanged(nameof(HasResults));
        }
    }

    private bool CanDelete() => SelectedResult is not null;

    /// <summary>Deletes the selected idea on the server after confirmation (an unsaved idea is only removed from the list).</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        var idea = SelectedResult!;
        if (!await ConfirmAsync($"Delete the idea \"{idea.DisplayTitle}\"?\n\nIt is removed from the server and cannot be restored.", "Delete"))
            return;

        if (!idea.IsNew)
        {
            try
            {
                var result = await _assistant.DeleteIdeaAsync(idea.Id!, cancellationToken);
                if (!result.IsSuccess)
                {
                    IsError = true;
                    Status = result.Message ?? "The Kaonomi server rejected the deletion.";
                    ActivityLog.Error("Idea", $"Delete idea {idea.Id} rejected: {Status}");
                    return;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null)
            {
                IsError = true;
                Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
                ActivityLog.Error("Idea", "Idea not deleted: server unreachable", ex);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                IsError = true;
                Status = ex.Message;
                ActivityLog.Error("Idea", "Idea not deleted", ex);
                return;
            }
        }

        var index = Results.IndexOf(idea);
        idea.Saved -= OnSaved;
        Results.Remove(idea);
        SelectedResult = Results.Count == 0 ? null : Results[Math.Min(index, Results.Count - 1)];
        RefreshCategories();
        OnPropertyChanged(nameof(HasResults));
        IsError = false;
        Status = $"Idea \"{idea.DisplayTitle}\" deleted";
        ActivityLog.Info("Idea", $"Idea deleted: {idea.Id ?? "(new)"}");
    }

    private void OnSaved(IdeaEntryViewModel idea) => RefreshCategories();

    /// <summary>A /project window on the same assistant, for an idea sent there as a new task.</summary>
    public ProjectWindowViewModel CreateProjectWindow() => new(_assistant);

    /// <summary>Rebuilds the category suggestions from the listed ideas.</summary>
    private void RefreshCategories()
    {
        var categories = Results.Select(r => r.Category.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Categories.Clear();
        foreach (var category in categories)
            Categories.Add(category);
    }

    /// <inheritdoc />
    public string NoteWindowName => "Ideas";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        searchTerm = Term,
        resultCount = Results.Count,
        selected = SelectedResult?.ToEntry()
    };
}
