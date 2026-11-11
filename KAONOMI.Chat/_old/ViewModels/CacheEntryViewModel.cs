using System;
using System.Collections.Generic;
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

/// <summary>Editable copy of a cached dialog; the id and date of the original document are kept on save.</summary>
public partial class CacheEntryViewModel : ViewModelBase
{
    private static readonly HashSet<string> EditableProperties =
    [
        nameof(AppName), nameof(MachineName), nameof(UserName), nameof(Comment), nameof(Prompt),
        nameof(Completion), nameof(Model), nameof(RelatedEntities), nameof(ExcludedEntities), nameof(IsDeleted)
    ];

    private readonly KaonomiAssistantService _assistant;
    private CacheEntry _entry;
    private bool _isLoading;

    /// <summary>Loads server data into editable fields and tracks dirty state.</summary>
    public CacheEntryViewModel(CacheEntry entry, KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _entry = entry;
        Load(entry);

        if (IsNew)
        {
            IsDirty = true;
            Status = "Unsaved new document";
        }
    }

    /// <summary>Blank document, created on the server (<c>PUT /cache</c>) on first save.</summary>
    public static CacheEntryViewModel CreateNew(KaonomiAssistantService assistant) => new(new CacheEntry
    {
        Date = DateTimeOffset.Now,
        AppName = "KAONOMI.Chat",
        MachineName = Environment.MachineName,
        UserName = Environment.UserName,
        Model = "user"
    }, assistant);

    /// <summary>Copy of the values currently displayed, without id, created on the server (<c>PUT /cache</c>) on first save.</summary>
    public CacheEntryViewModel Clone()
    {
        var copy = ToEntry();
        copy.Id = null;
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

    /// <summary>Compact summary line for list rows.</summary>
    public string Summary => Join(" · ", DateLabel, Model, AppName);

    /// <summary>True when a non-empty comment is set.</summary>
    public bool HasComment => !string.IsNullOrWhiteSpace(Comment);

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
    private string _prompt = string.Empty;

    [ObservableProperty]
    private string _completion = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _model = string.Empty;

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
        {
            IsDirty = true;
            IsError = false;
            Status = "Unsaved changes";
        }
    }

    private bool CanSave() => IsDirty;

    /// <summary>Creates or updates the cache document on the server.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var updated = ToEntry();
        var isNew = IsNew;
        IsError = false;
        Status = isNew ? "Creating…" : "Saving…";

        try
        {
            var result = isNew
                ? await _assistant.CreateCacheAsync(updated, cancellationToken)
                : await _assistant.UpdateCacheAsync(updated, cancellationToken);
            if (!result.IsSuccess)
            {
                IsError = true;
                Status = result.Message ?? "The Kaonomi server rejected the save.";
                ActivityLog.Error("Cache", $"{(isNew ? "Create" : "Update")} Cache document {_entry.Id ?? "(new)"} rejected: {Status}");
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
                ActivityLog.Error("Cache", "Cache document created, but the server did not return an id");
            else
                ActivityLog.Info("Cache", $"Cache document {(isNew ? "created" : "updated")}: {_entry.Id}");
        }
        catch (OperationCanceledException)
        {
            Status = "Save cancelled";
            ActivityLog.Info("Cache", "Cache document save cancelled");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Cache", "Cache document not saved: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Cache", "Cache document not saved", ex);
        }
    }

    /// <summary>Discards edits and reloads the last saved server snapshot.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Revert()
    {
        Load(_entry);
        IsDirty = IsNew;
        Status = "Changes discarded";
        ActivityLog.Info("Cache", $"Cache document changes discarded: {_entry.Id ?? "(new)"}");
    }

    private void Load(CacheEntry entry)
    {
        _isLoading = true;
        try
        {
            AppName = entry.AppName ?? string.Empty;
            MachineName = entry.MachineName ?? string.Empty;
            UserName = entry.UserName ?? string.Empty;
            Comment = entry.Comment ?? string.Empty;
            Prompt = entry.Prompt ?? string.Empty;
            Completion = entry.Completion ?? string.Empty;
            Model = entry.Model ?? string.Empty;
            RelatedEntities = string.Join(Environment.NewLine, entry.RelatedEntities ?? []);
            ExcludedEntities = string.Join(Environment.NewLine, entry.ExcludedEntities ?? []);
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
    internal CacheEntry ToEntry() => new()
    {
        Id = _entry.Id,
        Date = _entry.Date,
        AppName = AppName,
        MachineName = MachineName,
        UserName = UserName,
        Comment = Comment,
        Prompt = Prompt,
        Completion = Completion,
        Model = Model,
        RelatedEntities = SplitLines(RelatedEntities),
        ExcludedEntities = SplitLines(ExcludedEntities),
        IsDeleted = IsDeleted
    };

    private static List<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
