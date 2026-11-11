using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
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
/// /interview: the interviews. On the left, their search (the person, the notes; empty: the ones changed last) and their
/// list, a click opens one. In the middle, the person interviewed (last name, first name, email, phone, address, passport
/// or identity card and its number), then the notes, edited as they read with the editor and the ribbon of /work: Home
/// (Interview: New, Save, Delete; Recording: ⏺ Record / ⏹ Stop, 🎙 Live, 📝 Transcribe, ⤵ Insert, 📁 Folder; font,
/// paragraph, styles), Insert (pictures, links, code), View (zoom). On the right, the recording running (its length,
/// the level of the microphone), the recordings (WAV files of ~/kaonomi/audio/interview) and the transcription, a
/// paragraph for what is said between two silences of a second. ⌘/Ctrl S saves, ⌘/Ctrl K makes a link. The title of the
/// window is the name of the person.
/// </summary>
[ToolView(typeof(InterviewWindowViewModel), "Interviews", "🎙", 1480, 900)]
public sealed class InterviewView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (InterviewWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;

        var tape = new TapePage(shell, vm, "Interview", "the interview", "interview", _ => InterviewWindowViewModel.InterviewPicturesDirectory,
            "Write the notes of the interview here… select text to format it (the ribbon above, or ⌘/Ctrl B, I, U); ⏺ Record records it, ⤵ Insert puts its transcription here.");
        var editor = tape.Editor;
        var ribbon = tape.Ribbon;
        tape.Bind(vm, nameof(vm.Content));
        editor.LoadImage = vm.LoadImageAsync;
        editor.OpenImage = PictureLibrary.OpenWithSystem;

        // ── Left: the interviews ──
        var search = WritingUi.Line(vm, nameof(vm.Search), "Search the interviews (name, email, number, notes)… · Enter");
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Ui.Run(vm.SearchCommand);
                e.Handled = true;
            }
        };
        var searchButton = WritingUi.Command("⟳", vm.SearchCommand, tooltip: "Search (Enter); empty: the interviews changed last");
        searchButton.Margin = new Thickness(6, 0, 0, 0);
        var interviews = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var noInterview = WritingUi.Placeholder("No interview yet");
        noInterview.VerticalAlignment = VerticalAlignment.Top;
        WritingUi.Items(interviews, vm.Interviews, interview => InterviewRow(vm, interview), noInterview);
        var searchRow = Ui.Dock((searchButton, Dock.Right), (search, Dock.Left));
        searchRow.Margin = new Thickness(0, 0, 0, 8);
        var left = WritingUi.Page([searchRow], new Panel { Children = { Ui.Scroll(interviews), noInterview } });
        left.MinWidth = 240;
        var leftHolder = WritingUi.Margin(left, 10, 10, 6, 10);

        // ── Middle: the person, the ribbon, the notes ──
        var lastName = WritingUi.Line(vm, nameof(vm.LastName), "Last name");
        var firstName = WritingUi.Line(vm, nameof(vm.FirstName), "First name");
        var email = WritingUi.Line(vm, nameof(vm.Email), "name@example.org");
        var phone = WritingUi.Line(vm, nameof(vm.Phone), "+33 …");
        var address = WritingUi.Line(vm, nameof(vm.Address), "Street, postcode, city, country");
        var documentType = Ui.Combo(vm.DocumentTypes, vm, nameof(vm.DocumentType));
        var documentNumber = WritingUi.Line(vm, nameof(vm.DocumentNumber), "Number of the passport or identity card");
        var version = Ui.Bound(vm, nameof(vm.Info), Ui.Muted, 11);
        // The entities the NER found at the last save, in a few words (the whole of them in 📊 Report).
        var entities = Ui.Bound(vm, nameof(vm.EntitiesSummary), Ui.Accent, 11);
        entities.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        var info = Ui.Column(2, version, entities);
        info.VerticalAlignment = VerticalAlignment.Bottom;
        info.Margin = new Thickness(0, 0, 0, 6);
        var person = WritingUi.Cells(4,
            WritingUi.Field("Last name", lastName), WritingUi.Field("First name", firstName),
            WritingUi.Field("Email", email), WritingUi.Field("Phone", phone),
            WritingUi.Field("Address", address), WritingUi.Field("Identity document", documentType),
            WritingUi.Field("Passport / identity card number", documentNumber), info);
        person.Margin = new Thickness(0, 0, 0, 10);

        // The groups of the interview and of its recording.
        var newInterview = TapeRibbon.BigTool("🎙", "New", "A new interview", () => Ui.Run(vm.NewInterviewCommand));
        var save = TapeRibbon.BigTool("💾", "Save", "Save the interview (a new version each time) · ⌘/Ctrl S", () => Ui.Run(vm.SaveCommand));
        var delete = TapeRibbon.BigTool("🗑", "Delete", "Delete the interview (kept marked deleted in the search engine; its recordings stay)", () => Ui.Run(vm.DeleteCommand));
        var interviewGroup = TapeRibbon.Group("Interview", false, newInterview, save, delete);

        var record = TapeRibbon.BigTool("⏺", "Record", string.Empty, () => Ui.Run(vm.ToggleRecordingCommand), Ui.Red);
        Ui.When(record, vm, () =>
        {
            Face(record, vm.IsRecording ? "⏹" : "⏺", vm.IsRecording ? "Stop" : "Record");
            TapeRibbon.SetOn(record, vm.IsRecording);
            ToolTip.SetTip(record, vm.IsRecording
                ? "Stop the recording: its WAV file is closed, it is transcribed (a paragraph per silence of a second) and the interview is saved"
                : $"Record the interview from the microphone into a WAV file of {InterviewWindowViewModel.AudioDirectory}");
        }, nameof(vm.IsRecording));
        var live = TapeRibbon.BigTool("🗣", "Live", "Live transcription: each paragraph is written (and stored in the search engine) as soon as the silence of a second that ends it is heard, while the interview is recorded",
            () => vm.LiveTranscription = !vm.LiveTranscription);
        Ui.When(live, vm, () => TapeRibbon.SetOn(live, vm.LiveTranscription), nameof(vm.LiveTranscription));
        var transcribe = TapeRibbon.BigTool("📝", "Transcribe", "Transcribe the last recording again with Whisper: its paragraphs replace the ones stored in the search engine", () => Ui.Run(vm.TranscribeCommand));
        var insert = TapeRibbon.BigTool("⤵", "Insert", "Put the transcription at the end of the notes (each paragraph after its time)", () => Ui.Run(vm.InsertTranscriptCommand));
        var folder = TapeRibbon.BigTool("📁", "Folder", $"Show the folder of the recordings ({InterviewWindowViewModel.AudioDirectory})", () =>
        {
            System.IO.Directory.CreateDirectory(InterviewWindowViewModel.AudioDirectory);
            WritingUi.ShowInFolder(InterviewWindowViewModel.AudioDirectory);
        });
        var recordGroup = TapeRibbon.Group("Recording", false, record, live, transcribe, insert, folder);

        // The deep text analysis (the emotions of each paragraph, the entities added to the ontology) and its report.
        var deep = TapeRibbon.BigTool("🧠", "Deep analysis", "Deep text analysis of the notes and the transcription by the model of the chat: the emotions of each paragraph, and the people, organisations and places of the interview added to the ontology",
            () => Ui.Run(vm.DeepAnalysisCommand));
        var report = TapeRibbon.BigTool("📊", "Report", "The report of the interview: the emotions by paragraph, the entities found by the NER at the last save, the entities added to the ontology",
            () => Ui.Run(vm.ShowReportCommand));
        var audio = TapeRibbon.BigTool("🎚", "Audio analysis", "Audio analysis of the recordings: the pitch of the voice and how much it moves, its level, the speed of the words and the pauses, over each recording and each paragraph of its transcription",
            () => Ui.Run(vm.AnalyzeAudioCommand));
        var analysisGroup = TapeRibbon.Group("Analysis", false, deep, audio, report);

        var picturesTooltip = $"Insert pictures at the caret (copied into {InterviewWindowViewModel.InterviewPicturesDirectory}); resize them with the grip of their corner";
        var page = tape.Build(
            ("Home", [interviewGroup, recordGroup, analysisGroup, ribbon.FontGroup(), ribbon.ParagraphGroup(), ribbon.StylesGroup(last: true)]),
            ("Insert", [ribbon.PicturesGroup(picturesTooltip, () => _ = InsertPicturesAsync(vm, editor),
                $"Generate an image with Fooocus from a prompt, then insert it at the caret (saved in {InterviewWindowViewModel.InterviewPicturesDirectory})"),
                ribbon.LinksGroup(), ribbon.CodeGroup()]),
            ("View", [ribbon.ZoomGroup()]));
        var middleHolder = WritingUi.Margin(WritingUi.Page([person], page), 6, 10, 6, 10);

        // ── Right: the recording running, the recordings, the transcription ──
        var clock = Ui.Bound(vm, nameof(vm.RecordingTime), Ui.Red, 22);
        clock.FontFamily = Ui.Mono;
        var meter = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, MinHeight = 6, MinWidth = 60, VerticalAlignment = VerticalAlignment.Center };
        // The square root: a voice at a normal distance peaks around 0.1.
        Ui.When(meter, vm, () => meter.Value = Math.Sqrt(Math.Clamp(vm.Level, 0, 1)), nameof(vm.Level));
        var recDot = Ui.Label("⏺", Ui.Red, 16);
        recDot.VerticalAlignment = VerticalAlignment.Center;
        recDot.Margin = new Thickness(0, 0, 8, 0);
        clock.Margin = new Thickness(0, 0, 10, 0);
        var running = Ui.Dock((recDot, Dock.Left), (clock, Dock.Left), (meter, Dock.Left)).Visible(vm, nameof(vm.IsRecording));
        running.Margin = new Thickness(0, 0, 0, 8);

        // The audio source of the next recording, and ⟳ to read the sources of the system again.
        var source = Ui.Combo(vm.AudioSources, vm, nameof(vm.AudioSource));
        ToolTip.SetTip(source, "The audio source recorded: the microphone of the chat, or any input of the system (another microphone, a line input, a loopback such as Stereo Mix); used from the next recording");
        var refreshSources = WritingUi.Command("⟳", vm.RefreshAudioSourcesCommand, tooltip: "Read the audio sources of the system again (a microphone plugged in since)");
        refreshSources.Margin = new Thickness(6, 0, 0, 0);
        refreshSources.VerticalAlignment = VerticalAlignment.Bottom;
        var sourceRow = Ui.Dock((refreshSources, Dock.Right), (WritingUi.Field("Audio source", source), Dock.Left));
        sourceRow.Margin = new Thickness(0, 0, 0, 10);

        var recordings = new StackPanel();
        var noRecording = WritingUi.Placeholder("No recording yet: ⏺ Record");
        noRecording.VerticalAlignment = VerticalAlignment.Top;
        WritingUi.Items(recordings, vm.Recordings, recording => RecordingRow(vm, recording), noRecording);
        var recordingsArea = new Panel { Children = { Ui.Scroll(recordings), noRecording }, MaxHeight = 190, MinHeight = 56 };

        // The player of the recordings, in the window: shown by ▶ of a recording or a click on the time of a paragraph.
        var playerTitle = Ui.Bound(vm, nameof(vm.PlayerTitle), Ui.TextBright, 12);
        playerTitle.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        playerTitle.VerticalAlignment = VerticalAlignment.Center;
        var closePlayer = Ui.Button("✕", () => Ui.Run(vm.ClosePlayerCommand), "Stop and close the player");
        var back = Ui.Button("⏪", () => Ui.Run(vm.SkipPlayerCommand, "-10"), "10 seconds back");
        var toggle = Ui.Button("⏸", () => Ui.Run(vm.TogglePlayCommand), "Pause / play");
        Ui.When(toggle, vm, () => toggle.Content = vm.IsPlaying ? "⏸" : "▶", nameof(vm.IsPlaying));
        var forward = Ui.Button("⏩", () => Ui.Run(vm.SkipPlayerCommand, "10"), "10 seconds forward");
        var playerTime = Ui.Bound(vm, nameof(vm.PlayerTime), Ui.Muted, 11);
        playerTime.FontFamily = Ui.Mono;
        playerTime.VerticalAlignment = VerticalAlignment.Center;
        playerTime.Margin = new Thickness(8, 0, 0, 0);
        var slider = new Slider { Minimum = 0, Maximum = 1, MinWidth = 60, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(slider, "Where the player is in the recording: drag it, or click");
        // The slider follows the player; moved by hand, it moves the player.
        var following = false;
        Ui.When(slider, vm, () =>
        {
            following = true;
            slider.Maximum = Math.Max(0.1, vm.PlayerDuration);
            slider.Value = Math.Clamp(vm.PlayerPosition, 0, slider.Maximum);
            following = false;
        }, nameof(vm.PlayerPosition), nameof(vm.PlayerDuration));
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && !following)
                vm.SeekPlayer(slider.Value);
        };
        var playerButtons = Ui.Row(4, back, toggle, forward);
        playerButtons.VerticalAlignment = VerticalAlignment.Center;
        var player = new Border
        {
            Child = Ui.Column(6,
                Ui.Dock((closePlayer, Dock.Right), (playerTitle, Dock.Left)),
                Ui.Dock((playerButtons, Dock.Left), (playerTime, Dock.Right), (slider, Dock.Left))),
            Background = Ui.Header,
            BorderBrush = Ui.Accent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0, 8, 8, 0)
        }.Visible(vm, nameof(vm.HasPlayer));

        var paragraphs = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var noParagraph = WritingUi.Placeholder("No transcription yet");
        noParagraph.VerticalAlignment = VerticalAlignment.Top;
        WritingUi.Items(paragraphs, vm.Paragraphs, paragraph => ParagraphRow(vm, paragraph), noParagraph);
        var transcriptScroll = Ui.Scroll(paragraphs);
        // The last paragraph in view as they come (the live transcription).
        NotifyCollectionChangedEventHandler follow = (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                WritingUi.Later(transcriptScroll.ScrollToEnd);
        };
        vm.Paragraphs.CollectionChanged += follow;
        WritingUi.OnGone(transcriptScroll, () => vm.Paragraphs.CollectionChanged -= follow);

        var cancel = WritingUi.Command("CANCEL", vm.CancelTranscriptionCommand, tooltip: "Stop the transcription running").Visible(vm, nameof(vm.IsTranscribing));
        // Two tabs: the transcription (a paragraph per silence of 1 s), and the results of the analyses, in view as
        // soon as an interview that has some is opened.
        var transcriptTab = Ui.Button("Transcription", () => Ui.Run(vm.ShowTabCommand, "transcript"), "The transcription: a paragraph for what is said between two silences of a second");
        var resultsTab = Ui.Button("Analyses", () => Ui.Run(vm.ShowTabCommand, "results"), "The results of every analysis: the voice (audio), the emotions, the entities of the NER, the ontology");
        Ui.When(resultsTab, vm, () =>
        {
            resultsTab.Content = vm.HasResults ? "Analyses ●" : "Analyses";
            transcriptTab.BorderBrush = vm.ShowResults ? Ui.Edge : Ui.Accent;
            resultsTab.BorderBrush = vm.ShowResults ? Ui.Accent : Ui.Edge;
            transcriptTab.Foreground = vm.ShowResults ? Ui.Muted : Ui.TextBright;
            resultsTab.Foreground = vm.ShowResults ? Ui.TextBright : Ui.Muted;
        }, nameof(vm.ShowResults), nameof(vm.HasResults));
        var tabs = Ui.Row(4, transcriptTab, resultsTab);
        var transcriptCaption = Ui.Dock((cancel, Dock.Right), (tabs, Dock.Left));
        transcriptCaption.Margin = new Thickness(0, 10, 0, 6);
        var transcriptArea = new Panel { Children = { transcriptScroll, noParagraph } }.Visible(vm, nameof(vm.ShowResults), invert: true);
        // The results: their Markdown drawn again each time they change.
        var resultsHolder = new ContentControl { Margin = new Thickness(0, 0, 10, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Ui.When(resultsHolder, vm, () => resultsHolder.Content = KAONOMI.UI.Chat.MarkdownView.Build(vm.Results, 12), nameof(vm.Results));
        var resultsArea = Ui.Scroll(resultsHolder).Visible(vm, nameof(vm.ShowResults));
        var recordingsCaption = Ui.Caption("Recordings", Ui.Muted, 11);
        recordingsCaption.Margin = new Thickness(0, 0, 0, 6);
        var right = WritingUi.Page([sourceRow, running, recordingsCaption, recordingsArea, player, transcriptCaption], new Panel { Children = { transcriptArea, resultsArea } });
        var rightHolder = WritingUi.Margin(right, 6, 10, 10, 10);

        var work = WritingUi.SplitH(middleHolder, rightHolder, 0, leftStar: 2.4, rightStar: 1, minLeft: 420, minRight: 260);
        var split = WritingUi.SplitH(leftHolder, work, 280, minLeft: 220, minRight: 700);

        // Status: error dot, busy, the text, transcribing, not saved.
        var transcribing = new ProgressBar { IsIndeterminate = true, Width = 60, MinWidth = 60, Height = 4, MinHeight = 4 }.Visible(vm, nameof(vm.IsTranscribing));
        var dirty = Ui.Label("● not saved", WritingUi.Amber, 12).Visible(vm, nameof(vm.IsDirty));
        // The summary note is written by the model after a save, in the background.
        var summarising = Ui.Label("📝 writing the summary note…", Ui.Accent, 12).Visible(vm, nameof(vm.IsSummarising));
        var status = WritingUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy), extra: [summarising, transcribing, dirty]);
        var page0 = WritingUi.Page([], split, status);
        // A layer over the whole window, for the waiting popup of the deep text analysis.
        var root = new Grid();
        root.Children.Add(page0);

        // While the deep text analysis runs: the waiting popup of /work with a ring turning, what is being done, the time, Cancel.
        BusyPopup? busy = null;
        Spinner? spinner = null;
        Ui.When(root, vm, () =>
        {
            if (vm.IsAnalyzing && busy is null)
            {
                var cancel = new System.Threading.CancellationTokenSource();
                cancel.Token.Register(() => Ui.OnMain(() => Ui.Run(vm.CancelAnalysisCommand)));
                busy = BusyPopup.Show(root, "Analysis of the interview", vm.AnalysisStep, cancel);
                // The ring in place of the still glyph of the popup.
                if (busy.Child is Border { Child: StackPanel column } && column.Children.Count > 0)
                {
                    spinner = new Spinner();
                    column.Children[0] = spinner;
                }
            }
            else if (!vm.IsAnalyzing && busy is not null)
            {
                spinner?.Stop();
                spinner = null;
                busy.Finish();
                busy = null;
            }
            if (busy is not null && vm.AnalysisStep.Length > 0)
                busy.Message = vm.AnalysisStep;
        }, nameof(vm.IsAnalyzing), nameof(vm.AnalysisStep));

        // The report (the end of the analysis, or 📊 Report): a window with its Markdown, Copy, and Insert in the notes.
        void ShowReport(string markdown) => Ui.OnMain(() => WorkWindows.Evaluation(shell, vm.PersonName.Length > 0 ? vm.PersonName : "Interview", string.Empty, markdown, null,
            "📊 Analyses", "The voice (audio), the emotions of each paragraph, the entities found by the NER of the server, the entities added to the ontology",
            "INSERT IN THE NOTES", () => vm.InsertReport(markdown)));
        vm.ReportReady += ShowReport;
        WritingUi.OnGone(root, () => vm.ReportReady -= ShowReport);

        // The title of the window: the person.
        void UpdateTitle()
        {
            var name = vm.PersonName;
            WritingUi.SetTitle(root, name.Length == 0 ? "Interviews" : $"Interview · {name}");
        }
        Ui.When(root, vm, UpdateTitle, nameof(vm.PersonName));
        WritingUi.WhenInWindow(root, UpdateTitle);

        // ⌘/Ctrl S saves, ⌘/Ctrl K makes a link.
        WritingUi.Shortcuts(root, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SaveCommand);
            if (WritingUi.IsCommand(key, Key.K) && editor.IsFocused)
            {
                ribbon.ShowLinkFlyout();
                return true;
            }
            return false;
        });
        return root;
    }

    /// <summary>The animation of the waiting popup: an arc of light turning in a ring, drawn again 30 times a second while it is shown.</summary>
    private sealed class Spinner : Control
    {
        private readonly Avalonia.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
        private double _angle;

        public Spinner()
        {
            Width = Height = 64;
            HorizontalAlignment = HorizontalAlignment.Center;
            _timer.Tick += (_, _) =>
            {
                _angle = (_angle + 7) % 360;
                InvalidateVisual();
            };
            AttachedToVisualTree += (_, _) => _timer.Start();
            DetachedFromVisualTree += (_, _) => _timer.Stop();
        }

        public void Stop() => _timer.Stop();

        public override void Render(Avalonia.Media.DrawingContext context)
        {
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 5;
            context.DrawEllipse(null, new Avalonia.Media.Pen(Ui.Edge, 4), center, radius, radius);
            // The arc: a quarter of the ring, then a shorter and fainter one opposite.
            foreach (var (offset, sweep, opacity) in new[] { (0.0, 100.0, 1.0), (180.0, 50.0, 0.45) })
            {
                var from = (_angle + offset) * Math.PI / 180;
                var to = (_angle + offset + sweep) * Math.PI / 180;
                var geometry = new Avalonia.Media.StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(new Point(center.X + radius * Math.Cos(from), center.Y + radius * Math.Sin(from)), false);
                    path.ArcTo(new Point(center.X + radius * Math.Cos(to), center.Y + radius * Math.Sin(to)), new Size(radius, radius), 0, false, Avalonia.Media.SweepDirection.Clockwise);
                    path.EndFigure(false);
                }
                using (context.PushOpacity(opacity))
                    context.DrawGeometry(null, new Avalonia.Media.Pen(Ui.Accent, 4, lineCap: Avalonia.Media.PenLineCap.Round), geometry);
            }
            var glyph = new Avalonia.Media.FormattedText("🧠", System.Globalization.CultureInfo.CurrentUICulture, Avalonia.Media.FlowDirection.LeftToRight,
                Avalonia.Media.Typeface.Default, 22, Ui.TextBright);
            context.DrawText(glyph, new Point(center.X - glyph.Width / 2, center.Y - glyph.Height / 2));
        }
    }

    /// <summary>The icon and the name of a big tool of the ribbon (⏺ Record becomes ⏹ Stop).</summary>
    private static void Face(Button tool, string icon, string label)
    {
        if (tool.Content is not StackPanel { Children.Count: >= 2 } column)
            return;
        if (column.Children[0] is TextBlock glyph)
            glyph.Text = icon;
        if (column.Children[1] is TextBlock name)
            name.Text = label;
    }

    /// <summary>An interview of the list: the person, its date, its recordings and the start of its notes; the one open is outlined.</summary>
    private static Control InterviewRow(InterviewWindowViewModel vm, InterviewEntry interview)
    {
        var name = Ui.Label(interview.Person, Ui.TextBright, 13);
        var date = Ui.Label(interview.DateLabel, Ui.Muted, 10);
        date.Margin = new Thickness(6, 0, 0, 0);
        var details = WritingUi.Clamp(Ui.Label(interview.Details, Ui.Text, 11), 1);
        var content = Ui.Column(1, Ui.Dock((date, Dock.Right), (name, Dock.Left)), details);
        var row = WritingUi.ResultRow(vm, nameof(vm.InterviewId), () => vm.InterviewId == interview.Id,
            () => Ui.Run(vm.OpenEntryCommand, interview), content, 54);
        ToolTip.SetTip(row, interview.Summary.Length > 0 ? interview.Summary : interview.Person);
        return row;
    }

    /// <summary>A recording: its file, when it was made, its length and its transcription; ▶ plays it, 📝 transcribes it again.</summary>
    private static Control RecordingRow(InterviewWindowViewModel vm, InterviewRecordingEntry recording)
    {
        var file = Ui.Label(recording.FileName, Ui.TextBright, 12);
        file.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        var label = Ui.Label(recording.Label, Ui.Muted, 10);
        var play = Ui.Button("▶", () => Ui.Run(vm.PlayRecordingCommand, recording), "Play the recording in the player of the window");
        var again = Ui.Button("📝", () => Ui.Run(vm.TranscribeCommand, recording), "Transcribe this recording again: its paragraphs replace the ones stored");
        var buttons = Ui.Row(4, play, again);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        buttons.Margin = new Thickness(6, 0, 0, 0);
        var row = new Border
        {
            Child = Ui.Dock((buttons, Dock.Right), (Ui.Column(1, file, label), Dock.Left)),
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5),
            Margin = new Thickness(0, 0, 8, 2)
        };
        ToolTip.SetTip(row, recording.Path);
        return row;
    }

    /// <summary>A paragraph of the transcription: where it starts in its recording, then what was said.</summary>
    private static Control ParagraphRow(InterviewWindowViewModel vm, InterviewParagraphEntry paragraph)
    {
        // Its time: a click plays the recording from there; lit while the player is in the paragraph.
        var time = Ui.Label("▶ " + paragraph.TimeLabel, Ui.Accent, 10);
        time.FontFamily = Ui.Mono;
        time.Cursor = new Cursor(StandardCursorType.Hand);
        time.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(time, "Play the recording from this paragraph");
        time.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(time).Properties.IsLeftButtonPressed)
                return;
            Ui.Run(vm.PlayParagraphCommand, paragraph);
            e.Handled = true;
        };
        Ui.When(time, vm, () =>
        {
            var playing = vm.PlayingRecording?.FileName == paragraph.AudioFile && Math.Abs(vm.PlayingParagraphStart - paragraph.StartSeconds) < 0.001;
            time.Foreground = playing ? Ui.Red : Ui.Accent;
            time.FontWeight = playing ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;
        }, nameof(vm.PlayingParagraphStart), nameof(vm.PlayingRecording));
        var text = new SelectableTextBlock
        {
            Text = paragraph.Text,
            Foreground = Ui.Text,
            FontSize = 13,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        // Its emotion, from the deep text analysis (every emotion in the tooltip).
        var head = Ui.Row(10, time);
        if (paragraph.EmotionLabel.Length > 0)
        {
            var emotion = Ui.Label(paragraph.EmotionLabel, WritingUi.Amber, 10);
            ToolTip.SetTip(emotion, paragraph.EmotionDetails);
            head.Children.Add(emotion);
        }
        // Its voice, from the audio analysis (every figure in the tooltip).
        if (paragraph.Voice is { } voice)
        {
            var figures = Ui.Label(voice.Label, Ui.Muted, 10);
            ToolTip.SetTip(figures, voice.Details);
            head.Children.Add(figures);
        }
        var column = Ui.Column(2, head, text);
        column.Margin = new Thickness(0, 0, 0, 10);
        return column;
    }

    /// <summary>🖼: pictures picked, copied into the folder of the interviews, inserted at the caret.</summary>
    private static async Task InsertPicturesAsync(InterviewWindowViewModel vm, TapeEditor editor)
    {
        var files = await Dialogs.OpenFiles("Insert pictures", WritingUi.PictureFilters, vm.PicturesDirectory);
        if (files.Length == 0)
            return;
        var copies = vm.CopyPictures(files);
        if (copies.Count == 0)
            return;
        editor.InsertImages(copies);
        vm.Status = $"{copies.Count} picture(s) copied into the interviews and inserted";
    }
}
