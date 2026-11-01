using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.Chat.Services;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Work;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// /write: the ghost writer, opened at full size. Header: the title of the writing, NEW, ☁ SAVE, AUTOSAVE (every
/// minute while it has changes). Four tabs:
/// GENERAL (title, author, style, description, goals, audience, and the writings of the search engine), ACTORS,
/// LOCATIONS and CHAPTERS (a list on the left, the fields of the one selected on the right). A double-click on a
/// chapter (or ✍ WRITE) opens its own window, resizable, where its text is written (<see cref="ChapterTextView"/>).
/// </summary>
[ToolView(typeof(GhostWriterWindowViewModel), "Ghost writer", "✍", 1400, 900)]
public sealed class GhostWriterView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (GhostWriterWindowViewModel)viewModel;
        void Write(WriterChapter chapter) => OpenChapter(shell, vm, chapter);

        // ── The header ──
        var name = EveStyle.Text_("", EveStyle.Muted, 11);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Bind.When(name, vm, () => name.Text = (vm.Title.Trim().Length > 0 ? vm.Title.Trim() : "Untitled writing") + (vm.IsDirty ? " •" : ""),
            nameof(vm.Title), nameof(vm.IsDirty));

        var header = Ui.Toolbar(Ui.Row(8,
            EveStyle.Caption("Ghost writer", EveStyle.TextBright, 14),
            name,
            Ui.Action("✚ NEW", () => _ = NewAsync(vm), tooltip: "A new, empty writing (the changes not saved are asked about)"),
            Ui.Command("☁ SAVE", vm.SaveToServerCommand, primary: true, tooltip: "Save the writing in the search engine: a new document the first time (its id the slug of the title), a new version after"),
            Ui.Check("AUTOSAVE", vm, nameof(vm.AutoSave), "Save the writing in the search engine every minute while it has changes")));

        // ── The tabs ──
        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        AddTab(tabs, "General", "📖 GENERAL", BuildGeneral(vm));
        AddTab(tabs, "Actors", "🎭 ACTORS", BuildActors(vm));
        AddTab(tabs, "Locations", "🗺 LOCATIONS", BuildLocations(vm));
        AddTab(tabs, "Chapters", "📑 CHAPTERS", BuildChapters(vm, Write));
        AddTab(tabs, "Publish", "🚀 PUBLISH", BuildPublish(vm));
        AddTab(tabs, "Journal", "📜 JOURNAL", BuildJournal(vm));
        var journalTab = tabs.GetTabCount() - 1;
        // The journal read again when its tab shows, and when an entry of this writing arrives while it shows.
        tabs.TabChanged += tab =>
        {
            if (tab == journalTab)
                Bind.Run(vm.RefreshJournalCommand);
        };
        void JournalAdded(WriterJournalEntry entry) => Bind.OnMain(() =>
        {
            if (GodotObject.IsInstanceValid(tabs) && tabs.CurrentTab == journalTab && entry.WritingId == vm.JournalWritingId)
                Bind.Run(vm.RefreshJournalCommand);
        });
        WriterJournal.Added += JournalAdded;
        Bind.OnGone(tabs, () => WriterJournal.Added -= JournalAdded);

        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        var summary = Ui.Label(vm, nameof(vm.Summary), EveStyle.Muted, 11);
        summary.HorizontalAlignment = HorizontalAlignment.Right;
        status.GetChild<HBoxContainer>(0).AddChild(summary);

        var page = Ui.Column(0, header, Ui.FillHeight(Ui.Margin(tabs, 8, 8, 8, 8)), status);
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var root = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(page);

        // AUTOSAVE: every minute, the writing is saved when the box is ticked and it has changes.
        var autoSave = new Godot.Timer { WaitTime = 60, Autostart = true };
        autoSave.Timeout += vm.AutoSaveTick;
        root.AddChild(autoSave);

        // The text windows of the chapters close when the chapters are replaced (a new writing, another one opened).
        void CloseChapters() => Bind.OnMain(() =>
        {
            foreach (var window in shell.Deck.Windows.Where(w => w.Key.StartsWith(ChapterTextWindowViewModel.KeyPrefix, StringComparison.Ordinal)).ToList())
                if (window.ViewModel is ChapterTextWindowViewModel text && ReferenceEquals(text.Writing, vm))
                    shell.Deck.Close(window);
        });
        // A chapter removed: its text window closes (what would be typed there would go nowhere).
        void CloseChapter(WriterChapter chapter) => Bind.OnMain(() =>
        {
            if (shell.Deck.Find(ChapterTextWindowViewModel.KeyOf(chapter)) is { ViewModel: ChapterTextWindowViewModel text } window
                && ReferenceEquals(text.Chapter, chapter))
                shell.Deck.Close(window);
        });
        vm.ChaptersReplaced += CloseChapters;
        vm.ChapterRemoved += CloseChapter;
        vm.ConfirmAsync = WritingUi.Confirm;
        // A draft left by a window closed (or a chat stopped) with changes not saved: offered back once the window shows.
        // A block body: "() => _ = …" would return the task, which Godot cannot turn into a Variant.
        root.Ready += () => Callable.From(() => { _ = vm.OfferDraftAsync(); }).CallDeferred();
        Bind.OnGone(root, () =>
        {
            vm.ChaptersReplaced -= CloseChapters;
            vm.ChapterRemoved -= CloseChapter;
            CloseChapters();
        });

        Bind.When(root, vm, () =>
        {
            if (shell.WindowOf(root) is { } window)
                window.Title = "Ghost writer · " + (vm.Title.Trim().Length > 0 ? vm.Title.Trim() : "Untitled writing") + (vm.IsDirty ? " •" : "");
        }, nameof(vm.Title), nameof(vm.IsDirty));
        // Opened at full size: the whole desktop, between the bar of commands and the chat.
        root.Ready += () => Callable.From(() =>
        {
            if (shell.WindowOf(root) is { IsMaximized: false } window)
                shell.Deck.Maximize(window);
        }).CallDeferred();
        return root;
    }

    private static void AddTab(TabContainer tabs, string name, string title, Control content)
    {
        content.Name = name;
        tabs.AddChild(Ui.Margin(content, 10));
        tabs.SetTabTitle(tabs.GetTabCount() - 1, title);
    }

    // ── 1. General ──

    private static Control BuildGeneral(GhostWriterWindowViewModel vm)
    {
        var fields = Ui.Column(10,
            WritingUi.Cells(2,
                Ui.Field("Title", Ui.Line(vm, nameof(vm.Title), "The title of the writing (its name in the search engine)")),
                Ui.Field("Author", Ui.Line(vm, nameof(vm.Author), "Who writes it"))),
            Ui.Field("Style", Ui.Line(vm, nameof(vm.Style), "The style: tone, tense, point of view, references…")),
            Area(vm, "Description", Ui.Lines(vm, nameof(vm.Description), 140, "What the writing is about: its subject, its story in a few lines")),
            Area(vm, "Goals", Ui.Lines(vm, nameof(vm.Goals), 110, "What the writing must achieve: what the reader should feel, learn, do")),
            Area(vm, "Audience", Ui.Lines(vm, nameof(vm.AudienceDescription), 110, "Who it is written for: age, culture, what they know already")));

        // The writings of the search engine.
        var engine = Ui.Label(vm, nameof(vm.WritingInfo), EveStyle.Muted, 11);
        var search = Ui.Line(vm, nameof(vm.Search), "Search the writings (title, description, goals…) · Enter");
        search.TextSubmitted += _ => Bind.Run(vm.SearchCommand);
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var none = Ui.Placeholder("No writing in the search engine yet");
        Bind.Items(list, vm.Writings, entry => WritingRow(vm, entry), none);
        var side = Ui.Column(8,
            EveStyle.Caption("SEARCH ENGINE", EveStyle.Cyan, 11),
            engine,
            Ui.Row(6, search, Ui.Command("⟳", vm.SearchCommand, tooltip: "Search (Enter); empty: every writing, by title")),
            Ui.Scroll(Ui.Column(2, list, none)));
        side.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        side.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        side.CustomMinimumSize = new Vector2(420, 0);

        // The fields take two thirds of the width, the search engine one third (420 px at least); the bar between them moves.
        var form = Ui.Scroll(fields);
        form.SizeFlagsStretchRatio = 2;
        return Ui.SplitH(form, side, 0);
    }

    /// <summary>A writing of the search engine: its title, its version and date, its description; the one open is outlined.</summary>
    private static Control WritingRow(GhostWriterWindowViewModel vm, GhostWriterEntry entry)
    {
        var title = EveStyle.Text_(entry.Title, EveStyle.TextBright, 13);
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var date = EveStyle.Text_($"v{entry.Version} · {entry.DateLabel}", EveStyle.Muted, 10);
        var description = WritingUi.Clamp(EveStyle.Text_(entry.Description, EveStyle.Text with { A = 0.7f }, 11), 1);
        var content = Ui.Column(1, Ui.Row(6, title, date), description);
        var button = WritingUi.ResultRow(vm, nameof(vm.WritingId), () => vm.WritingId is not null && vm.WritingId == entry.Id,
            () => _ = OpenAsync(vm, entry), content, 50);
        button.TooltipText = entry.Description.Length > 0 ? $"{entry.Title}\n{entry.Description}" : entry.Title;
        return button;
    }

    // ── 2. Actors ──

    private static Control BuildActors(GhostWriterWindowViewModel vm) =>
        Master(vm, vm.Actors, nameof(vm.SelectedActor), a => a.Name, nameof(WriterActor.Name),
            a => string.Join(" · ", new[] { a.Disqualified ? "⛔ disqualified" : "", a.IsMainCharacter ? "★ main character" : "" }.Where(s => s.Length > 0)),
            () => vm.SelectedActor, a => vm.SelectedActor = a, "No actor yet: ✚ ADD",
            [Ui.Command("✚ ADD", vm.AddActorCommand, primary: true, tooltip: "A new actor"),
                Ui.Command("⛔ DISQUALIFIED", vm.DisqualifyActorCommand, tooltip: "Disqualify the actor selected: kept in the writing, marked disqualified (untick Disqualified to bring them back)")],
            Ui.Swap(vm, nameof(vm.SelectedActor), value => value is WriterActor actor ? ActorEditor(vm, actor) : Ui.Placeholder("Select an actor, or add one")),
            null);

    private static Control ActorEditor(GhostWriterWindowViewModel vm, WriterActor a)
    {
        var iq = Bind.Number(new SpinBox { MinValue = 40, MaxValue = 220, Step = 1 }, a, nameof(a.HisIQ));
        var eq = Bind.Number(new SpinBox { MinValue = 40, MaxValue = 220, Step = 1 }, a, nameof(a.HisEQ));
        var fields = Ui.Column(10,
            WritingUi.Cells(3,
                Ui.Field("Name", Ui.Line(a, nameof(a.Name), "The name of the actor")),
                Ui.Field("IQ", iq),
                Ui.Field("EQ", eq)),
            Ui.Row(16,
                Ui.Check("Main character", a, nameof(a.IsMainCharacter)),
                Ui.Check("Asperger", a, nameof(a.IsAsperger)),
                Ui.Check("Dyslexic", a, nameof(a.IsDyslexic)),
                Ui.Check("Disqualified", a, nameof(a.Disqualified), "Set by ⛔ DISQUALIFIED; untick to bring the actor back")),
            Area(vm, "Description", Ui.Lines(a, nameof(a.Description), 100, "Who they are: look, age, past")),
            WritingUi.Cells(2,
                Area(vm, "How they speak", Ui.Lines(a, nameof(a.HowHeSpeaks), 80, "Words, rhythm, accent, tics")),
                Area(vm, "How they think", Ui.Lines(a, nameof(a.HowHeThinks), 80, "Logic, beliefs, fears"))),
            WritingUi.Cells(2,
                Area(vm, "Motivation", Ui.Lines(a, nameof(a.HisMotivation), 70, "What drives them")),
                Area(vm, "Goal", Ui.Lines(a, nameof(a.HisGoal), 70, "What they want to reach"))),
            WritingUi.Cells(3,
                Area(vm, "Secret", Ui.Lines(a, nameof(a.HisSecret), 70, "What they hide")),
                Area(vm, "Weakness", Ui.Lines(a, nameof(a.HisWeakness), 70, "Where they fail")),
                Area(vm, "Strength", Ui.Lines(a, nameof(a.HisStrength), 70, "Where they shine"))));
        fields.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return Ui.Scroll(Ui.Row(14, ActorPicture(vm, a), fields));
    }

    /// <summary>
    /// The picture of the actor (a portrait), with 📂 UPLOAD (a file copied into Pictures/kaonomi/writer/&lt;title&gt;),
    /// 🎨 GENERATE (a prompt drawn by Fooocus, kept there too) and ✕ (no picture; the file stays). A double-click opens it.
    /// </summary>
    private static Control ActorPicture(GhostWriterWindowViewModel vm, WriterActor a)
    {
        var picture = Ui.Picture(null, 320);
        picture.CustomMinimumSize = new Vector2(240, 320);
        picture.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
        picture.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        picture.TooltipText = "Double-click: open the picture";
        var empty = Ui.Placeholder("No picture yet:\n📂 upload one, or 🎨 generate it with Fooocus");
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(240, 320) };
        frame.AddThemeStyleboxOverride("panel", EveStyle.Box(EveStyle.Sunken(0.2f), EveStyle.Edge with { A = 0.45f }, 6));
        frame.AddChild(empty);
        frame.AddChild(picture);
        picture.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true } && File.Exists(a.ImagePath))
                PictureLibrary.OpenWithSystem(a.ImagePath);
        };
        async void Show()
        {
            var path = a.ImagePath;
            var texture = File.Exists(path) ? AvaloniaBridge.ToTexture(await PictureLibrary.DecodeAsync(path, 800, CancellationToken.None)) : null;
            if (!GodotObject.IsInstanceValid(picture) || path != a.ImagePath)
                return;
            picture.Texture = texture;
            empty.Visible = texture is null;
        }
        Bind.When(picture, a, Show, nameof(a.ImagePath));

        var upload = Ui.Action("📂 UPLOAD", () => _ = UploadAsync(), tooltip: "Pick a picture: it is copied into Pictures/kaonomi/writer/<title>");
        var generate = Ui.Action("🎨 GENERATE", () => _ = GenerateAsync(), tooltip: "Describe the portrait: Fooocus draws it, kept in Pictures/kaonomi/writer/<title>");
        Bind.Enabled(generate, vm, nameof(vm.IsBusy), invert: true);
        var clear = Ui.Action("✕", () => a.ImagePath = string.Empty, tooltip: "No picture for this actor (the file is kept)");

        async Task UploadAsync()
        {
            var file = await Dialogs.OpenFile($"Picture of {a.Name}",
                ["*.jpg,*.jpeg,*.png,*.gif,*.webp,*.bmp,*.heic,*.heif,*.tif,*.tiff ; Pictures", "* ; All files"], ChatSettings.SystemPicturesDirectory);
            if (file is not null)
                vm.SetActorPicture(a, file);
        }
        async Task GenerateAsync()
        {
            var prompt = await Dialogs.Ask($"Portrait of {a.Name}", "Describe the picture for Fooocus (in English is best):", vm.ActorPrompt(a), multiline: true);
            if (!string.IsNullOrWhiteSpace(prompt))
                await vm.GenerateActorPictureAsync(a, prompt);
        }

        var column = Ui.Column(8, frame, Ui.Row(6, upload, generate, clear));
        column.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        return column;
    }

    // ── 3. Locations ──

    private static Control BuildLocations(GhostWriterWindowViewModel vm) =>
        Master(vm, vm.Locations, nameof(vm.SelectedLocation), l => l.Name, nameof(WriterLocation.Name), l => l.GeoLocation,
            () => vm.SelectedLocation, l => vm.SelectedLocation = l, "No location yet: ✚ ADD",
            [Ui.Command("✚ ADD", vm.AddLocationCommand, primary: true, tooltip: "A new location"), Ui.Command("✕ REMOVE", vm.RemoveLocationCommand, tooltip: "Remove the location selected")],
            Ui.Swap(vm, nameof(vm.SelectedLocation), value => value is WriterLocation location ? LocationEditor(vm, location) : Ui.Placeholder("Select a location, or add one")),
            null);

    private static Control LocationEditor(GhostWriterWindowViewModel vm, WriterLocation l) =>
        Ui.Scroll(Ui.Column(10,
            WritingUi.Cells(2,
                Ui.Field("Name", Ui.Line(l, nameof(l.Name), "The name of the location")),
                Ui.Field("Geolocation", Ui.Line(l, nameof(l.GeoLocation), "Where it is: country, city, coordinates…"))),
            Area(vm, "Description", Ui.Lines(l, nameof(l.Description), 110, "What it looks like, smells like, sounds like")),
            WritingUi.Cells(2,
                Area(vm, "History", Ui.Lines(l, nameof(l.History), 100, "What happened there")),
                Area(vm, "Culture", Ui.Lines(l, nameof(l.Culture), 100, "Who lives there, their customs"))),
            WritingUi.Cells(2,
                Ui.Field("Picture (URL)", Ui.Line(l, nameof(l.ImageUrl), "https://…")),
                Ui.Field("Wiki page (URL)", Ui.Line(l, nameof(l.WikiUrl), "https://…")))));

    // ── 4. Chapters ──

    /// <summary>Opens the text window of <paramref name="chapter"/> (brought back when it is open already).</summary>
    private static void OpenChapter(Shell shell, GhostWriterWindowViewModel vm, WriterChapter chapter)
    {
        vm.SelectedChapter = chapter;
        shell.Open(new ChapterTextWindowViewModel(vm, chapter), ChapterTextWindowViewModel.KeyOf(chapter), ChapterTextView.TitleOf(chapter));
    }

    private static Control BuildChapters(GhostWriterWindowViewModel vm, Action<WriterChapter> write) =>
        Master(vm, vm.Chapters, nameof(vm.SelectedChapter), c => $"{c.Position}. {c.Title}", nameof(WriterChapter.Title),
            c => $"{GhostWriterWindowViewModel.Words(c.Text)} word(s)",
            () => vm.SelectedChapter, c => vm.SelectedChapter = c, "No chapter yet: ✚ ADD",
            [
                Ui.Command("✚ ADD", vm.AddChapterCommand, primary: true, tooltip: "A new chapter, after the one selected"),
                Ui.Command("✕ REMOVE", vm.RemoveChapterCommand, tooltip: "Remove the chapter selected"),
                Ui.Command("▲", vm.MoveChapterUpCommand, tooltip: "Move the chapter up"),
                Ui.Command("▼", vm.MoveChapterDownCommand, tooltip: "Move the chapter down")
            ],
            Ui.Swap(vm, nameof(vm.SelectedChapter), value => value is WriterChapter chapter ? ChapterEditor(vm, chapter, write) : Ui.Placeholder("Select a chapter, or add one")),
            write);

    private static Control ChapterEditor(GhostWriterWindowViewModel vm, WriterChapter c, Action<WriterChapter> write)
    {
        var words = EveStyle.Text_("", EveStyle.Muted, 11);
        Bind.When(words, c, () => words.Text = $"{GhostWriterWindowViewModel.Words(c.Text)} word(s) written", nameof(c.Text));
        // The text as it reads (Markdown rendered), the words of a footnote followed by its number in superscript.
        var preview = Ui.Markdown();
        preview.AddThemeColorOverride("default_color", EveStyle.Text with { A = 0.8f });
        preview.MetaClicked += meta => Links.Open(meta.AsString());
        Bind.When(preview, c, () => Ui.SetMarkdown(preview, c.Text.Trim().Length > 0
            ? WithFootnoteMarks(c.Text.Trim())
            : "*Nothing written yet: double-click the chapter in the list, or ✍ WRITE.*"), nameof(c.Text));
        return Ui.Scroll(Ui.Column(10,
            Ui.Row(8, Ui.Field("Title", Ui.Line(c, nameof(c.Title), "The title of the chapter")),
                Ui.Action("✍ WRITE", () => write(c), primary: true, tooltip: "Open the window where the chapter is written (or double-click it in the list)")),
            Area(vm, "Description", Ui.Lines(c, nameof(c.Description), 100, "What happens in the chapter")),
            WritingUi.Cells(2,
                Area(vm, "Perception goal", Ui.Lines(c, nameof(c.PerceptionGoal), 80, "What the reader should feel or understand")),
                Area(vm, "Ambiance", Ui.Lines(c, nameof(c.Ambiance), 80, "Light, weather, rhythm, music of the chapter"))),
            Ui.Margin(EveStyle.Separator(), 0, 6, 0, 2),
            Ui.Row(8, EveStyle.Caption("TEXT", EveStyle.Cyan, 11), words),
            preview,
            FootnotesOf(c, write)));
    }

    private const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹";

    /// <summary>A number in superscript digits (¹, ²³…).</summary>
    internal static string Superscript(int number) =>
        new(number.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(d => Superscripts[d - '0']).ToArray());

    /// <summary><paramref name="markdown"/> with each footnote link <c>[words](note:N)</c> turned into the words followed by N in superscript.</summary>
    private static string WithFootnoteMarks(string markdown) =>
        System.Text.RegularExpressions.Regex.Replace(markdown,
            @"\[(?<words>(?:[^\[\]]|\[[^\]]*\])*)\]\(<?" + System.Text.RegularExpressions.Regex.Escape(WriterChapter.NoteScheme) + @"(?<number>\d+)>?\)",
            m => m.Groups["words"].Value + Superscript(int.Parse(m.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// The footnotes of chapter <paramref name="c"/> under its text (hidden while there is none): the number in
    /// superscript, the words, the text of the note and its links (a click opens one); a click on a note opens the
    /// window of the chapter, where it is edited.
    /// </summary>
    private static Control FootnotesOf(WriterChapter c, Action<WriterChapter> write)
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        Bind.Items(list, c.Footnotes, note =>
        {
            var main = new Button { Flat = true, Alignment = HorizontalAlignment.Left, ClipText = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
            main.Pressed += () => write(c);
            var linkRow = Ui.Row(4);
            var row = Ui.Row(6, main, linkRow);
            void Refresh()
            {
                main.Text = $"{Superscript(note.Number)} « {note.Anchor} » — {(note.Text.Length > 0 ? note.Text : "(no text yet)")}";
                main.TooltipText = $"Footnote {note.Number} on « {note.Anchor} »\n{note.Text}\n(click: open the chapter to edit it)";
                foreach (var child in linkRow.GetChildren())
                    child.QueueFree();
                foreach (var link in note.Links)
                {
                    var address = link.Url;
                    var button = Ui.Action("🔗 " + (link.Title.Length > 0 ? link.Title : address), () => Links.Open(address), tooltip: address);
                    button.AddThemeFontSizeOverride("font_size", 11);
                    linkRow.AddChild(button);
                }
            }
            Bind.When(row, note, Refresh, nameof(note.Number), nameof(note.Anchor), nameof(note.Text), nameof(note.Links));
            return row;
        });
        var box = Ui.Column(4, Ui.Margin(EveStyle.Separator(), 0, 6, 0, 2), EveStyle.Caption("FOOTNOTES", EveStyle.Cyan, 11), list);
        void Show() => Bind.OnMain(() =>
        {
            if (GodotObject.IsInstanceValid(box))
                box.Visible = c.Footnotes.Count > 0;
        });
        System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) => Show();
        c.Footnotes.CollectionChanged += changed;
        Bind.OnGone(box, () => c.Footnotes.CollectionChanged -= changed);
        Show();
        return box;
    }

    // ── 5. Publish ──

    /// <summary>Where the exports are written by default: Documents/kaonomi/writer.</summary>
    private static string ExportDirectory =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "kaonomi", "writer");

    /// <summary>
    /// PUBLISH: on the left the summary of the writing (its length, ✨ GENERATE with the model of the chat, the text to
    /// edit, saved with the writing); on the right the exports (MS Word, PDF, HTML, Blogger), with or without the
    /// summary, and the last file written (OPEN, FOLDER).
    /// </summary>
    // ── 6. Journal ──

    /// <summary>
    /// The journal of the writing, read-only: an entry per action that changed its text (newest first) on the left; the
    /// action, the chapter, the texts given, generated and corrected of the one selected on the right. There is nothing
    /// to change nor delete: the search engine keeps every entry.
    /// </summary>
    private static Control BuildJournal(GhostWriterWindowViewModel vm)
    {
        var list = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(380, 0) };
        list.AddThemeFontOverride("font", EveStyle.Mono);
        list.AddThemeFontSizeOverride("font_size", 12);
        Bind.List(list, vm.JournalEntries, e => e.Line, vm, nameof(vm.SelectedJournalEntry), e => e.Comment.Length > 0 ? e.Comment : e.Action);
        var refresh = Ui.Command("⟳ REFRESH", vm.RefreshJournalCommand, tooltip: "Read the journal of the writing from the search engine");
        var status = Ui.Label(vm, nameof(vm.JournalStatus), EveStyle.Muted, 11, wrap: true);
        var left = Ui.Column(8,
            EveStyle.Caption("JOURNAL", EveStyle.Cyan, 12),
            EveStyle.Text_("Every action that changes the text of the writing, kept in the search engine. It cannot be changed nor deleted.", EveStyle.Muted, 11, wrap: true),
            Ui.Row(8, refresh),
            status,
            list);
        left.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        TextEdit Text()
        {
            var edit = new TextEdit
            {
                Editable = false,
                WrapMode = TextEdit.LineWrappingMode.Boundary,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 90)
            };
            edit.AddThemeFontSizeOverride("font_size", 12);
            edit.AddThemeColorOverride("font_readonly_color", EveStyle.Text);
            return edit;
        }
        var header = EveStyle.Text_("", EveStyle.TextBright, 13, wrap: true);
        var details = EveStyle.Text_("", EveStyle.Muted, 11, wrap: true);
        var original = Text();
        var generated = Text();
        var corrected = Text();
        Control Box(string caption, TextEdit text)
        {
            var box = Ui.Column(4, EveStyle.Caption(caption), text);
            box.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            return box;
        }
        var originalBox = Box("Text given (before the action, or its prompt)", original);
        var generatedBox = Box("Text generated", generated);
        var correctedBox = Box("Text corrected", corrected);
        var right = Ui.Column(8, header, details, originalBox, generatedBox, correctedBox);
        right.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Bind.When(right, vm, () =>
        {
            var entry = vm.SelectedJournalEntry;
            header.Text = entry is null ? "No entry selected" : $"{entry.Action}{(entry.Chapter.Length > 0 ? " · chapter " + entry.Chapter : "")}";
            details.Text = entry is null ? "" : string.Join("  ·  ", new[]
            {
                $"{(entry.Happened ?? entry.Date)?.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
                $"{entry.WritingTitle} ({entry.WritingId})",
                entry.Model.Length > 0 ? "model " + entry.Model : "",
                entry.Comment
            }.Where(p => p.Length > 0));
            original.Text = entry?.OriginalText ?? "";
            generated.Text = entry?.GeneratedText ?? "";
            corrected.Text = entry?.CorrectedText ?? "";
            // Only the texts the action has.
            originalBox.Visible = original.Text.Length > 0;
            generatedBox.Visible = generated.Text.Length > 0;
            correctedBox.Visible = corrected.Text.Length > 0;
        }, nameof(vm.SelectedJournalEntry));

        return Ui.SplitH(left, right, 420);
    }

    private static Control BuildPublish(GhostWriterWindowViewModel vm)
    {
        Control root = null!;

        // ── The summary ──
        var length = WritingUi.Choice(vm, nameof(vm.SummaryLength), GhostWriterWindowViewModel.SummaryLengths);
        length.TooltipText = "Short: one paragraph · Medium: about 300 words · Long: an introduction, then a paragraph per chapter";
        length.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        length.CustomMinimumSize = new Vector2(140, 0);
        var generate = Ui.Action("✨ GENERATE", () => _ = GenerateSummaryAsync(vm, root), primary: true,
            tooltip: "The model of the chat reads the chapters and writes the summary of the writing (it replaces the one below)");
        Bind.Enabled(generate, vm, nameof(vm.IsBusy), invert: true);
        var words = EveStyle.Text_("", EveStyle.Muted, 11);
        Bind.When(words, vm, () => words.Text = $"{GhostWriterWindowViewModel.Words(vm.WritingSummary)} word(s)", nameof(vm.WritingSummary));
        var summaryText = Ui.Lines(vm, nameof(vm.WritingSummary), 320, "The summary of the writing: ✨ GENERATE writes it from the chapters, then edit it as you like");
        summaryText.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var summaryArea = Area(vm, "Summary", summaryText);
        summaryArea.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var left = Ui.Column(10,
            EveStyle.Caption("SUMMARY", EveStyle.Cyan, 12),
            EveStyle.Text_("A summary of the whole writing, for the back cover, a post or the start of the exports.", EveStyle.Muted, 11, wrap: true),
            Ui.Row(8, EveStyle.Caption("Length"), length, generate, Ui.Spring(), words),
            summaryArea);
        left.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // ── The exports ──
        var withSummary = new CheckBox { Text = "Include the summary", ButtonPressed = true, TooltipText = "The summary after the title, before the first chapter" };
        var last = EveStyle.Text_("Nothing exported yet", EveStyle.Muted, 11, wrap: true);
        string? lastFile = null;
        var open = Ui.Action("📂 OPEN", () => { if (lastFile is not null) PictureLibrary.OpenWithSystem(lastFile); }, tooltip: "Open the last file exported");
        var folder = Ui.Action("📁 FOLDER", () => OS.ShellShowInFileManager(lastFile ?? ExportDirectory), tooltip: "Show the file in its folder");
        open.Disabled = true;
        void Exported(string file, string message)
        {
            lastFile = file;
            last.Text = file;
            open.Disabled = false;
            vm.SetStatus(message, false);
        }

        Control Format(string icon, string name, string description, Func<Task> export)
        {
            var button = Ui.Action($"{icon}  {name}", () => _ = export(), primary: false);
            button.CustomMinimumSize = new Vector2(220, 44);
            button.Alignment = HorizontalAlignment.Left;
            var text = EveStyle.Text_(description, EveStyle.Muted, 11, wrap: true);
            text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            text.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            return Ui.Row(12, button, text);
        }

        async Task Export(string format)
        {
            var writing = vm.Snapshot();
            if (writing.Chapters.All(c => c.Text.Trim().Length == 0))
            {
                vm.SetStatus("Nothing to publish yet: write a chapter first", true);
                return;
            }
            var include = withSummary.ButtonPressed;
            var name = GhostWriterEntry.Slug(writing.Title);
            Directory.CreateDirectory(ExportDirectory);
            try
            {
                switch (format)
                {
                    case "docx":
                    case "pdf":
                    {
                        var word = format == "docx";
                        var file = await Dialogs.SaveFile(word ? "Export to MS Word" : "Export to PDF", $"{name}.{format}",
                            [word ? "*.docx ; Word document" : "*.pdf ; PDF document"], ExportDirectory);
                        if (file is null)
                            return;
                        if (!file.EndsWith("." + format, StringComparison.OrdinalIgnoreCase))
                            file += "." + format;
                        vm.SetStatus(word ? "Writing the Word document…" : "Writing the PDF…", false);
                        int pictures;
                        await using (var stream = File.Create(file))
                            pictures = word
                                ? await GhostWriterPublisher.WordAsync(stream, writing, include, vm.Language)
                                : await GhostWriterPublisher.PdfAsync(stream, writing, include, vm.Language);
                        Exported(file, $"✓ Exported to {Path.GetFileName(file)}" + (pictures > 0 ? $" ({pictures} picture(s))" : string.Empty));
                        break;
                    }
                    case "html":
                    {
                        var file = await Dialogs.SaveFile("Export to a web page (HTML)", $"{name}.html", ["*.html ; Web page"], ExportDirectory);
                        if (file is null)
                            return;
                        if (!file.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                            file += ".html";
                        await File.WriteAllTextAsync(file, GhostWriterPublisher.Html(writing, include, vm.Language), new System.Text.UTF8Encoding(false));
                        Exported(file, $"✓ Exported to {Path.GetFileName(file)} (one file, its pictures inside it)");
                        break;
                    }
                    case "blogger":
                    {
                        var (html, pictures) = GhostWriterPublisher.Blogger(writing, include, vm.Language);
                        var file = Path.Combine(ExportDirectory, $"{name}-blogger.html");
                        await File.WriteAllTextAsync(file, html, new System.Text.UTF8Encoding(false));
                        Dialogs.Copy(html);
                        Exported(file, "✓ The HTML of the post is in the clipboard: in Blogger, a new post, its title, ✎ HTML view, paste" +
                            (pictures.Count > 0 ? $"; {pictures.Count} picture(s) to upload there (marked 📷 in the post)" : string.Empty));
                        if (await Dialogs.Confirm("Publish on Blogger",
                                "The HTML of the post is in the clipboard (and in " + file + ").\n\n" +
                                "In Blogger: NEW POST, type its title (" + writing.Title.Trim() + "), switch to the HTML view (✎ › HTML view), paste, then PUBLISH." +
                                (pictures.Count > 0 ? $"\n\nThe pictures are not in the post: upload them in Blogger where 📷 marks them:\n{string.Join("\n", pictures.Distinct().Select(Path.GetFileName))}" : string.Empty),
                                "OPEN BLOGGER", "LATER"))
                            Links.Open("https://www.blogger.com/");
                        break;
                    }
                }
                ActivityLog.Info("GhostWriter", $"Writing {name} exported ({format}): {lastFile}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                vm.SetStatus($"Not exported: {ex.Message}", true);
                ActivityLog.Error("GhostWriter", $"Export ({format}) of {name} failed", ex);
            }
        }

        var right = Ui.Column(12,
            EveStyle.Caption("EXPORT", EveStyle.Cyan, 12),
            EveStyle.Text_($"The whole writing: its title and author, the summary, then each chapter with its footnotes. Saved in {ExportDirectory} by default.", EveStyle.Muted, 11, wrap: true),
            withSummary,
            Format("📄", "MS WORD (.docx)", "A Word document: the chapters as headings (the navigation pane lists them), their formatting and pictures, the notes after each chapter.", () => Export("docx")),
            Format("📕", "PDF", "A PDF to read or print: numbered pages, the pictures, the links clickable.", () => Export("pdf")),
            Format("🌐", "HTML", "A web page in one file (its pictures inside it): the notes linked to their words and back.", () => Export("html")),
            Format("🅱", "BLOGGER", "The HTML of a Blogger post, copied to the clipboard (and saved), then Blogger opens: paste it in the HTML view of a new post.", () => Export("blogger")),
            Ui.Margin(EveStyle.Separator(), 0, 6, 0, 2),
            EveStyle.Caption("LAST EXPORT", EveStyle.Cyan, 11),
            last,
            Ui.Row(6, open, folder));
        right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var form = Ui.Margin(left, 0, 0, 12, 0);
        root = Ui.SplitH(form, Ui.Scroll(right), 0);
        return root;
    }

    /// <summary>✨ GENERATE: the summary written by the model of the chat, with the waiting popup (Cancel).</summary>
    private static async Task GenerateSummaryAsync(GhostWriterWindowViewModel vm, Control host)
    {
        if (!vm.Assistant.CanAsk)
        {
            vm.SetStatus("No LLM available for the summary", true);
            return;
        }
        if (vm.WritingSummary.Trim().Length > 0
            && !await Dialogs.Confirm("Generate the summary", "The summary below will be replaced by a new one. Go on?", "Generate"))
            return;
        var model = vm.Assistant.CurrentModelName ?? "LLM";
        vm.IsBusy = true;
        try
        {
            var (_, error) = await ChapterTextView.AskAsync(host, "Summary", $"{model} reads the chapters and writes the summary of the writing…",
                async token =>
                {
                    await vm.GenerateSummaryAsync(token);
                    return vm.WritingSummary;
                }, "Summary");
            if (error is not null)
                vm.SetStatus("The summary was not generated: " + error, true);
        }
        finally
        {
            vm.IsBusy = false;
        }
    }

    // ── Dictation ──

    /// <summary>A text area under its caption, with the 🎤 of the dictation on the right of the caption.</summary>
    private static VBoxContainer Area(GhostWriterWindowViewModel vm, string caption, TextEdit edit)
    {
        edit.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var head = Ui.Row(6, EveStyle.Caption(caption), Mic(vm, Dictation(edit)));
        return Ui.Fill(Ui.Column(3, head, edit));
    }

    /// <summary>
    /// The 🎤 of the dictation: held, the microphone records (the button turns red) and the words said so far are
    /// given to <paramref name="show"/> as they come (final: false, each time all of them); released, the speech is
    /// turned into text (Whisper, in the language of the voice input of the chat) and given once more (final: true,
    /// empty when nothing was heard).
    /// </summary>
    internal static Button Mic(GhostWriterWindowViewModel vm, Action<string, bool> show)
    {
        var mic = EveStyle.Button("🎤", tooltip: "Hold to dictate (the words show as you speak), release: they are written at the caret");
        mic.FocusMode = Control.FocusModeEnum.None;
        mic.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        mic.AddThemeFontSizeOverride("font_size", 11);
        // True while this 🎤 records (its release, or the end of its control, stops it).
        var holding = false;
        async Task Release()
        {
            if (!holding)
                return;
            holding = false;
            if (GodotObject.IsInstanceValid(mic))
                mic.Modulate = Colors.White;
            try
            {
                var text = await vm.StopDictationAsync();
                if (GodotObject.IsInstanceValid(mic))
                    show(text, true);
            }
            catch (Exception ex)
            {
                vm.SetStatus("Dictation: " + ex.Message, true);
                ActivityLog.Error("GhostWriter", "Dictation stop failed", ex);
            }
        }
        mic.ButtonDown += () =>
        {
            // A dictation left on (its release was never seen: the window lost the pointer): this press ends it.
            if (vm.IsDictating)
            {
                holding = true;
                _ = Release();
                return;
            }
            vm.StartDictation(text => Bind.OnMain(() =>
            {
                if (GodotObject.IsInstanceValid(mic) && vm.IsDictating)
                    show(text, false);
            }));
            if (vm.IsDictating)
            {
                holding = true;
                mic.Modulate = new Color(1, 0.35f, 0.35f);
            }
        };
        mic.ButtonUp += () => _ = Release();
        // The control goes (the tab rebuilt, the window closed) while it records: the microphone is closed.
        Bind.OnGone(mic, () => _ = Release());
        Bind.Enabled(mic, vm, nameof(vm.IsTranscribing), invert: true);
        return mic;
    }

    /// <summary>
    /// The dictation into <paramref name="edit"/>: the words said so far typed at the caret (a space before them when
    /// they follow a word), replaced by the next ones as they come, then by the final text (removed when it is empty).
    /// </summary>
    private static Action<string, bool> Dictation(TextEdit edit)
    {
        // Where the words of the dictation in progress are: from (line, column) to the end; null: none shown.
        (int Line, int Column, int EndLine, int EndColumn)? shown = null;
        var space = string.Empty;
        return (text, final) =>
        {
            if (shown is { } range)
            {
                edit.RemoveText(range.Line, range.Column, range.EndLine, range.EndColumn);
                edit.SetCaretLine(range.Line);
                edit.SetCaretColumn(range.Column);
            }
            else
            {
                var line = edit.GetLine(edit.GetCaretLine());
                var column = Math.Min(edit.GetCaretColumn(), line.Length);
                space = column > 0 && !char.IsWhiteSpace(line[column - 1]) ? " " : "";
            }
            shown = null;
            if (text.Length == 0)
                return;
            var (startLine, startColumn) = (edit.GetCaretLine(), edit.GetCaretColumn());
            edit.InsertTextAtCaret(space + text);
            if (!final)
                shown = (startLine, startColumn, edit.GetCaretLine(), edit.GetCaretColumn());
        };
    }

    /// <summary>
    /// The dictation into the editor of a chapter: the words said so far typed at the caret, a space after them, taken
    /// back (undone) when the next ones come, then the final text.
    /// </summary>
    internal static Action<string, bool> Dictation(TapeEditor editor)
    {
        var shown = false;
        return (text, final) =>
        {
            if (shown)
                editor.Undo();
            shown = false;
            if (text.Length == 0)
                return;
            editor.InsertPlainText(text + " ");
            shown = !final;
        };
    }

    // ── A list and the editor of its item ──

    /// <summary>
    /// The list of <paramref name="items"/> on the left (each row its label, kept up to date when
    /// <paramref name="labelProperty"/> of the item changes, a muted detail under it), its buttons above it, the editor
    /// of the item selected on the right. A double-click on a row calls <paramref name="activate"/> when given.
    /// </summary>
    private static Control Master<T>(GhostWriterWindowViewModel vm, System.Collections.ObjectModel.ObservableCollection<T> items, string selectedProperty,
        Func<T, string> label, string labelProperty, Func<T, string> detail, Func<T?> selected, Action<T> select, string empty,
        Control[] buttons, Control editor, Action<T>? activate) where T : class, System.ComponentModel.INotifyPropertyChanged
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var none = Ui.Placeholder(empty);
        Bind.Items(list, items, item =>
        {
            var title = EveStyle.Text_("", EveStyle.TextBright, 13);
            title.ClipText = true;
            title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            var info = EveStyle.Text_("", EveStyle.Muted, 10);
            var content = Ui.Column(1, title, info);
            // Every field of the item may change the label or the detail (the title, the position, the text).
            Bind.When(content, item, () =>
            {
                title.Text = label(item);
                info.Text = detail(item);
            }, labelProperty, "Position", "Text", "IsMainCharacter", "Disqualified", "GeoLocation");
            var row = WritingUi.ResultRow(vm, selectedProperty, () => ReferenceEquals(selected(), item), () => select(item), content, 46);
            if (activate is not null)
            {
                row.TooltipText = "Double-click: write the chapter";
                row.GuiInput += e =>
                {
                    if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true })
                        activate(item);
                };
            }
            return row;
        }, none);

        var left = Ui.Column(8, Ui.Row(6, buttons), Ui.Scroll(Ui.Column(2, list, none)));
        left.CustomMinimumSize = new Vector2(280, 0);
        left.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return Ui.SplitH(left, editor, 340);
    }

    // ── New, open ──

    private static async Task NewAsync(GhostWriterWindowViewModel vm)
    {
        if (vm.IsDirty && !await Dialogs.Confirm("New writing", "The changes of the writing are not saved. Start a new writing anyway?", "New writing"))
            return;
        vm.NewCommand.Execute(null);
    }

    private static async Task OpenAsync(GhostWriterWindowViewModel vm, GhostWriterEntry entry)
    {
        if (vm.WritingId == entry.Id)
            return;
        if (vm.IsDirty && !await Dialogs.Confirm("Open a writing", "The changes of the writing are not saved. Open another writing anyway?", "Open"))
            return;
        Bind.Run(vm.OpenEntryCommand, entry);
    }
}

