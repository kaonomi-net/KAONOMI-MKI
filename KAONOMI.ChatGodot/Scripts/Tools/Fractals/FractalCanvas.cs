using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Tools.Fractals;

/// <summary>
/// The picture of /fractal, drawn in a viewport of its own (so that PNG captures it): the escape-time fractals by a
/// shader (<see cref="FractalShader"/>) on the GPU, the L-systems as lines, the IFS as a picture of the density of
/// the points of the chaos game. ⇧ + drag (or a drag in <see cref="BoxMode"/>): a square drawn on the picture, the
/// view zooms on it; wheel: zoom where the pointer is; drag: move; double click: zoom in (Shift: out);
/// right click on an escape-time fractal: its Julia set at that point; R: reset the view; + - and the arrows too.
/// Only what changed is done again, once per frame.
/// </summary>
public sealed partial class FractalCanvas : SubViewportContainer
{
    private const float Wheel = 1.2f;

    private readonly FractalWindowViewModel _vm;
    private readonly SubViewport _viewport;
    private readonly ColorRect _escape;
    private readonly ShaderMaterial _material = new();
    private readonly LineDrawing _lines;
    private readonly TextureRect _ifs;
    private readonly ColorRect _backdrop;
    private ImageTexture? _ifsTexture;

    private FractalIfs.Result? _cloud;
    private int _generation;

    private bool _dirtyShader = true, _dirtyUniforms = true, _dirtyLSystem = true, _dirtyIfs = true, _dirtyRaster = true, _dirtyColors = true;
    private double _debounce;
    private Vector2? _dragFrom;

    /// <summary>The square being drawn to zoom on it: where the drag started and where the pointer is (null: none).</summary>
    private Vector2? _boxFrom;
    private Vector2 _boxTo;

    /// <summary>The smallest square, in pixels, that zooms (a smaller drag is a click).</summary>
    private const float MinBox = 8;

    /// <summary>True: a drag draws a square to zoom on (false: it moves the view; with ⇧ it draws a square anyway).</summary>
    public bool BoxMode
    {
        get => _boxMode;
        set
        {
            _boxMode = value;
            MouseDefaultCursorShape = value ? CursorShape.Cross : CursorShape.Arrow;
        }
    }

    private bool _boxMode;
    private Vector2I _lastSize;

    /// <summary>The point of the plane under the pointer (null: the pointer is out), for the coordinates shown.</summary>
    public event Action<Vector2?>? PointerMoved;

    public FractalCanvas(FractalWindowViewModel vm)
    {
        _vm = vm;
        Stretch = true;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(320, 240);
        TooltipText = string.Empty;

        _viewport = new SubViewport
        {
            Disable3D = true,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
            Msaa2D = Viewport.Msaa.Msaa4X
        };
        AddChild(_viewport);

        _backdrop = new ColorRect { Color = new Color(0.02f, 0.03f, 0.045f), MouseFilter = MouseFilterEnum.Ignore };
        _escape = new ColorRect { Material = _material, MouseFilter = MouseFilterEnum.Ignore };
        _lines = new LineDrawing(vm) { MouseFilter = MouseFilterEnum.Ignore };
        _ifs = new TextureRect { MouseFilter = MouseFilterEnum.Ignore, StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize };
        foreach (var layer in new Control[] { _backdrop, _escape, _lines, _ifs })
        {
            layer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _viewport.AddChild(layer);
        }

        _vm.PropertyChanged += OnChanged;
        _vm.Loaded += OnLoaded;
        TreeExiting += () =>
        {
            _vm.PropertyChanged -= OnChanged;
            _vm.Loaded -= OnLoaded;
        };
        ShowFamily();
    }

    /// <summary>The viewport the picture is drawn in (for PNG).</summary>
    public SubViewport Picture => _viewport;

