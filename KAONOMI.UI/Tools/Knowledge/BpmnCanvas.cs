using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The canvas of <c>/bpmnai</c> (and <c>/infra</c>): the diagram drawn flat in the BPMN 2.0 notation (events as circles:
/// thin for a start, thick for an end, with the marker of their kind; tasks as rounded rectangles with the marker of
/// their type, a collapsed sub-process with its ⊞; gateways as diamonds marked ✕, + or ○; a data object as a page with a
/// folded corner; the AI elements with their pictogram; an annotation as an open bracket; sequence flows as arrows,
/// associations as dotted lines), on a grid.
/// <para>The pointer: a click selects, a drag moves an element (snapped to the grid) or pans the background; the wheel
/// zooms. The element selected shows an arrow handle on its right: dragged onto another element, it joins them; a
/// square handle at the bottom right resizes a task, a sub-process or an annotation. With an element dragged from the
/// palette, it is placed where it is dropped; with a kind chosen in the palette, a click places it (Shift kept:
/// several); with the connect tool, a click on the source then on the target joins them. A double-click renames; a
/// right click opens the menu; Delete removes the selection; the arrows nudge it; Escape cancels the tool;
/// Cmd/Ctrl+Z undoes, Shift+Cmd/Ctrl+Z (or Ctrl+Y) redoes, Ctrl+0 100 %, Ctrl+1 fits.</para>
/// </summary>
public sealed class BpmnCanvas : Control, IClusterCanvas
{
    private const double HandleRadius = 9;
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4;
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor ResizeCursor = new(StandardCursorType.BottomRightCorner);

    private readonly BpmnWindowViewModel _vm;

    private double _zoom = 1;
    private Vector _offset = new(40, 40);

    private BpmnShape? _hovered;
    private BpmnFlow? _hoveredFlow;
    private Point _pointer;
    private bool _pointerInside;

    // The drags.
    private BpmnShape? _moving;
    private Point _moveLast;
    private bool _panning;
    private Point _panStart;
    private Vector _offsetStart;
    private BpmnShape? _connectFrom;
    private bool _connectDrag;
    private BpmnShape? _resizing;
    private Point _resizeStart;
    private (float Width, float Height) _resizeSize;

    /// <summary>The kind dragged from the palette over the canvas (its ghost follows the pointer), null when none.</summary>
    private BpmnElementKind? _dragKind;

    // ---- The pen of the clusters, as in the graph of relations ----
    private readonly ClusterPen _pen = new();

    public BpmnCanvas(BpmnWindowViewModel vm)
    {
        _vm = vm;
        Focusable = true;
        ClipToBounds = true;
        Action changed = () => Ui.OnMain(InvalidateVisual);
        vm.Changed += changed;
        KnowledgeUi.OnGone(this, () => vm.Changed -= changed);
        Ui.When(this, vm, InvalidateVisual, nameof(vm.Selected), nameof(vm.Placing), nameof(vm.Connecting));
    }

    /// <summary>Raised when the zoom changes (the toolbar shows it).</summary>
    public event Action? ViewChanged;

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
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    public event Action<IReadOnlyList<ClusterMemberEntry>, IReadOnlyList<Point>>? ClusterDrawn;

    /// <inheritdoc />
    public event Action<ClusterEntry>? ClusterDoubleClicked;

    /// <inheritdoc />
    public void SetClusters(IReadOnlyList<ClusterEntry> clusters)
    {
        _pen.Clusters = clusters;
        InvalidateVisual();
    }

    /// <inheritdoc />
    public void ClearPen()
    {
        _pen.Clear();
        InvalidateVisual();
    }

    /// <summary>The id of an element in a cluster: the file of the diagram and the id of the element.</summary>
    private string MemberId(BpmnShape shape) => $"{_vm.FileName}#{shape.Id}";

    /// <summary>The box of a member of a cluster in the diagram (null when it is not one of its elements).</summary>
    private Rect? BoxOf(ClusterMemberEntry member) =>
        _vm.Diagram.Shapes.FirstOrDefault(s => MemberId(s) == member.ObjectId) is { } shape ? new Rect(shape.X, shape.Y, shape.Width, shape.Height) : null;

    /// <summary>The drawing of the pen closed: the elements whose centre is inside it make a cluster (none: the drawing goes).</summary>
    private void PenLifted()
    {
        var inside = _pen.Lift()
            ? _vm.Diagram.Shapes.Where(s => _pen.Contains(ScreenRect(s).Center)).ToList()
            : [];
        InvalidateVisual();
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

    private static Color Stroke => Paint.TextBright;
    private static Color Fill => Paint.IsLight ? Colors.White : Paint.Field;
    private static Color Selection => Paint.Cyan;
    private static Color Hover => Paint.Amber;

    // ---- The view ----

    private Point ToScreen(double x, double y) => new(x * _zoom + _offset.X, y * _zoom + _offset.Y);
    private Point ToScreen(Point world) => ToScreen(world.X, world.Y);
    private Point ToWorld(Point screen) => new((screen.X - _offset.X) / _zoom, (screen.Y - _offset.Y) / _zoom);
    private Rect ScreenRect(BpmnShape shape) => new(ToScreen(shape.X, shape.Y), new Size(shape.Width * _zoom, shape.Height * _zoom));

    private void ZoomBy(double factor, Point at)
    {
        var zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(zoom - _zoom) < 0.0001)
            return;
        var world = ToWorld(at);
        _zoom = zoom;
        _offset = new Vector(at.X - world.X * _zoom, at.Y - world.Y * _zoom);
        Redraw();
    }

    private void Redraw()
    {
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>The zoom back to 100 %, the origin near the top left.</summary>
    public void ResetView()
    {
        _zoom = 1;
        _offset = new Vector(40, 40);
        Redraw();
    }

    /// <summary>The whole diagram in the canvas.</summary>
    public void Fit()
    {
        var shapes = _vm.Diagram.Shapes;
        if (shapes.Count == 0)
        {
            ResetView();
            return;
        }
        var left = shapes.Min(s => s.X) - 40;
        var top = shapes.Min(s => s.Y) - 40;
        var right = shapes.Max(s => s.X + s.Width) + 40;
        var bottom = shapes.Max(s => s.Y + s.Height) + 60;
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        var size = Bounds.Size;
        _zoom = Math.Clamp(Math.Min(size.Width / width, size.Height / height), MinZoom, MaxZoom);
        _offset = new Vector((size.Width - width * _zoom) / 2 - left * _zoom, (size.Height - height * _zoom) / 2 - top * _zoom);
        Redraw();
    }

    /// <summary>The zoom shown in the toolbar.</summary>
    public int ZoomPercent => (int)Math.Round(_zoom * 100);

    // ---- Drawing ----

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        Paint.Rect(context, new Rect(size), Paint.Background);
        DrawGrid(context, size);
        var diagram = _vm.Diagram;
        _pen.DrawClusters(context, BoxOf, ToScreen, _zoom);
        foreach (var flow in diagram.Flows)
            DrawFlow(context, diagram, flow);
        foreach (var shape in diagram.Shapes)
            DrawShape(context, shape);
        DrawHandles(context);
        DrawConnection(context);
        DrawGhost(context);
        _pen.DrawPen(context);
        if (diagram.Shapes.Count == 0 && _vm.Placing is null)
        {
            var text = "Pick an element on the left and click here to place it";
            var measured = Paint.Measure(text, 14);
            Paint.Write(context, text, new Point((size.Width - measured.Width) / 2, size.Height / 2 - measured.Height / 2), 14, Paint.Muted);
        }
    }

    private void DrawGrid(DrawingContext context, Size size)
    {
        var step = BpmnWindowViewModel.Grid * 2 * _zoom;
        while (step < 14)
            step *= 2;
        var color = Paint.Alpha(Paint.Edge, 0.2);
        for (var x = Paint.PosMod(_offset.X, step); x < size.Width; x += step)
            Paint.Line(context, new Point(x, 0), new Point(x, size.Height), color);
        for (var y = Paint.PosMod(_offset.Y, step); y < size.Height; y += step)
            Paint.Line(context, new Point(0, y), new Point(size.Width, y), color);
    }

    private void DrawShape(DrawingContext context, BpmnShape shape)
    {
        var selected = ReferenceEquals(_vm.Selected, shape);
        var hovered = ReferenceEquals(_hovered, shape) && !selected;
        var stroke = selected ? Selection : hovered ? Hover : Stroke;
        var rect = ScreenRect(shape);
        var center = rect.Center;
        var z = _zoom;

        switch (shape.Kind)
        {
            case BpmnElementKind.StartEvent:
            case BpmnElementKind.MessageStartEvent:
            case BpmnElementKind.TimerStartEvent:
            case BpmnElementKind.EventCollector:
            case BpmnElementKind.EndEvent:
            {
                var radius = Math.Min(rect.Width, rect.Height) / 2;
                Paint.Circle(context, center, radius, Fill);
                var width = shape.Kind == BpmnElementKind.EndEvent ? Math.Max(2.5, 4 * z) : Math.Max(1, 1.5 * z);
                Paint.Ring(context, center, radius - width / 2, stroke, width);
                if (shape.Kind == BpmnElementKind.EventCollector)
                    DrawFunnel(context, center, radius, stroke);
                else if (shape.Kind == BpmnElementKind.MessageStartEvent)
                    DrawEnvelope(context, center, radius, stroke);
                else if (shape.Kind == BpmnElementKind.TimerStartEvent)
                    DrawClock(context, center, radius, stroke);
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Task:
            case BpmnElementKind.UserTask:
            case BpmnElementKind.AgentTask:
            case BpmnElementKind.VirtualAgentTask:
            case BpmnElementKind.SubProcess:
            {
                var border = Math.Max(1, Math.Round((shape.Kind == BpmnElementKind.SubProcess ? 2.5 : 1.5) * z));
                Paint.Round(context, rect, Math.Round(10 * z), Fill, stroke, border);
                var marker = shape.Kind switch { BpmnElementKind.UserTask => "👤", BpmnElementKind.AgentTask => "🤖", BpmnElementKind.VirtualAgentTask => "👾", _ => null };
                if (marker is not null)
                    Paint.Write(context, marker, rect.TopLeft + new Vector(6 * z, 4 * z), 12 * z, stroke);
                var nameSize = Paint.Measure(shape.Name, 12 * z, Math.Max(1, rect.Width - 12 * z), 4);
                Paint.Write(context, shape.Name, new Point(rect.X + 6 * z, center.Y - nameSize.Height / 2 - (shape.Kind == BpmnElementKind.SubProcess ? 4 * z : 0)),
                    12 * z, Stroke, Math.Max(1, rect.Width - 12 * z), 4, alignment: TextAlignment.Center);
                if (shape.Kind == BpmnElementKind.SubProcess)
                {
                    // The ⊞ of a collapsed sub-process, at the bottom centre.
                    var side = 12 * z;
                    var box = new Rect(center.X - side / 2, rect.Bottom - side - 4 * z, side, side);
                    var w = Math.Max(1, 1.2 * z);
                    Paint.Outline(context, box, stroke, w);
                    Paint.Line(context, new Point(box.X + 3 * z, box.Center.Y), new Point(box.Right - 3 * z, box.Center.Y), stroke, w);
                    Paint.Line(context, new Point(box.Center.X, box.Y + 3 * z), new Point(box.Center.X, box.Bottom - 3 * z), stroke, w);
                }
                break;
            }
            case BpmnElementKind.ExclusiveGateway:
            case BpmnElementKind.ParallelGateway:
            case BpmnElementKind.InclusiveGateway:
            {
                var hw = rect.Width / 2;
                var hh = rect.Height / 2;
                Point[] diamond = [center + new Vector(0, -hh), center + new Vector(hw, 0), center + new Vector(0, hh), center + new Vector(-hw, 0)];
                Paint.Polygon(context, diamond, Fill);
                Paint.Polyline(context, diamond, stroke, Math.Max(1, 1.5 * z), closed: true);
                var width = Math.Max(1.5, 2.5 * z);
                var m = Math.Min(hw, hh) * 0.4;
                switch (shape.Kind)
                {
                    case BpmnElementKind.ExclusiveGateway:
                        Paint.Line(context, center + new Vector(-m, -m), center + new Vector(m, m), stroke, width);
                        Paint.Line(context, center + new Vector(-m, m), center + new Vector(m, -m), stroke, width);
                        break;
                    case BpmnElementKind.ParallelGateway:
                        Paint.Line(context, center + new Vector(-m * 1.2, 0), center + new Vector(m * 1.2, 0), stroke, width);
                        Paint.Line(context, center + new Vector(0, -m * 1.2), center + new Vector(0, m * 1.2), stroke, width);
                        break;
                    default:
                        Paint.Ring(context, center, m * 1.1, stroke, width);
                        break;
                }
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            }
            case BpmnElementKind.DataObject:
            {
                var fold = Math.Min(rect.Width, rect.Height) * 0.3;
                Point[] page = [rect.TopLeft, new Point(rect.Right - fold, rect.Y), new Point(rect.Right, rect.Y + fold), rect.BottomRight, rect.BottomLeft];
                Paint.Polygon(context, page, Fill);
                Paint.Polyline(context, page, stroke, Math.Max(1, 1.5 * z), closed: true);
                Paint.Polyline(context, [new Point(rect.Right - fold, rect.Y), new Point(rect.Right - fold, rect.Y + fold), new Point(rect.Right, rect.Y + fold)], stroke, Math.Max(1, 1.2 * z));
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Database:
                // A database: a cylinder.
                DrawCylinder(context, rect, stroke);
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            case BpmnElementKind.SearchEngine:
            {
                // The search engine: a cylinder with a magnifying glass over its corner.
                DrawCylinder(context, rect, stroke);
                DrawGlass(context, new Point(rect.X + rect.Width * 0.7, rect.Y + rect.Height * 0.62), rect.Width * 0.17, stroke);
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            }
            case BpmnElementKind.VectorDatabase:
            {
                // A database of vectors: a cylinder with three arrows (the vectors) on its body.
                DrawCylinder(context, rect, stroke);
                var width = Math.Max(1, 1.2 * z);
                var left = rect.X + rect.Width * 0.26;
                var right = rect.X + rect.Width * 0.74;
                for (var i = 0; i < 3; i++)
                {
                    var y = rect.Y + rect.Height * (0.46 + 0.15 * i);
                    Paint.Line(context, new Point(left, y), new Point(right, y), stroke, width);
                    Paint.Polyline(context, [new Point(right - 4 * z, y - 3 * z), new Point(right, y), new Point(right - 4 * z, y + 3 * z)], stroke, width);
                }
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Rag:
            {
                // Retrieval: a page with a magnifying glass over its corner.
                var width = Math.Max(1, 1.5 * z);
                var page = new Rect(rect.X + rect.Width * 0.08, rect.Y, rect.Width * 0.68, rect.Height * 0.84);
                var fold = page.Width * 0.28;
                Point[] sheet = [page.TopLeft, new Point(page.Right - fold, page.Y), new Point(page.Right, page.Y + fold), page.BottomRight, page.BottomLeft];
                Paint.Polygon(context, sheet, Fill);
                Paint.Polyline(context, sheet, stroke, width, closed: true);
                for (var i = 0; i < 3; i++)
                {
                    var y = page.Y + page.Height * (0.42 + 0.16 * i);
                    Paint.Line(context, new Point(page.X + 5 * z, y), new Point(page.Right - 5 * z, y), Paint.Alpha(stroke, 0.7), Math.Max(1, z));
                }
                DrawGlass(context, new Point(rect.X + rect.Width * 0.72, rect.Y + rect.Height * 0.66), rect.Width * 0.16, stroke);
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Llm:
            {
                // A language model: a chip with its pins, LLM written on it, a spark at its corner.
                var width = Math.Max(1, 1.5 * z);
                var chip = Paint.Grow(rect, -rect.Width * 0.14);
                Paint.Rect(context, chip, Fill);
                Paint.Outline(context, chip, stroke, width);
                for (var i = 0; i < 4; i++)
                {
                    var t = chip.X + chip.Width * (0.2 + 0.2 * i);
                    Paint.Line(context, new Point(t, chip.Y), new Point(t, rect.Y + 1), stroke, width);
                    Paint.Line(context, new Point(t, chip.Bottom), new Point(t, rect.Bottom - 1), stroke, width);
                    var s = chip.Y + chip.Height * (0.2 + 0.2 * i);
                    Paint.Line(context, new Point(chip.X, s), new Point(rect.X + 1, s), stroke, width);
                    Paint.Line(context, new Point(chip.Right, s), new Point(rect.Right - 1, s), stroke, width);
                }
                DrawCenteredText(context, "LLM", Paint.Mono, 11 * z, chip, stroke);
                DrawSpark(context, new Point(chip.Right - 4 * z, chip.Y + 4 * z), 4 * z, stroke);
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Model:
            {
                // A model: a cube in perspective (its front, its top, its side).
                var width = Math.Max(1, 1.5 * z);
                var d = rect.Width * 0.26;
                var front = new Rect(rect.X, rect.Y + d, rect.Width - d, rect.Height - d);
                Point[] top = [front.TopLeft, front.TopLeft + new Vector(d, -d), new Point(front.Right + d, front.Y - d), new Point(front.Right, front.Y)];
                Point[] sideFace = [new Point(front.Right, front.Y), new Point(front.Right + d, front.Y - d), new Point(front.Right + d, front.Bottom - d), front.BottomRight];
                Paint.Rect(context, front, Fill);
                Paint.Polygon(context, top, Paint.Alpha(stroke, 0.18));
                Paint.Polygon(context, sideFace, Paint.Alpha(stroke, 0.32));
                Paint.Outline(context, front, stroke, width);
                Paint.Polyline(context, top, stroke, width, closed: true);
                Paint.Polyline(context, sideFace, stroke, width, closed: true);
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            }
            case BpmnElementKind.RestApi:
            {
                // An API: braces around REST, in a rounded box.
                Paint.Round(context, rect, Math.Round(8 * z), Fill, stroke, Math.Max(1, Math.Round(1.5 * z)));
                DrawCenteredText(context, "{ }", Paint.Mono, 20 * z, new Rect(rect.X, rect.Y, rect.Width, rect.Height * 0.68), stroke);
                DrawCenteredText(context, "REST", Paint.Mono, 8 * z, new Rect(rect.X, rect.Y + rect.Height * 0.62, rect.Width, rect.Height * 0.3), stroke);
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Mcp:
            {
                // A connector: a plug (its body, two prongs, its cable).
                var width = Math.Max(1, 1.5 * z);
                var body = new Rect(rect.X + rect.Width * 0.22, rect.Y + rect.Height * 0.3, rect.Width * 0.56, rect.Height * 0.4);
                Paint.Round(context, body, Math.Round(5 * z), Fill, stroke, Math.Max(1, Math.Round(1.5 * z)));
                foreach (var t in new[] { 0.32, 0.68 })
                {
                    var x = body.X + body.Width * t;
                    Paint.Line(context, new Point(x, body.Y), new Point(x, rect.Y + rect.Height * 0.08), stroke, Math.Max(1.5, 2.5 * z));
                }
                var cableTop = new Point(center.X, body.Bottom);
                var cableEnd = new Point(center.X + rect.Width * 0.18, rect.Bottom - 2 * z);
                var c1 = cableTop + new Vector(0, rect.Height * 0.14);
                var c2 = cableEnd - new Vector(rect.Width * 0.18, rect.Height * 0.08);
                var cable = new Point[13];
                for (var i = 0; i <= 12; i++)
                {
                    var t = i / 12.0;
                    var u = 1 - t;
                    cable[i] = new Point(u * u * u * cableTop.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * cableEnd.X,
                        u * u * u * cableTop.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * cableEnd.Y);
                }
                Paint.Polyline(context, cable, stroke, width);
                DrawCenteredText(context, "MCP", Paint.Mono, 8 * z, body, stroke);
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            }
            case BpmnElementKind.Graphbase:
            {
                // A graph: nodes (small discs) joined by edges, in a rounded box.
                Paint.Round(context, rect, Math.Round(8 * z), Fill, stroke, Math.Max(1, Math.Round(1.5 * z)));
                var w = rect.Width;
                var h = rect.Height;
                Point[] nodes =
                [
                    rect.TopLeft + new Vector(w * 0.5, h * 0.22), rect.TopLeft + new Vector(w * 0.22, h * 0.5), rect.TopLeft + new Vector(w * 0.78, h * 0.5),
                    rect.TopLeft + new Vector(w * 0.35, h * 0.8), rect.TopLeft + new Vector(w * 0.68, h * 0.8)
                ];
                var width = Math.Max(1, 1.2 * z);
                foreach (var (a, b) in new[] { (0, 1), (0, 2), (1, 3), (2, 4), (3, 4), (1, 2) })
                    Paint.Line(context, nodes[a], nodes[b], stroke, width);
                var dot = Math.Max(2, 3.2 * z);
                foreach (var node in nodes)
                {
                    Paint.Circle(context, node, dot, stroke);
                    Paint.Circle(context, node, dot * 0.45, Fill);
                }
                DrawLabelBelow(context, shape, rect, stroke);
                break;
            }
            case BpmnElementKind.TextAnnotation:
            {
                var width = Math.Max(1, 1.5 * z);
                var ear = Math.Min(14 * z, rect.Width / 3);
                Paint.Polyline(context, [new Point(rect.X + ear, rect.Y), rect.TopLeft, rect.BottomLeft, new Point(rect.X + ear, rect.Bottom)], stroke, width);
                Paint.Write(context, shape.Name, rect.TopLeft + new Vector(6 * z, 4 * z), 11 * z, Stroke, Math.Max(1, rect.Width - 10 * z), 6);
                break;
            }
            default:
                if (shape.Kind.IsInfra())
                    DrawDevice(context, shape, rect, stroke);
                break;
        }
    }

    /// <summary>
    /// An element of an infrastructure (/infra): a card with its emoji in its upper part and its short word (PC, DGX,
    /// PI…) in its lower part, its name under the card.
    /// </summary>
    private void DrawDevice(DrawingContext context, BpmnShape shape, Rect rect, Color stroke)
    {
        var z = _zoom;
        Paint.Round(context, rect, Math.Round(12 * z), Fill, stroke, Math.Max(1, Math.Round(1.5 * z)));
        var iconSize = Math.Max(1, Math.Min(rect.Width, rect.Height) * 0.42);
        var icon = Paint.Measure(shape.Kind.Icon(), iconSize, rect.Width);
        Paint.Write(context, shape.Kind.Icon(), new Point(rect.X, rect.Y + rect.Height * 0.38 - icon.Height / 2), iconSize, Colors.White, rect.Width, 1, alignment: TextAlignment.Center);
        var badgeWidth = Math.Max(1, rect.Width - 6 * z);
        var badge = Paint.Measure(shape.Kind.Badge(), 9 * z, badgeWidth, 1, Paint.Mono);
        Paint.Write(context, shape.Kind.Badge(), new Point(rect.X + 3 * z, rect.Bottom - badge.Height - 6 * z), 9 * z, Paint.Alpha(stroke, 0.85), badgeWidth, 1, Paint.Mono, TextAlignment.Center);
        DrawLabelBelow(context, shape, rect, stroke);
    }

    /// <summary>A magnifying glass: its lens at <paramref name="glass"/>, its handle going down to the right.</summary>
    private void DrawGlass(DrawingContext context, Point glass, double radius, Color stroke)
    {
        Paint.Circle(context, glass, radius, Fill);
        Paint.Ring(context, glass, radius, stroke, Math.Max(1.5, 2 * _zoom));
        Paint.Line(context, glass + new Vector(radius * 0.7, radius * 0.7), glass + new Vector(radius * 1.9, radius * 1.9), stroke, Math.Max(1.5, 2.5 * _zoom));
    }

    /// <summary>A database: a cylinder (an ellipse on top, the sides, the bottom arc, a second arc a little below the top).</summary>
    private void DrawCylinder(DrawingContext context, Rect rect, Color stroke)
    {
        var z = _zoom;
        var center = rect.Center;
        var width = Math.Max(1, 1.5 * z);
        var ry = rect.Height * 0.16;
        var rx = rect.Width / 2;
        var top = new Point(center.X, rect.Y + ry);
        var bottom = new Point(center.X, rect.Bottom - ry);
        var body = new List<Point>();
        for (var i = 0; i <= 24; i++)
            body.Add(top + new Vector(Math.Cos(Math.PI + Math.PI * i / 24) * rx, Math.Sin(Math.PI + Math.PI * i / 24) * ry));
        for (var i = 0; i <= 24; i++)
            body.Add(bottom + new Vector(Math.Cos(Math.PI * i / 24) * rx, Math.Sin(Math.PI * i / 24) * ry));
        Paint.Polygon(context, body, Fill);
        Paint.Polyline(context, body, stroke, width, closed: true);
        context.DrawEllipse(null, Paint.Pen(stroke, width), top, rx, ry);
        Paint.Polyline(context, Ellipse(top + new Vector(0, ry * 1.3), rx, ry).Skip(24).ToList(), stroke, Math.Max(1, 1.1 * z));
    }

    /// <summary>The points of an ellipse (the top half first, then the bottom half), closed.</summary>
    private static Point[] Ellipse(Point center, double rx, double ry)
    {
        var points = new Point[49];
        for (var i = 0; i <= 48; i++)
            points[i] = center + new Vector(Math.Cos(Math.PI + 2 * Math.PI * i / 48) * rx, Math.Sin(Math.PI + 2 * Math.PI * i / 48) * ry);
        return points;
    }

    /// <summary>A text centred in <paramref name="box"/>.</summary>
    private static void DrawCenteredText(DrawingContext context, string text, Typeface font, double fontSize, Rect box, Color color)
    {
        var size = Paint.Measure(text, fontSize, Math.Max(1, box.Width), 1, font);
        Paint.Write(context, text, new Point(box.X, box.Center.Y - size.Height / 2), fontSize, color, Math.Max(1, box.Width), 1, font, TextAlignment.Center);
    }

    /// <summary>A four-pointed spark.</summary>
    private static void DrawSpark(DrawingContext context, Point at, double size, Color color) =>
        Paint.Polygon(context, [at + new Vector(0, -size), at + new Vector(size * 0.3, -size * 0.3), at + new Vector(size, 0), at + new Vector(size * 0.3, size * 0.3),
            at + new Vector(0, size), at + new Vector(-size * 0.3, size * 0.3), at + new Vector(-size, 0), at + new Vector(-size * 0.3, -size * 0.3)], color);

    /// <summary>The marker of an event collector: a funnel in the circle, three events (dots) falling into it.</summary>
    private void DrawFunnel(DrawingContext context, Point center, double radius, Color color)
    {
        var width = Math.Max(1, 1.2 * _zoom);
        var top = center.Y - radius * 0.2;
        var neck = center.Y + radius * 0.25;
        var bottom = center.Y + radius * 0.6;
        var wide = radius * 0.55;
        var narrow = radius * 0.12;
        Paint.Polyline(context, [new Point(center.X - wide, top), new Point(center.X + wide, top), new Point(center.X + narrow, neck), new Point(center.X + narrow, bottom),
            new Point(center.X - narrow, bottom), new Point(center.X - narrow, neck), new Point(center.X - wide, top)], color, width);
        var dot = Math.Max(1.2, 1.8 * _zoom);
        Paint.Circle(context, new Point(center.X - radius * 0.3, center.Y - radius * 0.55), dot, color);
        Paint.Circle(context, new Point(center.X, center.Y - radius * 0.68), dot, color);
        Paint.Circle(context, new Point(center.X + radius * 0.3, center.Y - radius * 0.5), dot, color);
    }

    /// <summary>The marker of a message event: an envelope in the circle.</summary>
    private void DrawEnvelope(DrawingContext context, Point center, double radius, Color color)
    {
        var hw = radius * 0.5;
        var hh = radius * 0.34;
        var width = Math.Max(1, 1.2 * _zoom);
        var box = new Rect(center.X - hw, center.Y - hh, hw * 2, hh * 2);
        Paint.Outline(context, box, color, width);
        // The flap: from the two top corners to the middle.
        Paint.Polyline(context, [box.TopLeft, new Point(center.X, center.Y + hh * 0.15), box.TopRight], color, width);
    }

    /// <summary>The marker of a timer event: a clock face with its hands in the circle.</summary>
    private void DrawClock(DrawingContext context, Point center, double radius, Color color)
    {
        var r = radius * 0.6;
        var width = Math.Max(1, 1.2 * _zoom);
        Paint.Ring(context, center, r, color, width);
        for (var i = 0; i < 12; i++)
        {
            var angle = i * 2 * Math.PI / 12;
            var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
            Paint.Line(context, center + direction * r * 0.8, center + direction * r, color, width);
        }
        // Ten past ten, as on the clocks of the shops.
        Paint.Line(context, center, center + new Vector(Math.Cos(-Math.PI / 3), Math.Sin(-Math.PI / 3)) * r * 0.55, color, width);
        Paint.Line(context, center, center + new Vector(Math.Cos(-Math.PI / 2 + Math.PI / 6), Math.Sin(-Math.PI / 2 + Math.PI / 6)) * r * 0.8, color, width);
    }

    /// <summary>The name of an event, a gateway or a data object, centred under it.</summary>
    private void DrawLabelBelow(DrawingContext context, BpmnShape shape, Rect rect, Color color)
    {
        if (string.IsNullOrWhiteSpace(shape.Name))
            return;
        var width = 120 * _zoom;
        Paint.Write(context, shape.Name, new Point(rect.Center.X - width / 2, rect.Bottom + 4 * _zoom), 11 * _zoom,
            ReferenceEquals(_vm.Selected, shape) ? color : Stroke, width, 2, alignment: TextAlignment.Center);
    }

    private void DrawFlow(DrawingContext context, BpmnDiagram diagram, BpmnFlow flow)
    {
        var source = diagram.Shape(flow.SourceId);
        var target = diagram.Shape(flow.TargetId);
        if (source is null || target is null)
            return;
        var (from, to) = Ends(source, target);
        var selected = ReferenceEquals(_vm.Selected, flow);
        var hovered = ReferenceEquals(_hoveredFlow, flow) && !selected;
        var color = selected ? Selection : hovered ? Hover : Stroke;
        var width = Math.Max(1, (selected ? 2.5 : 1.5) * _zoom);
        if (flow.Kind == BpmnFlowKind.Association)
        {
            Paint.Dashed(context, from, to, color, width, Math.Max(3, 5 * _zoom));
            return;
        }
        var unit = Paint.Normalized(to - from);
        var arrow = Math.Max(7, 11 * _zoom);
        var tail = to - unit * arrow;
        Paint.Line(context, from, tail, color, width);
        var side = new Vector(-unit.Y, unit.X) * arrow * 0.45;
        Paint.Polygon(context, [to, tail + side, tail - side], color);
        if (!string.IsNullOrWhiteSpace(flow.Name))
        {
            var boxWidth = 140 * _zoom;
            var size = Paint.Measure(flow.Name, 11 * _zoom, boxWidth, 2);
            var middle = Paint.Lerp(from, to, 0.5);
            var box = new Rect(middle.X - size.Width / 2 - 3, middle.Y - size.Height - 6 * _zoom, size.Width + 6, size.Height + 2);
            Paint.Rect(context, box, Paint.Alpha(Paint.Background, 0.85));
            Paint.Write(context, flow.Name, new Point(middle.X - boxWidth / 2, box.Y + 1), 11 * _zoom, selected ? color : Stroke, boxWidth, 2, alignment: TextAlignment.Center);
        }
    }

    /// <summary>The two ends of a flow on the screen: on the borders of its elements.</summary>
    private (Point From, Point To) Ends(BpmnShape source, BpmnShape target)
    {
        var from = BpmnGeometry.Anchor(source, target.CenterX, target.CenterY);
        var to = BpmnGeometry.Anchor(target, source.CenterX, source.CenterY);
        return (ToScreen(from.X, from.Y), ToScreen(to.X, to.Y));
    }

    /// <summary>The handles of the element selected: a dashed frame, the arrow to join it, the square to resize it.</summary>
    private void DrawHandles(DrawingContext context)
    {
        if (_vm.Selected is not BpmnShape shape)
            return;
        Paint.DashedRect(context, Paint.Grow(ScreenRect(shape), 4), Paint.Alpha(Selection, 0.8), 1, 4);

        var handle = ConnectHandle(shape);
        var lit = Paint.Distance(handle, _pointer) <= HandleRadius + 2;
        Paint.Circle(context, handle, HandleRadius, lit ? Selection : Paint.PanelSolid);
        Paint.Ring(context, handle, HandleRadius, Selection, 1.5);
        var arrowColor = lit ? Paint.PanelSolid : Selection;
        Paint.Line(context, handle + new Vector(-4, 0), handle + new Vector(4, 0), arrowColor, 1.5);
        Paint.Polyline(context, [handle + new Vector(1, -3), handle + new Vector(4, 0), handle + new Vector(1, 3)], arrowColor, 1.5);

        if (CanResize(shape))
        {
            var square = ResizeHandle(shape);
            Paint.Rect(context, square, ResizeHandleLit(shape) ? Selection : Paint.PanelSolid);
            Paint.Outline(context, square, Selection, 1.5);
        }
    }

    /// <summary>The line of a connection being drawn: from its source to the pointer, or to the element under it.</summary>
    private void DrawConnection(DrawingContext context)
    {
        if (_connectFrom is null)
            return;
        var target = _hovered is not null && !ReferenceEquals(_hovered, _connectFrom) ? _hovered : null;
        Point from, to;
        if (target is not null)
            (from, to) = Ends(_connectFrom, target);
        else
        {
            var world = ToWorld(_pointer);
            var anchor = BpmnGeometry.Anchor(_connectFrom, (float)world.X, (float)world.Y);
            from = ToScreen(anchor.X, anchor.Y);
            to = _pointer;
        }
        Paint.Dashed(context, from, to, Hover, 2, 6);
        Paint.Circle(context, to, 4, Hover);
        if (target is not null)
            Paint.Outline(context, Paint.Grow(ScreenRect(target), 4), Hover, 1.5);
    }

    /// <summary>The element to place (chosen in the palette, or dragged from it), faint under the pointer.</summary>
    private void DrawGhost(DrawingContext context)
    {
        var placing = _dragKind ?? _vm.Placing;
        if (placing is not { } kind || (!_pointerInside && _dragKind is null) || _panning)
            return;
        var (width, height) = kind.DefaultSize();
        var world = ToWorld(_pointer);
        var rect = new Rect(ToScreen(BpmnWindowViewModel.Snap((float)world.X - width / 2), BpmnWindowViewModel.Snap((float)world.Y - height / 2)), new Size(width * _zoom, height * _zoom));
        var color = Paint.Alpha(Selection, 0.5);
        if (kind.IsEvent())
            Paint.Ring(context, rect.Center, rect.Width / 2, color, 1.5);
        else if (kind.IsGateway())
        {
            var c = rect.Center;
            Paint.Polyline(context, [c + new Vector(0, -rect.Height / 2), c + new Vector(rect.Width / 2, 0), c + new Vector(0, rect.Height / 2), c + new Vector(-rect.Width / 2, 0)], color, 1.5, closed: true);
        }
        else
            Paint.Outline(context, rect, color, 1.5);
    }

    // ---- Hit tests ----

    private BpmnShape? ShapeAt(Point screen)
    {
        var world = ToWorld(screen);
        for (var i = _vm.Diagram.Shapes.Count - 1; i >= 0; i--)
        {
            var shape = _vm.Diagram.Shapes[i];
            if (BpmnGeometry.Contains(shape, (float)world.X, (float)world.Y))
                return shape;
        }
        return null;
    }

    private BpmnFlow? FlowAt(Point screen)
    {
        var diagram = _vm.Diagram;
        BpmnFlow? best = null;
        var bestDistance = 7.0;
        foreach (var flow in diagram.Flows)
        {
            var source = diagram.Shape(flow.SourceId);
            var target = diagram.Shape(flow.TargetId);
            if (source is null || target is null)
                continue;
            var (from, to) = Ends(source, target);
            var distance = Paint.DistanceToSegment(screen, from, to);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = flow;
            }
        }
        return best;
    }

    private Point ConnectHandle(BpmnShape shape)
    {
        var rect = ScreenRect(shape);
        return new Point(rect.Right + HandleRadius + 6, rect.Center.Y);
    }

    private static bool CanResize(BpmnShape shape) => shape.Kind.IsActivity() || shape.Kind == BpmnElementKind.TextAnnotation;

    private Rect ResizeHandle(BpmnShape shape)
    {
        var rect = ScreenRect(shape);
        return new Rect(rect.Right - 5, rect.Bottom - 5, 10, 10);
    }

    private bool ResizeHandleLit(BpmnShape shape) => Paint.Grow(ResizeHandle(shape), 3).Contains(_pointer);

    // ---- Drag and drop from the palette ----

    /// <summary>An element of the palette dragged over the canvas (<paramref name="at"/> in its coordinates): its ghost follows the pointer.</summary>
    public void PaletteDragOver(BpmnElementKind kind, Point at)
    {
        var inside = new Rect(Bounds.Size).Contains(at);
        _dragKind = inside ? kind : null;
        _pointer = at;
        InvalidateVisual();
    }

    /// <summary>An element of the palette dropped at <paramref name="at"/> (coordinates of the canvas): placed there when it is inside.</summary>
    public void PaletteDrop(BpmnElementKind kind, Point at)
    {
        _dragKind = null;
        if (new Rect(Bounds.Size).Contains(at))
        {
            var world = ToWorld(at);
            _vm.AddShape(kind, (float)world.X, (float)world.Y);
            _vm.Placing = null;
            Focus();
        }
        InvalidateVisual();
    }

    // ---- Input ----

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _pointerInside = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointerInside = false;
        _hovered = null;
        _hoveredFlow = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        ZoomBy(Math.Pow(1.12, delta), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Handled = true;
        var position = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        _pointer = position;

        // The pen: a left drag draws around the elements; a right click removes the drawing.
        if (_pen.Active)
        {
            if (properties.IsRightButtonPressed)
                ClearPen();
            else if (properties.IsLeftButtonPressed)
            {
                _pen.Begin(position);
                e.Pointer.Capture(this);
                InvalidateVisual();
            }
            return;
        }

        // A double-click on the name of a cluster opens its popup.
        if (properties.IsLeftButtonPressed && e.ClickCount >= 2 && _pen.LabelAt(position) is { } cluster)
        {
            ClusterDoubleClicked?.Invoke(cluster);
            return;
        }

        if (properties.IsRightButtonPressed)
        {
            if (_connectFrom is not null)
            {
                CancelConnection();
                return;
            }
            ShowMenu(position);
            return;
        }
        if (properties.IsMiddleButtonPressed)
        {
            StartPan(position, e);
            return;
        }
        if (!properties.IsLeftButtonPressed)
            return;

        // The palette: a kind to place.
        if (_vm.Placing is { } kind)
        {
            var world = ToWorld(position);
            _vm.AddShape(kind, (float)world.X, (float)world.Y);
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                _vm.Placing = null;
            return;
        }

        // The connect tool: the source, then the target.
        if (_vm.Connecting)
        {
            var clicked = ShapeAt(position);
            if (_connectFrom is null)
            {
                if (clicked is null)
                    return;
                _connectFrom = clicked;
                _connectDrag = false;
                _vm.Selected = clicked;
                _vm.SetStatus($"Connect {clicked.Id}: now click the target", false);
                InvalidateVisual();
                return;
            }
            FinishConnection(clicked);
            return;
        }

        // A connection started from the handle or the menu, going on by clicks: this click is the target.
        if (_connectFrom is not null && !_connectDrag)
        {
            FinishConnection(ShapeAt(position));
            return;
        }

        if (e.ClickCount >= 2)
        {
            if (ShapeAt(position) is { } toRename)
                _ = RenameAsync(toRename);
            else if (FlowAt(position) is { } flowToRename)
                _ = RenameAsync(flowToRename);
            return;
        }

        // The handles of the element selected.
        if (_vm.Selected is BpmnShape selected)
        {
            if (Paint.Distance(ConnectHandle(selected), position) <= HandleRadius + 2)
            {
                _connectFrom = selected;
                _connectDrag = true;
                e.Pointer.Capture(this);
                InvalidateVisual();
                return;
            }
            if (CanResize(selected) && ResizeHandleLit(selected))
            {
                _resizing = selected;
                _resizeStart = position;
                _resizeSize = (selected.Width, selected.Height);
                e.Pointer.Capture(this);
                return;
            }
        }

        if (ShapeAt(position) is { } shape)
        {
            _vm.Selected = shape;
            _moving = shape;
            _moveLast = position;
            _vm.BeginMove();
            e.Pointer.Capture(this);
            return;
        }
        if (FlowAt(position) is { } flow)
        {
            _vm.Selected = flow;
            return;
        }
        _vm.Selected = null;
        StartPan(position, e);
    }

    private void StartPan(Point at, PointerEventArgs e)
    {
        _panning = true;
        _panStart = at;
        _offsetStart = _offset;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        var position = e.GetPosition(this);
        _pointer = position;
        if (_pen.Active && _pen.IsDown && e.InitialPressMouseButton == MouseButton.Left)
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
            var world = (position - _resizeStart) / _zoom;
            _vm.Resize(_resizing, (float)(_resizeSize.Width + world.X), (float)(_resizeSize.Height + world.Y));
            _resizing = null;
            return;
        }
        if (_connectFrom is not null && _connectDrag)
        {
            var target = ShapeAt(position);
            if (target is null || ReferenceEquals(target, _connectFrom))
            {
                // A click on the handle without a drag: the connection goes on by clicks, as the connect tool.
                if (Paint.Distance(position, ConnectHandle(_connectFrom)) <= HandleRadius + 4)
                {
                    _connectDrag = false;
                    _vm.SetStatus($"Connect {_connectFrom.Id}: click the target (Escape cancels)", false);
                    InvalidateVisual();
                    return;
                }
                CancelConnection();
                return;
            }
            FinishConnection(target);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var previous = _pointer;
        _pointer = position;
        if (_pen.Active)
        {
            Cursor = CrossCursor;
            if (_pen.Extend(position))
                InvalidateVisual();
            return;
        }
        if (_panning)
        {
            _offset = _offsetStart + (position - _panStart);
            InvalidateVisual();
            return;
        }
        if (_moving is not null)
        {
            var delta = (position - _moveLast) / _zoom;
            _moveLast = position;
            if (delta.X != 0 || delta.Y != 0)
                _vm.Move(_moving, (float)delta.X, (float)delta.Y);
            return;
        }
        if (_resizing is not null)
        {
            var world = (position - _resizeStart) / _zoom;
            _resizing.Width = (float)Math.Max(40, _resizeSize.Width + world.X);
            _resizing.Height = (float)Math.Max(30, _resizeSize.Height + world.Y);
            InvalidateVisual();
            return;
        }
        _ = previous;
        var hovered = ShapeAt(position);
        var hoveredFlow = hovered is null ? FlowAt(position) : null;
        var changed = !ReferenceEquals(hovered, _hovered) || !ReferenceEquals(hoveredFlow, _hoveredFlow);
        _hovered = hovered;
        _hoveredFlow = hoveredFlow;
        Cursor = _vm.Placing is not null || _vm.Connecting || _connectFrom is not null ? CrossCursor
            : _vm.Selected is BpmnShape s && Paint.Distance(ConnectHandle(s), position) <= HandleRadius + 2 ? HandCursor
            : _vm.Selected is BpmnShape r && CanResize(r) && ResizeHandleLit(r) ? ResizeCursor
            : hovered is not null ? MoveCursor
            : hoveredFlow is not null ? HandCursor
            : ArrowCursor;
        if (changed || _connectFrom is not null || _vm.Placing is not null || _vm.Selected is BpmnShape)
            InvalidateVisual();
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
            InvalidateVisual();
            return;
        }
        if (_vm.Connect(source, target, out var reason) is null)
            _vm.SetStatus(reason, true);
        if (_vm.Connecting)
            _vm.Connecting = false;
        InvalidateVisual();
    }

    private void CancelConnection()
    {
        _connectFrom = null;
        _connectDrag = false;
        _vm.SetStatus("Connection cancelled", false);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && Key(e))
            e.Handled = true;
    }

    private bool Key(KeyEventArgs key)
    {
        var command = key.KeyModifiers.HasFlag(KeyModifiers.Control) || key.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = key.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (key.Key)
        {
            case Avalonia.Input.Key.Escape when _pen.Active && _pen.HasDrawing:
                ClearPen();
                return true;
            case Avalonia.Input.Key.Escape:
                if (_connectFrom is not null)
                    CancelConnection();
                else if (_vm.Placing is not null || _vm.Connecting)
                    _vm.SelectToolCommand.Execute(null);
                else
                    _vm.Selected = null;
                return true;
            case Avalonia.Input.Key.Delete:
            case Avalonia.Input.Key.Back:
                if (_vm.Selected is { } selected)
                    _vm.Remove(selected);
                return true;
            case Avalonia.Input.Key.Z when command && shift:
            case Avalonia.Input.Key.Y when command:
                _vm.RedoCommand.Execute(null);
                return true;
            case Avalonia.Input.Key.Z when command:
                _vm.UndoCommand.Execute(null);
                return true;
            case Avalonia.Input.Key.Left or Avalonia.Input.Key.Right or Avalonia.Input.Key.Up or Avalonia.Input.Key.Down when _vm.Selected is BpmnShape shape:
            {
                var step = shift ? BpmnWindowViewModel.Grid * 5 : BpmnWindowViewModel.Grid;
                var (dx, dy) = key.Key switch
                {
                    Avalonia.Input.Key.Left => (-step, 0f),
                    Avalonia.Input.Key.Right => (step, 0f),
                    Avalonia.Input.Key.Up => (0f, -step),
                    _ => (0f, step)
                };
                _vm.BeginMove();
                _vm.Move(shape, dx, dy);
                _vm.EndMove();
                return true;
            }
            case Avalonia.Input.Key.Enter or Avalonia.Input.Key.F2 when _vm.Selected is { } toRename:
                _ = RenameAsync(toRename);
                return true;
            case Avalonia.Input.Key.D0 or Avalonia.Input.Key.NumPad0 when command:
                ResetView();
                return true;
            case Avalonia.Input.Key.D1 or Avalonia.Input.Key.NumPad1 when command:
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
        var annotation = element is BpmnShape { Kind: BpmnElementKind.TextAnnotation };
        var name = await Dialogs.Ask(title, annotation ? "The text of the annotation" : "The name", current ?? string.Empty, annotation);
        if (name is null)
            return;
        _vm.Rename(element, name.Trim());
        InvalidateVisual();
    }

    /// <summary>
    /// The right-click menu: on an element or a flow, its actions (annotate it too); on the background, the elements to
    /// add there (and annotate the whole diagram).
    /// </summary>
    private void ShowMenu(Point at)
    {
        var menu = new Menu();
        var shape = ShapeAt(at);
        var flow = shape is null ? FlowAt(at) : null;
        if (shape is not null)
        {
            _vm.Selected = shape;
            menu.Caption($"{shape.Kind.Title()} · {shape.Id}")
                .Item("Rename…", () => _ = RenameAsync(shape), gesture: "⏎")
                .Item("Connect from here…", () =>
                {
                    _connectFrom = shape;
                    _connectDrag = false;
                    _vm.SetStatus($"Connect {shape.Id}: click the target (Escape cancels)", false);
                    InvalidateVisual();
                })
                .Item(KnowledgeUi.AnnotateText, () => Annotate(shape.Id, shape.Name, shape.Kind.Title()))
                .Separator()
                .Item("Delete", () => _vm.Remove(shape), gesture: "⌫");
        }
        else if (flow is not null)
        {
            _vm.Selected = flow;
            menu.Caption(flow.Kind == BpmnFlowKind.SequenceFlow ? $"Sequence flow · {flow.Id}" : $"Association · {flow.Id}");
            if (flow.Kind == BpmnFlowKind.SequenceFlow)
                menu.Item("Name (condition)…", () => _ = RenameAsync(flow), gesture: "⏎");
            menu.Item(KnowledgeUi.AnnotateText, () => Annotate(flow.Id, flow.Name, flow.Kind == BpmnFlowKind.SequenceFlow ? "Sequence flow" : "Association"))
                .Separator()
                .Item("Delete", () => _vm.Remove(flow), gesture: "⌫");
        }
        else
        {
            var world = ToWorld(at);
            menu.Caption("Add here");
            string? group = null;
            foreach (var kind in BpmnElementKinds.All)
            {
                if (kind.Group() != group)
                {
                    group = kind.Group();
                    menu.Caption(group);
                }
                var k = kind;
                menu.Item(kind.Title(), () => _vm.AddShape(k, (float)world.X, (float)world.Y), indent: 1);
            }
            menu.Separator()
                .Item("Fit the diagram", Fit, gesture: "⌘1")
                .Item("Zoom 100 %", ResetView, gesture: "⌘0")
                .Separator()
                .Item(KnowledgeUi.AnnotateText, () => Annotate(null, null, null));
        }
        menu.Show(this);
    }

    /// <summary>
    /// Annotates the element or flow <paramref name="id"/> of the diagram (its name, else its kind, as title), or the
    /// whole diagram when <paramref name="id"/> is null.
    /// </summary>
    private void Annotate(string? id, string? name, string? kind)
    {
        var title = id is null ? $"{_vm.EditorTitle} · {_vm.FileName}" : string.IsNullOrWhiteSpace(name) ? $"{kind} · {id}" : name;
        KnowledgeUi.Annotate(this, new AnnotationTarget(AnnotationTargetTypes.DiagramElement,
            id is null ? _vm.FileName : $"{_vm.FileName}#{id}", title, string.IsNullOrWhiteSpace(name) ? null : name));
    }
}
