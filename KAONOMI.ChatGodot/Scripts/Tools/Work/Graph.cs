using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.Chat.Views;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// The graph of /work (port of GraphView): the curves of the functions over a grid with graduated axes. The wheel zooms
/// around the pointer (Shift: x only, Alt / Option: y only), a drag moves the view, a double-click goes back to the
/// default view, the arrows move, + / − zoom, 0 resets; under the pointer, a dashed cross, its coordinates and the value
/// of each curve. <see cref="Light"/> draws it on white (the picture of 📷 Camera, for the tape and Word).
/// </summary>
public partial class GraphCanvas : Control
{
    private double _xMin = -10, _xMax = 10, _yMin = -6, _yMax = 6;
    private bool _rangeSet;
    private Vector2? _dragStart;
    private (double XMin, double XMax, double YMin, double YMax) _dragRange;
    private Vector2? _pointer;
    private IReadOnlyList<GraphFunction> _functions = [];

    public GraphCanvas()
    {
        FocusMode = FocusModeEnum.All;
        ClipContents = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    /// <summary>Drawn on white with dark lines (the picture of the graph) instead of the dark EVE look.</summary>
    public bool Light { get; set; }

    /// <summary>Scale of the lines and texts (the picture is made at the density of the screen).</summary>
    public new float Scale { get; set; } = 1;

    public IReadOnlyList<GraphFunction> Functions
    {
        get => _functions;
        set
        {
            _functions = value ?? [];
            QueueRedraw();
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
            QueueRedraw();
        }
    }

    public void ResetView()
    {
        _xMin = -10;
        _xMax = 10;
        FitY();
        Changed();
    }

    public void Zoom(double factor, bool x = true, bool y = true) => ZoomAt(Size / 2, factor, x, y);

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
        var aspect = Size.X > 0 ? Size.Y / Size.X : 0.6;
        var half = (_xMax - _xMin) * aspect / 2;
        _yMin = -half;
        _yMax = half;
    }

    private void Changed()
    {
        QueueRedraw();
        RangeChanged?.Invoke();
    }

    private double ToScreenX(double x) => (x - _xMin) / (_xMax - _xMin) * Size.X;
    private double ToScreenY(double y) => Size.Y - (y - _yMin) / (_yMax - _yMin) * Size.Y;
    private double ToX(double sx) => _xMin + sx / Size.X * (_xMax - _xMin);
    private double ToY(double sy) => _yMin + (Size.Y - sy) / Size.Y * (_yMax - _yMin);

    private void ZoomAt(Vector2 at, double factor, bool zoomX, bool zoomY)
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

