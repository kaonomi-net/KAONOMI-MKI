using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace KAONOMI.Chat.Views;

/// <summary>A curve of the graph: its function of x (NaN where it is not defined), its label and its color.</summary>
public sealed record GraphFunction(string Label, Func<double, double> Evaluate, Color Color);

/// <summary>
/// Graph of the calculator: the curves of <see cref="Functions"/> over a grid with graduated axes. The mouse wheel
/// zooms around the pointer (Shift: x only, Alt / Option: y only), a drag moves the view in x and y, a double-click goes
/// back to the default view; the coordinates under the pointer and the value of each curve there are shown.
/// </summary>
public sealed class GraphView : Control
{
    /// <summary>Curves drawn.</summary>
    public static readonly StyledProperty<IReadOnlyList<GraphFunction>?> FunctionsProperty =
        AvaloniaProperty.Register<GraphView, IReadOnlyList<GraphFunction>?>(nameof(Functions));

    private static readonly Typeface LabelFace = new("Inter, Segoe UI, Helvetica, Arial");
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IPen MinorGrid = new Pen(new SolidColorBrush(Color.Parse("#F0F2F5")), 1);
    private static readonly IPen MajorGrid = new Pen(new SolidColorBrush(Color.Parse("#DDE1E7")), 1);
    private static readonly IPen Axis = new Pen(new SolidColorBrush(Color.Parse("#5B6170")), 1.4);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush CursorBrush = new SolidColorBrush(Color.Parse("#16181D"));
    private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.Parse("#9CA3AF")), 1, new DashStyle([4, 4], 0));

    // Visible range (default: -10…10 in x, y kept square with x at the first layout).
    private double _xMin = -10, _xMax = 10, _yMin = -6, _yMax = 6;
    private bool _rangeSet;

    private Point? _dragStart;
    private (double XMin, double XMax, double YMin, double YMax) _dragRange;
    private Point? _pointer;

    static GraphView()
    {
        AffectsRender<GraphView>(FunctionsProperty);
        ClipToBoundsProperty.OverrideDefaultValue<GraphView>(true);
        FocusableProperty.OverrideDefaultValue<GraphView>(true);
    }

    /// <inheritdoc cref="FunctionsProperty" />
    public IReadOnlyList<GraphFunction>? Functions
    {
        get => GetValue(FunctionsProperty);
        set => SetValue(FunctionsProperty, value);
    }

    /// <summary>Visible range, for the labels of the window: "x −10 … 10 · y −6 … 6".</summary>
    public string RangeText => $"x {Number(_xMin)} … {Number(_xMax)}   ·   y {Number(_yMin)} … {Number(_yMax)}";

    /// <summary>Raised when the visible range changes (zoom, move, reset).</summary>
    public event Action? RangeChanged;

    /// <summary>
    /// The graph as a picture at the pixel density of the screen (<paramref name="scaling"/>), without the pointer
    /// cross, with a white border and the functions written under it.
    /// </summary>
    public Avalonia.Media.Imaging.RenderTargetBitmap Snapshot(double scaling)
    {
        var pointer = _pointer;
        _pointer = null;
        try
        {
            var size = new PixelSize(Math.Max(1, (int)(Bounds.Width * scaling)), Math.Max(1, (int)(Bounds.Height * scaling)));
            var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(this);
            return bitmap;
        }
        finally
        {
            _pointer = pointer;
        }
    }

    /// <summary>Back to x from -10 to 10, y with the same scale, centered on 0.</summary>
    public void ResetView()
    {
        _xMin = -10;
        _xMax = 10;
        FitY();
        Changed();
    }

    /// <summary>Zooms in (factor &lt; 1) or out (factor &gt; 1) around the center, in x and / or y.</summary>
    public void Zoom(double factor, bool x = true, bool y = true) =>
        ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), factor, x, y);

    /// <summary>Moves the view by a share of its size (0.25 = a quarter of the width or height).</summary>
    public void Pan(double dx, double dy)
    {
        var w = (_xMax - _xMin) * dx;
        var h = (_yMax - _yMin) * dy;
        _xMin += w;
        _xMax += w;
        _yMin += h;
        _yMax += h;
        Changed();
    }

    private void FitY()
    {
        var aspect = Bounds.Width > 0 ? Bounds.Height / Bounds.Width : 0.6;
        var half = (_xMax - _xMin) * aspect / 2;
        _yMin = -half;
        _yMax = half;
    }

    private void Changed()
    {
        InvalidateVisual();
        RangeChanged?.Invoke();
    }

    private double ToScreenX(double x) => (x - _xMin) / (_xMax - _xMin) * Bounds.Width;
    private double ToScreenY(double y) => Bounds.Height - (y - _yMin) / (_yMax - _yMin) * Bounds.Height;
    private double ToX(double sx) => _xMin + sx / Bounds.Width * (_xMax - _xMin);
    private double ToY(double sy) => _yMin + (Bounds.Height - sy) / Bounds.Height * (_yMax - _yMin);

    private void ZoomAt(Point at, double factor, bool zoomX, bool zoomY)
    {
        var x = ToX(at.X);
        var y = ToY(at.Y);
        if (zoomX && (_xMax - _xMin) * factor is > 1e-9 and < 1e9)
        {
            _xMin = x - (x - _xMin) * factor;
            _xMax = x + (_xMax - x) * factor;
        }
        if (zoomY && (_yMax - _yMin) * factor is > 1e-9 and < 1e9)
        {
            _yMin = y - (y - _yMin) * factor;
            _yMax = y + (_yMax - y) * factor;
        }
        Changed();
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_rangeSet && e.NewSize.Width > 0)
        {
            _rangeSet = true;
            FitY();
            Changed();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var factor = Math.Pow(0.88, e.Delta.Y);
        var onlyX = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var onlyY = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        ZoomAt(e.GetPosition(this), factor, !onlyY, !onlyX);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.ClickCount == 2)
        {
            ResetView();
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragRange = (_xMin, _xMax, _yMin, _yMax);
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _pointer = position;
        if (_dragStart is { } start && Bounds.Width > 0 && Bounds.Height > 0)
        {
            var dx = (position.X - start.X) / Bounds.Width * (_dragRange.XMax - _dragRange.XMin);
            var dy = (position.Y - start.Y) / Bounds.Height * (_dragRange.YMax - _dragRange.YMin);
            _xMin = _dragRange.XMin - dx;
            _xMax = _dragRange.XMax - dx;
            _yMin = _dragRange.YMin + dy;
            _yMax = _dragRange.YMax + dy;
            Changed();
            return;
        }
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: Pan(-0.1, 0); break;
            case Key.Right: Pan(0.1, 0); break;
            case Key.Up: Pan(0, 0.1); break;
            case Key.Down: Pan(0, -0.1); break;
            case Key.OemPlus or Key.Add: Zoom(0.8); break;
            case Key.OemMinus or Key.Subtract: Zoom(1.25); break;
            case Key.D0 or Key.NumPad0: ResetView(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;
        context.FillRectangle(Background, new Rect(Bounds.Size));

        var stepX = NiceStep((_xMax - _xMin) / Math.Max(2, width / 90));
        var stepY = NiceStep((_yMax - _yMin) / Math.Max(2, height / 70));
        DrawGrid(context, stepX / 5, stepY / 5, MinorGrid);
        DrawGrid(context, stepX, stepY, MajorGrid);

        // Axes (kept at the edge when 0 is out of view), with their graduations.
        var axisY = Math.Clamp(ToScreenY(0), 0, height - 1);
        var axisX = Math.Clamp(ToScreenX(0), 0, width - 1);
        context.DrawLine(Axis, new Point(0, axisY), new Point(width, axisY));
        context.DrawLine(Axis, new Point(axisX, 0), new Point(axisX, height));
        for (var x = Math.Ceiling(_xMin / stepX) * stepX; x <= _xMax; x += stepX)
        {
            if (Math.Abs(x) < stepX / 1000)
                continue;
            var sx = ToScreenX(x);
            context.DrawLine(Axis, new Point(sx, axisY - 3), new Point(sx, axisY + 3));
            var label = Text(Number(x), 11, LabelBrush);
            var ly = axisY + 5 + label.Height > height ? axisY - 5 - label.Height : axisY + 5;
            context.DrawText(label, new Point(sx - label.Width / 2, ly));
        }
        for (var y = Math.Ceiling(_yMin / stepY) * stepY; y <= _yMax; y += stepY)
        {
            if (Math.Abs(y) < stepY / 1000)
                continue;
            var sy = ToScreenY(y);
            context.DrawLine(Axis, new Point(axisX - 3, sy), new Point(axisX + 3, sy));
            var label = Text(Number(y), 11, LabelBrush);
            var lx = axisX - 6 - label.Width < 0 ? axisX + 6 : axisX - 6 - label.Width;
            context.DrawText(label, new Point(lx, sy - label.Height / 2));
        }
        var origin = Text("0", 11, LabelBrush);
        context.DrawText(origin, new Point(axisX - origin.Width - 5, axisY + 4));

        // Curves: one sample per pixel; a jump across the whole height (tan, 1/x) is not joined.
        foreach (var function in Functions ?? [])
            DrawCurve(context, function, width, height);

        DrawPointer(context, width, height);
    }

    private void DrawGrid(DrawingContext context, double stepX, double stepY, IPen pen)
    {
        if (stepX <= 0 || stepY <= 0)
            return;
        for (var x = Math.Ceiling(_xMin / stepX) * stepX; x <= _xMax; x += stepX)
        {
            var sx = Math.Round(ToScreenX(x)) + 0.5;
            context.DrawLine(pen, new Point(sx, 0), new Point(sx, Bounds.Height));
        }
        for (var y = Math.Ceiling(_yMin / stepY) * stepY; y <= _yMax; y += stepY)
        {
            var sy = Math.Round(ToScreenY(y)) + 0.5;
            context.DrawLine(pen, new Point(0, sy), new Point(Bounds.Width, sy));
        }
    }

    private void DrawCurve(DrawingContext context, GraphFunction function, double width, double height)
    {
        var pen = new Pen(new SolidColorBrush(function.Color), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            var open = false;
            double previousY = 0;
            var samples = (int)Math.Ceiling(width);
            for (var i = 0; i <= samples; i++)
            {
                var sx = i * width / samples;
                var y = function.Evaluate(ToX(sx));
                if (!double.IsFinite(y))
                {
                    if (open)
                        path.EndFigure(false);
                    open = false;
                    continue;
                }
                var sy = Math.Clamp(ToScreenY(y), -height * 4, height * 5);
                if (open && Math.Abs(sy - previousY) > height * 1.5)
                {
                    path.EndFigure(false);
                    open = false;
                }
                if (!open)
                {
                    path.BeginFigure(new Point(sx, sy), false);
                    open = true;
                }
                else
                {
                    path.LineTo(new Point(sx, sy));
                }
                previousY = sy;
            }
            if (open)
                path.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>Under the pointer: a dashed cross, its coordinates, and the point and value of each curve at that x.</summary>
    private void DrawPointer(DrawingContext context, double width, double height)
    {
        if (_pointer is not { } pointer || _dragStart is not null)
            return;
        context.DrawLine(CursorPen, new Point(pointer.X, 0), new Point(pointer.X, height));
        context.DrawLine(CursorPen, new Point(0, pointer.Y), new Point(width, pointer.Y));
        var x = ToX(pointer.X);
        var lines = new List<(string Text, IBrush Brush)> { ($"x = {Number(x, 5)}   y = {Number(ToY(pointer.Y), 5)}", CursorBrush) };
        foreach (var function in Functions ?? [])
        {
            var y = function.Evaluate(x);
            var brush = new SolidColorBrush(function.Color);
            lines.Add(($"{function.Label} = {(double.IsFinite(y) ? Number(y, 6) : "undefined")}", brush));
            if (double.IsFinite(y) && ToScreenY(y) is var sy and >= 0 && sy <= height)
                context.DrawEllipse(brush, new Pen(Brushes.White, 1.5), new Point(pointer.X, sy), 4.5, 4.5);
        }

        var texts = lines.ConvertAll(l => Text(l.Text, 12, l.Brush));
        var boxWidth = 16 + MaxWidth(texts);
        var boxHeight = 10 + texts.Count * 17;
        var bx = pointer.X + 14 + boxWidth > width ? pointer.X - 14 - boxWidth : pointer.X + 14;
        var by = Math.Clamp(pointer.Y + 14, 0, Math.Max(0, height - boxHeight));
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#F2FFFFFF")), new Pen(new SolidColorBrush(Color.Parse("#DDE1E7")), 1),
            new Rect(bx, by, boxWidth, boxHeight), 6, 6);
        for (var i = 0; i < texts.Count; i++)
            context.DrawText(texts[i], new Point(bx + 8, by + 5 + i * 17));
    }

    private static double MaxWidth(List<FormattedText> texts)
    {
        var max = 0.0;
        foreach (var text in texts)
            max = Math.Max(max, text.Width);
        return max;
    }

    /// <summary>1, 2 or 5 × a power of ten, the closest above <paramref name="rough"/>.</summary>
    private static double NiceStep(double rough)
    {
        if (!(rough > 0) || !double.IsFinite(rough))
            return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var fraction = rough / power;
        return (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * power;
    }

    private static string Number(double value, int digits = 4)
    {
        if (Math.Abs(value) < 1e-12)
            return "0";
        var abs = Math.Abs(value);
        return abs is >= 1e6 or < 1e-4
            ? value.ToString("0.##E+0", CultureInfo.InvariantCulture)
            : Math.Round(value, digits).ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, size, brush);
}
