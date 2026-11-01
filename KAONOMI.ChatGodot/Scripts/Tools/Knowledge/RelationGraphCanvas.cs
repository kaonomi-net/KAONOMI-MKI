using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// The graph of the relations (port of <c>RelationGraphControl</c>), drawn by <see cref="_Draw"/> and laid out by
/// forces: the elements push each other away, the links pull their two ends together, a light pull keeps everything
/// around the centre. Each element is a card (its type in its colour, its titles, its id, ×n when ends were merged);
/// each link a curve from the source to the destination with an arrow and the title of the relation, red to green with
/// its probability (transparent when it does not meet the filters); several links between the same two elements bend
/// apart, a link to itself is a loop. The elements can be dragged (the others follow), the wheel zooms, dragging the
/// background moves the graph; a click selects an element; a double-click on an element opens it, on a link (its line
/// or its title) its relation.
/// <para>Each card has a link icon at its top right: a click starts a red string from it, a click on the link icon of
/// another element ends it and the relation popup opens with the two (<see cref="RelationRequested"/>); a double-click
/// joins an element and goes on, Enter ends with the elements joined, Escape or a right click cancels. The string stays
/// in the graph only, and shows until the popup is closed (<see cref="EndString"/>).</para>
/// </summary>
public partial class RelationGraphCanvas : Control
{
    private const float NodeWidth = 190;
    private const float NodeHeight = 74;
    private const float FadedOpacity = 0.12f;

    private static readonly Color[] Palette =
    [
        new("#4F8EF7"), new("#E8A23A"), new("#3FBF8F"), new("#E0609A"), new("#9B7CF0"), new("#3BB6CF"), new("#E35A4F"), new("#8BC34A")
    ];

    private static readonly Color StringColor = new("#E11D48");
    private static Color Background => Eve.Eve.IsLight ? new Color(0.985f, 0.99f, 0.995f) : new Color(0.02f, 0.03f, 0.04f, 0.92f);
    private static readonly SystemFont BoldFont = new() { FontNames = Eve.Eve.Body.FontNames, FontWeight = 700, Fallbacks = [Eve.Eve.Emoji] };
    private static readonly SystemFont SemiBold = new() { FontNames = Eve.Eve.Body.FontNames, FontWeight = 600, Fallbacks = [Eve.Eve.Emoji] };

    private readonly RelationGraphViewModel? _vm;
    private readonly bool _snapshot;
    private int _stepsLeft;

    /// <summary>The steps of the force layout still to run for a new graph, a few per frame (the window stays alive).</summary>
    private int _layoutLeft;
    private int _layoutTotal;

    /// <summary>The steps done by the layout running on the worker thread, its task and the way to stop it.</summary>
    private int _layoutDone;
    private Task<Dictionary<(RelationGraphEdge, int), float>?>? _layoutTask;
    private System.Threading.CancellationTokenSource? _layoutCancel;
    private double _tick;

    // View: world → screen = world * zoom + offset.
    private float _zoom = 1;
    private Vector2 _offset;

    private RelationGraphNode? _dragged;
    private Vector2 _dragStart;
    private Vector2 _panStart;
    private bool _panning;
    private bool _moved;
    private RelationGraphNode? _hovered;
    private RelationGraphNode? _hoveredLink;
    private Vector2 _lastSize;

    /// <summary>
    /// The bend of each link (by link and rank among the links of the same pair), in world units: how far the middle of its
    /// curve stands from the straight line, chosen to go around the other cards. Computed when the layout settles.
    /// </summary>
    private Dictionary<(RelationGraphEdge, int), float> _bends = [];

    /// <summary>The farthest the middle of a link may stand from the straight line, in world units.</summary>
    private const float MaxBend = 180;

    /// <summary>The empty space kept between two cards, in world units.</summary>
    private const float CardGap = 70;

    /// <summary>The labels of links written this frame: a label that would cover one of them is left out.</summary>
    private readonly List<Rect2> _labelBoxes = [];

    /// <summary>Where each link was drawn (points along it, and its label), to find the one under the pointer.</summary>
    private readonly List<(RelationGraphEdge Edge, List<Vector2> Points, Rect2 Label)> _edgeShapes = [];

    /// <summary>The texts laid out (title of the cards, labels of the links), kept between frames.</summary>
    private readonly Dictionary<(string, int, int, int, int), TextParagraph> _texts = [];

    // ---- The pen: a drawing around some elements, to make a cluster of them ----
    private readonly List<Vector2> _penPath = [];
    private bool _penDown;
    private bool _penClosed;
    private static readonly Color PenColor = new("#D4A017");

    /// <summary>True while the pen is chosen in the toolbar: a left drag draws around the elements instead of moving them.</summary>
    public bool PenMode { get; set; }

    /// <summary>
    /// Raised when a drawing closes around at least one element (their cards inside the shape), with the drawing
    /// simplified, in the coordinates of the graph (kept in the cluster, shown smoothed by "Show clusters").
    /// </summary>
    public event Action<IReadOnlyList<RelationGraphNode>, IReadOnlyList<Vector2>>? ClusterDrawn;

    /// <summary>Raised by a double-click on the name of a cluster shown by "Show clusters": its popup opens.</summary>
    public event Action<KAONOMI.Chat.Models.ClusterEntry>? ClusterDoubleClicked;

    /// <summary>Where the name of each cluster was drawn, to find the one under a double-click.</summary>
    private readonly List<(KAONOMI.Chat.Models.ClusterEntry Cluster, Rect2 Label)> _clusterLabels = [];

    /// <summary>The drawing with its useless points left out (Ramer–Douglas–Peucker, <paramref name="tolerance"/> pixels).</summary>
    internal static List<Vector2> Simplify(IReadOnlyList<Vector2> points, float tolerance)
    {
        if (points.Count < 3)
            return [.. points];
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            var farthest = -1;
            var distance = 0f;
            for (var i = first + 1; i < last; i++)
            {
                var d = DistanceToSegment(points[i], points[first], points[last]);
                if (d > distance)
                {
                    distance = d;
                    farthest = i;
                }
            }
            if (farthest >= 0 && distance > tolerance)
            {
                keep[farthest] = true;
                stack.Push((first, farthest));
                stack.Push((farthest, last));
            }
        }
        var result = new List<Vector2>();
        for (var i = 0; i < points.Count; i++)
            if (keep[i])
                result.Add(points[i]);
        return result;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var length = ab.LengthSquared();
        if (length < 1e-6f)
            return p.DistanceTo(a);
        var t = Math.Clamp((p - a).Dot(ab) / length, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>A closed shape smoothed (Chaikin, <paramref name="passes"/> times: the corners cut in two).</summary>
    internal static List<Vector2> Smooth(IReadOnlyList<Vector2> shape, int passes)
    {
        var points = new List<Vector2>(shape);
        for (var pass = 0; pass < passes && points.Count >= 3; pass++)
        {
            var next = new List<Vector2>(points.Count * 2);
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                next.Add(a.Lerp(b, 0.25f));
                next.Add(a.Lerp(b, 0.75f));
            }
            points = next;
        }
        return points;
    }

