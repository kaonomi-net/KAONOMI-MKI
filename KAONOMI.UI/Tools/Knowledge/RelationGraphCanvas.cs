using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The graph of the relations, drawn flat in 2D and laid out by forces: the elements push each other away, the links
/// pull their two ends together, a light pull keeps everything around the centre. Each element is a card (its type in
/// its colour, its titles, its id, ×n when ends were merged); each link a curve from the source to the destination with
/// an arrow and the title of the relation, red to green with its probability (transparent when it does not meet the
/// filters); several links between the same two elements bend apart, a link to itself is a loop. The elements can be
/// dragged (the others follow), the wheel zooms, dragging the background moves the graph; a click selects an element
/// (its 1st and 2nd degree neighbours lit); a double-click on an element opens it, on a link (its line or its title)
/// its relation; a right click on an element offers to annotate it.
/// <para>Each card has a link icon at its top right: a click starts a red string from it, a click on the link icon of
/// another element ends it and the relation popup opens with the two (<see cref="RelationRequested"/>); a double-click
/// joins an element and goes on, Enter ends with the elements joined, Escape or a right click cancels. The string shows
/// until the popup is closed (<see cref="EndString"/>).</para>
/// <para>The pen (<see cref="PenMode"/>): a left drag draws around some elements, the elements inside make a cluster
/// (<see cref="ClusterDrawn"/>); "Show clusters" draws the stored clusters around their elements.</para>
/// </summary>
public sealed class RelationGraphCanvas : Control
{
    private const double NodeWidth = 190;
    private const double NodeHeight = 74;
    private const double FadedOpacity = 0.12;

    private static readonly Color[] Palette =
    [
        Paint.Hex("#4F8EF7"), Paint.Hex("#E8A23A"), Paint.Hex("#3FBF8F"), Paint.Hex("#E0609A"), Paint.Hex("#9B7CF0"), Paint.Hex("#3BB6CF"), Paint.Hex("#E35A4F"), Paint.Hex("#8BC34A")
    ];

