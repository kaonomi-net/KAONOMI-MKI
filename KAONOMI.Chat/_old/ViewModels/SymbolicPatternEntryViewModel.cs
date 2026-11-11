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

/// <summary>Editable copy of a symbolic pattern; the id and date of the original document are kept on save.</summary>
public partial class SymbolicPatternEntryViewModel : ViewModelBase
{
    private static readonly HashSet<string> EditableProperties =
    [
        nameof(AppName), nameof(MachineName), nameof(UserName), nameof(Comment),
        nameof(Symbol), nameof(Event), nameof(Tags), nameof(IsDeleted)
    ];

    private readonly KaonomiAssistantService _assistant;
    private SymbolicPatternEntry _entry;
    private bool _isLoading;

    /// <summary>Loads server data into editable fields and tracks dirty state.</summary>
    public SymbolicPatternEntryViewModel(SymbolicPatternEntry entry, KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _entry = entry;
        RegexPatterns.CollectionChanged += OnRegexPatternsChanged;
        Load(entry);

        if (IsNew)
        {
            IsDirty = true;
            Status = "Unsaved new document";
        }
    }

    /// <summary>Blank document, created on the server (<c>PUT /symbolicpattern</c>) on first save.</summary>
    public static SymbolicPatternEntryViewModel CreateNew(KaonomiAssistantService assistant) => new(new SymbolicPatternEntry
    {
        Date = DateTimeOffset.Now,
        AppName = "KAONOMI.Chat",
        MachineName = Environment.MachineName,
        UserName = Environment.UserName
    }, assistant);

    /// <summary>Copy of the values currently displayed, without id, created on the server (<c>PUT /symbolicpattern</c>) on first save.</summary>
    public SymbolicPatternEntryViewModel Clone()
    {
        var copy = ToEntry();
        copy.Id = null;
        return new SymbolicPatternEntryViewModel(copy, _assistant);
    }

    /// <summary>Server document id, or null before first save.</summary>
    public string? Id => _entry.Id;

    /// <summary>True when the row has not yet been persisted.</summary>
    public bool IsNew => _entry.Id is null;

    /// <summary>Human-readable id status for the detail panel.</summary>
    public string IdLabel => IsNew ? "New document · id assigned on save" : $"Id: {_entry.Id}";

    /// <summary>Formatted local date for list display.</summary>
    public string DateLabel => _entry.Date?.ToLocalTime().ToString("g") ?? "Unknown date";

    /// <summary>Compact summary line for list rows.</summary>
    public string Summary => Join(" · ", DateLabel, AppName);

