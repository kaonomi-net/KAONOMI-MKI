using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

/// <summary>One prompt or one completion of a cache builder, edited in its own box.</summary>
public partial class CacheTextItemViewModel : ViewModelBase
{
    /// <summary>Creates a row for <paramref name="text"/>.</summary>
    public CacheTextItemViewModel(string text = "") => _text = text;

    [ObservableProperty]
    private string _text;
}

/// <summary>The mark a model gave to one completion as an answer to one prompt (0 to 10) and why.</summary>
public sealed record CacheEvaluation(string Prompt, string Completion, int Score, string Reason)
{
    /// <summary>The mark out of 10.</summary>
    public string ScoreLabel => $"{Score}/10";

    /// <summary>The mark is 7 or more.</summary>
    public bool IsGood => Score >= 7;

    /// <summary>The mark is under 4.</summary>
    public bool IsBad => Score < 4;
}

/// <summary>
/// Editable copy of a cache builder (<c>CacheBuilder</c> on the server side): several prompts, several completions;
/// the id and date of the original document are kept on save.
/// </summary>
public partial class CacheEntryViewModel : ViewModelBase
{
    private static readonly HashSet<string> EditableProperties =
    [
        nameof(Title), nameof(Model), nameof(Language), nameof(Comment), nameof(Tags), nameof(Events), nameof(Signals),
        nameof(RelatedEntities), nameof(ExcludedEntities), nameof(IsDeleted)
    ];

    private readonly KaonomiAssistantService _assistant;
    private CacheBuilderEntry _entry;
    private bool _isLoading;

    /// <summary>Loads server data into editable fields and tracks dirty state.</summary>
    public CacheEntryViewModel(CacheBuilderEntry entry, KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _entry = entry;
        Prompts.CollectionChanged += OnItemsChanged;
        Completions.CollectionChanged += OnItemsChanged;
        Load(entry);

        if (IsNew)
        {
            IsDirty = true;
            Status = "Unsaved new document";
        }
    }

    /// <summary>Blank document, created on the server (<c>PUT /cachebuilder</c>) on first save.</summary>
    public static CacheEntryViewModel CreateNew(KaonomiAssistantService assistant) => new(new CacheBuilderEntry
    {
        Date = DateTimeOffset.Now,
        AppName = "KAONOMI.Chat",
        MachineName = Environment.MachineName,
        UserName = Environment.UserName,
        Model = "user"
    }, assistant);

    /// <summary>Copy of the values currently displayed, without id, created on the server on first save.</summary>
    public CacheEntryViewModel Clone()
    {
        var copy = ToEntry();
        copy.Id = null;
        copy.Date = DateTimeOffset.Now;
        return new CacheEntryViewModel(copy, _assistant);
    }

    /// <summary>Server document id, or null before first save.</summary>
    public string? Id => _entry.Id;

    /// <summary>True when the row has not yet been persisted.</summary>
    public bool IsNew => _entry.Id is null;

    /// <summary>Human-readable id status for the detail panel.</summary>
    public string IdLabel => IsNew ? "New document · id assigned on save" : $"Id: {_entry.Id}";

    /// <summary>Formatted local date for list display.</summary>
    public string DateLabel => _entry.Date?.ToLocalTime().ToString("g") ?? "Unknown date";