    public override void _Notification(int what)
    {
        if (what == NotificationResized && !_rangeSet && Size.X > 0)
        {
            _rangeSet = true;
            FitY();
            Changed();
        }
        else if (what == NotificationMouseExit)
        {
            _pointer = null;
            QueueRedraw();
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
            {
                var steps = (wheel.ButtonIndex == MouseButton.WheelUp ? 1 : -1) * Math.Max(0.25, wheel.Factor);
                ZoomAt(wheel.Position, Math.Pow(0.88, steps), !wheel.AltPressed, !wheel.ShiftPressed);
                AcceptEvent();
                break;
            }
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press:
                GrabFocus();
                AcceptEvent();
                if (press.DoubleClick)
                {
                    ResetView();
                    return;
                }
                _dragStart = press.Position;
                _dragRange = (_xMin, _xMax, _yMin, _yMax);
                MouseDefaultCursorShape = CursorShape.Move;
                break;
            case InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left }:
                _dragStart = null;
                MouseDefaultCursorShape = CursorShape.Arrow;
                break;
            case InputEventMouseMotion motion:
                _pointer = motion.Position;
                if (_dragStart is { } start && Size.X > 0 && Size.Y > 0)
                {
                    var dx = (motion.Position.X - start.X) / Size.X * (_dragRange.XMax - _dragRange.XMin);
                    var dy = (motion.Position.Y - start.Y) / Size.Y * (_dragRange.YMax - _dragRange.YMin);
                    _xMin = _dragRange.XMin - dx;
                    _xMax = _dragRange.XMax - dx;
                    _yMin = _dragRange.YMin + dy;
                    _yMax = _dragRange.YMax + dy;
                    Changed();
                    return;
                }
                QueueRedraw();
                break;
            case InputEventKey { Pressed: true } key:
                switch (key.Keycode)
                {
                    case Key.Left: Pan(-0.1, 0); break;
                    case Key.Right: Pan(0.1, 0); break;
                    case Key.Up: Pan(0, 0.1); break;
                    case Key.Down: Pan(0, -0.1); break;
                    case Key.Plus or Key.KpAdd or Key.Equal: Zoom(0.8); break;
                    case Key.Minus or Key.KpSubtract: Zoom(1.25); break;
                    case Key.Key0 or Key.Kp0: ResetView(); break;
                    default: return;
                }
                AcceptEvent();
                break;
        }
    }

    // Colors of the two looks.
    private Color Background => Light ? Colors.White : new Color(0.01f, 0.015f, 0.02f, 0.92f);
    private Color MinorGrid => Light ? Color.FromHtml("#F0F2F5") : Eve.Eve.Edge with { A = 0.1f };
    private Color MajorGrid => Light ? Color.FromHtml("#DDE1E7") : Eve.Eve.Edge with { A = 0.28f };
    private Color AxisColor => Light ? Color.FromHtml("#5B6170") : Eve.Eve.EdgeBright;
    private Color LabelColor => Light ? Color.FromHtml("#6B7280") : Eve.Eve.Muted;
    private Color CursorText => Light ? Color.FromHtml("#16181D") : Eve.Eve.TextBright;
    private Color CurveColor(GraphFunction function)
    {
        var color = AvaloniaBridge.ToColor(function.Color);
        return Light ? color : color.Lightened(0.25f);
    }

    private Font LabelFont => Eve.Eve.Body;
    private int FontSize(float size) => (int)Math.Round(size * Scale);

    public override void _Draw()
    {
        var width = Size.X;
        var height = Size.Y;
        if (width <= 0 || height <= 0)
            return;
        DrawRect(new Rect2(Vector2.Zero, Size), Background);

        var stepX = NiceStep((_xMax - _xMin) / Math.Max(2, width / (90 * Scale)));
        var stepY = NiceStep((_yMax - _yMin) / Math.Max(2, height / (70 * Scale)));
        DrawGrid(stepX / 5, stepY / 5, MinorGrid);
        DrawGrid(stepX, stepY, MajorGrid);

        var axisY = (float)Math.Clamp(ToScreenY(0), 0, height - 1);
        var axisX = (float)Math.Clamp(ToScreenX(0), 0, width - 1);
        var axisWidth = 1.4f * Scale;
        DrawLine(new Vector2(0, axisY), new Vector2(width, axisY), AxisColor, axisWidth);
        DrawLine(new Vector2(axisX, 0), new Vector2(axisX, height), AxisColor, axisWidth);
        var size = FontSize(11);
        var font = LabelFont;
        var labelHeight = font.GetHeight(size);
        for (var x = Math.Ceiling(_xMin / stepX) * stepX; x <= _xMax; x += stepX)
        {
            if (Math.Abs(x) < stepX / 1000)
                continue;
            var sx = (float)ToScreenX(x);
            DrawLine(new Vector2(sx, axisY - 3 * Scale), new Vector2(sx, axisY + 3 * Scale), AxisColor, axisWidth);
            var label = Number(x);
            var w = font.GetStringSize(label, HorizontalAlignment.Left, -1, size).X;
            var ly = axisY + 5 * Scale + labelHeight > height ? axisY - 5 * Scale - labelHeight : axisY + 5 * Scale;
            DrawString(font, new Vector2(sx - w / 2, ly + font.GetAscent(size)), label, HorizontalAlignment.Left, -1, size, LabelColor);
        }
        for (var y = Math.Ceiling(_yMin / stepY) * stepY; y <= _yMax; y += stepY)
        {
            if (Math.Abs(y) < stepY / 1000)
                continue;
            var sy = (float)ToScreenY(y);
            DrawLine(new Vector2(axisX - 3 * Scale, sy), new Vector2(axisX + 3 * Scale, sy), AxisColor, axisWidth);
            var label = Number(y);
            var w = font.GetStringSize(label, HorizontalAlignment.Left, -1, size).X;
            var lx = axisX - 6 * Scale - w < 0 ? axisX + 6 * Scale : axisX - 6 * Scale - w;
            DrawString(font, new Vector2(lx, sy - labelHeight / 2 + font.GetAscent(size)), label, HorizontalAlignment.Left, -1, size, LabelColor);
        }
        var zero = font.GetStringSize("0", HorizontalAlignment.Left, -1, size).X;
        DrawString(font, new Vector2(axisX - zero - 5 * Scale, axisY + 4 * Scale + font.GetAscent(size)), "0", HorizontalAlignment.Left, -1, size, LabelColor);

        foreach (var function in _functions)
            DrawCurve(function, width, height);

        DrawPointer(width, height);
    }

    private void DrawGrid(double stepX, double stepY, Color color)
    {
        if (stepX <= 0 || stepY <= 0)
            return;
        for (var x = Math.Ceiling(_xMin / stepX) * stepX; x <= _xMax; x += stepX)
        {
            var sx = (float)Math.Round(ToScreenX(x)) + 0.5f;
            DrawLine(new Vector2(sx, 0), new Vector2(sx, Size.Y), color, Scale);
        }
        for (var y = Math.Ceiling(_yMin / stepY) * stepY; y <= _yMax; y += stepY)
        {
            var sy = (float)Math.Round(ToScreenY(y)) + 0.5f;
            DrawLine(new Vector2(0, sy), new Vector2(Size.X, sy), color, Scale);
        }
    }

    /// <summary>One sample per pixel; a jump across the whole height (tan, 1/x) is not joined.</summary>
    private void DrawCurve(GraphFunction function, float width, float height)
    {
        var color = CurveColor(function);
        var points = new List<Vector2>();
        double previousY = 0;
        var samples = (int)Math.Ceiling(width);
        void Flush()
        {
            if (points.Count >= 2)
                DrawPolyline(points.ToArray(), color, 2.2f * Scale, true);
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
            points.Add(new Vector2(sx, (float)sy));
            previousY = sy;
        }
        Flush();
    }

    private void DrawPointer(float width, float height)
    {
        if (_pointer is not { } pointer || _dragStart is not null || Light)
            return;
        var dash = Eve.Eve.Muted with { A = 0.8f };
        DrawDashedLine(new Vector2(pointer.X, 0), new Vector2(pointer.X, height), dash, 1, 4);
        DrawDashedLine(new Vector2(0, pointer.Y), new Vector2(width, pointer.Y), dash, 1, 4);
        var x = ToX(pointer.X);
        var lines = new List<(string Text, Color Color)> { ($"x = {Number(x, 5)}   y = {Number(ToY(pointer.Y), 5)}", CursorText) };
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
            var color = CurveColor(function);
            lines.Add(($"{function.Label} = {(double.IsFinite(y) ? Number(y, 6) : "undefined")}", color));
            if (double.IsFinite(y) && ToScreenY(y) is var sy and >= 0 && sy <= height)
            {
                DrawCircle(new Vector2(pointer.X, (float)sy), 5.5f, Colors.White);
                DrawCircle(new Vector2(pointer.X, (float)sy), 4.5f, color);
            }
        }
        var font = LabelFont;
        const int size = 12;
        float boxWidth = 0;
        foreach (var line in lines)
            boxWidth = Math.Max(boxWidth, font.GetStringSize(line.Text, HorizontalAlignment.Left, -1, size).X);
        boxWidth += 16;
        var boxHeight = 10 + lines.Count * 17;
        var bx = pointer.X + 14 + boxWidth > width ? pointer.X - 14 - boxWidth : pointer.X + 14;
        var by = Math.Clamp(pointer.Y + 14, 0, Math.Max(0, height - boxHeight));
        var box = Eve.Eve.Box(Eve.Eve.PanelSolid, Eve.Eve.EdgeBright with { A = 0.6f }, 0);
        DrawStyleBox(box, new Rect2(bx, by, boxWidth, boxHeight));
        for (var i = 0; i < lines.Count; i++)
            DrawString(font, new Vector2(bx + 8, by + 5 + i * 17 + font.GetAscent(size)), lines[i].Text, HorizontalAlignment.Left, -1, size, lines[i].Color);
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
    /// The graph as a PNG at <paramref name="path"/>: drawn again on white at the density of the screen in a viewport of
    /// its own, without the pointer cross.
    /// </summary>
    public async Task SnapshotAsync(string path)
    {
        var scale = Math.Max(1f, DisplayServer.ScreenGetScale());
        var size = new Vector2I(Math.Max(1, (int)(Size.X * scale)), Math.Max(1, (int)(Size.Y * scale)));
        var viewport = new SubViewport { Size = size, TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Disable3D = true };
        var copy = new GraphCanvas { Light = true, Scale = scale, Functions = _functions, Size = size };
        copy.Range = Range;
        viewport.AddChild(copy);
        AddChild(viewport);
        try
        {
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
            var image = viewport.GetTexture().GetImage();
            var error = image.SavePng(path);
            if (error != Error.Ok)
                throw new System.IO.IOException($"The picture could not be written ({error}).");
        }
        finally
        {
            viewport.QueueFree();
        }
    }
}

