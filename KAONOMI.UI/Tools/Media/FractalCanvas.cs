using System;
using System.ComponentModel;
using System.Globalization;
using Complex = System.Numerics.Complex;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// The picture of /fractal, computed on the CPU (<see cref="FractalCpu"/>) into a bitmap: the escape-time fractals
/// pixel by pixel in parallel, the L-systems as antialiased lines, the IFS as the density of the points of the chaos
/// game. While the view moves (drag, wheel, keys) a picture at a reduced resolution is drawn at once, the full one when
/// it stops. ⇧ + drag (or a drag in <see cref="BoxMode"/>): a square drawn on the picture, the view zooms on it; wheel:
/// zoom where the pointer is; drag: move; double click: zoom in (Shift: out); right click on an escape-time fractal: its
/// Julia set at that point; R: reset the view; + - and the arrows too. A change of the colours only colours the kept
/// values again; "Cycle the colours" turns the palette over time (escape time).
/// </summary>
public sealed class FractalCanvas : Control
{
    private const double Wheel = 1.2;

    /// <summary>The share of the resolution of the quick pictures drawn while the view moves.</summary>
    private const double PreviewScale = 0.35;

    /// <summary>The smallest square, in pixels, that zooms (a smaller drag is a click).</summary>
    private const double MinBox = 8;

    private readonly FractalWindowViewModel _vm;
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _cycle = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DateTime _start = DateTime.UtcNow;

    private WriteableBitmap? _bitmap;
    private CancellationTokenSource? _render;
    private FractalCpu.EscapeValues? _values;
    private FractalLSystem.Result? _lines;
    private FractalIfs.Result? _cloud;
    private int _generation;
    private bool _pending;
    private bool _pendingPreview;
    private bool _dirtyLSystem = true, _dirtyIfs = true;
    private bool _colouring;
    private Point? _dragFrom;
    private Point? _boxFrom;
    private Point _boxTo;
    private bool _boxMode;
    private Size _lastSize;

    /// <summary>The point of the plane under the pointer (null: the pointer is out), for the coordinates shown.</summary>
    public event Action<Point?>? PointerMovedOnPlane;

