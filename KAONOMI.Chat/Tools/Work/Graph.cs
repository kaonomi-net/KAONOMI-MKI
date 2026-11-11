using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using KAONOMI.Chat.ViewModels;
using KAONOMI.Chat.Views;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Work;

/// <summary>
/// The graph of /work: the curves of the functions over a grid with graduated axes. The wheel zooms around the pointer
/// (Shift: x only, Alt / Option: y only), a drag moves the view, a double-click goes back to the default view, the
/// arrows move, + / − zoom, 0 resets; under the pointer, a dashed cross, its coordinates and the value of each curve.
/// <see cref="Light"/> draws it on white (the picture of 📷 Camera, for the tape and Word).
/// </summary>
public sealed class GraphCanvas : Control
{
    private double _xMin = -10, _xMax = 10, _yMin = -6, _yMax = 6;
    private bool _rangeSet;
    private Point? _dragStart;
    private (double XMin, double XMax, double YMin, double YMax) _dragRange;
    private Point? _pointer;
    private IReadOnlyList<GraphFunction> _functions = [];

    private static readonly Typeface LabelFace = new(FontFamily.Default);
    private static readonly Typeface MonoFace = new(Ui.Mono);

    public GraphCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Drawn on white with dark lines (the picture of the graph) instead of the dark look.</summary>
    public bool Light { get; set; }

    public IReadOnlyList<GraphFunction> Functions
    {
        get => _functions;
        set
        {
            _functions = value ?? [];
            InvalidateVisual();
        }
    }

    /// <summary>Visible range: "x −10 … 10 · y −6 … 6".</summary>
    public string RangeText => $"x {Number(_xMin)} … {Number(_xMax)}   ·   y {Number(_yMin)} … {Number(_yMax)}";

    public event Action? RangeChanged;

    /// <summary>The visible range, to draw a copy of the graph (the picture).</summary>
    public (double XMin, double XMax, double YMin, double YMax) Range
    {
        get => (_xMin, _xMax, _yMin, _yMax);
        set
        {
            (_xMin, _xMax, _yMin, _yMax) = value;
            _rangeSet = true;
            InvalidateVisual();
        }
    }

    private double W => Bounds.Width;
    private double H => Bounds.Height;

    public void ResetView()
    {
        _xMin = -10;
        _xMax = 10;
        FitY();
        Changed();
    }

