using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// The canvas of <c>/bpmnai</c>: the diagram drawn in the BPMN 2.0 notation (events as circles: thin for a start, double
/// for an intermediate, thick for an end; tasks as rounded rectangles with the marker of their type, a collapsed
/// sub-process with its ⊞; gateways as diamonds marked ✕, + or ○; a data object as a page with a folded corner; an
/// annotation as an open bracket; sequence flows as arrows, associations as dotted lines), on a grid.
/// <para>The pointer: a click selects, a drag moves an element (snapped to the grid) or pans the background; the
/// wheel and the trackpad zoom. The element selected shows an arrow handle on its right: dragged onto another
/// element, it joins them; a square handle at the bottom right resizes a task, a sub-process or an annotation. With
/// an element dragged from the palette, it is placed where it is dropped; with a kind chosen in the palette, a click
/// places it (Shift kept: several); with the connect tool, a click on the
/// source then on the target joins them. A double-click renames; a right click opens the menu; Delete removes the
/// selection; the arrows nudge it; Escape cancels the tool; Cmd/Ctrl+Z undoes, Shift+Cmd/Ctrl+Z redoes.</para>
/// </summary>
public partial class BpmnCanvas : Control, Knowledge.IClusterCanvas
{
    private const float HandleRadius = 9;
    private const float MinZoom = 0.25f;
    private const float MaxZoom = 4;

    private readonly BpmnWindowViewModel _vm;
    private readonly Overlay _overlay;

    private float _zoom = 1;
    private Vector2 _offset = new(40, 40);

    private BpmnShape? _hovered;
    private BpmnFlow? _hoveredFlow;
    private Vector2 _pointer;
    private bool _pointerInside;

    // The drags.
    private BpmnShape? _moving;
    private bool _moved;
    private bool _panning;
    private Vector2 _panStart;
    private Vector2 _offsetStart;
    private BpmnShape? _connectFrom;
    private bool _connectDrag;
    private BpmnShape? _resizing;
    private Vector2 _resizeStart;
    private (float Width, float Height) _resizeSize;

    /// <summary>The kind dragged from the palette over the canvas (its ghost follows the pointer), null when none.</summary>
    private BpmnElementKind? _dragKind;

    // ---- The pen of the clusters, as in the graph of relations ----
    private readonly Knowledge.ClusterPen _pen = new();

    /// <inheritdoc />
    public string ClusterCategory => _vm.IsInfrastructure ? ClusterCategories.Infra : ClusterCategories.Bpmnai;

    /// <inheritdoc />
    public bool PenMode
    {
        get => _pen.Active;
        set => _pen.Active = value;
    }

    /// <inheritdoc />
    public bool ShowClusters
    {
        get => _pen.ShowClusters;
        set
        {
            _pen.ShowClusters = value;
            QueueRedraw();
        }
    }

    /// <inheritdoc />
    public event Action<IReadOnlyList<ClusterMemberEntry>, IReadOnlyList<Vector2>>? ClusterDrawn;

    /// <inheritdoc />
    public event Action<ClusterEntry>? ClusterDoubleClicked;

    /// <inheritdoc />
    public void SetClusters(IReadOnlyList<ClusterEntry> clusters)
    {
        _pen.Clusters = clusters;
        QueueRedraw();
    }

    /// <inheritdoc />
    public void ClearPen()
    {
        _pen.Clear();
        if (IsInstanceValid(this))
            QueueRedraw();
    }

    /// <summary>The id of an element in a cluster: the file of the diagram and the id of the element.</summary>
    private string MemberId(BpmnShape shape) => $"{_vm.FileName}#{shape.Id}";

    /// <summary>The box of a member of a cluster in the diagram (null when it is not one of its elements).</summary>
    private Rect2? BoxOf(ClusterMemberEntry member) =>
        _vm.Diagram.Shapes.FirstOrDefault(s => MemberId(s) == member.ObjectId) is { } shape ? new Rect2(shape.X, shape.Y, shape.Width, shape.Height) : null;

    /// <summary>The drawing of the pen closed: the elements whose centre is inside it make a cluster (none: the drawing goes).</summary>
    private void PenLifted()
    {
        var inside = _pen.Lift()
            ? _vm.Diagram.Shapes.Where(s => _pen.Contains(ScreenRect(s).GetCenter())).ToList()
            : [];
        QueueRedraw();
        if (inside.Count == 0)
        {
            ClearPen();
            return;
        }
        var members = inside.Select(s => new ClusterMemberEntry
        {
            WindowName = _vm.EditorTitle,
            ObjectId = MemberId(s),
            Title = string.IsNullOrWhiteSpace(s.Name) ? $"{s.Kind.Title()} · {s.Id}" : s.Name
        }).ToList();
        ClusterDrawn?.Invoke(members, _pen.Simplified(ToWorld));
    }

    private readonly Dictionary<(string, int, int, int, int), TextParagraph> _texts = [];
    private readonly StyleBoxFlat _rounded = new() { CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10, BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2, AntiAliasing = true };

    private static Color Background => Eve.Eve.IsLight ? new Color(0.985f, 0.99f, 0.995f) : new Color(0.02f, 0.03f, 0.04f, 0.92f);
    private static Color Stroke => Eve.Eve.TextBright;
    private static Color Fill => Eve.Eve.IsLight ? Colors.White : Eve.Eve.Field;
    private static Color Selection => Eve.Eve.Cyan;
    private static Color Hover => Eve.Eve.Amber;

    public BpmnCanvas(BpmnWindowViewModel vm, Overlay overlay)
    {
        _vm = vm;
        _overlay = overlay;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        // The photos of the infrastructure elements drawn smaller than they are stay smooth.
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseEntered += () => { _pointerInside = true; QueueRedraw(); };
        MouseExited += () => { _pointerInside = false; _hovered = null; _hoveredFlow = null; QueueRedraw(); };
        Action changed = () => Bind.OnMain(QueueRedraw);
        vm.Changed += changed;
        Bind.OnGone(this, () => vm.Changed -= changed);
        Bind.When(this, vm, QueueRedraw, nameof(vm.Selected), nameof(vm.Placing), nameof(vm.Connecting));
    }

    // ---- The view ----

    private Vector2 ToScreen(float x, float y) => new Vector2(x, y) * _zoom + _offset;
    private Vector2 ToScreen(Vector2 world) => world * _zoom + _offset;
    private Vector2 ToWorld(Vector2 screen) => (screen - _offset) / _zoom;
    private Rect2 ScreenRect(BpmnShape shape) => new(ToScreen(shape.X, shape.Y), new Vector2(shape.Width, shape.Height) * _zoom);

