using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>An experience of the CV open, as it is typed (its dates as text: <c>2021-03</c>; its technologies on one line).</summary>
public sealed partial class CvExperienceItem : ObservableObject
{
    [ObservableProperty] private string _company = string.Empty;
    [ObservableProperty] private string _position = string.Empty;
    [ObservableProperty] private string _start = string.Empty;
    [ObservableProperty] private string _end = string.Empty;
    [ObservableProperty] private string _technologies = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
}

/// <summary>An education of the CV open, as it is typed.</summary>
public sealed partial class CvEducationItem : ObservableObject
{
    [ObservableProperty] private string _institution = string.Empty;
    [ObservableProperty] private string _degree = string.Empty;
    [ObservableProperty] private string _fieldOfStudy = string.Empty;
    [ObservableProperty] private string _start = string.Empty;
    [ObservableProperty] private string _end = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
}

/// <summary>A creation of the CV open (a project, a publication, a product), as it is typed.</summary>
public sealed partial class CvCreationItem : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _url = string.Empty;
}

/// <summary>
/// The CVs (<c>/cv</c>): each one stored in the search engine (the <c>cv</c> index of the server), listed and searched
/// on the left; the one open has the person, a summary, the experiences, the educations, the skills and the creations,
/// with its reading (Markdown) beside. The model of the chat helps to write it (✨: the summary, the description of an
/// experience, an education or a creation, the skills) and rewrites a clone of it for a purpose (⧉ Clone: a popup asks
/// the adaptation to make). The assistance and the clone are in CvWindowViewModel.Assist.cs.
/// </summary>
public sealed partial class CvWindowViewModel : ViewModelBase, IUnsavedChanges, IProjectDocument
{
    /// <summary>The project of the CV open (chosen by its name in the header of the window), or null; saved with the CV.</summary>
    [ObservableProperty]
    private string? _projectId;

    /// <summary>A CV is always open (a new one when none was chosen).</summary>
    public bool HasProjectDocument => true;

    private readonly KaonomiAssistantService _server;
    private readonly OllamaModelService? _ollama;
    private readonly Func<string?>? _model;
    private readonly Func<string> _language;
    /// <summary>True while the fields are filled from a CV: no "not saved" for each of them.</summary>
    private bool _loading;
    private string _clonedFrom = string.Empty;
    private string _adaptation = string.Empty;

