using System;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Media;

/// <summary>
/// /recorder: an audio recorder. At the top, the project (the recordings go to <c>~/kaonomi/audio/&lt;project&gt;</c>), the
/// title of the recording, ● REC / ■ STOP, the time, and the folder. In the middle, what the microphone hears, live,
/// recorded or not: a VU meter (RMS level and peak held, in dB), a spectrum analyser (60 Hz to 16 kHz, logarithmic,
/// with the envelope of the voice over it and its formants marked) and the real-time formant analysis (the pitch F0
/// and the formants F1 to F4 as values, and the closest French vowel). Under them, the spectrogram of the recording
/// selected, as the phoneticians read it: the time across, the frequency up from 0 to 8000 Hz, the darker the louder,
/// on a wide band (the pulses of the voice show as vertical lines, its formants as dark bands), with the formants
/// found marked over it (FORMANTS hides them), the annotations and the position played. It is never shown while it
/// records: it is computed from the file as soon as the recording ends (or one is selected). It zooms: the wheel
/// zooms the time around the pointer, a drag moves it, a double click (or ⟲ ZOOM) shows the whole again. Over it,
/// the recording as the audio track of a video editor: a time ruler, the clip with its waveform, the playhead. A
/// click on the track puts the playhead there; a drag selects a part, that ✂ CUT takes out of the recording and TRIM
/// keeps alone (the file is written again; ORIGINAL brings back the recording as it was recorded). On the
/// left, the recordings of the project; under the meters, ▶ PLAY for the one selected and its annotations: a note is
/// typed and added at the time recorded so far (while it records) or at the position played; a click on its time goes
/// back there, ✕ removes it.
/// <para>
/// A right click on the track or the spectrogram opens the menu of the part selected (the whole recording when none
/// is): play it, play it in a loop, stop, name it (the named selections are drawn over the ruler; a click on one
/// selects it again), save it as a sample, cut it out, keep it alone. What is played from this menu, and what is
/// saved as a sample, goes through the filters of the panel at the bottom right: a gain, a compressor (threshold,
/// ratio), a low and a high cut that keep a band of frequencies (or, inverted, take it out), and an equalizer whose
/// curve is drawn: its seven points are dragged up and down (a double click puts one back to 0 dB). Each manipulation
/// (cut, trim, original, name, sample) first asks its annotation in a popup; everything is kept in the document of
/// the recording, beside it and in the search engine of the server. On the right, the manipulations of the recording
/// selected, the newest first: what was done, when, by whom, its annotation and on what.
/// </para>
/// </summary>
[ToolView(typeof(RecorderWindowViewModel), "Recorder", "🎙", 1600, 960)]
public sealed partial class RecorderView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (RecorderWindowViewModel)viewModel;
        // The popups of the manipulations: the name of a selection or of a sample, the annotation of each of them.
        vm.AskAsync = (caption, question, initial, multiline) => Dialogs.Ask(caption, question, initial, multiline);

        // The project, the title, the recording.
        var project = Ui.Line(vm, nameof(vm.Project), "Project (the folder of the recordings)");
        project.CustomMinimumSize = new Vector2(200, 0);
        Bind.When(project, vm, () => project.Editable = vm.CanEditProject, nameof(vm.CanEditProject));
        var title = Ui.Line(vm, nameof(vm.Title), "Title of the recording (optional)");
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var record = Ui.Command("● REC", vm.RecordCommand, primary: true, tooltip: "Start recording the microphone in the folder of the project; again to stop");
        record.CustomMinimumSize = new Vector2(110, 0);
        Bind.When(record, vm, () =>
        {
            record.Text = vm.RecordLabel;
            record.Modulate = vm.IsRecording ? new Color(1f, 0.45f, 0.45f) : Colors.White;
        }, nameof(vm.RecordLabel), nameof(vm.IsRecording));
        var time = Ui.Label(vm, nameof(vm.TimeLabel), Eve.Eve.TextBright, 20);
        time.AddThemeFontOverride("font", Eve.Eve.Mono);
        time.CustomMinimumSize = new Vector2(190, 0);
        time.HorizontalAlignment = HorizontalAlignment.Right;
        var toolbar = Ui.Toolbar(Ui.Row(10,
            Ui.Field("Project", project),
            Ui.Fill(Ui.Field("Title", title)),
            record, time,
            Ui.Command("📁 FOLDER", vm.OpenFolderCommand, tooltip: "Open the folder of the project")));

        // The recordings of the project.
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("No recording in this project");
        Bind.Items(list, vm.Recordings, item => RecordingRow(vm, item), empty);
        var left = Ui.Column(4, Eve.Eve.Caption("Recordings of the project"), Ui.FillHeight(Ui.Scroll(list)), empty);
        left.CustomMinimumSize = new Vector2(260, 0);

        // The meters, live.
        var scope = new Scope(vm) { CustomMinimumSize = new Vector2(0, 400), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };

        // Listening again, and the annotations.
        var play = Ui.Command("▶ PLAY", vm.PlayCommand, tooltip: "Play the recording selected through the filters of the panel (RESET: as it was recorded), or pause it");
        play.CustomMinimumSize = new Vector2(100, 0);
        Bind.When(play, vm, () => play.Text = vm.PlayLabel, nameof(vm.PlayLabel));
        var note = Ui.Line(vm, nameof(vm.NewAnnotation), "A note on this moment (Enter): at the time recorded so far, or at the position played");
        note.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        note.TextSubmitted += _ => vm.AddAnnotationCommand.Execute(null);
        var notes = new VBoxContainer();
        notes.AddThemeConstantOverride("separation", 2);
        var noNotes = Ui.Placeholder("No annotation");
        Bind.Items(notes, vm.Annotations, annotation => AnnotationRow(vm, annotation), noNotes);
        var tools = Ui.Row(8, play, note, Ui.Command("+ ANNOTATE", vm.AddAnnotationCommand, tooltip: "Add the note at the time recorded so far, or at the position played"));
        var noteList = Ui.Column(4, Eve.Eve.Caption("Annotations"), Ui.FillHeight(Ui.Scroll(notes)), noNotes);
        noteList.CustomMinimumSize = new Vector2(240, 0);
        var annotations = Ui.Column(6, tools, Ui.FillHeight(Ui.SplitH(noteList, Effects(vm), 330)));
        annotations.CustomMinimumSize = new Vector2(0, 250);

        // The cuts; the spectrogram: its formants, its zoom (also the wheel, a drag, a double click on it).
        tools.AddChild(Ui.Action("✂ CUT", scope.CutSelection, tooltip: "Take the part selected on the track out of the recording (drag on the track to select)"));
        tools.AddChild(Ui.Action("TRIM", scope.TrimSelection, tooltip: "Keep only the part selected on the track"));
        tools.AddChild(Ui.Action("↶ ORIGINAL", scope.Restore, tooltip: "Bring the recording back as it was recorded: every cut is undone"));
        tools.AddChild(Ui.Action("FORMANTS", scope.ToggleFormants, tooltip: "Show or hide the formants found (F1 to F4) over the spectrogram"));
        tools.AddChild(Ui.Action("＋", () => scope.Zoom(0.5), tooltip: "Zoom in the spectrogram (also the mouse wheel, around the pointer)"));
        tools.AddChild(Ui.Action("－", () => scope.Zoom(2), tooltip: "Zoom out"));
        tools.AddChild(Ui.Action("⟲ ZOOM", scope.ResetZoom, tooltip: "Show the whole recording again (also a double click on the spectrogram). On the spectrogram: a drag draws a rectangle (time and frequencies) to play or save as a sample; the middle button (or Shift) moves the time; the wheel zooms"));
        var right = Ui.SplitV(scope, annotations, 520);
        // The manipulations of the recording selected, on the right.
        var actions = new VBoxContainer();
        actions.AddThemeConstantOverride("separation", 4);
        var noActions = Ui.Placeholder("No manipulation yet");
        Bind.Items(actions, vm.History, ActionRow, noActions);
        var history = Ui.Column(4, Eve.Eve.Caption("Manipulations of the recording"), Ui.FillHeight(Ui.Scroll(actions)), noActions);
        history.CustomMinimumSize = new Vector2(240, 0);

        var page = Ui.Column(6, toolbar, Ui.FillHeight(Ui.SplitH(left, Ui.SplitH(right, history, 1010), 280)), Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)));

        // The space bar stops and starts the playing again, wherever the focus is in this window (but in a text).
        scope.Page = page;

        // The microphone opens once the window is shown, and closes with it.
        page.Ready += vm.Listen;
        Bind.OnGone(page, vm.Dispose);
        return page;
    }

    /// <summary>A recording of the list: its title or name, its date, length and notes; a click selects it.</summary>
    private static Control RecordingRow(RecorderWindowViewModel vm, RecorderItemViewModel item)
    {
        var button = new Button { ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 46), Alignment = HorizontalAlignment.Left };
        button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Sunken(0.15f), Eve.Eve.Edge with { A = 0.15f }, 6));
        var name = Ui.Label(item, nameof(item.Name), Eve.Eve.TextBright, 13);
        name.ClipText = true;
        var details = Ui.Label(item, nameof(item.Details), Eve.Eve.Muted, 10);
        var content = Ui.Column(1, name, details);
        content.MouseFilter = Control.MouseFilterEnum.Ignore;
        name.MouseFilter = details.MouseFilter = Control.MouseFilterEnum.Ignore;
        var margin = Ui.Margin(content, 8, 4, 8, 4);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(margin);
        button.Pressed += () => vm.SelectedRecording = item;
        Bind.When(button, vm, () => button.SetPressedNoSignal(ReferenceEquals(vm.SelectedRecording, item)), nameof(vm.SelectedRecording));
        return button;
    }

    /// <summary>A manipulation: what was done, when and by whom; its annotation; what it was done on.</summary>
    private static Control ActionRow(RecorderAction action)
    {
        var color = action.Action switch
        {
            "recorded" => Eve.Eve.Red,
            "cut" or "trim" => Eve.Eve.Amber,
            "restore" => Eve.Eve.Green,
            "save-sample" => Eve.Eve.Cyan,
            _ => new Color(0.72f, 0.55f, 1f)
        };
        var name = Eve.Eve.Text_(action.Action.ToUpperInvariant(), color, 12);
        name.AddThemeFontOverride("font", Eve.Eve.Mono);
        var when = Eve.Eve.Text_($"{action.Date.ToLocalTime():dd/MM/yyyy HH:mm:ss} · {action.User}", Eve.Eve.Muted, 10);
        var column = Ui.Column(1, name, when);
        if (action.Annotation.Length > 0)
            column.AddChild(Eve.Eve.Text_(action.Annotation, Eve.Eve.TextBright, 12, wrap: true));
        if (action.Details.Length > 0)
            column.AddChild(Eve.Eve.Text_(action.Details, Eve.Eve.Muted, 10, wrap: true));
        var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.15f), color with { A = 0.35f }, 6));
        panel.AddChild(Ui.Margin(column, 8, 5, 8, 5));
        return panel;
    }

    /// <summary>An annotation: its time (a click goes there and plays), its text, ✕.</summary>
    private static Control AnnotationRow(RecorderWindowViewModel vm, RecorderAnnotation annotation)
    {
        var jump = Ui.Command(annotation.TimeLabel, vm.JumpCommand, tooltip: "Go to this moment of the recording selected and play", parameter: () => annotation);
        jump.CustomMinimumSize = new Vector2(84, 0);
        jump.AddThemeFontOverride("font", Eve.Eve.Mono);
        var text = Eve.Eve.Text_(annotation.Text, Eve.Eve.Text, 13, wrap: true);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return Ui.Row(8, jump, text, Ui.Command("✕", vm.RemoveAnnotationCommand, tooltip: "Remove this annotation", parameter: () => annotation));
    }

    /// <summary>
    /// The filters of what is listened to from the menu of the selection and of the samples saved: the equalizer and
    /// its curve on the left; on the right the gain, the band kept (low cut, high cut, inverted or not), the
    /// compressor, and the buttons that play, loop, stop and save the part selected.
    /// </summary>
    private static Control Effects(RecorderWindowViewModel vm)
    {
        var equalizer = new Equalizer(vm) { CustomMinimumSize = new Vector2(260, 150), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        Bind.When(equalizer, vm, equalizer.QueueRedraw, nameof(vm.FiltersVersion), nameof(vm.SelectedRecording));

        static string Hz(double hz) => hz >= 1000 ? $"{hz / 1000:0.0#} kHz" : $"{hz:0} Hz";
        var sliders = Ui.Column(3,
            Slider(vm, "Gain", -24, 24, 0.5, false, () => vm.GainDb, v => vm.GainDb = v, v => $"{v:+0.0;-0.0;0.0} dB", nameof(vm.GainDb),
                "The gain applied to the whole, in dB"),
            Slider(vm, "Low cut", AudioFilterSettings.MinHz, AudioFilterSettings.MaxHz, 1, true, () => vm.LowHz, v => vm.LowHz = v, Hz, nameof(vm.LowHz),
                "The bottom of the band of frequencies: what is under it is taken out (kept, when the band is inverted)"),
            Slider(vm, "High cut", AudioFilterSettings.MinHz, AudioFilterSettings.MaxHz, 1, true, () => vm.HighHz, v => vm.HighHz = v, Hz, nameof(vm.HighHz),
                "The top of the band of frequencies: what is over it is taken out (kept, when the band is inverted)"),
            Ui.Row(10,
                Ui.Check("Invert the band", vm, nameof(vm.InvertBand), "Take the band between the low and the high cut out, instead of keeping only it"),
                Ui.Check("Compressor", vm, nameof(vm.Compressor), "Lower what is louder than the threshold, by the ratio")),
            Slider(vm, "Threshold", -60, 0, 1, false, () => vm.ThresholdDb, v => vm.ThresholdDb = v, v => $"{v:0} dB", nameof(vm.ThresholdDb),
                "The compressor: the level over which the sound is lowered"),
            Slider(vm, "Ratio", 1, 20, 0.5, false, () => vm.Ratio, v => vm.Ratio = v, v => $"{v:0.#} : 1", nameof(vm.Ratio),
                "The compressor: how much what is over the threshold is lowered"));
        sliders.CustomMinimumSize = new Vector2(300, 0);

        var buttons = Ui.Row(6,
            Ui.Action("▶ ONCE", () => vm.PlaySelection(false), tooltip: "Play the part selected, or the whole recording when nothing is selected, once through the filters; also a right click on the track"),
            Ui.Action("⟳ LOOP", () => vm.PlaySelection(true), tooltip: "Play the part selected, or the whole recording when nothing is selected, again and again through the filters: a filter moved is heard at once. A click elsewhere or a new selection does not stop it"),
            Ui.Command("■", vm.StopCommand, tooltip: "Stop what is playing"),
            Ui.Action("💾 SAVE SAMPLE", () => _ = vm.SaveSampleAsync(), primary: true,
                tooltip: "Save the part selected, with the filters applied, as a new WAV file of <project>/samples; its name and its annotation are asked, and it is added to the document of the recording"),
            Ui.Command("RESET", vm.ResetFiltersCommand, tooltip: "No filter: the sound as it was recorded"));
        var filters = Ui.Label(vm, nameof(vm.FiltersLabel), Eve.Eve.Muted, 10);
        filters.ClipText = true;
        var server = Ui.Label(vm, nameof(vm.ServerLabel), Eve.Eve.Muted, 10);
        server.ClipText = true;
        return Ui.Column(4,
            Eve.Eve.Caption("Filters of everything played, selection or not · equalizer (drag the points)"),
            Ui.FillHeight(Ui.Row(10, equalizer, sliders)),
            buttons, filters, server);
    }

    /// <summary>A filter as a line: its name, a slider (logarithmic for the frequencies), its value.</summary>
    private static Control Slider(RecorderWindowViewModel vm, string caption, double min, double max, double step, bool logarithmic,
        Func<double> get, Action<double> set, Func<double, string> text, string property, string tooltip)
    {
        var name = Eve.Eve.Text_(caption, Eve.Eve.Muted, 11);
        name.CustomMinimumSize = new Vector2(64, 0);
        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step, ExpEdit = logarithmic, TooltipText = tooltip,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(120, 16)
        };
        var value = Eve.Eve.Text_(text(get()), Eve.Eve.TextBright, 11);
        value.AddThemeFontOverride("font", Eve.Eve.Mono);
        value.CustomMinimumSize = new Vector2(74, 0);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        void Show()
        {
            slider.SetValueNoSignal(get());
            value.Text = text(get());
        }
        Show();
        slider.ValueChanged += v => set(v);
        Bind.When(slider, vm, Show, property);
        return Ui.Row(6, name, slider, value);
    }

    /// <summary>
    /// The equalizer, as its curve: what the filters do to each frequency (20 Hz to 20 kHz, logarithmic; ±30 dB), the
    /// gain, the cuts and the bands together. The seven points of the bands are dragged up and down (±18 dB); a double
    /// click puts a point back to 0 dB.
    /// </summary>
    private sealed partial class Equalizer : Control
    {
        private const float RangeDb = 30;
        private readonly RecorderWindowViewModel? _vm;
        private int _dragged = -1;

        public Equalizer()
        {
        }

        public Equalizer(RecorderWindowViewModel vm)
        {
            _vm = vm;
            MouseFilter = MouseFilterEnum.Stop;
            TooltipText = "The equalizer: drag a point up or down (double click: back to 0 dB). The curve is what all the filters do together.";
        }

        private Rect2 Plot => new(30, 6, MathF.Max(10, Size.X - 38), MathF.Max(10, Size.Y - 24));

        private static readonly double LowLog = Math.Log(AudioFilterSettings.MinHz);
        private static readonly double SpanLog = Math.Log(AudioFilterSettings.MaxHz) - Math.Log(AudioFilterSettings.MinHz);

        private float X(double hz) => Plot.Position.X + Plot.Size.X * (float)((Math.Log(hz) - LowLog) / SpanLog);
        private double HzAt(float x) => Math.Exp(LowLog + (x - Plot.Position.X) / Plot.Size.X * SpanLog);
        private float Y(double db) => Plot.Position.Y + Plot.Size.Y * (0.5f - (float)Math.Clamp(db / RangeDb, -1, 1) / 2);
        private double DbAt(float y) => (0.5 - (y - Plot.Position.Y) / Plot.Size.Y) * 2 * RangeDb;

        public override void _GuiInput(InputEvent @event)
        {
            if (_vm is null)
                return;
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
            {
                if (!button.Pressed)
                {
                    _dragged = -1;
                    return;
                }
                // The point of the band the closest to the pointer.
                var closest = 0;
                for (var band = 1; band < AudioFilterSettings.EqualizerHz.Length; band++)
                    if (MathF.Abs(X(AudioFilterSettings.EqualizerHz[band]) - button.Position.X) < MathF.Abs(X(AudioFilterSettings.EqualizerHz[closest]) - button.Position.X))
                        closest = band;
                if (button.DoubleClick)
                    _vm.SetEqualizerBand(closest, 0);
                else
                {
                    _dragged = closest;
                    _vm.SetEqualizerBand(closest, DbAt(button.Position.Y));
                }
                AcceptEvent();
            }
            else if (@event is InputEventMouseMotion motion && _dragged >= 0)
            {
                _vm.SetEqualizerBand(_dragged, DbAt(motion.Position.Y));
                AcceptEvent();
            }
        }

        public override void _Draw()
        {
            if (_vm is null)
                return;
            var plot = Plot;
            DrawRect(new Rect2(Vector2.Zero, Size), Eve.Eve.Sunken(0.35f));
            DrawRect(plot, Eve.Eve.Sunken(0.45f));
            var rate = _vm.SelectedRecording?.Info.Rate is > 0 and var r ? r : 44100;
            var filters = _vm.Filters;

            // What the cuts take out, greyed.
            var shade = new Color(0, 0, 0, 0.35f);
            if (filters.InvertBand && (filters.CutsLow || filters.CutsHigh))
                DrawRect(new Rect2(X(filters.LowHz), plot.Position.Y, MathF.Max(1, X(filters.HighHz) - X(filters.LowHz)), plot.Size.Y), shade);
            else
            {
                if (filters.CutsLow)
                    DrawRect(new Rect2(plot.Position.X, plot.Position.Y, X(filters.LowHz) - plot.Position.X, plot.Size.Y), shade);
                if (filters.CutsHigh)
                    DrawRect(new Rect2(X(filters.HighHz), plot.Position.Y, plot.End.X - X(filters.HighHz), plot.Size.Y), shade);
            }

            // The grid.
            foreach (var hz in new[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
            {
                DrawLine(new Vector2(X(hz), plot.Position.Y), new Vector2(X(hz), plot.End.Y), Eve.Eve.Edge with { A = 0.22f }, 1);
                DrawString(Eve.Eve.Mono, new Vector2(X(hz) - 10, plot.End.Y + 12), hz >= 1000 ? $"{hz / 1000}k" : hz.ToString(), HorizontalAlignment.Left, -1, 9, Eve.Eve.Muted);
            }
            foreach (var db in new[] { -24, -12, 0, 12, 24 })
            {
                DrawLine(new Vector2(plot.Position.X, Y(db)), new Vector2(plot.End.X, Y(db)), Eve.Eve.Edge with { A = db == 0 ? 0.55f : 0.2f }, 1);
                DrawString(Eve.Eve.Mono, new Vector2(2, Y(db) + 3), $"{db:+0;-0;0}", HorizontalAlignment.Left, -1, 9, Eve.Eve.Muted);
            }

            // The curve: what the filters do to each frequency.
            var count = Math.Max(2, (int)(plot.Size.X / 3));
            var curve = new Vector2[count];
            for (var i = 0; i < count; i++)
            {
                var x = plot.Position.X + plot.Size.X * i / (count - 1);
                var db = AudioEffects.ResponseDb(HzAt(x), rate, filters);
                curve[i] = new Vector2(x, Y(double.IsFinite(db) ? db : -RangeDb));
            }
            DrawPolyline(curve, Eve.Eve.Cyan, 2, true);

            // The points of the bands.
            for (var band = 0; band < AudioFilterSettings.EqualizerHz.Length; band++)
            {
                var at = new Vector2(X(AudioFilterSettings.EqualizerHz[band]), Y(_vm.EqualizerBand(band)));
                DrawLine(new Vector2(at.X, Y(0)), at, Eve.Eve.Amber with { A = 0.5f }, 1);
                DrawCircle(at, band == _dragged ? 6 : 4.5f, Eve.Eve.Amber);
                DrawArc(at, band == _dragged ? 6 : 4.5f, 0, MathF.Tau, 16, Eve.Eve.TextBright, 1, true);
                if (Math.Abs(_vm.EqualizerBand(band)) >= 0.05)
                    DrawString(Eve.Eve.Mono, at + new Vector2(-12, _vm.EqualizerBand(band) > 0 ? -8 : 16), $"{_vm.EqualizerBand(band):+0.#;-0.#}", HorizontalAlignment.Left, -1, 9, Eve.Eve.TextBright);
            }
            DrawRect(plot, Eve.Eve.Edge with { A = 0.5f }, false, 1);
        }
    }

    /// <summary>
    /// The meters, live (a VU meter on the left, the spectrum analyser at the top, the values of the formants under
    /// it), and under them the spectrogram of the recording selected, which zooms. It reads the analyser of the
    /// recorder thirty times a second while it is shown.
    /// </summary>
    private sealed partial class Scope : Control
    {
        private const float FloorDb = -80;
        /// <summary>The width of the picture of the spectrogram, in columns: what is in view is drawn on them.</summary>
        private const int Columns = 1000;
        private const int Rows = Spectrogram.Rows;
        /// <summary>White is this many dB under black, and black 6 dB under the loudest of the recording.</summary>
        private const float RangeDb = 50;
        /// <summary>The closest zoom: this much time across the whole width.</summary>
        private const double MinSpan = 0.25;

        private static readonly Color[] FormantColors = [new(0.35f, 0.85f, 0.45f), new(0.35f, 0.75f, 0.95f), new(0.95f, 0.7f, 0.25f), new(0.95f, 0.4f, 0.4f)];

        private readonly RecorderWindowViewModel? _vm;
        private readonly float[] _spectrum = new float[AudioAnalyzer.Bands];
        private readonly float[] _envelope = new float[AudioAnalyzer.Bands];
        private readonly double[] _formants = new double[4];
        private int _formantCount;
        private float _rms = FloorDb, _peak = FloorDb, _held = FloorDb;
        private double _heldAge, _pitch, _sinceAnalysis, _sinceRefresh;
        private string _vowel = string.Empty;
        private bool _voiced;

        // The spectrogram: the one shown, the time in view, and its picture (made again when one of them changes).
        private Spectrogram? _shown;
        private double _viewStart, _viewEnd;
        private readonly byte[] _pixels = new byte[Columns * Rows];
        private Image? _image;
        private ImageTexture? _texture;
        private bool _imageDirty = true;
        private bool _showFormants = true;
        private Rect2 _spectrogramArea;
        private bool _dragging;
        // The rectangle being drawn on the spectrogram: where it was started (pointer, time, frequency).
        private bool _boxing;
        private Vector2 _boxPress;
        private double _boxTime, _boxHz;

        // The audio track: where it is drawn; the part selected on it is the one of the view model (seconds).
        private Rect2 _rulerArea, _trackArea;
        private double _selectionStart => _vm?.SelectionStart ?? -1;
        private double _selectionEnd => _vm?.SelectionEnd ?? -1;
        private bool _selecting;
        private float _pressX;
        private double _pressTime;
        private Vector2[] _wave = [];

        public Scope()
        {
        }

        public Scope(RecorderWindowViewModel vm)
        {
            _vm = vm;
            Array.Fill(_spectrum, FloorDb);
            Array.Fill(_envelope, float.NaN);
            // The picture of the spectrogram is stretched to its place: smoothed, not in blocks.
            TextureFilter = TextureFilterEnum.Linear;
            MouseFilter = MouseFilterEnum.Stop;
        }

        /// <summary>The whole window of the recorder: the space bar is taken while the focus is in it.</summary>
        public Control? Page { get; set; }

        /// <summary>
        /// The space bar stops what is playing, or starts it again. It is taken before the button that has the focus
        /// (which it would press), but left to a text being typed, and to the other windows of the chat.
        /// </summary>
        public override void _Input(InputEvent @event)
        {
            if (_vm is null || @event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Space } || !IsVisibleInTree())
                return;
            var focus = GetViewport()?.GuiGetFocusOwner();
            if (focus is LineEdit or TextEdit)
                return;
            if (focus is not null && focus != this && !(Page?.IsAncestorOf(focus) ?? false))
                return;
            if (_vm.IsRecording || _vm.SelectedRecording is null)
                return;
            _vm.TogglePlayback();
            GetViewport()?.SetInputAsHandled();
        }

        public override void _Process(double delta)
        {
            if (_vm is null || !IsVisibleInTree())
                return;
            if ((_sinceRefresh += delta) >= 0.1)
            {
                _sinceRefresh = 0;
                _vm.Refresh();
            }
            if ((_sinceAnalysis += delta) < 1.0 / 30)
                return;
            var step = (float)_sinceAnalysis;
            _sinceAnalysis = 0;

            // Another recording selected, or its spectrogram just computed: shown whole.
            if (!ReferenceEquals(_shown, _vm.Spectrogram))
            {
                _shown = _vm.Spectrogram;
                _selecting = false;
                _viewStart = 0;
                _viewEnd = Math.Max(MinSpan, _shown?.Seconds ?? 0);
                _imageDirty = true;
            }

            if (_vm.Recorder.IsListening)
            {
                var frame = _vm.Recorder.Analyzer.Analyze();
                // The needles rise at once and fall slowly; the peak is held a second.
                var rms = MathF.Max(FloorDb, AudioFrame.Decibels(frame.Rms));
                var peak = MathF.Max(FloorDb, AudioFrame.Decibels(frame.Peak));
                _rms = rms > _rms ? rms : MathF.Max(rms, _rms - 40 * step);
                _peak = peak > _peak ? peak : MathF.Max(peak, _peak - 40 * step);
                if (peak >= _held)
                {
                    _held = peak;
                    _heldAge = 0;
                }
                else if ((_heldAge += step) > 1)
                    _held = MathF.Max(peak, _held - 30 * step);

                for (var b = 0; b < AudioAnalyzer.Bands; b++)
                {
                    var value = MathF.Max(FloorDb, frame.Spectrum[b]);
                    _spectrum[b] = value > _spectrum[b] ? value : MathF.Max(value, _spectrum[b] - 60 * step);
                    _envelope[b] = frame.Envelope[b];
                }

                _voiced = frame.Voiced;
                _pitch = frame.Pitch;
                _vowel = frame.Vowel;
                _formantCount = frame.FormantCount;
                for (var i = 0; i < 4; i++)
                    _formants[i] = i < frame.FormantCount ? frame.Formants[i] : 0;
            }
            QueueRedraw();
        }

        // ── The zoom of the spectrogram ──

        /// <summary>Shows or hides the formants found over the spectrogram.</summary>
        public void ToggleFormants()
        {
            _showFormants = !_showFormants;
            QueueRedraw();
        }

        /// <summary>The whole recording in view again.</summary>
        public void ResetZoom()
        {
            if (_shown is null)
                return;
            _viewStart = 0;
            _viewEnd = Math.Max(MinSpan, _shown.Seconds);
            _imageDirty = true;
            QueueRedraw();
        }

        /// <summary>
        /// Zooms the time in view by <paramref name="factor"/> (under 1: closer) around <paramref name="anchor"/>
        /// (0: its left edge, 1: its right edge; its middle by default), within the recording.
        /// </summary>
        public void Zoom(double factor, double anchor = 0.5)
        {
            if (_shown is null)
                return;
            var whole = Math.Max(MinSpan, _shown.Seconds);
            var span = _viewEnd - _viewStart;
            var newSpan = Math.Clamp(span * factor, Math.Min(MinSpan, whole), whole);
            var pivot = _viewStart + span * anchor;
            SetView(pivot - newSpan * anchor, newSpan);
        }

        private void SetView(double start, double span)
        {
            var whole = Math.Max(MinSpan, _shown?.Seconds ?? 0);
            span = Math.Min(span, whole);
            start = Math.Clamp(start, 0, whole - span);
            if (start == _viewStart && start + span == _viewEnd)
                return;
            _viewStart = start;
            _viewEnd = start + span;
            _imageDirty = true;
            QueueRedraw();
        }

        // ── The cuts ──

        private bool HasSelection => _selectionEnd - _selectionStart >= 0.01;

        /// <summary>Takes the part selected on the track out of the recording.</summary>
        public void CutSelection() => Edit(trim: false);

        /// <summary>Keeps only the part selected on the track.</summary>
        public void TrimSelection() => Edit(trim: true);

        private void Edit(bool trim)
        {
            if (_vm is null)
                return;
            // The selection is kept until the annotation of the cut is given: cancelled, nothing changed.
            var (start, end) = HasSelection ? (_selectionStart, _selectionEnd) : (0d, 0d);
            _ = _vm.EditAsync(trim, start, end);
        }

        /// <summary>Brings the recording back as it was recorded.</summary>
        public void Restore()
        {
            if (_vm is not null)
                _ = _vm.RestoreAsync();
        }

        /// <summary>The frequency under the pointer at <paramref name="y"/> on the spectrogram, within 0 to 8000 Hz.</summary>
        private double HzAt(float y) =>
            Math.Clamp((_spectrogramArea.End.Y - y) / Math.Max(1f, _spectrogramArea.Size.Y), 0, 1) * Spectrogram.MaxHz;

        /// <summary>The shortest named selection holding <paramref name="time"/>, if any.</summary>
        private RecorderSelection? NamedAt(double time)
        {
            RecorderSelection? found = null;
            foreach (var selection in _vm!.Selections)
                if (time >= selection.Start && time <= selection.End && (found is null || selection.End - selection.Start < found.End - found.Start))
                    found = selection;
            return found;
        }

        /// <summary>
        /// The menu of a right click on the track or the spectrogram: what can be done with the part selected (the whole
        /// recording when none is), and the named selections to select again.
        /// </summary>
        private void OpenMenu(Vector2 at)
        {
            if (_vm is null || _vm.IsRecording || _vm.SelectedRecording is null)
                return;
            var what = !HasSelection ? "the whole recording"
                : _vm.HasRegion ? $"the rectangle ({_selectionEnd - _selectionStart:0.00} s, {_vm.RegionLowHz:0}–{_vm.RegionHighHz:0} Hz)"
                : $"the selection ({_selectionEnd - _selectionStart:0.00} s)";
            var named = _vm.Selections;
            var menu = new PopupMenu();
            menu.AddItem($"▶  Play {what}", 0);
            menu.AddItem($"⟳  Loop {what}", 1);
            menu.AddItem("■  Stop  (space bar: stop / go on)", 2);
            menu.AddSeparator();
            menu.AddItem("🏷  Name the selection…", 3);
            menu.AddItem($"💾  Save {what} as a sample (with the filters)…", 4);
            menu.AddSeparator();
            menu.AddItem("✂  Cut the selection out…", 5);
            menu.AddItem("Trim: keep only the selection…", 6);
            menu.AddItem("Clear the selection", 7);
            foreach (var id in new[] { 3, 5, 6, 7 })
                menu.SetItemDisabled(menu.GetItemIndex(id), !HasSelection);
            menu.SetItemDisabled(menu.GetItemIndex(2), _vm.PlayPosition is null);
            if (named.Count > 0)
            {
                menu.AddSeparator("Named selections");
                for (var i = 0; i < named.Count; i++)
                    menu.AddItem($"Select « {named[i].Name} »  {RecorderWindowViewModel.Clock(named[i].Start)} – {RecorderWindowViewModel.Clock(named[i].End)}", 100 + i);
            }
            menu.IdPressed += id =>
            {
                switch (id)
                {
                    case 0: _vm.PlaySelection(false); break;
                    case 1: _vm.PlaySelection(true); break;
                    case 2: _vm.StopCommand.Execute(null); break;
                    case 3: _ = _vm.NameSelectionAsync(); break;
                    case 4: _ = _vm.SaveSampleAsync(); break;
                    case 5: CutSelection(); break;
                    case 6: TrimSelection(); break;
                    case 7: _vm.ClearSelection(); break;
                    case >= 100 when id - 100 < named.Count:
                        _vm.Select(named[(int)id - 100].Start, named[(int)id - 100].End);
                        break;
                }
            };
            menu.PopupHide += () => menu.CallDeferred(Node.MethodName.QueueFree);
            AddChild(menu);
            menu.Position = (Vector2I)(GetScreenTransform() * at);
            menu.Popup();
        }

        /// <summary>The time under the pointer at <paramref name="x"/>, within the recording.</summary>
        private double TimeAt(float x) =>
            Math.Clamp(_viewStart + (x - _trackArea.Position.X) / Math.Max(1f, _trackArea.Size.X) * (_viewEnd - _viewStart), 0, _shown?.Seconds ?? 0);

        public override void _GuiInput(InputEvent @event)
        {
            if (_shown is null)
                return;
            if (@event is InputEventMouseButton button)
            {
                var onTrack = _trackArea.HasPoint(button.Position) || _rulerArea.HasPoint(button.Position);
                var onSpectrogram = _spectrogramArea.HasPoint(button.Position);
                // A button released anywhere ends what was started.
                if (button.ButtonIndex == MouseButton.Middle && !button.Pressed)
                {
                    _dragging = false;
                    return;
                }
                if (button.ButtonIndex == MouseButton.Left && !button.Pressed)
                {
                    _dragging = false;
                    if (_boxing)
                    {
                        _boxing = false;
                        if ((button.Position - _boxPress).Length() < 4)
                        {
                            // A click without a move: no rectangle, the playhead goes there.
                            _vm!.ClearSelection();
                            _vm.Seek(_boxTime);
                        }
                        else if (_vm!.HasSelection)
                        {
                            // The rectangle is drawn: a loop playing goes there; what can be done with it (play, loop, save as a sample…).
                            _vm.FollowSelection();
                            OpenMenu(button.Position);
                        }
                        else
                            _vm.ClearSelection();
                        QueueRedraw();
                        AcceptEvent();
                        return;
                    }
                    if (_selecting)
                    {
                        _selecting = false;
                        // A click without a move: no selection, the playhead goes there.
                        if (MathF.Abs(button.Position.X - _pressX) < 3)
                        {
                            // On the ruler, on a named selection: it is selected again.
                            if (_rulerArea.HasPoint(button.Position) && NamedAt(_pressTime) is { } named)
                                _vm!.Select(named.Start, named.End);
                            else
                            {
                                _vm!.ClearSelection();
                                _vm.Seek(_pressTime);
                            }
                        }
                        else
                            // A part selected while a loop plays: the loop goes there.
                            _vm!.FollowSelection();
                        QueueRedraw();
                        AcceptEvent();
                    }
                    return;
                }
                if (!onTrack && !onSpectrogram)
                    return;
                var anchor = Math.Clamp((button.Position.X - _trackArea.Position.X) / Math.Max(1f, _trackArea.Size.X), 0, 1);
                if (button.Pressed && button.ButtonIndex == MouseButton.WheelUp)
                    Zoom(0.8, anchor);
                else if (button.Pressed && button.ButtonIndex == MouseButton.WheelDown)
                    Zoom(1.25, anchor);
                else if (button.ButtonIndex == MouseButton.Right && button.Pressed)
                    OpenMenu(button.Position);
                else if (button.ButtonIndex == MouseButton.Left && button.DoubleClick && onSpectrogram)
                    ResetZoom();
                else if (button.ButtonIndex == MouseButton.Left && onTrack)
                {
                    // On the track: a drag selects a part.
                    _selecting = true;
                    _pressX = button.Position.X;
                    _pressTime = TimeAt(button.Position.X);
                }
                else if (button.ButtonIndex == MouseButton.Middle || button.ButtonIndex == MouseButton.Left && button.ShiftPressed)
                {
                    // On the spectrogram, with the middle button (or Shift): the time follows the pointer.
                    if (button.Pressed)
                        _dragging = true;
                }
                else if (button.ButtonIndex == MouseButton.Left)
                {
                    // On the spectrogram: a drag draws a rectangle, a part of the time and a band of frequencies.
                    _boxing = true;
                    _boxPress = button.Position;
                    _boxTime = TimeAt(button.Position.X);
                    _boxHz = HzAt(button.Position.Y);
                }
                else
                    return;
                AcceptEvent();
            }
            else if (@event is InputEventMouseMotion motion && _selecting)
            {
                _vm!.Select(_pressTime, TimeAt(motion.Position.X));
                QueueRedraw();
                AcceptEvent();
            }
            else if (@event is InputEventMouseMotion box && _boxing)
            {
                if ((box.Position - _boxPress).Length() >= 4)
                    _vm!.SelectRegion(_boxTime, TimeAt(box.Position.X), _boxHz, HzAt(box.Position.Y));
                QueueRedraw();
                AcceptEvent();
            }
            else if (@event is InputEventMouseMotion pan && _dragging && _spectrogramArea.Size.X > 0)
            {
                // On the spectrogram: the time follows the pointer.
                var span = _viewEnd - _viewStart;
                SetView(_viewStart - pan.Relative.X / _spectrogramArea.Size.X * span, span);
                AcceptEvent();
            }
        }

        /// <summary>
        /// The picture of what is in view, made again when the view or the recording changed: white paper, the darker
        /// the louder, 8 kHz at the top. Each column of the picture is the loudest of the columns of the spectrogram it
        /// covers (zoomed out), or lies between two of them (zoomed in).
        /// </summary>
        private ImageTexture SpectrogramTexture(Spectrogram data)
        {
            if (_imageDirty || _texture is null)
            {
                var whiteDb = MathF.Max(data.LoudestDb - 6, -70) - RangeDb;
                // The ink of each of the 256 levels a cell can hold.
                Span<byte> ink = stackalloc byte[256];
                for (var level = 0; level < 256; level++)
                    ink[level] = (byte)(255 - (int)(Mathf.Clamp((Spectrogram.Decibels((byte)level) - whiteDb) / RangeDb, 0, 1) * 255));

                var perColumn = (_viewEnd - _viewStart) / Columns / data.ColumnSeconds;
                for (var c = 0; c < Columns; c++)
                {
                    var from = _viewStart / data.ColumnSeconds + c * perColumn;
                    var first = (int)Math.Floor(from);
                    if (perColumn < 1)
                    {
                        // Zoomed in: between two columns of the spectrogram, a mix of them (no blocks).
                        var left = Math.Clamp(first, 0, data.Columns - 1);
                        var right = Math.Clamp(first + 1, 0, data.Columns - 1);
                        var mix = (float)(from - first);
                        for (var row = 0; row < Rows; row++)
                        {
                            var value = data.Cells[left * Rows + row] + (data.Cells[right * Rows + row] - data.Cells[left * Rows + row]) * mix;
                            _pixels[(Rows - 1 - row) * Columns + c] = ink[(int)MathF.Round(value)];
                        }
                        continue;
                    }
                    var last = Math.Max(first, (int)Math.Ceiling(from + perColumn) - 1);
                    for (var row = 0; row < Rows; row++)
                    {
                        byte loudest = 0;
                        for (var k = first; k <= last; k++)
                        {
                            if (k < 0 || k >= data.Columns)
                                continue;
                            var cell = data.Cells[k * Rows + row];
                            if (cell > loudest)
                                loudest = cell;
                        }
                        _pixels[(Rows - 1 - row) * Columns + c] = ink[loudest];
                    }
                }
                if (_image is null)
                {
                    _image = Image.CreateFromData(Columns, Rows, false, Image.Format.L8, _pixels);
                    _texture = ImageTexture.CreateFromImage(_image);
                }
                else
                {
                    _image.SetData(Columns, Rows, false, Image.Format.L8, _pixels);
                    _texture!.Update(_image);
                }
                _imageDirty = false;
            }
            return _texture!;
        }

        public override void _Draw()
        {
            var size = Size;
            DrawRect(new Rect2(Vector2.Zero, size), Eve.Eve.Sunken(0.35f));
            DrawRect(new Rect2(Vector2.Zero, size), Eve.Eve.Edge with { A = 0.35f }, false, 1);
            if (size.X < 240 || size.Y < 200)
                return;

            const float meterWidth = 78;
            var meter = new Rect2(8, 8, meterWidth - 16, size.Y - 16);
            var plotsLeft = meterWidth + 34;
            var plotsWidth = size.X - plotsLeft - 12;
            var spectrumHeight = (size.Y - 16) * 0.30f;
            var spectrum = new Rect2(plotsLeft, 26, plotsWidth, spectrumHeight - 44);
            // The audio track (its ruler over it), then the spectrogram, on the same time: the frequencies of the
            // spectrogram are written on their right.
            const float rulerHeight = 32, trackHeight = 78;
            var top = 8 + spectrumHeight + 34;
            _rulerArea = new Rect2(plotsLeft, top, plotsWidth - 38, rulerHeight);
            _trackArea = new Rect2(plotsLeft, top + rulerHeight, plotsWidth - 38, trackHeight);
            var below = top + rulerHeight + trackHeight + 8;
            _spectrogramArea = new Rect2(plotsLeft, below, plotsWidth - 38, MathF.Max(40, size.Y - 8 - below - 18));

            DrawMeter(meter);
            DrawSpectrum(spectrum);
            DrawFormantValues(new Vector2(plotsLeft, 8 + spectrumHeight + 8));
            DrawTrack(_rulerArea, _trackArea, new Rect2(meterWidth + 2, top + rulerHeight, plotsLeft - meterWidth - 6, trackHeight));
            DrawSpectrogram(_spectrogramArea);
        }

        private void Write(string text, Vector2 at, Color color, int size = 11, HorizontalAlignment align = HorizontalAlignment.Left, float width = -1) =>
            DrawString(Eve.Eve.Mono, at, text, align, width, size, color);

        // ── The VU meter ──

        private void DrawMeter(Rect2 area)
        {
            Write("VU dB", area.Position + new Vector2(0, 10), Eve.Eve.Muted, 10);
            var bar = new Rect2(area.Position.X, area.Position.Y + 18, 22, area.Size.Y - 44);
            DrawRect(bar, Eve.Eve.Sunken(0.5f));
            float Y(float db) => bar.Position.Y + bar.Size.Y * (1 - Mathf.Clamp((db - FloorDb) / -FloorDb, 0, 1));

            // The RMS level: cyan, amber from -12 dB, red from -3 dB.
            foreach (var (from, to, color) in new[] { (FloorDb, -12f, Eve.Eve.Cyan), (-12f, -3f, Eve.Eve.Amber), (-3f, 0f, Eve.Eve.Red) })
            {
                var top = MathF.Min(_rms, to);
                if (top > from)
                    DrawRect(new Rect2(bar.Position.X, Y(top), bar.Size.X, Y(from) - Y(top)), color);
            }
            // The peak now (a thin line) and the peak held (a thick one).
            DrawLine(new Vector2(bar.Position.X, Y(_peak)), new Vector2(bar.End.X, Y(_peak)), Eve.Eve.TextBright with { A = 0.6f }, 1);
            DrawLine(new Vector2(bar.Position.X - 2, Y(_held)), new Vector2(bar.End.X + 2, Y(_held)), _held > -3 ? Eve.Eve.Red : Eve.Eve.TextBright, 2);

            for (var db = 0; db >= FloorDb; db -= 10)
            {
                var y = Y(db);
                DrawLine(new Vector2(bar.End.X + 2, y), new Vector2(bar.End.X + 6, y), Eve.Eve.Muted with { A = 0.6f }, 1);
                Write(db.ToString(), new Vector2(bar.End.X + 9, y + 4), Eve.Eve.Muted, 10);
            }
            Write(_rms <= FloorDb ? "  -∞" : $"{_rms,5:0.0}", new Vector2(area.Position.X, area.End.Y - 12), Eve.Eve.TextBright, 11);
            Write(_held <= FloorDb ? "pk -∞" : $"pk{_held,5:0.0}", new Vector2(area.Position.X, area.End.Y), _held > -3 ? Eve.Eve.Red : Eve.Eve.Muted, 10);
        }

        // ── The spectrum analyser ──

        private void DrawSpectrum(Rect2 area)
        {
            Write("SPECTRUM", new Vector2(area.Position.X, area.Position.Y - 8), Eve.Eve.Muted, 10);
            DrawRect(area, Eve.Eve.Sunken(0.4f));
            var frequencies = _vm!.Recorder.Analyzer.BandFrequencies;
            var low = Math.Log(frequencies[0]);
            var span = Math.Log(frequencies[^1]) - low;
            float X(double hz) => area.Position.X + area.Size.X * (float)((Math.Log(Math.Max(hz, frequencies[0])) - low) / span);
            float Y(float db) => area.Position.Y + area.Size.Y * (1 - Mathf.Clamp((db - FloorDb) / -FloorDb, 0, 1));

            // The grid: the decades and their halves, every 20 dB.
            foreach (var hz in new[] { 100, 200, 500, 1000, 2000, 5000, 10000 })
            {
                var x = X(hz);
                DrawLine(new Vector2(x, area.Position.Y), new Vector2(x, area.End.Y), Eve.Eve.Edge with { A = 0.25f }, 1);
                Write(hz >= 1000 ? $"{hz / 1000}k" : hz.ToString(), new Vector2(x - 10, area.End.Y + 12), Eve.Eve.Muted, 10);
            }
            for (var db = -20; db > FloorDb; db -= 20)
            {
                DrawLine(new Vector2(area.Position.X, Y(db)), new Vector2(area.End.X, Y(db)), Eve.Eve.Edge with { A = 0.2f }, 1);
                Write(db.ToString(), new Vector2(area.Position.X - 28, Y(db) + 4), Eve.Eve.Muted, 10);
            }

            // The bands.
            var width = area.Size.X / AudioAnalyzer.Bands;
            for (var b = 0; b < AudioAnalyzer.Bands; b++)
            {
                var top = Y(_spectrum[b]);
                if (top >= area.End.Y - 0.5f)
                    continue;
                var color = _spectrum[b] > -6 ? Eve.Eve.Red : _spectrum[b] > -20 ? Eve.Eve.Amber : Eve.Eve.Cyan;
                DrawRect(new Rect2(area.Position.X + b * width + 0.5f, top, MathF.Max(1, width - 1), area.End.Y - top), color with { A = 0.8f });
            }

            if (!_voiced)
                return;
            // The envelope of the voice, and its formants.
            var points = new System.Collections.Generic.List<Vector2>(AudioAnalyzer.Bands);
            for (var b = 0; b < AudioAnalyzer.Bands; b++)
                if (!float.IsNaN(_envelope[b]))
                    points.Add(new Vector2(area.Position.X + (b + 0.5f) * width, Y(MathF.Max(FloorDb, _envelope[b]))));
            if (points.Count > 1)
                DrawPolyline(points.ToArray(), Eve.Eve.TextBright with { A = 0.85f }, 1.5f, true);
            for (var i = 0; i < _formantCount; i++)
            {
                var x = X(_formants[i]);
                DrawLine(new Vector2(x, area.Position.Y), new Vector2(x, area.End.Y), FormantColors[i] with { A = 0.9f }, 1.5f);
                Write($"F{i + 1}", new Vector2(x + 3, area.Position.Y + 12), FormantColors[i], 11);
            }
        }

        // ── The formant analysis, live ──

        private void DrawFormantValues(Vector2 header)
        {
            var x = header.X;
            Write("FORMANTS", new Vector2(x, header.Y + 4), Eve.Eve.Muted, 10);
            x += 76;
            Write(_voiced ? $"F0 {_pitch,4:0} Hz" : "F0   — ", new Vector2(x, header.Y + 6), _voiced ? Eve.Eve.TextBright : Eve.Eve.Muted, 13);
            x += 104;
            for (var i = 0; i < 4; i++)
            {
                var has = _voiced && i < _formantCount;
                Write(has ? $"F{i + 1} {_formants[i],4:0} Hz" : $"F{i + 1}   — ", new Vector2(x, header.Y + 6), has ? FormantColors[i] : Eve.Eve.Muted, 13);
                x += 104;
            }
            if (_voiced && _vowel.Length > 0)
                Write($"≈ [{_vowel}]", new Vector2(x, header.Y + 6), Eve.Eve.TextBright, 14);
            else if (!_voiced)
                Write("no voice", new Vector2(x, header.Y + 6), Eve.Eve.Muted, 11);
        }

        // ── The audio track of the recording selected ──

        private static readonly Color ClipBody = new(0.13f, 0.42f, 0.38f);
        private static readonly Color ClipEdge = new(0.30f, 0.78f, 0.66f);
        private static readonly Color ClipWave = new(0.62f, 0.98f, 0.84f);
        private static readonly Color Playhead = new(0.25f, 0.6f, 1f);
        private static readonly double[] RulerSteps = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];

        /// <summary>
        /// The recording as the audio track of a video editor: the ruler of the time in view, the header of the track,
        /// the clip (its name, its waveform), the part selected and the playhead.
        /// </summary>
        private void DrawTrack(Rect2 ruler, Rect2 track, Rect2 header)
        {
            DrawRect(ruler, new Color(0.10f, 0.10f, 0.12f));
            DrawRect(track, new Color(0.14f, 0.14f, 0.16f));
            DrawRect(header, new Color(0.20f, 0.20f, 0.23f));
            Write("A1", new Vector2(header.Position.X + 4, header.Position.Y + 16), Eve.Eve.TextBright, 11);
            Write("🎤", new Vector2(header.Position.X + 4, header.Position.Y + 36), Eve.Eve.Muted, 11);
            if (_shown is not { } data)
            {
                DrawRect(track, Eve.Eve.Edge with { A = 0.3f }, false, 1);
                return;
            }

            var span = _viewEnd - _viewStart;
            float X(double seconds) => track.Position.X + track.Size.X * (float)((seconds - _viewStart) / span);

            // The ruler: a mark and its time at each step, about ten across.
            var step = RulerSteps[^1];
            foreach (var candidate in RulerSteps)
            {
                if (candidate >= span / 10)
                {
                    step = candidate;
                    break;
                }
            }
            for (var time = Math.Ceiling(_viewStart / step) * step; time <= _viewEnd + 1e-9; time += step)
            {
                var x = X(time);
                DrawLine(new Vector2(x, ruler.End.Y - 6), new Vector2(x, ruler.End.Y), Eve.Eve.Muted, 1);
                var label = step < 1 ? $"{time:0.00}" : RecorderWindowViewModel.Clock(time)[..^2];
                if (x + 4 < ruler.End.X - 30)
                    Write(label, new Vector2(x + 3, ruler.End.Y - 5), Eve.Eve.Muted, 10);
            }

            // The clip: the recording, from its start to its end, what of it is in view.
            var left = MathF.Max(track.Position.X, X(0));
            var right = MathF.Min(track.End.X, X(data.Seconds));
            if (right > left + 1)
            {
                var clip = new Rect2(left, track.Position.Y + 2, right - left, track.Size.Y - 4);
                DrawRect(clip, ClipBody);

                // The waveform: for each pixel, from the lowest to the highest sample of its time.
                var pixels = (int)clip.Size.X;
                if (_wave.Length != pixels * 2)
                    _wave = new Vector2[pixels * 2];
                var middle = clip.Position.Y + clip.Size.Y / 2 + 5;
                var half = (clip.Size.Y - 18) / 2;
                for (var i = 0; i < pixels; i++)
                {
                    var from = (_viewStart + (clip.Position.X + i - track.Position.X) / track.Size.X * span) / data.ColumnSeconds;
                    var to = (_viewStart + (clip.Position.X + i + 1 - track.Position.X) / track.Size.X * span) / data.ColumnSeconds;
                    var first = Math.Clamp((int)from, 0, data.Columns - 1);
                    var last = Math.Clamp(Math.Max(first, (int)Math.Ceiling(to) - 1), 0, data.Columns - 1);
                    float low = 0, high = 0;
                    for (var k = first; k <= last; k++)
                    {
                        low = MathF.Min(low, data.Low[k]);
                        high = MathF.Max(high, data.High[k]);
                    }
                    _wave[2 * i] = new Vector2(clip.Position.X + i + 0.5f, middle - high * half);
                    _wave[2 * i + 1] = new Vector2(clip.Position.X + i + 0.5f, middle - low * half + 1);
                }
                if (pixels > 0)
                    DrawMultiline(_wave, ClipWave, 1);
                DrawLine(new Vector2(clip.Position.X, middle), new Vector2(clip.End.X, middle), ClipWave with { A = 0.35f }, 1);
                DrawRect(clip, ClipEdge, false, 1);
                // Its name, on a band at its top.
                DrawRect(new Rect2(clip.Position.X, clip.Position.Y, clip.Size.X, 13), new Color(0, 0, 0, 0.28f));
                var name = _vm!.SelectedRecording?.Name ?? string.Empty;
                Write($"{name}  ·  {RecorderWindowViewModel.Clock(data.Seconds)}", new Vector2(clip.Position.X + 5, clip.Position.Y + 10), Eve.Eve.TextBright, 10,
                    HorizontalAlignment.Left, MathF.Max(1, clip.Size.X - 8));
            }

            // The part selected, to cut out or to keep.
            if (HasSelection)
            {
                var from = MathF.Max(track.Position.X, X(_selectionStart));
                var to = MathF.Min(track.End.X, X(_selectionEnd));
                if (to > from)
                {
                    DrawRect(new Rect2(from, ruler.Position.Y, to - from, ruler.Size.Y + track.Size.Y), new Color(1, 1, 1, 0.22f));
                    DrawLine(new Vector2(from, ruler.Position.Y), new Vector2(from, track.End.Y), Colors.White, 1);
                    DrawLine(new Vector2(to, ruler.Position.Y), new Vector2(to, track.End.Y), Colors.White, 1);
                    Write($"{_selectionEnd - _selectionStart:0.00} s", new Vector2(from + 4, track.End.Y - 5), Colors.White, 10);
                }
            }

            // The named selections, as bands at the top of the ruler with their names.
            foreach (var selection in _vm!.Selections)
            {
                var from = MathF.Max(ruler.Position.X, X(selection.Start));
                var to = MathF.Min(ruler.End.X, X(selection.End));
                if (to <= from)
                    continue;
                DrawRect(new Rect2(from, ruler.Position.Y + 1, MathF.Max(2, to - from), 12), new Color(0.62f, 0.42f, 0.95f, 0.75f));
                if (to - from > 14)
                    Write(selection.Name, new Vector2(from + 3, ruler.Position.Y + 11), Colors.White, 9, HorizontalAlignment.Left, to - from - 4);
            }

            // The annotations, as marks on the ruler; the playhead over the ruler and the track.
            foreach (var annotation in _vm.Annotations)
            {
                if (annotation.Time >= _viewStart && annotation.Time <= _viewEnd)
                    DrawRect(new Rect2(X(annotation.Time) - 3, ruler.Position.Y + 14, 6, 8), new Color(0.95f, 0.75f, 0.2f));
            }
            if (_vm.PlayPosition is { } position && position >= _viewStart && position <= _viewEnd)
            {
                var x = X(position);
                DrawLine(new Vector2(x, ruler.Position.Y), new Vector2(x, track.End.Y), Playhead, 1.5f);
                DrawColoredPolygon([new Vector2(x - 5, ruler.Position.Y), new Vector2(x + 5, ruler.Position.Y), new Vector2(x, ruler.Position.Y + 8)], Playhead);
            }
            DrawRect(new Rect2(ruler.Position, new Vector2(ruler.Size.X, ruler.Size.Y + track.Size.Y)), Eve.Eve.Edge with { A = 0.5f }, false, 1);
        }

        // ── The spectrogram of the recording selected ──

        private void DrawSpectrogram(Rect2 area)
        {
            if (_shown is not { } data)
            {
                // Nothing while it records: the spectrogram comes with the end of the recording.
                DrawRect(area, Eve.Eve.Sunken(0.4f));
                DrawRect(area, Eve.Eve.Edge with { A = 0.3f }, false, 1);
                var message = _vm!.IsRecording ? "● Recording… the spectrogram is shown when the recording ends"
                    : _vm.IsAnalysing ? "Computing the spectrogram…"
                    : "The spectrogram of a recording is shown here: record one, or select one in the list";
                Write(message, new Vector2(area.Position.X, area.Position.Y + area.Size.Y / 2), _vm.IsRecording ? Eve.Eve.Red : Eve.Eve.Muted, 13,
                    HorizontalAlignment.Center, area.Size.X);
                return;
            }

            // The time across (what is in view), 0 to 8000 Hz upwards.
            DrawTextureRect(SpectrogramTexture(data), area, false);
            DrawRect(area, Eve.Eve.Edge with { A = 0.5f }, false, 1);
            var span = _viewEnd - _viewStart;
            float X(double seconds) => area.Position.X + area.Size.X * (float)((seconds - _viewStart) / span);
            float Y(double hz) => area.End.Y - area.Size.Y * (float)Math.Clamp(hz / Spectrogram.MaxHz, 0, 1);
            var ink = new Color(0, 0, 0, 0.55f);
            for (var hz = 0; hz <= Spectrogram.MaxHz; hz += 1000)
            {
                if (hz is > 0 and < (int)Spectrogram.MaxHz)
                    DrawDashedLine(new Vector2(area.Position.X, Y(hz)), new Vector2(area.End.X, Y(hz)), ink, 1, 3);
                Write(hz.ToString(), new Vector2(area.End.X + 5, Y(hz) + 4), Eve.Eve.Muted, 10);
            }

            // The time axis of what is in view: its two ends and a mark at each quarter.
            for (var quarter = 0; quarter <= 4; quarter++)
            {
                var at = area.Position.X + area.Size.X * quarter / 4;
                var seconds = _viewStart + span * quarter / 4;
                var label = span < 10 ? $"{seconds:0.00} s" : RecorderWindowViewModel.Clock(seconds);
                Write(label, new Vector2(quarter == 4 ? at - 46 : quarter == 0 ? at : at - 20, area.End.Y + 12), Eve.Eve.Muted, 10);
            }
            var whole = Math.Max(MinSpan, data.Seconds);
            if (span < whole - 1e-6)
                Write($"zoom ×{whole / span:0.#}", new Vector2(area.End.X - 78, area.Position.Y - 6), Eve.Eve.Muted, 10);

            // The formants found, over their bands.
            if (_showFormants && data.Formants.Length >= 4)
            {
                var count = data.Formants.Length / 4;
                var first = Math.Max(0, (int)(_viewStart / data.FormantSeconds));
                var last = Math.Min(count - 1, (int)Math.Ceiling(_viewEnd / data.FormantSeconds));
                // No more points than pixels across.
                var stride = Math.Max(1, (int)((last - first + 1) / Math.Max(1f, area.Size.X)));
                var dot = Mathf.Clamp(area.Size.X / Math.Max(1, last - first + 1), 2f, 6f);
                for (var index = first; index <= last; index += stride)
                {
                    var x = X((index + 0.5) * data.FormantSeconds);
                    if (x < area.Position.X || x > area.End.X - dot)
                        continue;
                    for (var n = 0; n < 4; n++)
                    {
                        var hz = data.Formants[index * 4 + n];
                        if (hz > 0)
                            DrawRect(new Rect2(x, Y(hz) - 1, dot, 2.5f), FormantColors[n]);
                    }
                }
            }

            // The part selected on the track, here too.
            if (HasSelection)
            {
                var from = MathF.Max(area.Position.X, X(_selectionStart));
                var to = MathF.Min(area.End.X, X(_selectionEnd));
                if (to > from && _vm!.HasRegion)
                {
                    // The rectangle drawn here: its time and its band of frequencies.
                    var rectangle = new Rect2(from, Y(_vm.RegionHighHz), to - from, MathF.Max(2, Y(_vm.RegionLowHz) - Y(_vm.RegionHighHz)));
                    DrawRect(rectangle, new Color(1f, 0.55f, 0.1f, 0.22f));
                    DrawRect(rectangle, new Color(1f, 0.45f, 0f), false, 2);
                    var label = $"{_selectionEnd - _selectionStart:0.00} s · {_vm.RegionLowHz:0}–{_vm.RegionHighHz:0} Hz";
                    var labelY = rectangle.Position.Y > area.Position.Y + 16 ? rectangle.Position.Y - 4 : rectangle.End.Y + 12;
                    DrawRect(new Rect2(rectangle.Position.X, labelY - 11, label.Length * 6.6f + 6, 14), new Color(1, 1, 1, 0.8f));
                    Write(label, new Vector2(rectangle.Position.X + 3, labelY), new Color(0.6f, 0.25f, 0f), 10);
                }
                else if (to > from)
                    DrawRect(new Rect2(from, area.Position.Y, to - from, area.Size.Y), new Color(0.2f, 0.45f, 1f, 0.16f));
            }

            // The annotations in view, and where the playing is.
            foreach (var annotation in _vm!.Annotations)
            {
                if (annotation.Time < _viewStart || annotation.Time > _viewEnd)
                    continue;
                var x = X(annotation.Time);
                DrawLine(new Vector2(x, area.Position.Y), new Vector2(x, area.End.Y), new Color(0.1f, 0.3f, 0.8f, 0.7f), 1.5f);
                DrawRect(new Rect2(x - 3, area.Position.Y - 6, 6, 6), new Color(0.1f, 0.3f, 0.8f));
            }
            if (_vm.PlayPosition is { } position && position >= _viewStart && position <= _viewEnd)
                DrawLine(new Vector2(X(position), area.Position.Y), new Vector2(X(position), area.End.Y), Playhead, 1.5f);
        }
    }
}