/// <summary>
/// The window of the graph of /work (Insert › Graph, port of GraphPanel): the functions of x typed above (separated by
/// ;), their errors, the curves with their legend, zoom and move buttons, and 📷 Camera, which saves a picture of the
/// graph (calculator.graphDirectory of config.yml) and adds it to the tape.
/// </summary>
internal static class GraphPanel
{
    public static Control Build(CalculatorViewModel vm)
    {
        var canvas = new GraphCanvas();
        Bind.When(canvas, vm, () => canvas.Functions = vm.GraphFunctions, nameof(vm.GraphFunctions));
        var range = Eve.Eve.Text_("", Eve.Eve.Muted, 13);
        canvas.RangeChanged += () => range.Text = canvas.RangeText;
        var header = Ui.Column(0, Eve.Eve.Caption("Graph", Eve.Eve.Amber, 11), range);

        var input = Ui.Line(vm, nameof(vm.GraphInput), "sin(x); x^2/4 - 2; 1/x   (several functions separated by ;)");
        input.AddThemeFontOverride("font", Eve.Eve.Mono);
        input.AddThemeFontSizeOverride("font_size", 14);
        input.TooltipText = "Functions of x, separated by ; (y = and f(x) = are optional). Everything of the calculator works: + − × ÷ ^ √ ! %, sin cos tan (radians), ln log exp abs, π e, implicit multiplication (2x, 3sin(x))";
        var y = Eve.Eve.Text_("y =", Eve.Eve.Muted, 14);
        y.AddThemeFontOverride("font", Eve.Eve.Mono);
        var inputRow = Ui.Row(6, y, input);
        var error = Ui.Label(vm, nameof(vm.GraphError), Eve.Eve.Red, 12, wrap: true);
        Bind.Visible(error, vm, nameof(vm.HasGraphError));

        // The curves with their legend at the top left.
        var legend = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        legend.AddThemeConstantOverride("separation", 2);
        Bind.When(legend, vm, () =>
        {
            foreach (var child in legend.GetChildren())
                child.QueueFree();
            foreach (var function in vm.GraphFunctions)
            {
                var swatch = new ColorRect { Color = AvaloniaBridge.ToColor(function.Color).Lightened(0.25f), CustomMinimumSize = new Vector2(14, 3), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
                var label = Eve.Eve.Text_(function.Label, Eve.Eve.TextBright, 12);
                label.AddThemeFontOverride("font", Eve.Eve.Mono);
                legend.AddChild(Ui.Row(6, swatch, label));
            }
        }, nameof(vm.GraphFunctions));
        var legendPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        legendPanel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.PanelSolid with { A = 0.85f }, Eve.Eve.Edge with { A = 0.5f }, 6));
        legendPanel.AddChild(legend);
        legendPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        legendPanel.Position = new Vector2(8, 8);
        Bind.When(legendPanel, vm, () => legendPanel.Visible = vm.GraphFunctions.Count > 0, nameof(vm.GraphFunctions));
        var area = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true, CustomMinimumSize = new Vector2(0, 240) };
        canvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        area.AddChild(canvas);
        area.AddChild(legendPanel);

        var status = Eve.Eve.Text_("", Eve.Eve.Green, 11);
        var camera = Ui.Action("📷 CAMERA", () => _ = Camera(vm, canvas, status), primary: true,
            tooltip: "Take a picture of the graph (PNG in calculator.graphDirectory of config.yml: Pictures/kaonomi/maths by default) and add it to the tape");
        var buttons = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("h_separation", 4);
        buttons.AddThemeConstantOverride("v_separation", 4);
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
            buttons.AddChild(Ui.Action(text, action, tooltip: tip));
        buttons.AddChild(camera);
        buttons.AddChild(status);

        var page = Ui.Column(8, header, inputRow, error, area, buttons);
        page.Ready += () => Callable.From(() => input.GrabFocus()).CallDeferred();
        return Ui.Margin(page, 14);
    }

    /// <summary>📷: the picture of the graph saved in the pictures folder of the calculator, then added to the tape.</summary>
    private static async Task Camera(CalculatorViewModel vm, GraphCanvas canvas, Label status)
    {
        try
        {
            var directory = vm.GraphPicturesDirectory;
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, $"graph-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await canvas.SnapshotAsync(path);
            vm.AddGraphToTape(path, canvas.RangeText);
            status.AddThemeColorOverride("font_color", Eve.Eve.Green);
            status.Text = "✓ Added to the tape";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            status.AddThemeColorOverride("font_color", Eve.Eve.Red);
            status.Text = $"Not saved: {ex.Message}";
        }
        if (GodotObject.IsInstanceValid(status))
            status.GetTree().CreateTimer(3).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(status))
                    status.Text = string.Empty;
            };
    }
}
