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

/// <summary>Search and edit UI for Kaonomi cache documents.</summary>
public partial class CacheWindowViewModel : ViewModelBase, INoteContextProvider
{
    private const int PageSize = 10;

    private readonly KaonomiAssistantService _assistant;

    /// <summary>Design-time constructor using default server URL from settings.</summary>
    public CacheWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    /// <summary>Uses the given assistant client for cache API calls.</summary>
    public CacheWindowViewModel(KaonomiAssistantService assistant)
    {
        _assistant = assistant;
    }

    /// <summary>Search hits and locally created rows bound to the results list.</summary>
    public ObservableCollection<CacheEntryViewModel> Results { get; } = [];

    /// <summary>True when at least one result row is present.</summary>
    public bool HasResults => Results.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _term = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    private CacheEntryViewModel? _selectedResult;

    [ObservableProperty]
    private string _status = "Enter a term and start the search";

    [ObservableProperty]
    private bool _isError;

    /// <summary>Inserts a new unsaved cache document at the top of the list.</summary>
    [RelayCommand]
    private void New()
    {
        var entry = CacheEntryViewModel.CreateNew(_assistant);
        Results.Insert(0, entry);
        SelectedResult = entry;
        OnPropertyChanged(nameof(HasResults));
        ActivityLog.Info("Cache", "New cache document started (unsaved)");
    }

    private bool CanClone() => SelectedResult is not null;

    /// <summary>Duplicates the selected entry as a new unsaved document.</summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private void Clone()
    {
        var source = SelectedResult!;
        var clone = source.Clone();
        Results.Insert(Results.IndexOf(source) + 1, clone);
        SelectedResult = clone;
        clone.Status = "Unsaved clone";
        ActivityLog.Info("Cache", $"cache document cloned from {source.Id ?? "(new)"} (unsaved)");
    }

    private bool CanSearch() => !string.IsNullOrWhiteSpace(Term);

    /// <summary>
    /// Queries the server for cache entries matching <see cref="Term"/>. When the plain search finds nothing, a fuzzy
    /// search follows (one word with a typo or as the start of a word, accents ignored, is enough) and the status says
    /// that the results are close matches.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var term = Term.Trim();
        IsError = false;
        Status = $"Searching for \"{term}\"…";

        try
        {
            var entries = await _assistant.SearchCacheAsync(term, PageSize, cancellationToken: cancellationToken);
            var fuzzy = false;
            if (entries.Count == 0)
            {
                Status = $"No exact result for \"{term}\": fuzzy search…";
                entries = await _assistant.SearchCacheAsync(term, PageSize, fuzzy: true, cancellationToken);
                fuzzy = true;
            }

            Results.Clear();
            foreach (var entry in entries)
                Results.Add(new CacheEntryViewModel(entry, _assistant));

            SelectedResult = Results.FirstOrDefault();
            Status = Results.Count == 0
                ? $"No results for \"{term}\" (fuzzy search included)"
                : fuzzy
                    ? $"No exact result for \"{term}\": {Results.Count} close match(es) (fuzzy search)"
                    : $"{Results.Count} result(s) for \"{term}\"";
            ActivityLog.Info("Cache", fuzzy
                ? $"Search \"{term}\": no exact result, {Results.Count} close match(es) by fuzzy search"
                : $"Search \"{term}\": {Results.Count} result(s)");
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled";
            ActivityLog.Info("Cache", $"Search \"{term}\" cancelled");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Cache", $"Search \"{term}\" failed: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Cache", $"Search \"{term}\" failed", ex);
        }
        finally
        {
            OnPropertyChanged(nameof(HasResults));
        }
    }

    /// <inheritdoc />
    public string NoteWindowName => "Cache search";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        searchTerm = Term,
        resultCount = Results.Count,
        selected = SelectedResult?.ToEntry()
    };
}
