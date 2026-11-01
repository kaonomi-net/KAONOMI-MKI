using System;
using System.Collections.ObjectModel;
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

/// <summary>
/// <c>/search</c>: a loose search of a term in the indices of KAONOMI at once (<c>POST /search</c>): a word with a typo
/// or as the start of a word, accents ignored, is enough. The indices to look into are ticked; the hits come grouped
/// by index, the best first; the selected one shows its document as JSON.
/// </summary>
public partial class SearchWindowViewModel : ViewModelBase
{
    private const int PageSize = 10;

    /// <summary>The indices the search can look into (the ones of the server).</summary>
    public static readonly string[] AllIndices =
        ["cache", "completion", "dialog", "diary", "document", "idea", "imageanalysis", "intel", "ontology", "relation", "taskitem", "youtubevideo", "cluster", "wiki", "bpmn", "infra", "mindmap", "ghostwriter"];

    private readonly KaonomiAssistantService _assistant;

    public SearchWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    public SearchWindowViewModel(KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        foreach (var index in AllIndices)
        {
            var choice = new SearchIndexChoice(index);
            // An index ticked or unticked: the search runs again (once for ALL / NONE).
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SearchIndexChoice.Enabled) && !_toggling)
                    SearchAgain();
            };
            Indices.Add(choice);
        }
    }

    private bool _toggling;
    private bool _again;

    /// <summary>Runs the search again with the indices ticked now (when a term is there); a search running is cancelled first.</summary>
    private void SearchAgain()
    {
        if (string.IsNullOrWhiteSpace(Term))
            return;
        if (IsSearching)
        {
            _again = true;
            SearchCommand.Cancel();
            return;
        }
        SearchCommand.Execute(null);
    }

    /// <summary>The indices, each one ticked or not.</summary>
    public ObservableCollection<SearchIndexChoice> Indices { get; } = [];

    /// <summary>The hits, grouped by index, the best of each index first.</summary>
    public ObservableCollection<SearchHitViewModel> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _term = string.Empty;

    [ObservableProperty]
    private SearchHitViewModel? _selectedResult;

    [ObservableProperty]
    private string _status = "Type a few words: a typo or the start of a word is enough";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _isSearching;

    /// <summary>Raised by a double-click on a hit (or OPEN): the host opens it in the window of its kind.</summary>
    public event Action<SearchHit>? OpenRequested;

    public void Open(SearchHitViewModel hit)
    {
        SelectedResult = hit;
        OpenRequested?.Invoke(hit.Hit);
    }

    private bool CanSearch() => !string.IsNullOrWhiteSpace(Term) && !IsSearching;

    /// <summary>Every index ticked, or none.</summary>
    [RelayCommand]
    private void ToggleAll()
    {
        var all = Indices.All(i => i.Enabled);
        _toggling = true;
        try
        {
            foreach (var index in Indices)
                index.Enabled = !all;
        }
        finally
        {
            _toggling = false;
        }
        SearchAgain();
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var term = Term.Trim();
        var indices = Indices.Where(i => i.Enabled).Select(i => i.Name).ToList();
        if (indices.Count == 0)
        {
            Results.Clear();
            SelectedResult = null;
            foreach (var choice in Indices)
                choice.Count = 0;
            IsError = false;
            Status = "No index ticked";
            OnPropertyChanged(nameof(HasResults));
            return;
        }
        IsError = false;
        IsSearching = true;
        SearchCommand.NotifyCanExecuteChanged();
        Status = $"Searching \"{term}\" in {indices.Count} index(es)…";
        try
        {
            var hits = await _assistant.SearchAllAsync(term, indices, PageSize, cancellationToken);
            Results.Clear();
            // Grouped by index, the best first; each hit knows its weight in its index (its score over the best one),
            // the first of each index opens its group; each index shows how many hits it has.
            var ordered = hits.OrderBy(h => Array.IndexOf(AllIndices, h.Index)).ThenByDescending(h => h.Score).ToList();
            var best = ordered.GroupBy(h => h.Index).ToDictionary(g => g.Key, g => g.Max(h => h.Score));
            string? previous = null;
            foreach (var hit in ordered)
            {
                var item = new SearchHitViewModel(hit)
                {
                    Relative = best[hit.Index] > 0 ? hit.Score / best[hit.Index] : 0,
                    IsFirstOfIndex = hit.Index != previous,
                    CountInIndex = ordered.Count(h => h.Index == hit.Index)
                };
                previous = hit.Index;
                Results.Add(item);
            }
            foreach (var choice in Indices)
                choice.Count = ordered.Count(h => h.Index == choice.Name);
            SelectedResult = Results.FirstOrDefault();
            var indexesHit = Results.Select(r => r.Index).Distinct().Count();
            Status = Results.Count == 0
                ? $"Nothing found for \"{term}\" in {indices.Count} index(es)"
                : $"{Results.Count} hit(s) for \"{term}\" in {indexesHit} index(es)";
            ActivityLog.Info("Search", Status);
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Search", $"Search \"{term}\" failed: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Search", $"Search \"{term}\" failed", ex);
        }
        finally
        {
            IsSearching = false;
            SearchCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasResults));
            if (_again)
            {
                // The indices changed while it ran: once more with the indices ticked now.
                _again = false;
                SearchCommand.Execute(null);
            }
        }
    }
}

/// <summary>An index of the search, ticked or not.</summary>
public partial class SearchIndexChoice(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>Hits of the last search in this index.</summary>
    [ObservableProperty]
    private int _count;
}

/// <summary>A hit of the search: its index, title, snippet, date, and its document as JSON (indented).</summary>
public sealed class SearchHitViewModel(SearchHit hit)
{
    public SearchHit Hit { get; } = hit;

    /// <summary>The score over the best score of its index (0 to 1).</summary>
    public double Relative { get; init; }

    /// <summary>True for the first hit of its index in the list (it opens the group).</summary>
    public bool IsFirstOfIndex { get; init; }

    /// <summary>Hits of its index in the list.</summary>
    public int CountInIndex { get; init; }

    public string Index => Hit.Index;
    public string Id => Hit.Id;
    public string Title => string.IsNullOrWhiteSpace(Hit.Title) ? Hit.Id : Hit.Title;
    public string Snippet => Hit.Snippet;
    public string Summary => $"{Hit.Index}{(Hit.Date is { } date ? " · " + date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : string.Empty)} · score {Hit.Score:0.00}";
    public string IdLabel => $"{Hit.Index} · {Hit.Id}";

    /// <summary>The document, indented.</summary>
    public string SourceJson
    {
        get
        {
            try
            {
                using var document = JsonDocument.Parse(Hit.Source);
                return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
            catch (JsonException)
            {
                return Hit.Source;
            }
        }
    }
}
