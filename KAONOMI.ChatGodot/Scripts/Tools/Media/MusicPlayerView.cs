using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Media;

/// <summary>
/// /music: the music folders of <c>music.directory</c>. Header: ↑ parent folder, the clickable path, Open folder (Finder /
/// Explorer), ⟳ refresh (F5), the filter of the tracks by name. Body: the sub-folders as buttons, the tracks of the
/// folder (number or ♪ for the one playing, name, format, size; double-click or Enter plays one, the list becomes the
/// queue). Now playing: the cover (or ♫), title and artist / album, the position (drag to seek, the time and duration),
/// shuffle, previous, play / pause (Space), next, repeat (all, one, off), stop, the volume and a level meter of the
/// loudness. The audio itself is played by <see cref="MusicEngine"/> (a helper process), stopped when the window closes.
/// </summary>
[ToolView(typeof(MusicPlayerViewModel), "Music", "🎵", 1200, 760)]
public sealed partial class MusicPlayerView : IToolView
{
    /// <summary>Created once by the shell (after Avalonia has started): the helper of the player is made available.</summary>
    public MusicPlayerView() => MusicAssets.Install();

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (MusicPlayerViewModel)viewModel;

        // Header: title, ↑, path, open folder, refresh; the filter below.
        var crumbs = new HBoxContainer();
        crumbs.AddThemeConstantOverride("separation", 0);
        Bind.Items(crumbs, vm.Breadcrumbs, folder => Crumb(vm, folder));
        var up = Ui.Command("↑", vm.UpCommand, tooltip: "Parent folder");
        var header = Ui.Row(8,
            Eve.Eve.Caption("Music", Eve.Eve.TextBright, 14),
            up,
            Ui.Fill(HorizontalScroll(Ui.Scroll(crumbs))),
            Ui.Command("OPEN FOLDER", vm.OpenFolderInSystemCommand, tooltip: "Open this folder in the Finder / Explorer"),
            Ui.Command("REFRESH", vm.RefreshCommand, tooltip: "Read the folder again (F5)"));
        var filter = Ui.Line(vm, nameof(vm.SearchText), "Filter the tracks of this folder by name…");
        var toolbar = Ui.Toolbar(Ui.Column(8, header, filter));

