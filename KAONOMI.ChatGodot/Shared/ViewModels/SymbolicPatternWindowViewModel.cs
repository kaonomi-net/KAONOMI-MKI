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

/// <summary>Search and edit UI for Kaonomi symbolic pattern documents.</summary>
public partial class SymbolicPatternWindowViewModel : ViewModelBase, INoteContextProvider, IProjectDocument
{
    /// <summary>The project of the pattern selected (shown and chosen by its name in the header of the window).</summary>
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

    partial void OnSelectedResultChanged(SymbolicPatternEntryViewModel? value)
    {
        OnPropertyChanged(nameof(ProjectId));
        OnPropertyChanged(nameof(HasProjectDocument));
    }

    private const int PageSize = 10;

    private readonly KaonomiAssistantService _assistant;

    /// <summary>Design-time constructor using default server URL from settings.</summary>
    public SymbolicPatternWindowViewModel() : this(
        ChatSettings.Load())
    {
    }

    private SymbolicPatternWindowViewModel(ChatSettings settings) : this(
        new KaonomiAssistantService(settings.ServerUrl), new OllamaModelService(settings.OllamaUrl, settings.LlmProvider),
        () => null, settings.ResolvedRegexDirectory)
    {
    }

    /// <summary>
    /// Uses the given assistant client for symbolic pattern API calls; the regex assistant asks Ollama directly with
    /// the model returned by <paramref name="model"/> (the one selected in the chat window) and saves its regexes in
    /// <paramref name="regexDirectory"/>.
    /// </summary>
    public SymbolicPatternWindowViewModel(
        KaonomiAssistantService assistant, OllamaModelService ollama, Func<string?> model, string regexDirectory)
    {
        _assistant = assistant;
        RegexAssistant = new RegexAssistantViewModel(ollama, model, () => SelectedResult, regexDirectory);
        _transfer = new DocumentTransfer<SymbolicPatternEntry>
        {
            Plural = "symbolic patterns",
            LogArea = "Symbol",
            LoadAll = (size, ct) => _assistant.GetLastSymbolicPatternsAsync(size, ct),
            Id = e => e.Id,
            Create = (e, ct) => _assistant.CreateSymbolicPatternAsync(e, ct),
            Save = (e, ct) => _assistant.UpdateSymbolicPatternAsync(e, ct),
            Confirm = (message, label) => ConfirmAsync(message, label),
            OpenTextFile = () => OpenTextFileAsync(),
            PickFolder = title => PickFolderAsync(title),
            Report = (status, isError) => { Status = status; IsError = isError; }
        };
    }

    private readonly DocumentTransfer<SymbolicPatternEntry> _transfer;

    /// <summary>Asks the user to confirm (message, confirm label); set by the view.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    /// <summary>Asks for a JSON file to import and returns its text, or null when cancelled; set by the view.</summary>
    public Func<Task<string?>> OpenTextFileAsync { get; set; } = () => Task.FromResult<string?>(null);

    /// <summary>Asks for a folder (argument: the dialog title), or null when cancelled; set by the view.</summary>
    public Func<string, Task<string?>> PickFolderAsync { get; set; } = _ => Task.FromResult<string?>(null);

    /// <summary>Exports every symbolic pattern of the server (deleted ones included) to a folder: one JSON file per pattern.</summary>
    [RelayCommand]
    private Task ExportAllAsync(CancellationToken cancellationToken) => _transfer.ExportAllAsync(cancellationToken);

    /// <summary>
    /// Imports the symbolic patterns of a JSON file (one pattern or an array, as the seed files). The server reloads its
    /// patterns within five minutes.
    /// </summary>
    [RelayCommand]
    private Task ImportAsync(CancellationToken cancellationToken) => _transfer.ImportFileAsync(cancellationToken);

    /// <summary>Imports the symbolic patterns of a folder (an "Export all", one file per pattern).</summary>
    [RelayCommand]
    private Task ImportFolderAsync(CancellationToken cancellationToken) => _transfer.ImportFolderAsync(cancellationToken);

    /// <summary>Regex generation assistant (examples + goal → regex added to the selected document).</summary>
    public RegexAssistantViewModel RegexAssistant { get; }

    /// <summary>Search hits and locally created rows bound to the results list.</summary>
    public ObservableCollection<SymbolicPatternEntryViewModel> Results { get; } = [];

    /// <summary>True when at least one result row is present.</summary>
    public bool HasResults => Results.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _term = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand))]
    private SymbolicPatternEntryViewModel? _selectedResult;

    [ObservableProperty]
    private string _status = "Enter a term and start the search";

    [ObservableProperty]
    private bool _isError;

    /// <summary>Inserts a new unsaved pattern document at the top of the list.</summary>
    [RelayCommand]
    private void New()
    {
        var entry = SymbolicPatternEntryViewModel.CreateNew(_assistant);
        Results.Insert(0, entry);
        SelectedResult = entry;
        OnPropertyChanged(nameof(HasResults));
        ActivityLog.Info("Symbol", "New symbolic pattern started (unsaved)");
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
        ActivityLog.Info("Symbol", $"symbolic pattern cloned from {source.Id ?? "(new)"} (unsaved)");
    }

    private bool CanSearch() => !string.IsNullOrWhiteSpace(Term);

    /// <summary>Queries the server for patterns matching <see cref="Term"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var term = Term.Trim();
        IsError = false;
        Status = $"Searching for \"{term}\"…";

        try
        {
            var entries = await _assistant.SearchSymbolicPatternsAsync(term, PageSize, cancellationToken);

            Results.Clear();
            foreach (var entry in entries)
                Results.Add(new SymbolicPatternEntryViewModel(entry, _assistant));

            SelectedResult = Results.FirstOrDefault();
            Status = Results.Count == 0
                ? $"No results for \"{term}\""
                : $"{Results.Count} result(s) for \"{term}\"";
            ActivityLog.Info("Symbol", $"Search \"{term}\": {Results.Count} result(s)");
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled";
            ActivityLog.Info("Symbol", $"Search \"{term}\" cancelled");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Symbol", $"Search \"{term}\" failed: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Symbol", $"Search \"{term}\" failed", ex);
        }
        finally
        {
            OnPropertyChanged(nameof(HasResults));
        }
    }

    /// <inheritdoc />
    public string NoteWindowName => "Symbolic patterns";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        searchTerm = Term,
        resultCount = Results.Count,
        selected = SelectedResult?.ToEntry()
    };
}
