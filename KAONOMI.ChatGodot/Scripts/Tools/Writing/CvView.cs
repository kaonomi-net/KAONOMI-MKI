using System;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Work;

namespace KAONOMI.ChatGodot.Tools.Writing;

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

        var root = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };

        // The title of the window: the CV.
        void UpdateTitle()
        {
            if (!GodotObject.IsInstanceValid(root) || shell.WindowOf(root) is not { } window)
                return;
            window.Title = vm.Label == "CV" ? "CVs" : $"CV · {vm.Label}";
        }
        Bind.When(root, vm, UpdateTitle, nameof(vm.Label));
        root.Ready += () => Callable.From(UpdateTitle).CallDeferred();

        // ── Left: the CVs ──
        var search = Ui.Line(vm, nameof(vm.Search), "Search the CVs (name, job, skill, employer)… · Enter");
        search.TextSubmitted += _ => Bind.Run(vm.SearchCommand);
        var cvs = new VBoxContainer();
        cvs.AddThemeConstantOverride("separation", 2);
        var noCv = Ui.Placeholder("No CV yet");
        Bind.Items(cvs, vm.Cvs, cv => CvRow(vm, cv), noCv);
        var cvsScroll = Ui.Scroll(Ui.Column(2, cvs, noCv));
        var left = Ui.Column(8,
            Ui.Row(6, search, Ui.Command("⟳", vm.SearchCommand, tooltip: "Search (Enter); empty: the CVs changed last")),
            cvsScroll);
        left.CustomMinimumSize = new Vector2(280, 0);
        var leftHolder = Ui.Margin(left, 10, 10, 0, 10);

        // ── Top: the CV, its clone, the purpose given to the model ──
        var guidance = Ui.Line(vm, nameof(vm.Guidance), "Purpose given to ✨ (optional): the job aimed at, the tone, the language…");
        guidance.TooltipText = "What the texts are written for: given to the model of the chat with each ✨, and proposed as the adaptation of ⧉ CLONE";
        var toolbar = Ui.Toolbar(Ui.Margin(Ui.Row(6,
            Ui.Command("NEW", vm.NewCvCommand, tooltip: "A new CV"),
            Ui.Command("SAVE", vm.SaveCommand, primary: true, tooltip: "Save the CV (a new version each time) · ⌘/Ctrl S"),
            Ui.Command("⧉ CLONE", vm.CloneCommand, tooltip: "Copy the CV into a new one: a popup asks the adaptation to make, and the model of the chat rewrites the copy for it (empty: a plain copy). The original is not changed"),
            Ui.Command("DELETE", vm.DeleteCommand, tooltip: "Delete the CV (kept marked deleted in the search engine)"),
            new VSeparator(),
            Ui.Command("↶", vm.UndoAssistCommand, tooltip: "Put back the text the last ✨ replaced"),
            guidance), 6, 0, 6, 0));

        // ── Middle: the CV ──
        var person = WritingUi.Cells(4,
            Ui.Field("Title of the CV", Ui.Line(vm, nameof(vm.Title), "The job aimed at: Senior .NET developer")),
            Ui.Field("Last name", Ui.Line(vm, nameof(vm.LastName), "Last name")),
            Ui.Field("First name", Ui.Line(vm, nameof(vm.FirstName), "First name")),
            Ui.Field("Email", Ui.Line(vm, nameof(vm.Email), "name@example.org")),
            Ui.Field("Phone", Ui.Line(vm, nameof(vm.Phone), "+33 …")),
            Ui.Field("LinkedIn profile", Ui.Line(vm, nameof(vm.LinkedInProfile), "https://www.linkedin.com/in/…")),
            Ui.Field("Number", Ui.Line(vm, nameof(vm.AddressNumber), "12")),
            Ui.Field("Street", Ui.Line(vm, nameof(vm.AddressStreet), "Street")),
            Ui.Field("Postcode", Ui.Line(vm, nameof(vm.AddressZip), "Postcode")),
            Ui.Field("City", Ui.Line(vm, nameof(vm.AddressCity), "City")),
            Ui.Field("Country", Ui.Line(vm, nameof(vm.AddressCountry), "Country")));

        // The photo: a round portrait, 📷 chooses its file (copied into Pictures/kaonomi/cv), ✕ takes it off.
        var portrait = new TextureRect
        {
            CustomMinimumSize = new Vector2(104, 104),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        var noPortrait = Eve.Eve.Text_("👤", Eve.Eve.Muted, 40);
        noPortrait.HorizontalAlignment = HorizontalAlignment.Center;
        noPortrait.VerticalAlignment = VerticalAlignment.Center;
        Bind.When(portrait, vm, () =>
        {
            ImageTexture? texture = null;
            if (vm.PhotoPng is { } png)
            {
                var image = new Image();
                if (image.LoadPngFromBuffer(png) == Error.Ok)
                    texture = ImageTexture.CreateFromImage(image);
            }
            portrait.Texture = texture;
            noPortrait.Visible = texture is null;
        }, nameof(vm.PhotoPng));
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(112, 112), SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        frame.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.2f), Eve.Eve.Edge with { A = 0.3f }, 4));
        frame.AddChild(noPortrait);
        frame.AddChild(portrait);
        var choosePhoto = Ui.Action("📷 PHOTO", () => _ = ChoosePhotoAsync(vm), tooltip: $"Choose the photo of the person (copied into {CvWindowViewModel.CvPicturesDirectory}); it is shown and exported as a round portrait");
        var removePhoto = Bind.Visible(Ui.Command("✕", vm.RemovePhotoCommand, tooltip: "Take the photo off the CV"), vm, nameof(vm.HasPhoto));
        var photoBox = Ui.Column(6, frame, Ui.Row(4, choosePhoto, removePhoto));
        photoBox.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        photoBox.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        person.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var personRow = Ui.Row(14, photoBox, person);

        var summary = Ui.Lines(vm, nameof(vm.Summary), 110, "The profile, in a few sentences… ✨ writes it from the CV, or rewrites what is typed here");
        var skills = Ui.Lines(vm, nameof(vm.Skills), 60, "The skills, a comma between them… ✨ adds the ones the experiences show");

        var experiences = new VBoxContainer();
        experiences.AddThemeConstantOverride("separation", 8);
        var noExperience = Hint("No experience yet: + ADD");
        Bind.Items(experiences, vm.Experiences, item => ExperienceCard(vm, item), noExperience);
        var educations = new VBoxContainer();
        educations.AddThemeConstantOverride("separation", 8);
        var noEducation = Hint("No education yet: + ADD");
        Bind.Items(educations, vm.Educations, item => EducationCard(vm, item), noEducation);
        var creations = new VBoxContainer();
        creations.AddThemeConstantOverride("separation", 8);
        var noCreation = Hint("No creation yet (a project, a product, a publication): + ADD");
        Bind.Items(creations, vm.Creations, item => CreationCard(vm, item), noCreation);

        var form = Ui.Column(10,
            Ui.Label(vm, nameof(vm.Info), Eve.Eve.Muted, 11),
            personRow,
            Section("Summary", Ui.Command("✨ WRITE", vm.AssistSummaryCommand, tooltip: "The model of the chat writes the summary from the CV (or rewrites the one typed)")),
            summary,
            Section("Skills", Ui.Command("✨ SUGGEST", vm.AssistSkillsCommand, tooltip: "The model of the chat adds the skills the experiences, the educations and the creations show")),
            skills,
            Section("Experiences", Ui.Command("+ ADD", vm.AddExperienceCommand, tooltip: "A new experience, first of the list")),
            experiences, noExperience,
            Section("Educations", Ui.Command("+ ADD", vm.AddEducationCommand, tooltip: "A new education, first of the list")),
            educations, noEducation,
            Section("Creations", Ui.Command("+ ADD", vm.AddCreationCommand, tooltip: "A new creation: a project, a product, a publication")),
            creations, noCreation);
        var formScroll = Ui.Scroll(Ui.Margin(form, 0, 0, 12, 0));
        var middleHolder = Ui.Margin(formScroll, 10, 10, 6, 10);
        middleHolder.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        middleHolder.SizeFlagsStretchRatio = 1.5f;
        middleHolder.CustomMinimumSize = new Vector2(520, 0);

        // ── Right: the CV as it reads ──
        var copy = Ui.Action("📋 COPY", () =>
        {
            Dialogs.Copy(vm.Markdown());
            vm.Status = "The CV is on the clipboard (Markdown)";
        }, tooltip: "Copy the CV (Markdown) to the clipboard");
        var preview = Ui.Scroll(Ui.Margin(Ui.Markdown(vm, nameof(vm.Preview)), 0, 0, 10, 0));
        var word = Ui.Action("📄 WORD", () => _ = ExportAsync(vm, word: true), tooltip: $"Export the CV to a Word document (.docx) in a modern look, then open it; by default in {CvExport.Folder}");
        var pdf = Ui.Action("📕 PDF", () => _ = ExportAsync(vm, word: false), tooltip: $"Export the CV to a PDF in a modern look (a dark side column), then open it; by default in {CvExport.Folder}");
        var right = Ui.Column(8, Ui.Row(6, Eve.Eve.Caption("The CV as it reads", Eve.Eve.Muted, 11), Ui.Spring(), word, pdf, copy), preview);
        right.CustomMinimumSize = new Vector2(320, 0);
        var rightHolder = Ui.Margin(right, 6, 10, 10, 10);
        rightHolder.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var work = new HSplitContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        work.AddChild(middleHolder);
        work.AddChild(rightHolder);
        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddChild(leftHolder);
        split.AddChild(work);

        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        if (status.GetChild(0) is HBoxContainer statusRow)
            statusRow.AddChild(Bind.Visible(Eve.Eve.Text_("● not saved", Eve.Eve.Amber, 12), vm, nameof(vm.IsDirty)));

        var content = Ui.Column(0, toolbar, split, status);
        content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        root.AddChild(content);

        // While the model writes: the waiting popup of /work over the whole window (what is being written, the time, Cancel).
        BusyPopup? busy = null;
        Bind.When(root, vm, () =>
        {
            if (vm.IsGenerating && busy is null)
            {
                var cancel = new System.Threading.CancellationTokenSource();
                cancel.Token.Register(() => Bind.OnMain(() => Bind.Run(vm.CancelGenerationCommand)));
                busy = BusyPopup.Show(root, "The model is writing", vm.GenerationStep, cancel);
            }
            else if (!vm.IsGenerating && busy is not null)
            {
                busy.Finish();
                busy = null;
            }
            if (busy is not null && GodotObject.IsInstanceValid(busy) && vm.GenerationStep.Length > 0)
                busy.Message = vm.GenerationStep;
        }, nameof(vm.IsGenerating), nameof(vm.GenerationStep));

        WritingUi.Shortcuts(root, key => WritingUi.IsCommand(key, Key.S) && WritingUi.Run(vm.SaveCommand));
        return root;
    }

    /// <summary>📷 PHOTO: a picture picked, copied into the folder of the CVs, shown as the portrait.</summary>
    private static async System.Threading.Tasks.Task ChoosePhotoAsync(CvWindowViewModel vm)
    {
        var file = await Dialogs.OpenFile("Choose the photo of the CV", ["*.jpg,*.jpeg,*.png,*.webp,*.bmp,*.gif ; Pictures", "* ; All files"], vm.PicturesDirectory);
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

    /// <summary>The head of a part of the CV: its name, then its buttons on the right.</summary>
    private static Control Section(string caption, params Control[] buttons)
    {
        var row = Ui.Row(8, Eve.Eve.Caption(caption, Eve.Eve.Cyan, 12), Ui.Spring());
        foreach (var button in buttons)
            row.AddChild(button);
        return Ui.Margin(row, 0, 8, 0, 0);
    }

    private static Label Hint(string text)
    {
        var label = Eve.Eve.Text_(text, Eve.Eve.Muted, 12);
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        return label;
    }

    /// <summary>The frame of an item of a list of the CV.</summary>
    private static PanelContainer Card(params Control[] rows)
    {
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.15f), Eve.Eve.Edge with { A = 0.3f }, 8));
        card.AddChild(Ui.Column(8, rows));
        return card;
    }

    private const string DateHint = "2021-03";
    private const string DateTip = "A month (2021-03), a year (2021); an end left empty: today";

    private static LineEdit Date(System.ComponentModel.INotifyPropertyChanged item, string name)
    {
        var edit = Ui.Line(item, name, DateHint);
        edit.TooltipText = DateTip;
        // Red while it is not understood.
        Bind.When(edit, item, () =>
        {
            if (CvDates.IsValid(Bind.Get(item, name)?.ToString()))
                edit.RemoveThemeColorOverride("font_color");
            else
                edit.AddThemeColorOverride("font_color", Eve.Eve.Red);
        }, name);
        return edit;
    }

    private static Control ExperienceCard(CvWindowViewModel vm, CvExperienceItem item) => Card(
        WritingUi.Cells(4,
            Ui.Field("Position", Ui.Line(item, nameof(item.Position), "Position held")),
            Ui.Field("Company", Ui.Line(item, nameof(item.Company), "Employer")),
            Ui.Field("From", Date(item, nameof(item.Start))),
            Ui.Field("To", Date(item, nameof(item.End)))),
        Ui.Field("Technologies", Ui.Line(item, nameof(item.Technologies), "A comma between them")),
        Ui.Field("Description", Ui.Lines(item, nameof(item.Description), 100, "What was done there: a few notes are enough, ✨ writes them up")),
        Ui.Row(6, Ui.Spring(),
            Ui.Action("✨ WRITE", () => Bind.Run(vm.AssistExperienceCommand, item), tooltip: "The model of the chat writes the description from the position, the technologies and the notes typed in it"),
            Ui.Action("🗑", () => Bind.Run(vm.RemoveExperienceCommand, item), tooltip: "Take this experience off the CV")));

    private static Control EducationCard(CvWindowViewModel vm, CvEducationItem item) => Card(
        WritingUi.Cells(4,
            Ui.Field("Degree", Ui.Line(item, nameof(item.Degree), "Degree")),
            Ui.Field("Field of study", Ui.Line(item, nameof(item.FieldOfStudy), "Field of study")),
            Ui.Field("From", Date(item, nameof(item.Start))),
            Ui.Field("To", Date(item, nameof(item.End)))),
        Ui.Field("Institution", Ui.Line(item, nameof(item.Institution), "School, university")),
        Ui.Field("Description", Ui.Lines(item, nameof(item.Description), 60, "A speciality, a thesis, a distinction…")),
        Ui.Row(6, Ui.Spring(),
            Ui.Action("✨ WRITE", () => Bind.Run(vm.AssistEducationCommand, item), tooltip: "The model of the chat writes the description from the degree and the notes typed in it"),
            Ui.Action("🗑", () => Bind.Run(vm.RemoveEducationCommand, item), tooltip: "Take this education off the CV")));

    private static Control CreationCard(CvWindowViewModel vm, CvCreationItem item) => Card(
        WritingUi.Cells(2,
            Ui.Field("Title", Ui.Line(item, nameof(item.Title), "A project, a product, a publication")),
            Ui.Field("Link", Ui.Line(item, nameof(item.Url), "https://…"))),
        Ui.Field("Description", Ui.Lines(item, nameof(item.Description), 60, "What it is, the part taken in it…")),
        Ui.Row(6, Ui.Spring(),
            Ui.Action("✨ WRITE", () => Bind.Run(vm.AssistCreationCommand, item), tooltip: "The model of the chat writes the description from the title and the notes typed in it"),
            Ui.Action("🗑", () => Bind.Run(vm.RemoveCreationCommand, item), tooltip: "Take this creation off the CV")));

    /// <summary>A CV of the list: its title, its date, the person and what it holds; the one open is outlined.</summary>
    private static Control CvRow(CvWindowViewModel vm, CvEntry cv)
    {
        var name = Eve.Eve.Text_(cv.Label, Eve.Eve.TextBright, 13);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var date = Eve.Eve.Text_(cv.DateLabel, Eve.Eve.Muted, 10);
        var details = WritingUi.Clamp(Eve.Eve.Text_(cv.Details, Eve.Eve.Text with { A = 0.7f }, 11), 1);
        var button = WritingUi.ResultRow(vm, nameof(vm.CvId), () => vm.CvId == cv.Id,
            () => Bind.Run(vm.OpenEntryCommand, cv), Ui.Column(1, Ui.Row(6, name, date), details), 54);
        button.TooltipText = cv.Adaptation.Length > 0 ? "Clone, adapted: " + cv.Adaptation : cv.Summary.Length > 0 ? cv.Summary : cv.Label;
        return button;
    }
}