    public void Zoom(double factor, bool x = true, bool y = true) => ZoomAt(new Point(W / 2, H / 2), factor, x, y);

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
        var aspect = W > 0 ? H / W : 0.6;
        var half = (_xMax - _xMin) * aspect / 2;
        _yMin = -half;
        _yMax = half;
    }

    private void Changed()
    {
        InvalidateVisual();
        RangeChanged?.Invoke();
    }

    private double ToScreenX(double x) => (x - _xMin) / (_xMax - _xMin) * W;
    private double ToScreenY(double y) => H - (y - _yMin) / (_yMax - _yMin) * H;
    private double ToX(double sx) => _xMin + sx / W * (_xMax - _xMin);
    private double ToY(double sy) => _yMin + (H - sy) / H * (_yMax - _yMin);

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

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (!_rangeSet && finalSize.Width > 0)
        {
            _rangeSet = true;
            WorkUi.Later(() =>
            {
                FitY();
                Changed();
            });
        }
        return size;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var delta = Math.Abs(e.Delta.Y) > 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0)
            return;
        var steps = Math.Sign(delta) * Math.Max(0.25, Math.Abs(delta));
        ZoomAt(e.GetPosition(this), Math.Pow(0.88, steps), !e.KeyModifiers.HasFlag(KeyModifiers.Alt), !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        Focus();
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            ResetView();
            return;
        }
        _dragStart = point.Position;
        _dragRange = (_xMin, _xMax, _yMin, _yMax);
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is null)
            return;
        _dragStart = null;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _pointer = position;
        if (_dragStart is { } start && W > 0 && H > 0)
        {
            var dx = (position.X - start.X) / W * (_dragRange.XMax - _dragRange.XMin);
            var dy = (position.Y - start.Y) / H * (_dragRange.YMax - _dragRange.YMin);
            _xMin = _dragRange.XMin - dx;
            _xMax = _dragRange.XMax - dx;
            _yMin = _dragRange.YMin + dy;
            _yMax = _dragRange.YMax + dy;
            Changed();
            return;
        }
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        InvalidateVisual();
    }

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

    // Colors of the two looks.
    private IBrush Background => Light ? Brushes.White : WorkPalette.PageBrush;
    private Color MinorGrid => Light ? Color.Parse("#F0F2F5") : Color.FromRgb(19, 25, 30);
    private Color MajorGrid => Light ? Color.Parse("#DDE1E7") : Color.FromRgb(30, 39, 46);
    private IBrush AxisBrush => Light ? WorkPalette.BrushOf(Color.Parse("#5B6170")) : Ui.Muted;
    private IBrush LabelBrush => Light ? WorkPalette.BrushOf(Color.Parse("#6B7280")) : Ui.Muted;
    private IBrush CursorText => Light ? WorkPalette.BrushOf(Color.Parse("#16181D")) : Ui.TextBright;

    private Color CurveColor(GraphFunction function) => Light ? function.Color : WorkPalette.Lighten(function.Color, 0.25);

    private static FormattedText Text(string text, double size, IBrush brush, Typeface? face = null) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? LabelFace, size, brush);

    public override void Render(DrawingContext context)
    {
        var width = W;
        var height = H;
        if (width <= 0 || height <= 0)
            return;
        context.FillRectangle(Background, new Rect(Bounds.Size));

        var stepX = NiceStep((_xMax - _xMin) / Math.Max(2, width / 90));
        var stepY = NiceStep((_yMax - _yMin) / Math.Max(2, height / 70));
        DrawGrid(context, stepX / 5, stepY / 5, new Pen(WorkPalette.BrushOf(MinorGrid), 1));
        DrawGrid(context, stepX, stepY, new Pen(WorkPalette.BrushOf(MajorGrid), 1));

        var axisY = Math.Clamp(ToScreenY(0), 0, height - 1);
        var axisX = Math.Clamp(ToScreenX(0), 0, width - 1);
        var axis = new Pen(AxisBrush, 1.4);
        context.DrawLine(axis, new Point(0, axisY), new Point(width, axisY));
        context.DrawLine(axis, new Point(axisX, 0), new Point(axisX, height));
        const double size = 11;
        for (var x = Math.Ceiling(_xMin / stepX) * stepX; x <= _xMax; x += stepX)
        {
            if (Math.Abs(x) < stepX / 1000)
                continue;
            var sx = ToScreenX(x);
            context.DrawLine(axis, new Point(sx, axisY - 3), new Point(sx, axisY + 3));
            var label = Text(Number(x), size, LabelBrush);
            var ly = axisY + 5 + label.Height > height ? axisY - 5 - label.Height : axisY + 5;
            context.DrawText(label, new Point(sx - label.Width / 2, ly));
        }
        for (var y = Math.Ceiling(_yMin / stepY) * stepY; y <= _yMax; y += stepY)
        {
            if (Math.Abs(y) < stepY / 1000)
                continue;
            var sy = ToScreenY(y);
            context.DrawLine(axis, new Point(axisX - 3, sy), new Point(axisX + 3, sy));
            var label = Text(Number(y), size, LabelBrush);
            var lx = axisX - 6 - label.Width < 0 ? axisX + 6 : axisX - 6 - label.Width;
            context.DrawText(label, new Point(lx, sy - label.Height / 2));
        }
        var zero = Text("0", size, LabelBrush);
        context.DrawText(zero, new Point(axisX - zero.Width - 5, axisY + 4));

        foreach (var function in _functions)
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
            context.DrawLine(pen, new Point(sx, 0), new Point(sx, H));
        }
        for (var y = Math.Ceiling(_yMin / stepY) * stepY; y <= _yMax; y += stepY)
        {
            var sy = Math.Round(ToScreenY(y)) + 0.5;
            context.DrawLine(pen, new Point(0, sy), new Point(W, sy));
        }
    }

    /// <summary>One sample per pixel; a jump across the whole height (tan, 1/x) is not joined.</summary>
    private void DrawCurve(DrawingContext context, GraphFunction function, double width, double height)
    {
        var pen = new Pen(WorkPalette.BrushOf(CurveColor(function)), 2.2, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
        var points = new List<Point>();
        double previousY = 0;
        var samples = (int)Math.Ceiling(width);
        void Flush()
        {
            if (points.Count >= 2)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(points[0], false);
                    for (var i = 1; i < points.Count; i++)
                        g.LineTo(points[i]);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, pen, geometry);
            }
            points.Clear();
        }
        for (var i = 0; i <= samples; i++)
        {
            var sx = i * width / samples;
            double y;
            try
            {
                y = function.Evaluate(ToX(sx));
            }
            catch (Exception)
            {
                y = double.NaN;
            }
            if (!double.IsFinite(y))
            {
                Flush();
                continue;
            }
            var sy = Math.Clamp(ToScreenY(y), -height * 4, height * 5);
            if (points.Count > 0 && Math.Abs(sy - previousY) > height * 1.5)
                Flush();
            points.Add(new Point(sx, sy));
            previousY = sy;
        }
        Flush();
    }

    private void DrawPointer(DrawingContext context, double width, double height)
    {
        if (_pointer is not { } pointer || _dragStart is not null || Light)
            return;
        var dash = new Pen(Ui.Muted, 1) { DashStyle = new DashStyle([4, 4], 0) };
        context.DrawLine(dash, new Point(pointer.X, 0), new Point(pointer.X, height));
        context.DrawLine(dash, new Point(0, pointer.Y), new Point(width, pointer.Y));
        var x = ToX(pointer.X);
        var lines = new List<(string Text, IBrush Brush)> { ($"x = {Number(x, 5)}   y = {Number(ToY(pointer.Y), 5)}", CursorText) };
        foreach (var function in _functions)
        {
            double y;
            try
            {
                y = function.Evaluate(x);
            }
            catch (Exception)
            {
                y = double.NaN;
            }
            var brush = WorkPalette.BrushOf(CurveColor(function));
            lines.Add(($"{function.Label} = {(double.IsFinite(y) ? Number(y, 6) : "undefined")}", brush));
            if (double.IsFinite(y) && ToScreenY(y) is var sy and >= 0 && sy <= height)
            {
                context.DrawEllipse(Brushes.White, null, new Point(pointer.X, sy), 5.5, 5.5);
                context.DrawEllipse(brush, null, new Point(pointer.X, sy), 4.5, 4.5);
            }
        }
        const double size = 12;
        var texts = new List<FormattedText>();
        double boxWidth = 0;
        foreach (var line in lines)
        {
            var text = Text(line.Text, size, line.Brush);
            texts.Add(text);
            boxWidth = Math.Max(boxWidth, text.Width);
        }
        boxWidth += 16;
        var boxHeight = 10 + lines.Count * 17;
        var bx = pointer.X + 14 + boxWidth > width ? pointer.X - 14 - boxWidth : pointer.X + 14;
        var by = Math.Clamp(pointer.Y + 14, 0, Math.Max(0, height - boxHeight));
        context.DrawRectangle(Ui.Panel, new Pen(Ui.Edge, 1), new Rect(bx, by, boxWidth, boxHeight));
        for (var i = 0; i < texts.Count; i++)
            context.DrawText(texts[i], new Point(bx + 8, by + 5 + i * 17));
    }

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

    /// <summary>
    /// The graph as a PNG at <paramref name="path"/>: drawn again on white at the density of the screen, without the
    /// pointer cross.
    /// </summary>
    public void Snapshot(string path)
    {
        var scale = Math.Max(1, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        var size = new Size(Math.Max(1, W), Math.Max(1, H));
        var copy = new GraphCanvas { Light = true, Functions = _functions };
        copy.Range = Range;
        copy.Measure(size);
        copy.Arrange(new Rect(size));
        var pixels = new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(copy);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}

/// <summary>
/// The window of the graph of /work (Insert › Graph): the functions of x typed above (separated by ;), their errors,
/// the curves with their legend, zoom and move buttons, and 📷 Camera, which saves a picture of the graph
/// (calculator.graphDirectory of config.yml) and adds it to the tape.
/// </summary>
public static class GraphPanel
{
    public static Control Build(CalculatorViewModel vm)
    {
        var canvas = new GraphCanvas();
        var range = Ui.Label("", Ui.Muted, 13);
        canvas.RangeChanged += () => range.Text = canvas.RangeText;
        var header = Ui.Column(0, Ui.Caption("Graph", Ui.Warm, 11), range);

        var input = Ui.Edit(vm, nameof(vm.GraphInput), "sin(x); x^2/4 - 2; 1/x   (several functions separated by ;)");
        input.FontFamily = Ui.Mono;
        input.FontSize = 14;
        ToolTip.SetTip(input, "Functions of x, separated by ; (y = and f(x) = are optional). Everything of the calculator works: + − × ÷ ^ √ ! %, sin cos tan (radians), ln log exp abs, π e, implicit multiplication (2x, 3sin(x))");
        var y = Ui.Label("y =", Ui.Muted, 14);
        y.FontFamily = Ui.Mono;
        y.Margin = new Thickness(0, 0, 6, 0);
        var inputRow = Ui.Dock((y, Avalonia.Controls.Dock.Left), (input, Avalonia.Controls.Dock.Left));
        var error = Ui.Bound(vm, nameof(vm.GraphError), Ui.Red, 12, wrap: true).Visible(vm, nameof(vm.HasGraphError));

        // The curves with their legend at the top left.
        var legend = Ui.Column(2);
        var legendPanel = new Border
        {
            Child = legend,
            Background = Ui.Panel,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        var area = new Panel { MinHeight = 240, ClipToBounds = true, Children = { canvas, legendPanel } };
        Ui.When(area, vm, () =>
        {
            canvas.Functions = vm.GraphFunctions;
            legend.Children.Clear();
            foreach (var function in vm.GraphFunctions)
            {
                var swatch = new Border { Width = 14, Height = 3, Background = WorkPalette.BrushOf(WorkPalette.Lighten(function.Color, 0.25)), VerticalAlignment = VerticalAlignment.Center };
                var label = Ui.Label(function.Label, Ui.TextBright, 12);
                label.FontFamily = Ui.Mono;
                legend.Children.Add(Ui.Row(6, swatch, label));
            }
            legendPanel.IsVisible = vm.GraphFunctions.Count > 0;
        }, nameof(vm.GraphFunctions));

        var status = Ui.Label("", Ui.Green, 11);
        var version = 0;
        void Camera()
        {
            try
            {
                var directory = vm.GraphPicturesDirectory;
                System.IO.Directory.CreateDirectory(directory);
                var path = System.IO.Path.Combine(directory, $"graph-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                canvas.Snapshot(path);
                vm.AddGraphToTape(path, canvas.RangeText);
                status.Foreground = Ui.Green;
                status.Text = "✓ Added to the tape";
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                status.Foreground = Ui.Red;
                status.Text = $"Not saved: {ex.Message}";
            }
            var shown = ++version;
            WorkUi.After(3, () =>
            {
                if (shown == version)
                    status.Text = string.Empty;
            });
        }
        var camera = Ui.Button("📷 CAMERA", Camera,
            "Take a picture of the graph (PNG in calculator.graphDirectory of config.yml: Pictures/kaonomi/maths by default) and add it to the tape", primary: true);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, ItemSpacing = 4, LineSpacing = 4 };
        foreach (var (text, tip, action) in new (string, string, Action)[]
                 {
                     ("＋ ZOOM", "Zoom in (wheel, + key)", () => canvas.Zoom(0.8)),
                     ("－ ZOOM", "Zoom out (wheel, − key)", () => canvas.Zoom(1.25)),
                     ("X ＋", "Zoom in x only (Shift + wheel)", () => canvas.Zoom(0.8, y: false)),
                     ("X －", "Zoom out x only (Shift + wheel)", () => canvas.Zoom(1.25, y: false)),
                     ("Y ＋", "Zoom in y only (Alt / Option + wheel)", () => canvas.Zoom(0.8, x: false)),
                     ("Y －", "Zoom out y only (Alt / Option + wheel)", () => canvas.Zoom(1.25, x: false)),
                     ("←", "Move left (drag, ← key)", () => canvas.Pan(-0.15, 0)),
                     ("→", "Move right (drag, → key)", () => canvas.Pan(0.15, 0)),
                     ("↑", "Move up (drag, ↑ key)", () => canvas.Pan(0, 0.15)),
                     ("↓", "Move down (drag, ↓ key)", () => canvas.Pan(0, -0.15)),
                     ("RESET", "Back to -10…10 (double-click, 0 key)", canvas.ResetView)
                 })
            buttons.Children.Add(Ui.Button(text, action, tip));
        buttons.Children.Add(camera);
        status.VerticalAlignment = VerticalAlignment.Center;
        buttons.Children.Add(status);

        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(14) };
        Control[] rows = [header, inputRow, error, area, buttons];
        for (var i = 0; i < rows.Length; i++)
        {
            Grid.SetRow(rows[i], i);
            page.Children.Add(rows[i]);
        }
        page.AttachedToVisualTree += (_, _) => WorkUi.Later(() => input.Focus());
        return page;
    }
}
