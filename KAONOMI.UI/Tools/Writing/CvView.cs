using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;
using KAONOMI.UI.Tools.Work;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>
/// /cv: the CVs. On the left, their search (empty: the ones changed last) and their list, a click opens one. At the
/// top, NEW, SAVE, ⧉ CLONE (a popup asks the adaptation to make: the model of the chat rewrites the copy for it),
/// DELETE, ↶ (the text before the last ✨), and the purpose given to the model. In the middle, the CV: its title, the
/// person, the summary, the skills, the experiences, the educations and the creations; each ✨ has the model write the
/// text beside it from what the CV says. On the right, the CV as it reads (📋 copies its Markdown). While the model
/// writes, the waiting popup of /work is shown. ⌘/Ctrl S saves. The title of the window is the one of the CV.
/// </summary>
[ToolView(typeof(CvWindowViewModel), "CVs", "📇", 1480, 900)]
public sealed class CvView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (CvWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;
        vm.AskAsync = (title, text, initial) => Dialogs.Ask(title, text, initial, multiline: true);

        // ── Left: the CVs ──
        var search = WritingUi.Line(vm, nameof(vm.Search), "Search the CVs (name, job, skill, employer)… · Enter");
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Ui.Run(vm.SearchCommand);
                e.Handled = true;
            }
        };
        var searchButton = WritingUi.Command("⟳", vm.SearchCommand, tooltip: "Search (Enter); empty: the CVs changed last");
        searchButton.Margin = new Thickness(6, 0, 0, 0);
        var cvs = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var noCv = WritingUi.Placeholder("No CV yet");
        noCv.VerticalAlignment = VerticalAlignment.Top;
        WritingUi.Items(cvs, vm.Cvs, cv => CvRow(vm, cv), noCv);
        var searchRow = Ui.Dock((searchButton, Dock.Right), (search, Dock.Left));
        searchRow.Margin = new Thickness(0, 0, 0, 8);
        var left = WritingUi.Page([searchRow], new Panel { Children = { Ui.Scroll(cvs), noCv } });
        left.MinWidth = 240;
        var leftHolder = WritingUi.Margin(left, 10, 10, 6, 10);

        // ── Top: the CV, its clone, the purpose given to the model ──
        var guidance = WritingUi.Line(vm, nameof(vm.Guidance), "Purpose given to ✨ (optional): the job aimed at, the tone, the language…");
        guidance.Margin = new Thickness(10, 0, 0, 0);
        ToolTip.SetTip(guidance, "What the texts are written for: given to the model of the chat with each ✨, and proposed as the adaptation of ⧉ CLONE");
        var buttons = Ui.Row(6,
            WritingUi.Command("NEW", vm.NewCvCommand, tooltip: "A new CV"),
            WritingUi.Command("SAVE", vm.SaveCommand, primary: true, tooltip: "Save the CV (a new version each time) · ⌘/Ctrl S"),
            WritingUi.Command("⧉ CLONE", vm.CloneCommand, tooltip: "Copy the CV into a new one: a popup asks the adaptation to make, and the model of the chat rewrites the copy for it (empty: a plain copy). The original is not changed"),
            WritingUi.Command("DELETE", vm.DeleteCommand, tooltip: "Delete the CV (kept marked deleted in the search engine)"),
            WritingUi.Command("↶", vm.UndoAssistCommand, tooltip: "Put back the text the last ✨ replaced"));
        var toolbar = WritingUi.Toolbar(Ui.Dock((buttons, Dock.Left), (guidance, Dock.Left)));

        // ── Middle: the CV ──
        var person = WritingUi.Cells(4,
            WritingUi.Field("Title of the CV", WritingUi.Line(vm, nameof(vm.Title), "The job aimed at: Senior .NET developer")),
            WritingUi.Field("Last name", WritingUi.Line(vm, nameof(vm.LastName), "Last name")),
            WritingUi.Field("First name", WritingUi.Line(vm, nameof(vm.FirstName), "First name")),
            WritingUi.Field("Email", WritingUi.Line(vm, nameof(vm.Email), "name@example.org")),
            WritingUi.Field("Phone", WritingUi.Line(vm, nameof(vm.Phone), "+33 …")),
            WritingUi.Field("LinkedIn profile", WritingUi.Line(vm, nameof(vm.LinkedInProfile), "https://www.linkedin.com/in/…")),
            WritingUi.Field("Number", WritingUi.Line(vm, nameof(vm.AddressNumber), "12")),
            WritingUi.Field("Street", WritingUi.Line(vm, nameof(vm.AddressStreet), "Street")),
            WritingUi.Field("Postcode", WritingUi.Line(vm, nameof(vm.AddressZip), "Postcode")),
            WritingUi.Field("City", WritingUi.Line(vm, nameof(vm.AddressCity), "City")),
            WritingUi.Field("Country", WritingUi.Line(vm, nameof(vm.AddressCountry), "Country")));

        // The photo: a round portrait, 📷 chooses its file (copied into Pictures/kaonomi/cv), ✕ takes it off.
        var portrait = new Avalonia.Controls.Image { Width = 104, Height = 104 };
        var noPortrait = Ui.Label("👤", Ui.Muted, 40);
        noPortrait.HorizontalAlignment = HorizontalAlignment.Center;
        Ui.When(portrait, vm, () =>
        {
            Avalonia.Media.Imaging.Bitmap? bitmap = null;
            if (vm.PhotoPng is { } png)
            {
                try
                {
                    using var stream = new System.IO.MemoryStream(png);
                    bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                }
                catch (Exception)
                {
                    bitmap = null;
                }
            }
            portrait.Source = bitmap;
            noPortrait.IsVisible = bitmap is null;
        }, nameof(vm.PhotoPng));
        var frame = Ui.Box(new Panel { Children = { noPortrait, portrait }, Width = 104, Height = 104 }, 4, Ui.Field);
        frame.HorizontalAlignment = HorizontalAlignment.Center;
        var choosePhoto = Ui.Button("📷 PHOTO", () => _ = ChoosePhotoAsync(vm), $"Choose the photo of the person (copied into {CvWindowViewModel.CvPicturesDirectory}); it is shown and exported as a round portrait");
        var removePhoto = WritingUi.Command("✕", vm.RemovePhotoCommand, tooltip: "Take the photo off the CV").Visible(vm, nameof(vm.HasPhoto));
        var photoBox = Ui.Column(6, frame, Ui.Row(4, choosePhoto, removePhoto));
        photoBox.Margin = new Thickness(0, 0, 14, 0);
        photoBox.VerticalAlignment = VerticalAlignment.Top;
        var personRow = Ui.Dock((photoBox, Dock.Left), (person, Dock.Left));

        var summary = WritingUi.Lines(vm, nameof(vm.Summary), 110, "The profile, in a few sentences… ✨ writes it from the CV, or rewrites what is typed here");
        var skills = WritingUi.Lines(vm, nameof(vm.Skills), 60, "The skills, a comma between them… ✨ adds the ones the experiences show");

        var experiences = new StackPanel { Spacing = 8 };
        var noExperience = Ui.Label("No experience yet: + ADD", Ui.Muted, 12);
        WritingUi.Items(experiences, vm.Experiences, item => ExperienceCard(vm, item), noExperience);
        var educations = new StackPanel { Spacing = 8 };
        var noEducation = Ui.Label("No education yet: + ADD", Ui.Muted, 12);
        WritingUi.Items(educations, vm.Educations, item => EducationCard(vm, item), noEducation);
        var creations = new StackPanel { Spacing = 8 };
        var noCreation = Ui.Label("No creation yet (a project, a product, a publication): + ADD", Ui.Muted, 12);
        WritingUi.Items(creations, vm.Creations, item => CreationCard(vm, item), noCreation);

        var form = Ui.Column(10,
            Ui.Bound(vm, nameof(vm.Info), Ui.Muted, 11),
            personRow,
            Section("Summary", WritingUi.Command("✨ WRITE", vm.AssistSummaryCommand, tooltip: "The model of the chat writes the summary from the CV (or rewrites the one typed)")),
            summary,
            Section("Skills", WritingUi.Command("✨ SUGGEST", vm.AssistSkillsCommand, tooltip: "The model of the chat adds the skills the experiences, the educations and the creations show")),
            skills,
            Section("Experiences", WritingUi.Command("+ ADD", vm.AddExperienceCommand, tooltip: "A new experience, first of the list")),
            experiences, noExperience,
            Section("Educations", WritingUi.Command("+ ADD", vm.AddEducationCommand, tooltip: "A new education, first of the list")),
            educations, noEducation,
            Section("Creations", WritingUi.Command("+ ADD", vm.AddCreationCommand, tooltip: "A new creation: a project, a product, a publication")),
            creations, noCreation);
        form.Margin = new Thickness(0, 0, 14, 0);
        var middleHolder = WritingUi.Margin(Ui.Scroll(form), 6, 10, 6, 10);

        // ── Right: the CV as it reads ──
        var copy = Ui.Button("📋 COPY", () =>
        {
            Dialogs.Copy(vm.Markdown());
            vm.Status = "The CV is on the clipboard (Markdown)";
        }, "Copy the CV (Markdown) to the clipboard");
        var word = Ui.Button("📄 WORD", () => _ = ExportAsync(vm, word: true), $"Export the CV to a Word document (.docx) in a modern look, then open it; by default in {CvExport.Folder}");
        var pdf = Ui.Button("📕 PDF", () => _ = ExportAsync(vm, word: false), $"Export the CV to a PDF in a modern look (a dark side column), then open it; by default in {CvExport.Folder}");
        var previewCaption = Ui.Dock((Ui.Row(6, word, pdf, copy), Dock.Right), (Ui.Caption("The CV as it reads", Ui.Muted, 11), Dock.Left));
        previewCaption.Margin = new Thickness(0, 0, 0, 8);
        // Its Markdown drawn again each time the CV changes.
        var previewHolder = new ContentControl { Margin = new Thickness(0, 0, 10, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Ui.When(previewHolder, vm, () => previewHolder.Content = KAONOMI.UI.Chat.MarkdownView.Build(vm.Preview, 12), nameof(vm.Preview));
        var right = WritingUi.Page([previewCaption], Ui.Scroll(previewHolder));
        var rightHolder = WritingUi.Margin(right, 6, 10, 10, 10);

        var work = WritingUi.SplitH(middleHolder, rightHolder, 0, leftStar: 1.5, rightStar: 1, minLeft: 520, minRight: 280);
        var split = WritingUi.SplitH(leftHolder, work, 280, minLeft: 220, minRight: 800);

        var dirty = Ui.Label("● not saved", WritingUi.Amber, 12).Visible(vm, nameof(vm.IsDirty));
        var status = WritingUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy), extra: [dirty]);
        // A layer over the whole window, for the waiting popup.
        var root = new Grid();
        root.Children.Add(WritingUi.Page([toolbar], split, status));

        // While the model writes: the waiting popup of /work (what is being written, the time, Cancel).
        BusyPopup? busy = null;
        Ui.When(root, vm, () =>
        {
            if (vm.IsGenerating && busy is null)
            {
                var cancel = new System.Threading.CancellationTokenSource();
                cancel.Token.Register(() => Ui.OnMain(() => Ui.Run(vm.CancelGenerationCommand)));
                busy = BusyPopup.Show(root, "The model is writing", vm.GenerationStep, cancel);
            }
            else if (!vm.IsGenerating && busy is not null)
            {
                busy.Finish();
                busy = null;
            }
            if (busy is not null && vm.GenerationStep.Length > 0)
                busy.Message = vm.GenerationStep;
        }, nameof(vm.IsGenerating), nameof(vm.GenerationStep));

        // The title of the window: the CV.
        void UpdateTitle() => WritingUi.SetTitle(root, vm.Label == "CV" ? "CVs" : $"CV · {vm.Label}");
        Ui.When(root, vm, UpdateTitle, nameof(vm.Label));
        WritingUi.WhenInWindow(root, UpdateTitle);

        WritingUi.Shortcuts(root, key => WritingUi.IsCommand(key, Key.S) && WritingUi.Run(vm.SaveCommand));
        return root;
    }

    /// <summary>📷 PHOTO: a picture picked, copied into the folder of the CVs, shown as the portrait.</summary>
    private static async System.Threading.Tasks.Task ChoosePhotoAsync(CvWindowViewModel vm)
    {
        var file = await Dialogs.OpenFile("Choose the photo of the CV", WritingUi.PictureFilters, vm.PicturesDirectory);
        if (file is not null)
            vm.SetPhoto(file);
    }

    /// <summary>📄 WORD / 📕 PDF: asks where to write the file, writes the CV open into it, and opens it.</summary>
    private static async System.Threading.Tasks.Task ExportAsync(CvWindowViewModel vm, bool word)
    {
        var format = word ? "docx" : "pdf";
        System.IO.Directory.CreateDirectory(CvExport.Folder);
        var file = await Dialogs.SaveFile(word ? "Export the CV to MS Word" : "Export the CV to PDF", $"{vm.ExportName}.{format}",
            [word ? "*.docx ; Word document" : "*.pdf ; PDF document"], CvExport.Folder);
        if (file is null)
            return;
        if (!file.EndsWith("." + format, StringComparison.OrdinalIgnoreCase))
            file += "." + format;
        if (await vm.ExportAsync(file, word))
            PictureLibrary.OpenWithSystem(file);
    }

    /// <summary>The head of a part of the CV: its name, then its button on the right.</summary>
    private static Control Section(string caption, Control button)
    {
        var row = Ui.Dock((button, Dock.Right), (Ui.Caption(caption, Ui.Accent, 12), Dock.Left));
        row.Margin = new Thickness(0, 8, 0, 0);
        return row;
    }

    /// <summary>The frame of an item of a list of the CV: its rows, then ✨ and 🗑 on the right.</summary>
    private static Control Card(Action assist, string assistTip, Action remove, string removeTip, params Control[] rows)
    {
        var buttons = Ui.Row(6, Ui.Button("✨ WRITE", assist, assistTip), Ui.Button("🗑", remove, removeTip));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var column = Ui.Column(8, rows);
        column.Children.Add(buttons);
        return WritingUi.Card(column);
    }

    /// <summary>A date as it is typed (2021-03), red while it is not understood.</summary>
    private static TextBox Date(INotifyPropertyChanged item, string name)
    {
        var box = WritingUi.Line(item, name, "2021-03");
        ToolTip.SetTip(box, "A month (2021-03), a year (2021); an end left empty: today");
        var normal = box.Foreground;
        Ui.When(box, item, () => box.Foreground = CvDates.IsValid(WritingUi.Get(item, name)?.ToString()) ? normal : Ui.Red, name);
        return box;
    }

    private static Control ExperienceCard(CvWindowViewModel vm, CvExperienceItem item) => Card(
        () => Ui.Run(vm.AssistExperienceCommand, item), "The model of the chat writes the description from the position, the technologies and the notes typed in it",
        () => Ui.Run(vm.RemoveExperienceCommand, item), "Take this experience off the CV",
        WritingUi.Cells(4,
            WritingUi.Field("Position", WritingUi.Line(item, nameof(item.Position), "Position held")),
            WritingUi.Field("Company", WritingUi.Line(item, nameof(item.Company), "Employer")),
            WritingUi.Field("From", Date(item, nameof(item.Start))),
            WritingUi.Field("To", Date(item, nameof(item.End)))),
        WritingUi.Field("Technologies", WritingUi.Line(item, nameof(item.Technologies), "A comma between them")),
        WritingUi.Field("Description", WritingUi.Lines(item, nameof(item.Description), 100, "What was done there: a few notes are enough, ✨ writes them up")));

    private static Control EducationCard(CvWindowViewModel vm, CvEducationItem item) => Card(
        () => Ui.Run(vm.AssistEducationCommand, item), "The model of the chat writes the description from the degree and the notes typed in it",
        () => Ui.Run(vm.RemoveEducationCommand, item), "Take this education off the CV",
        WritingUi.Cells(4,
            WritingUi.Field("Degree", WritingUi.Line(item, nameof(item.Degree), "Degree")),
            WritingUi.Field("Field of study", WritingUi.Line(item, nameof(item.FieldOfStudy), "Field of study")),
            WritingUi.Field("From", Date(item, nameof(item.Start))),
            WritingUi.Field("To", Date(item, nameof(item.End)))),
        WritingUi.Field("Institution", WritingUi.Line(item, nameof(item.Institution), "School, university")),
        WritingUi.Field("Description", WritingUi.Lines(item, nameof(item.Description), 60, "A speciality, a thesis, a distinction…")));

    private static Control CreationCard(CvWindowViewModel vm, CvCreationItem item) => Card(
        () => Ui.Run(vm.AssistCreationCommand, item), "The model of the chat writes the description from the title and the notes typed in it",
        () => Ui.Run(vm.RemoveCreationCommand, item), "Take this creation off the CV",
        WritingUi.Cells(2,
            WritingUi.Field("Title", WritingUi.Line(item, nameof(item.Title), "A project, a product, a publication")),
            WritingUi.Field("Link", WritingUi.Line(item, nameof(item.Url), "https://…"))),
        WritingUi.Field("Description", WritingUi.Lines(item, nameof(item.Description), 60, "What it is, the part taken in it…")));

    /// <summary>A CV of the list: its title, its date, the person and what it holds; the one open is outlined.</summary>
    private static Control CvRow(CvWindowViewModel vm, CvEntry cv)
    {
        var name = Ui.Label(cv.Label, Ui.TextBright, 13);
        var date = Ui.Label(cv.DateLabel, Ui.Muted, 10);
        date.Margin = new Thickness(6, 0, 0, 0);
        var details = WritingUi.Clamp(Ui.Label(cv.Details, Ui.Text, 11), 1);
        var content = Ui.Column(1, Ui.Dock((date, Dock.Right), (name, Dock.Left)), details);
        var row = WritingUi.ResultRow(vm, nameof(vm.CvId), () => vm.CvId == cv.Id, () => Ui.Run(vm.OpenEntryCommand, cv), content, 54);
        ToolTip.SetTip(row, cv.Adaptation.Length > 0 ? "Clone, adapted: " + cv.Adaptation : cv.Summary.Length > 0 ? cv.Summary : cv.Label);
        return row;
    }
}
