using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The canvas of <c>/mindmap</c>: the central topic in a pill, its branches in rounded boxes of their colour, the
/// deeper topics as texts underlined in the colour of their branch, joined by curves from a parent to its children.
/// <para>The pointer: a click selects, a drag moves a topic with its branch (dropped onto another topic: it moves under
/// it), a drag of the background pans, the wheel zooms. The topic selected shows a + handle on its outer side (the
/// central topic: one on each side, a child to the right or to the left): a click adds a child. A double-click renames;
/// a right click opens the menu; Tab adds a child, Enter a sibling, F2 renames, Delete removes the branch, the arrows
/// nudge, Escape deselects, Cmd/Ctrl+Z undoes, Shift+Cmd/Ctrl+Z (or Ctrl+Y) redoes, Ctrl+0 100 %, Ctrl+1 fits.</para>
/// </summary>
public sealed class MindmapCanvas : Control, IClusterCanvas
{
    private const double HandleRadius = 9;
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4;
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

    private readonly MindmapWindowViewModel _vm;

    private double _zoom = 1;
    private Vector _offset;
    private bool _centered;

    private MindmapNode? _hovered;
    private MindmapNode? _dropTarget;
    private Point _pointer;

    private MindmapNode? _moving;
    private Point _moveStartPointer;
    private Point _moveLast;
    private bool _panning;
    private Point _panStart;
    private Vector _offsetStart;

    /// <summary>Where each node was drawn last (its box on the screen), for the hit tests.</summary>
    private readonly Dictionary<string, Rect> _boxes = [];

    private static Color Selection => Paint.Cyan;
    private static Color Hover => Paint.Amber;

    // ---- The pen of the clusters, as in the graph of relations ----
    private readonly ClusterPen _pen = new();

    public MindmapCanvas(MindmapWindowViewModel vm)
    {
        _vm = vm;
        Focusable = true;
        ClipToBounds = true;
        Action changed = () => Ui.OnMain(InvalidateVisual);
        vm.Changed += changed;
        KnowledgeUi.OnGone(this, () => vm.Changed -= changed);
        Ui.When(this, vm, InvalidateVisual, nameof(vm.Selected));
        SizeChanged += (_, e) =>
        {
            if (!_centered && e.NewSize.Width > 0)
            {
                _centered = true;
                ResetView();
            }
        };
    }

    /// <summary>Raised when the zoom changes (the toolbar shows it).</summary>
    public event Action? ViewChanged;

    /// <inheritdoc />
    public string ClusterCategory => ClusterCategories.Mindmap;

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

    /// <summary>The id of a topic in a cluster: the file of the map and the id of the topic.</summary>
    private string MemberId(MindmapNode node) => $"{_vm.FileName}#{node.Id}";

    /// <summary>The box of a member of a cluster in the map, in its coordinates (null when it is not one of its topics).</summary>
    private Rect? MemberBoxOf(ClusterMemberEntry member) =>
        _vm.Map.Nodes.FirstOrDefault(n => MemberId(n) == member.ObjectId) is { } node && _boxes.TryGetValue(node.Id, out var box)
            ? new Rect(ToWorld(box.TopLeft), new Size(box.Width / _zoom, box.Height / _zoom))
            : null;

    /// <summary>The drawing of the pen closed: the topics whose centre is inside it make a cluster (none: the drawing goes).</summary>
    private void PenLifted()
    {
        var inside = _pen.Lift()
            ? _vm.Map.Nodes.Where(n => _boxes.TryGetValue(n.Id, out var box) && _pen.Contains(box.Center)).ToList()
            : [];
        InvalidateVisual();
        if (inside.Count == 0)
        {
            ClearPen();
            return;
        }
        var members = inside.Select(n => new ClusterMemberEntry
        {
            WindowName = "Mind map",
            ObjectId = MemberId(n),
            Title = n.Text.Length == 0 ? $"Topic · {n.Id}" : n.Text
        }).ToList();
        ClusterDrawn?.Invoke(members, _pen.Simplified(ToWorld));
    }

    // ---- The view ----