    /// <summary>Compact summary line for list rows: date, model, how many prompts and completions.</summary>
    public string Summary => string.Join(" · ", new[] { DateLabel, Language, Model, CountLabel }.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>How many prompts and completions are filled.</summary>
    public string CountLabel => $"{NonEmpty(Prompts).Count()} prompt(s) → {NonEmpty(Completions).Count()} completion(s)";

    /// <summary>The line shown in the list: the title, or the first prompt when there is none.</summary>
    public string Headline => !string.IsNullOrWhiteSpace(Title) ? Title : NonEmpty(Prompts).FirstOrDefault() ?? "(no prompt)";

    /// <summary>The first completion, for the list rows.</summary>
    public string CompletionPreview => NonEmpty(Completions).FirstOrDefault() ?? string.Empty;

    /// <summary>True when at least one completion is filled.</summary>
    public bool HasCompletion => CompletionPreview.Length > 0;

    /// <summary>The wordings of the prompt (one box each; the empty ones are not saved).</summary>
    public ObservableCollection<CacheTextItemViewModel> Prompts { get; } = [];

    /// <summary>The completions (one box each; the empty ones are not saved).</summary>
    public ObservableCollection<CacheTextItemViewModel> Completions { get; } = [];

    /// <summary>
    /// The last evaluation of every prompt with every completion, the worst marks first. Not saved with the document:
    /// it is what the model thought of the texts as they were when EVALUATE ran.
    /// </summary>
    public ObservableCollection<CacheEvaluation> Evaluations { get; } = [];

    /// <summary>True when an evaluation is shown.</summary>
    public bool HasEvaluations => Evaluations.Count > 0;

    /// <summary>The average, the lowest mark and how many pairs are under 7.</summary>
    [ObservableProperty]
    private string _evaluationSummary = string.Empty;

    /// <summary>The prompts that are filled, in their order.</summary>
    public IReadOnlyList<string> PromptTexts => NonEmpty(Prompts).ToList();

    /// <summary>The completions that are filled, in their order.</summary>
    public IReadOnlyList<string> CompletionTexts => NonEmpty(Completions).ToList();

    /// <summary>Adds the prompts that are not there yet (an empty box is used first); how many were added.</summary>
    public int AddPrompts(IEnumerable<string> texts) => AddTexts(Prompts, texts);

    /// <summary>Adds the completions that are not there yet (an empty box is used first); how many were added.</summary>
    public int AddCompletions(IEnumerable<string> texts) => AddTexts(Completions, texts);

    private static int AddTexts(ObservableCollection<CacheTextItemViewModel> items, IEnumerable<string> texts)
    {
        var added = 0;
        foreach (var text in texts.Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            if (items.Any(i => string.Equals(i.Text.Trim(), text, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (items.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Text)) is { } empty)
                empty.Text = text;
            else
                items.Add(new CacheTextItemViewModel(text));
            added++;
        }
        return added;
    }

    /// <summary>The title as it is typed (not the first prompt).</summary>
    internal string TitleText => Title.Trim();

    /// <summary>Shows the marks of an evaluation, the worst first, and their summary.</summary>
    public void ShowEvaluations(IReadOnlyList<CacheEvaluation> evaluations)
    {
        Evaluations.Clear();
        foreach (var evaluation in evaluations.OrderBy(e => e.Score))
            Evaluations.Add(evaluation);
        EvaluationSummary = evaluations.Count == 0
            ? string.Empty
            : $"{evaluations.Count} pair(s) · average {evaluations.Average(e => e.Score):0.0}/10 · lowest {evaluations.Min(e => e.Score)}/10 · {evaluations.Count(e => !e.IsGood)} under 7";
        OnPropertyChanged(nameof(HasEvaluations));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Headline))]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _model = string.Empty;

    /// <summary>The language of the prompts and of the completions (FR, EN, DE…), in upper case on save; empty: not said.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _language = string.Empty;

    [ObservableProperty]
    private string _comment = string.Empty;

    /// <summary>One tag per line (#TOPIC; normalized on save).</summary>
    [ObservableProperty]
    private string _tags = string.Empty;

    /// <summary>One event per line (@EVENT).</summary>
    [ObservableProperty]
    private string _events = string.Empty;

    /// <summary>One signal per line (!SIGNAL).</summary>
    [ObservableProperty]
    private string _signals = string.Empty;

    /// <summary>One entity per line.</summary>
    [ObservableProperty]
    private string _relatedEntities = string.Empty;

    /// <summary>One entity per line.</summary>
    [ObservableProperty]
    private string _excludedEntities = string.Empty;

