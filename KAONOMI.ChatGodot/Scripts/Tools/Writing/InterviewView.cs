using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Work;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// /interview: the interviews. On the left, their search (the person, the notes; empty: the ones changed last) and
/// their list, a click opens one. In the middle, the person interviewed (last name, first name, email, phone, address,
/// passport or identity card and its number), then the notes with the editor and the ribbon of /work: Home (Interview:
/// New, Save, Delete; Recording: ⏺ Record / ⏹ Stop, 🗣 Live, 📝 Transcribe, ⤵ Insert, 📁 Folder; font, paragraph,
/// styles), Insert (pictures, links, code), View (zoom). On the right, the recording running (its length, the level of
/// the microphone), the recordings (WAV files of ~/kaonomi/audio/interview) and the transcription, a paragraph for
/// what is said between two silences of a second. ⌘/Ctrl S saves, ⌘/Ctrl K makes a link. The title of the window is
/// the name of the person.
/// </summary>
[ToolView(typeof(InterviewWindowViewModel), "Interviews", "🎙", 1480, 900)]
public sealed class InterviewView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (InterviewWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;

        var root = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var overlay = new Overlay();
        var editor = new TapeEditor
        {
            KeepSource = true,
            Placeholder = "Write the notes of the interview here… select text to format it (the ribbon above, or ⌘/Ctrl B, I, U); ⏺ Record records it, ⤵ Insert puts its transcription here."
        };
        var transient = Eve.Eve.Text_("", Eve.Eve.Green, 11);
        var ribbon = new TapeRibbon(editor, overlay, shell, root, transient, vm, "Interview", "the interview", "interview",
            _ => InterviewWindowViewModel.InterviewPicturesDirectory);

        // The notes, both ways (Markdown).
        editor.TextCommitted += text => vm.Content = text;
        Bind.When(editor, vm, () => editor.Text = vm.Content, nameof(vm.Content));
        editor.LoadImage = async path => AvaloniaBridge.ToTexture(await vm.LoadImageAsync(path));
        editor.OpenImage = PictureLibrary.OpenWithSystem;

        // The title of the window: the person.
        void UpdateTitle()
        {
            if (!GodotObject.IsInstanceValid(root) || shell.WindowOf(root) is not { } window)
                return;
            var name = vm.PersonName;
            window.Title = name.Length == 0 ? "Interviews" : $"Interview · {name}";
        }
        Bind.When(root, vm, UpdateTitle, nameof(vm.PersonName));
        root.Ready += () => Callable.From(UpdateTitle).CallDeferred();

        // ── Left: the interviews ──
        var search = Ui.Line(vm, nameof(vm.Search), "Search the interviews (name, email, number, notes)… · Enter");
        search.TextSubmitted += _ => Bind.Run(vm.SearchCommand);
        var interviews = new VBoxContainer();
        interviews.AddThemeConstantOverride("separation", 2);
        var noInterview = Ui.Placeholder("No interview yet");
        Bind.Items(interviews, vm.Interviews, interview => InterviewRow(vm, interview), noInterview);
        var interviewsScroll = Ui.Scroll(Ui.Column(2, interviews, noInterview));
        interviewsScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var left = Ui.Column(8,
            Ui.Row(6, search, Ui.Command("⟳", vm.SearchCommand, tooltip: "Search (Enter); empty: the interviews changed last")),
            interviewsScroll);
        left.CustomMinimumSize = new Vector2(280, 0);
        var leftHolder = Ui.Margin(left, 10, 10, 0, 10);

        // ── Middle: the person, the ribbon, the notes ──
        // Its version, and the entities the NER found at the last save, in a few words (the whole of them in 📊 Report).
        var entities = Ui.Label(vm, nameof(vm.EntitiesSummary), Eve.Eve.Cyan, 11);
        entities.ClipText = true;
        entities.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var info = Ui.Column(2, Ui.Label(vm, nameof(vm.Info), Eve.Eve.Muted, 11), entities);
        info.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        var person = WritingUi.Cells(4,
            Ui.Field("Last name", Ui.Line(vm, nameof(vm.LastName), "Last name")),
            Ui.Field("First name", Ui.Line(vm, nameof(vm.FirstName), "First name")),
            Ui.Field("Email", Ui.Line(vm, nameof(vm.Email), "name@example.org")),
            Ui.Field("Phone", Ui.Line(vm, nameof(vm.Phone), "+33 …")),
            Ui.Field("Address", Ui.Line(vm, nameof(vm.Address), "Street, postcode, city, country")),
            Ui.Field("Identity document", WritingUi.Choice(vm, nameof(vm.DocumentType), vm.DocumentTypes)),
            Ui.Field("Passport / identity card number", Ui.Line(vm, nameof(vm.DocumentNumber), "Number of the passport or identity card")),
            info);

        // The groups of the interview and of its recording.
        var newInterview = TapeRibbon.BigTool("🎙", "New", "A new interview", () => Bind.Run(vm.NewInterviewCommand));
        var save = TapeRibbon.BigTool("💾", "Save", "Save the interview (a new version each time) · ⌘/Ctrl S", () => Bind.Run(vm.SaveCommand));
        var delete = TapeRibbon.BigTool("🗑", "Delete", "Delete the interview (kept marked deleted in the search engine; its recordings stay)", () => Bind.Run(vm.DeleteCommand));
        var interviewGroup = TapeRibbon.Group("Interview", false, newInterview, save, delete);

        var record = TapeRibbon.BigTool("⏺", "Record", string.Empty, () => Bind.Run(vm.ToggleRecordingCommand), Eve.Eve.Red);
        Bind.When(record, vm, () =>
        {
            Face(record, vm.IsRecording ? "⏹" : "⏺", vm.IsRecording ? "Stop" : "Record");
            TapeRibbon.SetOn(record, vm.IsRecording);
            record.TooltipText = vm.IsRecording
                ? "Stop the recording: its WAV file is closed, it is transcribed (a paragraph per silence of a second) and the interview is saved"
                : $"Record the interview from the microphone into a WAV file of {InterviewWindowViewModel.AudioDirectory}";
        }, nameof(vm.IsRecording));
        var live = TapeRibbon.BigTool("🗣", "Live", "Live transcription: each paragraph is written (and stored in the search engine) as soon as the silence of a second that ends it is heard, while the interview is recorded",
            () => vm.LiveTranscription = !vm.LiveTranscription);
        Bind.When(live, vm, () => TapeRibbon.SetOn(live, vm.LiveTranscription), nameof(vm.LiveTranscription));
        var transcribe = TapeRibbon.BigTool("📝", "Transcribe", "Transcribe the last recording again with Whisper: its paragraphs replace the ones stored in the search engine", () => Bind.Run(vm.TranscribeCommand));
        var insert = TapeRibbon.BigTool("⤵", "Insert", "Put the transcription at the end of the notes (each paragraph after its time)", () => Bind.Run(vm.InsertTranscriptCommand));
        var folder = TapeRibbon.BigTool("📁", "Folder", $"Show the folder of the recordings ({InterviewWindowViewModel.AudioDirectory})", () =>
        {
            System.IO.Directory.CreateDirectory(InterviewWindowViewModel.AudioDirectory);
            OS.ShellShowInFileManager(InterviewWindowViewModel.AudioDirectory);
        });
        var recordGroup = TapeRibbon.Group("Recording", false, record, live, transcribe, insert, folder);

        // The deep text analysis (the emotions of each paragraph, the entities added to the ontology) and its report.
        var deep = TapeRibbon.BigTool("🧠", "Deep analysis", "Deep text analysis of the notes and the transcription by the model of the chat: the emotions of each paragraph, and the people, organisations and places of the interview added to the ontology",
            () => Bind.Run(vm.DeepAnalysisCommand));
        var report = TapeRibbon.BigTool("📊", "Report", "The report of the interview: the emotions by paragraph, the entities found by the NER at the last save, the entities added to the ontology",
            () => Bind.Run(vm.ShowReportCommand));
        var audio = TapeRibbon.BigTool("🎚", "Audio analysis", "Audio analysis of the recordings: the pitch of the voice and how much it moves, its level, the speed of the words and the pauses, over each recording and each paragraph of its transcription",
            () => Bind.Run(vm.AnalyzeAudioCommand));
        var analysisGroup = TapeRibbon.Group("Analysis", false, deep, audio, report);

        var picturesTooltip = $"Insert pictures at the caret (copied into {InterviewWindowViewModel.InterviewPicturesDirectory}); resize them with the grip of their corner";
        var ribbonPanel = ribbon.Build(transient,
            ("Home", [interviewGroup, recordGroup, analysisGroup, ribbon.FontGroup(), ribbon.ParagraphGroup(), ribbon.StylesGroup(last: true)]),
            ("Insert", [ribbon.PicturesGroup(picturesTooltip, () => _ = InsertPicturesAsync(vm, editor),
                $"Generate an image with Fooocus from a prompt, then insert it at the caret (saved in {InterviewWindowViewModel.InterviewPicturesDirectory})"),
                ribbon.LinksGroup(), ribbon.CodeGroup()]),
            ("View", [ribbon.ZoomGroup()]));
        var scroll = Ui.Scroll(editor);
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var page = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.AddThemeStyleboxOverride("panel", Eve.Eve.Box(WorkPalette.Page, Eve.Eve.Edge with { A = 0.45f }, 0));
        page.AddChild(Ui.Column(0, ribbonPanel, scroll));
        var middle = Ui.Column(10, person, page);
        middle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        middle.CustomMinimumSize = new Vector2(420, 0);
        var middleHolder = Ui.Margin(middle, 10, 10, 6, 10);
        middleHolder.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        middleHolder.SizeFlagsStretchRatio = 2.4f;

        // ── Right: the recording running, the recordings, the transcription ──
        var clock = Ui.Label(vm, nameof(vm.RecordingTime), Eve.Eve.Red, 22);
        clock.AddThemeFontOverride("font", Eve.Eve.Mono);
        var meter = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.001,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(60, 6),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        // The square root: a voice at a normal distance peaks around 0.1.
        Bind.When(meter, vm, () => meter.Value = Math.Sqrt(Math.Clamp(vm.Level, 0, 1)), nameof(vm.Level));
        var running = Bind.Visible(Ui.Row(8, Eve.Eve.Text_("⏺", Eve.Eve.Red, 16), clock, meter), vm, nameof(vm.IsRecording));

        // The audio source of the next recording, and ⟳ to read the sources of the system again.
        var source = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
            FitToLongestItem = false,
            TooltipText = "The audio source recorded: the microphone of the chat, or any input of the system (another microphone, a line input, a loopback such as Stereo Mix); used from the next recording"
        };
        void SelectSource()
        {
            for (var i = 0; i < source.ItemCount; i++)
                if (source.GetItemText(i) == vm.AudioSource)
                {
                    source.Select(i);
                    return;
                }
        }
        void FillSources()
        {
            if (!GodotObject.IsInstanceValid(source))
                return;
            source.Clear();
            foreach (var name in vm.AudioSources)
                source.AddItem(name);
            SelectSource();
        }
        NotifyCollectionChangedEventHandler sourcesChanged = (_, _) => Bind.OnMain(FillSources);
        vm.AudioSources.CollectionChanged += sourcesChanged;
        Bind.OnGone(source, () => vm.AudioSources.CollectionChanged -= sourcesChanged);
        FillSources();
        Bind.When(source, vm, SelectSource, nameof(vm.AudioSource));
        source.ItemSelected += index => vm.AudioSource = source.GetItemText((int)index);
        var refreshSources = Ui.Command("⟳", vm.RefreshAudioSourcesCommand, tooltip: "Read the audio sources of the system again (a microphone plugged in since)");
        refreshSources.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        var sourceRow = Ui.Row(6, Ui.Field("Audio source", source), refreshSources);

        var recordings = new VBoxContainer();
        recordings.AddThemeConstantOverride("separation", 2);
        var noRecording = Ui.Placeholder("No recording yet: ⏺ Record");
        Bind.Items(recordings, vm.Recordings, recording => RecordingRow(vm, recording), noRecording);
        var recordingsScroll = Ui.Scroll(Ui.Column(2, recordings, noRecording));
        recordingsScroll.CustomMinimumSize = new Vector2(0, 150);

        // The player of the recordings, in the window: shown by ▶ of a recording or a click on the time of a paragraph.
        var playerTitle = Ui.Label(vm, nameof(vm.PlayerTitle), Eve.Eve.TextBright, 12);
        playerTitle.ClipText = true;
        playerTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        playerTitle.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var closePlayer = Ui.Action("✕", () => Bind.Run(vm.ClosePlayerCommand), tooltip: "Stop and close the player");
        var back = Ui.Action("⏪", () => Bind.Run(vm.SkipPlayerCommand, "-10"), tooltip: "10 seconds back");
        var toggle = Ui.Action("⏸", () => Bind.Run(vm.TogglePlayCommand), tooltip: "Pause / play");
        Bind.When(toggle, vm, () => toggle.Text = vm.IsPlaying ? "⏸" : "▶", nameof(vm.IsPlaying));
        var forward = Ui.Action("⏩", () => Bind.Run(vm.SkipPlayerCommand, "10"), tooltip: "10 seconds forward");
        var playerTime = Ui.Label(vm, nameof(vm.PlayerTime), Eve.Eve.Muted, 11);
        playerTime.AddThemeFontOverride("font", Eve.Eve.Mono);
        var slider = new HSlider
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.1,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(60, 0),
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Where the player is in the recording: drag it, or click"
        };
        // The slider follows the player (not while it is dragged); moved by hand, it moves the player.
        var dragging = false;
        Bind.When(slider, vm, () =>
        {
            if (dragging)
                return;
            slider.MaxValue = Math.Max(0.1, vm.PlayerDuration);
            slider.SetValueNoSignal(Math.Clamp(vm.PlayerPosition, 0, slider.MaxValue));
        }, nameof(vm.PlayerPosition), nameof(vm.PlayerDuration));
        slider.DragStarted += () => dragging = true;
        slider.DragEnded += _ =>
        {
            dragging = false;
            vm.SeekPlayer(slider.Value);
        };
        slider.ValueChanged += value =>
        {
            if (!dragging)
                vm.SeekPlayer(value);
        };
        var player = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        player.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.25f), Eve.Eve.Cyan with { A = 0.6f }, 8));
        player.AddChild(Ui.Column(6, Ui.Row(6, playerTitle, closePlayer), Ui.Row(4, back, toggle, forward, slider, playerTime)));
        Bind.Visible(player, vm, nameof(vm.HasPlayer));

        var paragraphs = new VBoxContainer();
        paragraphs.AddThemeConstantOverride("separation", 10);
        var noParagraph = Ui.Placeholder("No transcription yet");
        Bind.Items(paragraphs, vm.Paragraphs, paragraph => ParagraphRow(vm, paragraph), noParagraph);
        var transcriptScroll = Ui.Scroll(Ui.Column(2, paragraphs, noParagraph));
        transcriptScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        // The last paragraph in view as they come (the live transcription): once the list is laid out again.
        async void Follow()
        {
            if (!GodotObject.IsInstanceValid(transcriptScroll) || !transcriptScroll.IsInsideTree())
                return;
            var tree = transcriptScroll.GetTree();
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (GodotObject.IsInstanceValid(transcriptScroll))
                transcriptScroll.ScrollVertical = (int)transcriptScroll.GetVScrollBar().MaxValue;
        }
        NotifyCollectionChangedEventHandler follow = (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                Bind.OnMain(Follow);
        };
        vm.Paragraphs.CollectionChanged += follow;
        Bind.OnGone(transcriptScroll, () => vm.Paragraphs.CollectionChanged -= follow);

        var cancel = Bind.Visible(Ui.Command("CANCEL", vm.CancelTranscriptionCommand, tooltip: "Stop the transcription running"), vm, nameof(vm.IsTranscribing));
        // Two tabs: the transcription (a paragraph per silence of 1 s), and the results of the analyses, in view as
        // soon as an interview that has some is opened.
        var transcriptTab = Ui.Action("Transcription", () => Bind.Run(vm.ShowTabCommand, "transcript"), tooltip: "The transcription: a paragraph for what is said between two silences of a second");
        var resultsTab = Ui.Action("Analyses", () => Bind.Run(vm.ShowTabCommand, "results"), tooltip: "The results of every analysis: the voice (audio), the emotions, the entities of the NER, the ontology");
        transcriptTab.ToggleMode = resultsTab.ToggleMode = true;
        Bind.When(resultsTab, vm, () =>
        {
            resultsTab.Text = vm.HasResults ? "Analyses ●" : "Analyses";
            transcriptTab.SetPressedNoSignal(!vm.ShowResults);
            resultsTab.SetPressedNoSignal(vm.ShowResults);
        }, nameof(vm.ShowResults), nameof(vm.HasResults));
        Bind.Visible(transcriptScroll, vm, nameof(vm.ShowResults), invert: true);
        // The results: their Markdown drawn again each time they change.
        var results = Ui.Markdown(vm, nameof(vm.Results));
        var resultsScroll = Ui.Scroll(Ui.Margin(results, 0, 0, 10, 0));
        resultsScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        Bind.Visible(resultsScroll, vm, nameof(vm.ShowResults));
        var right = Ui.Column(8,
            sourceRow,
            running,
            Eve.Eve.Caption("Recordings", Eve.Eve.Muted, 11),
            recordingsScroll,
            player,
            Ui.Row(6, transcriptTab, resultsTab, Ui.Spring(), cancel),
            transcriptScroll,
            resultsScroll);
        right.CustomMinimumSize = new Vector2(300, 0);
        var rightHolder = Ui.Margin(right, 6, 10, 10, 10);
        rightHolder.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var work = new HSplitContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        work.AddChild(middleHolder);
        work.AddChild(rightHolder);
        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddChild(leftHolder);
        split.AddChild(work);

        // Status: busy, error dot, the text, transcribing, not saved.
        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        if (status.GetChild(0) is HBoxContainer statusRow)
        {
            var transcribing = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(60, 4), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            // The summary note is written by the model after a save, in the background.
            statusRow.AddChild(Bind.Visible(Eve.Eve.Text_("📝 writing the summary note…", Eve.Eve.Cyan, 12), vm, nameof(vm.IsSummarising)));
            statusRow.AddChild(Bind.Visible(transcribing, vm, nameof(vm.IsTranscribing)));
            statusRow.AddChild(Bind.Visible(Eve.Eve.Text_("● not saved", Eve.Eve.Amber, 12), vm, nameof(vm.IsDirty)));
        }

        var content = Ui.Column(0, split, status);
        content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        root.AddChild(content);
        root.AddChild(overlay);

        // While the deep text analysis runs: the waiting popup of /work over the whole window (a ring of light turning
        // around a pulsing ✨, what is being done, the time elapsed, Cancel).
        BusyPopup? busy = null;
        Bind.When(root, vm, () =>
        {
            if (vm.IsAnalyzing && busy is null)
            {
                var cancel = new System.Threading.CancellationTokenSource();
                cancel.Token.Register(() => Bind.OnMain(() => Bind.Run(vm.CancelAnalysisCommand)));
                busy = BusyPopup.Show(root, "Analysis of the interview", vm.AnalysisStep, cancel);
            }
            else if (!vm.IsAnalyzing && busy is not null)
            {
                busy.Finish();
                busy = null;
            }
            if (busy is not null && GodotObject.IsInstanceValid(busy) && vm.AnalysisStep.Length > 0)
                busy.Message = vm.AnalysisStep;
        }, nameof(vm.IsAnalyzing), nameof(vm.AnalysisStep));

        // The report (the end of the analysis, or 📊 Report): a window with its Markdown, Copy, and Insert in the notes.
        void ShowReport(string markdown) => Bind.OnMain(() =>
        {
            if (GodotObject.IsInstanceValid(root))
                WorkWindows.Evaluation(shell, vm.PersonName.Length > 0 ? vm.PersonName : "Interview", string.Empty, markdown, null,
                    "📊 Analyses", "The voice (audio), the emotions of each paragraph, the entities found by the NER of the server, the entities added to the ontology",
                    "INSERT IN THE NOTES", () => vm.InsertReport(markdown));
        });
        vm.ReportReady += ShowReport;
        Bind.OnGone(root, () => vm.ReportReady -= ShowReport);

        // Shortcuts: ⌘/Ctrl S saves, ⌘/Ctrl K makes a link.
        WritingUi.Shortcuts(root, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SaveCommand);
            if (WritingUi.IsCommand(key, Key.K) && editor.HasFocus())
            {
                ribbon.ShowLinkFlyout();
                return true;
            }
            return false;
        });
        return root;
    }

    /// <summary>The icon and the name of a big tool of the ribbon (⏺ Record becomes ⏹ Stop).</summary>
    private static void Face(Button tool, string icon, string label)
    {
        if (tool.GetChildCount() == 0 || tool.GetChild(0) is not VBoxContainer column || column.GetChildCount() < 2)
            return;
        if (column.GetChild(0) is Label glyph)
            glyph.Text = icon;
        if (column.GetChild(1) is Label name)
            name.Text = label;
    }

    /// <summary>An interview of the list: the person, its date, its recordings and the start of its notes; the one open is outlined.</summary>
    private static Control InterviewRow(InterviewWindowViewModel vm, InterviewEntry interview)
    {
        var name = Eve.Eve.Text_(interview.Person, Eve.Eve.TextBright, 13);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var date = Eve.Eve.Text_(interview.DateLabel, Eve.Eve.Muted, 10);
        var details = WritingUi.Clamp(Eve.Eve.Text_(interview.Details, Eve.Eve.Text with { A = 0.7f }, 11), 1);
        var button = WritingUi.ResultRow(vm, nameof(vm.InterviewId), () => vm.InterviewId == interview.Id,
            () => Bind.Run(vm.OpenEntryCommand, interview), Ui.Column(1, Ui.Row(6, name, date), details), 54);
        button.TooltipText = interview.Summary.Length > 0 ? interview.Summary : interview.Person;
        return button;
    }

    /// <summary>A recording: its file, when it was made, its length and its transcription; ▶ plays it, 📝 transcribes it again.</summary>
    private static Control RecordingRow(InterviewWindowViewModel vm, InterviewRecordingEntry recording)
    {
        var file = Eve.Eve.Text_(recording.FileName, Eve.Eve.TextBright, 12);
        file.ClipText = true;
        file.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var label = Eve.Eve.Text_(recording.Label, Eve.Eve.Muted, 10);
        var texts = Ui.Column(1, file, label);
        texts.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var play = Ui.Action("▶", () => Bind.Run(vm.PlayRecordingCommand, recording), tooltip: "Play the recording in the player of the window");
        var again = Ui.Action("📝", () => Bind.Run(vm.TranscribeCommand, recording), tooltip: "Transcribe this recording again: its paragraphs replace the ones stored");
        play.SizeFlagsVertical = again.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var row = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = recording.Path };
        row.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.15f), Eve.Eve.Edge with { A = 0.15f }, 6));
        row.AddChild(Ui.Row(6, texts, play, again));
        return row;
    }

    /// <summary>A paragraph of the transcription: where it starts in its recording, then what was said.</summary>
    private static Control ParagraphRow(InterviewWindowViewModel vm, InterviewParagraphEntry paragraph)
    {
        // Its time: a click plays the recording from there; lit while the player is in the paragraph.
        var time = new LinkButton
        {
            Text = "▶ " + paragraph.TimeLabel,
            Underline = LinkButton.UnderlineMode.OnHover,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Play the recording from this paragraph"
        };
        time.AddThemeFontOverride("font", Eve.Eve.Mono);
        time.AddThemeFontSizeOverride("font_size", 10);
        time.Pressed += () => Bind.Run(vm.PlayParagraphCommand, paragraph);
        Bind.When(time, vm, () =>
        {
            var playing = vm.PlayingRecording?.FileName == paragraph.AudioFile && Math.Abs(vm.PlayingParagraphStart - paragraph.StartSeconds) < 0.001;
            var color = playing ? Eve.Eve.Red : Eve.Eve.Cyan;
            time.AddThemeColorOverride("font_color", color);
            time.AddThemeColorOverride("font_hover_color", color);
        }, nameof(vm.PlayingParagraphStart), nameof(vm.PlayingRecording));
        var text = Eve.Eve.Text_(paragraph.Text, Eve.Eve.Text, 13, wrap: true);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        text.CustomMinimumSize = new Vector2(1, 0);
        // Its emotion, from the deep text analysis (every emotion in the tooltip).
        var head = Ui.Row(10, time);
        if (paragraph.EmotionLabel.Length > 0)
        {
            var emotion = Eve.Eve.Text_(paragraph.EmotionLabel, Eve.Eve.Amber, 10);
            emotion.TooltipText = paragraph.EmotionDetails;
            emotion.MouseFilter = Control.MouseFilterEnum.Pass;
            head.AddChild(emotion);
        }
        // Its voice, from the audio analysis (every figure in the tooltip).
        if (paragraph.Voice is { } voice)
        {
            var figures = Eve.Eve.Text_(voice.Label, Eve.Eve.Muted, 10);
            figures.TooltipText = voice.Details;
            figures.MouseFilter = Control.MouseFilterEnum.Pass;
            head.AddChild(figures);
        }
        var column = Ui.Column(2, head, text);
        column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return column;
    }

    /// <summary>🖼: pictures picked, copied into the folder of the interviews, inserted at the caret.</summary>
    private static async Task InsertPicturesAsync(InterviewWindowViewModel vm, TapeEditor editor)
    {
        var files = await Dialogs.OpenFiles("Insert pictures",
            ["*.jpg,*.jpeg,*.png,*.gif,*.webp,*.bmp,*.heic,*.heif,*.tif,*.tiff ; Pictures", "* ; All files"], vm.PicturesDirectory);
        if (files.Length == 0 || !GodotObject.IsInstanceValid(editor))
            return;
        var copies = vm.CopyPictures(files);
        if (copies.Count == 0)
            return;
        editor.InsertImages(copies);
        vm.Status = $"{copies.Count} picture(s) copied into the interviews and inserted";
    }
}