    private Point ToScreen(double x, double y) => new(x * _zoom + _offset.X, y * _zoom + _offset.Y);
    private Point ToWorld(Point screen) => new((screen.X - _offset.X) / _zoom, (screen.Y - _offset.Y) / _zoom);

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

    /// <summary>The zoom back to 100 %, the root in the middle.</summary>
    public void ResetView()
    {
        _zoom = 1;
        var root = _vm.Map.Root;
        _offset = new Vector(Bounds.Width / 2 - (root?.X ?? 0), Bounds.Height / 2 - (root?.Y ?? 0));
        Redraw();
    }

    /// <summary>The whole map in the canvas.</summary>
    public void Fit()
    {
        var nodes = _vm.Map.Nodes;
        if (nodes.Count == 0)
        {
            ResetView();
            return;
        }
        var left = nodes.Min(n => n.X) - 140;
        var top = nodes.Min(n => n.Y) - 60;
        var right = nodes.Max(n => n.X) + 140;
        var bottom = nodes.Max(n => n.Y) + 60;
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        var size = Bounds.Size;
        _zoom = Math.Clamp(Math.Min(size.Width / width, size.Height / height), MinZoom, MaxZoom);
        _offset = new Vector((size.Width - width * _zoom) / 2 - left * _zoom, (size.Height - height * _zoom) / 2 - top * _zoom);
        Redraw();
    }

    public int ZoomPercent => (int)Math.Round(_zoom * 100);

    // ---- The look of a node ----

    private (double FontSize, double PadX, double Height) Metrics(int depth) => depth switch
    {
        0 => (16 * _zoom, 16 * _zoom, 44 * _zoom),
        1 => (13 * _zoom, 12 * _zoom, 32 * _zoom),
        _ => (12 * _zoom, 8 * _zoom, 26 * _zoom)
    };

    private static Typeface FontOf(int depth) => depth == 0 ? Paint.SemiBold : Paint.Body;

    private Rect BoxOf(MindmapNode node)
    {
        var depth = _vm.Map.Depth(node);
        var (fontSize, padX, height) = Metrics(depth);
        var text = node.Text.Length == 0 ? "…" : node.Text;
        var width = Math.Max(40 * _zoom, Paint.Measure(text, fontSize, typeface: FontOf(depth)).Width + padX * 2 + 4 * _zoom);
        var center = ToScreen(node.X, node.Y);
        return new Rect(center.X - width / 2, center.Y - height / 2, width, height);
    }

    private Color ColorOf(MindmapNode node)
    {
        var branch = _vm.Map.BranchColor(node);
        return branch is not null && Color.TryParse(branch, out var color) ? color : Paint.Cyan;
    }

    /// <summary>The side of the node: +1 to the right of the root, -1 to its left (0 for the root).</summary>
    private int SideOf(MindmapNode node)
    {
        var root = _vm.Map.Root;
        if (root is null || node.ParentId is null)
            return 0;
        for (var current = node; current is not null; current = _vm.Map.Parent(current))
            if (current.ParentId == root.Id)
                return current.X >= root.X ? 1 : -1;
        return 1;
    }