    private void OnLoaded() => Callable.From(() =>
    {
        _dirtyShader = _dirtyUniforms = _dirtyLSystem = _dirtyIfs = _dirtyRaster = _dirtyColors = true;
        _debounce = 0;
        ShowFamily();
    }).CallDeferred();

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FractalWindowViewModel.Family):
                Callable.From(ShowFamily).CallDeferred();
                _dirtyUniforms = _dirtyLSystem = _dirtyIfs = true;
                break;
            case nameof(FractalWindowViewModel.Glsl):
                // Compiled once the typing pauses, not at each key.
                _dirtyShader = true;
                _debounce = 0.35;
                break;
            case nameof(FractalWindowViewModel.CenterX) or nameof(FractalWindowViewModel.CenterY) or nameof(FractalWindowViewModel.Scale):
                _dirtyUniforms = true;
                _dirtyRaster = true;
                break;
            case nameof(FractalWindowViewModel.Palette) or nameof(FractalWindowViewModel.Density) or nameof(FractalWindowViewModel.Offset) or nameof(FractalWindowViewModel.Cycle):
                _dirtyUniforms = true;
                _dirtyColors = true;
                _dirtyRaster = true;
                break;
            case nameof(FractalWindowViewModel.Axiom) or nameof(FractalWindowViewModel.Rules) or nameof(FractalWindowViewModel.Angle)
                or nameof(FractalWindowViewModel.Depth) or nameof(FractalWindowViewModel.Heading) or nameof(FractalWindowViewModel.DrawSymbols):
                _dirtyLSystem = true;
                _debounce = 0.3;
                break;
            case nameof(FractalWindowViewModel.Maps) or nameof(FractalWindowViewModel.Points):
                _dirtyIfs = true;
                _debounce = 0.3;
                break;
            case nameof(FractalWindowViewModel.Status) or nameof(FractalWindowViewModel.IsError) or nameof(FractalWindowViewModel.FormulaError)
                or nameof(FractalWindowViewModel.Name) or nameof(FractalWindowViewModel.Description) or nameof(FractalWindowViewModel.Selected)
                or nameof(FractalWindowViewModel.IsOwn) or nameof(FractalWindowViewModel.Items) or nameof(FractalWindowViewModel.Formula):
                break;
            default:
                // A parameter of the escape-time fractals (c, z0, a, b, iterations, bailout, colouring, quality…).
                _dirtyUniforms = true;
                break;
        }
    }

    private void ShowFamily()
    {
        var family = _vm.Family;
        _escape.Visible = family == FractalFamily.EscapeTime;
        _lines.Visible = family == FractalFamily.LSystem;
        _ifs.Visible = family == FractalFamily.Ifs;
        _dirtyUniforms = _dirtyRaster = true;
        _lines.QueueRedraw();
    }

    public override void _Process(double delta)
    {
        var size = _viewport.Size;
        if (size != _lastSize)
        {
            _lastSize = size;
            _dirtyUniforms = _dirtyRaster = true;
            _lines.QueueRedraw();
        }
        if (_debounce > 0)
        {
            _debounce -= delta;
            if (_debounce > 0)
                return;
        }
        var render = false;
        switch (_vm.Family)
        {
            case FractalFamily.EscapeTime:
                if (_dirtyShader)
                {
                    _dirtyShader = false;
                    _dirtyUniforms = true;
                    if (_vm.Glsl is { } glsl)
                        _material.Shader = new Shader { Code = FractalShader.Build(glsl) };
                }
                if (_dirtyUniforms)
                {
                    _dirtyUniforms = false;
                    ApplyUniforms(size);
                    render = true;
                }
                break;
            case FractalFamily.LSystem:
                if (_dirtyLSystem)
                {
                    _dirtyLSystem = false;
                    _ = BuildLSystemAsync();
                }
                if (_dirtyColors)
                {
                    _dirtyColors = false;
                    _lines.Recolor();
                }
                if (_dirtyUniforms || _dirtyRaster)
                {
                    _dirtyUniforms = _dirtyRaster = false;
                    _lines.QueueRedraw();
                    render = true;
                }
                break;
            case FractalFamily.Ifs:
                if (_dirtyIfs)
                {
                    _dirtyIfs = false;
                    _ = BuildIfsAsync();
                }
                if (_dirtyRaster && _cloud is not null)
                {
                    _dirtyRaster = _dirtyUniforms = false;
                    _ = RasterAsync(size);
                }
                break;
        }
        _viewport.RenderTargetUpdateMode = _vm.Cycle && _vm.Family == FractalFamily.EscapeTime
            ? SubViewport.UpdateMode.Always
            : render ? SubViewport.UpdateMode.Once : _viewport.RenderTargetUpdateMode;
    }

    /// <summary>Draws again at once (after a change of the viewport made outside, PNG at another size).</summary>
    public void RenderNow()
    {
        _dirtyUniforms = _dirtyRaster = true;
        _lines.QueueRedraw();
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    private void ApplyUniforms(Vector2I size)
    {
        var palette = FractalPalette.Find(_vm.Palette);
        static Vector3 V(double[] v) => new((float)v[0], (float)v[1], (float)v[2]);
        _material.SetShaderParameter("center", new Vector2((float)_vm.CenterX, (float)_vm.CenterY));
        _material.SetShaderParameter("scale", (float)_vm.Scale);
        _material.SetShaderParameter("view_size", new Vector2(Math.Max(size.X, 1), Math.Max(size.Y, 1)));
        _material.SetShaderParameter("max_iter", Math.Clamp(_vm.Iterations, 1, FractalShader.MaxIterations));
        _material.SetShaderParameter("bailout", (float)Math.Max(_vm.Bailout, 1.0001));
        _material.SetShaderParameter("degree", (float)Math.Max(_vm.Degree, 1.0001));
        _material.SetShaderParameter("julia", _vm.Julia);
        _material.SetShaderParameter("julia_c", new Vector2((float)_vm.Cx, (float)_vm.Cy));
        _material.SetShaderParameter("z0", new Vector2((float)_vm.Z0x, (float)_vm.Z0y));
        _material.SetShaderParameter("u_a", new Vector2((float)_vm.Ax, (float)_vm.Ay));
        _material.SetShaderParameter("u_b", new Vector2((float)_vm.Bx, (float)_vm.By));
        _material.SetShaderParameter("coloring", (int)_vm.Coloring);
        _material.SetShaderParameter("samples", Math.Clamp(_vm.Quality, 1, 3));
        _material.SetShaderParameter("pal_a", V(palette.A));
        _material.SetShaderParameter("pal_b", V(palette.B));
        _material.SetShaderParameter("pal_c", V(palette.C));
        _material.SetShaderParameter("pal_d", V(palette.D));
        _material.SetShaderParameter("density", (float)_vm.Density);
        _material.SetShaderParameter("offset", (float)_vm.Offset);
        _material.SetShaderParameter("cycle", _vm.Cycle ? 0.05f : 0f);
    }

    // ── L-system ──

    private async Task BuildLSystemAsync()
    {
        var generation = ++_generation;
        var (axiom, rules, angle, depth, heading, draw) = (_vm.Axiom, _vm.Rules, _vm.Angle, _vm.Depth, _vm.Heading, _vm.DrawSymbols);
        _vm.Say("Rewriting the L-system…", false);
        var result = await Task.Run(() => FractalLSystem.Draw(axiom, rules, angle, depth, heading, draw));
        if (!IsInstanceValid(this) || generation != _generation)
            return;
        _lines.Set(result);
        if (result.Error is not null && result.Count == 0)
            _vm.Say(result.Error, true);
        else
            _vm.Say($"{result.Count:N0} lines from a string of {result.Length:N0} letters{(result.Truncated ? " · cut: too long, lower the depth" : string.Empty)}", false);
        RenderNow();
    }

    // ── IFS ──

    private async Task BuildIfsAsync()
    {
        var generation = ++_generation;
        var (maps, points) = (_vm.Maps, _vm.Points);
        _vm.Say("Playing the chaos game…", false);
        var result = await Task.Run(() => FractalIfs.Generate(maps, points));
        if (!IsInstanceValid(this) || generation != _generation)
            return;
        if (result.Error is not null)
        {
            _vm.Say(result.Error, true);
            return;
        }
        _cloud = result;
        _vm.Say($"{result.Points.Length / 2:N0} points, {result.MapCount} map(s)", false);
        _dirtyRaster = true;
    }

    private bool _rastering;

    /// <summary>The density of the points of the chaos game seen in the view, coloured by the map that made each one.</summary>
    private async Task RasterAsync(Vector2I size)
    {
        if (_rastering)
        {
            _dirtyRaster = true;
            return;
        }
        if (_cloud is not { } cloud || size.X < 2 || size.Y < 2)
            return;
        _rastering = true;
        var palette = FractalPalette.Find(_vm.Palette);
        var (cx, cy, scale, density, offset) = (_vm.CenterX, _vm.CenterY, _vm.Scale, _vm.Density, _vm.Offset);
        var bytes = await Task.Run(() => Raster(cloud, size.X, size.Y, cx, cy, scale, palette, density, offset));
        _rastering = false;
        if (!IsInstanceValid(this))
            return;
        var image = Image.CreateFromData(size.X, size.Y, false, Image.Format.Rgba8, bytes);
        if (_ifsTexture is null || _ifsTexture.GetSize() != new Vector2(size.X, size.Y))
        {
            _ifsTexture = ImageTexture.CreateFromImage(image);
            _ifs.Texture = _ifsTexture;
        }
        else
            _ifsTexture.Update(image);
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    private static byte[] Raster(FractalIfs.Result cloud, int width, int height, double cx, double cy, double scale, FractalPalette palette, double density, double offset)
    {
        var counts = new int[width * height];
        var red = new float[width * height];
        var green = new float[width * height];
        var blue = new float[width * height];
        var colours = new (float R, float G, float B)[Math.Max(cloud.MapCount, 1)];
        for (var k = 0; k < colours.Length; k++)
        {
            var (r, g, b) = palette.At(k / (double)colours.Length * density + offset);
            colours[k] = ((float)r, (float)g, (float)b);
        }
        var half = height / 2.0;
        var max = 0;
        var points = cloud.Points;
        for (var k = 0; k < cloud.Maps.Length; k++)
        {
            var x = (int)(width / 2.0 + (points[k * 2] - cx) / scale * half);
            var y = (int)(half - (points[k * 2 + 1] - cy) / scale * half);
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                continue;
            var at = y * width + x;
            var c = colours[cloud.Maps[k]];
            red[at] += c.R;
            green[at] += c.G;
            blue[at] += c.B;
            if (++counts[at] > max)
                max = counts[at];
        }
        var bytes = new byte[width * height * 4];
        var logMax = Math.Log(1 + Math.Max(max, 1));
        for (var at = 0; at < counts.Length; at++)
        {
            var n = counts[at];
            var o = at * 4;
            if (n == 0)
            {
                bytes[o] = 5;
                bytes[o + 1] = 8;
                bytes[o + 2] = 11;
                bytes[o + 3] = 255;
                continue;
            }
            // Log density, lifted so that a single point shows: the dense parts bright, the thin ones dim.
            var light = Math.Pow(Math.Log(1 + n) / logMax, 0.45) * 0.85 + 0.15;
            bytes[o] = (byte)Math.Clamp(red[at] / n * light * 255, 0, 255);
            bytes[o + 1] = (byte)Math.Clamp(green[at] / n * light * 255, 0, 255);
            bytes[o + 2] = (byte)Math.Clamp(blue[at] / n * light * 255, 0, 255);
            bytes[o + 3] = 255;
        }
        return bytes;
    }

    // ── The pointer and the keys ──

    /// <summary>The point of the plane at <paramref name="local"/> (a position in this control).</summary>
    public Vector2 ToPlane(Vector2 local)
    {
        var size = Size;
        var half = Math.Max(size.Y / 2, 1);
        return new Vector2(
            (float)(_vm.CenterX + (local.X - size.X / 2) / half * _vm.Scale),
            (float)(_vm.CenterY - (local.Y - size.Y / 2) / half * _vm.Scale));
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
            {
                var at = ToPlane(wheel.Position);
                var factor = wheel.ButtonIndex == MouseButton.WheelUp ? Wheel : 1 / Wheel;
                if (wheel.Factor > 0 && wheel.Factor != 1)
                    factor = MathF.Pow(factor, wheel.Factor);
                _vm.ZoomAt(at.X, at.Y, factor);
                AcceptEvent();
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true } dbl:
            {
                var at = ToPlane(dbl.Position);
                _vm.ZoomAt(at.X, at.Y, dbl.ShiftPressed ? 0.5 : 2);
                _dragFrom = null;
                AcceptEvent();
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } left when left.ShiftPressed || BoxMode:
                // A square to zoom on: drawn from here.
                GrabFocus();
                _dragFrom = null;
                _boxFrom = left.Position;
                _boxTo = left.Position;
                QueueRedraw();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _boxFrom is not null:
                ZoomToBox();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                GrabFocus();
                _dragFrom = left.Pressed ? left.Position : null;
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } right:
                if (_vm.IsEscapeTime && !_vm.Julia)
                {
                    var at = ToPlane(right.Position);
                    _vm.JuliaAt(at.X, at.Y);
                }
                AcceptEvent();
                break;
            case InputEventMagnifyGesture pinch:
            {
                var at = ToPlane(pinch.Position);
                _vm.ZoomAt(at.X, at.Y, pinch.Factor);
                AcceptEvent();
                break;
            }
            case InputEventPanGesture pan:
            {
                var unit = _vm.Scale / Math.Max(Size.Y / 2, 1);
                _vm.Pan(pan.Delta.X * 8 * unit, -pan.Delta.Y * 8 * unit);
                AcceptEvent();
                break;
            }
            case InputEventMouseMotion motion:
            {
                if (_boxFrom is not null && (motion.ButtonMask & MouseButtonMask.Left) != 0)
                {
                    _boxTo = motion.Position;
                    QueueRedraw();
                }
                else if (_dragFrom is { } from && (motion.ButtonMask & MouseButtonMask.Left) != 0)
                {
                    var unit = _vm.Scale / Math.Max(Size.Y / 2, 1);
                    var moved = motion.Position - from;
                    _vm.Pan(-moved.X * unit, moved.Y * unit);
                    _dragFrom = motion.Position;
                }
                PointerMoved?.Invoke(ToPlane(motion.Position));
                break;
            }
            case InputEventKey { Pressed: true } key:
                if (HandleKey(key))
                    AcceptEvent();
                break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit)
            PointerMoved?.Invoke(null);
    }

    // ── The square to zoom on ──

    /// <summary>The square drawn from where the drag started to the pointer (its side the larger of the two moves).</summary>
    private Rect2 Box()
    {
        var from = _boxFrom ?? Vector2.Zero;
        var move = _boxTo - from;
        var side = Math.Max(Math.Abs(move.X), Math.Abs(move.Y));
        var end = from + new Vector2(move.X < 0 ? -side : side, move.Y < 0 ? -side : side);
        return new Rect2(new Vector2(Math.Min(from.X, end.X), Math.Min(from.Y, end.Y)), new Vector2(side, side));
    }

    /// <summary>The drag is over: the view zooms on the square (the square fills the height of the view).</summary>
    private void ZoomToBox()
    {
        var box = Box();
        _boxFrom = null;
        QueueRedraw();
        if (box.Size.X < MinBox)
            return;
        var center = ToPlane(box.GetCenter());
        var unit = _vm.Scale / Math.Max(Size.Y / 2, 1);
        _vm.ZoomToSquare(center.X, center.Y, box.Size.X / 2 * unit);
    }

    /// <summary>The square being drawn, over the picture: its edge, its shade and the zoom it will give.</summary>
    public override void _Draw()
    {
        if (_boxFrom is null)
            return;
        var box = Box();
        if (box.Size.X < 2)
            return;
        DrawRect(box, new Color(1, 1, 1, 0.12f));
        DrawRect(box, new Color(0, 0, 0, 0.75f), false, 3);
        DrawRect(box, new Color(1, 1, 1, 0.95f), false, 1.5f);
        if (box.Size.X >= MinBox)
        {
            var zoom = Size.Y / box.Size.X;
            var label = $"×{zoom.ToString(zoom < 10 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture)}";
            var font = Eve.Eve.Mono;
            var at = box.Position + new Vector2(4, -6);
            if (at.Y < 14)
                at.Y = box.End.Y + 16;
            DrawStringOutline(font, at, label, HorizontalAlignment.Left, -1, 13, 4, new Color(0, 0, 0, 0.8f));
            DrawString(font, at, label, HorizontalAlignment.Left, -1, 13, Colors.White);
        }
    }

    private bool HandleKey(InputEventKey key)
    {
        var step = _vm.Scale * 0.15;
        switch (key.Keycode)
        {
            case Key.Escape when _boxFrom is not null:
                _boxFrom = null;
                QueueRedraw();
                return true;
            case Key.R:
                _vm.ResetViewCommand.Execute(null);
                return true;
            case Key.Plus or Key.Equal or Key.KpAdd:
                _vm.ZoomInCommand.Execute(null);
                return true;
            case Key.Minus or Key.KpSubtract:
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

    /// <summary>
    /// The lines of an L-system, kept in the units of the drawing (fitted in [-1, 1]) and drawn through the transform
    /// of the view: moving and zooming draw again without computing the lines again. One colour per line along the
    /// path of the turtle, from the palette.
    /// </summary>
    private sealed partial class LineDrawing(FractalWindowViewModel vm) : Control
    {
        private const int Batch = 60_000, ThickLimit = 250_000;

        private Vector2[] _points = [];
        private Color[] _colors = [];
        private int _count;

        public void Set(FractalLSystem.Result result)
        {
            _count = result.Count;
            _points = new Vector2[_count * 2];
            var s = result.Segments;
            for (var k = 0; k < _count * 2; k++)
                _points[k] = new Vector2(s[k * 2], -s[k * 2 + 1]);
            Recolor();
        }

        public void Recolor()
        {
            var palette = FractalPalette.Find(vm.Palette);
            _colors = new Color[_count];
            var steps = Math.Max(_count - 1, 1);
            for (var k = 0; k < _count; k++)
            {
                var (r, g, b) = palette.At(k / (double)steps * vm.Density + vm.Offset);
                // Lifted a little so that no line vanishes in the dark background.
                _colors[k] = new Color((float)(0.3 + 0.7 * r), (float)(0.3 + 0.7 * g), (float)(0.3 + 0.7 * b));
            }
            QueueRedraw();
        }

        public override void _Draw()
        {
            if (_count == 0)
                return;
            var half = Math.Max(Size.Y / 2, 1);
            var zoom = (float)(half / vm.Scale);
            var origin = new Vector2(Size.X / 2 - (float)vm.CenterX * zoom, Size.Y / 2 + (float)vm.CenterY * zoom);
            DrawSetTransform(origin, 0, new Vector2(zoom, zoom));
            // About 1.6 pixels wide (in the units of the drawing), antialiased; thin primitive lines for the very long drawings.
            var width = _count <= ThickLimit ? 1.6f / zoom : -1;
            for (var start = 0; start < _count; start += Batch)
            {
                var n = Math.Min(Batch, _count - start);
                DrawMultilineColors(_points[(start * 2)..((start + n) * 2)], _colors[start..(start + n)], width, antialiased: width > 0);
            }
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        }
    }
}
