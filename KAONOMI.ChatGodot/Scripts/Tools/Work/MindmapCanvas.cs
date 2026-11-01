using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// The canvas of <c>/mindmap</c>: the central topic in a pill, its branches in rounded boxes of their colour, the
/// deeper topics as texts underlined in the colour of their branch, joined by curves from a parent to its children.
/// <para>The pointer: a click selects, a drag moves a topic with its branch (dropped onto another topic: it moves
/// under it), a drag of the background pans, the wheel and the trackpad zoom. The topic selected shows a + handle on
/// its outer side (the central topic: one on each side, a child to the right or to the left): a click adds a child. A double-click renames; a right click opens the menu; Tab adds a child,
/// Enter a sibling, F2 renames, Delete removes the branch, the arrows nudge, Escape deselects, Cmd/Ctrl+Z undoes,
/// Shift+Cmd/Ctrl+Z redoes.</para>
/// </summary>
public partial class MindmapCanvas : Control, Knowledge.IClusterCanvas
{
    private const float HandleRadius = 9;
    private const float MinZoom = 0.25f;
    private const float MaxZoom = 4;

    private readonly MindmapWindowViewModel _vm;
    private readonly Overlay _overlay;

    private float _zoom = 1;
    private Vector2 _offset;
    private bool _centered;

    private MindmapNode? _hovered;
    private MindmapNode? _dropTarget;
    private Vector2 _pointer;

    private MindmapNode? _moving;
    private Vector2 _moveStartPointer;
    private bool _panning;
    private Vector2 _panStart;
    private Vector2 _offsetStart;

    /// <summary>Where each node was drawn last (its box on the screen), for the hit tests.</summary>
    private readonly Dictionary<string, Rect2> _boxes = [];
    private readonly Dictionary<(string, int, int), TextParagraph> _texts = [];
    private readonly StyleBoxFlat _box = new() { AntiAliasing = true };

    private static Color Background => Eve.Eve.IsLight ? new Color(0.985f, 0.99f, 0.995f) : new Color(0.02f, 0.03f, 0.04f, 0.92f);
    private static Color Selection => Eve.Eve.Cyan;
    private static Color Hover => Eve.Eve.Amber;

    // ---- The pen of the clusters, as in the graph of relations ----
    private readonly Knowledge.ClusterPen _pen = new();

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

    /// <summary>The id of a topic in a cluster: the file of the map and the id of the topic.</summary>
    private string MemberId(MindmapNode node) => $"{_vm.FileName}#{node.Id}";

    /// <summary>The box of a member of a cluster in the map, in its coordinates (null when it is not one of its topics).</summary>
    private Rect2? MemberBoxOf(ClusterMemberEntry member) =>
        _vm.Map.Nodes.FirstOrDefault(n => MemberId(n) == member.ObjectId) is { } node && _boxes.TryGetValue(node.Id, out var box)
            ? new Rect2(ToWorld(box.Position), box.Size / _zoom)
            : null;