    /// <summary>The colours of the entity types of the ontology (the other types: a colour of <see cref="Palette"/>).</summary>
    private static readonly Dictionary<string, Color> EntityColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PERSON"] = Paint.Hex("#F59E0B"), ["PER"] = Paint.Hex("#F59E0B"),
        ["ORG"] = Paint.Hex("#3B82F6"), ["ORGANISATION"] = Paint.Hex("#3B82F6"), ["ORGANIZATION"] = Paint.Hex("#3B82F6"),
        ["GPE"] = Paint.Hex("#10B981"), ["LOC"] = Paint.Hex("#22C55E"), ["LOCATION"] = Paint.Hex("#22C55E"), ["FAC"] = Paint.Hex("#14B8A6"),
        ["NORP"] = Paint.Hex("#A855F7"),
        ["PRODUCT"] = Paint.Hex("#EC4899"), ["HARDWARE"] = Paint.Hex("#F97316"), ["SOFTWARE"] = Paint.Hex("#06B6D4"), ["AI"] = Paint.Hex("#8B5CF6"),
        ["EVENT"] = Paint.Hex("#EF4444"), ["WORK_OF_ART"] = Paint.Hex("#D946EF"), ["LAW"] = Paint.Hex("#64748B"), ["LANGUAGE"] = Paint.Hex("#0EA5E9"),
        ["DATE"] = Paint.Hex("#EAB308"), ["TIME"] = Paint.Hex("#FACC15"),
        ["PERCENT"] = Paint.Hex("#84CC16"), ["MONEY"] = Paint.Hex("#16A34A"), ["QUANTITY"] = Paint.Hex("#65A30D"), ["ORDINAL"] = Paint.Hex("#A3A3A3"), ["CARDINAL"] = Paint.Hex("#9CA3AF"),
        ["FOOD"] = Paint.Hex("#FB7185"), ["MATHEMATIC"] = Paint.Hex("#6366F1"), ["MATERIAL"] = Paint.Hex("#B45309"),
        ["MISC"] = Paint.Hex("#94A3B8"), ["OTHERS"] = Paint.Hex("#94A3B8"), ["CLASS"] = Paint.Hex("#475569")
    };

    private static readonly Color StringColor = Paint.Hex("#E11D48");
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);

    private readonly RelationGraphViewModel _vm;

    /// <summary>True while the layout of a new graph runs on a worker thread (nothing is drawn but the background).</summary>
    private bool _layingOut;
    private CancellationTokenSource? _layoutCancel;

    // View: world → screen = world * zoom + offset.
    private double _zoom = 1;
    private Vector _offset;

    private RelationGraphNode? _dragged;
    private Point _dragStart;
    private Vector _panStart;
    private bool _panning;
    private bool _moved;
    private RelationGraphNode? _hovered;
    private RelationGraphNode? _hoveredLink;
    private Size _lastSize;
    private bool _capturing;

    /// <summary>
    /// The bend of each link (by link and rank among the links of the same pair), in world units: how far the middle of its
    /// curve stands from the straight line, chosen to go around the other cards. Computed when the layout settles.
    /// </summary>
    private Dictionary<(RelationGraphEdge, int), double> _bends = [];

    /// <summary>The farthest the middle of a link may stand from the straight line, in world units.</summary>
    private const double MaxBend = 180;

    /// <summary>The empty space kept between two cards, in world units.</summary>
    private const double CardGap = 70;

    /// <summary>The labels of links written this frame: a label that would cover one of them is left out.</summary>
    private readonly List<Rect> _labelBoxes = [];

    /// <summary>Where each link was drawn (points along it, and its label), to find the one under the pointer.</summary>
    private readonly List<(RelationGraphEdge Edge, List<Point> Points, Rect Label)> _edgeShapes = [];

    // ---- The pen: a drawing around some elements, to make a cluster of them ----
    private readonly List<Point> _penPath = [];
    private bool _penDown;
    private bool _penClosed;

    /// <summary>Where the name of each cluster was drawn, to find the one under a double-click.</summary>
    private readonly List<(ClusterEntry Cluster, Rect Label)> _clusterLabels = [];

    // ---- The red string between the link icons of the elements ----
    private RelationGraphNode? _stringFrom;
    private readonly List<RelationGraphNode> _stringJoined = [];
    private Point? _stringPointer;
    private bool _stringDialogOpen;
    private int _stringClick;

    public RelationGraphCanvas(RelationGraphViewModel vm)
    {
        _vm = vm;
        Focusable = true;
        ClipToBounds = true;
        Action graphChanged = () => Ui.OnMain(OnGraphChanged);
        Action titlesChanged = () => Ui.OnMain(InvalidateVisual);
        Action clustersChanged = () => Ui.OnMain(InvalidateVisual);
        Action<RelationGraphNode> nodeAdded = node => Ui.OnMain(() => OnNodeAdded(node));
        System.ComponentModel.PropertyChangedEventHandler selected = (_, e) =>
        {
            if (e.PropertyName is nameof(vm.SelectedNode) or nameof(vm.ShowClusters))
                Ui.OnMain(InvalidateVisual);
        };
        vm.GraphChanged += graphChanged;
        vm.TitlesChanged += titlesChanged;
        vm.ClustersChanged += clustersChanged;
        vm.NodeAdded += nodeAdded;
        vm.PropertyChanged += selected;
        KnowledgeUi.OnGone(this, () =>
        {
            vm.GraphChanged -= graphChanged;
            vm.TitlesChanged -= titlesChanged;
            vm.ClustersChanged -= clustersChanged;
            vm.NodeAdded -= nodeAdded;
            vm.PropertyChanged -= selected;
            _layoutCancel?.Cancel();
        });
        SizeChanged += (_, e) =>
        {
            if (_lastSize.Width <= 0 && e.NewSize.Width > 0)
                Fit();
            _lastSize = e.NewSize;
        };
        OnGraphChanged();
    }

    /// <summary>Raised by a double-click on a link: its relation is to be opened.</summary>
    public event Action<RelationGraphEdge>? EdgeDoubleClicked;

    /// <summary>Raised by a double-click on an element.</summary>
    public event Action<RelationGraphNode>? NodeDoubleClicked;

    /// <summary>Raised when a red string ends: a relation from the first element to the others joined. The string stays until <see cref="EndString"/>.</summary>
    public event Action<RelationGraphNode, IReadOnlyList<RelationGraphNode>>? RelationRequested;

    /// <summary>Raised when a drawing of the pen closes around at least one element, with the drawing simplified, in the coordinates of the graph.</summary>
    public event Action<IReadOnlyList<RelationGraphNode>, IReadOnlyList<Point>>? ClusterDrawn;

    /// <summary>Raised by a double-click on the name of a cluster shown by "Show clusters": its popup opens.</summary>
    public event Action<ClusterEntry>? ClusterDoubleClicked;

    /// <summary>True while the pen is chosen in the toolbar: a left drag draws around the elements instead of moving them.</summary>
    public bool PenMode { get; set; }

    /// <summary>True while a red string is drawn (or shown with its relation popup).</summary>
    public bool IsStringActive => _stringFrom is not null;

    private Size ViewSize => Bounds.Size;

    // ---- Colours ----

    /// <summary>A colour per type of element, the same one for a type in every search.</summary>
    internal static Color ColorOf(string type)
    {
        var hash = type.ToLowerInvariant().Aggregate(17, (h, c) => unchecked(h * 31 + c));
        return Palette[Math.Abs(hash % Palette.Length)];
    }

    /// <summary>
    /// The colour of an element: for an entity of the ontology the colour of its entity type (grey without one), for
    /// another element the colour of its window.
    /// </summary>
    internal static Color ColorOf(RelationGraphNode node)
    {
        if (node.Type != RelationGraphViewModel.EntityType)
            return ColorOf(node.Type);
        if (string.IsNullOrWhiteSpace(node.EntityType))
            return Paint.Hex("#8A939E");
        return EntityColors.TryGetValue(node.EntityType.Trim(), out var color) ? color : ColorOf("entity:" + node.EntityType.Trim().ToUpperInvariant());
    }

    /// <summary>Red for an impossible relation, grey to evaluate, green for a certain one.</summary>
    internal static Color ColorOf(int probability)
    {
        var t = Math.Clamp(probability / 100.0, -1, 1);
        var grey = Paint.Hex("#8A939E");
        return t < 0 ? Paint.Lerp(grey, Paint.Hex("#E5484D"), -t) : Paint.Lerp(grey, Paint.Hex("#30C48D"), t);
    }

    // ---- Layout ----

    /// <summary>An element added by hand: placed in the middle of the view, the forces make room for it.</summary>
    private void OnNodeAdded(RelationGraphNode node)
    {
        var center = ToWorld(new Point(ViewSize.Width / 2, ViewSize.Height / 2));
        var count = _vm.Nodes.Count;
        node.X = center.X + 30 * Math.Cos(count);
        node.Y = center.Y + 30 * Math.Sin(count);
        node.VelocityX = node.VelocityY = 0;
        Settle(50);
    }

    /// <summary>A new graph: its elements placed on a circle, settled by the forces (on a worker thread), fitted to the view.</summary>
    private void OnGraphChanged()
    {
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
        _layoutCancel?.Cancel();
        _layoutCancel = null;
        var total = nodes.Count <= 1 ? 0 : nodes.Count > 500 ? 250 : 400;
        _vm.LayoutProgress = 0;
        _vm.IsLayingOut = total > 0;
        _layingOut = total > 0;
        if (total == 0)
            FinishLayout(ComputeBends(nodes, _vm.Edges));
        else
        {
            // The forces, the overlaps and the routing run on a worker thread: the window stays alive, the bar moves.
            var cancel = _layoutCancel = new CancellationTokenSource();
            var edges = _vm.Edges;
            Task.Run(() => RunLayout(nodes, edges, total, cancel.Token)).ContinueWith(task => Dispatcher.UIThread.Post(() =>
            {
                if (cancel.IsCancellationRequested || !ReferenceEquals(cancel, _layoutCancel))
                    return;
                _layoutCancel = null;
                if (task.IsCompletedSuccessfully && task.Result is { } bends)
                    FinishLayout(bends);
                else
                {
                    if (task.Exception is { } error)
                        ActivityLog.Error("Relation", "Relation graph layout", error.InnerException ?? error);
                    FinishLayout([]);
                }
            }));
        }
        if (_stringFrom is not null && !_stringDialogOpen)
            EndString();
        InvalidateVisual();
    }

    /// <summary>The layout of a new graph, on a worker thread: the steps of the forces, the overlaps, then the routing of the links.</summary>
    private Dictionary<(RelationGraphEdge, int), double>? RunLayout(IReadOnlyList<RelationGraphNode> nodes, IReadOnlyList<RelationGraphEdge> edges,
        int total, CancellationToken cancel)
    {
        for (var step = 0; step < total; step++)
        {
            if (cancel.IsCancellationRequested)
                return null;
            StepCore(nodes, edges, 1, total - step);
            if (step % 8 == 0)
            {
                var progress = 95.0 * (step + 1) / total;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cancel.IsCancellationRequested)
                        _vm.LayoutProgress = progress;
                });
            }
        }
        ResolveOverlaps(nodes, 60);
        return cancel.IsCancellationRequested ? null : ComputeBends(nodes, edges);
    }

    /// <summary>The layout done: the routing of the links kept, the whole graph fitted to the view.</summary>
    private void FinishLayout(Dictionary<(RelationGraphEdge, int), double> bends)
    {
        _bends = bends;
        _layingOut = false;
        Fit();
        _vm.LayoutProgress = 100;
        _vm.IsLayingOut = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Some steps of the forces at once (after a drag, an element added): the graph settles without being animated;
    /// a big graph does fewer of them.
    /// </summary>
    private void Settle(int ticks)
    {
        var count = Math.Max(1, _vm.Nodes.Count);
        var steps = (int)Math.Min(ticks * 3, Math.Max(6, 3_000_000.0 / ((double)count * count)));
        StepCore(_vm.Nodes, _vm.Edges, steps, 0);
        _bends = ComputeBends(_vm.Nodes, _vm.Edges);
        InvalidateVisual();
    }

    /// <summary>
    /// Zooms by <paramref name="scale"/> (above 1: closer) around <paramref name="at"/> (a point of the view, its centre
    /// by default), which stays where it is.
    /// </summary>
    public void ZoomBy(double scale, Point? at = null)
    {
        var point = at ?? new Point(ViewSize.Width / 2, ViewSize.Height / 2);
        var world = ToWorld(point);
        _zoom = Math.Clamp(_zoom * scale, 0.03, 3);
        _offset = new Vector(point.X - world.X * _zoom, point.Y - world.Y * _zoom);
        InvalidateVisual();
    }

    /// <summary>Zooms and moves the view so that the whole graph shows.</summary>
    public void Fit()
    {
        var size = ViewSize;
        if (_vm.Nodes.Count == 0 || size.Width <= 0)
        {
            _zoom = 1;
            _offset = new Vector(size.Width / 2, size.Height / 2);
            InvalidateVisual();
            return;
        }
        var minX = _vm.Nodes.Min(n => n.X) - NodeWidth / 2 - 40;
        var maxX = _vm.Nodes.Max(n => n.X) + NodeWidth / 2 + 40;
        var minY = _vm.Nodes.Min(n => n.Y) - NodeHeight / 2 - 40;
        var maxY = _vm.Nodes.Max(n => n.Y) + NodeHeight / 2 + 40;
        _zoom = Math.Clamp(Math.Min(size.Width / (maxX - minX), size.Height / (maxY - minY)), 0.03, 1.6);
        _offset = new Vector(size.Width / 2 - (minX + maxX) / 2 * _zoom, size.Height / 2 - (minY + maxY) / 2 * _zoom);
        InvalidateVisual();
    }

    /// <summary>
    /// <paramref name="steps"/> steps of the forces on <paramref name="nodes"/> (also run on the worker thread of a new
    /// graph): repulsion between the elements, springs along the links, a pull to the centre; <paramref name="phase"/>
    /// spaces the passes against the overlaps of a big graph.
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
    private static double DefaultBend(double length, int index) =>
        Math.Min(0.12 * length, MaxBend / 3) + (index == 0 ? 0 : (index % 2 == 1 ? 1 : -1) * ((index + 1) / 2) * 46);

    /// <summary>
    /// Chooses the bend of every link: among a few arcs on either side, the one whose curve crosses the fewest other cards,
    /// the gentlest on a tie; the links shared among the cores (also run on the worker thread).
    /// </summary>
    private static Dictionary<(RelationGraphEdge, int), double> ComputeBends(IReadOnlyList<RelationGraphNode> nodes, IReadOnlyList<RelationGraphEdge> edges)
    {
        var cards = nodes.Select(n => (Node: n, Box: new Rect(n.X - NodeWidth / 2 - 6, n.Y - NodeHeight / 2 - 6, NodeWidth + 12, NodeHeight + 12))).ToArray();
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
        var chosen = new double[ranked.Count];
        Parallel.For(0, ranked.Count, n => chosen[n] = BestBend(ranked[n].Edge, ranked[n].Index, cards));
        var bends = new Dictionary<(RelationGraphEdge, int), double>(ranked.Count);
        for (var n = 0; n < ranked.Count; n++)
            bends[(ranked[n].Edge, ranked[n].Index)] = chosen[n];
        return bends;
    }

    private static readonly double[] Arcs = [0.12, -0.12, 0.25, -0.25, 0.4, -0.4, 0.6, -0.6];

    /// <summary>The bend of one link: the first arc that crosses no other card, else the one that crosses the fewest.</summary>
    private static double BestBend(RelationGraphEdge edge, int index, (RelationGraphNode Node, Rect Box)[] cards)
    {
        var from = new Point(edge.From.X, edge.From.Y);
        var to = new Point(edge.To.X, edge.To.Y);
        var direction = to - from;
        var length = Math.Max(1, Paint.Length(direction));
        var normal = new Vector(-direction.Y / length, direction.X / length);
        var fan = DefaultBend(length, index) - Math.Min(0.12 * length, MaxBend / 3);
        var best = DefaultBend(length, index);
        var fewest = int.MaxValue;
        foreach (var arc in Arcs)
        {
            var bend = Math.Clamp(arc * length, -MaxBend, MaxBend) + fan;
            var control = Paint.Lerp(from, to, 0.5) + normal * bend * 2;
            // The curve stays in the box of its three points: the cards out of it are not tested.
            var area = new Rect(from, from).Union(new Rect(to, to)).Union(new Rect(control, control));
            var hits = 0;
            foreach (var (node, box) in cards)
            {
                if (ReferenceEquals(node, edge.From) || ReferenceEquals(node, edge.To) || !area.Intersects(box))
                    continue;
                for (var i = 1; i < 12; i++)
                    if (box.Contains(Bezier(from, control, to, i / 12.0)))
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

    // ---- Geometry ----

    private Point ToScreen(double x, double y) => new(x * _zoom + _offset.X, y * _zoom + _offset.Y);

    private Point ToWorld(Point p) => new((p.X - _offset.X) / _zoom, (p.Y - _offset.Y) / _zoom);

    private Rect CardOf(RelationGraphNode node)
    {
        var center = ToScreen(node.X, node.Y);
        return new Rect(center.X - NodeWidth * _zoom / 2, center.Y - NodeHeight * _zoom / 2, NodeWidth * _zoom, NodeHeight * _zoom);
    }

    private RelationGraphNode? NodeAt(Point p) => _vm.Nodes.LastOrDefault(n => CardOf(n).Contains(p));

    /// <summary>The link icon of a card, at its top right.</summary>
    private Rect LinkIconOf(RelationGraphNode node)
    {
        var card = CardOf(node);
        var size = 20 * _zoom;
        return new Rect(card.Right - size - 5 * _zoom, card.Y + 5 * _zoom, size, size);
    }

    private RelationGraphNode? LinkIconAt(Point p) =>
        _zoom < 0.3 ? null : _vm.Nodes.LastOrDefault(n => Paint.Grow(LinkIconOf(n), 4).Contains(p));

    private static Point Bezier(Point a, Point c, Point b, double t) =>
        new((1 - t) * (1 - t) * a.X + 2 * (1 - t) * t * c.X + t * t * b.X, (1 - t) * (1 - t) * a.Y + 2 * (1 - t) * t * c.Y + t * t * b.Y);

    /// <summary>Where the segment from <paramref name="from"/> to the centre of <paramref name="card"/> meets its edge.</summary>
    private static Point ClipToCard(Point from, Point center, Rect card)
    {
        var d = center - from;
        if (Math.Abs(d.X) < 0.001 && Math.Abs(d.Y) < 0.001)
            return center;
        var tx = Math.Abs(d.X) < 0.001 ? double.MaxValue : card.Width / 2 / Math.Abs(d.X);
        var ty = Math.Abs(d.Y) < 0.001 ? double.MaxValue : card.Height / 2 / Math.Abs(d.Y);
        var t = Math.Min(1, Math.Min(tx, ty));
        return center - d * t;
    }

    // ---- The pen ----

    /// <summary>Removes the drawing of the pen (its cluster was saved or its popup closed).</summary>
    public void ClearPen()
    {
        _penPath.Clear();
        _penDown = false;
        _penClosed = false;
        InvalidateVisual();
    }

    /// <summary>The elements whose card centre is inside the drawing (a ray cast along x).</summary>
    private List<RelationGraphNode> NodesInsidePen() =>
        _penPath.Count < 3 ? [] : _vm.Nodes.Where(n => ClusterPen.Inside(_penPath, CardOf(n).Center)).ToList();

    // ---- The red string ----

    /// <summary>Starts a red string from the link icon of <paramref name="node"/>.</summary>
    public void StartString(RelationGraphNode node)
    {
        _stringFrom = node;
        _stringJoined.Clear();
        _stringPointer = null;
        _stringDialogOpen = false;
        Focus();
        InvalidateVisual();
    }

    /// <summary>Removes the string (its relation popup was saved or closed, or it was cancelled).</summary>
    public void EndString()
    {
        _stringFrom = null;
        _stringJoined.Clear();
        _stringPointer = null;
        _stringDialogOpen = false;
        InvalidateVisual();
    }

    private void JoinString(RelationGraphNode node)
    {
        if (ReferenceEquals(node, _stringFrom) || _stringJoined.Contains(node))
            return;
        _stringJoined.Add(node);
        InvalidateVisual();
    }

    /// <summary>A click while the string is drawn: on a link icon it joins (and links), elsewhere it cancels an empty string.</summary>
    private void StringClicked(Point p, bool doubleClick)
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
        await Task.Delay(320);
        Ui.OnMain(() =>
        {
            if (number == _stringClick && _stringFrom is not null && !_stringDialogOpen)
                FinishString();
        });
    }

    private void FinishString()
    {
        if (_stringFrom is not { } from || _stringJoined.Count == 0 || _stringDialogOpen)
            return;
        _stringDialogOpen = true;
        _stringPointer = null;
        InvalidateVisual();
        RelationRequested?.Invoke(from, _stringJoined.ToList());
    }

    // ---- Drawing ----

    public override void Render(DrawingContext context)
    {
        var size = ViewSize;
        Paint.Rect(context, new Rect(size), Paint.Background);
        DrawGrid(context, size);
        if (_vm.Nodes.Count == 0)
        {
            var text = "No relation to show";
            var measured = Paint.Measure(text, 14);
            Paint.Write(context, text, new Point((size.Width - measured.Width) / 2, size.Height / 2 - measured.Height / 2), 14, Paint.Muted);
            return;
        }

        _edgeShapes.Clear();
        _labelBoxes.Clear();
        if (_layingOut)
            return;
        var pairs = new Dictionary<(string, string), int>();
        foreach (var edge in _vm.Edges)
        {
            var key = string.CompareOrdinal(edge.From.Key, edge.To.Key) < 0 ? (edge.From.Key, edge.To.Key) : (edge.To.Key, edge.From.Key);
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
            // An element selected: the links toward its 1st and 2nd degree stay, the others fade.
            var alpha = edge.MatchesFilters ? 1 : FadedOpacity;
            var level = _vm.HasProximity ? ProximityOf(_vm, edge) : null;
            if (_vm.HasProximity && level is null)
                alpha *= ProximityFade;
            DrawEdge(context, edge, pairs[key] - 1, alpha);
            if (level is { } l)
                DrawProximityEdge(context, edge, l);
        }
        DrawClusters(context);
        foreach (var node in _vm.Nodes)
            DrawNode(context, node, ReferenceEquals(node, _vm.SelectedNode), ReferenceEquals(node, _hovered));
        // The neighbours of the selected element lit (accent: 1st degree, green: 2nd degree), the others faded.
        if (_vm.HasProximity)
            foreach (var node in _vm.Nodes)
                DrawProximity(context, node, _vm.DistanceOf(node));
        if (!_capturing)
            DrawStringThread(context);
        ClusterPen.DrawPath(context, _penPath, _penClosed);
    }

    /// <summary>A faint grid behind the graph, moving and zooming with it.</summary>
    private void DrawGrid(DrawingContext context, Size size)
    {
        var step = 80 * _zoom;
        if (step < 12)
            return;
        var color = Paint.Alpha(Paint.Edge, 0.18);
        for (var x = Paint.PosMod(_offset.X, step); x < size.Width; x += step)
            Paint.Line(context, new Point(x, 0), new Point(x, size.Height), color);
        for (var y = Paint.PosMod(_offset.Y, step); y < size.Height; y += step)
            Paint.Line(context, new Point(0, y), new Point(size.Width, y), color);
    }

    /// <summary>
    /// The clusters stored on the server, when "Show clusters" is ticked: around their elements present in the graph,
    /// the drawing of the pen smoothed, or, when the elements have moved out of it, the hull of their cards; the name
    /// of the cluster at its top.
    /// </summary>
    private void DrawClusters(DrawingContext context)
    {
        _clusterLabels.Clear();
        if (!_vm.ShowClusters || _vm.Clusters.Count == 0)
            return;
        foreach (var cluster in _vm.Clusters)
        {
            var members = _vm.Nodes.Where(n => cluster.Members.Any(m => m.WindowName == n.Type && m.ObjectId == (n.ObjectId ?? n.Key))).ToList();
            if (members.Count == 0)
                continue;
            var drawn = cluster.Path.Select(p => new Point(p.X, p.Y)).ToList();
            var centres = members.Select(n => new Point(n.X, n.Y)).ToList();
            List<Point> shape;
            if (drawn.Count >= 3 && centres.All(c => ClusterPen.Inside(drawn, c)))
                shape = drawn;
            else
            {
                // The elements moved out of the drawing: the hull of their cards, with some room around.
                var corners = new List<Point>();
                foreach (var c in centres)
                    foreach (var (dx, dy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                        corners.Add(new Point(c.X + dx * (NodeWidth / 2 + 28), c.Y + dy * (NodeHeight / 2 + 28)));
                shape = ClusterPen.Hull(corners);
            }
            if (ClusterPen.DrawShape(context, shape, cluster.Name, p => ToScreen(p.X, p.Y), _zoom) is { } label)
                _clusterLabels.Add((cluster, label));
        }
    }

    private void DrawEdge(DrawingContext context, RelationGraphEdge edge, int index, double alpha)
    {
        if (ReferenceEquals(edge.From, edge.To))
        {
            DrawLoop(context, edge, index, alpha);
            return;
        }
        var from = ToScreen(edge.From.X, edge.From.Y);
        var to = ToScreen(edge.To.X, edge.To.Y);
        var color = Paint.Alpha(ColorOf(edge.Relation.Current.Probability), alpha);
        var width = Math.Max(1.2, 2 * _zoom);
        var direction = to - from;
        var length = Math.Max(1, Paint.Length(direction));
        var normal = new Vector(-direction.Y / length, direction.X / length);
        var bend = (_bends.TryGetValue((edge, index), out var routed) ? routed : DefaultBend(length / _zoom, index)) * _zoom;
        var control = Paint.Lerp(from, to, 0.5) + normal * bend * 2;
        var middle = Paint.Lerp(from, to, 0.5) + normal * bend;

        var end = ClipToCard(control, to, CardOf(edge.To));
        var points = Enumerable.Range(0, 21).Select(i => Bezier(from, control, end, i / 20.0)).ToList();
        Paint.Polyline(context, points, color, width);
        var unit = Paint.Normalized(end - control);
        var arrowSize = Math.Max(7, 11 * _zoom);
        var side = new Vector(-unit.Y, unit.X) * arrowSize * 0.5;
        Paint.Polygon(context, [end, end - unit * arrowSize + side, end - unit * arrowSize - side], color);
        _edgeShapes.Add((edge, points, default));

        if (_zoom < 0.35)
            return;
        var fontSize = Math.Max(9, 11 * _zoom);
        var labelSize = Paint.Measure(edge.ShortLabel, fontSize, 200 * _zoom, 2);
        var box = new Rect(middle.X - labelSize.Width / 2 - 5, middle.Y - labelSize.Height / 2 - 2, labelSize.Width + 10, labelSize.Height + 4);
        if (!PlaceLabel(box, alpha))
            return;
        DrawLabelBox(context, box, color, alpha);
        Paint.Write(context, edge.ShortLabel, box.TopLeft + new Vector(5, 2), fontSize, Paint.Alpha(Paint.TextBright, alpha), 200 * _zoom, 2);
        _edgeShapes[^1] = (edge, points, box);
    }

    /// <summary>
    /// Whether a label fits at <paramref name="box"/>: not over another label nor over a card (the link stays, its
    /// title shows on hover); a faded link writes no label at all.
    /// </summary>
    private bool PlaceLabel(Rect box, double alpha)
    {
        if (alpha < 0.5)
            return false;
        if (_labelBoxes.Any(b => b.Intersects(box)) || _vm.Nodes.Any(n => CardOf(n).Intersects(box)))
            return false;
        _labelBoxes.Add(box);
        return true;
    }

    private static void DrawLabelBox(DrawingContext context, Rect box, Color edge, double alpha) =>
        Paint.Round(context, box, 4, Paint.Alpha(Paint.PanelSolid, 0.95 * alpha), edge, 1);

    /// <summary>A relation from an element to itself: a loop above the right of its card, with its title.</summary>
    private void DrawLoop(DrawingContext context, RelationGraphEdge edge, int index, double alpha)
    {
        var card = CardOf(edge.From);
        var color = Paint.Alpha(ColorOf(edge.Relation.Current.Probability), alpha);
        var size = (34 + 16 * index) * _zoom;
        var start = new Point(card.Right - 40 * _zoom, card.Y);
        var end = new Point(card.Right, card.Y + 22 * _zoom);
        var c1 = new Point(start.X, start.Y - size * 1.6);
        var c2 = new Point(end.X + size * 1.6, end.Y);
        var points = Enumerable.Range(0, 21).Select(i =>
        {
            var t = i / 20.0;
            var u = 1 - t;
            return new Point(u * u * u * start.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * end.X,
                u * u * u * start.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * end.Y);
        }).ToList();
        Paint.Polyline(context, points, color, Math.Max(1.2, 2 * _zoom));
        _edgeShapes.Add((edge, points, default));
        if (_zoom < 0.35)
            return;
        var fontSize = Math.Max(9, 11 * _zoom);
        var labelSize = Paint.Measure(edge.ShortLabel, fontSize, 200 * _zoom, 1);
        var box = new Rect(card.Right + size * 0.4, card.Y - size * 1.1 - labelSize.Height / 2, labelSize.Width + 10, labelSize.Height + 4);
        if (!PlaceLabel(box, alpha))
            return;
        DrawLabelBox(context, box, color, alpha);
        Paint.Write(context, edge.ShortLabel, box.TopLeft + new Vector(5, 2), fontSize, Paint.Alpha(Paint.TextBright, alpha), 200 * _zoom, 1);
        _edgeShapes[^1] = (edge, points, box);
    }

    /// <summary>The opacity left to the elements and links beyond the second degree of the selected element.</summary>
    private const double ProximityFade = 0.25;

    private static Color FirstDegree => Paint.Cyan;
    private static Color SecondDegree => Paint.Green;

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
    private void DrawProximityEdge(DrawingContext context, RelationGraphEdge edge, int level)
    {
        if (ReferenceEquals(edge.From, edge.To))
            return;
        var color = Paint.Alpha(level == 1 ? FirstDegree : SecondDegree, level == 1 ? 0.75 : 0.55);
        // Drawn right after the link itself: over the same curve.
        IReadOnlyList<Point> points = _edgeShapes.Count > 0 && ReferenceEquals(_edgeShapes[^1].Edge, edge)
            ? _edgeShapes[^1].Points
            : [CardOf(edge.From).Center, CardOf(edge.To).Center];
        Paint.Polyline(context, points, color, Math.Max(1.5, (level == 1 ? 3.5 : 2.5) * _zoom));
    }

    /// <summary>An element seen from the selected one: a frame in the colour of its degree, or a veil beyond the 2nd degree.</summary>
    private void DrawProximity(DrawingContext context, RelationGraphNode node, int? distance)
    {
        var card = CardOf(node);
        switch (distance)
        {
            case 0:
                return;
            case 1:
            case 2:
                var color = distance == 1 ? FirstDegree : SecondDegree;
                Paint.Outline(context, Paint.Grow(card, 4), Paint.Alpha(color, 0.85), Math.Max(1.5, (distance == 1 ? 2.5 : 2) * _zoom));
                Paint.Outline(context, Paint.Grow(card, 8), Paint.Alpha(color, 0.25), Math.Max(1, 1.5 * _zoom));
                var badge = new Point(card.Right - 2, card.Y + 2);
                Paint.Circle(context, badge, Math.Max(5, 8 * _zoom), color);
                var size = Math.Max(7, 9 * _zoom);
                var text = distance == 1 ? "1" : "2";
                var measured = Paint.Measure(text, size, typeface: Paint.Bold);
                Paint.Write(context, text, new Point(badge.X - measured.Width / 2, badge.Y - measured.Height / 2), size, Colors.Black, typeface: Paint.Bold);
                return;
            default:
                Paint.Rect(context, Paint.Grow(card, 2), Paint.Alpha(Paint.Background, 1 - ProximityFade));
                return;
        }
    }

    /// <summary>What an element is, at the top of its card: the entity type of an entity of the ontology (PERSON, LOCATION…), else its window.</summary>
    private static string KindOf(RelationGraphNode node) =>
        (node.Type == RelationGraphViewModel.EntityType && !string.IsNullOrWhiteSpace(node.EntityType) ? node.EntityType.Trim() : node.Type).ToUpperInvariant();

    private void DrawNode(DrawingContext context, RelationGraphNode node, bool selected, bool hovered)
    {
        var card = CardOf(node);
        var color = ColorOf(node);
        var border = selected ? Paint.Amber : hovered ? color : Paint.Alpha(color, 0.7);
        Paint.Round(context, card, Math.Max(1, 4 * _zoom), Paint.PanelSolid, border, selected ? 2 : 1);
        Paint.Rect(context, new Rect(card.X + 1, card.Y + 1, Math.Max(2, 4 * _zoom), Math.Max(0, card.Height - 2)), color);
        if (selected)
            Paint.Outline(context, Paint.Grow(card, 3), Paint.Alpha(Paint.Amber, 0.35));
        if (_zoom < 0.3)
            return;
        var pad = 10 * _zoom;
        var width = card.Width - 2 * pad;
        var typeSize = Paint.Write(context, KindOf(node), new Point(card.X + pad, card.Y + 6 * _zoom), 9.5 * _zoom, color, Math.Max(1, width - 24 * _zoom), 1, Paint.SemiBold);
        Paint.Write(context, node.Title, new Point(card.X + pad, card.Y + 6 * _zoom + typeSize.Height + 2 * _zoom), 12 * _zoom, Paint.TextBright, width, 2, Paint.SemiBold);
        var id = node.ObjectId is { } objectId ? "id " + (objectId.Length > 24 ? objectId[..24] + "…" : objectId) : "no id";
        var idSize = Paint.Measure(id, 9 * _zoom, width, 1, Paint.Mono);
        Paint.Write(context, id, new Point(card.X + pad, card.Bottom - idSize.Height - 6 * _zoom), 9 * _zoom, Paint.Muted, width, 1, Paint.Mono);
        if (node.Titles.Count > 1)
        {
            // The number of ends merged into the element.
            var count = $"×{node.Titles.Count}";
            var size = Paint.Measure(count, 9 * _zoom, 0, 1, Paint.Bold);
            var badge = new Rect(LinkIconOf(node).X - size.Width - 14 * _zoom, card.Y + 7 * _zoom, size.Width + 8 * _zoom, size.Height + 2 * _zoom);
            Paint.Rect(context, badge, color);
            Paint.Write(context, count, badge.TopLeft + new Vector(4 * _zoom, 1 * _zoom), 9 * _zoom, Colors.White, 0, 1, Paint.Bold);
        }
        DrawLinkIcon(context, node);
    }

    /// <summary>The link icon of a card: two links of a chain (lit while its element is an end of the string).</summary>
    private void DrawLinkIcon(DrawingContext context, RelationGraphNode node)
    {
        var icon = LinkIconOf(node);
        var lit = !_capturing && (ReferenceEquals(node, _stringFrom) || _stringJoined.Contains(node));
        if (lit || ReferenceEquals(node, _hoveredLink))
            Paint.Rect(context, Paint.Grow(icon, 2), lit ? Paint.Alpha(StringColor, 0.25) : Paint.Alpha(Paint.Text, 0.12));
        var color = lit ? StringColor : Paint.Alpha(Paint.Text, 0.85);
        var center = icon.Center;
        var axis = Paint.Normalized(new Vector(1, -1));
        var unit = icon.Width / 24;
        foreach (var shift in new[] { -3.2, 3.2 })
            Paint.Polyline(context, Stadium(center + axis * shift * unit, axis, 5.5 * unit, 2.6 * unit), color, Math.Max(1, 1.8 * unit));
    }

    /// <summary>The outline of a rounded link of chain: <paramref name="half"/> long on each side of its centre along <paramref name="axis"/>.</summary>
    private static List<Point> Stadium(Point center, Vector axis, double half, double radius)
    {
        var normal = new Vector(-axis.Y, axis.X);
        var points = new List<Point>();
        var straight = half - radius;
        for (var i = 0; i <= 10; i++)
        {
            var a = -Math.PI / 2 + Math.PI * i / 10;
            points.Add(center + axis * (straight + radius * Math.Cos(a)) + normal * (radius * Math.Sin(a)));
        }
        for (var i = 0; i <= 10; i++)
        {
            var a = Math.PI / 2 + Math.PI * i / 10;
            points.Add(center + axis * (-straight + radius * Math.Cos(a)) + normal * (radius * Math.Sin(a)));
        }
        points.Add(points[0]);
        return points;
    }

    /// <summary>The thread from one point to another, hanging a little between them.</summary>
    private static void DrawThread(DrawingContext context, Point start, Point end)
    {
        var length = Paint.Distance(start, end);
        var middle = new Point((start.X + end.X) / 2, Math.Max(start.Y, end.Y) + Math.Min(120, length * 0.18));
        var points = Enumerable.Range(0, 25).Select(i => Bezier(start, middle, end, i / 24.0)).ToList();
        Paint.Polyline(context, points, StringColor, 3);
    }

    /// <summary>The string: from the first link icon through the joined ones to the pointer, ringing the icon under it.</summary>
    private void DrawStringThread(DrawingContext context)
    {
        if (_stringFrom is not { } from)
            return;
        var points = new List<Point> { LinkIconOf(from).Center };
        points.AddRange(_stringJoined.Select(n => LinkIconOf(n).Center));
        if (_stringPointer is { } pointer)
            points.Add(pointer);
        for (var i = 1; i < points.Count; i++)
            DrawThread(context, points[i - 1], points[i]);
        Paint.Circle(context, points[0], 4, StringColor);
        foreach (var node in _stringJoined)
        {
            var icon = LinkIconOf(node);
            Paint.Circle(context, icon.Center, 4, StringColor);
            context.DrawEllipse(null, Paint.Pen(StringColor, 2, 4), icon.Center, icon.Width / 2 + 7, icon.Width / 2 + 7);
        }
        if (_stringPointer is { } at && LinkIconAt(at) is { } target && !ReferenceEquals(target, from))
        {
            var icon = LinkIconOf(target);
            Paint.Ring(context, icon.Center, icon.Width / 2 + 7, StringColor, 2.5);
        }
    }

    // ---- Hit tests ----

    /// <summary>The link under <paramref name="p"/>: on its title, or within a few pixels of its line.</summary>
    private RelationGraphEdge? EdgeAt(Point p)
    {
        for (var n = _edgeShapes.Count - 1; n >= 0; n--)
            if (_edgeShapes[n].Label.Width > 0 && _edgeShapes[n].Label.Contains(p))
                return _edgeShapes[n].Edge;
        for (var n = _edgeShapes.Count - 1; n >= 0; n--)
        {
            var points = _edgeShapes[n].Points;
            for (var i = 1; i < points.Count; i++)
                if (Paint.DistanceToSegment(p, points[i - 1], points[i]) < 7)
                    return _edgeShapes[n].Edge;
        }
        return null;
    }

    // ---- Input ----

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_layingOut)
            return;
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        ZoomBy(Math.Pow(1.12, delta), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;
        if (e.Key == Key.Escape && PenMode && _penPath.Count > 0)
        {
            ClearPen();
            e.Handled = true;
        }
        else if (_stringFrom is not null && !_stringDialogOpen)
        {
            if (e.Key == Key.Escape)
            {
                EndString();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                FinishString();
                e.Handled = true;
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_layingOut)
            return;
        Focus();
        e.Handled = true;
        var position = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        var doubleClick = e.ClickCount >= 2;
        // The pen: a left drag draws around the elements; a right click removes the drawing.
        if (PenMode)
        {
            if (properties.IsRightButtonPressed)
                ClearPen();
            else if (properties.IsLeftButtonPressed)
            {
                _penPath.Clear();
                _penPath.Add(position);
                _penDown = true;
                _penClosed = false;
                e.Pointer.Capture(this);
                InvalidateVisual();
            }
            return;
        }
        // While a red string is drawn, the clicks are for it: a right click cancels it.
        if (_stringFrom is not null)
        {
            if (_stringDialogOpen)
                return;
            if (properties.IsRightButtonPressed)
                EndString();
            else if (properties.IsLeftButtonPressed)
                StringClicked(position, doubleClick);
            return;
        }
        // A right click on an entity (or an image analysis) offers to annotate it.
        if (properties.IsRightButtonPressed && NodeAt(position) is { } clicked && RelationGraphViewModel.AnnotationTargetOf(clicked) is { } target)
        {
            KnowledgeUi.AnnotateMenu(this, target);
            return;
        }
        if (!properties.IsLeftButtonPressed)
            return;
        // A click on the link icon of an element starts a red string from it.
        if (LinkIconAt(position) is { } linkFrom)
        {
            StartString(linkFrom);
            _stringPointer = position;
            return;
        }
        // A double-click on the name of a cluster opens its popup.
        if (doubleClick && _clusterLabels.LastOrDefault(l => l.Label.Contains(position)).Cluster is { } cluster)
        {
            ReleaseDrag();
            ClusterDoubleClicked?.Invoke(cluster);
            return;
        }
        // A double-click on an element opens its object; on a link (not on an element), its relation.
        if (doubleClick && NodeAt(position) is { } doubleClicked)
        {
            ReleaseDrag();
            NodeDoubleClicked?.Invoke(doubleClicked);
            return;
        }
        if (doubleClick && EdgeAt(position) is { } edge)
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
        e.Pointer.Capture(this);
    }

    private void ReleaseDrag()
    {
        if (_dragged is not null)
            _dragged.Pinned = false;
        _dragged = null;
        _panning = false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_layingOut)
            return;
        var position = e.GetPosition(this);
        if (PenMode)
        {
            Cursor = CrossCursor;
            if (_penDown && (_penPath.Count == 0 || Paint.Distance(position, _penPath[^1]) > 2))
            {
                _penPath.Add(position);
                InvalidateVisual();
            }
            return;
        }
        var hoveredLink = LinkIconAt(position);
        if (!ReferenceEquals(hoveredLink, _hoveredLink))
        {
            _hoveredLink = hoveredLink;
            InvalidateVisual();
        }
        if (_stringFrom is not null)
        {
            if (!_stringDialogOpen)
            {
                _stringPointer = position;
                InvalidateVisual();
            }
            Cursor = hoveredLink is not null ? HandCursor : CrossCursor;
            return;
        }
        if (_dragged is { } node)
        {
            // A drag starts beyond 3 px: the small shake of a click moves nothing.
            _moved |= Paint.Distance(position, _dragStart) > 3;
            if (!_moved)
                return;
            var world = ToWorld(position);
            node.X = world.X;
            node.Y = world.Y;
            node.VelocityX = node.VelocityY = 0;
            // The others follow the card dragged.
            var count = Math.Max(1, _vm.Nodes.Count);
            StepCore(_vm.Nodes, _vm.Edges, count * count > 1_000_000 ? 1 : 3, 0);
            InvalidateVisual();
            return;
        }
        if (_panning)
        {
            _offset = _panStart + (position - _dragStart);
            _moved |= Paint.Distance(position, _dragStart) > 3;
            InvalidateVisual();
            return;
        }
        var hovered = NodeAt(position);
        if (!ReferenceEquals(hovered, _hovered))
        {
            _hovered = hovered;
            InvalidateVisual();
        }
        var link = hovered is null ? EdgeAt(position) : null;
        Cursor = hovered is not null || hoveredLink is not null || link is not null ? HandCursor : ArrowCursor;
        // The full title of the link under the pointer (the graph writes only its nature, when there is room).
        var tip = link?.Label;
        if (!Equals(ToolTip.GetTip(this), tip))
            ToolTip.SetTip(this, tip);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (_layingOut)
            return;
        var left = e.InitialPressMouseButton == MouseButton.Left;
        // The pen lifted: the drawing closes; the elements inside it make a cluster (none: the drawing goes).
        if (PenMode && _penDown && left)
        {
            _penDown = false;
            _penClosed = _penPath.Count >= 3;
            var inside = NodesInsidePen();
            InvalidateVisual();
            if (inside.Count == 0)
            {
                ClearPen();
                return;
            }
            // The drawing kept: simplified (3 px), in the coordinates of the graph.
            var path = ClusterPen.Simplify(_penPath, 3).Select(ToWorld).ToList();
            ClusterDrawn?.Invoke(inside, path);
            return;
        }
        if (_stringFrom is not null || !left)
            return;
        if (!_moved && (_dragged is not null || _panning))
            _vm.SelectedNode = _dragged;
        if (_dragged is not null)
        {
            _dragged.Pinned = false;
            if (_moved)
                Settle(40);
        }
        _dragged = null;
        _panning = false;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hovered is not null || _hoveredLink is not null)
        {
            _hovered = null;
            _hoveredLink = null;
            InvalidateVisual();
        }
    }

    // ---- Capture ----

    /// <summary>Saves the graph as it is shown (its view, zoom and filters, without the red string), twice its size on screen, to <paramref name="path"/>.</summary>
    public bool SaveCapture(string path)
    {
        _capturing = true;
        try
        {
            return KnowledgeUi.SavePng(this, path, 2);
        }
        finally
        {
            _capturing = false;
            InvalidateVisual();
        }
    }
}