    /// <summary>For the designer.</summary>
    public CvWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl), null, null, null)
    {
    }

    public CvWindowViewModel(KaonomiAssistantService server, OllamaModelService? ollama, Func<string?>? model, Func<string>? language)
    {
        _server = server;
        _ollama = ollama;
        _model = model;
        _language = language ?? (() => "fr");
        Experiences.CollectionChanged += OnItemsChanged;
        Educations.CollectionChanged += OnItemsChanged;
        Creations.CollectionChanged += OnItemsChanged;
        Load(null);
        Status = "A new CV: fill it in (✨ asks the model of the chat to write a text), then SAVE; ⧉ Clone rewrites a copy of it for a purpose";
        _ = SearchAsync();
    }

    /// <summary>Asks the user (a message and the label of the button); false when not answered.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    /// <summary>Asks the user a text (the title of the popup, what is asked, the text to start from); null when cancelled. Set by the view.</summary>
    public Func<string, string, string, Task<string?>> AskAsync { get; set; } = (_, _, _) => Task.FromResult<string?>(null);

    // ── The CVs ──

    /// <summary>The CVs changed last, or the ones found, on the left.</summary>
    public ObservableCollection<CvEntry> Cvs { get; } = [];

    /// <summary>A few words looked for in the CVs; empty: the ones changed last.</summary>
    [ObservableProperty]
    private string _search = string.Empty;

    // ── The CV open ──

    [ObservableProperty]
    private string _cvId = Guid.NewGuid().ToString();

    /// <summary>True for a CV not saved yet.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private bool _isNew = true;

    /// <summary>The title of the CV: the job aimed at ("Senior .NET developer").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _lastName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _firstName = string.Empty;

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _linkedInProfile = string.Empty;
    [ObservableProperty] private string _addressStreet = string.Empty;
    [ObservableProperty] private string _addressNumber = string.Empty;
    [ObservableProperty] private string _addressZip = string.Empty;
    [ObservableProperty] private string _addressCity = string.Empty;
    [ObservableProperty] private string _addressCountry = string.Empty;

    /// <summary>The summary of the CV (the profile, a few sentences).</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>The photo of the person: the path of its file (a copy in <see cref="CvPicturesDirectory"/>); empty: none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPhoto))]
    private string _photo = string.Empty;

    /// <summary>The photo as it is shown and exported: a round portrait (PNG); null when there is none or when its file cannot be read.</summary>
    [ObservableProperty]
    private byte[]? _photoPng;

    public bool HasPhoto => Photo.Length > 0;

    partial void OnPhotoChanged(string value) => PhotoPng = CvExport.Portrait(value);

    /// <summary>Folder of the photos of the CVs: the photo chosen is copied there (Pictures/kaonomi/cv).</summary>
    public static string CvPicturesDirectory => System.IO.Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "cv");

    /// <summary>Folder the picker of the photo opens in.</summary>
    public string PicturesDirectory => ChatSettings.SystemPicturesDirectory;

    /// <summary>The photo of the CV: <paramref name="file"/> is copied into the folder of the CVs and shown as a round portrait.</summary>
    public void SetPhoto(string file)
    {
        if (CvExport.Portrait(file) is null)
        {
            SetStatus($"\"{System.IO.Path.GetFileName(file)}\" is not a picture that can be read (JPEG, PNG, WebP, BMP, GIF)", true);
            return;
        }
        try
        {
            System.IO.Directory.CreateDirectory(CvPicturesDirectory);
            var copy = System.IO.Path.Combine(CvPicturesDirectory, $"{CvId[..Math.Min(8, CvId.Length)]}-{DateTime.Now:yyyyMMdd-HHmmss}{System.IO.Path.GetExtension(file).ToLowerInvariant()}");
            System.IO.File.Copy(file, copy, overwrite: true);
            Photo = copy;
            SetStatus($"Photo copied into {CvPicturesDirectory}: SAVE keeps it with the CV", false);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            SetStatus("The photo could not be copied: " + ex.Message, true);
        }
    }

    /// <summary>Takes the photo off the CV (its file stays in the folder).</summary>
    [RelayCommand]
    private void RemovePhoto()
    {
        if (Photo.Length == 0)
            return;
        Photo = string.Empty;
        SetStatus("Photo taken off the CV", false);
    }

    /// <summary>The skills, on one text: commas (or lines) between them.</summary>
    [ObservableProperty]
    private string _skills = string.Empty;

    public ObservableCollection<CvExperienceItem> Experiences { get; } = [];

    public ObservableCollection<CvEducationItem> Educations { get; } = [];

    public ObservableCollection<CvCreationItem> Creations { get; } = [];

    /// <summary>Its version, its dates, and the CV it was cloned from.</summary>
    [ObservableProperty]
    private string _info = string.Empty;

    /// <summary>The CV as it reads (Markdown), beside the form.</summary>
    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>The name of the CV in the messages and in the title of the window.</summary>
    public string Label => Title.Trim().Length > 0 ? Title.Trim() : PersonName.Length > 0 ? PersonName : "CV";

    private string PersonName => (LastName.Trim() + " " + FirstName.Trim()).Trim();

    private static readonly HashSet<string> Fields =
    [
        nameof(Title), nameof(LastName), nameof(FirstName), nameof(Email), nameof(Phone), nameof(LinkedInProfile), nameof(AddressStreet),
        nameof(AddressNumber), nameof(AddressZip), nameof(AddressCity), nameof(AddressCountry), nameof(Summary), nameof(Skills), nameof(Photo),
        nameof(ProjectId)
    ];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is { } name && Fields.Contains(name))
            Changed();
    }

    /// <summary>Something of the CV open changed: it is not saved any more, and its reading follows.</summary>
    private void Changed()
    {
        if (_loading)
            return;
        IsDirty = true;
        Preview = ToMarkdown(Snapshot());
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Changed();

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in e.OldItems?.OfType<INotifyPropertyChanged>() ?? [])
            item.PropertyChanged -= OnItemChanged;
        foreach (var item in e.NewItems?.OfType<INotifyPropertyChanged>() ?? [])
            item.PropertyChanged += OnItemChanged;
        Changed();
    }

    private void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }

    // ── Search, open, new, save, delete ──

    /// <summary>The CVs changed last (empty search), or the ones found.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        try
        {
            var term = Search.Trim();
            var found = term.Length == 0 ? await _server.GetLastCvsAsync(300) : await _server.SearchCvsAsync(term, 100);
            Cvs.Clear();
            foreach (var cv in found)
                Cvs.Add(cv);
            if (term.Length > 0)
                SetStatus($"{Cvs.Count} CV(s) found for \"{term}\"", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The CVs could not be read: " + ex.Message, true);
        }
    }

    /// <summary>True (with the reason in the status) while the model is writing: the CV open cannot be left.</summary>
    private bool Held(string what)
    {
        if (!IsGenerating)
            return false;
        SetStatus($"The model is writing: wait for it (or cancel it), then {what}", true);
        return true;
    }

    /// <summary>Opens the CV <paramref name="id"/>.</summary>
    public async Task OpenCvAsync(string id)
    {
        if (Held("open another CV"))
            return;
        if (IsDirty && !await ConfirmAsync($"The CV \"{Label}\" is not saved. Open the other one anyway?", "Open"))
            return;
        IsBusy = true;
        try
        {
            var cv = await _server.GetCvAsync(id);
            if (cv is null)
            {
                SetStatus("This CV does not exist any more", true);
                return;
            }
            Load(cv);
            SetStatus($"CV \"{Label}\" (version {cv.Version})", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The CV could not be read: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenEntryAsync(CvEntry? cv) => cv?.Id is { } id ? OpenCvAsync(id) : Task.CompletedTask;

    private void Load(CvEntry? cv)
    {
        _loading = true;
        try
        {
            CvId = cv?.Id ?? Guid.NewGuid().ToString();
            IsNew = cv is null;
            ProjectId = cv?.ProjectId;
            Title = cv?.Title ?? string.Empty;
            LastName = cv?.LastName ?? string.Empty;
            FirstName = cv?.FirstName ?? string.Empty;
            Email = cv?.Email ?? string.Empty;
            Phone = cv?.Phone ?? string.Empty;
            LinkedInProfile = cv?.LinkedInProfile ?? string.Empty;
            AddressStreet = cv?.AddressStreet ?? string.Empty;
            AddressNumber = cv?.AddressNumber ?? string.Empty;
            AddressZip = cv?.AddressZip ?? string.Empty;
            AddressCity = cv?.AddressCity ?? string.Empty;
            AddressCountry = cv?.AddressCountry ?? string.Empty;
            Summary = cv?.Summary ?? string.Empty;
            Photo = cv?.Photo ?? string.Empty;
            Skills = string.Join(", ", cv?.Skills ?? []);
            Experiences.Clear();
            foreach (var e in cv?.Experiences ?? [])
                Experiences.Add(new CvExperienceItem
                {
                    Company = e.Company ?? string.Empty,
                    Position = e.Position ?? string.Empty,
                    Start = CvDates.Format(e.StartDate),
                    End = CvDates.Format(e.EndDate),
                    Technologies = string.Join(", ", e.Technologies ?? []),
                    Description = e.Description ?? string.Empty
                });
            Educations.Clear();
            foreach (var e in cv?.Educations ?? [])
                Educations.Add(new CvEducationItem
                {
                    Institution = e.Institution ?? string.Empty,
                    Degree = e.Degree ?? string.Empty,
                    FieldOfStudy = e.FieldOfStudy ?? string.Empty,
                    Start = CvDates.Format(e.StartDate),
                    End = CvDates.Format(e.EndDate),
                    Description = e.Description ?? string.Empty
                });
            Creations.Clear();
            foreach (var c in cv?.Creations ?? [])
                Creations.Add(new CvCreationItem { Title = c.Title ?? string.Empty, Description = c.Description ?? string.Empty, Url = c.Url ?? string.Empty });
            _clonedFrom = cv?.ClonedFrom ?? string.Empty;
            _adaptation = cv?.Adaptation ?? string.Empty;
            ForgetUndo();
            SetInfo(cv);
            IsDirty = false;
        }
        finally
        {
            _loading = false;
        }
        Preview = ToMarkdown(Snapshot());
    }

    private void SetInfo(CvEntry? cv) =>
        Info = cv is null
            ? "new CV · not saved"
            : $"version {cv.Version} · {cv.DateLabel}"
              + (cv.Created is { } created ? $" · created {created.ToLocalTime():yyyy-MM-dd}" : string.Empty)
              + (cv.ClonedFrom.Length > 0 ? " · ⧉ clone" + (cv.Adaptation.Length > 0 ? $": {Cut(cv.Adaptation, 90)}" : string.Empty) : string.Empty);

    private static string Cut(string text, int length)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= length ? text : text[..length].TrimEnd() + "…";
    }

    /// <summary>The CV open, as it is typed.</summary>
    private CvEntry Snapshot() => new()
    {
        Id = CvId,
        AppName = "KAONOMI.Chat",
        ProjectId = ProjectId,
        MachineName = Environment.MachineName,
        UserName = Environment.UserName,
        Title = Title.Trim(),
        LastName = LastName.Trim(),
        FirstName = FirstName.Trim(),
        Email = Email.Trim(),
        Phone = Phone.Trim(),
        LinkedInProfile = LinkedInProfile.Trim(),
        AddressStreet = AddressStreet.Trim(),
        AddressNumber = AddressNumber.Trim(),
        AddressZip = AddressZip.Trim(),
        AddressCity = AddressCity.Trim(),
        AddressCountry = AddressCountry.Trim(),
        Summary = Summary.Trim(),
        Photo = Photo,
        Skills = CvDates.Split(Skills),
        Experiences = Experiences.Select(e => new CvExperienceEntry
        {
            Company = e.Company.Trim(),
            Position = e.Position.Trim(),
            StartDate = CvDates.Parse(e.Start),
            EndDate = CvDates.Parse(e.End),
            Technologies = CvDates.Split(e.Technologies),
            Description = e.Description.Trim()
        }).ToList(),
        Educations = Educations.Select(e => new CvEducationEntry
        {
            Institution = e.Institution.Trim(),
            Degree = e.Degree.Trim(),
            FieldOfStudy = e.FieldOfStudy.Trim(),
            StartDate = CvDates.Parse(e.Start),
            EndDate = CvDates.Parse(e.End),
            Description = e.Description.Trim()
        }).ToList(),
        Creations = Creations.Select(c => new CvCreationEntry { Title = c.Title.Trim(), Description = c.Description.Trim(), Url = c.Url.Trim() }).ToList(),
        ClonedFrom = _clonedFrom,
        Adaptation = _adaptation
    };

    /// <summary>The first date of the CV that is not understood ("" when all are).</summary>
    private string BadDate()
    {
        foreach (var e in Experiences)
            if (!CvDates.IsValid(e.Start) || !CvDates.IsValid(e.End))
                return $"the experience \"{(e.Company.Trim().Length > 0 ? e.Company.Trim() : e.Position.Trim())}\"";
        foreach (var e in Educations)
            if (!CvDates.IsValid(e.Start) || !CvDates.IsValid(e.End))
                return $"the education \"{(e.Institution.Trim().Length > 0 ? e.Institution.Trim() : e.Degree.Trim())}\"";
        return string.Empty;
    }

    /// <summary>A new CV.</summary>
    [RelayCommand]
    private async Task NewCvAsync()
    {
        if (Held("start a new CV"))
            return;
        if (IsDirty && !await ConfirmAsync($"The CV \"{Label}\" is not saved. Start a new one anyway?", "New CV"))
            return;
        Load(null);
        SetStatus("New CV: fill it in, then SAVE", false);
    }

    /// <summary>Saves the CV (created, or a new version of it).</summary>
    [RelayCommand]
    private Task SaveAsync() => SaveEntryAsync(Snapshot(), quiet: false);

    /// <summary>Saves <paramref name="entry"/> (the CV open, or the clone that takes its place) and shows it as saved.</summary>
    private async Task<bool> SaveEntryAsync(CvEntry entry, bool quiet)
    {
        if (BadDate() is { Length: > 0 } bad && entry.Id == CvId)
        {
            SetStatus($"A date of {bad} is not understood: write a month (2021-03), a year (2021), or leave it empty", true);
            return false;
        }
        IsBusy = true;
        try
        {
            var result = await _server.SaveCvAsync(entry);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("The CV was not saved: " + (result.Message ?? "no answer"), true);
                return false;
            }
            // Read back: its version, its title when the server gave it one.
            var saved = await _server.GetCvAsync(entry.Id!);
            if (entry.Id == CvId)
            {
                IsNew = false;
                IsDirty = false;
                SetInfo(saved);
            }
            if (!quiet)
                SetStatus($"CV \"{Label}\" saved", false);
            ActivityLog.Info("Cv", $"CV {entry.Id} saved ({entry.Experiences.Count} experience(s), {entry.Educations.Count} education(s), {entry.Skills.Count} skill(s))");
            await SearchAsync();
            // The search engine may not show it yet: the CV saved is in the list at once, a new row for its new version.
            if (saved is not null && Search.Trim().Length == 0)
            {
                foreach (var former in Cvs.Where(c => c.Id == saved.Id).ToList())
                    Cvs.Remove(former);
                Cvs.Insert(0, saved);
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The CV was not saved: " + ex.Message, true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDelete() => !IsNew;

    /// <summary>Marks the CV deleted (kept in the search engine), then starts a new one.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (Held("delete the CV"))
            return;
        if (!await ConfirmAsync($"Delete the CV \"{Label}\"? It leaves the list (kept, marked deleted, in the search engine).", "Delete"))
            return;
        try
        {
            var result = await _server.DeleteCvAsync(CvId);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("The CV was not deleted: " + (result.Message ?? "no answer"), true);
                return;
            }
            ActivityLog.Info("Cv", $"CV {CvId} marked deleted");
            var label = Label;
            Load(null);
            await SearchAsync();
            SetStatus($"CV \"{label}\" deleted", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The CV was not deleted: " + ex.Message, true);
        }
    }

    // ── The lists of the CV ──

    [RelayCommand]
    private void AddExperience() => Experiences.Insert(0, new CvExperienceItem());

    [RelayCommand]
    private void RemoveExperience(CvExperienceItem? item)
    {
        if (item is not null)
            Experiences.Remove(item);
    }

    [RelayCommand]
    private void AddEducation() => Educations.Insert(0, new CvEducationItem());

    [RelayCommand]
    private void RemoveEducation(CvEducationItem? item)
    {
        if (item is not null)
            Educations.Remove(item);
    }

    [RelayCommand]
    private void AddCreation() => Creations.Add(new CvCreationItem());

    [RelayCommand]
    private void RemoveCreation(CvCreationItem? item)
    {
        if (item is not null)
            Creations.Remove(item);
    }

    // ── The CV as it reads ──

    /// <summary>The CV in Markdown: what the reading beside the form shows, what 📋 copies, and what the model is given to read.</summary>
    public static string ToMarkdown(CvEntry cv)
    {
        var text = new StringBuilder();
        var person = (cv.FirstName + " " + cv.LastName).Trim();
        text.Append("# ").Append(person.Length > 0 ? person : cv.Title.Length > 0 ? cv.Title : "CV").Append("\n\n");
        if (cv.Title.Length > 0 && person.Length > 0)
            text.Append("**").Append(cv.Title).Append("**\n\n");
        var street = (cv.AddressNumber + " " + cv.AddressStreet).Trim();
        var city = (cv.AddressZip + " " + cv.AddressCity).Trim();
        var contact = new[] { cv.Email, cv.Phone, cv.LinkedInProfile, string.Join(", ", new[] { street, city, cv.AddressCountry }.Where(p => p.Length > 0)) }
            .Where(p => p.Length > 0).ToList();
        if (contact.Count > 0)
            text.Append(string.Join(" · ", contact)).Append("\n\n");
        if (cv.Summary.Length > 0)
            text.Append("## Summary\n\n").Append(cv.Summary).Append("\n\n");
        if (cv.Skills.Count > 0)
            text.Append("## Skills\n\n").Append(string.Join(" · ", cv.Skills)).Append("\n\n");
        if (cv.Experiences.Count > 0)
        {
            text.Append("## Experience\n\n");
            foreach (var e in cv.Experiences)
            {
                var head = string.Join(" — ", new[] { e.Position, e.Company }.Where(p => p.Length > 0));
                text.Append("### ").Append(head.Length > 0 ? head : "…").Append('\n');
                var period = CvDates.Period(CvDates.Format(e.StartDate), CvDates.Format(e.EndDate));
                if (period.Length > 0)
                    text.Append('*').Append(period).Append("*\n");
                text.Append('\n');
                if (e.Description.Length > 0)
                    text.Append(e.Description).Append("\n\n");
                if (e.Technologies.Count > 0)
                    text.Append("Technologies: ").Append(string.Join(", ", e.Technologies)).Append("\n\n");
            }
        }
        if (cv.Educations.Count > 0)
        {
            text.Append("## Education\n\n");
            foreach (var e in cv.Educations)
            {
                var degree = string.Join(", ", new[] { e.Degree, e.FieldOfStudy }.Where(p => p.Length > 0));
                var head = string.Join(" — ", new[] { degree, e.Institution }.Where(p => p.Length > 0));
                text.Append("### ").Append(head.Length > 0 ? head : "…").Append('\n');
                var period = CvDates.Period(CvDates.Format(e.StartDate), CvDates.Format(e.EndDate));
                if (period.Length > 0)
                    text.Append('*').Append(period).Append("*\n");
                text.Append('\n');
                if (e.Description.Length > 0)
                    text.Append(e.Description).Append("\n\n");
            }
        }
        if (cv.Creations.Count > 0)
        {
            text.Append("## Creations\n\n");
            foreach (var c in cv.Creations)
            {
                text.Append("### ").Append(c.Title.Length > 0 ? c.Title : "…").Append("\n\n");
                if (c.Description.Length > 0)
                    text.Append(c.Description).Append("\n\n");
                if (c.Url.Length > 0)
                    text.Append(c.Url).Append("\n\n");
            }
        }
        return text.ToString().TrimEnd();
    }
}