    /// <summary>The drawing of the pen closed: the topics whose centre is inside it make a cluster (none: the drawing goes).</summary>
    private void PenLifted()
    {
        var inside = _pen.Lift()
            ? _vm.Map.Nodes.Where(n => _boxes.TryGetValue(n.Id, out var box) && _pen.Contains(box.GetCenter())).ToList()
            : [];
        QueueRedraw();
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

    public MindmapCanvas(MindmapWindowViewModel vm, Overlay overlay)
    {
        _vm = vm;
        _overlay = overlay;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseExited += () => { _hovered = null; QueueRedraw(); };
        Action changed = () => Bind.OnMain(QueueRedraw);
        vm.Changed += changed;
        Bind.OnGone(this, () => vm.Changed -= changed);
        Bind.When(this, vm, QueueRedraw, nameof(vm.Selected));
        Resized += () =>
        {
            if (!_centered && Size.X > 0)
            {
                _centered = true;
                ResetView();
            }
        };
    }

    // ---- The view ----

    private Vector2 ToScreen(float x, float y) => new Vector2(x, y) * _zoom + _offset;
    private Vector2 ToWorld(Vector2 screen) => (screen - _offset) / _zoom;

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

    /// <summary>The zoom back to 100 %, the root in the middle.</summary>
    public void ResetView()
    {
        _zoom = 1;
        var root = _vm.Map.Root;
        _offset = Size / 2 - new Vector2(root?.X ?? 0, root?.Y ?? 0);
        QueueRedraw();
    }

    /// <summary>The whole map in the canvas.</summary>
    public void Fit()
    {
        if (_vm.Map.Nodes.Count == 0)
        {
            ResetView();
            return;
        }
        var left = _vm.Map.Nodes.Min(n => n.X) - 140;
        var top = _vm.Map.Nodes.Min(n => n.Y) - 60;
        var right = _vm.Map.Nodes.Max(n => n.X) + 140;
        var bottom = _vm.Map.Nodes.Max(n => n.Y) + 60;
        var width = Math.Max(1, right - left);
        var height = Math.Max(1, bottom - top);
        _zoom = Math.Clamp(Math.Min(Size.X / width, Size.Y / height), MinZoom, MaxZoom);
        _offset = new Vector2((Size.X - width * _zoom) / 2 - left * _zoom, (Size.Y - height * _zoom) / 2 - top * _zoom);
        QueueRedraw();
    }

    public int ZoomPercent => (int)Math.Round(_zoom * 100);

    // ---- The look of a node ----

    private (float FontSize, float PadX, float Height) Metrics(int depth) => depth switch
    {
        0 => (16 * _zoom, 16 * _zoom, 44 * _zoom),
        1 => (13 * _zoom, 12 * _zoom, 32 * _zoom),
        _ => (12 * _zoom, 8 * _zoom, 26 * _zoom)
    };

    private static Font FontOf(int depth) => depth == 0 ? Eve.Eve.Display : Eve.Eve.Body;

    private Rect2 BoxOf(MindmapNode node)
    {
        var depth = _vm.Map.Depth(node);
        var (fontSize, padX, height) = Metrics(depth);
        var text = node.Text.Length == 0 ? "…" : node.Text;
        var width = Math.Max(40 * _zoom, FontOf(depth).GetStringSize(text, fontSize: Math.Max(1, (int)Math.Round(fontSize))).X + padX * 2 + 4 * _zoom);
        var center = ToScreen(node.X, node.Y);
        return new Rect2(center - new Vector2(width, height) / 2, new Vector2(width, height));
    }

    private Color ColorOf(MindmapNode node)
    {
        var branch = _vm.Map.BranchColor(node);
        return branch is not null && Color.HtmlIsValid(branch) ? Color.FromHtml(branch) : Eve.Eve.Cyan;
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

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), Background);
        DrawGrid(size);
        _boxes.Clear();
        foreach (var node in _vm.Map.Nodes)
            _boxes[node.Id] = BoxOf(node);
        _pen.DrawClusters(this, MemberBoxOf, p => ToScreen(p.X, p.Y), _zoom);
        foreach (var node in _vm.Map.Nodes)
            if (_vm.Map.Parent(node) is { } parent)
                DrawLink(parent, node);
        foreach (var node in _vm.Map.Nodes)
            DrawNode(node);
        DrawHandles();
        _pen.DrawPen(this);
        if (_texts.Count > 2000)
            _texts.Clear();
    }

    private void DrawGrid(Vector2 size)
    {
        var step = 40 * _zoom;
        while (step < 14)
            step *= 2;
        var color = Eve.Eve.Edge with { A = 0.08f };
        for (var x = Mathf.PosMod(_offset.X, step); x < size.X; x += step)
            DrawLine(new Vector2(x, 0), new Vector2(x, size.Y), color);
        for (var y = Mathf.PosMod(_offset.Y, step); y < size.Y; y += step)
            DrawLine(new Vector2(0, y), new Vector2(size.X, y), color);
    }

    private void DrawLink(MindmapNode parent, MindmapNode child)
    {
        var from = _boxes[parent.Id];
        var to = _boxes[child.Id];
        var toRight = to.GetCenter().X >= from.GetCenter().X;
        var start = new Vector2(toRight ? from.End.X : from.Position.X, from.GetCenter().Y);
        var end = new Vector2(toRight ? to.Position.X : to.End.X, to.GetCenter().Y);
        var bend = Math.Max(20, Math.Abs(end.X - start.X) * 0.45f);
        var c1 = start + new Vector2(toRight ? bend : -bend, 0);
        var c2 = end - new Vector2(toRight ? bend : -bend, 0);
        var points = new Vector2[25];
        for (var i = 0; i <= 24; i++)
        {
            var t = i / 24f;
            var u = 1 - t;
            points[i] = u * u * u * start + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * end;
        }
        var color = ColorOf(child);
        var selected = ReferenceEquals(_vm.Selected, child);
        var depth = _vm.Map.Depth(child);
        DrawPolyline(points, selected ? Selection : color with { A = 0.9f }, Math.Max(1, (depth == 1 ? 3 : 2) * _zoom), true);
    }

    private void DrawNode(MindmapNode node)
    {
        var box = _boxes[node.Id];
        var depth = _vm.Map.Depth(node);
        var selected = ReferenceEquals(_vm.Selected, node);
        var hovered = ReferenceEquals(_hovered, node) && !selected;
        var dropping = ReferenceEquals(_dropTarget, node);
        var color = ColorOf(node);
        var edge = selected ? Selection : dropping || hovered ? Hover : color;
        var (fontSize, padX, _) = Metrics(depth);
        var canvas = GetCanvasItem();
        var textColor = Eve.Eve.TextBright;

        if (depth <= 1)
        {
            var radius = (int)Math.Round((depth == 0 ? box.Size.Y / 2 : 8 * _zoom));
            _box.CornerRadiusTopLeft = _box.CornerRadiusTopRight = _box.CornerRadiusBottomLeft = _box.CornerRadiusBottomRight = radius;
            var border = Math.Max(1, (int)Math.Round((depth == 0 ? 2.5f : 2f) * _zoom));
            _box.BorderWidthLeft = _box.BorderWidthTop = _box.BorderWidthRight = _box.BorderWidthBottom = border;
            _box.BgColor = depth == 0 ? Eve.Eve.Cyan with { A = Eve.Eve.IsLight ? 0.18f : 0.28f } : color with { A = Eve.Eve.IsLight ? 0.14f : 0.22f };
            _box.BorderColor = edge;
            _box.Draw(canvas, box);
        }
        else
        {
            // A text underlined in the colour of its branch.
            DrawLine(new Vector2(box.Position.X, box.End.Y - 1), new Vector2(box.End.X, box.End.Y - 1), edge, Math.Max(1, 2 * _zoom), true);
            if (hovered || dropping)
                DrawRect(box, edge with { A = 0.12f });
        }
        var font = FontOf(depth);
        var paragraph = Text(node.Text.Length == 0 ? "…" : node.Text, font, fontSize, box.Size.X - padX * 2 + 6 * _zoom);
        var height = paragraph.GetLineCount() > 0 ? paragraph.GetLineSize(0).Y : fontSize;
        paragraph.Draw(canvas, new Vector2(box.Position.X + padX - 2, box.GetCenter().Y - height / 2), textColor);
        if (node.Note.Length > 0)
        {
            // A small mark: the topic has a note.
            var mark = new Vector2(box.End.X - 5 * _zoom, box.Position.Y + 5 * _zoom);
            DrawCircle(mark, Math.Max(2, 3 * _zoom), Eve.Eve.Amber);
        }
    }

    private TextParagraph Text(string text, Font font, float fontSize, float width)
    {
        var size = Math.Max(1, (int)Math.Round(fontSize));
        var key = (text, size, font.GetHashCode());
        if (_texts.TryGetValue(key, out var paragraph))
            return paragraph;
        paragraph = new TextParagraph { Width = Math.Max(1, width), MaxLinesVisible = 1, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, BreakFlags = TextServer.LineBreakFlag.None };
        paragraph.AddString(text, font, size);
        _texts[key] = paragraph;
        return paragraph;
    }

    /// <summary>The + handles of a node: one on each side of the root (a child to the right, to the left), one on the outer side of the others.</summary>
    private IEnumerable<(Vector2 Point, int Side)> AddHandles(MindmapNode node)
    {
        var box = _boxes[node.Id];
        var y = box.GetCenter().Y;
        var side = SideOf(node);
        if (side >= 0)
            yield return (new Vector2(box.End.X + HandleRadius + 6, y), 1);
        if (side <= 0)
            yield return (new Vector2(box.Position.X - HandleRadius - 6, y), -1);
    }

    /// <summary>The side of the + handle of the selection under <paramref name="at"/>, null when none.</summary>
    private int? HandleAt(Vector2 at) =>
        _vm.Selected is { } node && _boxes.ContainsKey(node.Id)
            ? AddHandles(node).Where(h => h.Point.DistanceTo(at) <= HandleRadius + 2).Select(h => (int?)h.Side).FirstOrDefault()
            : null;

    private void DrawHandles()
    {
        if (_vm.Selected is not { } node || !_boxes.ContainsKey(node.Id))
            return;
        var box = _boxes[node.Id].Grow(4);
        var color = Selection with { A = 0.8f };
        DrawDashedLine(box.Position, new Vector2(box.End.X, box.Position.Y), color, 1, 4);
        DrawDashedLine(new Vector2(box.End.X, box.Position.Y), box.End, color, 1, 4);
        DrawDashedLine(box.End, new Vector2(box.Position.X, box.End.Y), color, 1, 4);
        DrawDashedLine(new Vector2(box.Position.X, box.End.Y), box.Position, color, 1, 4);
        foreach (var (handle, _) in AddHandles(node))
        {
            var lit = handle.DistanceTo(_pointer) <= HandleRadius + 2;
            DrawCircle(handle, HandleRadius, lit ? Selection : Eve.Eve.PanelSolid);
            DrawArc(handle, HandleRadius, 0, Mathf.Tau, 32, Selection, 1.5f, true);
            var plus = lit ? Eve.Eve.PanelSolid : Selection;
            DrawLine(handle + new Vector2(-4, 0), handle + new Vector2(4, 0), plus, 1.5f, true);
            DrawLine(handle + new Vector2(0, -4), handle + new Vector2(0, 4), plus, 1.5f, true);
        }
    }

    // ---- Hit tests ----

    private MindmapNode? NodeAt(Vector2 screen)
    {
        for (var i = _vm.Map.Nodes.Count - 1; i >= 0; i--)
        {
            var node = _vm.Map.Nodes[i];
            if (_boxes.TryGetValue(node.Id, out var box) && box.Grow(2).HasPoint(screen))
                return node;
        }
        return null;
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
        // The pen: a left drag draws around the topics; a right click removes the drawing.
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
        if (press.DoubleClick)
        {
            if (NodeAt(press.Position) is { } toRename)
                _ = RenameAsync(toRename);
            return;
        }
        if (_vm.Selected is { } selected && HandleAt(press.Position) is { } side)
        {
            if (_vm.AddChild(selected, side: side) is { } child)
                _ = RenameAsync(child);
            return;
        }
        if (NodeAt(press.Position) is { } node)
        {
            _vm.Selected = node;
            _moving = node;
            _moveStartPointer = press.Position;
            _vm.BeginMove();
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
        if (_moving is { } moving)
        {
            _moving = null;
            var target = _dropTarget;
            _dropTarget = null;
            if (target is not null && release.Position.DistanceTo(_moveStartPointer) > 8)
            {
                // Dropped onto another topic: the branch moves under it, back where it was dragged from.
                var before = _vm.Map;
                _vm.EndMove();
                _ = before;
                if (_vm.Reparent(moving, target, out var reason))
                    _vm.LayoutCommand.Execute(null);
                else if (reason.Length > 0)
                    _vm.SetStatus(reason, true);
                return;
            }
            _vm.EndMove();
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
        if (_moving is { } moving)
        {
            var delta = motion.Relative / _zoom;
            if (delta.LengthSquared() > 0)
                _vm.Move(moving, delta.X, delta.Y);
            var under = NodeAt(motion.Position);
            _dropTarget = under is not null && !ReferenceEquals(under, moving) && !_vm.Map.Subtree(moving).Contains(under) && moving.ParentId is not null ? under : null;
            return;
        }
        var hovered = NodeAt(motion.Position);
        var changed = !ReferenceEquals(hovered, _hovered);
        _hovered = hovered;
        MouseDefaultCursorShape = HandleAt(motion.Position) is not null ? CursorShape.PointingHand
            : hovered is not null ? CursorShape.Move : CursorShape.Arrow;
        if (changed || _vm.Selected is not null)
            QueueRedraw();
    }

    private bool Key(InputEventKey key)
    {
        var command = key.CtrlPressed || key.MetaPressed;
        switch (key.Keycode)
        {
            case Godot.Key.Tab when _vm.Selected is { } parent:
                if (_vm.AddChild(parent) is { } child)
                    _ = RenameAsync(child);
                return true;
            case Godot.Key.Enter or Godot.Key.KpEnter when _vm.Selected is { } node && !key.ShiftPressed:
                if (_vm.AddSibling(node) is { } sibling)
                    _ = RenameAsync(sibling);
                return true;
            case Godot.Key.F2 when _vm.Selected is { } toRename:
                _ = RenameAsync(toRename);
                return true;
            case Godot.Key.Escape when _pen.Active && _pen.HasDrawing:
                ClearPen();
                return true;
            case Godot.Key.Escape:
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
            case Godot.Key.Left or Godot.Key.Right or Godot.Key.Up or Godot.Key.Down when _vm.Selected is { } moved:
            {
                var step = key.ShiftPressed ? 50f : 10f;
                var (dx, dy) = key.Keycode switch
                {
                    Godot.Key.Left => (-step, 0f),
                    Godot.Key.Right => (step, 0f),
                    Godot.Key.Up => (0f, -step),
                    _ => (0f, step)
                };
                _vm.BeginMove();
                _vm.Move(moved, dx, dy);
                _vm.EndMove();
                return true;
            }
            case Godot.Key.Key0 when command:
                ResetView();
                return true;
            case Godot.Key.Key1 when command:
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
        QueueRedraw();
    }

    private async System.Threading.Tasks.Task NoteAsync(MindmapNode node)
    {
        var note = await Dialogs.Ask($"Note of \"{node.Text}\"", "The note of the topic (shown in the properties, kept in the file)", node.Note, multiline: true);
        if (note is null)
            return;
        _vm.SetNote(node, note.Trim());
        QueueRedraw();
    }

    /// <summary>The right-click menu: on a topic, its actions (annotate it too); on the background, the map (annotate the whole map).</summary>
    private void ShowMenu(Vector2 at)
    {
        var items = new List<Overlay.MenuItem?>();
        if (NodeAt(at) is { } node)
        {
            _vm.Selected = node;
            items.Add(new Overlay.MenuItem(node.ParentId is null ? "Central topic" : $"Topic · {node.Id}", false, null));
            items.Add(new Overlay.MenuItem("Add a child", true, () => { if (_vm.AddChild(node) is { } child) _ = RenameAsync(child); }, "⇥"));
            if (node.ParentId is not null)
                items.Add(new Overlay.MenuItem("Add a sibling", true, () => { if (_vm.AddSibling(node) is { } sibling) _ = RenameAsync(sibling); }, "⏎"));
            items.Add(new Overlay.MenuItem("Rename…", true, () => _ = RenameAsync(node), "F2"));
            items.Add(new Overlay.MenuItem("Note…", true, () => _ = NoteAsync(node)));
            items.Add(new Overlay.MenuItem(Data.Annotations.MenuText, true, () => Data.Annotations.Open(this,
                new AnnotationTarget(AnnotationTargetTypes.MindmapTopic, $"{_vm.FileName}#{node.Id}", node.Text, node.Text))));
            if (_vm.Map.Depth(node) == 1)
            {
                items.Add(null);
                items.Add(new Overlay.MenuItem("Colour of the branch", false, null));
                foreach (var color in MindmapDiagram.Palette)
                {
                    var c = color;
                    items.Add(new Overlay.MenuItem($"{(node.Color == c ? "● " : "○ ")}{c}", true, () => _vm.SetColor(node, c), Indent: 1));
                }
            }
            if (node.ParentId is not null)
            {
                items.Add(null);
                items.Add(new Overlay.MenuItem("Delete the branch", true, () => _vm.Remove(node), "⌫"));
            }
        }
        else
        {
            items.Add(new Overlay.MenuItem("Map", false, null));
            items.Add(new Overlay.MenuItem("Lay out again", true, () => _vm.LayoutCommand.Execute(null)));
            items.Add(new Overlay.MenuItem("Fit the map", true, Fit, "⌘1"));
            items.Add(new Overlay.MenuItem("Zoom 100 %", true, ResetView, "⌘0"));
            items.Add(null);
            items.Add(new Overlay.MenuItem(Data.Annotations.MenuText, true, () => Data.Annotations.Open(this,
                new AnnotationTarget(AnnotationTargetTypes.MindmapTopic, _vm.FileName, _vm.FileName))));
        }
        _overlay.Menu(items, GetGlobalPosition() - _overlay.GetGlobalPosition() + at);
    }
}
