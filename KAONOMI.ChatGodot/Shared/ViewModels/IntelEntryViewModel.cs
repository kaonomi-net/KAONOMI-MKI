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
        nameof(AppName), nameof(MachineName), nameof(UserName), nameof(Comment), nameof(IntelTitle),
        nameof(IncludedEntities), nameof(ExcludedEntities), nameof(TriggerLevel), nameof(Instructions), nameof(IsFuzzy),
        nameof(Locations), nameof(Times), nameof(RelatedEntities), nameof(Questions),
        nameof(Tags), nameof(Events), nameof(Signals), nameof(ProjectId)
    ];

    /// <summary>The project the intel is attached to (the id of a project, chosen by its name in the header of the window), or null.</summary>
    [ObservableProperty]
    private string? _projectId;

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
        TriggerLevel = 50
    }, assistant);

    /// <summary>Copy of the values currently displayed, without id, created on the server (<c>PUT /intel</c>) on first save.</summary>
    public IntelEntryViewModel Clone()
    {
        var copy = ToEntry();
        copy.Id = null;
        copy.Date = DateTimeOffset.Now;
        copy.Title = string.IsNullOrWhiteSpace(copy.Title) ? copy.Title : copy.Title + " (copy)";
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
    public string Title => string.IsNullOrWhiteSpace(IntelTitle) ? "(untitled)" : IntelTitle;

    /// <summary>Compact summary line for list rows: date, trigger level, fuzzy.</summary>
    public string Summary => Join(" · ", DateLabel, $"triggers at {TriggerLevel:0} %", IsFuzzy ? "fuzzy" : null);

    /// <summary>The entities the intel looks for, in one line, for the list rows.</summary>
    public string Description => string.Join(", ", ParseEntities(IncludedEntities).Select(e => e.EntityName));

    /// <summary>True when the intel looks for at least one entity.</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>True when at least one tag line is present.</summary>
    public bool HasTags => !string.IsNullOrWhiteSpace(Tags);

    /// <summary>Comma-separated tags for compact list display.</summary>
    public string TagsPreview => string.Join(", ", SplitLines(Tags));

    [ObservableProperty]
    private string _appName = string.Empty;

    [ObservableProperty]
    private string _machineName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _userName = string.Empty;

    [ObservableProperty]
    private string _comment = string.Empty;

    /// <summary>The title of the intel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _intelTitle = string.Empty;

    // ── If ──

    /// <summary>The entities that must be there, one per line: "Entity = weight" (the weight may be left out).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description), nameof(HasDescription))]
    private string _includedEntities = string.Empty;

    /// <summary>The entities that must not be there, one per line: "Entity = weight".</summary>
    [ObservableProperty]
    private string _excludedEntities = string.Empty;

    /// <summary>The level at which the intel triggers, 0 to 100 %.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private double _triggerLevel;

    [ObservableProperty]
    private string _instructions = string.Empty;

    /// <summary>True when the entities may match loosely.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _isFuzzy;

    // ── Then ──

    /// <summary>One place per line.</summary>
    [ObservableProperty]
    private string _locations = string.Empty;

    /// <summary>One time per line.</summary>
    [ObservableProperty]
    private string _times = string.Empty;

    /// <summary>One entity per line.</summary>
    [ObservableProperty]
    private string _relatedEntities = string.Empty;

    /// <summary>One question per line.</summary>
    [ObservableProperty]
    private string _questions = string.Empty;

    // ── Metadata ──

    /// <summary>One tag per line (#TOPIC; the spaces of a line become '-', see <see cref="MetaTags"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTags), nameof(TagsPreview))]
    private string _tags = string.Empty;

    /// <summary>One event per line (@EVENT).</summary>
    [ObservableProperty]
    private string _events = string.Empty;

    /// <summary>One signal per line (!SIGNAL).</summary>
    [ObservableProperty]
    private string _signals = string.Empty;

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
                ActivityLog.Info("Intel", $"Intel {(isNew ? "created" : "updated")}: {_entry.Id} ({IntelTitle})");
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
            IntelTitle = entry.Title ?? string.Empty;
            IncludedEntities = FormatEntities(entry.IncludedEntities);
            ExcludedEntities = FormatEntities(entry.ExcludedEntities);
            TriggerLevel = Math.Clamp(entry.TriggerLevel, 0, 100);
            Instructions = entry.Instructions ?? string.Empty;
            IsFuzzy = entry.IsFuzzy;
            Locations = Lines(entry.Locations);
            Times = Lines(entry.Times);
            RelatedEntities = Lines(entry.RelatedEntities);
            Questions = Lines(entry.Questions);
            Tags = Lines(entry.Tags);
            Events = Lines(entry.Events);
            Signals = Lines(entry.Signals);
            ProjectId = entry.ProjectId;
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
        ProjectId = ProjectId,
        MachineName = MachineName,
        UserName = UserName,
        Comment = Comment,
        Title = IntelTitle.Trim(),
        IncludedEntities = ParseEntities(IncludedEntities),
        ExcludedEntities = ParseEntities(ExcludedEntities),
        TriggerLevel = (int)Math.Round(Math.Clamp(TriggerLevel, 0, 100)),
        Instructions = Instructions,
        IsFuzzy = IsFuzzy,
        Locations = SplitLines(Locations),
        Times = SplitLines(Times),
        RelatedEntities = SplitLines(RelatedEntities),
        Questions = SplitLines(Questions),
        Tags = MetaTags.Normalize(SplitLines(Tags), MetaTags.Tag),
        Events = MetaTags.Normalize(SplitLines(Events), MetaTags.Event),
        Signals = MetaTags.Normalize(SplitLines(Signals), MetaTags.Signal)
    };

    /// <summary>The entities, one per line: "Entity = weight", or "Entity" alone when it has no weight.</summary>
    private static string FormatEntities(List<IntelEntityWeight>? entities) =>
        string.Join(Environment.NewLine, (entities ?? []).Where(e => !string.IsNullOrWhiteSpace(e.EntityName))
            .Select(e => string.IsNullOrWhiteSpace(e.EntityWeight) ? e.EntityName : $"{e.EntityName} = {e.EntityWeight}"));

    /// <summary>The entities of the lines "Entity = weight" (the weight after the last '='; none when there is no '=').</summary>
    private static List<IntelEntityWeight> ParseEntities(string text) =>
        SplitLines(text).Select(line =>
        {
            var at = line.LastIndexOf('=');
            return at < 0
                ? new IntelEntityWeight { EntityName = line }
                : new IntelEntityWeight { EntityName = line[..at].Trim(), EntityWeight = line[(at + 1)..].Trim() };
        }).Where(e => e.EntityName.Length > 0).ToList();

    private static string Lines(List<string>? values) => string.Join(Environment.NewLine, values ?? []);

    private static List<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
