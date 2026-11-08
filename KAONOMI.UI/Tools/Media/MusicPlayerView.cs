using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// /music: the music folders of <c>music.directory</c>. Header: ↑ parent folder, the clickable path, Open folder (Finder /
/// Explorer), Refresh (F5), the filter of the tracks by name. Body: the sub-folders as buttons, the tracks of the folder
/// (number or ♪ for the one playing, name, format, size; double-click or Enter plays one, the list becomes the queue;
/// right click: annotate). Now playing: the cover (or ♫), title and artist / album, the position (drag to seek, the time
/// and duration), shuffle, previous, play / pause (Space), next, repeat (all, one, off), stop, the volume and a level
/// meter of the loudness. The audio itself is played by <see cref="MusicEngine"/> (a helper process).
/// </summary>
[ToolView(typeof(MusicPlayerViewModel), "Music", "🎵", 1200, 760)]
public sealed class MusicPlayerView : IToolView
{
    /// <summary>Created once by the shell (after Avalonia has started): the helper of the player is made available.</summary>
    public MusicPlayerView() => MusicAssets.Install();

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (MusicPlayerViewModel)viewModel;

        // Header: title, ↑, path, open folder, refresh; the filter below.
        var crumbs = Kit.Items(vm.Breadcrumbs, folder => Crumb(vm, folder), () => new StackPanel { Orientation = Orientation.Horizontal });
        var crumbScroll = Kit.ScrollH(crumbs, showBar: false);
        // Keep the end of a long path visible.
        crumbScroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.X != 0)
                crumbScroll.Offset = new Vector(Math.Max(0, crumbScroll.Extent.Width - crumbScroll.Viewport.Width), 0);
        };
        var header = Kit.FillRow(8,
            [Ui.Label("Music", Ui.TextBright, 14, bold: true), Ui.Command("↑", vm.UpCommand, tooltip: "Parent folder")],
            crumbScroll,
            Ui.Command("OPEN FOLDER", vm.OpenFolderInSystemCommand, tooltip: "Open this folder in the Finder / Explorer"),
            Ui.Command("REFRESH", vm.RefreshCommand, tooltip: "Read the folder again (F5)"));
        var filter = Ui.Edit(vm, nameof(vm.SearchText), "Filter the tracks of this folder by name…");
        var toolbar = Kit.Toolbar(Ui.Column(8, header, filter));

        // Folders (a flow of buttons, at most 150 high) and tracks.
        var folders = Kit.Items(vm.Folders, folder => FolderButton(vm, folder), () => new WrapPanel { ItemSpacing = 8, LineSpacing = 8 });
        var folderScroll = new ScrollViewer { Content = folders, MaxHeight = 150, Margin = new Thickness(0, 0, 0, 10) }.Visible(vm, nameof(vm.HasFolders));

        var tracks = TrackList(vm);
        var empty = Kit.Placeholder("No track here").Visible(vm, nameof(vm.IsEmpty));
        var trackArea = new Grid { Children = { tracks, empty } };
        var left = Kit.Margin(Ui.Dock((folderScroll, Dock.Top), (trackArea, Dock.Top)), 4, 6, 4, 4);

        var split = Kit.SplitH(left, NowPlaying(vm), "*", "380", 300, 300);
        var page = Kit.Rows("Auto,*,Auto", toolbar, split, Status(vm));
        Kit.Keys(page, e =>
        {
            switch (e.Key)
            {
                case Key.Space when !Kit.CommandHeld(e.KeyModifiers) && !Kit.IsTyping(e):
                    Ui.Run(vm.PlayPauseCommand);
                    return true;
                case Key.F5:
                    Ui.Run(vm.RefreshCommand);
                    return true;
                default:
                    return false;
            }
        });
        return page;
    }

    /// <summary>A folder of the path: › then its name (a click opens it).</summary>
    private static Control Crumb(MusicPlayerViewModel vm, PictureFolder folder)
    {
        var button = Kit.Command(folder.Name, vm.OpenFolderCommand, () => folder, folder.Path);
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(4, 2);
        return Ui.Row(2, Ui.Label("›", Ui.Muted), button);
    }

    /// <summary>A sub-folder: a button with its name (a click opens it).</summary>
    private static Control FolderButton(MusicPlayerViewModel vm, PictureFolder folder)
    {
        var button = Kit.Command(string.Empty, vm.OpenFolderCommand, () => folder, folder.Path);
        button.Content = Ui.Label("📁  " + folder.Name);
        button.Width = 190;
        button.Height = 40;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        return button;
    }

    /// <summary>
    /// The tracks of the folder: number (♪ for the one playing, in the accent colour and bold), name (its path as
    /// tooltip), format, size; the selection follows <see cref="MusicPlayerViewModel.SelectedTrack"/>, a double-click
    /// or Enter plays, a right click offers to annotate the audio file.
    /// </summary>
    private static Control TrackList(MusicPlayerViewModel vm)
    {
        var list = new ListBox
        {
            ItemsSource = vm.Tracks,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MusicTrackViewModel>((track, _) => track is null ? new Panel() : TrackRow(track), supportsRecycling: false),
            Background = Brushes.Transparent
        };
        list.Bind(SelectingItemsControl.SelectedItemProperty, vm, nameof(vm.SelectedTrack), Avalonia.Data.BindingMode.TwoWay);
        Ui.When(list, vm, () =>
        {
            if (vm.SelectedTrack is { } track)
                Dispatcher.UIThread.Post(() => list.ScrollIntoView(track), DispatcherPriority.Background);
        }, nameof(vm.SelectedTrack));
        list.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is not null)
                vm.PlayTrackCommand.Execute(vm.SelectedTrack);
        };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                vm.PlayTrackCommand.Execute(vm.SelectedTrack);
                e.Handled = true;
            }
        };
        list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!Kit.IsRight(e, list) || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is not { DataContext: MusicTrackViewModel track } item)
                return;
            vm.SelectedTrack = track;
            Kit.AnnotateMenu(item, new AnnotationTarget(AnnotationTargetTypes.Audio, track.Path, track.Name));
        }, RoutingStrategies.Tunnel);
        return list;
    }

    private static Control TrackRow(MusicTrackViewModel track)
    {
        var number = Ui.Label(string.Empty, Ui.Muted, 13);
        var name = Ui.Label(track.Name, Ui.Text, 13);
        ToolTip.SetTip(name, track.Path);
        var format = Ui.Label(track.Format, Ui.Muted, 12);
        var size = Ui.Label(track.SizeLabel, Ui.Muted, 12);
        size.HorizontalAlignment = HorizontalAlignment.Right;
        Ui.When(number, track, () =>
        {
            var current = track.IsCurrent;
            number.Text = current ? "♪" : track.Number.ToString();
            number.Foreground = current ? Ui.Accent : Ui.Muted;
            name.Foreground = current ? Ui.Accent : Ui.Text;
            number.FontWeight = name.FontWeight = current ? FontWeight.Bold : FontWeight.Normal;
        }, nameof(track.IsCurrent));
        var row = Kit.Columns("44,*,64,86", 6, number, name, format, size);
        row.Margin = new Thickness(0, 4);
        return row;
    }

    /// <summary>The track being played: cover, titles, position, transport, volume and level.</summary>
    private static Control NowPlaying(MusicPlayerViewModel vm)
    {
        // Cover.
        var note = Ui.Label("♫", Ui.Edge, 96);
        note.HorizontalAlignment = HorizontalAlignment.Center;
        var cover = new Image { Stretch = Stretch.UniformToFill };
        cover.Bind(Image.SourceProperty, vm, nameof(vm.Artwork));
        Ui.When(cover, vm, () => note.IsVisible = vm.Artwork is null, nameof(vm.Artwork));
        var frame = new Border
        {
            Width = 260,
            Height = 260,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = new Grid { Children = { note, cover } }
        };

        var title = Ui.Bound(vm, nameof(vm.Title), Ui.TextBright, 17, wrap: true);
        title.TextAlignment = TextAlignment.Center;
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var subtitle = Ui.Bound(vm, nameof(vm.Subtitle), Ui.Muted, 12);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;

        // Position: follows the playback, except while held; the track jumps where it is released (or clicked).
        var position = new Slider { Minimum = 0, SmallChange = 0.1, LargeChange = 5, Focusable = false };
        ToolTip.SetTip(position, "Drag to move in the track");
        var dragging = false;
        var updating = false;
        Ui.When(position, vm, () =>
        {
            updating = true;
            position.Maximum = Math.Max(vm.Duration, 0.1);
            if (!dragging)
                position.Value = vm.Position;
            position.IsEnabled = vm.HasTrack;
            updating = false;
        }, nameof(vm.Position), nameof(vm.Duration), nameof(vm.HasTrack));
        position.AddHandler(Thumb.DragStartedEvent, (_, _) =>
        {
            dragging = true;
            vm.IsSeeking = true;
        });
        position.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
        {
            vm.SeekTo(position.Value);
            vm.IsSeeking = false;
            dragging = false;
        });
        position.ValueChanged += (_, _) =>
        {
            // A click on the track (not a drag): the playback jumps there.
            if (!updating && !dragging && vm.HasTrack)
                vm.SeekTo(position.Value);
        };
        var times = Ui.Dock((Ui.Bound(vm, nameof(vm.DurationLabel), Ui.Muted, 11), Dock.Right), (Ui.Bound(vm, nameof(vm.PositionLabel), Ui.Muted, 11), Dock.Left));

        // Transport.
        var shuffle = Transport(Glyph.Shuffle, vm.ToggleShuffleCommand, "Shuffle");
        shuffle.Bind(Visual.OpacityProperty, vm, nameof(vm.ShuffleOpacity));
        var play = Transport(Glyph.Play, vm.PlayPauseCommand, "Play / pause (Space)", 60, primary: true);
        Ui.When(play, vm, () => ((GlyphIcon)play.Content!).Kind = vm.IsPlaying ? Glyph.Pause : Glyph.Play, nameof(vm.IsPlaying));
        var repeat = Transport(Glyph.Repeat, vm.ToggleRepeatCommand, "Repeat: all, one, off");
        Ui.When(repeat, vm, () =>
        {
            ((GlyphIcon)repeat.Content!).Kind = vm.Repeat == MusicRepeat.One ? Glyph.RepeatOne : Glyph.Repeat;
            repeat.Opacity = vm.RepeatOpacity;
        }, nameof(vm.Repeat), nameof(vm.RepeatOpacity));
        var transport = Ui.Row(6,
            shuffle,
            Transport(Glyph.Previous, vm.PreviousCommand, "Previous track (back to the start after 3 s)"),
            play,
            Transport(Glyph.Next, vm.NextCommand, "Next track"),
            repeat);
        transport.HorizontalAlignment = HorizontalAlignment.Center;
        var stop = Ui.Command("■  STOP", vm.StopCommand);
        stop.Focusable = false;
        stop.MinWidth = 110;
        stop.HorizontalAlignment = HorizontalAlignment.Center;
        stop.HorizontalContentAlignment = HorizontalAlignment.Center;

        // Volume and level.
        var volume = Kit.Slider(vm, nameof(vm.Volume), 0, 100, 1, "Volume");
        var volumeRow = Kit.FillRow(10, [Ui.Caption("Volume")], volume, Kit.Text(vm, nameof(vm.Volume), v => v is double d ? $"{d:0}" : string.Empty, Ui.Muted, 11));
        volumeRow.Margin = new Thickness(0, 6, 0, 0);
        var meter = new LevelMeter(vm) { Height = 46 };
        ToolTip.SetTip(meter, "Loudness of the track");

        var column = Ui.Column(10,
            frame,
            Ui.Column(2, title, subtitle),
            Ui.Column(0, position, times),
            transport,
            stop,
            volumeRow,
            Ui.Caption("Level"),
            meter);
        return Ui.Scroll(Kit.Margin(column, 18, 16, 18, 16));
    }

    /// <summary>A round transport button drawing <paramref name="glyph"/> (no focus: Space stays play / pause).</summary>
    private static Button Transport(Glyph glyph, ICommand command, string tooltip, double size = 44, bool primary = false)
    {
        var button = new Button
        {
            Content = new GlyphIcon { Kind = glyph, Width = size * 0.6, Height = size * 0.6 },
            Command = command,
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(size / 2),
            Focusable = false,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (primary)
            button.Classes.Add("primary");
        else
            button.Background = Brushes.Transparent;
        ToolTip.SetTip(button, tooltip);
        return button;
    }

    /// <summary>The status, and the root of the music folders.</summary>
    private static Control Status(MusicPlayerViewModel vm)
    {
        var root = Kit.Text(vm, nameof(vm.RootDirectory), v => $"· music: {v}", Ui.Muted, 11);
        root.MaxWidth = 420;
        return Kit.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), right: root);
    }

    internal enum Glyph { Play, Pause, Previous, Next, Shuffle, Repeat, RepeatOne }

    /// <summary>A transport symbol (play, pause, previous, next, shuffle, repeat, repeat one) drawn in the colour of the text.</summary>
    private sealed class GlyphIcon : Control
    {
        private Glyph _kind;

        public GlyphIcon()
        {
            IsHitTestVisible = false;
        }

        public Glyph Kind
        {
            get => _kind;
            set
            {
                _kind = value;
                InvalidateVisual();
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsEffectivelyEnabledProperty)
                InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            var brush = IsEffectivelyEnabled ? Ui.TextBright : Ui.Muted;
            var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var u = Math.Min(Bounds.Width, Bounds.Height) * 0.27;
            Point P(double x, double y) => new(c.X + x * u, c.Y + y * u);
            void Polygon(params Point[] points)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(points[0], true);
                    for (var i = 1; i < points.Length; i++)
                        g.LineTo(points[i]);
                    g.EndFigure(true);
                }
                context.DrawGeometry(brush, null, geometry);
            }
            void Rect(double x, double y, double w, double h) => context.FillRectangle(brush, new Rect(c.X + x * u, c.Y + y * u, w * u, h * u));
            var pen = new Pen(brush, Math.Max(1.5, u * 0.22), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            void Arrow(params Point[] points)
            {
                for (var i = 1; i < points.Length; i++)
                    context.DrawLine(pen, points[i - 1], points[i]);
                var end = points[^1];
                var d = end - points[^2];
                var length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                if (length < 0.001)
                    return;
                var dir = new Vector(d.X / length, d.Y / length);
                var side = new Vector(-dir.Y, dir.X);
                Polygon(end + dir * u * 0.35, end - dir * u * 0.3 + side * u * 0.4, end - dir * u * 0.3 - side * u * 0.4);
            }
            switch (_kind)
            {
                case Glyph.Play:
                    Polygon(P(-0.8, -1.2), P(1.3, 0), P(-0.8, 1.2));
                    break;
                case Glyph.Pause:
                    Rect(-1, -1.1, 0.7, 2.2);
                    Rect(0.3, -1.1, 0.7, 2.2);
                    break;
                case Glyph.Previous:
                    Rect(-1.2, -1, 0.35, 2);
                    Polygon(P(1.1, -1), P(1.1, 1), P(-0.75, 0));
                    break;
                case Glyph.Next:
                    Rect(0.85, -1, 0.35, 2);
                    Polygon(P(-1.1, -1), P(-1.1, 1), P(0.75, 0));
                    break;
                case Glyph.Shuffle:
                    Arrow(P(-1.3, 0.8), P(-0.4, 0.8), P(0.5, -0.8), P(1.3, -0.8));
                    Arrow(P(-1.3, -0.8), P(-0.4, -0.8), P(0.5, 0.8), P(1.3, 0.8));
                    break;
                case Glyph.Repeat or Glyph.RepeatOne:
                    Arrow(P(-1.3, 0.2), P(-1.3, -0.8), P(1.3, -0.8));
                    Arrow(P(1.3, -0.2), P(1.3, 0.8), P(-1.3, 0.8));
                    if (_kind == Glyph.RepeatOne)
                    {
                        var text = new FormattedText("1", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, u * 1.3, brush);
                        context.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2));
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// The loudness of the track: a row of segments lit from the left (accent, warm near the top, red at the top), the
    /// peak held a moment, and a trace of the last seconds behind it (sampled 30 times a second while shown).
    /// </summary>
    private sealed class LevelMeter : Control
    {
        private const int Segments = 32;
        private const int History = 96;
        private readonly MusicPlayerViewModel _vm;
        private readonly float[] _trace = new float[History];
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
        private int _head;
        private double _level;
        private double _peak;
        private DateTime _peakTime;

        public LevelMeter(MusicPlayerViewModel vm)
        {
            _vm = vm;
            _timer.Tick += (_, _) => Sample();
            AttachedToVisualTree += (_, _) => _timer.Start();
            DetachedFromVisualTree += (_, _) => _timer.Stop();
        }

        private void Sample()
        {
            _level = Math.Clamp(_vm.IsPlaying ? _vm.Level : 0, 0, 1);
            var now = DateTime.UtcNow;
            if (_level >= _peak || now - _peakTime > TimeSpan.FromSeconds(0.8))
            {
                _peak = _level;
                _peakTime = now;
            }
            _trace[_head] = (float)_level;
            _head = (_head + 1) % History;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            context.DrawRectangle(Ui.Field, new Pen(Ui.Edge, 1), new Rect(size));

            // Trace of the last seconds.
            var traceHeight = size.Height - 14;
            var pen = new Pen(Ui.Edge, 1);
            Point At(int i) => new(2 + i * (size.Width - 4) / (History - 1), 2 + traceHeight * (1 - _trace[(_head + i) % History]));
            for (var i = 1; i < History; i++)
                context.DrawLine(pen, At(i - 1), At(i));

            // Segments.
            const double gap = 2;
            var width = (size.Width - 4 - gap * (Segments - 1)) / Segments;
            var top = size.Height - 10;
            var lit = (int)Math.Round(_level * Segments);
            var peak = Math.Clamp((int)Math.Round(_peak * Segments) - 1, 0, Segments - 1);
            for (var i = 0; i < Segments; i++)
            {
                var color = i >= Segments - 3 ? Ui.Red : i >= Segments * 3 / 4 ? Ui.Warm : Ui.Accent;
                var on = i < lit || (i == peak && _peak > 0.02);
                context.FillRectangle(on ? color : Ui.Edge, new Rect(2 + i * (width + gap), top, Math.Max(1, width), 8));
            }
        }
    }
}