/// <summary>
/// The window where a chapter of /write is written (a double-click on the chapter, or ✍ WRITE): a window of the desktop
/// like the others (moved, resized, closed), one per chapter, with the editor and the ribbon of /work and of the
/// journal: Home (Chapter: Save, AI-Check, Done; font, paragraph, styles), Insert (pictures, copied into Pictures/kaonomi/write;
/// links; code), View (zoom); the right click corrects, rewrites, evaluates or translates with the model of the chat.
/// Over the ribbon: the position and title of the chapter, the count of its words. The text is kept in the chapter
/// (Markdown) after each change; ⌘/Ctrl S saves the writing, ⌘/Ctrl K makes a link.
/// </summary>
[ToolView(typeof(ChapterTextWindowViewModel), "Chapter", "✍", 1000, 720)]
public sealed class ChapterTextView : IToolView
{
    /// <summary>The title of the window of <paramref name="chapter"/>.</summary>
    public static string TitleOf(WriterChapter chapter) => $"✍ {chapter.Position}. {chapter.Title}";

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ChapterTextWindowViewModel)viewModel;
        var chapter = vm.Chapter;

        var root = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var overlay = new Overlay();
        var editor = new TapeEditor
        {
            KeepSource = true,
            Placeholder = "Write the chapter here… select text to format it (the ribbon above, or ⌘/Ctrl B, I, U) or put a footnote on it (¹); right click: correct, rewrite, evaluate, translate."
        };
        var transient = EveStyle.Text_("", EveStyle.Green, 11);
        var ribbon = new TapeRibbon(editor, overlay, shell, root, transient, vm, "GhostWriter", "the chapter", "write", _ => GhostWriterWindowViewModel.PicturesDirectory);

        // The text of the chapter, both ways (Markdown).
        editor.Text = chapter.Text;
        editor.TextCommitted += text => chapter.Text = text;
        Bind.When(editor, chapter, () => editor.Text = chapter.Text, nameof(chapter.Text));
        editor.LoadImage = async path => AvaloniaBridge.ToTexture(await vm.LoadImageAsync(path));
        editor.OpenImage = PictureLibrary.OpenWithSystem;
        // A click on the words of a footnote opens it; any other link opens as in /work (⌥/Alt + click edits a link).
        editor.LinksOpenOnClick = true;
        editor.OpenLink = link =>
        {
            if (link.StartsWith(WriterChapter.NoteScheme, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(link[WriterChapter.NoteScheme.Length..], out var number) && vm.Footnote(number) is { } note)
                EditFootnote(vm, shell, editor, ribbon, note);
            else
                ribbon.OpenLink(link);
        };

        // ── The head: the chapter, its words ──
        var title = EveStyle.Caption("", EveStyle.TextBright, 16);
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var words = EveStyle.Text_("", EveStyle.Muted, 11);
        // The 🎤: the words dictated are typed at the caret of the chapter as they are said, a space after them.
        var mic = GhostWriterView.Mic(vm.Writing, GhostWriterView.Dictation(editor));
        var head = Ui.Row(8, title, mic, words);

        void Close()
        {
            if (shell.WindowOf(root) is { } window)
                shell.Deck.Close(window);
        }
        var save = TapeRibbon.BigTool("💾", "Save", "Save the writing (every chapter) in the search engine · ⌘/Ctrl S", () => Bind.Run(vm.Writing.SaveToServerCommand));
        var done = TapeRibbon.BigTool("✓", "Done", "Close this window (the text is kept in the chapter)", Close);
        var check = TapeRibbon.BigTool("🧪", "AI-Check", "Does the text match the description, the perception goal and the ambiance of the chapter? The model of the chat says it, then offers to rewrite it",
            () => _ = AiCheckAsync(vm, shell, root, editor, ribbon));
        var footnote = TapeRibbon.BigTool("¹", "Footnote", "A footnote on the selected words: its text and links (a click on the words opens it again)",
            () => AddFootnote(vm, shell, editor, ribbon));
        var chapterGroup = TapeRibbon.Group("Chapter", false, save, check, footnote, done);
        var ribbonPanel = ribbon.Build(transient,
            ("Home", [chapterGroup, ribbon.FontGroup(), ribbon.ParagraphGroup(), ribbon.StylesGroup(last: true)]),
            ("Insert", [ribbon.PicturesGroup($"Insert pictures at the caret (copied into {GhostWriterWindowViewModel.PicturesDirectory}); resize them with the grip of their corner",
                () => _ = InsertPicturesAsync(vm, editor),
                $"Generate an image with Fooocus from a prompt, then insert it at the caret (saved in {GhostWriterWindowViewModel.PicturesDirectory})"),
                ribbon.LinksGroup(), ribbon.CodeGroup()]),
            ("View", [ribbon.ZoomGroup()]));
        var scroll = Ui.Scroll(editor);
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var page = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.AddThemeStyleboxOverride("panel", EveStyle.Box(WorkPalette.Page, EveStyle.Edge with { A = 0.45f }, 0));
        page.AddChild(Ui.Column(0, ribbonPanel, scroll, Footnotes(vm, shell, editor, ribbon)));

        var content = Ui.Column(8, head, page);
        content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        root.AddChild(Ui.Margin(content, 10, 10, 10, 10));
        root.AddChild(overlay);

        Bind.When(root, chapter, () =>
        {
            title.Text = $"{chapter.Position}. {chapter.Title}";
            words.Text = $"{GhostWriterWindowViewModel.Words(chapter.Text)} word(s)";
            if (shell.WindowOf(root) is { } window)
                window.Title = TitleOf(chapter);
        }, nameof(chapter.Title), nameof(chapter.Position), nameof(chapter.Text));

        // Shortcuts: ⌘/Ctrl S saves the writing, ⌘/Ctrl K makes a link.
        WritingUi.Shortcuts(root, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.Writing.SaveToServerCommand);
            if (WritingUi.IsCommand(key, Key.K) && editor.HasFocus())
            {
                ribbon.ShowLinkFlyout();
                return true;
            }
            return false;
        });
        root.Ready += () => Callable.From(() => editor.GrabFocus()).CallDeferred();
        return root;
    }

    // ── Footnotes ──

    /// <summary>¹ Footnote: a footnote on the selected words (one paragraph); saved, the words link to it (note:&lt;number&gt;).</summary>
    private static void AddFootnote(ChapterTextWindowViewModel vm, Shell shell, TapeEditor editor, TapeRibbon ribbon)
    {
        var words = editor.SelectedText.Trim();
        if (words.Length == 0 || words.Contains('\n'))
        {
            ribbon.ShowStatus("Select the words the footnote is about first (in one paragraph)", EveStyle.Amber);
            return;
        }
        var note = vm.NewFootnote(words);
        FootnoteDialog(shell, note, $"¹ New footnote {note.Number}", () =>
        {
            if (!GodotObject.IsInstanceValid(editor))
                return;
            vm.AddFootnote(note);
            editor.SetLink(WriterChapter.NoteLink(note.Number));
            ribbon.UpdateToolbar();
            ribbon.ShowStatus($"Footnote {note.Number} added on « {words} » (a click on the words opens it)");
        }, null);
    }

    /// <summary>Opens footnote <paramref name="note"/>: SAVE keeps its changes, DELETE removes it and the link of its words.</summary>
    private static void EditFootnote(ChapterTextWindowViewModel vm, Shell shell, TapeEditor editor, TapeRibbon ribbon, WriterFootnote note) =>
        FootnoteDialog(shell, note, $"¹ Footnote {note.Number}", () => vm.Writing.MarkDirty(), () =>
        {
            if (!GodotObject.IsInstanceValid(editor))
                return;
            editor.ReplaceAll(vm.RemoveFootnote(note, editor.Text));
            ribbon.ShowStatus($"Footnote {note.Number} removed (its words stay, without the link)");
        });

    /// <summary>
    /// The window of a footnote: the words it is on, its text, its links (a title and an address each, ✚ ADD LINK, ✕);
    /// SAVE writes them in <paramref name="note"/> then calls <paramref name="saved"/>; DELETE (when
    /// <paramref name="deleted"/> is given) calls it; CANCEL changes nothing.
    /// </summary>
    private static void FootnoteDialog(Shell shell, WriterFootnote note, string title, Action saved, Action? deleted)
    {
        var dialog = new ConfirmationDialog { Title = title.ToUpperInvariant(), OkButtonText = "SAVE", MinSize = new Vector2I(640, 0) };
        dialog.GetCancelButton().Text = "CANCEL";
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        var anchor = EveStyle.Text_($"On « {note.Anchor} »", EveStyle.Amber, 12, wrap: true);
        anchor.CustomMinimumSize = new Vector2(600, 0);
        column.AddChild(anchor);
        var text = new TextEdit
        {
            Text = note.Text,
            PlaceholderText = "The text of the footnote…",
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(600, 150)
        };
        column.AddChild(Ui.Field("Note", text));

        // The links, edited on copies: CANCEL leaves the footnote as it was.
        column.AddChild(EveStyle.Caption("LINKS", EveStyle.Cyan, 11));
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 4);
        var links = note.Links.Select(l => new WriterFootnoteLink { Title = l.Title, Url = l.Url }).ToList();
        void AddRow(WriterFootnoteLink link)
        {
            var name = new LineEdit { Text = link.Title, PlaceholderText = "Title", CustomMinimumSize = new Vector2(200, 0) };
            var address = new LineEdit { Text = link.Url, PlaceholderText = "https://…", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            name.TextChanged += value => link.Title = value;
            address.TextChanged += value => link.Url = value;
            HBoxContainer? row = null;
            var remove = Ui.Action("✕", () =>
            {
                links.Remove(link);
                row!.QueueFree();
            }, tooltip: "Remove this link");
            row = Ui.Row(6, name, address, remove);
            rows.AddChild(row);
        }
        foreach (var link in links)
            AddRow(link);
        column.AddChild(rows);
        column.AddChild(Ui.Row(6, Ui.Action("✚ ADD LINK", () =>
        {
            var link = new WriterFootnoteLink();
            links.Add(link);
            AddRow(link);
        }, tooltip: "A link of the footnote: a title and an address (a web page, a file…)"), Ui.Spring()));

        if (deleted is not null)
        {
            dialog.AddButton("DELETE", false, "delete");
            dialog.CustomAction += action =>
            {
                if (action != "delete")
                    return;
                dialog.Hide();
                deleted();
                dialog.QueueFree();
            };
        }
        dialog.AddChild(column);
        dialog.Confirmed += () =>
        {
            note.Text = text.Text.Trim();
            note.Links = links.Where(l => l.Url.Trim().Length > 0)
                .Select(l => new WriterFootnoteLink { Title = l.Title.Trim(), Url = TapeStyle.NormalizeLink(l.Url) ?? l.Url.Trim() })
                .ToList();
            saved();
            dialog.QueueFree();
        };
        dialog.Canceled += () => dialog.QueueFree();
        shell.Root.AddChild(dialog);
        dialog.PopupCentered();
        text.GrabFocus();
    }

    /// <summary>
    /// The footnotes of the chapter under its text (hidden while there is none): each one its number, its words, its
    /// text and its links (a click on a link opens it); a click on the note opens it to edit.
    /// </summary>
    private static Control Footnotes(ChapterTextWindowViewModel vm, Shell shell, TapeEditor editor, TapeRibbon ribbon)
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        Bind.Items(list, vm.Chapter.Footnotes, note =>
        {
            var main = new Button { Flat = true, Alignment = HorizontalAlignment.Left, ClipText = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
            main.Pressed += () => EditFootnote(vm, shell, editor, ribbon, note);
            var linkRow = Ui.Row(4);
            var row = Ui.Row(6, main, linkRow);
            void Refresh()
            {
                main.Text = $"{GhostWriterView.Superscript(note.Number)} « {note.Anchor} » — {(note.Text.Length > 0 ? note.Text : "(no text yet)")}";
                main.TooltipText = $"Footnote {note.Number} on « {note.Anchor} »\n{note.Text}\n(click: edit it)";
                foreach (var child in linkRow.GetChildren())
                    child.QueueFree();
                foreach (var link in note.Links)
                {
                    var address = link.Url;
                    var button = Ui.Action("🔗 " + (link.Title.Length > 0 ? link.Title : address), () => ribbon.OpenLink(address), tooltip: address);
                    button.AddThemeFontSizeOverride("font_size", 11);
                    linkRow.AddChild(button);
                }
            }
            Bind.When(row, note, Refresh, nameof(note.Number), nameof(note.Anchor), nameof(note.Text), nameof(note.Links));
            return row;
        });
        var scroll = Ui.Scroll(list);
        scroll.CustomMinimumSize = new Vector2(0, 96);
        scroll.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        var box = Ui.Margin(Ui.Column(4, EveStyle.Caption("FOOTNOTES", EveStyle.Cyan, 11), scroll), 12, 6, 12, 8);
        box.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        void Show() => Bind.OnMain(() =>
        {
            if (GodotObject.IsInstanceValid(box))
                box.Visible = vm.Chapter.Footnotes.Count > 0;
        });
        System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) => Show();
        vm.Chapter.Footnotes.CollectionChanged += changed;
        Bind.OnGone(box, () => vm.Chapter.Footnotes.CollectionChanged -= changed);
        Show();
        return box;
    }

    /// <summary>
    /// 🧪 AI-Check: the model of the chat compares the text with the description, the perception goal and the ambiance
    /// of the chapter (the waiting popup, Cancel); its analysis opens in a window that offers ✍ REWRITE IT.
    /// </summary>
    private static async Task AiCheckAsync(ChapterTextWindowViewModel vm, Shell shell, Control host, TapeEditor editor, TapeRibbon ribbon)
    {
        var text = vm.Chapter.Text.Trim();
        if (text.Length == 0)
        {
            ribbon.ShowStatus("Write the chapter first: the AI-Check reads its text", EveStyle.Amber);
            return;
        }
        if (!vm.CanEvaluate)
        {
            ribbon.ShowStatus("No LLM available for the AI-Check", EveStyle.Red);
            return;
        }
        var model = vm.CurrentModelName ?? "LLM";
        var (analysis, error) = await AskAsync(host, "AI-Check", $"{model} compares the chapter with its description, perception goal and ambiance…",
            vm.CheckAsync, "AI-Check");
        if (analysis is null && error is null)
            return;
        WorkWindows.Evaluation(shell, text, model, analysis, error, "🧪 AI-Check",
            $"Does the chapter match its description, perception goal and ambiance? · {model}",
            "✍ REWRITE IT", () => _ = RewriteAsync(vm, shell, host, editor, analysis!));
    }

    /// <summary>
    /// ✍ REWRITE IT: the chapter rewritten to match its intent and the goals and audience of the writing (General);
    /// the result opens in a window where it can be changed, then replace the chapter (undoable) or go below it.
    /// </summary>
    private static async Task RewriteAsync(ChapterTextWindowViewModel vm, Shell shell, Control host, TapeEditor editor, string analysis)
    {
        var text = vm.Chapter.Text;
        var model = vm.CurrentModelName ?? "LLM";
        var (rewritten, error) = await AskAsync(host, "Rewriting",
            $"{model} rewrites the chapter for its description, perception goal and ambiance, the goals and the audience of the writing…",
            token => vm.RewriteToFitAsync(analysis, token), "AI rewriting");
        if (rewritten is null && error is null)
            return;
        WorkWindows.Translation(shell, text, "", model, rewritten, error,
            result =>
            {
                if (!GodotObject.IsInstanceValid(editor))
                    return false;
                editor.ReplaceAll(result);
                return true;
            },
            result =>
            {
                if (!GodotObject.IsInstanceValid(editor))
                    return false;
                editor.ReplaceAll(editor.Text.TrimEnd() + "\n\n" + result);
                return true;
            },
            $"✍ Rewritten · {vm.Chapter.Position}. {vm.Chapter.Title}", "Rewriting", "REPLACE THE CHAPTER", "ADD BELOW");
    }

    /// <summary>
    /// Runs <paramref name="ask"/> with the waiting popup over <paramref name="host"/> (Cancel, ten minutes at most):
    /// its answer, or the error; both null when it was cancelled.
    /// </summary>
    internal static async Task<(string? Answer, string? Error)> AskAsync(Control host, string heading, string message,
        Func<CancellationToken, Task<string>> ask, string what)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var busy = BusyPopup.Show(host, heading, message, cancel);
        try
        {
            return (await ask(cancel.Token), null);
        }
        catch (OperationCanceledException) when (GodotObject.IsInstanceValid(busy) && busy.IsCancelledByUser)
        {
            return (null, null);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException
                                       or OperationCanceledException or System.Text.Json.JsonException)
        {
            ActivityLog.Error("GhostWriter", $"{what} failed", ex);
            return (null, ex is OperationCanceledException ? "no answer after 10 minutes" : ex.Message);
        }
        finally
        {
            busy.Finish();
        }
    }

    /// <summary>🖼: pictures picked, copied into Pictures/kaonomi/write, inserted at the caret.</summary>
    private static async Task InsertPicturesAsync(ChapterTextWindowViewModel vm, TapeEditor editor)
    {
        var files = await Dialogs.OpenFiles("Insert pictures",
            ["*.jpg,*.jpeg,*.png,*.gif,*.webp,*.bmp,*.heic,*.heif,*.tif,*.tiff ; Pictures", "* ; All files"], ChatSettings.SystemPicturesDirectory);
        if (files.Length == 0 || !GodotObject.IsInstanceValid(editor))
            return;
        var copies = vm.CopyPictures(files);
        if (copies.Count == 0)
            return;
        editor.InsertImages(copies);
        vm.Writing.SetStatus($"{copies.Count} picture(s) copied into {GhostWriterWindowViewModel.PicturesDirectory} and inserted", false);
    }
}