    /// <summary>True when a non-empty comment is set.</summary>
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);

    /// <summary>True when an event name is set.</summary>
    public bool HasEvent => !string.IsNullOrWhiteSpace(Event);

    /// <summary>True when at least one tag line is present.</summary>
    public bool HasTags => !string.IsNullOrWhiteSpace(Tags);

    /// <summary>Regular expressions of the pattern (edited one per row; empty rows are not saved).</summary>
    public ObservableCollection<RegexPatternItemViewModel> RegexPatterns { get; } = [];

    /// <summary>The non-empty regular expressions, separated by "|", for the list rows.</summary>
    public string RegexPreview => string.Join("  |  ", NonEmptyPatterns());

    /// <summary>True when at least one regular expression does not compile (saving is refused).</summary>
    public bool HasInvalidPattern => RegexPatterns.Any(p => p.IsInvalid);

    /// <summary>Comma-separated tags for compact list display.</summary>
    public string TagsPreview => string.Join(", ", SplitLines(Tags));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _appName = string.Empty;

    [ObservableProperty]
    private string _machineName = string.Empty;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComment))]
    private string _comment = string.Empty;

    [ObservableProperty]
    private string _symbol = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEvent))]
    private string _event = string.Empty;

    /// <summary>One tag per line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags), nameof(TagsPreview))]
    private string _tags = string.Empty;

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
        {
            IsDirty = true;
            IsError = false;
            Status = "Unsaved changes";
        }
    }

    private bool CanSave() => IsDirty;

    /// <summary>Adds an empty regular expression row.</summary>
    [RelayCommand]
    private void AddPattern() => RegexPatterns.Add(new RegexPatternItemViewModel());

    /// <summary>Removes a regular expression row.</summary>
    [RelayCommand]
    private void RemovePattern(RegexPatternItemViewModel? item)
    {
        if (item is not null)
            RegexPatterns.Remove(item);
    }

    /// <summary>Follows added / removed rows and their edits for the dirty state and the list preview.</summary>
    private void OnRegexPatternsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (RegexPatternItemViewModel item in e.OldItems ?? Array.Empty<RegexPatternItemViewModel>())
            item.PropertyChanged -= OnRegexPatternEdited;
        foreach (RegexPatternItemViewModel item in e.NewItems ?? Array.Empty<RegexPatternItemViewModel>())
            item.PropertyChanged += OnRegexPatternEdited;
        if (e.Action == NotifyCollectionChangedAction.Reset)
            return;
        OnRegexPatternsEdited();
    }

    private void OnRegexPatternEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RegexPatternItemViewModel.Pattern))
            OnRegexPatternsEdited();
        else if (e.PropertyName == nameof(RegexPatternItemViewModel.Error))
            OnPropertyChanged(nameof(HasInvalidPattern));
    }

    private void OnRegexPatternsEdited()
    {
        OnPropertyChanged(nameof(RegexPreview));
        OnPropertyChanged(nameof(HasInvalidPattern));
        if (_isLoading)
            return;
        IsDirty = true;
        IsError = false;
        Status = "Unsaved changes";
    }

    private IEnumerable<string> NonEmptyPatterns() =>
        RegexPatterns.Select(p => p.Pattern).Where(p => !string.IsNullOrWhiteSpace(p));

    /// <summary>Creates or updates the symbolic pattern on the server.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (RegexPatterns.FirstOrDefault(p => p.IsInvalid) is { } invalid)
        {
            IsError = true;
            Status = $"Not saved: \"{invalid.Pattern}\" is not a valid regular expression";
            return;
        }

        var updated = ToEntry();
        var isNew = IsNew;
        IsError = false;
        Status = isNew ? "Creating…" : "Saving…";

        try
        {
            var result = isNew
                ? await _assistant.CreateSymbolicPatternAsync(updated, cancellationToken)
                : await _assistant.UpdateSymbolicPatternAsync(updated, cancellationToken);
            if (!result.IsSuccess)
            {
                IsError = true;
                Status = result.Message ?? "The Kaonomi server rejected the save.";
                ActivityLog.Error("Symbol", $"{(isNew ? "Create" : "Update")} Symbolic pattern {_entry.Id ?? "(new)"} rejected: {Status}");
                return;
            }

            if (isNew)
                updated.Id = string.IsNullOrEmpty(result.ReturnedId) ? null : result.ReturnedId;

            _entry = updated;
            IsDirty = IsNew;
            OnPropertyChanged(nameof(Id));
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(IdLabel));
            IsError = IsNew;
            Status = IsNew
                ? "Created, but the server did not return an id"
                : $"{(isNew ? "Created" : "Saved")} · {DateTime.Now:T}";
            if (IsNew)
                ActivityLog.Error("Symbol", "Symbolic pattern created, but the server did not return an id");
            else
                ActivityLog.Info("Symbol", $"Symbolic pattern {(isNew ? "created" : "updated")}: {_entry.Id}");
        }
        catch (OperationCanceledException)
        {
            Status = "Save cancelled";
            ActivityLog.Info("Symbol", "Symbolic pattern save cancelled");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Symbol", "Symbolic pattern not saved: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Symbol", "Symbolic pattern not saved", ex);
        }
    }

    /// <summary>Discards edits and reloads the last saved server snapshot.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Revert()
    {
        Load(_entry);
        IsDirty = IsNew;
        Status = "Changes discarded";
        ActivityLog.Info("Symbol", $"Symbolic pattern changes discarded: {_entry.Id ?? "(new)"}");
    }

    private void Load(SymbolicPatternEntry entry)
    {
        _isLoading = true;
        try
        {
            AppName = entry.AppName ?? string.Empty;
            MachineName = entry.MachineName ?? string.Empty;
            UserName = entry.UserName ?? string.Empty;
            Comment = entry.Comment ?? string.Empty;
            RegexPatterns.Clear();
            foreach (var pattern in entry.RegexPatterns ?? [])
                RegexPatterns.Add(new RegexPatternItemViewModel(pattern));
            if (RegexPatterns.Count == 0)
                RegexPatterns.Add(new RegexPatternItemViewModel());
            OnPropertyChanged(nameof(RegexPreview));
            OnPropertyChanged(nameof(HasInvalidPattern));
            Symbol = entry.Symbol ?? string.Empty;
            Event = entry.Event ?? string.Empty;
            Tags = string.Join(Environment.NewLine, entry.Tags ?? []);
            IsDeleted = entry.IsDeleted;
            IsDirty = false;
            IsError = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Copy of the values currently edited (also used as note context).</summary>
    internal SymbolicPatternEntry ToEntry() => new()
    {
        Id = _entry.Id,
        Date = _entry.Date,
        AppName = AppName,
        MachineName = MachineName,
        UserName = UserName,
        Comment = Comment,
        RegexPatterns = NonEmptyPatterns().ToList(),
        Symbol = Symbol,
        Event = string.IsNullOrWhiteSpace(Event) ? null : Event,
        Tags = SplitLines(Tags),
        IsDeleted = IsDeleted
    };

    private static List<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