    private void ZoomBy(float factor, Vector2 at)
    {
        var zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 0.0001f)
            return;
        var world = ToWorld(at);
        _zoom = zoom;
        _offset = at - world * _zoom;
        QueueRedraw();
    }

    /// <summary>The zoom back to 100 %, the origin near the top left.</summary>
    public void ResetView()
    {
        _zoom = 1;
        _offset = new Vector2(40, 40);
        QueueRedraw();
    }

    /// <summary>The whole diagram in the canvas.</summary>
    public void Fit()
    {
        if (_vm.Diagram.Shapes.Count == 0)
        {
            ResetView();
            return;
        }
        var left = _vm.Diagram.Shapes.Min(s => s.X) - 40;
        var top = _vm.Diagram.Shapes.Min(s => s.Y) - 40;
        var right = _vm.Diagram.Shapes.Max(s => s.X + s.Width) + 40;
        var bottom = _vm.Diagram.Shapes.Max(s => s.Y + s.Height) + 60;
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        _zoom = Math.Clamp(Math.Min(Size.X / width, Size.Y / height), MinZoom, MaxZoom);
        _offset = new Vector2((Size.X - width * _zoom) / 2 - left * _zoom, (Size.Y - height * _zoom) / 2 - top * _zoom);
        QueueRedraw();
    }

    /// <summary>The zoom shown in the toolbar.</summary>
    public int ZoomPercent => (int)Math.Round(_zoom * 100);

    // ---- Drawing ----

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), Background);
        DrawGrid(size);
        var diagram = _vm.Diagram;
        _pen.DrawClusters(this, BoxOf, ToScreen, _zoom);
        foreach (var flow in diagram.Flows)
            DrawFlow(diagram, flow);
        foreach (var shape in diagram.Shapes)
            DrawShape(shape);
        DrawHandles();
        DrawConnection();
        DrawGhost();
        _pen.DrawPen(this);
        if (diagram.Shapes.Count == 0 && _vm.Placing is null)
        {
            var text = "Pick an element on the left and click here to place it";
            var font = Eve.Eve.Body;
            var width = font.GetStringSize(text, fontSize: 14).X;
            DrawString(font, new Vector2((size.X - width) / 2, size.Y / 2), text, HorizontalAlignment.Left, -1, 14, Eve.Eve.Muted);
        }
        if (_texts.Count > 2000)
            _texts.Clear();
    }

    private void DrawGrid(Vector2 size)
    {
        var step = BpmnWindowViewModel.Grid * 2 * _zoom;
        while (step < 14)
            step *= 2;
        var color = Eve.Eve.Edge with { A = 0.1f };
        for (var x = Mathf.PosMod(_offset.X, step); x < size.X; x += step)
            DrawLine(new Vector2(x, 0), new Vector2(x, size.Y), color);
        for (var y = Mathf.PosMod(_offset.Y, step); y < size.Y; y += step)
            DrawLine(new Vector2(0, y), new Vector2(size.X, y), color);
    }

    private void DrawShape(BpmnShape shape)
    {
        var selected = ReferenceEquals(_vm.Selected, shape);
        var hovered = ReferenceEquals(_hovered, shape) && !selected;
        var stroke = selected ? Selection : hovered ? Hover : Stroke;
        var rect = ScreenRect(shape);
        var center = rect.GetCenter();
        var z = _zoom;
        var canvas = GetCanvasItem();

        switch (shape.Kind)
        {
            case BpmnElementKind.StartEvent:
            case BpmnElementKind.MessageStartEvent:
            case BpmnElementKind.TimerStartEvent:
            case BpmnElementKind.EventCollector:
            case BpmnElementKind.EndEvent:
            {
                var radius = Math.Min(rect.Size.X, rect.Size.Y) / 2;
                DrawCircle(center, radius, Fill);
                var width = shape.Kind == BpmnElementKind.EndEvent ? Math.Max(2.5f, 4 * z) : Math.Max(1, 1.5f * z);
                DrawArc(center, radius - width / 2, 0, Mathf.Tau, 64, stroke, width, true);
                if (shape.Kind == BpmnElementKind.EventCollector)
                    DrawFunnel(center, radius, stroke);
                else if (shape.Kind == BpmnElementKind.MessageStartEvent)
                    DrawEnvelope(center, radius, stroke);
                else if (shape.Kind == BpmnElementKind.TimerStartEvent)
                    DrawClock(center, radius, stroke);
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Task:
            case BpmnElementKind.UserTask:
            case BpmnElementKind.AgentTask:
            case BpmnElementKind.VirtualAgentTask:
            case BpmnElementKind.SubProcess:
            {
                _rounded.BgColor = Fill;
                _rounded.BorderColor = stroke;
                var border = Math.Max(1, (int)Math.Round((shape.Kind == BpmnElementKind.SubProcess ? 2.5f : 1.5f) * z));
                _rounded.BorderWidthLeft = _rounded.BorderWidthTop = _rounded.BorderWidthRight = _rounded.BorderWidthBottom = border;
                var radius = (int)Math.Round(10 * z);
                _rounded.CornerRadiusTopLeft = _rounded.CornerRadiusTopRight = _rounded.CornerRadiusBottomLeft = _rounded.CornerRadiusBottomRight = radius;
                _rounded.Draw(canvas, rect);
                var marker = shape.Kind switch { BpmnElementKind.UserTask => "👤", BpmnElementKind.AgentTask => "🤖", BpmnElementKind.VirtualAgentTask => "👾", _ => null };
                if (marker is not null)
                {
                    var glyph = Text(marker, Eve.Eve.Emoji, 12 * z, 30 * z, 1, HorizontalAlignment.Left);
                    glyph.Draw(canvas, rect.Position + new Vector2(6 * z, 4 * z), stroke);
                }
                var name = Text(shape.Name, Eve.Eve.Body, 12 * z, rect.Size.X - 12 * z, 4, HorizontalAlignment.Center);
                var height = LineHeight(name);
                name.Draw(canvas, new Vector2(rect.Position.X + 6 * z, center.Y - height / 2 - (shape.Kind == BpmnElementKind.SubProcess ? 4 * z : 0)), Stroke);
                if (shape.Kind == BpmnElementKind.SubProcess)
                {
                    // The ⊞ of a collapsed sub-process, at the bottom centre.
                    var side = 12 * z;
                    var box = new Rect2(center.X - side / 2, rect.End.Y - side - 4 * z, side, side);
                    DrawRect(box, stroke, false, Math.Max(1, 1.2f * z));
                    DrawLine(new Vector2(box.Position.X + 3 * z, box.GetCenter().Y), new Vector2(box.End.X - 3 * z, box.GetCenter().Y), stroke, Math.Max(1, 1.2f * z));
                    DrawLine(new Vector2(box.GetCenter().X, box.Position.Y + 3 * z), new Vector2(box.GetCenter().X, box.End.Y - 3 * z), stroke, Math.Max(1, 1.2f * z));
                }
                break;
            }
            case BpmnElementKind.ExclusiveGateway:
            case BpmnElementKind.ParallelGateway:
            case BpmnElementKind.InclusiveGateway:
            {
                var hw = rect.Size.X / 2;
                var hh = rect.Size.Y / 2;
                Vector2[] diamond = [center + new Vector2(0, -hh), center + new Vector2(hw, 0), center + new Vector2(0, hh), center + new Vector2(-hw, 0)];
                DrawColoredPolygon(diamond, Fill);
                DrawPolyline([.. diamond, diamond[0]], stroke, Math.Max(1, 1.5f * z), true);
                var width = Math.Max(1.5f, 2.5f * z);
                var m = Math.Min(hw, hh) * 0.4f;
                switch (shape.Kind)
                {
                    case BpmnElementKind.ExclusiveGateway:
                        DrawLine(center + new Vector2(-m, -m), center + new Vector2(m, m), stroke, width, true);
                        DrawLine(center + new Vector2(-m, m), center + new Vector2(m, -m), stroke, width, true);
                        break;
                    case BpmnElementKind.ParallelGateway:
                        DrawLine(center + new Vector2(-m * 1.2f, 0), center + new Vector2(m * 1.2f, 0), stroke, width, true);
                        DrawLine(center + new Vector2(0, -m * 1.2f), center + new Vector2(0, m * 1.2f), stroke, width, true);
                        break;
                    default:
                        DrawArc(center, m * 1.1f, 0, Mathf.Tau, 48, stroke, width, true);
                        break;
                }
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.DataObject:
            {
                var fold = Math.Min(rect.Size.X, rect.Size.Y) * 0.3f;
                Vector2[] page = [rect.Position, new Vector2(rect.End.X - fold, rect.Position.Y), new Vector2(rect.End.X, rect.Position.Y + fold), rect.End, new Vector2(rect.Position.X, rect.End.Y)];
                DrawColoredPolygon(page, Fill);
                DrawPolyline([.. page, page[0]], stroke, Math.Max(1, 1.5f * z), true);
                DrawPolyline([new Vector2(rect.End.X - fold, rect.Position.Y), new Vector2(rect.End.X - fold, rect.Position.Y + fold), new Vector2(rect.End.X, rect.Position.Y + fold)], stroke, Math.Max(1, 1.2f * z), true);
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Database:
            {
                // A database: a cylinder.
                DrawCylinder(rect, stroke);
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.SearchEngine:
            {
                // The search engine: a cylinder with a magnifying glass over its corner.
                DrawCylinder(rect, stroke);
                var glass = rect.Position + rect.Size * new Vector2(0.7f, 0.62f);
                var radius = rect.Size.X * 0.17f;
                DrawCircle(glass, radius, Fill);
                DrawArc(glass, radius, 0, Mathf.Tau, 32, stroke, Math.Max(1.5f, 2 * z), true);
                DrawLine(glass + new Vector2(radius * 0.7f, radius * 0.7f), glass + new Vector2(radius * 1.9f, radius * 1.9f), stroke, Math.Max(1.5f, 2.5f * z), true);
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.VectorDatabase:
            {
                // A database of vectors: a cylinder with three arrows (the vectors) on its body.
                DrawCylinder(rect, stroke);
                var width = Math.Max(1, 1.2f * z);
                var left = rect.Position.X + rect.Size.X * 0.26f;
                var right = rect.Position.X + rect.Size.X * 0.74f;
                for (var i = 0; i < 3; i++)
                {
                    var y = rect.Position.Y + rect.Size.Y * (0.46f + 0.15f * i);
                    DrawLine(new Vector2(left, y), new Vector2(right, y), stroke, width, true);
                    DrawPolyline([new Vector2(right - 4 * z, y - 3 * z), new Vector2(right, y), new Vector2(right - 4 * z, y + 3 * z)], stroke, width, true);
                }
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Rag:
            {
                // Retrieval: a page with a magnifying glass over its corner.
                var width = Math.Max(1, 1.5f * z);
                var page = new Rect2(rect.Position + new Vector2(rect.Size.X * 0.08f, 0), new Vector2(rect.Size.X * 0.68f, rect.Size.Y * 0.84f));
                var fold = page.Size.X * 0.28f;
                Vector2[] sheet = [page.Position, new Vector2(page.End.X - fold, page.Position.Y), new Vector2(page.End.X, page.Position.Y + fold), page.End, new Vector2(page.Position.X, page.End.Y)];
                DrawColoredPolygon(sheet, Fill);
                DrawPolyline([.. sheet, sheet[0]], stroke, width, true);
                for (var i = 0; i < 3; i++)
                {
                    var y = page.Position.Y + page.Size.Y * (0.42f + 0.16f * i);
                    DrawLine(new Vector2(page.Position.X + 5 * z, y), new Vector2(page.End.X - 5 * z, y), stroke with { A = 0.7f }, Math.Max(1, 1 * z), true);
                }
                var glass = rect.Position + rect.Size * new Vector2(0.72f, 0.66f);
                var radius = rect.Size.X * 0.16f;
                DrawCircle(glass, radius, Fill);
                DrawArc(glass, radius, 0, Mathf.Tau, 32, stroke, Math.Max(1.5f, 2 * z), true);
                DrawLine(glass + new Vector2(radius * 0.7f, radius * 0.7f), glass + new Vector2(radius * 1.9f, radius * 1.9f), stroke, Math.Max(1.5f, 2.5f * z), true);
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Llm:
            {
                // A language model: a chip with its pins, LLM written on it, a spark at its corner.
                var width = Math.Max(1, 1.5f * z);
                var chip = rect.Grow(-rect.Size.X * 0.14f);
                DrawRect(chip, Fill);
                DrawRect(chip, stroke, false, width);
                for (var i = 0; i < 4; i++)
                {
                    var t = chip.Position.X + chip.Size.X * (0.2f + 0.2f * i);
                    DrawLine(new Vector2(t, chip.Position.Y), new Vector2(t, rect.Position.Y + 1), stroke, width, true);
                    DrawLine(new Vector2(t, chip.End.Y), new Vector2(t, rect.End.Y - 1), stroke, width, true);
                    var s = chip.Position.Y + chip.Size.Y * (0.2f + 0.2f * i);
                    DrawLine(new Vector2(chip.Position.X, s), new Vector2(rect.Position.X + 1, s), stroke, width, true);
                    DrawLine(new Vector2(chip.End.X, s), new Vector2(rect.End.X - 1, s), stroke, width, true);
                }
                DrawCenteredText("LLM", Eve.Eve.Mono, 11 * z, chip, stroke);
                DrawSpark(chip.End - new Vector2(4 * z, chip.Size.Y - 4 * z), 4 * z, stroke);
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Model:
            {
                // A model: a cube in perspective (its front, its top, its side).
                var width = Math.Max(1, 1.5f * z);
                var d = rect.Size.X * 0.26f;
                var front = new Rect2(rect.Position + new Vector2(0, d), rect.Size - new Vector2(d, d));
                Vector2[] top = [front.Position, front.Position + new Vector2(d, -d), new Vector2(front.End.X + d, front.Position.Y - d), new Vector2(front.End.X, front.Position.Y)];
                Vector2[] sideFace = [new Vector2(front.End.X, front.Position.Y), new Vector2(front.End.X + d, front.Position.Y - d), new Vector2(front.End.X + d, front.End.Y - d), front.End];
                DrawRect(front, Fill);
                DrawColoredPolygon(top, stroke with { A = 0.18f });
                DrawColoredPolygon(sideFace, stroke with { A = 0.32f });
                DrawRect(front, stroke, false, width);
                DrawPolyline([.. top, top[0]], stroke, width, true);
                DrawPolyline([.. sideFace, sideFace[0]], stroke, width, true);
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.RestApi:
            {
                // An API: braces around REST, in a rounded box.
                _rounded.BgColor = Fill;
                _rounded.BorderColor = stroke;
                var border = Math.Max(1, (int)Math.Round(1.5f * z));
                _rounded.BorderWidthLeft = _rounded.BorderWidthTop = _rounded.BorderWidthRight = _rounded.BorderWidthBottom = border;
                var radius = (int)Math.Round(8 * z);
                _rounded.CornerRadiusTopLeft = _rounded.CornerRadiusTopRight = _rounded.CornerRadiusBottomLeft = _rounded.CornerRadiusBottomRight = radius;
                _rounded.Draw(canvas, rect);
                DrawCenteredText("{ }", Eve.Eve.Mono, 20 * z, new Rect2(rect.Position, new Vector2(rect.Size.X, rect.Size.Y * 0.68f)), stroke);
                DrawCenteredText("REST", Eve.Eve.Mono, 8 * z, new Rect2(rect.Position + new Vector2(0, rect.Size.Y * 0.62f), new Vector2(rect.Size.X, rect.Size.Y * 0.3f)), stroke);
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Mcp:
            {
                // A connector: a plug (its body, two prongs, its cable).
                var width = Math.Max(1, 1.5f * z);
                var body = new Rect2(rect.Position + new Vector2(rect.Size.X * 0.22f, rect.Size.Y * 0.3f), new Vector2(rect.Size.X * 0.56f, rect.Size.Y * 0.4f));
                _rounded.BgColor = Fill;
                _rounded.BorderColor = stroke;
                var border = Math.Max(1, (int)Math.Round(1.5f * z));
                _rounded.BorderWidthLeft = _rounded.BorderWidthTop = _rounded.BorderWidthRight = _rounded.BorderWidthBottom = border;
                var radius = (int)Math.Round(5 * z);
                _rounded.CornerRadiusTopLeft = _rounded.CornerRadiusTopRight = _rounded.CornerRadiusBottomLeft = _rounded.CornerRadiusBottomRight = radius;
                _rounded.Draw(canvas, body);
                foreach (var t in new[] { 0.32f, 0.68f })
                {
                    var x = body.Position.X + body.Size.X * t;
                    DrawLine(new Vector2(x, body.Position.Y), new Vector2(x, rect.Position.Y + rect.Size.Y * 0.08f), stroke, Math.Max(1.5f, 2.5f * z), true);
                }
                var cableTop = new Vector2(center.X, body.End.Y);
                var cableEnd = new Vector2(center.X + rect.Size.X * 0.18f, rect.End.Y - 2 * z);
                var c1 = cableTop + new Vector2(0, rect.Size.Y * 0.14f);
                var c2 = cableEnd - new Vector2(rect.Size.X * 0.18f, rect.Size.Y * 0.08f);
                var cable = new Vector2[13];
                for (var i = 0; i <= 12; i++)
                {
                    var t = i / 12f;
                    var u = 1 - t;
                    cable[i] = u * u * u * cableTop + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * cableEnd;
                }
                DrawPolyline(cable, stroke, width, true);
                DrawCenteredText("MCP", Eve.Eve.Mono, 8 * z, body, stroke);
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Graphbase:
            {
                // A graph: nodes (small discs) joined by edges, in a rounded box.
                _rounded.BgColor = Fill;
                _rounded.BorderColor = stroke;
                var border = Math.Max(1, (int)Math.Round(1.5f * z));
                _rounded.BorderWidthLeft = _rounded.BorderWidthTop = _rounded.BorderWidthRight = _rounded.BorderWidthBottom = border;
                var radius = (int)Math.Round(8 * z);
                _rounded.CornerRadiusTopLeft = _rounded.CornerRadiusTopRight = _rounded.CornerRadiusBottomLeft = _rounded.CornerRadiusBottomRight = radius;
                _rounded.Draw(canvas, rect);
                var w = rect.Size.X;
                var h = rect.Size.Y;
                Vector2[] nodes =
                [
                    rect.Position + new Vector2(w * 0.5f, h * 0.22f), rect.Position + new Vector2(w * 0.22f, h * 0.5f), rect.Position + new Vector2(w * 0.78f, h * 0.5f),
                    rect.Position + new Vector2(w * 0.35f, h * 0.8f), rect.Position + new Vector2(w * 0.68f, h * 0.8f)
                ];
                var width = Math.Max(1, 1.2f * z);
                foreach (var (a, b) in new[] { (0, 1), (0, 2), (1, 3), (2, 4), (3, 4), (1, 2) })
                    DrawLine(nodes[a], nodes[b], stroke, width, true);
                var dot = Math.Max(2, 3.2f * z);
                foreach (var node in nodes)
                {
                    DrawCircle(node, dot, stroke);
                    DrawCircle(node, dot * 0.45f, Fill);
                }
                DrawLabelBelow(shape, rect, stroke);
                break;
            }
            case BpmnElementKind.TextAnnotation:
            {
                var width = Math.Max(1, 1.5f * z);
                var ear = Math.Min(14 * z, rect.Size.X / 3);
                DrawPolyline([new Vector2(rect.Position.X + ear, rect.Position.Y), rect.Position, new Vector2(rect.Position.X, rect.End.Y), new Vector2(rect.Position.X + ear, rect.End.Y)], stroke, width, true);
                var text = Text(shape.Name, Eve.Eve.Body, 11 * z, rect.Size.X - 10 * z, 6, HorizontalAlignment.Left);
                text.Draw(canvas, rect.Position + new Vector2(6 * z, 4 * z), Stroke);
                break;
            }
            default:
                if (shape.Kind.IsInfra())
                    DrawDevice(shape, rect, stroke);
                break;
        }
    }

    /// <summary>
    /// An element of an infrastructure (/infra): a card with the photo of the machine or the device
    /// (<see cref="InfraPictures"/>), its name under the card; without photo, its emoji and its short word (PC, DGX, PI…).
    /// </summary>
    private void DrawDevice(BpmnShape shape, Rect2 rect, Color stroke)
    {
        var z = _zoom;
        var canvas = GetCanvasItem();
        if (InfraPictures.Of(shape.Kind) is { } photo)
        {
            // The photo kept square, centred in the card, framed by the colour of the selection.
            var side = Math.Min(rect.Size.X, rect.Size.Y);
            var square = new Rect2(rect.GetCenter() - new Vector2(side, side) / 2, new Vector2(side, side));
            DrawTextureRect(photo, square, false);
            var frame = Math.Max(1, (ReferenceEquals(_vm.Selected, shape) ? 2.5f : 1.2f) * z);
            DrawRect(square, stroke, false, frame);
            DrawLabelBelow(shape, square, stroke);
            return;
        }
        _rounded.BgColor = Fill;
        _rounded.BorderColor = stroke;
        var border = Math.Max(1, (int)Math.Round(1.5f * z));
        _rounded.BorderWidthLeft = _rounded.BorderWidthTop = _rounded.BorderWidthRight = _rounded.BorderWidthBottom = border;
        var radius = (int)Math.Round(12 * z);
        _rounded.CornerRadiusTopLeft = _rounded.CornerRadiusTopRight = _rounded.CornerRadiusBottomLeft = _rounded.CornerRadiusBottomRight = radius;
        _rounded.Draw(canvas, rect);
        // The picture in the upper part (its own colours), the word in the lower part.
        var icon = Text(shape.Kind.Icon(), Eve.Eve.Emoji, Math.Min(rect.Size.X, rect.Size.Y) * 0.42f, rect.Size.X, 1, HorizontalAlignment.Center);
        icon.Draw(canvas, new Vector2(rect.Position.X, rect.Position.Y + rect.Size.Y * 0.38f - LineHeight(icon) / 2), Colors.White);
        var badge = Text(shape.Kind.Badge(), Eve.Eve.Mono, 9 * z, rect.Size.X - 6 * z, 1, HorizontalAlignment.Center);
        badge.Draw(canvas, new Vector2(rect.Position.X + 3 * z, rect.End.Y - LineHeight(badge) - 6 * z), stroke with { A = 0.85f });
        DrawLabelBelow(shape, rect, stroke);
    }

    /// <summary>A database: a cylinder (an ellipse on top, the sides, the bottom arc, a second ellipse a little below the top).</summary>
    private void DrawCylinder(Rect2 rect, Color stroke)
    {
        var z = _zoom;
        var center = rect.GetCenter();
        var width = Math.Max(1, 1.5f * z);
        var ry = rect.Size.Y * 0.16f;
        var rx = rect.Size.X / 2;
        var top = new Vector2(center.X, rect.Position.Y + ry);
        var bottom = new Vector2(center.X, rect.End.Y - ry);
        var body = new List<Vector2>();
        for (var i = 0; i <= 24; i++)
            body.Add(top + new Vector2(MathF.Cos(Mathf.Pi + Mathf.Pi * i / 24) * rx, MathF.Sin(Mathf.Pi + Mathf.Pi * i / 24) * ry));
        for (var i = 0; i <= 24; i++)
            body.Add(bottom + new Vector2(MathF.Cos(Mathf.Pi * i / 24) * rx, MathF.Sin(Mathf.Pi * i / 24) * ry));
        DrawColoredPolygon(body.ToArray(), Fill);
        DrawPolyline([.. body, body[0]], stroke, width, true);
        DrawPolyline(Ellipse(top, rx, ry), stroke, width, true);
        DrawPolyline(Ellipse(top + new Vector2(0, ry * 1.3f), rx, ry).Skip(24).ToArray(), stroke, Math.Max(1, 1.1f * z), true);
    }

    /// <summary>A text centred in <paramref name="box"/>.</summary>
    private void DrawCenteredText(string text, Font font, float fontSize, Rect2 box, Color color)
    {
        var paragraph = Text(text, font, fontSize, box.Size.X, 1, HorizontalAlignment.Center);
        var height = LineHeight(paragraph);
        paragraph.Draw(GetCanvasItem(), new Vector2(box.Position.X, box.GetCenter().Y - height / 2), color);
    }

    /// <summary>A four-pointed spark.</summary>
    private void DrawSpark(Vector2 at, float size, Color color)
    {
        DrawColoredPolygon([at + new Vector2(0, -size), at + new Vector2(size * 0.3f, -size * 0.3f), at + new Vector2(size, 0), at + new Vector2(size * 0.3f, size * 0.3f),
            at + new Vector2(0, size), at + new Vector2(-size * 0.3f, size * 0.3f), at + new Vector2(-size, 0), at + new Vector2(-size * 0.3f, -size * 0.3f)], color);
    }

    /// <summary>The points of an ellipse (the top half first, then the bottom half), closed.</summary>
    private static Vector2[] Ellipse(Vector2 center, float rx, float ry)
    {
        var points = new Vector2[49];
        for (var i = 0; i <= 48; i++)
            points[i] = center + new Vector2(MathF.Cos(Mathf.Pi + Mathf.Tau * i / 48) * rx, MathF.Sin(Mathf.Pi + Mathf.Tau * i / 48) * ry);
        return points;
    }

    /// <summary>The marker of an event collector: a funnel in the circle, three events (dots) falling into it.</summary>
    private void DrawFunnel(Vector2 center, float radius, Color color)
    {
        var width = Math.Max(1, 1.2f * _zoom);
        var top = center.Y - radius * 0.2f;
        var neck = center.Y + radius * 0.25f;
        var bottom = center.Y + radius * 0.6f;
        var wide = radius * 0.55f;
        var narrow = radius * 0.12f;
        DrawPolyline([new Vector2(center.X - wide, top), new Vector2(center.X + wide, top), new Vector2(center.X + narrow, neck), new Vector2(center.X + narrow, bottom),
            new Vector2(center.X - narrow, bottom), new Vector2(center.X - narrow, neck), new Vector2(center.X - wide, top)], color, width, true);
        var dot = Math.Max(1.2f, 1.8f * _zoom);
        DrawCircle(new Vector2(center.X - radius * 0.3f, center.Y - radius * 0.55f), dot, color);
        DrawCircle(new Vector2(center.X, center.Y - radius * 0.68f), dot, color);
        DrawCircle(new Vector2(center.X + radius * 0.3f, center.Y - radius * 0.5f), dot, color);
    }

    /// <summary>The marker of a message event: an envelope in the circle.</summary>
    private void DrawEnvelope(Vector2 center, float radius, Color color)
    {
        var hw = radius * 0.5f;
        var hh = radius * 0.34f;
        var width = Math.Max(1, 1.2f * _zoom);
        var box = new Rect2(center - new Vector2(hw, hh), new Vector2(hw * 2, hh * 2));
        DrawRect(box, color, false, width);
        // The flap: from the two top corners to the middle.
        DrawPolyline([box.Position, new Vector2(center.X, center.Y + hh * 0.15f), new Vector2(box.End.X, box.Position.Y)], color, width, true);
    }

    /// <summary>The marker of a timer event: a clock face with its hands in the circle.</summary>
    private void DrawClock(Vector2 center, float radius, Color color)
    {
        var r = radius * 0.6f;
        var width = Math.Max(1, 1.2f * _zoom);
        DrawArc(center, r, 0, Mathf.Tau, 48, color, width, true);
        for (var i = 0; i < 12; i++)
        {
            var angle = i * Mathf.Tau / 12;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            DrawLine(center + direction * r * 0.8f, center + direction * r, color, width, true);
        }
        // Ten past ten, as on the clocks of the shops.
        DrawLine(center, center + new Vector2(MathF.Cos(-Mathf.Pi / 3), MathF.Sin(-Mathf.Pi / 3)) * r * 0.55f, color, width, true);
        DrawLine(center, center + new Vector2(MathF.Cos(-Mathf.Pi / 2 + Mathf.Tau / 12), MathF.Sin(-Mathf.Pi / 2 + Mathf.Tau / 12)) * r * 0.8f, color, width, true);
    }

    /// <summary>The name of an event, a gateway or a data object, centred under it.</summary>
    private void DrawLabelBelow(BpmnShape shape, Rect2 rect, Color color)
    {
        if (string.IsNullOrWhiteSpace(shape.Name))
            return;
        var width = 120 * _zoom;
        var label = Text(shape.Name, Eve.Eve.Body, 11 * _zoom, width, 2, HorizontalAlignment.Center);
        label.Draw(GetCanvasItem(), new Vector2(rect.GetCenter().X - width / 2, rect.End.Y + 4 * _zoom), ReferenceEquals(_vm.Selected, shape) ? color : Stroke);
    }

    private void DrawFlow(BpmnDiagram diagram, BpmnFlow flow)
    {
        var source = diagram.Shape(flow.SourceId);
        var target = diagram.Shape(flow.TargetId);
        if (source is null || target is null)
            return;
        var (from, to) = Ends(source, target);
        var selected = ReferenceEquals(_vm.Selected, flow);
        var hovered = ReferenceEquals(_hoveredFlow, flow) && !selected;
        var color = selected ? Selection : hovered ? Hover : Stroke;
        var width = Math.Max(1, (selected ? 2.5f : 1.5f) * _zoom);
        if (flow.Kind == BpmnFlowKind.Association)
        {
            DrawDashedLine(from, to, color, width, Math.Max(3, 5 * _zoom), true, true);
            return;
        }
        var direction = to - from;
        var unit = direction.Length() < 0.001f ? Vector2.Right : direction.Normalized();
        var arrow = Math.Max(7, 11 * _zoom);
        var tip = to;
        var tail = to - unit * arrow;
        DrawLine(from, tail, color, width, true);
        var side = new Vector2(-unit.Y, unit.X) * arrow * 0.45f;
        DrawColoredPolygon([tip, tail + side, tail - side], color);
        if (!string.IsNullOrWhiteSpace(flow.Name))
        {
            var boxWidth = 140 * _zoom;
            var label = Text(flow.Name, Eve.Eve.Body, 11 * _zoom, boxWidth, 2, HorizontalAlignment.Center);
            var middle = (from + to) / 2;
            var height = LineHeight(label);
            var textWidth = TextWidth(label);
            var box = new Rect2(middle.X - textWidth / 2 - 3, middle.Y - height - 6 * _zoom, textWidth + 6, height + 2);
            DrawRect(box, Background with { A = 0.85f });
            label.Draw(GetCanvasItem(), new Vector2(middle.X - boxWidth / 2, box.Position.Y + 1), selected ? color : Stroke);
        }
    }

    /// <summary>The two ends of a flow on the screen: on the borders of its elements.</summary>
    private (Vector2 From, Vector2 To) Ends(BpmnShape source, BpmnShape target)
    {
        var from = BpmnGeometry.Anchor(source, target.CenterX, target.CenterY);
        var to = BpmnGeometry.Anchor(target, source.CenterX, source.CenterY);
        return (ToScreen(from.X, from.Y), ToScreen(to.X, to.Y));
    }

    /// <summary>The handles of the element selected: a dashed frame, the arrow to join it, the square to resize it.</summary>
    private void DrawHandles()
    {
        if (_vm.Selected is not BpmnShape shape)
            return;
        var rect = ScreenRect(shape).Grow(4);
        var color = Selection with { A = 0.8f };
        DrawDashedLine(rect.Position, new Vector2(rect.End.X, rect.Position.Y), color, 1, 4);
        DrawDashedLine(new Vector2(rect.End.X, rect.Position.Y), rect.End, color, 1, 4);
        DrawDashedLine(rect.End, new Vector2(rect.Position.X, rect.End.Y), color, 1, 4);
        DrawDashedLine(new Vector2(rect.Position.X, rect.End.Y), rect.Position, color, 1, 4);

        var handle = ConnectHandle(shape);
        var lit = handle.DistanceTo(_pointer) <= HandleRadius + 2;
        DrawCircle(handle, HandleRadius, lit ? Selection : Eve.Eve.PanelSolid);
        DrawArc(handle, HandleRadius, 0, Mathf.Tau, 32, Selection, 1.5f, true);
        var arrowColor = lit ? Eve.Eve.PanelSolid : Selection;
        DrawLine(handle + new Vector2(-4, 0), handle + new Vector2(4, 0), arrowColor, 1.5f, true);
        DrawPolyline([handle + new Vector2(1, -3), handle + new Vector2(4, 0), handle + new Vector2(1, 3)], arrowColor, 1.5f, true);

        if (CanResize(shape))
        {
            var square = ResizeHandle(shape);
            DrawRect(square, ResizeHandleLit(shape) ? Selection : Eve.Eve.PanelSolid);
            DrawRect(square, Selection, false, 1.5f);
        }
    }

    /// <summary>The line of a connection being drawn: from its source to the pointer, or to the element under it.</summary>
    private void DrawConnection()
    {
        if (_connectFrom is null)
            return;
        var from = ToScreen(_connectFrom.CenterX, _connectFrom.CenterY);
        var target = _hovered is not null && !ReferenceEquals(_hovered, _connectFrom) ? _hovered : null;
        var to = target is not null ? ToScreen(target.CenterX, target.CenterY) : _pointer;
        if (target is not null)
            (from, to) = Ends(_connectFrom, target);
        else
        {
            var anchor = BpmnGeometry.Anchor(_connectFrom, ToWorld(_pointer).X, ToWorld(_pointer).Y);
            from = ToScreen(anchor.X, anchor.Y);
        }
        DrawDashedLine(from, to, Hover, 2, 6, true, true);
        DrawCircle(to, 4, Hover);
        if (target is not null)
            DrawRect(ScreenRect(target).Grow(4), Hover, false, 1.5f);
    }

    /// <summary>The element to place (chosen in the palette, or dragged from it), faint under the pointer.</summary>
    private void DrawGhost()
    {
        var placing = _dragKind ?? _vm.Placing;
        if (placing is not { } kind || !_pointerInside && _dragKind is null || _panning)
            return;
        var (width, height) = kind.DefaultSize();
        var world = ToWorld(_pointer);
        var rect = new Rect2(ToScreen(BpmnWindowViewModel.Snap(world.X - width / 2), BpmnWindowViewModel.Snap(world.Y - height / 2)), new Vector2(width, height) * _zoom);
        var color = Selection with { A = 0.5f };
        if (kind.IsEvent())
            DrawArc(rect.GetCenter(), rect.Size.X / 2, 0, Mathf.Tau, 48, color, 1.5f, true);
        else if (kind.IsGateway())
        {
            var c = rect.GetCenter();
            DrawPolyline([c + new Vector2(0, -rect.Size.Y / 2), c + new Vector2(rect.Size.X / 2, 0), c + new Vector2(0, rect.Size.Y / 2), c + new Vector2(-rect.Size.X / 2, 0), c + new Vector2(0, -rect.Size.Y / 2)], color, 1.5f, true);
        }
        else
            DrawRect(rect, color, false, 1.5f);
    }

    // ---- Texts ----

    private TextParagraph Text(string text, Font font, float fontSize, float width, int lines, HorizontalAlignment alignment)
    {
        var size = Math.Max(1, (int)Math.Round(fontSize));
        var key = (text, size, (int)width, lines * 10 + (int)alignment, font.GetHashCode());
        if (_texts.TryGetValue(key, out var paragraph))
            return paragraph;
        paragraph = new TextParagraph
        {
            Width = Math.Max(1, width),
            MaxLinesVisible = lines,
            Alignment = alignment,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            BreakFlags = lines > 1 ? TextServer.LineBreakFlag.WordBound | TextServer.LineBreakFlag.Adaptive | TextServer.LineBreakFlag.Mandatory : TextServer.LineBreakFlag.None
        };
        paragraph.AddString(text, font, size);
        _texts[key] = paragraph;
        return paragraph;
    }

    private static float LineHeight(TextParagraph paragraph)
    {
        var lines = Math.Min(paragraph.GetLineCount(), paragraph.MaxLinesVisible > 0 ? paragraph.MaxLinesVisible : int.MaxValue);
        var height = 0f;
        for (var i = 0; i < lines; i++)
            height += paragraph.GetLineSize(i).Y;
        return height;
    }

    private static float TextWidth(TextParagraph paragraph)
    {
        var lines = Math.Min(paragraph.GetLineCount(), paragraph.MaxLinesVisible > 0 ? paragraph.MaxLinesVisible : int.MaxValue);
        var width = 0f;
        for (var i = 0; i < lines; i++)
            width = Math.Max(width, paragraph.GetLineWidth(i));
        return width;
    }

    // ---- Hit tests ----

    private BpmnShape? ShapeAt(Vector2 screen)
    {
        var world = ToWorld(screen);
        for (var i = _vm.Diagram.Shapes.Count - 1; i >= 0; i--)
        {
            var shape = _vm.Diagram.Shapes[i];
            if (BpmnGeometry.Contains(shape, world.X, world.Y))
                return shape;
        }
        return null;
    }

    private BpmnFlow? FlowAt(Vector2 screen)
    {
        var diagram = _vm.Diagram;
        BpmnFlow? best = null;
        var bestDistance = 7f;
        foreach (var flow in diagram.Flows)
        {
            var source = diagram.Shape(flow.SourceId);
            var target = diagram.Shape(flow.TargetId);
            if (source is null || target is null)
                continue;
            var (from, to) = Ends(source, target);
            var distance = DistanceToSegment(screen, from, to);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = flow;
            }
        }
        return best;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var length = ab.LengthSquared();
        if (length < 0.0001f)
            return p.DistanceTo(a);
        var t = Math.Clamp((p - a).Dot(ab) / length, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    private Vector2 ConnectHandle(BpmnShape shape)
    {
        var rect = ScreenRect(shape);
        return new Vector2(rect.End.X + HandleRadius + 6, rect.GetCenter().Y);
    }

    private static bool CanResize(BpmnShape shape) => shape.Kind.IsActivity() || shape.Kind == BpmnElementKind.TextAnnotation;

    private Rect2 ResizeHandle(BpmnShape shape)
    {
        var rect = ScreenRect(shape);
        return new Rect2(rect.End - new Vector2(5, 5), new Vector2(10, 10));
    }

    private bool ResizeHandleLit(BpmnShape shape) => ResizeHandle(shape).Grow(3).HasPoint(_pointer);

    // ---- Drag and drop from the palette ----

    private const string DragTag = "bpmn-element";

    /// <summary>The data of a drag of <paramref name="kind"/> from the palette (a dictionary, so that nothing else is taken for it).</summary>
    public static Variant DragData(BpmnElementKind kind) => new Godot.Collections.Dictionary { ["type"] = DragTag, ["kind"] = (int)kind };

    private static BpmnElementKind? KindOf(Variant data) =>
        data.VariantType == Variant.Type.Dictionary && data.AsGodotDictionary() is { } dictionary
        && dictionary.TryGetValue("type", out var type) && type.AsString() == DragTag
        && dictionary.TryGetValue("kind", out var kind) && Enum.IsDefined((BpmnElementKind)kind.AsInt32())
            ? (BpmnElementKind)kind.AsInt32()
            : null;

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (KindOf(data) is not { } kind)
            return false;
        _dragKind = kind;
        _pointer = atPosition;
        QueueRedraw();
        return true;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        _dragKind = null;
        if (KindOf(data) is not { } kind)
            return;
        var world = ToWorld(atPosition);
        _vm.AddShape(kind, world.X, world.Y);
        _vm.Placing = null;
        GrabFocus();
        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationDragEnd && _dragKind is not null)
        {
            _dragKind = null;
            QueueRedraw();
        }
    }

    // ---- Input ----

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel:
            {
                var factor = wheel.Factor > 0 ? wheel.Factor : 1;
                ZoomBy(wheel.ButtonIndex == MouseButton.WheelUp ? Mathf.Pow(1.12f, factor) : 1 / Mathf.Pow(1.12f, factor), wheel.Position);
                AcceptEvent();
                break;
            }
            case InputEventMagnifyGesture magnify:
                ZoomBy(magnify.Factor, magnify.Position);
                AcceptEvent();
                break;
            case InputEventPanGesture pan:
                _offset -= pan.Delta * 12;
                QueueRedraw();
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true } press:
                Pressed(press);
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: false } release:
                Released(release);
                AcceptEvent();
                break;
            case InputEventMouseMotion motion:
                Moved(motion);
                break;
            case InputEventKey { Pressed: true } key:
                if (Key(key))
                    AcceptEvent();
                break;
        }
    }

    private void Pressed(InputEventMouseButton press)
    {
        GrabFocus();
        _pointer = press.Position;
        if (_overlay.IsOpen)
            _overlay.Close();

        // The pen: a left drag draws around the elements; a right click removes the drawing.
        if (_pen.Active)
        {
            if (press.ButtonIndex == MouseButton.Right)
                ClearPen();
            else if (press.ButtonIndex == MouseButton.Left)
            {
                _pen.Begin(press.Position);
                QueueRedraw();
            }
            return;
        }

        // A double-click on the name of a cluster opens its popup.
        if (press.ButtonIndex == MouseButton.Left && press.DoubleClick && _pen.LabelAt(press.Position) is { } cluster)
        {
            ClusterDoubleClicked?.Invoke(cluster);
            return;
        }

        if (press.ButtonIndex == MouseButton.Right)
        {
            if (_connectFrom is not null)
            {
                CancelConnection();
                return;
            }
            ShowMenu(press.Position);
            return;
        }
        if (press.ButtonIndex == MouseButton.Middle)
        {
            StartPan(press.Position);
            return;
        }
        if (press.ButtonIndex != MouseButton.Left)
            return;

        // The palette: a kind to place.
        if (_vm.Placing is { } kind)
        {
            var world = ToWorld(press.Position);
            _vm.AddShape(kind, world.X, world.Y);
            if (!press.ShiftPressed)
                _vm.Placing = null;
            return;
        }

        // The connect tool: the source, then the target.
        if (_vm.Connecting)
        {
            var clicked = ShapeAt(press.Position);
            if (_connectFrom is null)
            {
                if (clicked is null)
                    return;
                _connectFrom = clicked;
                _connectDrag = false;
                _vm.Selected = clicked;
                _vm.SetStatus($"Connect {clicked.Id}: now click the target", false);
                QueueRedraw();
                return;
            }
            FinishConnection(clicked);
            return;
        }

        if (press.DoubleClick)
        {
            if (ShapeAt(press.Position) is { } toRename)
                _ = RenameAsync(toRename);
            else if (FlowAt(press.Position) is { } flowToRename)
                _ = RenameAsync(flowToRename);
            return;
        }

        // The handles of the element selected.
        if (_vm.Selected is BpmnShape selected)
        {
            if (ConnectHandle(selected).DistanceTo(press.Position) <= HandleRadius + 2)
            {
                _connectFrom = selected;
                _connectDrag = true;
                QueueRedraw();
                return;
            }
            if (CanResize(selected) && ResizeHandleLit(selected))
            {
                _resizing = selected;
                _resizeStart = press.Position;
                _resizeSize = (selected.Width, selected.Height);
                return;
            }
        }

        if (ShapeAt(press.Position) is { } shape)
        {
            _vm.Selected = shape;
            _moving = shape;
            _moved = false;
            _vm.BeginMove();
            return;
        }
        if (FlowAt(press.Position) is { } flow)
        {
            _vm.Selected = flow;
            return;
        }
        _vm.Selected = null;
        StartPan(press.Position);
    }

    private void StartPan(Vector2 at)
    {
        _panning = true;
        _panStart = at;
        _offsetStart = _offset;
    }

    private void Released(InputEventMouseButton release)
    {
        _pointer = release.Position;
        if (_pen.Active && _pen.IsDown && release.ButtonIndex == MouseButton.Left)
        {
            PenLifted();
            return;
        }
        if (_panning)
        {
            _panning = false;
            return;
        }
        if (_moving is not null)
        {
            _moving = null;
            _vm.EndMove();
            return;
        }
        if (_resizing is not null)
        {
            var world = (release.Position - _resizeStart) / _zoom;
            _vm.Resize(_resizing, _resizeSize.Width + world.X, _resizeSize.Height + world.Y);
            _resizing = null;
            return;
        }
        if (_connectFrom is not null && _connectDrag)
        {
            var target = ShapeAt(release.Position);
            if (target is null || ReferenceEquals(target, _connectFrom))
            {
                // A click on the handle without a drag: the connection goes on by clicks, as the connect tool.
                if (release.Position.DistanceTo(ConnectHandle(_connectFrom)) <= HandleRadius + 4)
                {
                    _connectDrag = false;
                    _vm.SetStatus($"Connect {_connectFrom.Id}: click the target (Escape cancels)", false);
                    QueueRedraw();
                    return;
                }
                CancelConnection();
                return;
            }
            FinishConnection(target);
        }
    }

    private void Moved(InputEventMouseMotion motion)
    {
        _pointer = motion.Position;
        if (_pen.Active)
        {
            MouseDefaultCursorShape = CursorShape.Cross;
            if (_pen.Extend(motion.Position))
                QueueRedraw();
            return;
        }
        if (_panning)
        {
            _offset = _offsetStart + motion.Position - _panStart;
            QueueRedraw();
            return;
        }
        if (_moving is not null)
        {
            var delta = motion.Relative / _zoom;
            if (delta.LengthSquared() > 0)
            {
                _moved = true;
                _vm.Move(_moving, delta.X, delta.Y);
            }
            return;
        }
        if (_resizing is not null)
        {
            var world = (motion.Position - _resizeStart) / _zoom;
            _resizing.Width = Math.Max(40, _resizeSize.Width + world.X);
            _resizing.Height = Math.Max(30, _resizeSize.Height + world.Y);
            QueueRedraw();
            return;
        }
        var hovered = ShapeAt(motion.Position);
        var hoveredFlow = hovered is null ? FlowAt(motion.Position) : null;
        var changed = !ReferenceEquals(hovered, _hovered) || !ReferenceEquals(hoveredFlow, _hoveredFlow);
        _hovered = hovered;
        _hoveredFlow = hoveredFlow;
        MouseDefaultCursorShape = _vm.Placing is not null || _vm.Connecting || _connectFrom is not null ? CursorShape.Cross
            : _vm.Selected is BpmnShape s && (ConnectHandle(s).DistanceTo(motion.Position) <= HandleRadius + 2) ? CursorShape.PointingHand
            : _vm.Selected is BpmnShape r && CanResize(r) && ResizeHandleLit(r) ? CursorShape.Fdiagsize
            : hovered is not null ? CursorShape.Move
            : hoveredFlow is not null ? CursorShape.PointingHand
            : CursorShape.Arrow;
        if (changed || _connectFrom is not null || _vm.Placing is not null || _vm.Selected is BpmnShape)
            QueueRedraw();
    }

    private void FinishConnection(BpmnShape? target)
    {
        var source = _connectFrom;
        _connectFrom = null;
        _connectDrag = false;
        if (source is null)
            return;
        if (target is null)
        {
            _vm.SetStatus("Connection cancelled: no element there", false);
            QueueRedraw();
            return;
        }
        if (_vm.Connect(source, target, out var reason) is null)
            _vm.SetStatus(reason, true);
        if (_vm.Connecting)
            _vm.Connecting = false;
        QueueRedraw();
    }

    private void CancelConnection()
    {
        _connectFrom = null;
        _connectDrag = false;
        _vm.SetStatus("Connection cancelled", false);
        QueueRedraw();
    }

    private bool Key(InputEventKey key)
    {
        var command = key.CtrlPressed || key.MetaPressed;
        switch (key.Keycode)
        {
            case Godot.Key.Escape when _pen.Active && _pen.HasDrawing:
                ClearPen();
                return true;
            case Godot.Key.Escape:
                if (_connectFrom is not null)
                    CancelConnection();
                else if (_vm.Placing is not null || _vm.Connecting)
                    _vm.SelectToolCommand.Execute(null);
                else
                    _vm.Selected = null;
                return true;
            case Godot.Key.Delete:
            case Godot.Key.Backspace:
                if (_vm.Selected is { } selected)
                    _vm.Remove(selected);
                return true;
            case Godot.Key.Z when command && key.ShiftPressed:
            case Godot.Key.Y when command:
                _vm.RedoCommand.Execute(null);
                return true;
            case Godot.Key.Z when command:
                _vm.UndoCommand.Execute(null);
                return true;
            case Godot.Key.Left or Godot.Key.Right or Godot.Key.Up or Godot.Key.Down when _vm.Selected is BpmnShape shape:
            {
                var step = key.ShiftPressed ? BpmnWindowViewModel.Grid * 5 : BpmnWindowViewModel.Grid;
                var (dx, dy) = key.Keycode switch
                {
                    Godot.Key.Left => (-step, 0f),
                    Godot.Key.Right => (step, 0f),
                    Godot.Key.Up => (0f, -step),
                    _ => (0f, step)
                };
                _vm.BeginMove();
                _vm.Move(shape, dx, dy);
                _vm.EndMove();
                return true;
            }
            case Godot.Key.Enter or Godot.Key.KpEnter or Godot.Key.F2 when _vm.Selected is { } toRename:
                _ = RenameAsync(toRename);
                return true;
            case Godot.Key.Key0 when command:
                ResetView();
                return true;
            case Godot.Key.Key1 when command:
                Fit();
                return true;
        }
        return false;
    }

    private async System.Threading.Tasks.Task RenameAsync(object element)
    {
        var (title, current) = element switch
        {
            BpmnShape shape => ($"{shape.Kind.Title()} {shape.Id}", shape.Name),
            BpmnFlow flow => (flow.Kind == BpmnFlowKind.SequenceFlow ? $"Sequence flow {flow.Id}" : $"Association {flow.Id}", flow.Name),
            _ => ("Name", string.Empty)
        };
        var name = await Dialogs.Ask(title, element is BpmnShape { Kind: BpmnElementKind.TextAnnotation } ? "The text of the annotation" : "The name", current, element is BpmnShape { Kind: BpmnElementKind.TextAnnotation });
        if (name is null)
            return;
        _vm.Rename(element, name.Trim());
        QueueRedraw();
    }

    /// <summary>
    /// The right-click menu: on an element or a flow, its actions (annotate it too); on the background, the elements to
    /// add there (and annotate the whole diagram).
    /// </summary>
    private void ShowMenu(Vector2 at)
    {
        var items = new List<Overlay.MenuItem?>();
        var shape = ShapeAt(at);
        var flow = shape is null ? FlowAt(at) : null;
        if (shape is not null)
        {
            _vm.Selected = shape;
            items.Add(new Overlay.MenuItem($"{shape.Kind.Title()} · {shape.Id}", false, null));
            items.Add(new Overlay.MenuItem("Rename…", true, () => _ = RenameAsync(shape), "⏎"));
            items.Add(new Overlay.MenuItem("Connect from here…", true, () =>
            {
                _connectFrom = shape;
                _connectDrag = false;
                _vm.SetStatus($"Connect {shape.Id}: click the target (Escape cancels)", false);
                QueueRedraw();
            }));
            items.Add(new Overlay.MenuItem(Data.Annotations.MenuText, true, () => Annotate(shape.Id, shape.Name, shape.Kind.Title())));
            items.Add(null);
            items.Add(new Overlay.MenuItem("Delete", true, () => _vm.Remove(shape), "⌫"));
        }
        else if (flow is not null)
        {
            _vm.Selected = flow;
            items.Add(new Overlay.MenuItem(flow.Kind == BpmnFlowKind.SequenceFlow ? $"Sequence flow · {flow.Id}" : $"Association · {flow.Id}", false, null));
            if (flow.Kind == BpmnFlowKind.SequenceFlow)
                items.Add(new Overlay.MenuItem("Name (condition)…", true, () => _ = RenameAsync(flow), "⏎"));
            items.Add(new Overlay.MenuItem(Data.Annotations.MenuText, true, () =>
                Annotate(flow.Id, flow.Name, flow.Kind == BpmnFlowKind.SequenceFlow ? "Sequence flow" : "Association")));
            items.Add(null);
            items.Add(new Overlay.MenuItem("Delete", true, () => _vm.Remove(flow), "⌫"));
        }
        else
        {
            var world = ToWorld(at);
            items.Add(new Overlay.MenuItem("Add here", false, null));
            string? group = null;
            foreach (var kind in BpmnElementKinds.All)
            {
                if (kind.Group() != group)
                {
                    group = kind.Group();
                    items.Add(new Overlay.MenuItem(group, false, null));
                }
                var k = kind;
                items.Add(new Overlay.MenuItem(kind.Title(), true, () => _vm.AddShape(k, world.X, world.Y), Indent: 1));
            }
            items.Add(null);
            items.Add(new Overlay.MenuItem("Fit the diagram", true, Fit, "⌘1"));
            items.Add(new Overlay.MenuItem("Zoom 100 %", true, ResetView, "⌘0"));
            items.Add(null);
            items.Add(new Overlay.MenuItem(Data.Annotations.MenuText, true, () => Annotate(null, null, null)));
        }
        _overlay.Menu(items, GetGlobalPosition() - _overlay.GetGlobalPosition() + at);
    }

    /// <summary>
    /// Annotates the element or flow <paramref name="id"/> of the diagram (its name, else its kind, as title), or the
    /// whole diagram when <paramref name="id"/> is null.
    /// </summary>
    private void Annotate(string? id, string? name, string? kind)
    {
        var title = id is null ? $"{_vm.EditorTitle} · {_vm.FileName}" : string.IsNullOrWhiteSpace(name) ? $"{kind} · {id}" : name;
        Data.Annotations.Open(this, new AnnotationTarget(AnnotationTargetTypes.DiagramElement,
            id is null ? _vm.FileName : $"{_vm.FileName}#{id}", title, string.IsNullOrWhiteSpace(name) ? null : name));
    }
}
