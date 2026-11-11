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

/// <summary>A completion of the results of <c>/completion</c>.</summary>
public sealed class CompletionItemViewModel(CompletionEntry entry)
{
    /// <summary>The completion as saved.</summary>
    public CompletionEntry Entry { get; } = entry;

    /// <summary>First line of the prompt, shown in the list.</summary>
    public string Title => FirstLine(Entry.Prompt) is { Length: > 0 } line ? line : "(empty prompt)";

    /// <summary>Date, model and topic, shown under the title.</summary>
    public string Summary => string.Join(" · ", new[]
    {
        Entry.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), Entry.Model, Entry.Topic, Entry.CollectionName is "generic" ? null : Entry.CollectionName
    }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string FirstLine(string text) => text.Trim().Split('\n')[0].Trim();
}

/// <summary>A term of the word cloud: its size and color follow its number of occurrences.</summary>
public sealed record WordCloudItem(string Term, int Count, double FontSize, IBrush Brush)
{
    /// <summary>Tooltip of the term.</summary>
    public string Tip => $"{Term}: {Count} occurrence(s) · click to search it";
}

/// <summary>
/// Completions of Kaonomi (<c>/completion</c>, <c>/ncompletion</c> in its own window): a search on their prompt and
/// completion (<c>POST /completion/search</c>; empty: the newest ones), the results, the selected completion, and a
/// word cloud of the most frequent terms of the results (stop words left out).
/// </summary>
public partial class CompletionWindowViewModel : ViewModelBase
{
    /// <summary>Number of terms of the word cloud.</summary>
    private const int CloudSize = 70;

    /// <summary>Colors of the terms, from the most frequent ones.</summary>
    private static readonly IBrush[] Palette = new[] { "#2563EB", "#7C3AED", "#DB2777", "#EA580C", "#059669", "#0891B2", "#CA8A04", "#4F46E5" }
        .Select(c => (IBrush)new SolidColorBrush(Color.Parse(c))).ToArray();

    private readonly KaonomiAssistantService _assistant;

    /// <summary>Design-time constructor using default server URL from settings.</summary>
    public CompletionWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    /// <summary>Uses the given assistant client; lists the newest completions.</summary>
    public CompletionWindowViewModel(KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _ = SearchAsync(CancellationToken.None);
    }

    /// <summary>Completions found.</summary>
    public ObservableCollection<CompletionItemViewModel> Results { get; } = [];

    /// <summary>Most frequent terms of the results, in a scattered order.</summary>
    public ObservableCollection<WordCloudItem> Cloud { get; } = [];

    /// <summary>Languages of the results (field <c>language</c>), for the pie chart: the most frequent first.</summary>
    [ObservableProperty]
    private IReadOnlyList<Views.PieSlice> _languages = [];

    /// <summary>Choices of the number of results.</summary>
    public int[] SizeChoices { get; } = [50, 100, 200, 500, 1000];

    /// <summary>Asks the user to confirm (message, confirm label); set by the view.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (_, _) => Task.FromResult(true);

    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    private int _size = 200;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private CompletionItemViewModel? _selectedResult;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Loading the latest completions…";

    [ObservableProperty]
    private bool _isError;

    /// <summary>
    /// Searches the completions matching <see cref="Term"/> in their prompt and completion (empty: the newest ones),
    /// then builds the word cloud of the results.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var term = Term.Trim();
        var isLast = term.Length == 0 || term == "*";
        IsBusy = true;
        IsError = false;
        Status = isLast ? "Loading the latest completions…" : $"Searching for \"{term}\"…";
        try
        {
            var entries = isLast
                ? await _assistant.GetLastCompletionsAsync(Size, cancellationToken)
                : await _assistant.SearchCompletionsAsync(term, Size, cancellationToken);
            Results.Clear();
            foreach (var entry in entries)
                Results.Add(new CompletionItemViewModel(entry));
            SelectedResult = Results.FirstOrDefault();
            BuildCloud();
            Status = isLast
                ? $"{Results.Count} latest completion(s) · {Cloud.Count} terms in the cloud"
                : Results.Count == 0 ? $"No completion for \"{term}\"" : $"{Results.Count} completion(s) for \"{term}\" · {Cloud.Count} terms in the cloud";
            ActivityLog.Info("Completion", isLast ? $"Latest completions: {Results.Count}" : $"Search \"{term}\": {Results.Count} result(s)");
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Completion", "Completion search failed: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Completion", "Completion search failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A term of the cloud clicked: it becomes the search.</summary>
    [RelayCommand]
    private async Task SearchTermAsync(string term)
    {
        Term = term;
        await SearchAsync(CancellationToken.None);
    }

    /// <summary>
    /// The cloud of the most frequent terms of the prompts and completions of the results: the font size grows with
    /// the square root of the count (12 to 46 points), the most frequent terms get the first colors; the terms are
    /// scattered in an order fixed by their spelling, so that the big ones are not all together.
    /// </summary>
    private void BuildCloud()
    {
        BuildLanguages();
        Cloud.Clear();
        var terms = WordCloud.TopTerms(Results.SelectMany(r => new[] { r.Entry.Prompt, r.Entry.Completion }), CloudSize);
        if (terms.Count == 0)
            return;
        double max = Math.Sqrt(terms[0].Count), min = Math.Sqrt(terms[^1].Count);
        var items = terms.Select((t, rank) =>
        {
            var share = max - min < 1e-9 ? 1 : (Math.Sqrt(t.Count) - min) / (max - min);
            return new WordCloudItem(t.Term, t.Count, Math.Round(12 + 34 * share), Palette[Math.Min(Palette.Length - 1, rank * Palette.Length / terms.Count)]);
        });
        foreach (var item in items.OrderBy(i => StableHash(i.Term)))
            Cloud.Add(item);
    }

    /// <summary>The pie chart of the languages of the results (an empty language is "?").</summary>
    private void BuildLanguages()
    {
        var groups = Results.GroupBy(r => string.IsNullOrWhiteSpace(r.Entry.Language) ? "?" : r.Entry.Language.Trim().ToUpperInvariant())
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var total = Math.Max(1, Results.Count);
        Languages = groups.Select((g, i) => new Views.PieSlice(g.Key, g.Count(), Palette[i % Palette.Length]) { Share = g.Count() / (double)total })
            .ToList();
    }

    /// <summary>A hash of a term that is the same at every run (string.GetHashCode changes from one run to the next).</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;
        return hash;
    }

    private bool CanDelete() => SelectedResult is not null;

    /// <summary>Deletes the selected completion from Kaonomi (after confirmation).</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (SelectedResult is not { } selected || !await ConfirmAsync($"Delete the completion \"{selected.Title}\" from Kaonomi?", "Delete"))
            return;
        try
        {
            var result = await _assistant.DeleteCompletionAsync(selected.Entry.Id);
            if (!result.Success)
            {
                IsError = true;
                Status = result.Message ?? "Not deleted";
                return;
            }
            var index = Results.IndexOf(selected);
            Results.Remove(selected);
            SelectedResult = Results.Count == 0 ? null : Results[Math.Min(index, Results.Count - 1)];
            BuildCloud();
            IsError = false;
            Status = "Completion deleted";
            ActivityLog.Info("Completion", $"Deleted completion {selected.Entry.Id}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            IsError = true;
            Status = ex.Message;
        }
    }
}
