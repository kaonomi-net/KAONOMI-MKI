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

/// <summary>Editable copy of an intel; the id and date of the original document are kept on save.</summary>
public partial class IntelEntryViewModel : ViewModelBase
{
    private static readonly HashSet<string> EditableProperties =
    [
        nameof(AppName), nameof(MachineName), nameof(UserName), nameof(Comment), nameof(Name), nameof(Description),
        nameof(RelatedEntities), nameof(ExcludedEntities), nameof(SentEvents), nameof(Questions), nameof(WhenStatement),
        nameof(WhereStatement), nameof(WithWhatStatement), nameof(OtherInstructions), nameof(StartDate), nameof(EndDate)
    ];

    private readonly KaonomiAssistantService _assistant;
    private IntelEntry _entry;
    private bool _isLoading;

    /// <summary>Loads server data into editable fields and tracks dirty state.</summary>
    public IntelEntryViewModel(IntelEntry entry, KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _entry = entry;
        Load(entry);

        if (IsNew)
        {
            IsDirty = true;
            Status = "Unsaved new intel";
        }
    }

    /// <summary>Blank intel starting today, created on the server (<c>PUT /intel</c>) on first save.</summary>
    public static IntelEntryViewModel CreateNew(KaonomiAssistantService assistant) => new(new IntelEntry
    {
        Date = DateTimeOffset.Now,
        AppName = "KAONOMI.Chat",
        MachineName = Environment.MachineName,
        UserName = Environment.UserName,
        StartDate = DateTime.Today
    }, assistant);

    /// <summary>Copy of the values currently displayed, without id, created on the server (<c>PUT /intel</c>) on first save.</summary>
    public IntelEntryViewModel Clone()
    {
        var copy = ToEntry();
        copy.Id = null;
        copy.Date = DateTimeOffset.Now;
        copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? copy.Name : copy.Name + " (copy)";
        return new IntelEntryViewModel(copy, _assistant);
    }

    /// <summary>Server document id, or null before first save.</summary>
    public string? Id => _entry.Id;

    /// <summary>True when the row has not yet been persisted.</summary>
    public bool IsNew => _entry.Id is null;

    /// <summary>Human-readable id status for the detail panel.</summary>
    public string IdLabel => IsNew ? "New intel · id assigned on save" : $"Id: {_entry.Id}";

    /// <summary>Formatted local date for list display.</summary>
    public string DateLabel => _entry.Date?.ToLocalTime().ToString("g") ?? "Unknown date";

    /// <summary>Title of the list row.</summary>
    public string Title => string.IsNullOrWhiteSpace(Name) ? "(untitled)" : Name;

    /// <summary>Compact summary line for list rows: date, period and user.</summary>
    public string Summary => Join(" · ", DateLabel, Period, UserName);

    /// <summary>Start and end days, when set.</summary>
    private string? Period => StartDate is null && EndDate is null ? null : $"{StartDate:d} → {EndDate:d}";

    /// <summary>True when a non-empty description is set.</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    [ObservableProperty]
    private string _appName = string.Empty;

    [ObservableProperty]
    private string _machineName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string _description = string.Empty;

    /// <summary>One entity per line.</summary>
    [ObservableProperty]
    private string _relatedEntities = string.Empty;

    /// <summary>One entity per line.</summary>
    [ObservableProperty]
    private string _excludedEntities = string.Empty;

    /// <summary>One event per line.</summary>
    [ObservableProperty]
    private string _sentEvents = string.Empty;

    /// <summary>One question per line.</summary>
    [ObservableProperty]
    private string _questions = string.Empty;

    [ObservableProperty]
    private string _whenStatement = string.Empty;

    [ObservableProperty]
    private string _whereStatement = string.Empty;

    [ObservableProperty]
    private string _withWhatStatement = string.Empty;

    [ObservableProperty]
    private string _otherInstructions = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private DateTime? _startDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private DateTime? _endDate;

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

    /// <summary>Creates or updates the intel on the server.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (EndDate is { } end && StartDate is { } start && end < start)
        {
            IsError = true;
            Status = "The end date is before the start date.";
            return;
        }

        var updated = ToEntry();
        var isNew = IsNew;
        IsError = false;
        Status = isNew ? "Creating…" : "Saving…";

        try
        {
            var result = isNew
                ? await _assistant.CreateIntelAsync(updated, cancellationToken)
                : await _assistant.UpdateIntelAsync(updated, cancellationToken);
            if (!result.IsSuccess)
            {
                IsError = true;
                Status = result.Message ?? "The Kaonomi server rejected the save.";
                ActivityLog.Error("Intel", $"{(isNew ? "Create" : "Update")} intel {_entry.Id ?? "(new)"} rejected: {Status}");
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
                ActivityLog.Error("Intel", "Intel created, but the server did not return an id");
            else
                ActivityLog.Info("Intel", $"Intel {(isNew ? "created" : "updated")}: {_entry.Id} ({Name})");
        }
        catch (OperationCanceledException)
        {
            Status = "Save cancelled";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Intel", "Intel not saved: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Intel", "Intel not saved", ex);
        }
    }

    /// <summary>Discards edits and reloads the last saved server snapshot.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Revert()
    {
        Load(_entry);
        IsDirty = IsNew;
        Status = "Changes discarded";
    }

    private void Load(IntelEntry entry)
    {
        _isLoading = true;
        try
        {
            AppName = entry.AppName ?? string.Empty;
            MachineName = entry.MachineName ?? string.Empty;
            UserName = entry.UserName ?? string.Empty;
            Comment = entry.Comment ?? string.Empty;
            Name = entry.Name ?? string.Empty;
            Description = entry.Description ?? string.Empty;
            RelatedEntities = Lines(entry.RelatedEntities);
            ExcludedEntities = Lines(entry.ExcludedEntities);
            SentEvents = Lines(entry.SentEvents);
            Questions = Lines(entry.Questions);
            WhenStatement = entry.WhenStatement ?? string.Empty;
            WhereStatement = entry.WhereStatement ?? string.Empty;
            WithWhatStatement = entry.WithWhatStatement ?? string.Empty;
            OtherInstructions = entry.OtherInstructions ?? string.Empty;
            StartDate = DayOf(entry.StartDate);
            EndDate = DayOf(entry.EndDate);
            IsDirty = false;
            IsError = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Copy of the values currently edited (also used as note context).</summary>
    internal IntelEntry ToEntry() => new()
    {
        Id = _entry.Id,
        Date = _entry.Date,
        IsDeleted = _entry.IsDeleted,
        AppName = AppName,
        MachineName = MachineName,
        UserName = UserName,
        Comment = Comment,
        Name = Name.Trim(),
        Description = Description,
        RelatedEntities = SplitLines(RelatedEntities),
        ExcludedEntities = SplitLines(ExcludedEntities),
        SentEvents = SplitLines(SentEvents),
        Questions = SplitLines(Questions),
        WhenStatement = WhenStatement,
        WhereStatement = WhereStatement,
        WithWhatStatement = WithWhatStatement,
        OtherInstructions = OtherInstructions,
        StartDate = Utc(StartDate),
        EndDate = Utc(EndDate)
    };

    private static string Lines(List<string>? values) => string.Join(Environment.NewLine, values ?? []);

    private static List<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The day of <paramref name="value"/> in local time; null for none (or the default date of the server).</summary>
    private static DateTime? DayOf(DateTime? value) =>
        value is { } date && date.Year > 1 ? (date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date).Date : null;

    /// <summary>The day, as midnight UTC.</summary>
    private static DateTime? Utc(DateTime? day) =>
        day is { } d ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc) : null;

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