        // Folders (a flow of buttons, at most 150 high) and tracks.
        var folders = new HFlowContainer();
        folders.AddThemeConstantOverride("h_separation", 8);
        folders.AddThemeConstantOverride("v_separation", 8);
        Bind.Items(folders, vm.Folders, folder => FolderButton(vm, folder));
        var folderScroll = Ui.Scroll(folders);
        folderScroll.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        Bind.Visible(folderScroll, vm, nameof(vm.HasFolders));
        // As high as the folders, 150 at most (then it scrolls).
        folders.SortChildren += () => Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(folderScroll))
                folderScroll.CustomMinimumSize = new Vector2(0, Math.Min(150, folders.GetCombinedMinimumSize().Y));
        }).CallDeferred();

        var tracks = new TrackTable(vm);
        var empty = Ui.Placeholder("No track here");
        empty.MouseFilter = Control.MouseFilterEnum.Ignore;
        Bind.Visible(empty, vm, nameof(vm.IsEmpty));
        var trackArea = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        tracks.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        empty.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        trackArea.AddChild(tracks);
        trackArea.AddChild(empty);
        var left = Ui.Margin(Ui.Column(10, folderScroll, trackArea), 4, 6, 4, 4);

        var page = Ui.Column(6, toolbar, SplitRight(left, NowPlaying(vm), 380), Status(vm));
        return MediaKeys.Attach(page, key =>
        {
            switch (key.Keycode)
            {
                case Key.Space when !key.CtrlPressed && !key.MetaPressed && !IsTyping(page):
                    Bind.Run(vm.PlayPauseCommand);
                    return true;
                case Key.F5:
                    Bind.Run(vm.RefreshCommand);
                    return true;
                default:
                    return false;
            }
        });
    }

    private static bool IsTyping(Control page) => page.GetViewport()?.GuiGetFocusOwner() is LineEdit or TextEdit;

    private static ScrollContainer HorizontalScroll(ScrollContainer scroll)
    {
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.ShowNever;
        scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        scroll.CustomMinimumSize = new Vector2(0, 30);
        if (scroll.GetChildCount() > 0 && scroll.GetChild(0) is Control content)
            content.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        // Keep the end of a long path visible.
        scroll.GetHScrollBar().Changed += () => scroll.ScrollHorizontal = (int)scroll.GetHScrollBar().MaxValue;
        return scroll;
    }

    /// <summary>A folder of the path: › then its name (a click opens it).</summary>
    private static Control Crumb(MusicPlayerViewModel vm, PictureFolder folder)
    {
        var button = Ui.Command(folder.Name, vm.OpenFolderCommand, tooltip: folder.Path, parameter: () => folder);
        button.Flat = true;
        button.AddThemeColorOverride("font_hover_color", Eve.Eve.Cyan);
        return Ui.Row(2, Eve.Eve.Text_("›", Eve.Eve.Muted), button);
    }

    /// <summary>A sub-folder: a button with its name (a click opens it).</summary>
    private static Control FolderButton(MusicPlayerViewModel vm, PictureFolder folder)
    {
        var button = Ui.Command("📁  " + folder.Name, vm.OpenFolderCommand, tooltip: folder.Path, parameter: () => folder);
        button.CustomMinimumSize = new Vector2(190, 40);
        button.Alignment = HorizontalAlignment.Left;
        button.ClipText = true;
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return button;
    }

    /// <summary>The list and the now-playing panel, the panel <paramref name="width"/> wide at first.</summary>
    private static HSplitContainer SplitRight(Control left, Control right, int width)
    {
        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        left.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        right.CustomMinimumSize = new Vector2(width, 0);
        split.AddChild(left);
        split.AddChild(right);
        return split;
    }

    /// <summary>The track being played: cover, titles, position, transport, volume and level.</summary>
    private static Control NowPlaying(MusicPlayerViewModel vm)
    {
        // Cover.
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(260, 260), SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        frame.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.35f), Eve.Eve.Edge with { A = 0.5f }, 1));
        var cover = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            ClipContents = true
        };
        var note = Eve.Eve.Text_("♫", Eve.Eve.Cyan with { A = 0.25f }, 96);
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.VerticalAlignment = VerticalAlignment.Center;
        frame.AddChild(note);
        frame.AddChild(cover);
        Bind.When(cover, vm, () =>
        {
            cover.Texture = AvaloniaBridge.ToTexture(vm.Artwork);
            note.Visible = cover.Texture is null;
        }, nameof(vm.Artwork));

        var title = Ui.Label(vm, nameof(vm.Title), Eve.Eve.TextBright, 17, wrap: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.MaxLinesVisible = 2;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var subtitle = Ui.Label(vm, nameof(vm.Subtitle), Eve.Eve.Muted, 12);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        subtitle.ClipText = true;
        subtitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;

        // Position: follows the playback, except while held; the track jumps where it is released.
        var position = new HSlider { MinValue = 0, Step = 0.1, TooltipText = "Drag to move in the track", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
        var dragging = false;
        Bind.When(position, vm, () =>
        {
            position.MaxValue = Math.Max(vm.Duration, 0.1);
            if (!dragging)
                position.SetValueNoSignal(vm.Position);
            position.Editable = vm.HasTrack;
        }, nameof(vm.Position), nameof(vm.Duration), nameof(vm.HasTrack));
        position.DragStarted += () =>
        {
            dragging = true;
            vm.IsSeeking = true;
        };
        position.DragEnded += _ =>
        {
            vm.SeekTo(position.Value);
            vm.IsSeeking = false;
            dragging = false;
        };
        var times = Ui.Row(0, Ui.Fill(Ui.Label(vm, nameof(vm.PositionLabel), Eve.Eve.Muted, 11)), Ui.Label(vm, nameof(vm.DurationLabel), Eve.Eve.Muted, 11));

        // Transport.
        var shuffle = Transport(TransportButton.Shuffle, vm.ToggleShuffleCommand, "Shuffle");
        Bind.When(shuffle, vm, () => shuffle.Modulate = new Color(1, 1, 1, (float)vm.ShuffleOpacity), nameof(vm.ShuffleOpacity));
        var play = Transport(TransportButton.Play, vm.PlayPauseCommand, "Play / pause (Space)", 60);
        Eve.Eve.StylePrimary(play);
        Round(play, 60);
        Bind.When(play, vm, () => play.Glyph = vm.IsPlaying ? TransportButton.Pause : TransportButton.Play, nameof(vm.IsPlaying));
        var repeat = Transport(TransportButton.Repeat, vm.ToggleRepeatCommand, "Repeat: all, one, off");
        Bind.When(repeat, vm, () =>
        {
            repeat.Glyph = vm.Repeat == MusicRepeat.One ? TransportButton.RepeatOne : TransportButton.Repeat;
            repeat.Modulate = new Color(1, 1, 1, (float)vm.RepeatOpacity);
        }, nameof(vm.Repeat));
        var transport = Ui.Row(6,
            shuffle,
            Transport(TransportButton.Previous, vm.PreviousCommand, "Previous track (back to the start after 3 s)"),
            play,
            Transport(TransportButton.Next, vm.NextCommand, "Next track"),
            repeat);
        transport.Alignment = BoxContainer.AlignmentMode.Center;
        var stop = Ui.Command("■  STOP", vm.StopCommand);
        stop.FocusMode = Control.FocusModeEnum.None;
        stop.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        stop.CustomMinimumSize = new Vector2(110, 0);

        // Volume and level.
        var volume = Bind.Range(new HSlider { MinValue = 0, MaxValue = 100, Step = 1, TooltipText = "Volume", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }, vm, nameof(vm.Volume));
        var volumeRow = Ui.Row(10, Eve.Eve.Caption("Volume"), volume, Ui.Label(vm, nameof(vm.Volume), Eve.Eve.Muted, 11, format: v => v is double d ? $"{d:0}" : ""));
        var meter = new LevelMeter(vm) { TooltipText = "Loudness of the track", CustomMinimumSize = new Vector2(0, 46), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        var column = Ui.Column(10,
            frame,
            Ui.Column(2, title, subtitle),
            Ui.Column(0, position, times),
            transport,
            stop,
            Ui.Margin(volumeRow, 0, 6, 0, 0),
            Eve.Eve.Caption("Level"),
            meter);
        return Ui.Scroll(Ui.Margin(column, 18, 16, 18, 16));
    }

    /// <summary>A round transport button drawing <paramref name="glyph"/> (no focus: Space stays play / pause).</summary>
    private static TransportButton Transport(string glyph, System.Windows.Input.ICommand command, string tooltip, int size = 44)
    {
        var button = Bind.Command(new TransportButton { Glyph = glyph, TooltipText = tooltip, MouseDefaultCursorShape = Control.CursorShape.PointingHand }, command);
        button.FocusMode = Control.FocusModeEnum.None;
        button.CustomMinimumSize = new Vector2(size, size);
        button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        Round(button, size);
        if (size < 60)
        {
            var normal = (StyleBoxFlat)button.GetThemeStylebox("normal");
            normal.BgColor = new Color(0, 0, 0, 0);
            normal.BorderColor = Eve.Eve.Edge with { A = 0.25f };
        }
        return button;
    }

    /// <summary>Makes the boxes of <paramref name="button"/> round.</summary>
    private static void Round(Button button, int size)
    {
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled" })
        {
            if (button.GetThemeStylebox(state) is not StyleBoxFlat flat)
                continue;
            var box = (StyleBoxFlat)flat.Duplicate();
            box.SetCornerRadiusAll(size / 2);
            box.AntiAliasing = true;
            button.AddThemeStyleboxOverride(state, box);
        }
    }

    /// <summary>The status, and the root of the music folders.</summary>
    private static Control Status(MusicPlayerViewModel vm)
    {
        var bar = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var root = Ui.Label(vm, nameof(vm.RootDirectory), Eve.Eve.Muted with { A = 0.7f }, 11, format: v => $"· music: {v}");
        root.ClipText = true;
        root.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        root.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        if (bar.GetChild(0) is HBoxContainer row)
            row.AddChild(root);
        return bar;
    }

    /// <summary>
    /// The tracks of the folder: number (♪ for the one playing, in cyan and bold), name (its path as tooltip), format,
    /// size; the selection follows <see cref="MusicPlayerViewModel.SelectedTrack"/>, a double-click or Enter plays.
    /// </summary>
    private sealed partial class TrackTable : Tree
    {
        private readonly MusicPlayerViewModel? _vm;
        private readonly Dictionary<MusicTrackViewModel, TreeItem> _rows = new(ReferenceEqualityComparer.Instance);
        private TreeItem? _root;
        private bool _selecting;

        public TrackTable()
        {
        }

        public TrackTable(MusicPlayerViewModel vm)
        {
            _vm = vm;
            Columns = 4;
            HideRoot = true;
            SelectMode = SelectModeEnum.Row;
            ColumnTitlesVisible = false;
            SetColumnExpand(0, false);
            SetColumnCustomMinimumWidth(0, 44);
            SetColumnExpand(1, true);
            SetColumnExpand(2, false);
            SetColumnCustomMinimumWidth(2, 64);
            SetColumnExpand(3, false);
            SetColumnCustomMinimumWidth(3, 86);
            AddThemeConstantOverride("v_separation", 8);
            NotifyCollectionChangedEventHandler changed = (_, _) => Bind.OnMain(Fill);
            vm.Tracks.CollectionChanged += changed;
            Bind.OnGone(this, () => vm.Tracks.CollectionChanged -= changed);
            // The track played is marked once the view model has updated both tracks (IsCurrent is set after CurrentTrack).
            Bind.When(this, vm, () => Callable.From(Mark).CallDeferred(), nameof(vm.CurrentTrack));
            Bind.When(this, vm, () => Callable.From(Select).CallDeferred(), nameof(vm.SelectedTrack));
            ItemSelected += () =>
            {
                if (!_selecting && GetSelected()?.GetMetadata(0).Obj is TrackRef { Track: var track })
                    vm.SelectedTrack = track;
            };
            ItemActivated += () => vm.PlayTrackCommand.Execute(vm.SelectedTrack);
            // A right click on a track selects it and offers to annotate the audio file.
            AllowRmbSelect = true;
            ItemMouseSelected += (position, button) =>
            {
                if (button == (long)MouseButton.Right && GetSelected()?.GetMetadata(0).Obj is TrackRef { Track: var track })
                    Data.Annotations.ShowMenu(this, position, new AnnotationTarget(AnnotationTargetTypes.Audio, track.Path, track.Name));
            };
            Fill();
        }

        private sealed partial class TrackRef(MusicTrackViewModel track) : RefCounted
        {
            public MusicTrackViewModel Track { get; } = track;
        }

        private void Fill()
        {
            if (_vm is null || !IsInstanceValid(this))
                return;
            Clear();
            _rows.Clear();
            _root = CreateItem();
            foreach (var track in _vm.Tracks)
            {
                var row = CreateItem(_root);
                row.SetMetadata(0, new TrackRef(track));
                row.SetText(1, track.Name);
                row.SetTooltipText(1, track.Path);
                row.SetText(2, track.Format);
                row.SetText(3, track.SizeLabel);
                row.SetTextAlignment(3, HorizontalAlignment.Right);
                for (var column = 2; column < 4; column++)
                    row.SetCustomColor(column, Eve.Eve.Muted);
                _rows[track] = row;
                Paint(track, row);
            }
            Select();
        }

        private void Paint(MusicTrackViewModel track, TreeItem row)
        {
            var current = track.IsCurrent;
            row.SetText(0, current ? "♪" : track.Number.ToString());
            row.SetCustomColor(0, current ? Eve.Eve.Cyan : Eve.Eve.Muted);
            row.SetCustomColor(1, current ? Eve.Eve.Cyan.Lightened(0.25f) : Eve.Eve.Text);
            if (current)
            {
                row.SetCustomFont(0, BoldFont);
                row.SetCustomFont(1, BoldFont);
            }
            else
            {
                row.SetCustomFont(0, null);
                row.SetCustomFont(1, null);
            }
        }

        private static readonly SystemFont BoldFont = new() { FontNames = Eve.Eve.Body.FontNames, FontWeight = 700, Fallbacks = [Eve.Eve.Emoji] };

        private void Mark()
        {
            if (!IsInstanceValid(this))
                return;
            foreach (var (track, row) in _rows)
                Paint(track, row);
        }

        private void Select()
        {
            if (_vm is null || !IsInstanceValid(this))
                return;
            _selecting = true;
            if (_vm.SelectedTrack is { } track && _rows.TryGetValue(track, out var row))
            {
                if (GetSelected() != row)
                {
                    SetSelected(row, 1);
                    ScrollToItem(row);
                }
            }
            else
            {
                DeselectAll();
            }
            _selecting = false;
        }
    }

    /// <summary>A button drawing a transport symbol (play, pause, previous, next, shuffle, repeat, repeat one) in its font colour.</summary>
    private sealed partial class TransportButton : Button
    {
        public const string Play = "play", Pause = "pause", Previous = "previous", Next = "next", Shuffle = "shuffle", Repeat = "repeat", RepeatOne = "repeat-one";

        private string _glyph = Play;

        public string Glyph
        {
            get => _glyph;
            set
            {
                _glyph = value;
                QueueRedraw();
            }
        }

        public override void _Draw()
        {
            var color = Disabled ? Eve.Eve.Muted with { A = 0.5f } : IsHovered() || ButtonPressed ? Eve.Eve.TextBright : GetThemeColor("font_color");
            var c = Size / 2;
            var u = Math.Min(Size.X, Size.Y) * 0.16f;
            switch (_glyph)
            {
                case Play:
                    DrawColoredPolygon([c + new Vector2(-u * 0.8f, -u * 1.2f), c + new Vector2(u * 1.3f, 0), c + new Vector2(-u * 0.8f, u * 1.2f)], color);
                    break;
                case Pause:
                    DrawRect(new Rect2(c.X - u, c.Y - u * 1.1f, u * 0.7f, u * 2.2f), color);
                    DrawRect(new Rect2(c.X + u * 0.3f, c.Y - u * 1.1f, u * 0.7f, u * 2.2f), color);
                    break;
                case Previous:
                    DrawRect(new Rect2(c.X - u * 1.2f, c.Y - u, u * 0.35f, u * 2), color);
                    DrawColoredPolygon([c + new Vector2(u * 1.1f, -u), c + new Vector2(u * 1.1f, u), c + new Vector2(-u * 0.75f, 0)], color);
                    break;
                case Next:
                    DrawRect(new Rect2(c.X + u * 0.85f, c.Y - u, u * 0.35f, u * 2), color);
                    DrawColoredPolygon([c + new Vector2(-u * 1.1f, -u), c + new Vector2(-u * 1.1f, u), c + new Vector2(u * 0.75f, 0)], color);
                    break;
                case Shuffle:
                    DrawArrowLine([c + new Vector2(-u * 1.3f, u * 0.8f), c + new Vector2(-u * 0.4f, u * 0.8f), c + new Vector2(u * 0.5f, -u * 0.8f), c + new Vector2(u * 1.3f, -u * 0.8f)], color, u);
                    DrawArrowLine([c + new Vector2(-u * 1.3f, -u * 0.8f), c + new Vector2(-u * 0.4f, -u * 0.8f), c + new Vector2(u * 0.5f, u * 0.8f), c + new Vector2(u * 1.3f, u * 0.8f)], color, u);
                    break;
                case Repeat or RepeatOne:
                    DrawArrowLine([c + new Vector2(-u * 1.3f, u * 0.2f), c + new Vector2(-u * 1.3f, -u * 0.8f), c + new Vector2(u * 1.3f, -u * 0.8f)], color, u);
                    DrawArrowLine([c + new Vector2(u * 1.3f, -u * 0.2f), c + new Vector2(u * 1.3f, u * 0.8f), c + new Vector2(-u * 1.3f, u * 0.8f)], color, u);
                    if (_glyph == RepeatOne)
                    {
                        var font = GetThemeDefaultFont();
                        DrawString(font, c + new Vector2(-u * 0.3f, u * 0.4f), "1", HorizontalAlignment.Left, -1, (int)(u * 1.3f), color);
                    }
                    break;
            }
        }

        /// <summary>A polyline ending with an arrow head.</summary>
        private void DrawArrowLine(Vector2[] points, Color color, float u)
        {
            DrawPolyline(points, color, Math.Max(1.5f, u * 0.22f), true);
            var end = points[^1];
            var direction = (end - points[^2]).Normalized();
            var side = new Vector2(-direction.Y, direction.X);
            DrawColoredPolygon([end + direction * u * 0.35f, end - direction * u * 0.3f + side * u * 0.4f, end - direction * u * 0.3f - side * u * 0.4f], color);
        }
    }

    /// <summary>
    /// The loudness of the track: a row of segments lit from the left (cyan, amber near the top, red at the top), the
    /// peak held a moment, and a trace of the last seconds behind it.
    /// </summary>
    private sealed partial class LevelMeter : Control
    {
        private const int Segments = 32;
        private const int History = 96;
        private readonly MusicPlayerViewModel? _vm;
        private readonly float[] _trace = new float[History];
        private int _head;
        private float _level;
        private float _peak;
        private double _peakAge;
        private double _sample;

        public LevelMeter()
        {
        }

        public LevelMeter(MusicPlayerViewModel vm)
        {
            _vm = vm;
            MouseFilter = MouseFilterEnum.Pass;
        }

        public override void _Process(double delta)
        {
            if (_vm is null)
                return;
            var target = (float)Math.Clamp(_vm.IsPlaying ? _vm.Level : 0, 0, 1);
            // Rise fast, fall slowly.
            _level = target > _level ? Mathf.Lerp(_level, target, 0.6f) : Mathf.MoveToward(_level, target, (float)delta * 1.2f);
            if (_level >= _peak)
            {
                _peak = _level;
                _peakAge = 0;
            }
            else if ((_peakAge += delta) > 0.8)
            {
                _peak = Mathf.MoveToward(_peak, _level, (float)delta * 0.6f);
            }
            if ((_sample += delta) >= 1.0 / 30)
            {
                _sample = 0;
                _trace[_head] = _level;
                _head = (_head + 1) % History;
            }
            QueueRedraw();
        }

        public override void _Draw()
        {
            var size = Size;
            DrawRect(new Rect2(Vector2.Zero, size), Eve.Eve.Sunken(0.3f));
            DrawRect(new Rect2(Vector2.Zero, size), Eve.Eve.Edge with { A = 0.35f }, false, 1);

            // Trace of the last seconds.
            var traceHeight = size.Y - 14;
            var points = new Vector2[History];
            for (var i = 0; i < History; i++)
            {
                var value = _trace[(_head + i) % History];
                points[i] = new Vector2(2 + i * (size.X - 4) / (History - 1), 2 + traceHeight * (1 - value));
            }
            DrawPolyline(points, Eve.Eve.Cyan with { A = 0.3f }, 1, true);

            // Segments.
            var gap = 2f;
            var width = (size.X - 4 - gap * (Segments - 1)) / Segments;
            var top = size.Y - 10;
            var lit = (int)Math.Round(_level * Segments);
            var peak = Math.Clamp((int)Math.Round(_peak * Segments) - 1, 0, Segments - 1);
            for (var i = 0; i < Segments; i++)
            {
                var color = i >= Segments - 3 ? Eve.Eve.Red : i >= Segments * 3 / 4 ? Eve.Eve.Amber : Eve.Eve.Cyan;
                var on = i < lit || (i == peak && _peak > 0.02f);
                DrawRect(new Rect2(2 + i * (width + gap), top, width, 8), on ? color : color with { A = 0.12f });
            }
        }
    }
}