    /// <summary>The convex hull of <paramref name="points"/> (monotone chain), counter-clockwise.</summary>
    internal static List<Vector2> Hull(List<Vector2> points)
    {
        points = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (points.Count < 3)
            return points;
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var lower = new List<Vector2>();
        foreach (var p in points)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }
        var upper = new List<Vector2>();
        for (var i = points.Count - 1; i >= 0; i--)
        {
            var p = points[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        return [.. lower, .. upper];
    }

    internal static bool Inside(IReadOnlyList<Vector2> shape, Vector2 p)
    {
        var inside = false;
        for (int i = 0, j = shape.Count - 1; i < shape.Count; j = i++)
        {
            var a = shape[i];
            var b = shape[j];
            if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// The clusters stored on the server, when "Show clusters" is ticked: around their elements present in the graph,
    /// the drawing of the pen smoothed, or, when the elements have moved out of it, the hull of their cards; the name
    /// of the cluster at its top.
    /// </summary>
    private void DrawClusters()
    {
        _clusterLabels.Clear();
        if (_vm is not { ShowClusters: true } vm || vm.Clusters.Count == 0)
            return;
        foreach (var cluster in vm.Clusters)
        {
            var members = vm.Nodes.Where(n => cluster.Members.Any(m => m.WindowName == n.Type && m.ObjectId == (n.ObjectId ?? n.Key))).ToList();
            if (members.Count == 0)
                continue;
            var drawn = cluster.Path.Select(p => new Vector2((float)p.X, (float)p.Y)).ToList();
            var centres = members.Select(n => new Vector2((float)n.X, (float)n.Y)).ToList();
            List<Vector2> shape;
            if (drawn.Count >= 3 && centres.All(c => Inside(drawn, c)))
                shape = drawn;
            else
            {
                // The elements moved out of the drawing: the hull of their cards, with some room around.
                var corners = new List<Vector2>();
                foreach (var c in centres)
                    foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                        corners.Add(c + new Vector2(dx * (NodeWidth / 2 + 28), dy * (NodeHeight / 2 + 28)));
                shape = Hull(corners);
            }
            var smooth = Smooth(shape, 3).Select(p => ToScreen(p.X, p.Y)).ToArray();
            if (smooth.Length < 3)
                continue;
            DrawColoredPolygon(smooth, PenColor with { A = 0.07f });
            DrawPolyline([.. smooth, smooth[0]], PenColor with { A = 0.85f }, 2.5f, antialiased: true);
            var top = smooth.OrderBy(p => p.Y).First();
            var font = SemiBold;
            var size = (int)Math.Clamp(13 * _zoom, 9, 18);
            var width = font.GetStringSize(cluster.Name, fontSize: size).X;
            var height = font.GetHeight(size);
            DrawString(font, new Vector2(top.X - width / 2, top.Y - 6), cluster.Name, HorizontalAlignment.Left, -1, size, PenColor);
            _clusterLabels.Add((cluster, new Rect2(top.X - width / 2 - 4, top.Y - 6 - height, width + 8, height + 6)));
        }
    }

    /// <summary>Removes the drawing of the pen (its cluster was saved or its popup closed).</summary>
    public void ClearPen()
    {
        _penPath.Clear();
        _penDown = false;
        _penClosed = false;
        if (IsInstanceValid(this))
            QueueRedraw();
    }

    /// <summary>The elements whose card centre is inside the drawing (a ray cast along x).</summary>
    private List<RelationGraphNode> NodesInsidePen()
    {
        var inside = new List<RelationGraphNode>();
        if (_vm is null || _penPath.Count < 3)
            return inside;
        foreach (var node in _vm.Nodes)
        {
            var p = CardOf(node).GetCenter();
            var crossings = false;
            for (int i = 0, j = _penPath.Count - 1; i < _penPath.Count; j = i++)
            {
                var a = _penPath[i];
                var b = _penPath[j];
                if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    crossings = !crossings;
            }
            if (crossings)
                inside.Add(node);
        }
        return inside;
    }

    /// <summary>The drawing of the pen: a line of gold, the shape filled faintly once it is closed.</summary>
    private void DrawPen()
    {
        if (_penPath.Count < 2)
            return;
        if (_penClosed && _penPath.Count >= 3)
            DrawColoredPolygon(_penPath.ToArray(), PenColor with { A = 0.1f });
        DrawPolyline(_penPath.ToArray(), PenColor with { A = _penClosed ? 0.9f : 0.75f }, 3, antialiased: true);
        if (_penClosed)
            DrawLine(_penPath[^1], _penPath[0], PenColor with { A = 0.9f }, 3, antialiased: true);
    }

    // ---- The red string between the link icons of the elements ----
    private RelationGraphNode? _stringFrom;
    private readonly List<RelationGraphNode> _stringJoined = [];
    private Vector2? _stringPointer;
    private bool _stringDialogOpen;
    private int _stringClick;

    public RelationGraphCanvas()
    {
    }

    public RelationGraphCanvas(RelationGraphViewModel vm, bool snapshot = false)
    {
        _vm = vm;
        _snapshot = snapshot;
        ClipContents = true;
        FocusMode = snapshot ? FocusModeEnum.None : FocusModeEnum.All;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        if (snapshot)
            return;
        Action graphChanged = () => Bind.OnMain(OnGraphChanged);
        Action titlesChanged = () => Bind.OnMain(QueueRedraw);
        Action<RelationGraphNode> nodeAdded = node => Bind.OnMain(() => OnNodeAdded(node));
        System.ComponentModel.PropertyChangedEventHandler selected = (_, e) =>
        {
            if (e.PropertyName is nameof(vm.SelectedNode) or nameof(vm.ShowClusters))
                Bind.OnMain(QueueRedraw);
        };
        vm.GraphChanged += graphChanged;
        vm.TitlesChanged += titlesChanged;
        vm.NodeAdded += nodeAdded;
        vm.PropertyChanged += selected;
        KnowledgeUi.OnFreed(this, () =>
        {
            vm.GraphChanged -= graphChanged;
            vm.TitlesChanged -= titlesChanged;
            vm.NodeAdded -= nodeAdded;
            vm.PropertyChanged -= selected;
            _layoutCancel?.Cancel();
        });
        Resized += OnResized;
        OnGraphChanged();
    }

    /// <summary>Raised by a double-click on a link: its relation is to be opened.</summary>
    public event Action<RelationGraphEdge>? EdgeDoubleClicked;

    /// <summary>Raised by a double-click on an element.</summary>
    public event Action<RelationGraphNode>? NodeDoubleClicked;

    /// <summary>Raised when a red string ends: a relation from the first element to the others joined. The string stays until <see cref="EndString"/>.</summary>
    public event Action<RelationGraphNode, IReadOnlyList<RelationGraphNode>>? RelationRequested;

    /// <summary>True while a red string is drawn (or shown with its relation popup).</summary>
    public bool IsStringActive => _stringFrom is not null;

    // ---- Layout ----

    private void OnResized()
    {
        if (_lastSize.X <= 0 && Size.X > 0)
            Fit();
        _lastSize = Size;
    }

    /// <summary>An element added by hand: placed in the middle of the view, the forces make room for it.</summary>
    private void OnNodeAdded(RelationGraphNode node)
    {
        var center = ToWorld(Size / 2);
        var count = _vm?.Nodes.Count ?? 1;
        node.X = center.X + 30 * Math.Cos(count);
        node.Y = center.Y + 30 * Math.Sin(count);
        node.VelocityX = node.VelocityY = 0;
        Animate(50);
        QueueRedraw();
    }

    /// <summary>A new graph: its elements placed on a circle, settled by the forces, fitted to the view.</summary>
    private void OnGraphChanged()
    {
        if (_vm is null || !IsInstanceValid(this))
            return;
        var nodes = _vm.Nodes;
        var radius = 60 + 40 * Math.Sqrt(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            var angle = 2 * Math.PI * i / Math.Max(1, nodes.Count);
            nodes[i].X = radius * Math.Cos(angle);
            nodes[i].Y = radius * Math.Sin(angle);
            nodes[i].VelocityX = nodes[i].VelocityY = 0;
        }
        _bends.Clear();
        _stepsLeft = 0;
        _layoutCancel?.Cancel();
        _layoutCancel = null;
        _layoutTask = null;
        _layoutTotal = _layoutLeft = nodes.Count <= 1 ? 0 : nodes.Count > 500 ? 250 : 400;
        _layoutDone = 0;
        _vm.LayoutProgress = 0;
        _vm.IsLayingOut = _layoutLeft > 0;
        if (_layoutLeft == 0)
            FinishLayout(ComputeBends(nodes, _vm.Edges));
        else
        {
            // The forces, the overlaps and the routing run on a worker thread: the window stays alive, the bar moves.
            var cancel = _layoutCancel = new System.Threading.CancellationTokenSource();
            var edges = _vm.Edges;
            var total = _layoutTotal;
            _layoutTask = Task.Run(() => RunLayout(nodes, edges, total, cancel.Token));
        }
        if (_stringFrom is not null && !_stringDialogOpen)
            EndString();
        QueueRedraw();
    }

    /// <summary>The layout of a new graph, on a worker thread: the steps of the forces, the overlaps, then the routing of the links.</summary>
    private Dictionary<(RelationGraphEdge, int), float>? RunLayout(IReadOnlyList<RelationGraphNode> nodes, IReadOnlyList<RelationGraphEdge> edges,
        int total, System.Threading.CancellationToken cancel)
    {
        for (var step = 0; step < total; step++)
        {
            if (cancel.IsCancellationRequested)
                return null;
            StepCore(nodes, edges, 1, total - step);
            System.Threading.Volatile.Write(ref _layoutDone, step + 1);
        }
        ResolveOverlaps(nodes, 60);
        return cancel.IsCancellationRequested ? null : ComputeBends(nodes, edges);
    }

    /// <summary>Follows the layout running on the worker thread; once done, takes its routing and fits the graph to the view.</summary>
    private void FollowLayout(Task<Dictionary<(RelationGraphEdge, int), float>?> task)
    {
        if (_vm is not null)
            _vm.LayoutProgress = 95.0 * System.Threading.Volatile.Read(ref _layoutDone) / Math.Max(1, _layoutTotal);
        if (!task.IsCompleted)
            return;
        _layoutTask = null;
        if (task.IsCompletedSuccessfully && task.Result is { } bends)
            FinishLayout(bends);
        else if (task.Exception is { } error)
        {
            GD.PushError("Relation graph layout: " + error.InnerException?.Message);
            FinishLayout([]);
        }
    }

    /// <summary>The layout done: the routing of the links kept, the whole graph fitted to the view.</summary>
    private void FinishLayout(Dictionary<(RelationGraphEdge, int), float> bends)
    {
        _bends = bends;
        _layoutLeft = 0;
        Fit();
        if (_vm is not null)
        {
            _vm.LayoutProgress = 100;
            _vm.IsLayingOut = false;
        }
        QueueRedraw();
    }

    /// <summary>
    /// Zooms by <paramref name="scale"/> (above 1: closer) around <paramref name="at"/> (a point of the view, its centre
    /// by default), which stays where it is.
    /// </summary>
    public void ZoomBy(float scale, Vector2? at = null)
    {
        var point = at ?? Size / 2;
        var world = ToWorld(point);
        _zoom = Math.Clamp(_zoom * scale, 0.03f, 3f);
        _offset = point - world * _zoom;
        QueueRedraw();
    }

    /// <summary>Zooms and moves the view so that the whole graph shows.</summary>
    public void Fit()
    {
        if (_vm is not { Nodes.Count: > 0 } vm || Size.X <= 0)
        {
            _zoom = 1;
            _offset = Size / 2;
            QueueRedraw();
            return;
        }
        var minX = vm.Nodes.Min(n => n.X) - NodeWidth / 2 - 40;
        var maxX = vm.Nodes.Max(n => n.X) + NodeWidth / 2 + 40;
        var minY = vm.Nodes.Min(n => n.Y) - NodeHeight / 2 - 40;
        var maxY = vm.Nodes.Max(n => n.Y) + NodeHeight / 2 + 40;
        _zoom = (float)Math.Clamp(Math.Min(Size.X / (maxX - minX), Size.Y / (maxY - minY)), 0.03, 1.6);
        _offset = new Vector2(Size.X / 2 - (float)((minX + maxX) / 2) * _zoom, Size.Y / 2 - (float)((minY + maxY) / 2) * _zoom);
        QueueRedraw();
    }

    /// <summary>Steps of the force layout: repulsion between the elements, springs along the links, a pull to the centre.</summary>
    private void Step(int steps)
    {
        if (_vm is { } vm)
            StepCore(vm.Nodes, vm.Edges, steps, 0);
    }

    /// <summary>
    /// <paramref name="steps"/> steps of the forces on <paramref name="nodes"/> (also run on the worker thread of a new
    /// graph); <paramref name="phase"/> spaces the passes against the overlaps of a big graph.
    /// </summary>
    private static void StepCore(IReadOnlyList<RelationGraphNode> nodes, IReadOnlyList<RelationGraphEdge> edges, int steps, int phase)
    {
        const double repulsion = 120000;
        const double springLength = 430;
        const double springStrength = 0.025;
        const double gravity = 0.004;
        const double damping = 0.82;
        var count = nodes.Count;
        var x = new double[count];
        var y = new double[count];
        var fx = new double[count];
        var fy = new double[count];
        for (var step = 0; step < steps; step++)
        {
            for (var i = 0; i < count; i++)
            {
                x[i] = nodes[i].X;
                y[i] = nodes[i].Y;
                fx[i] = -x[i] * gravity;
                fy[i] = -y[i] * gravity;
            }
            // The push of all the others on each element: the elements shared among the cores of the processor.
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = count < 200 ? 1 : -1 }, i =>
            {
                double sumX = 0, sumY = 0;
                var xi = x[i];
                var yi = y[i];
                for (var j = 0; j < count; j++)
                {
                    if (j == i)
                        continue;
                    var dx = xi - x[j];
                    var dy = yi - y[j];
                    var distance2 = Math.Max(dx * dx + dy * dy, 100);
                    var scale = repulsion / (distance2 * Math.Sqrt(distance2));
                    sumX += dx * scale;
                    sumY += dy * scale;
                }
                fx[i] += sumX;
                fy[i] += sumY;
            });
            for (var i = 0; i < count; i++)
            {
                nodes[i].VelocityX = (nodes[i].VelocityX + fx[i]) * damping;
                nodes[i].VelocityY = (nodes[i].VelocityY + fy[i]) * damping;
            }
            foreach (var edge in edges)
            {
                if (ReferenceEquals(edge.From, edge.To))
                    continue;
                var dx = edge.To.X - edge.From.X;
                var dy = edge.To.Y - edge.From.Y;
                var distance = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                var force = (distance - springLength) * springStrength;
                edge.From.VelocityX += dx / distance * force;
                edge.From.VelocityY += dy / distance * force;
                edge.To.VelocityX -= dx / distance * force;
                edge.To.VelocityY -= dy / distance * force;
            }
            foreach (var node in nodes.Where(n => !n.Pinned))
            {
                node.X += Math.Clamp(node.VelocityX, -40, 40);
                node.Y += Math.Clamp(node.VelocityY, -40, 40);
            }
            // A pass against the overlaps every few steps (a big graph); the end of the layout makes them all.
            if (count < 300 || (phase + step) % 4 == 0)
                ResolveOverlaps(nodes, 1);
        }
    }

    /// <summary>
    /// Pushes apart the cards that overlap (with <see cref="CardGap"/> around them), along the axis where they overlap the
    /// least; a pinned card (being dragged) stays put and the other one moves the whole way.
    /// </summary>
    private static void ResolveOverlaps(IReadOnlyList<RelationGraphNode> nodes, int passes)
    {
        const double width = NodeWidth + CardGap;
        const double height = NodeHeight + CardGap;
        var sorted = nodes.ToArray();
        for (var pass = 0; pass < passes; pass++)
        {
            var moved = false;
            // Sorted from left to right: only the cards less than a width apart can overlap.
            Array.Sort(sorted, (p, q) => p.X.CompareTo(q.X));
            for (var i = 0; i < sorted.Length; i++)
                for (var j = i + 1; j < sorted.Length && sorted[j].X - sorted[i].X < width; j++)
                {
                    var a = sorted[i];
                    var b = sorted[j];
                    var dx = b.X - a.X;
                    var dy = b.Y - a.Y;
                    var overlapX = width - Math.Abs(dx);
                    var overlapY = height - Math.Abs(dy);
                    if (overlapX <= 0 || overlapY <= 0 || (a.Pinned && b.Pinned))
                        continue;
                    moved = true;
                    // Same place: split them apart on a direction of their own.
                    if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
                        dx = (i + j) % 2 == 0 ? 1 : -1;
                    double pushX = 0, pushY = 0;
                    if (overlapX / width < overlapY / height)
                        pushX = Math.Sign(dx) * overlapX;
                    else
                        pushY = Math.Sign(dy == 0 ? 1 : dy) * overlapY;
                    var shareA = a.Pinned ? 0 : b.Pinned ? 1 : 0.5;
                    a.X -= pushX * shareA;
                    a.Y -= pushY * shareA;
                    b.X += pushX * (1 - shareA);
                    b.Y += pushY * (1 - shareA);
                }
            if (!moved)
                return;
        }
    }

    /// <summary>The bend of a link before any routing: a light arc, the links of a same pair fanned out.</summary>
    private static float DefaultBend(float length, int index) =>
        Math.Min(0.12f * length, MaxBend / 3) + (index == 0 ? 0 : (index % 2 == 1 ? 1 : -1) * ((index + 1) / 2) * 46);

    /// <summary>
    /// Chooses the bend of every link: among a few arcs on either side, the one whose curve crosses the fewest other cards,
    /// the gentlest on a tie.
    /// </summary>
    private void RouteEdges()
    {
        if (_vm is { } vm)
            _bends = ComputeBends(vm.Nodes, vm.Edges);
    }

    /// <summary>The bends of <see cref="RouteEdges"/>, the links shared among the cores (also run on the worker thread).</summary>
    private static Dictionary<(RelationGraphEdge, int), float> ComputeBends(IReadOnlyList<RelationGraphNode> nodes, IReadOnlyList<RelationGraphEdge> edges)
    {
        var cards = nodes.Select(n => (Node: n, Box: new Rect2((float)n.X - NodeWidth / 2 - 6, (float)n.Y - NodeHeight / 2 - 6, NodeWidth + 12, NodeHeight + 12))).ToArray();
        // The rank of each link among the links of its pair, in the order they are drawn.
        var pairs = new Dictionary<(string, string), int>();
        var ranked = new List<(RelationGraphEdge Edge, int Index)>();
        foreach (var edge in edges)
        {
            var key = string.CompareOrdinal(edge.From.Key, edge.To.Key) < 0 ? (edge.From.Key, edge.To.Key) : (edge.To.Key, edge.From.Key);
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
            if (!ReferenceEquals(edge.From, edge.To))
                ranked.Add((edge, pairs[key] - 1));
        }
        var chosen = new float[ranked.Count];
        Parallel.For(0, ranked.Count, n => chosen[n] = BestBend(ranked[n].Edge, ranked[n].Index, cards));
        var bends = new Dictionary<(RelationGraphEdge, int), float>(ranked.Count);
        for (var n = 0; n < ranked.Count; n++)
            bends[(ranked[n].Edge, ranked[n].Index)] = chosen[n];
        return bends;
    }

    private static readonly float[] Arcs = [0.12f, -0.12f, 0.25f, -0.25f, 0.4f, -0.4f, 0.6f, -0.6f];

    /// <summary>The bend of one link: the first arc that crosses no other card, else the one that crosses the fewest.</summary>
    private static float BestBend(RelationGraphEdge edge, int index, (RelationGraphNode Node, Rect2 Box)[] cards)
    {
        {
            var arcs = Arcs;
            var from = new Vector2((float)edge.From.X, (float)edge.From.Y);
            var to = new Vector2((float)edge.To.X, (float)edge.To.Y);
            var direction = to - from;
            var length = Math.Max(1, direction.Length());
            var normal = new Vector2(-direction.Y / length, direction.X / length);
            var fan = DefaultBend(length, index) - Math.Min(0.12f * length, MaxBend / 3);
            var best = DefaultBend(length, index);
            var fewest = int.MaxValue;
            foreach (var arc in arcs)
            {
                var bend = Math.Clamp(arc * length, -MaxBend, MaxBend) + fan;
                var control = (from + to) / 2 + normal * bend * 2;
                // The curve stays in the box of its three points: the cards out of it are not tested.
                var area = new Rect2(from, Vector2.Zero).Expand(to).Expand(control);
                var hits = 0;
                foreach (var (node, box) in cards)
                {
                    if (ReferenceEquals(node, edge.From) || ReferenceEquals(node, edge.To) || !area.Intersects(box))
                        continue;
                    for (var i = 1; i < 12; i++)
                        if (box.HasPoint(Bezier(from, control, to, i / 12f)))
                        {
                            hits++;
                            break;
                        }
                }
                if (hits < fewest)
                {
                    fewest = hits;
                    best = bend;
                }
                if (hits == 0)
                    break;
            }
            return best;
        }
    }

    /// <summary>Runs the animation of the forces for a while (after a drag).</summary>
    private void Animate(int ticks = 60) => _stepsLeft = Math.Max(_stepsLeft, ticks);

    public override void _Process(double delta)
    {
        if (_snapshot)
            return;
        if (_layoutTask is { } layout)
        {
            FollowLayout(layout);
            return;
        }
        // A card held but not moved (a click) starts no animation: the other cards stay still.
        var dragging = _dragged is not null && _moved;
        if (_stepsLeft <= 0 && !dragging)
            return;
        _tick += delta;
        if (_tick < 0.03)
            return;
        _tick = 0;
        Step(3);
        QueueRedraw();
        if (_stepsLeft > 0)
            _stepsLeft--;
        if (_stepsLeft == 0 && !dragging)
            RouteEdges();
    }

    private Vector2 ToScreen(double x, double y) => new((float)x * _zoom + _offset.X, (float)y * _zoom + _offset.Y);

    private Vector2 ToWorld(Vector2 p) => (p - _offset) / _zoom;

    private Rect2 CardOf(RelationGraphNode node)
    {
        var center = ToScreen(node.X, node.Y);
        return new Rect2(center.X - NodeWidth * _zoom / 2, center.Y - NodeHeight * _zoom / 2, NodeWidth * _zoom, NodeHeight * _zoom);
    }

    private RelationGraphNode? NodeAt(Vector2 p) => _vm?.Nodes.LastOrDefault(n => CardOf(n).HasPoint(p));

    /// <summary>The link icon of a card, at its top right.</summary>
    private Rect2 LinkIconOf(RelationGraphNode node)
    {
        var card = CardOf(node);
        var size = 20 * _zoom;
        return new Rect2(card.End.X - size - 5 * _zoom, card.Position.Y + 5 * _zoom, size, size);
    }

    private RelationGraphNode? LinkIconAt(Vector2 p) =>
        _zoom < 0.3f ? null : _vm?.Nodes.LastOrDefault(n => LinkIconOf(n).Grow(4).HasPoint(p));

    /// <summary>A colour per type of element, the same one for a type in every search.</summary>
    internal static Color ColorOf(string type)
    {
        var hash = type.ToLowerInvariant().Aggregate(17, (h, c) => unchecked(h * 31 + c));
        return Palette[Math.Abs(hash % Palette.Length)];
    }

    /// <summary>Red for an impossible relation, grey to evaluate, green for a certain one.</summary>
    internal static Color ColorOf(int probability)
    {
        var t = Math.Clamp(probability / 100f, -1, 1);
        var grey = new Color("#8A939E");
        return t < 0 ? grey.Lerp(new Color("#E5484D"), -t) : grey.Lerp(new Color("#30C48D"), t);
    }

    // ---- The red string ----

    /// <summary>Starts a red string from the link icon of <paramref name="node"/>.</summary>
    public void StartString(RelationGraphNode node)
    {
        _stringFrom = node;
        _stringJoined.Clear();
        _stringPointer = null;
        _stringDialogOpen = false;
        GrabFocus();
        QueueRedraw();
    }

    /// <summary>Removes the string (its relation popup was saved or closed, or it was cancelled).</summary>
    public void EndString()
    {
        _stringFrom = null;
        _stringJoined.Clear();
        _stringPointer = null;
        _stringDialogOpen = false;
        if (IsInstanceValid(this))
            QueueRedraw();
    }

    private void JoinString(RelationGraphNode node)
    {
        if (ReferenceEquals(node, _stringFrom) || _stringJoined.Contains(node))
            return;
        _stringJoined.Add(node);
        QueueRedraw();
    }

    /// <summary>A click while the string is drawn: on a link icon it joins (and links), elsewhere it cancels an empty string.</summary>
    private void StringClicked(Vector2 p, bool doubleClick)
    {
        var number = ++_stringClick;
        if (LinkIconAt(p) is not { } node)
        {
            if (_stringJoined.Count == 0)
                EndString();
            return;
        }
        JoinString(node);
        if (!doubleClick)
            _ = FinishStringUnlessClickedAgain(number);
    }

    /// <summary>A click on a link icon waits a little for a second one (a double-click goes on instead of linking).</summary>
    private async Task FinishStringUnlessClickedAgain(int number)
    {
        await ToSignal(GetTree().CreateTimer(0.32), SceneTreeTimer.SignalName.Timeout);
        if (IsInstanceValid(this) && number == _stringClick && _stringFrom is not null && !_stringDialogOpen)
            FinishString();
    }

    private void FinishString()
    {
        if (_stringFrom is not { } from || _stringJoined.Count == 0 || _stringDialogOpen)
            return;
        _stringDialogOpen = true;
        _stringPointer = null;
        QueueRedraw();
        RelationRequested?.Invoke(from, _stringJoined.ToList());
    }

    // ---- Drawing ----

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), Background);
        DrawGrid(size);
        if (_vm is not { } vm)
            return;
        if (vm.Nodes.Count == 0)
        {
            var font = Eve.Eve.Body;
            var text = "No relation to show";
            var width = font.GetStringSize(text, fontSize: 14).X;
            DrawString(font, new Vector2((size.X - width) / 2, size.Y / 2), text, HorizontalAlignment.Left, -1, 14, Eve.Eve.Muted);
            return;
        }

        _edgeShapes.Clear();
        _labelBoxes.Clear();
        if (_layoutLeft > 0)
            return;
        var pairs = new Dictionary<(string, string), int>();
        foreach (var edge in vm.Edges)
        {
            var key = string.CompareOrdinal(edge.From.Key, edge.To.Key) < 0 ? (edge.From.Key, edge.To.Key) : (edge.To.Key, edge.From.Key);
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
            // An element selected: the links toward its 1st and 2nd degree stay, the others fade.
            var alpha = edge.MatchesFilters ? 1 : FadedOpacity;
            if (vm.HasProximity && ProximityOf(vm, edge) is null)
                alpha *= ProximityFade;
            DrawEdge(edge, pairs[key] - 1, alpha);
            if (vm.HasProximity && ProximityOf(vm, edge) is { } level)
                DrawProximityEdge(edge, level);
        }
        DrawClusters();
        foreach (var node in vm.Nodes)
            DrawNode(node, ReferenceEquals(node, vm.SelectedNode), ReferenceEquals(node, _hovered));
        // The neighbours of the selected element lit (cyan: 1st degree, green: 2nd degree), the others faded.
        if (vm.HasProximity)
            foreach (var node in vm.Nodes)
                DrawProximity(node, vm.DistanceOf(node));
        DrawStringThread();
        DrawPen();
        if (_texts.Count > 3000)
            _texts.Clear();
    }

    /// <summary>A faint grid behind the graph, moving and zooming with it.</summary>
    private void DrawGrid(Vector2 size)
    {
        var step = 80 * _zoom;
        if (step < 12)
            return;
        var color = Eve.Eve.Edge with { A = 0.08f };
        for (var x = Mathf.PosMod(_offset.X, step); x < size.X; x += step)
            DrawLine(new Vector2(x, 0), new Vector2(x, size.Y), color);
        for (var y = Mathf.PosMod(_offset.Y, step); y < size.Y; y += step)
            DrawLine(new Vector2(0, y), new Vector2(size.X, y), color);
    }

    /// <summary>A text laid out in <paramref name="width"/> on at most <paramref name="lines"/> lines, with an ellipsis.</summary>
    private TextParagraph Text(string text, Font font, float fontSize, float width, int lines)
    {
        var size = Math.Max(1, (int)Math.Round(fontSize));
        var key = (text, size, (int)width, lines, font.GetHashCode());
        if (_texts.TryGetValue(key, out var paragraph))
            return paragraph;
        paragraph = new TextParagraph
        {
            Width = Math.Max(1, width),
            MaxLinesVisible = lines,
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

    private static Vector2 Bezier(Vector2 a, Vector2 c, Vector2 b, float t) => (1 - t) * (1 - t) * a + 2 * (1 - t) * t * c + t * t * b;

    private void DrawEdge(RelationGraphEdge edge, int index, float alpha)
    {
        if (ReferenceEquals(edge.From, edge.To))
        {
            DrawLoop(edge, index, alpha);
            return;
        }
        var from = ToScreen(edge.From.X, edge.From.Y);
        var to = ToScreen(edge.To.X, edge.To.Y);
        var color = ColorOf(edge.Relation.Current.Probability) with { A = alpha };
        var width = Math.Max(1.2f, 2 * _zoom);
        var direction = to - from;
        var length = Math.Max(1, direction.Length());
        var normal = new Vector2(-direction.Y / length, direction.X / length);
        var bend = (_bends.TryGetValue((edge, index), out var routed) ? routed : DefaultBend(length / _zoom, index)) * _zoom;
        var control = (from + to) / 2 + normal * bend * 2;
        var middle = (from + to) / 2 + normal * bend;

        var end = Clip(control, to, CardOf(edge.To));
        var points = Enumerable.Range(0, 21).Select(i => Bezier(from, control, end, i / 20f)).ToList();
        DrawPolyline(points.ToArray(), color, width, true);
        var tangent = end - control;
        var unit = tangent.Length() < 0.001f ? Vector2.Right : tangent.Normalized();
        var arrowSize = Math.Max(7, 11 * _zoom);
        var side = new Vector2(-unit.Y, unit.X) * arrowSize * 0.5f;
        DrawColoredPolygon([end, end - unit * arrowSize + side, end - unit * arrowSize - side], color);
        _edgeShapes.Add((edge, points, default));

        if (_zoom < 0.35f)
            return;
        var label = Text(edge.ShortLabel, Eve.Eve.Body, Math.Max(9, 11 * _zoom), 200 * _zoom, 2);
        var labelSize = new Vector2(TextWidth(label), LineHeight(label));
        var box = new Rect2(middle.X - labelSize.X / 2 - 5, middle.Y - labelSize.Y / 2 - 2, labelSize.X + 10, labelSize.Y + 4);
        if (!PlaceLabel(box, alpha))
            return;
        DrawLabelBox(box, color, alpha);
        label.Draw(GetCanvasItem(), box.Position + new Vector2(5, 2), Eve.Eve.TextBright with { A = alpha });
        _edgeShapes[^1] = (edge, points, box);
    }

    /// <summary>
    /// Whether a label fits at <paramref name="box"/>: not over another label nor over a card (the link stays, its
    /// title shows on hover); a faded link writes no label at all.
    /// </summary>
    private bool PlaceLabel(Rect2 box, float alpha)
    {
        if (alpha < 0.5f || _vm is not { } vm)
            return false;
        if (_labelBoxes.Any(b => b.Intersects(box)) || vm.Nodes.Any(n => CardOf(n).Intersects(box)))
            return false;
        _labelBoxes.Add(box);
        return true;
    }

    private void DrawLabelBox(Rect2 box, Color edge, float alpha)
    {
        var style = new StyleBoxFlat { BgColor = (Eve.Eve.IsLight ? Colors.White : new Color(0.05f, 0.07f, 0.09f)) with { A = 0.95f * alpha }, BorderColor = edge, AntiAliasing = true };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        DrawStyleBox(style, box);
    }

    /// <summary>A relation from an element to itself: a loop above the right of its card, with its title.</summary>
    private void DrawLoop(RelationGraphEdge edge, int index, float alpha)
    {
        var card = CardOf(edge.From);
        var color = ColorOf(edge.Relation.Current.Probability) with { A = alpha };
        var size = (34 + 16 * index) * _zoom;
        var start = new Vector2(card.End.X - 40 * _zoom, card.Position.Y);
        var end = new Vector2(card.End.X, card.Position.Y + 22 * _zoom);
        var c1 = new Vector2(start.X, start.Y - size * 1.6f);
        var c2 = new Vector2(end.X + size * 1.6f, end.Y);
        var points = Enumerable.Range(0, 21).Select(i =>
        {
            var t = i / 20f;
            var u = 1 - t;
            return u * u * u * start + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * end;
        }).ToList();
        DrawPolyline(points.ToArray(), color, Math.Max(1.2f, 2 * _zoom), true);
        _edgeShapes.Add((edge, points, default));
        if (_zoom < 0.35f)
            return;
        var label = Text(edge.ShortLabel, Eve.Eve.Body, Math.Max(9, 11 * _zoom), 200 * _zoom, 1);
        var labelSize = new Vector2(TextWidth(label), LineHeight(label));
        var box = new Rect2(card.End.X + size * 0.4f, card.Position.Y - size * 1.1f - labelSize.Y / 2, labelSize.X + 10, labelSize.Y + 4);
        if (!PlaceLabel(box, alpha))
            return;
        DrawLabelBox(box, color, alpha);
        label.Draw(GetCanvasItem(), box.Position + new Vector2(5, 2), Eve.Eve.TextBright with { A = alpha });
        _edgeShapes[^1] = (edge, points, box);
    }

    /// <summary>Where the segment from <paramref name="from"/> to the centre of <paramref name="card"/> meets its edge.</summary>
    private static Vector2 Clip(Vector2 from, Vector2 center, Rect2 card)
    {
        var d = center - from;
        if (Math.Abs(d.X) < 0.001f && Math.Abs(d.Y) < 0.001f)
            return center;
        var tx = Math.Abs(d.X) < 0.001f ? float.MaxValue : card.Size.X / 2 / Math.Abs(d.X);
        var ty = Math.Abs(d.Y) < 0.001f ? float.MaxValue : card.Size.Y / 2 / Math.Abs(d.Y);
        var t = Math.Min(1, Math.Min(tx, ty));
        return center - d * t;
    }

    /// <summary>The opacity left to the elements and links beyond the second degree of the selected element.</summary>
    private const float ProximityFade = 0.25f;

    private static Color FirstDegree => Eve.Eve.Cyan;
    private static Color SecondDegree => Eve.Eve.Green;

    /// <summary>
    /// The level of a link near the selected element: 1 when it joins the selected element to a neighbour, 2 when it
    /// joins a neighbour of the 1st degree to one of the 2nd; null for the others.
    /// </summary>
    private static int? ProximityOf(RelationGraphViewModel vm, RelationGraphEdge edge)
    {
        if (vm.DistanceOf(edge.From) is not { } a || vm.DistanceOf(edge.To) is not { } b)
            return null;
        return (Math.Min(a, b), Math.Max(a, b)) switch { (0, 1) => 1, (1, 2) => 2, (0, 0) => 1, _ => null };
    }

    /// <summary>A link near the selected element, drawn over in the colour of its degree.</summary>
    private void DrawProximityEdge(RelationGraphEdge edge, int level)
    {
        if (ReferenceEquals(edge.From, edge.To))
            return;
        var color = (level == 1 ? FirstDegree : SecondDegree) with { A = level == 1 ? 0.75f : 0.55f };
        // Drawn right after the link itself: over the same curve.
        Vector2[] points = _edgeShapes.Count > 0 && ReferenceEquals(_edgeShapes[^1].Edge, edge) ? [.. _edgeShapes[^1].Points]
            : [CardOf(edge.From).GetCenter(), CardOf(edge.To).GetCenter()];
        DrawPolyline(points, color, Math.Max(1.5f, (level == 1 ? 3.5f : 2.5f) * _zoom), true);
    }

    /// <summary>An element seen from the selected one: a halo in the colour of its degree, or a veil beyond the 2nd degree.</summary>
    private void DrawProximity(RelationGraphNode node, int? distance)
    {
        var card = CardOf(node);
        switch (distance)
        {
            case 0:
                return;
            case 1:
            case 2:
                var color = distance == 1 ? FirstDegree : SecondDegree;
                DrawRect(card.Grow(4), color with { A = 0.85f }, false, Math.Max(1.5f, (distance == 1 ? 2.5f : 2f) * _zoom));
                DrawRect(card.Grow(8), color with { A = 0.25f }, false, Math.Max(1f, 1.5f * _zoom));
                DrawCircle(new Vector2(card.End.X - 2, card.Position.Y + 2), Math.Max(5, 8 * _zoom), color);
                var badge = Text(distance == 1 ? "1" : "2", BoldFont, Math.Max(7, 9 * _zoom), 40, 1);
                badge.Draw(GetCanvasItem(), new Vector2(card.End.X - 2 - TextWidth(badge) / 2, card.Position.Y + 2 - LineHeight(badge) / 2), Colors.Black);
                return;
            default:
                DrawRect(card.Grow(2), Background with { A = 1 - ProximityFade }, true);
                return;
        }
    }

    /// <summary>What an element is, at the top of its card: the entity type of an entity of the ontology (PERSON, LOCATION…), else its window.</summary>
    private static string KindOf(RelationGraphNode node) =>
        (node.Type == RelationGraphViewModel.EntityType && !string.IsNullOrWhiteSpace(node.EntityType) ? node.EntityType.Trim() : node.Type).ToUpperInvariant();

    private void DrawNode(RelationGraphNode node, bool selected, bool hovered)
    {
        var card = CardOf(node);
        var color = RelationGraph3D.ColorOf(node);
        var style = new StyleBoxFlat
        {
            BgColor = Eve.Eve.IsLight ? Colors.White : new Color(0.055f, 0.07f, 0.085f, 0.97f),
            BorderColor = selected ? Eve.Eve.Amber : hovered ? color : color with { A = 0.7f },
            ShadowColor = Eve.Eve.Sunken(0.5f),
            ShadowSize = 6,
            AntiAliasing = true
        };
        style.SetBorderWidthAll(selected ? 2 : 1);
        style.SetCornerRadiusAll((int)Math.Max(1, 4 * _zoom));
        DrawStyleBox(style, card);
        DrawRect(new Rect2(card.Position.X + 1, card.Position.Y + 1, Math.Max(2, 4 * _zoom), card.Size.Y - 2), color);
        if (selected)
            DrawRect(card.Grow(3), Eve.Eve.Amber with { A = 0.35f }, false, 1);
        if (_zoom < 0.3f)
            return;
        var pad = 10 * _zoom;
        var width = card.Size.X - 2 * pad;
        var type = Text(KindOf(node), Eve.Eve.Display, 9.5f * _zoom, Math.Max(1, width - 24 * _zoom), 1);
        var title = Text(node.Title, SemiBold, 12 * _zoom, width, 2);
        var id = Text(node.ObjectId is { } objectId ? "id " + (objectId.Length > 24 ? objectId[..24] + "…" : objectId) : "no id", Eve.Eve.Mono, 9 * _zoom, width, 1);
        var canvas = GetCanvasItem();
        type.Draw(canvas, new Vector2(card.Position.X + pad, card.Position.Y + 6 * _zoom), color);
        title.Draw(canvas, new Vector2(card.Position.X + pad, card.Position.Y + 6 * _zoom + LineHeight(type) + 2 * _zoom), Eve.Eve.TextBright);
        id.Draw(canvas, new Vector2(card.Position.X + pad, card.End.Y - LineHeight(id) - 6 * _zoom), Eve.Eve.Muted);
        if (node.Titles.Count > 1)
        {
            // The number of ends merged into the element.
            var count = Text($"×{node.Titles.Count}", BoldFont, 9 * _zoom, 200, 1);
            var size = new Vector2(TextWidth(count), LineHeight(count));
            var badge = new Rect2(LinkIconOf(node).Position.X - size.X - 14 * _zoom, card.Position.Y + 7 * _zoom, size.X + 8 * _zoom, size.Y + 2 * _zoom);
            DrawRect(badge, color);
            count.Draw(canvas, badge.Position + new Vector2(4 * _zoom, 1 * _zoom), Colors.White);
        }
        DrawLinkIcon(node);
    }

    /// <summary>The link icon of a card: two links of a chain (lit while its element is an end of the string).</summary>
    private void DrawLinkIcon(RelationGraphNode node)
    {
        var icon = LinkIconOf(node);
        var lit = ReferenceEquals(node, _stringFrom) || _stringJoined.Contains(node);
        if (lit || ReferenceEquals(node, _hoveredLink))
            DrawRect(icon.Grow(2), lit ? StringColor with { A = 0.25f } : Eve.Eve.Raised(0.08f));
        var color = lit ? StringColor : Eve.Eve.Text with { A = 0.85f };
        var center = icon.GetCenter();
        var axis = new Vector2(1, -1).Normalized();
        var unit = icon.Size.X / 24;
        foreach (var shift in new[] { -3.2f, 3.2f })
            DrawPolyline(Stadium(center + axis * shift * unit, axis, 5.5f * unit, 2.6f * unit), color, Math.Max(1, 1.8f * unit), true);
    }

    /// <summary>The outline of a rounded link of chain: <paramref name="half"/> long on each side of its centre along <paramref name="axis"/>.</summary>
    private static Vector2[] Stadium(Vector2 center, Vector2 axis, float half, float radius)
    {
        var normal = new Vector2(-axis.Y, axis.X);
        var points = new List<Vector2>();
        var straight = half - radius;
        for (var i = 0; i <= 10; i++)
        {
            var a = -Mathf.Pi / 2 + Mathf.Pi * i / 10;
            points.Add(center + axis * (straight + radius * Mathf.Cos(a)) + normal * (radius * Mathf.Sin(a)));
        }
        for (var i = 0; i <= 10; i++)
        {
            var a = Mathf.Pi / 2 + Mathf.Pi * i / 10;
            points.Add(center + axis * (-straight + radius * Mathf.Cos(a)) + normal * (radius * Mathf.Sin(a)));
        }
        points.Add(points[0]);
        return points.ToArray();
    }

    /// <summary>The thread from one point to another, hanging a little between them.</summary>
    private void DrawThread(Vector2 start, Vector2 end)
    {
        var length = (end - start).Length();
        var middle = new Vector2((start.X + end.X) / 2, Math.Max(start.Y, end.Y) + Math.Min(120, length * 0.18f));
        var points = Enumerable.Range(0, 25).Select(i => Bezier(start, middle, end, i / 24f)).ToArray();
        DrawPolyline(points, StringColor, 3, true);
    }

    /// <summary>The string: from the first link icon through the joined ones to the pointer, ringing the icon under it.</summary>
    private void DrawStringThread()
    {
        if (_stringFrom is not { } from)
            return;
        var points = new List<Vector2> { LinkIconOf(from).GetCenter() };
        points.AddRange(_stringJoined.Select(n => LinkIconOf(n).GetCenter()));
        if (_stringPointer is { } pointer)
            points.Add(pointer);
        for (var i = 1; i < points.Count; i++)
            DrawThread(points[i - 1], points[i]);
        DrawCircle(points[0], 4, StringColor);
        foreach (var node in _stringJoined)
        {
            var icon = LinkIconOf(node);
            DrawCircle(icon.GetCenter(), 4, StringColor);
            DrawDashedRing(icon.GetCenter(), icon.Size.X / 2 + 7);
        }
        if (_stringPointer is { } at && LinkIconAt(at) is { } target && !ReferenceEquals(target, from))
        {
            var icon = LinkIconOf(target);
            DrawArc(icon.GetCenter(), icon.Size.X / 2 + 7, 0, Mathf.Tau, 32, StringColor, 2.5f, true);
        }
    }

    private void DrawDashedRing(Vector2 center, float radius)
    {
        const int dashes = 14;
        for (var i = 0; i < dashes; i++)
        {
            var a = Mathf.Tau * i / dashes;
            DrawArc(center, radius, a, a + Mathf.Tau / dashes * 0.6f, 4, StringColor, 2, true);
        }
    }

    // ---- Hit tests ----

    /// <summary>The link under <paramref name="p"/>: on its title, or within a few pixels of its line.</summary>
    private RelationGraphEdge? EdgeAt(Vector2 p)
    {
        foreach (var (edge, _, label) in Enumerable.Reverse(_edgeShapes))
            if (label.HasArea() && label.HasPoint(p))
                return edge;
        foreach (var (edge, points, _) in Enumerable.Reverse(_edgeShapes))
            for (var i = 1; i < points.Count; i++)
                if (Geometry2D.GetClosestPointToSegment(p, points[i - 1], points[i]).DistanceTo(p) < 7)
                    return edge;
        return null;
    }

    // ---- Input ----

    public override void _GuiInput(InputEvent e)
    {
        if (_vm is null || _snapshot || _layoutLeft > 0)
            return;
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel:
            {
                var factor = wheel.Factor > 0 ? wheel.Factor : 1;
                ZoomBy(wheel.ButtonIndex == MouseButton.WheelUp ? Mathf.Pow(1.12f, factor) : 1 / Mathf.Pow(1.12f, factor), wheel.Position);
                AcceptEvent();
                break;
            }
            // The trackpad: a pinch, or two fingers up and down, zoom too.
            case InputEventMagnifyGesture magnify:
                ZoomBy(magnify.Factor, magnify.Position);
                AcceptEvent();
                break;
            case InputEventPanGesture pan:
                ZoomBy(Mathf.Pow(1.06f, -pan.Delta.Y), pan.Position);
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
                Moved(motion.Position);
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Escape } when PenMode && _penPath.Count > 0:
                ClearPen();
                AcceptEvent();
                break;
            case InputEventKey { Pressed: true } key when _stringFrom is not null && !_stringDialogOpen:
                if (key.Keycode == Key.Escape)
                {
                    EndString();
                    AcceptEvent();
                }
                else if (key.Keycode is Key.Enter or Key.KpEnter)
                {
                    FinishString();
                    AcceptEvent();
                }
                break;
        }
    }

    private void Pressed(InputEventMouseButton press)
    {
        GrabFocus();
        var position = press.Position;
        // The pen: a left drag draws around the elements; a right click removes the drawing.
        if (PenMode)
        {
            if (press.ButtonIndex == MouseButton.Right)
                ClearPen();
            else if (press.ButtonIndex == MouseButton.Left)
            {
                _penPath.Clear();
                _penPath.Add(position);
                _penDown = true;
                _penClosed = false;
                QueueRedraw();
            }
            return;
        }
        // While a red string is drawn, the clicks are for it: a right click cancels it.
        if (_stringFrom is not null)
        {
            if (_stringDialogOpen)
                return;
            if (press.ButtonIndex == MouseButton.Right)
                EndString();
            else if (press.ButtonIndex == MouseButton.Left)
                StringClicked(position, press.DoubleClick);
            return;
        }
        // A right click on an entity (or an image analysis) offers to annotate it.
        if (press.ButtonIndex == MouseButton.Right && NodeAt(position) is { } clicked && Data.Annotations.OfGraphNode(clicked) is { } target)
        {
            Data.Annotations.ShowMenu(this, position, target);
            return;
        }
        if (press.ButtonIndex != MouseButton.Left)
            return;
        // A click on the link icon of an element starts a red string from it.
        if (LinkIconAt(position) is { } linkFrom)
        {
            StartString(linkFrom);
            _stringPointer = position;
            return;
        }
        // A double-click on the name of a cluster opens its popup.
        if (press.DoubleClick && _clusterLabels.LastOrDefault(l => l.Label.HasPoint(position)).Cluster is { } cluster)
        {
            ReleaseDrag();
            ClusterDoubleClicked?.Invoke(cluster);
            return;
        }
        // A double-click on an element opens its object; on a link (not on an element), its relation.
        if (press.DoubleClick && NodeAt(position) is { } doubleClicked)
        {
            ReleaseDrag();
            NodeDoubleClicked?.Invoke(doubleClicked);
            return;
        }
        if (press.DoubleClick && EdgeAt(position) is { } edge)
        {
            ReleaseDrag();
            EdgeDoubleClicked?.Invoke(edge);
            return;
        }
        _dragStart = position;
        _moved = false;
        _dragged = NodeAt(position);
        if (_dragged is not null)
            _dragged.Pinned = true;
        else
        {
            _panning = true;
            _panStart = _offset;
        }
    }

    private void ReleaseDrag()
    {
        if (_dragged is not null)
            _dragged.Pinned = false;
        _dragged = null;
        _panning = false;
    }

    private void Moved(Vector2 position)
    {
        if (PenMode)
        {
            MouseDefaultCursorShape = CursorShape.Cross;
            if (_penDown && (_penPath.Count == 0 || position.DistanceTo(_penPath[^1]) > 2))
            {
                _penPath.Add(position);
                QueueRedraw();
            }
            return;
        }
        var hoveredLink = LinkIconAt(position);
        if (!ReferenceEquals(hoveredLink, _hoveredLink))
        {
            _hoveredLink = hoveredLink;
            QueueRedraw();
        }
        if (_stringFrom is not null)
        {
            if (!_stringDialogOpen)
            {
                _stringPointer = position;
                QueueRedraw();
            }
            MouseDefaultCursorShape = hoveredLink is not null ? CursorShape.PointingHand : CursorShape.Cross;
            return;
        }
        if (_dragged is { } node)
        {
            // A drag starts beyond 3 px: the small shake of a click moves nothing.
            _moved |= position.DistanceTo(_dragStart) > 3;
            if (!_moved)
                return;
            var world = ToWorld(position);
            node.X = world.X;
            node.Y = world.Y;
            node.VelocityX = node.VelocityY = 0;
            Animate();
            QueueRedraw();
            return;
        }
        if (_panning)
        {
            _offset = _panStart + (position - _dragStart);
            _moved |= position.DistanceTo(_dragStart) > 3;
            QueueRedraw();
            return;
        }
        var hovered = NodeAt(position);
        if (!ReferenceEquals(hovered, _hovered))
        {
            _hovered = hovered;
            QueueRedraw();
        }
        var link = hovered is null ? EdgeAt(position) : null;
        MouseDefaultCursorShape = hovered is not null || hoveredLink is not null || link is not null ? CursorShape.PointingHand : CursorShape.Arrow;
        // The full title of the link under the pointer (the graph writes only its nature, when there is room).
        TooltipText = link?.Label ?? string.Empty;
    }

    private void Released(InputEventMouseButton release)
    {
        // The pen lifted: the drawing closes; the elements inside it make a cluster (none: the drawing goes).
        if (PenMode && _penDown && release.ButtonIndex == MouseButton.Left)
        {
            _penDown = false;
            _penClosed = _penPath.Count >= 3;
            var inside = NodesInsidePen();
            QueueRedraw();
            if (inside.Count == 0)
            {
                ClearPen();
                return;
            }
            // The drawing kept: simplified (3 px), in the coordinates of the graph.
            var path = Simplify(_penPath, 3).Select(ToWorld).ToList();
            ClusterDrawn?.Invoke(inside, path);
            return;
        }
        if (_stringFrom is not null || release.ButtonIndex != MouseButton.Left)
            return;
        if (_vm is not null && !_moved && (_dragged is not null || _panning))
            _vm.SelectedNode = _dragged;
        if (_dragged is not null)
        {
            _dragged.Pinned = false;
            if (_moved)
                Animate(40);
        }
        _dragged = null;
        _panning = false;
        QueueRedraw();
    }

    // ---- Capture ----

    private const float SnapshotScale = 2;

    /// <summary>
    /// The graph as it is shown (its view, zoom and filters), drawn again twice the size of the control on screen in a
    /// viewport of its own; null when it cannot be drawn.
    /// </summary>
    public async Task<Image?> CaptureAsync()
    {
        if (_vm is null || Size.X < 1 || Size.Y < 1)
            return null;
        var viewport = new SubViewport
        {
            Size = (Vector2I)(Size * SnapshotScale).Ceil(),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            TransparentBg = false,
            Disable3D = true
        };
        var copy = new RelationGraphCanvas(_vm, snapshot: true)
        {
            // Drawn again at twice the zoom (sharp texts), not scaled.
            _zoom = _zoom * SnapshotScale,
            _offset = _offset * SnapshotScale,
            _stringFrom = null
        };
        copy.Size = Size * SnapshotScale;
        viewport.AddChild(copy);
        AddChild(viewport);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = viewport.GetTexture().GetImage();
        viewport.QueueFree();
        return image;
    }
}