    // ---- Drawing ----

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        Paint.Rect(context, new Rect(size), Paint.Background);
        DrawGrid(context, size);
        _boxes.Clear();
        foreach (var node in _vm.Map.Nodes)
            _boxes[node.Id] = BoxOf(node);
        _pen.DrawClusters(context, MemberBoxOf, p => ToScreen(p.X, p.Y), _zoom);
        foreach (var node in _vm.Map.Nodes)
            if (_vm.Map.Parent(node) is { } parent && _boxes.ContainsKey(parent.Id))
                DrawLink(context, parent, node);
        foreach (var node in _vm.Map.Nodes)
            DrawNode(context, node);
        DrawHandles(context);
        _pen.DrawPen(context);
    }

    private void DrawGrid(DrawingContext context, Size size)
    {
        var step = 40 * _zoom;
        while (step < 14)
            step *= 2;
        var color = Paint.Alpha(Paint.Edge, 0.18);
        for (var x = Paint.PosMod(_offset.X, step); x < size.Width; x += step)
            Paint.Line(context, new Point(x, 0), new Point(x, size.Height), color);
        for (var y = Paint.PosMod(_offset.Y, step); y < size.Height; y += step)
            Paint.Line(context, new Point(0, y), new Point(size.Width, y), color);
    }

    private void DrawLink(DrawingContext context, MindmapNode parent, MindmapNode child)
    {
        var from = _boxes[parent.Id];
        var to = _boxes[child.Id];
        var toRight = to.Center.X >= from.Center.X;
        var start = new Point(toRight ? from.Right : from.X, from.Center.Y);
        var end = new Point(toRight ? to.X : to.Right, to.Center.Y);
        var bend = Math.Max(20, Math.Abs(end.X - start.X) * 0.45);
        var c1 = start + new Vector(toRight ? bend : -bend, 0);
        var c2 = end - new Vector(toRight ? bend : -bend, 0);
        var points = new Point[25];
        for (var i = 0; i <= 24; i++)
        {
            var t = i / 24.0;
            var u = 1 - t;
            points[i] = new Point(u * u * u * start.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * end.X,
                u * u * u * start.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * end.Y);
        }
        var color = ColorOf(child);
        var selected = ReferenceEquals(_vm.Selected, child);
        var depth = _vm.Map.Depth(child);
        Paint.Polyline(context, points, selected ? Selection : Paint.Alpha(color, 0.9), Math.Max(1, (depth == 1 ? 3 : 2) * _zoom));
    }

    private void DrawNode(DrawingContext context, MindmapNode node)
    {
        var box = _boxes[node.Id];
        var depth = _vm.Map.Depth(node);
        var selected = ReferenceEquals(_vm.Selected, node);
        var hovered = ReferenceEquals(_hovered, node) && !selected;
        var dropping = ReferenceEquals(_dropTarget, node);
        var color = ColorOf(node);
        var edge = selected ? Selection : dropping || hovered ? Hover : color;
        var (fontSize, padX, _) = Metrics(depth);

        if (depth <= 1)
        {
            var radius = depth == 0 ? box.Height / 2 : Math.Round(8 * _zoom);
            var border = Math.Max(1, Math.Round((depth == 0 ? 2.5 : 2) * _zoom));
            var fill = depth == 0 ? Paint.Alpha(Paint.Cyan, Paint.IsLight ? 0.18 : 0.28) : Paint.Alpha(color, Paint.IsLight ? 0.14 : 0.22);
            Paint.Round(context, box, radius, fill, edge, border);
        }
        else
        {
            // A text underlined in the colour of its branch.
            Paint.Line(context, new Point(box.X, box.Bottom - 1), new Point(box.Right, box.Bottom - 1), edge, Math.Max(1, 2 * _zoom));
            if (hovered || dropping)
                Paint.Rect(context, box, Paint.Alpha(edge, 0.12));
        }
        var text = node.Text.Length == 0 ? "…" : node.Text;
        var width = Math.Max(1, box.Width - padX * 2 + 6 * _zoom);
        var size = Paint.Measure(text, fontSize, width, 1, FontOf(depth));
        Paint.Write(context, text, new Point(box.X + padX - 2, box.Center.Y - size.Height / 2), fontSize, Paint.TextBright, width, 1, FontOf(depth));
        if (node.Note.Length > 0)
        {
            // A small mark: the topic has a note.
            Paint.Circle(context, new Point(box.Right - 5 * _zoom, box.Y + 5 * _zoom), Math.Max(2, 3 * _zoom), Paint.Amber);
        }
    }

    /// <summary>The + handles of a node: one on each side of the root (a child to the right, to the left), one on the outer side of the others.</summary>
    private IEnumerable<(Point Point, int Side)> AddHandles(MindmapNode node)
    {
        var box = _boxes[node.Id];
        var y = box.Center.Y;
        var side = SideOf(node);
        if (side >= 0)
            yield return (new Point(box.Right + HandleRadius + 6, y), 1);
        if (side <= 0)
            yield return (new Point(box.X - HandleRadius - 6, y), -1);
    }

    /// <summary>The side of the + handle of the selection under <paramref name="at"/>, null when none.</summary>
    private int? HandleAt(Point at) =>
        _vm.Selected is { } node && _boxes.ContainsKey(node.Id)
            ? AddHandles(node).Where(h => Paint.Distance(h.Point, at) <= HandleRadius + 2).Select(h => (int?)h.Side).FirstOrDefault()
            : null;

    private void DrawHandles(DrawingContext context)
    {
        if (_vm.Selected is not { } node || !_boxes.ContainsKey(node.Id))
            return;
        Paint.DashedRect(context, Paint.Grow(_boxes[node.Id], 4), Paint.Alpha(Selection, 0.8), 1, 4);
        foreach (var (handle, _) in AddHandles(node))
        {
            var lit = Paint.Distance(handle, _pointer) <= HandleRadius + 2;
            Paint.Circle(context, handle, HandleRadius, lit ? Selection : Paint.PanelSolid);
            Paint.Ring(context, handle, HandleRadius, Selection, 1.5);
            var plus = lit ? Paint.PanelSolid : Selection;
            Paint.Line(context, handle + new Vector(-4, 0), handle + new Vector(4, 0), plus, 1.5);
            Paint.Line(context, handle + new Vector(0, -4), handle + new Vector(0, 4), plus, 1.5);
        }
    }

    // ---- Hit tests ----

    private MindmapNode? NodeAt(Point screen)
    {
        for (var i = _vm.Map.Nodes.Count - 1; i >= 0; i--)
        {
            var node = _vm.Map.Nodes[i];
            if (_boxes.TryGetValue(node.Id, out var box) && Paint.Grow(box, 2).Contains(screen))
                return node;
        }
        return null;
    }

    // ---- Input ----

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = null;
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
        // The pen: a left drag draws around the topics; a right click removes the drawing.
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
        if (e.ClickCount >= 2)
        {
            if (NodeAt(position) is { } toRename)
                _ = RenameAsync(toRename);
            return;
        }
        if (_vm.Selected is { } selected && HandleAt(position) is { } side)
        {
            if (_vm.AddChild(selected, side: side) is { } child)
                _ = RenameAsync(child);
            return;
        }
        if (NodeAt(position) is { } node)
        {
            _vm.Selected = node;
            _moving = node;
            _moveStartPointer = position;
            _moveLast = position;
            _vm.BeginMove();
            e.Pointer.Capture(this);
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
        if (_moving is { } moving)
        {
            _moving = null;
            var target = _dropTarget;
            _dropTarget = null;
            _vm.EndMove();
            if (target is not null && Paint.Distance(position, _moveStartPointer) > 8)
            {
                // Dropped onto another topic: the branch moves under it, laid out again.
                if (_vm.Reparent(moving, target, out var reason))
                    _vm.LayoutCommand.Execute(null);
                else if (reason.Length > 0)
                    _vm.SetStatus(reason, true);
            }
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
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
        if (_moving is { } moving)
        {
            var delta = (position - _moveLast) / _zoom;
            _moveLast = position;
            if (delta.X != 0 || delta.Y != 0)
                _vm.Move(moving, (float)delta.X, (float)delta.Y);
            var under = NodeAt(position);
            _dropTarget = under is not null && !ReferenceEquals(under, moving) && !_vm.Map.Subtree(moving).Contains(under) && moving.ParentId is not null ? under : null;
            InvalidateVisual();
            return;
        }
        var hovered = NodeAt(position);
        var changed = !ReferenceEquals(hovered, _hovered);
        _hovered = hovered;
        Cursor = HandleAt(position) is not null ? HandCursor : hovered is not null ? MoveCursor : ArrowCursor;
        if (changed || _vm.Selected is not null)
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
            case Avalonia.Input.Key.Tab when _vm.Selected is { } parent:
                if (_vm.AddChild(parent) is { } child)
                    _ = RenameAsync(child);
                return true;
            case Avalonia.Input.Key.Enter when _vm.Selected is { } node && !shift:
                if (_vm.AddSibling(node) is { } sibling)
                    _ = RenameAsync(sibling);
                return true;
            case Avalonia.Input.Key.F2 when _vm.Selected is { } toRename:
                _ = RenameAsync(toRename);
                return true;
            case Avalonia.Input.Key.Escape when _pen.Active && _pen.HasDrawing:
                ClearPen();
                return true;
            case Avalonia.Input.Key.Escape:
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
            case Avalonia.Input.Key.Left or Avalonia.Input.Key.Right or Avalonia.Input.Key.Up or Avalonia.Input.Key.Down when _vm.Selected is { } moved:
            {
                var step = shift ? 50f : 10f;
                var (dx, dy) = key.Key switch
                {
                    Avalonia.Input.Key.Left => (-step, 0f),
                    Avalonia.Input.Key.Right => (step, 0f),
                    Avalonia.Input.Key.Up => (0f, -step),
                    _ => (0f, step)
                };
                _vm.BeginMove();
                _vm.Move(moved, dx, dy);
                _vm.EndMove();
                return true;
            }
            case Avalonia.Input.Key.D0 or Avalonia.Input.Key.NumPad0 when command:
                ResetView();
                return true;
            case Avalonia.Input.Key.D1 or Avalonia.Input.Key.NumPad1 when command:
                Fit();
                return true;
        }
        return false;
    }

    private async System.Threading.Tasks.Task RenameAsync(MindmapNode node)
    {
        var text = await Dialogs.Ask(node.ParentId is null ? "Central topic" : $"Topic {node.Id}", "The text of the topic", node.Text);
        if (text is null)
            return;
        _vm.Rename(node, text.Trim());
        InvalidateVisual();
        Focus();
    }

    private async System.Threading.Tasks.Task NoteAsync(MindmapNode node)
    {
        var note = await Dialogs.Ask($"Note of \"{node.Text}\"", "The note of the topic (shown in the properties, kept in the file)", node.Note, multiline: true);
        if (note is null)
            return;
        _vm.SetNote(node, note.Trim());
        InvalidateVisual();
    }

    /// <summary>The right-click menu: on a topic, its actions (annotate it too); on the background, the map (annotate the whole map).</summary>
    private void ShowMenu(Point at)
    {
        var menu = new Menu();
        if (NodeAt(at) is { } node)
        {
            _vm.Selected = node;
            menu.Caption(node.ParentId is null ? "Central topic" : $"Topic · {node.Id}")
                .Item("Add a child", () => { if (_vm.AddChild(node) is { } child) _ = RenameAsync(child); }, gesture: "⇥");
            if (node.ParentId is not null)
                menu.Item("Add a sibling", () => { if (_vm.AddSibling(node) is { } sibling) _ = RenameAsync(sibling); }, gesture: "⏎");
            menu.Item("Rename…", () => _ = RenameAsync(node), gesture: "F2")
                .Item("Note…", () => _ = NoteAsync(node))
                .Item(KnowledgeUi.AnnotateText, () => KnowledgeUi.Annotate(this,
                    new AnnotationTarget(AnnotationTargetTypes.MindmapTopic, $"{_vm.FileName}#{node.Id}", node.Text, node.Text)));
            if (_vm.Map.Depth(node) == 1)
            {
                menu.Separator().Caption("Colour of the branch");
                foreach (var color in MindmapDiagram.Palette)
                {
                    var c = color;
                    menu.Item($"{(node.Color == c ? "● " : "○ ")}{c}", () => _vm.SetColor(node, c), indent: 1);
                }
            }
            if (node.ParentId is not null)
                menu.Separator().Item("Delete the branch", () => _vm.Remove(node), gesture: "⌫");
        }
        else
        {
            menu.Caption("Map")
                .Item("Lay out again", () => _vm.LayoutCommand.Execute(null))
                .Item("Fit the map", Fit, gesture: "⌘1")
                .Item("Zoom 100 %", ResetView, gesture: "⌘0")
                .Separator()
                .Item(KnowledgeUi.AnnotateText, () => KnowledgeUi.Annotate(this,
                    new AnnotationTarget(AnnotationTargetTypes.MindmapTopic, _vm.FileName, _vm.FileName)));
        }
        menu.Show(this);
    }
}