    [ObservableProperty]
    private bool _isDeleted;

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
            MarkDirty();
    }

    private void MarkDirty()
    {
        IsDirty = true;
        IsError = false;
        Status = "Unsaved changes";
    }

    /// <summary>Adds an empty prompt.</summary>
    [RelayCommand]
    private void AddPrompt() => Prompts.Add(new CacheTextItemViewModel());

    /// <summary>Removes a prompt.</summary>
    [RelayCommand]
    private void RemovePrompt(CacheTextItemViewModel? item)
    {
        if (item is not null)
            Prompts.Remove(item);
    }

    /// <summary>Adds an empty completion.</summary>
    [RelayCommand]
    private void AddCompletion() => Completions.Add(new CacheTextItemViewModel());

    /// <summary>Removes a completion.</summary>
    [RelayCommand]
    private void RemoveCompletion(CacheTextItemViewModel? item)
    {
        if (item is not null)
            Completions.Remove(item);
    }

    /// <summary>Follows added / removed rows and their edits for the dirty state and the list rows.</summary>
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (CacheTextItemViewModel item in e.OldItems ?? Array.Empty<CacheTextItemViewModel>())
            item.PropertyChanged -= OnItemEdited;
        foreach (CacheTextItemViewModel item in e.NewItems ?? Array.Empty<CacheTextItemViewModel>())
            item.PropertyChanged += OnItemEdited;
        if (e.Action != NotifyCollectionChangedAction.Reset)
            OnItemsEdited();
    }

    private void OnItemEdited(object? sender, PropertyChangedEventArgs e) => OnItemsEdited();

    private void OnItemsEdited()
    {
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(CompletionPreview));
        OnPropertyChanged(nameof(HasCompletion));
        if (!_isLoading)
            MarkDirty();
    }

    private bool CanSave() => IsDirty;

    /// <summary>Creates or updates the cache builder on the server; at least one prompt and one completion are asked.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var updated = ToEntry();
        if (updated.Prompts!.Count == 0 || updated.Completions!.Count == 0)
        {
            IsError = true;
            Status = "Not saved: at least one prompt and one completion are needed";
            return;
        }

        var isNew = IsNew;
        IsError = false;
        Status = isNew ? "Creating…" : "Saving…";

        try
        {
            var result = isNew
                ? await _assistant.CreateCacheBuilderAsync(updated, cancellationToken)
                : await _assistant.UpdateCacheBuilderAsync(updated, cancellationToken);
            if (!result.IsSuccess)
            {
                IsError = true;
                Status = result.Message ?? "The Kaonomi server rejected the save.";
                ActivityLog.Error("Cache", $"{(isNew ? "Create" : "Update")} cache builder {_entry.Id ?? "(new)"} rejected: {Status}");
                return;
            }

            if (isNew)
                updated.Id = string.IsNullOrEmpty(result.ReturnedId) ? null : result.ReturnedId;

            _entry = updated;
            var detected = IsNew ? string.Empty : await ShowDetectionsAsync(updated, cancellationToken);
            IsDirty = IsNew;
            OnPropertyChanged(nameof(Id));
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(IdLabel));
            IsError = IsNew;
            Status = IsNew
                ? "Created, but the server did not return an id"
                : $"{(isNew ? "Created" : "Saved")} · {DateTime.Now:T}{detected}";
            if (IsNew)
                ActivityLog.Error("Cache", "Cache builder created, but the server did not return an id");
            else
                ActivityLog.Info("Cache", $"Cache builder {(isNew ? "created" : "updated")}: {_entry.Id}");
        }
        catch (OperationCanceledException)
        {
            Status = "Save cancelled";
            ActivityLog.Info("Cache", "Cache builder save cancelled");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Cache", "Cache builder not saved: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Cache", "Cache builder not saved", ex);
        }
    }

    /// <summary>
    /// After a save: when the related entities or the tags were left empty, the server detected them in the prompts
    /// and the completions; they are read back and shown (the document stays saved, not modified). Returns what to
    /// add to the status ("" when nothing was detected or the document could not be read back).
    /// </summary>
    private async Task<string> ShowDetectionsAsync(CacheBuilderEntry saved, CancellationToken cancellationToken)
    {
        var noEntities = saved.RelatedEntities is not { Count: > 0 };
        var noTags = saved.Tags is not { Count: > 0 };
        if (!noEntities && !noTags || saved.Id is null)
            return string.Empty;

        CacheBuilderEntry? stored;
        try
        {
            stored = await _assistant.GetCacheBuilderAsync(saved.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return string.Empty;
        }
        if (stored is null)
            return string.Empty;

        var parts = new List<string>();
        _isLoading = true;
        try
        {
            if (noEntities && stored.RelatedEntities is { Count: > 0 } entities)
            {
                saved.RelatedEntities = entities;
                RelatedEntities = string.Join(Environment.NewLine, entities);
                parts.Add($"{entities.Count} entity(ies) detected");
            }
            if (noTags && stored.Tags is { Count: > 0 } tags)
            {
                saved.Tags = tags;
                Tags = string.Join(Environment.NewLine, tags);
                parts.Add($"{tags.Count} tag(s) detected");
            }
        }
        finally
        {
            _isLoading = false;
        }
        return parts.Count == 0 ? string.Empty : " · " + string.Join(", ", parts);
    }

    /// <summary>Discards edits and reloads the last saved server snapshot.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Revert()
    {
        Load(_entry);
        IsDirty = IsNew;
        Status = "Changes discarded";
        ActivityLog.Info("Cache", $"Cache builder changes discarded: {_entry.Id ?? "(new)"}");
    }

    private void Load(CacheBuilderEntry entry)
    {
        _isLoading = true;
        try
        {
            Fill(Prompts, entry.Prompts);
            Fill(Completions, entry.Completions);
            Title = entry.Title ?? string.Empty;
            Model = entry.Model ?? string.Empty;
            Language = entry.Language ?? string.Empty;
            Comment = entry.Comment ?? string.Empty;
            Tags = string.Join(Environment.NewLine, entry.Tags ?? []);
            Events = string.Join(Environment.NewLine, entry.Events ?? []);
            Signals = string.Join(Environment.NewLine, entry.Signals ?? []);
            RelatedEntities = string.Join(Environment.NewLine, entry.RelatedEntities ?? []);
            ExcludedEntities = string.Join(Environment.NewLine, entry.ExcludedEntities ?? []);
            IsDeleted = entry.IsDeleted;
            OnItemsEdited();
            IsDirty = false;
            IsError = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>The rows of <paramref name="texts"/>, one at a time (so that each is followed), an empty one when there is none.</summary>
    private static void Fill(ObservableCollection<CacheTextItemViewModel> items, List<string>? texts)
    {
        while (items.Count > 0)
            items.RemoveAt(items.Count - 1);
        foreach (var text in texts ?? [])
            items.Add(new CacheTextItemViewModel(text));
        if (items.Count == 0)
            items.Add(new CacheTextItemViewModel());
    }

    /// <summary>Copy of the values currently edited (also used as note context).</summary>
    internal CacheBuilderEntry ToEntry() => new()
    {
        Id = _entry.Id,
        Date = _entry.Date,
        AppName = _entry.AppName,
        MachineName = _entry.MachineName,
        UserName = _entry.UserName,
        Comment = Comment,
        Title = Title.Trim(),
        Model = string.IsNullOrWhiteSpace(Model) ? "user" : Model.Trim(),
        Language = string.IsNullOrWhiteSpace(Language) ? null : Language.Trim().ToUpperInvariant(),
        Prompts = NonEmpty(Prompts).ToList(),
        Completions = NonEmpty(Completions).ToList(),
        Tags = MetaTags.Parse(Tags, MetaTags.Tag),
        Events = MetaTags.Parse(Events, MetaTags.Event),
        Signals = MetaTags.Parse(Signals, MetaTags.Signal),
        RelatedEntities = SplitLines(RelatedEntities),
        ExcludedEntities = SplitLines(ExcludedEntities),
        IsDeleted = IsDeleted
    };

    private static IEnumerable<string> NonEmpty(IEnumerable<CacheTextItemViewModel> items) =>
        items.Select(i => i.Text.Trim()).Where(t => t.Length > 0);

    private static List<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