    public FractalCanvas(FractalWindowViewModel vm)
    {
        _vm = vm;
        Focusable = true;
        ClipToBounds = true;
        MinWidth = 320;
        MinHeight = 240;
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Schedule(preview: false);
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Schedule(preview: false);
        };
        _cycle.Tick += (_, _) => Recolour();
        // Pinch on a touchpad: zoom where the pointer is.
        AddHandler(PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            var at = ToPlane(e.GetPosition(this));
            _vm.ZoomAt(at.X, at.Y, Math.Max(0.2, 1 + e.Delta.X));
            e.Handled = true;
        });
        AttachedToVisualTree += (_, _) =>
        {
            _vm.PropertyChanged += OnChanged;
            _vm.Loaded += OnLoaded;
            UpdateCycle();
            Schedule(preview: false);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _vm.PropertyChanged -= OnChanged;
            _vm.Loaded -= OnLoaded;
            _cycle.Stop();
            _settle.Stop();
            _debounce.Stop();
            _render?.Cancel();
        };
    }

    /// <summary>True: a drag draws a square to zoom on (false: it moves the view; with ⇧ it draws a square anyway).</summary>
    public bool BoxMode
    {
        get => _boxMode;
        set
        {
            _boxMode = value;
            Cursor = new Cursor(value ? StandardCursorType.Cross : StandardCursorType.Arrow);
        }
    }

    private void OnLoaded() => Ui.OnMain(() =>
    {
        _dirtyLSystem = _dirtyIfs = true;
        _values = null;
        Schedule(preview: false);
    });

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Ui.OnMain(() =>
    {
        switch (e.PropertyName)
        {
            case nameof(FractalWindowViewModel.Family):
                _dirtyLSystem = _dirtyIfs = true;
                UpdateCycle();
                Schedule(preview: false);
                break;
            case nameof(FractalWindowViewModel.Glsl):
                // Drawn once the typing pauses, not at each key.
                Debounce();
                break;
            case nameof(FractalWindowViewModel.CenterX) or nameof(FractalWindowViewModel.CenterY) or nameof(FractalWindowViewModel.Scale):
                Moved();
                break;
            case nameof(FractalWindowViewModel.Palette) or nameof(FractalWindowViewModel.Density) or nameof(FractalWindowViewModel.Offset):
                if (_vm.Family == FractalFamily.EscapeTime)
                    Recolour();
                else
                    Schedule(preview: false);
                break;
            case nameof(FractalWindowViewModel.Cycle):
                UpdateCycle();
                Recolour();
                break;
            case nameof(FractalWindowViewModel.Axiom) or nameof(FractalWindowViewModel.Rules) or nameof(FractalWindowViewModel.Angle)
                or nameof(FractalWindowViewModel.Depth) or nameof(FractalWindowViewModel.Heading) or nameof(FractalWindowViewModel.DrawSymbols):
                _dirtyLSystem = true;
                Debounce();
                break;
            case nameof(FractalWindowViewModel.Maps) or nameof(FractalWindowViewModel.Points):
                _dirtyIfs = true;
                Debounce();
                break;
            case nameof(FractalWindowViewModel.Status) or nameof(FractalWindowViewModel.IsError) or nameof(FractalWindowViewModel.FormulaError)
                or nameof(FractalWindowViewModel.Name) or nameof(FractalWindowViewModel.Description) or nameof(FractalWindowViewModel.Selected)
                or nameof(FractalWindowViewModel.IsOwn) or nameof(FractalWindowViewModel.Items) or nameof(FractalWindowViewModel.Formula)
                or nameof(FractalWindowViewModel.IsEscapeTime):
                break;
            default:
                // A parameter of the escape-time fractals (c, z0, a, b, iterations, bailout, colouring, quality…).
                if (_vm.Family == FractalFamily.EscapeTime)
                    Debounce(150);
                break;
        }
    });

    private void Debounce(int milliseconds = 300)
    {
        _debounce.Stop();
        _debounce.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _debounce.Start();
    }

    /// <summary>The view moved: a quick picture now, the full one once it stops.</summary>
    private void Moved()
    {
        Schedule(preview: true);
        _settle.Stop();
        _settle.Start();
    }

    private void UpdateCycle()
    {
        if (_vm.Cycle && _vm.Family == FractalFamily.EscapeTime)
            _cycle.Start();
        else
            _cycle.Stop();
    }

    /// <summary>Where the palette starts now (it turns over time while "Cycle the colours" is ticked).</summary>
    private double Shift => _vm.Offset + (_vm.Cycle ? (DateTime.UtcNow - _start).TotalSeconds * 0.05 : 0);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Width < 2 || e.NewSize.Height < 2 || e.NewSize == _lastSize)
            return;
        _lastSize = e.NewSize;
        Moved();
    }

    /// <summary>The pixels of the picture at full resolution.</summary>
    private PixelSize FullPixels()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return new PixelSize(Math.Max(2, (int)(Bounds.Width * scaling)), Math.Max(2, (int)(Bounds.Height * scaling)));
    }

    /// <summary>Asks for a picture (once per turn of the interface, the last request wins).</summary>
    private void Schedule(bool preview)
    {
        _pendingPreview = _pending ? _pendingPreview && preview : preview;
        if (_pending)
            return;
        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            _ = RenderAsync(_pendingPreview);
        }, DispatcherPriority.Background);
    }

    /// <summary>Draws the picture now at full resolution (for the PNG); null when it cannot.</summary>
    public async Task<WriteableBitmap?> RenderFullAsync()
    {
        _settle.Stop();
        _debounce.Stop();
        await RenderAsync(false);
        return _bitmap is { } bitmap && bitmap.PixelSize == FullPixels() ? bitmap : null;
    }

    private async Task RenderAsync(bool preview)
    {
        if (Bounds.Width < 2 || Bounds.Height < 2)
            return;
        _render?.Cancel();
        var cancel = new CancellationTokenSource();
        _render = cancel;
        var token = cancel.Token;
        var generation = ++_generation;
        var full = FullPixels();
        var size = preview
            ? new PixelSize(Math.Max(2, (int)(full.Width * PreviewScale)), Math.Max(2, (int)(full.Height * PreviewScale)))
            : full;
        var palette = FractalPalette.Find(_vm.Palette);
        var (cx, cy, scale, density) = (_vm.CenterX, _vm.CenterY, _vm.Scale, _vm.Density);
        byte[]? bytes = null;
        try
        {
            switch (_vm.Family)
            {
                case FractalFamily.EscapeTime:
                {
                    if (FractalCpu.Compile(_vm.Glsl) is not { } next)
                        return;
                    var settings = new FractalCpu.EscapeSettings(next, cx, cy, scale, _vm.Iterations, _vm.Bailout, _vm.Degree, _vm.Julia,
                        new Complex(_vm.Cx, _vm.Cy), new Complex(_vm.Z0x, _vm.Z0y), new Complex(_vm.Ax, _vm.Ay), new Complex(_vm.Bx, _vm.By),
                        _vm.Coloring, preview ? 1 : _vm.Quality);
                    var shift = Shift;
                    var result = await Task.Run(() =>
                    {
                        var values = FractalCpu.Iterate(settings, size.Width, size.Height, token);
                        return values is null ? (null, null) : (values, FractalCpu.Colorize(values, palette, density, shift));
                    }, token);
                    if (result.values is null || generation != _generation)
                        return;
                    _values = result.values;
                    bytes = result.Item2;
                    break;
                }
                case FractalFamily.LSystem:
                {
                    if (_dirtyLSystem || _lines is null)
                    {
                        _dirtyLSystem = false;
                        var (axiom, rules, angle, depth, heading, draw) = (_vm.Axiom, _vm.Rules, _vm.Angle, _vm.Depth, _vm.Heading, _vm.DrawSymbols);
                        _vm.Say("Rewriting the L-system…", false);
                        var lines = await Task.Run(() => FractalLSystem.Draw(axiom, rules, angle, depth, heading, draw), token);
                        if (generation != _generation)
                        {
                            _dirtyLSystem = true;
                            return;
                        }
                        _lines = lines;
                        if (lines.Error is not null && lines.Count == 0)
                            _vm.Say(lines.Error, true);
                        else
                            _vm.Say($"{lines.Count:N0} lines from a string of {lines.Length:N0} letters{(lines.Truncated ? " · cut: too long, lower the depth" : string.Empty)}", false);
                    }
                    var current = _lines;
                    var offset = _vm.Offset;
                    bytes = await Task.Run(() => FractalCpu.RasterLines(current, size.Width, size.Height, cx, cy, scale, palette, density, offset), token);
                    break;
                }
                case FractalFamily.Ifs:
                {
                    if (_dirtyIfs || _cloud is null)
                    {
                        _dirtyIfs = false;
                        var (maps, points) = (_vm.Maps, _vm.Points);
                        _vm.Say("Playing the chaos game…", false);
                        var cloud = await Task.Run(() => FractalIfs.Generate(maps, points), token);
                        if (generation != _generation)
                        {
                            _dirtyIfs = true;
                            return;
                        }
                        if (cloud.Error is not null)
                        {
                            _vm.Say(cloud.Error, true);
                            return;
                        }
                        _cloud = cloud;
                        _vm.Say($"{cloud.Points.Length / 2:N0} points, {cloud.MapCount} map(s)", false);
                    }
                    var current = _cloud;
                    var offset = _vm.Offset;
                    bytes = await Task.Run(() => FractalCpu.RasterIfs(current, size.Width, size.Height, cx, cy, scale, palette, density, offset), token);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (bytes is null || generation != _generation)
            return;
        Show(bytes, size);
    }

    /// <summary>Colours the kept values of the escape-time picture again (palette, density, offset, cycling).</summary>
    private async void Recolour()
    {
        if (_values is not { } values || _vm.Family != FractalFamily.EscapeTime || _colouring)
            return;
        _colouring = true;
        try
        {
            var palette = FractalPalette.Find(_vm.Palette);
            var (density, shift) = (_vm.Density, Shift);
            var generation = _generation;
            var bytes = await Task.Run(() => FractalCpu.Colorize(values, palette, density, shift));
            if (generation == _generation && ReferenceEquals(values, _values))
                Show(bytes, new PixelSize(values.Width, values.Height));
        }
        finally
        {
            _colouring = false;
        }
    }

    private void Show(byte[] bytes, PixelSize size)
    {
        if (_bitmap is null || _bitmap.PixelSize != size)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using (var buffer = _bitmap.Lock())
        {
            var row = size.Width * 4;
            for (var y = 0; y < size.Height; y++)
                Marshal.Copy(bytes, y * row, buffer.Address + y * buffer.RowBytes, row);
        }
        InvalidateVisual();
    }

    // ── Drawing ──

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(FractalCpu.BackR, FractalCpu.BackG, FractalCpu.BackB)), bounds);
        if (_bitmap is not null)
            context.DrawImage(_bitmap, new Rect(_bitmap.Size), bounds);

        // The square being drawn, over the picture: its edge and the zoom it will give.
        if (_boxFrom is null)
            return;
        var box = Box();
        if (box.Width < 2)
            return;
        context.DrawRectangle(null, new Pen(Brushes.Black, 3), box);
        context.DrawRectangle(null, new Pen(Brushes.White, 1.5), box);
        if (box.Width >= MinBox)
        {
            var zoom = Bounds.Height / box.Width;
            var label = $"×{zoom.ToString(zoom < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)}";
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Ui.Mono), 13, Brushes.White);
            var at = new Point(box.X + 4, box.Y - text.Height - 4);
            if (at.Y < 0)
                at = new Point(at.X, box.Bottom + 4);
            context.FillRectangle(Brushes.Black, new Rect(at.X - 3, at.Y - 1, text.Width + 6, text.Height + 2));
            context.DrawText(text, at);
        }
    }

    // ── The pointer and the keys ──

    /// <summary>The point of the plane at <paramref name="local"/> (a position in this control).</summary>
    public Point ToPlane(Point local)
    {
        var half = Math.Max(Bounds.Height / 2, 1);
        return new Point(
            _vm.CenterX + (local.X - Bounds.Width / 2) / half * _vm.Scale,
            _vm.CenterY - (local.Y - Bounds.Height / 2) / half * _vm.Scale);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var at = ToPlane(e.GetPosition(this));
        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 1e-9)
            return;
        _vm.ZoomAt(at.X, at.Y, Math.Pow(Wheel, delta));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var position = e.GetPosition(this);
        Focus();
        if (point.Properties.IsRightButtonPressed)
        {
            if (_vm.IsEscapeTime && !_vm.Julia)
            {
                var at = ToPlane(position);
                _vm.JuliaAt(at.X, at.Y);
            }
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            var at = ToPlane(position);
            _vm.ZoomAt(at.X, at.Y, e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.5 : 2);
            _dragFrom = null;
            _boxFrom = null;
            return;
        }
        e.Pointer.Capture(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || BoxMode)
        {
            // A square to zoom on: drawn from here.
            _dragFrom = null;
            _boxFrom = position;
            _boxTo = position;
            InvalidateVisual();
        }
        else
            _dragFrom = position;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var left = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        if (_boxFrom is not null && left)
        {
            _boxTo = position;
            InvalidateVisual();
        }
        else if (_dragFrom is { } from && left)
        {
            var unit = _vm.Scale / Math.Max(Bounds.Height / 2, 1);
            var moved = position - from;
            _vm.Pan(-moved.X * unit, moved.Y * unit);
            _dragFrom = position;
        }
        PointerMovedOnPlane?.Invoke(ToPlane(position));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        _dragFrom = null;
        if (_boxFrom is not null)
            ZoomToBox();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        PointerMovedOnPlane?.Invoke(null);
    }

    /// <summary>The square drawn from where the drag started to the pointer (its side the larger of the two moves).</summary>
    private Rect Box()
    {
        var from = _boxFrom ?? default;
        var move = _boxTo - from;
        var side = Math.Max(Math.Abs(move.X), Math.Abs(move.Y));
        var end = from + new Vector(move.X < 0 ? -side : side, move.Y < 0 ? -side : side);
        return new Rect(new Point(Math.Min(from.X, end.X), Math.Min(from.Y, end.Y)), new Size(side, side));
    }

    /// <summary>The drag is over: the view zooms on the square (the square fills the height of the view).</summary>
    private void ZoomToBox()
    {
        var box = Box();
        _boxFrom = null;
        InvalidateVisual();
        if (box.Width < MinBox)
            return;
        var center = ToPlane(box.Center);
        var unit = _vm.Scale / Math.Max(Bounds.Height / 2, 1);
        _vm.ZoomToSquare(center.X, center.Y, box.Width / 2 * unit);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && HandleKey(e))
            e.Handled = true;
    }

    private bool HandleKey(KeyEventArgs e)
    {
        var step = _vm.Scale * 0.15;
        switch (e.Key)
        {
            case Key.Escape when _boxFrom is not null:
                _boxFrom = null;
                InvalidateVisual();
                return true;
            case Key.R:
                _vm.ResetViewCommand.Execute(null);
                return true;
            case Key.OemPlus or Key.Add:
                _vm.ZoomInCommand.Execute(null);
                return true;
            case Key.OemMinus or Key.Subtract:
                _vm.ZoomOutCommand.Execute(null);
                return true;
            case Key.Left:
                _vm.Pan(-step, 0);
                return true;
            case Key.Right:
                _vm.Pan(step, 0);
                return true;
            case Key.Up:
                _vm.Pan(0, step);
                return true;
            case Key.Down:
                _vm.Pan(0, -step);
                return true;
        }
        return false;
    }
}
