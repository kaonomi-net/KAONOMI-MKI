using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;
using KAONOMI.UI.Themes;
using SkiaSharp;
using Point = Avalonia.Point;
using Color = Avalonia.Media.Color;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The relation graph in 3D (the 3D button of /relation), as in the Godot chat (<c>RelationGraph3D.cs</c> there): a
/// shape per element (a body for a person, a building for an organisation… a sphere for an element that is not an
/// entity, see <see cref="EntityShapes"/>), in the colour of its entity type (else of its window), its name above it with
/// its entity type (or its window) under the name; a line per link from the source to the destination, coloured by the
/// probability of the relation (red to green, grey to evaluate), with an arrowhead and the title of the relation at its
/// middle; the links that do not meet the filters are transparent. The depth is the time: the oldest elements (the date
/// of their first relation) at the back, the most recent in front, a timeline under the graph with its dates, a thin line
/// from each element down to its date. An entity with a start date or an end date has a 3D arrow along the time under
/// it, from its start (a disc there) to its end (or to today), coming out of a fade when it has no start. The height is
/// the probability (the mean of its relations, from -100 to +100): the higher, the lower; a scale beside the timeline
/// gives it. Across, the elements are laid out by forces from their places in the 2D graph. Nothing moves until the user
/// does: drag to turn around the graph, right drag (or Shift + drag) to move it, the wheel to come closer, Z Q S D (W A
/// S D) or the arrows to move, F for the FPS mode (the mouse aims, the keys move); a click selects an element, a
/// double-click opens it, a double-click on a link title opens its relation, a right click annotates an entity. With
/// "Show clusters", each cluster is a translucent coloured blob around its elements with its name above it (a double-click
/// on it opens the cluster). The shapes and lines are drawn with OpenGL (<see cref="Graph3DRenderer"/>), the names over
/// them by Avalonia, facing the camera, sized by their distance.
/// </summary>
public sealed class RelationGraph3D : Panel
{
    private const float Unit = 0.06f;       // world units per pixel of the 2D layout
    private const float CubeSize = 3.2f;
    private const float HeightUnit = 0.25f; // world units per point of probability at the scale 1
    private const float FieldOfView = 50;   // degrees, vertical
    private const float BlobRadius = CubeSize * 1.9f;
    private const float BlobThreshold = 0.5f;
    private const float MouseSensitivity = 0.002f;
    private const double LabelFontSize = 32;  // the size the names are laid out at, then scaled

    private readonly RelationGraphViewModel _vm;
    private readonly Graph3DRenderer _renderer = new();
    private readonly LabelLayer _labelLayer;
    private readonly TextBlock _crosshair = new() { Text = "+", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, IsVisible = false };
    private readonly TextBlock _flyHint = new()
    {
        Text = "FPS · mouse: aim · Z Q S D (W A S D) or arrows: move · Space / C: up / down · Shift: faster · Alt: slower · wheel: speed · click: select · F or Escape: release the mouse",
        FontSize = 11, Margin = new Thickness(10, 8), IsHitTestVisible = false, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };

    private readonly Dictionary<RelationGraphNode, Vector3> _positions = [];
    private readonly Dictionary<RelationGraphNode, Vector3> _velocities = [];
    private readonly Dictionary<RelationGraphNode, float> _depths = [];
    private readonly Dictionary<RelationGraphNode, float> _heights = [];
    private readonly Dictionary<RelationGraphNode, (Mesh3D Mesh, Color Color)> _shapes = [];
    private readonly List<Solid> _arrowSolids = [];
    private readonly List<Tag> _tags = [];
    private readonly List<(RelationGraphEdge Edge, Tag Tag)> _edgeTags = [];
    private readonly List<(ClusterEntry Cluster, Mesh3D Mesh, Color Color, Vector3 Centre, List<Vector3> Members, float Sigma)> _blobs = [];
    private float[] _lines = [];
    private DateTimeOffset _oldest, _newest;
    private DateTimeOffset? _rangeFrom, _rangeTo;
    private float _depthSpan, _timelineY, _heightScale = 1;
    private bool _dirty;

    // The camera turns around its target at a distance; seen from the front, a little from the side and above.
    private Vector3 _target;
    private float _yaw = 0.55f, _pitch = 0.3f, _distance = 60;
    private Matrix4x4 _view, _projection, _viewProjection;
    private Vector3 _cameraPosition, _right, _up, _back;

    // The mouse and the keys.
    private bool _orbiting, _panning, _moved, _fly;
    private Point _last;
    private RelationGraphNode? _hovered;
    private readonly HashSet<PhysicalKey> _keys = [];
    private readonly HashSet<Key> _arrows = [];
    private KeyModifiers _modifiers;
    private float _speedFactor = 1;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;

    /// <summary>Raised after each build with the dates of the whole graph (oldest, newest) and the ones shown (from, to).</summary>
    public event Action<DateTimeOffset, DateTimeOffset, DateTimeOffset, DateTimeOffset>? TimeBoundsChanged;

    /// <summary>Raised when the FPS mode starts or ends.</summary>
    public event Action<bool>? FlyChanged;

    public event Action<RelationGraphNode>? NodeDoubleClicked;
    public event Action<RelationGraphEdge>? EdgeDoubleClicked;
    public event Action<ClusterEntry>? ClusterDoubleClicked;

    public RelationGraph3D(RelationGraphViewModel vm)
    {
        _vm = vm;
        Focusable = true;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        _labelLayer = new LabelLayer(this);
        _renderer.Camera = Frame;
        Children.Add(_renderer);
        Children.Add(_labelLayer);
        Children.Add(_crosshair);
        Children.Add(_flyHint);
        _crosshair.Foreground = Ui.Accent;
        _flyHint.Foreground = Ui.TextBright;

        Action graphChanged = () => Ui.OnMain(Rebuild);
        Action titlesChanged = () => Ui.OnMain(UpdateTitles);
        Action<RelationGraphNode> nodeAdded = _ => Ui.OnMain(Rebuild);
        Action<Palette> themeChanged = _ => Ui.OnMain(() => Build(fit: false));
        AttachedToVisualTree += (_, _) =>
        {
            vm.GraphChanged += graphChanged;
            vm.TitlesChanged += titlesChanged;
            vm.NodeAdded += nodeAdded;
            vm.PeriodsChanged += graphChanged;
            DeskTheme.Changed += themeChanged;
            _timer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            vm.GraphChanged -= graphChanged;
            vm.TitlesChanged -= titlesChanged;
            vm.NodeAdded -= nodeAdded;
            vm.PeriodsChanged -= graphChanged;
            DeskTheme.Changed -= themeChanged;
            _timer.Stop();
            SetFly(false);
        };
        Ui.When(this, vm, Compose, nameof(RelationGraphViewModel.SelectedNode));
        _timer.Tick += (_, _) => Tick();
        LostFocus += (_, _) => FocusLost();
        SizeChanged += (_, _) => Refresh();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible && _dirty)
                Rebuild();
            if (e.Property == IsVisibleProperty && !IsVisible)
                SetFly(false);
        };
    }

    /// <summary>True while the mouse aims the camera (FPS mode).</summary>
    public bool Flying => _fly;

    /// <summary>The vertical scale of the probability (1: from -25 to +25; 0: flat); the camera follows the centre of the graph.</summary>
    public float HeightScale
    {
        get => _heightScale;
        set
        {
            if (MathF.Abs(_heightScale - value) < 1e-4f)
                return;
            _heightScale = value;
            if (_vm.Nodes.Count == 0)
                return;
            var before = Centre();
            Build(fit: false);
            _target += Centre() - before;
            Refresh();
        }
    }

    /// <summary>Shows the elements of the dates from <paramref name="from"/> to <paramref name="to"/> only; all of them when it is the whole graph.</summary>
    public void SetRange(DateTimeOffset from, DateTimeOffset to, bool whole)
    {
        (_rangeFrom, _rangeTo) = whole ? ((DateTimeOffset?)null, (DateTimeOffset?)null) : (from, to);
        Build(fit: false);
    }

    // ── Building ──

    /// <summary>The shapes, names and links of the graph now, laid out from the places of the 2D graph, the whole graph in view.</summary>
    public void Rebuild()
    {
        if (!IsVisible)
        {
            _dirty = true;
            return;
        }
        Build(fit: true);
    }

    private static bool IsLight => DeskTheme.Current.IsLight;

    private static Color Outline => IsLight ? Color.FromArgb(230, 255, 255, 255) : Color.FromArgb(217, 0, 0, 0);

    /// <summary>Builds the scene; <paramref name="layout"/>: the forces run again (not for a change of the names only).</summary>
    private void Build(bool fit, bool layout = true)
    {
        _dirty = false;
        var keep = new Dictionary<RelationGraphNode, Vector3>(_positions);
        _positions.Clear();
        _velocities.Clear();
        _depths.Clear();
        _heights.Clear();
        _shapes.Clear();
        _arrowSolids.Clear();
        _tags.Clear();
        _edgeTags.Clear();

        var today = DateTimeOffset.Now;
        var dates = _vm.Nodes.ToDictionary(n => n, n => Local(n.StartDate) ?? DateOf(n));
        var periods = _vm.Nodes.Where(n => n.StartDate is not null || n.EndDate is not null).ToList();
        var bounds = dates.Values.Concat(periods.Select(n => Local(n.EndDate) ?? today)).ToList();
        _oldest = bounds.Count == 0 ? today : bounds.Min();
        _newest = bounds.Count == 0 ? today : bounds.Max();
        var oldestStart = periods.Select(n => Local(n.StartDate)).Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty(_oldest).Min();
        var hiddenStart = oldestStart - TimeSpan.FromSeconds(Math.Max((_newest - _oldest).TotalSeconds, 86400) * 0.1);
        if (periods.Any(n => n.StartDate is null) && hiddenStart < _oldest)
            _oldest = hiddenStart;
        var (fullOldest, fullNewest) = (_oldest, _newest);
        if (_rangeFrom is { } from && _rangeTo is { } to)
        {
            from = from < fullOldest ? fullOldest : from;
            to = to > fullNewest ? fullNewest : to;
            if (from < to)
                (_oldest, _newest) = (from, to);
            else
                (_rangeFrom, _rangeTo) = (null, null);
        }
        bool Shown(RelationGraphNode node)
        {
            var period = node.StartDate is not null || node.EndDate is not null;
            var first = period ? Local(node.StartDate) ?? hiddenStart : dates[node];
            var last = period ? Local(node.EndDate) ?? today : dates[node];
            return first <= _newest && last >= _oldest;
        }
        var shown = _vm.Nodes.Where(Shown).ToList();
        _depthSpan = Math.Max(30, shown.Count * 7);
        foreach (var node in shown)
        {
            _depths[node] = DepthOf(dates[node]);
            _heights[node] = HeightOf(ProbabilityOf(node));
            _positions[node] = keep.TryGetValue(node, out var kept)
                ? kept with { Y = _heights[node], Z = _depths[node] }
                : new Vector3((float)node.X * Unit, _heights[node], _depths[node]);
            _velocities[node] = Vector3.Zero;
            var color = ColorOf(node);
            _shapes[node] = (EntityShapes.For(node.Type == RelationGraphViewModel.EntityType ? node.EntityType : null, CubeSize), color);
        }
        // Laid out at once: nothing moves afterwards until the user does.
        if (layout || keep.Count == 0)
            Step(400);

        foreach (var node in shown)
        {
            var p = _positions[node];
            var color = _shapes[node].Color;
            var title = new Tag(Title(node), p + new Vector3(0, CubeSize * 1.3f, 0), 1.12f, IsLight ? Color.FromRgb(26, 31, 36) : Color.FromRgb(235, 242, 247), false, 16);
            _tags.Add(title);
            if (KindOf(node) is { } kind)
                _tags.Add(new Tag(kind, title.Position + new Vector3(0, -0.85f, 0), 0.8f, Lighten(color, IsLight ? 0 : 0.2f), true));
            if (node.StartDate is not null || node.EndDate is not null)
                Arrow(node, p, color, hiddenStart, today);
        }
        foreach (var edge in _vm.Edges.Where(e => !ReferenceEquals(e.From, e.To) && _positions.ContainsKey(e.From) && _positions.ContainsKey(e.To)))
        {
            var color = Lighten(RelationGraphCanvas.ColorOf(edge.Relation.Current.Probability), IsLight ? 0 : 0.25f);
            var tag = new Tag(edge.Label, (_positions[edge.From] + _positions[edge.To]) / 2 + new Vector3(0, 0.6f, 0), 0.64f,
                Color.FromArgb((byte)(edge.MatchesFilters ? 255 : 38), color.R, color.G, color.B), false);
            _tags.Add(tag);
            _edgeTags.Add((edge, tag));
        }
        BuildClusters();
        BuildTimeline();
        BuildLines();
        Compose();
        if (fit)
            Fit();
        else
            Refresh();
        TimeBoundsChanged?.Invoke(fullOldest, fullNewest, _oldest, _newest);
    }

    private static DateTimeOffset DateOf(RelationGraphNode node) =>
        node.Relations
            .Select(r => r.Versions.FirstOrDefault()?.Date ?? r.Date)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .DefaultIfEmpty(DateTimeOffset.Now)
            .Min();

    private float DepthOf(DateTimeOffset date)
    {
        var span = (_newest - _oldest).TotalSeconds;
        var t = span <= 0 ? 1 : (float)Math.Clamp((date - _oldest).TotalSeconds / span, 0, 1);
        return -(1 - t) * _depthSpan;
    }

    private static DateTimeOffset? Local(DateTime? date) =>
        date is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d.Date, DateTimeKind.Local)) : null;

    private static double ProbabilityOf(RelationGraphNode node) =>
        node.Relations.Count == 0 ? 0 : Math.Clamp(node.Relations.Average(r => r.Current.Probability), -100, 100);

    private float HeightOf(double probability) => -(float)probability * HeightUnit * _heightScale;

    private static readonly Mesh3D Shaft = Mesh3D.Cylinder(0.32f, 0.32f, 1, 16);
    private static readonly Mesh3D Disc = Mesh3D.Cylinder(0.32f * 2.6f, 0.32f * 2.6f, 0.16f, 16);
    private static readonly Mesh3D Head = Mesh3D.Cylinder(0, 0.85f, 2.2f, 16);

    /// <summary>
    /// The 3D arrow of the period of an entity, along the time, just under its shape: a shaft from its start (a disc there)
    /// to its end and a head at the end (to today without an end); without a start, it comes out of a fade. Its dates at its ends.
    /// </summary>
    private void Arrow(RelationGraphNode node, Vector3 p, Color color, DateTimeOffset hiddenStart, DateTimeOffset today)
    {
        var start = Local(node.StartDate);
        var end = Local(node.EndDate);
        var zStart = DepthOf(start ?? hiddenStart);
        var zEnd = DepthOf(end ?? today);
        if (zEnd < zStart)
            (zStart, zEnd) = (zEnd, zStart);
        var startShown = start is { } first && first >= _oldest;
        var endShown = (end ?? today) <= _newest;
        const float head = 2.2f;
        var shaftEnd = endShown ? Math.Max(zStart, zEnd - head) : zEnd;
        var tint = Lighten(color, IsLight ? 0 : 0.15f);
        var root = new Vector3(p.X, p.Y - CubeSize * 0.95f, 0);
        // The cylinders are along Y: turned a quarter about X, along Z (towards the newest dates).
        void Piece(Mesh3D mesh, float from, float to, float alpha, bool stretch)
        {
            if (to - from < 0.01f)
                return;
            var model = (stretch ? Matrix4x4.CreateScale(1, to - from, 1) : Matrix4x4.Identity)
                        * Matrix4x4.CreateRotationX(MathF.PI / 2)
                        * Matrix4x4.CreateTranslation(root + new Vector3(0, 0, (from + to) / 2));
            _arrowSolids.Add(new Solid(mesh, model, ToVector(tint, alpha), 0.25f * alpha));
        }
        if (start is null)
        {
            const int fades = 10;
            var fadeEnd = zStart + (shaftEnd - zStart) * 0.4f;
            for (var i = 0; i < fades; i++)
                Piece(Shaft, zStart + (fadeEnd - zStart) * i / fades, zStart + (fadeEnd - zStart) * (i + 1) / fades, (i + 1f) / (fades + 1), true);
            Piece(Shaft, fadeEnd, shaftEnd, 1, true);
        }
        else
        {
            Piece(Shaft, zStart, shaftEnd, 1, true);
            if (startShown)
                Piece(Disc, zStart - 0.08f, zStart + 0.08f, 1, false);
        }
        if (endShown)
            Piece(Head, zEnd - head, zEnd, 1, false);
        var dateColor = IsLight ? Color.FromRgb(38, 46, 56) : Color.FromRgb(217, 230, 242);
        if (startShown)
            _tags.Add(new Tag(start!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), root + new Vector3(0, -1.3f, zStart), 0.72f, dateColor, false));
        if (endShown)
            _tags.Add(new Tag(end is { } e ? e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "today", root + new Vector3(0, -1.3f, zEnd), 0.72f, dateColor, false));
    }

    /// <summary>The names of the timeline: a date at each tick, a caption at its ends, the scale of the probability.</summary>
    private void BuildTimeline()
    {
        _timelineY = HeightOf(100) - CubeSize * 3;
        var span = _newest - _oldest;
        var format = span.TotalDays >= 2 ? "yyyy-MM-dd" : span.TotalHours >= 2 ? "dd/MM HH:mm" : "HH:mm:ss";
        var ticks = span.TotalSeconds <= 0 ? 1 : 6;
        var amber = DeskTheme.Current.Warm;
        void Add(string text, Vector3 at) => _tags.Add(new Tag(text, at, 1.77f, amber, true));
        for (var i = 0; i < ticks; i++)
        {
            var t = ticks == 1 ? 1 : i / (float)(ticks - 1);
            var date = _oldest + TimeSpan.FromSeconds(span.TotalSeconds * t);
            var prefix = i == 0 && ticks > 1 ? (_rangeFrom is null ? "oldest · " : "from · ") : i == ticks - 1 ? (_rangeTo is null ? "newest · " : "to · ") : "";
            Add(prefix + date.ToLocalTime().ToString(format, CultureInfo.InvariantCulture), new Vector3(TimelineX, _timelineY - 1.6f, -(1 - t) * _depthSpan));
        }
        Add("TIME →", new Vector3(TimelineX, _timelineY + 1.2f, 4));
        foreach (var probability in new[] { 100, 50, 0, -50, -100 })
            Add(probability > 0 ? $"+{probability}" : $"{probability}", new Vector3(TimelineX - 2.6f, HeightOf(probability), 4));
        Add("PROBABILITY", new Vector3(TimelineX, HeightOf(-100) + 2, 4));
    }

    private float TimelineX => _positions.Count == 0 ? 0 : (_positions.Values.Min(p => p.X) + _positions.Values.Max(p => p.X)) / 2;

    private static string Title(RelationGraphNode node) => node.Title.Length > 48 ? node.Title[..48] + "…" : node.Title;

    private static string? KindOf(RelationGraphNode node) =>
        node.Type == RelationGraphViewModel.EntityType ? node.EntityType?.ToUpperInvariant() : node.Type.ToUpperInvariant();

    private static readonly Dictionary<string, string> EntityColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PERSON"] = "#F59E0B", ["PER"] = "#F59E0B",
        ["ORG"] = "#3B82F6", ["ORGANISATION"] = "#3B82F6", ["ORGANIZATION"] = "#3B82F6",
        ["GPE"] = "#10B981", ["LOC"] = "#22C55E", ["LOCATION"] = "#22C55E", ["FAC"] = "#14B8A6",
        ["NORP"] = "#A855F7",
        ["PRODUCT"] = "#EC4899", ["HARDWARE"] = "#F97316", ["SOFTWARE"] = "#06B6D4", ["AI"] = "#8B5CF6",
        ["EVENT"] = "#EF4444", ["WORK_OF_ART"] = "#D946EF", ["LAW"] = "#64748B", ["LANGUAGE"] = "#0EA5E9",
        ["DATE"] = "#EAB308", ["TIME"] = "#FACC15",
        ["PERCENT"] = "#84CC16", ["MONEY"] = "#16A34A", ["QUANTITY"] = "#65A30D", ["ORDINAL"] = "#A3A3A3", ["CARDINAL"] = "#9CA3AF",
        ["FOOD"] = "#FB7185", ["MATHEMATIC"] = "#6366F1", ["MATERIAL"] = "#B45309",
        ["MISC"] = "#94A3B8", ["OTHERS"] = "#94A3B8", ["CLASS"] = "#475569"
    };

    /// <summary>The colour of an element: of its entity type for an entity (grey without one), of its window otherwise.</summary>
    internal static Color ColorOf(RelationGraphNode node)
    {
        if (node.Type != RelationGraphViewModel.EntityType)
            return RelationGraphCanvas.ColorOf(node.Type);
        if (string.IsNullOrWhiteSpace(node.EntityType))
            return Color.Parse("#8A939E");
        return EntityColors.TryGetValue(node.EntityType.Trim(), out var hex) ? Color.Parse(hex) : RelationGraphCanvas.ColorOf("entity:" + node.EntityType.Trim().ToUpperInvariant());
    }

    private static Color Lighten(Color c, float amount)
    {
        byte Channel(byte v) => (byte)Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromArgb(c.A, Channel(c.R), Channel(c.G), Channel(c.B));
    }

    private static Vector4 ToVector(Color c, float alpha = 1) => new(c.R / 255f, c.G / 255f, c.B / 255f, alpha);

    /// <summary>The names changed, or "Show clusters" was toggled: built again, the camera staying.</summary>
    private void UpdateTitles()
    {
        if (IsVisible)
            Build(fit: false, layout: false);
        else
            _dirty = true;
    }

    // ── The clusters: translucent coloured blobs ──

    private void BuildClusters()
    {
        _blobs.Clear();
        if (!_vm.ShowClusters || _vm.Clusters.Count == 0)
            return;
        foreach (var cluster in _vm.Clusters)
        {
            var members = _positions
                .Where(kv => cluster.Members.Any(m => m.WindowName == kv.Key.Type && m.ObjectId == (kv.Key.ObjectId ?? kv.Key.Key)))
                .Select(kv => kv.Value)
                .ToList();
            if (members.Count == 0)
                continue;
            var centre = members.Aggregate(Vector3.Zero, (s, p) => s + p) / members.Count;
            var sigma = BlobRadius / MathF.Sqrt(-MathF.Log(BlobThreshold));
            var color = RelationGraphCanvas.ColorOf("cluster:" + (cluster.Id ?? cluster.Name));
            var mesh = BlobMesh(centre, members, sigma, out var top);
            _blobs.Add((cluster, mesh, color, centre, members, sigma));
            _tags.Add(new Tag(cluster.Name + (cluster.Probability < 100 ? $" · {cluster.Probability} %" : string.Empty), top + new Vector3(0, 1.4f, 0), 1.54f,
                Lighten(color, IsLight ? -0.25f : 0.25f), true));
        }
    }

    private static float BlobField(Vector3 p, Vector3 centre, List<Vector3> members, float sigma)
    {
        var field = 0f;
        foreach (var m in members)
        {
            var d = DistanceToSegment(p, centre, m);
            field += MathF.Exp(-d * d / (sigma * sigma));
        }
        return field;
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var length2 = ab.LengthSquared();
        if (length2 < 1e-6f)
            return Vector3.Distance(p, a);
        var t = Math.Clamp(Vector3.Dot(p - a, ab) / length2, 0, 1);
        return Vector3.Distance(p, a + ab * t);
    }

    /// <summary>The mesh of a blob: a sphere of rays from its centre, each pushed to where the field falls under the threshold, its normals the gradient of the field.</summary>
    private static Mesh3D BlobMesh(Vector3 centre, List<Vector3> members, float sigma, out Vector3 top)
    {
        const int rings = 40, segments = 64;
        var reach = members.Max(m => Vector3.Distance(m, centre)) + BlobRadius * 2.5f;
        float Field(Vector3 p) => BlobField(p, centre, members, sigma);
        Vector3 Surface(Vector3 direction)
        {
            var step = Math.Max(0.5f, reach / 120);
            var last = 0f;
            for (var t = 0f; t <= reach; t += step)
                if (Field(centre + direction * t) >= BlobThreshold)
                    last = t;
            var (low, high) = (last, last + step);
            for (var i = 0; i < 12; i++)
            {
                var mid = (low + high) / 2;
                if (Field(centre + direction * mid) >= BlobThreshold)
                    low = mid;
                else
                    high = mid;
            }
            return centre + direction * ((low + high) / 2);
        }
        Vector3 Normal(Vector3 p)
        {
            const float e = 0.05f;
            var gradient = new Vector3(
                Field(p + new Vector3(e, 0, 0)) - Field(p - new Vector3(e, 0, 0)),
                Field(p + new Vector3(0, e, 0)) - Field(p - new Vector3(0, e, 0)),
                Field(p + new Vector3(0, 0, e)) - Field(p - new Vector3(0, 0, e)));
            return gradient.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(-gradient);
        }
        var points = new Vector3[(rings + 1) * (segments + 1)];
        var normals = new Vector3[points.Length];
        top = centre;
        for (var i = 0; i <= rings; i++)
        {
            var theta = MathF.PI * i / rings;
            for (var j = 0; j <= segments; j++)
            {
                var phi = 2 * MathF.PI * j / segments;
                var p = Surface(new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi)));
                var k = i * (segments + 1) + j;
                points[k] = p;
                normals[k] = Normal(p);
                if (p.Y > top.Y)
                    top = p;
            }
        }
        var mesh = new Mesh3D();
        for (var i = 0; i < rings; i++)
            for (var j = 0; j < segments; j++)
            {
                var a = i * (segments + 1) + j;
                var b = a + segments + 1;
                foreach (var k in new[] { a, b, a + 1, a + 1, b, b + 1 })
                    mesh.Corner(points[k], normals[k]);
            }
        return mesh;
    }

    private ClusterEntry? ClusterAt(Point point)
    {
        if (_blobs.Count == 0)
            return null;
        var (origin, direction) = Ray(point);
        ClusterEntry? best = null;
        var bestDepth = float.MaxValue;
        foreach (var (cluster, _, _, centre, members, sigma) in _blobs)
        {
            var reach = members.Max(m => Vector3.Distance(m, centre)) + BlobRadius * 2.5f;
            var near = Math.Max(0, Vector3.Distance(origin, centre) - reach);
            var far = Vector3.Distance(origin, centre) + reach;
            for (var t = near; t <= far; t += 0.6f)
            {
                if (BlobField(origin + direction * t, centre, members, sigma) < BlobThreshold)
                    continue;
                if (t < bestDepth)
                    (best, bestDepth) = (cluster, t);
                break;
            }
        }
        return best;
    }

    // ── The scene ──

    /// <summary>The links with their arrowheads, the timeline and its ticks, and a thin line from each element down to its date.</summary>
    private void BuildLines()
    {
        var lines = new List<float>();
        void Line(Vector3 a, Vector3 b, Color color, float alpha)
        {
            foreach (var p in new[] { a, b })
                lines.AddRange([p.X, p.Y, p.Z, color.R / 255f, color.G / 255f, color.B / 255f, alpha]);
        }
        foreach (var edge in _vm.Edges)
        {
            if (ReferenceEquals(edge.From, edge.To) || !_positions.TryGetValue(edge.From, out var from) || !_positions.TryGetValue(edge.To, out var to))
                continue;
            var color = RelationGraphCanvas.ColorOf(edge.Relation.Current.Probability);
            var alpha = edge.MatchesFilters ? 0.95f : 0.12f;
            var direction = Vector3.Normalize(to - from);
            var start = from + direction * CubeSize * 0.8f;
            var end = to - direction * CubeSize * 0.9f;
            Line(start, end, color, alpha);
            var side = Vector3.Normalize(Vector3.Cross(direction, MathF.Abs(direction.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY));
            var up = Vector3.Normalize(Vector3.Cross(side, direction));
            var back = end - direction * 1.2f;
            foreach (var v in new[] { side, -side, up, -up })
                Line(end, back + v * 0.5f, color, alpha);
        }
        if (_positions.Count > 0)
        {
            var x = TimelineX;
            var amber = DeskTheme.Current.Warm;
            Line(new Vector3(x, _timelineY, -_depthSpan - 4), new Vector3(x, _timelineY, 4), amber, 1);
            Line(new Vector3(x, _timelineY, 4), new Vector3(x - 0.7f, _timelineY, 2.6f), amber, 1);
            Line(new Vector3(x, _timelineY, 4), new Vector3(x + 0.7f, _timelineY, 2.6f), amber, 1);
            for (var i = 0; i < 6; i++)
            {
                var z = -_depthSpan * (1 - i / 5f);
                Line(new Vector3(x - 0.8f, _timelineY, z), new Vector3(x + 0.8f, _timelineY, z), amber, 1);
            }
            Line(new Vector3(x, HeightOf(100), 4), new Vector3(x, HeightOf(-100), 4), amber, 1);
            for (var probability = -100; probability <= 100; probability += 50)
                Line(new Vector3(x - 0.8f, HeightOf(probability), 4), new Vector3(x + 0.8f, HeightOf(probability), 4), amber, 1);
            var drop = IsLight ? Color.FromRgb(89, 102, 115) : Color.FromRgb(153, 173, 191);
            foreach (var p in _positions.Values)
            {
                Line(p - new Vector3(0, CubeSize * 0.6f, 0), new Vector3(p.X, _timelineY, p.Z), drop, 0.28f);
                Line(new Vector3(p.X, _timelineY, p.Z), new Vector3(x, _timelineY, p.Z), drop, 0.28f);
            }
        }
        _lines = [.. lines];
    }

    /// <summary>
    /// The shapes as they are now: the selected element larger and glowing, its neighbours glowing (the first degree more
    /// than the second), the others dimmed while there is a selection; then the arrows and the blobs.
    /// </summary>
    private void Compose()
    {
        var solids = new List<Solid>();
        foreach (var (node, (mesh, color)) in _shapes)
        {
            if (!_positions.TryGetValue(node, out var p))
                continue;
            var selected = ReferenceEquals(node, _vm.SelectedNode);
            var hovered = ReferenceEquals(node, _hovered);
            var distance = _vm.DistanceOf(node);
            var glow = selected ? 1.4f : hovered ? 0.6f : distance == 1 ? 0.9f : distance == 2 ? 0.5f : _vm.HasProximity ? 0.03f : 0.15f;
            var scale = selected ? 1.35f : hovered ? 1.15f : distance == 1 ? 1.2f : distance == 2 ? 1.1f : 1f;
            solids.Add(new Solid(mesh, Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(p), ToVector(color), glow));
        }
        solids.AddRange(_arrowSolids);
        foreach (var (_, mesh, color, _, _, _) in _blobs)
            solids.Add(new Solid(mesh, Matrix4x4.Identity, ToVector(color, IsLight ? 0.22f : 0.18f), IsLight ? 0.05f : 0.35f, 0.6f));
        _renderer.Scene = new Scene3D(solids, _lines);
        Refresh();
    }

    // ── Layout by forces, across only ──

    private void Step(int steps)
    {
        var nodes = _positions.Keys.ToList();
        const float repulsion = 900, springLength = 16, springStrength = 0.04f, gravity = 0.006f, damping = 0.82f;
        for (var step = 0; step < steps; step++)
        {
            foreach (var a in nodes)
            {
                var pa = _positions[a];
                var force = -pa * gravity;
                foreach (var b in nodes)
                {
                    if (ReferenceEquals(a, b))
                        continue;
                    var d = pa - _positions[b];
                    var distance2 = Math.Max(d.LengthSquared(), 1f);
                    force += (d.LengthSquared() < 1e-12f ? Vector3.UnitX : Vector3.Normalize(d)) * (repulsion / distance2);
                }
                _velocities[a] = (_velocities[a] + force) * damping;
            }
            foreach (var edge in _vm.Edges)
            {
                if (ReferenceEquals(edge.From, edge.To) || !_positions.TryGetValue(edge.From, out var from) || !_positions.TryGetValue(edge.To, out var to))
                    continue;
                var d = to - from;
                var distance = Math.Max(0.1f, d.Length());
                var pull = d / distance * ((distance - springLength) * springStrength);
                _velocities[edge.From] += pull;
                _velocities[edge.To] -= pull;
            }
            foreach (var node in nodes)
            {
                var v = _velocities[node];
                if (v.Length() > 4)
                    v = Vector3.Normalize(v) * 4;
                _positions[node] = (_positions[node] + v) with { Y = _heights[node], Z = _depths[node] };
            }
        }
    }

    // ── The camera ──

    /// <summary>Zooms by <paramref name="scale"/> (above 1: closer).</summary>
    public void ZoomBy(float scale)
    {
        _distance = Math.Clamp(_distance / scale, 6, 2000);
        Refresh();
    }

    private Vector3 Offset() => new(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));

    private double Aspect => Bounds.Width > 1 && Bounds.Height > 1 ? Bounds.Width / Bounds.Height : 1.6;

    /// <summary>The whole graph in view, from where the camera is turned: its elements with their names, the arrows and the timeline.</summary>
    public void Fit()
    {
        if (_positions.Count == 0)
        {
            _target = Vector3.Zero;
            _distance = 40;
            Refresh();
            return;
        }
        var points = new List<Vector3>();
        foreach (var p in _positions.Values)
        {
            points.Add(p + new Vector3(-CubeSize, CubeSize * 1.8f, 0));
            points.Add(p + new Vector3(CubeSize, -CubeSize * 1.4f, 0));
        }
        points.Add(new Vector3(TimelineX, _timelineY - 2.5f, -_depthSpan - 4));
        points.Add(new Vector3(TimelineX, _timelineY - 2.5f, 4));
        points.Add(new Vector3(TimelineX - 3, HeightOf(-100) + 3, 4));
        var low = points.Aggregate(Vector3.Min);
        var high = points.Aggregate(Vector3.Max);
        _target = (low + high) / 2;
        _pitch = Math.Clamp(_pitch, -1.45f, 1.45f);
        var back = Offset();
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, back));
        var up = Vector3.Cross(back, right);
        var tanV = MathF.Tan(FieldOfView * MathF.PI / 360);
        var tanH = tanV * (float)Aspect;
        var distance = 12f;
        foreach (var p in points)
        {
            var d = p - _target;
            distance = Math.Max(distance, Vector3.Dot(d, back) + Math.Max(MathF.Abs(Vector3.Dot(d, right)) / tanH, MathF.Abs(Vector3.Dot(d, up)) / tanV));
        }
        _distance = Math.Clamp(distance * 1.05f, 12, 2000);
        Refresh();
    }

    private Vector3 Centre()
    {
        if (_positions.Count == 0)
            return Vector3.Zero;
        var points = _positions.Values.Append(new Vector3(TimelineX, _timelineY, 0)).Append(new Vector3(TimelineX, _timelineY, -_depthSpan)).ToList();
        return points.Aggregate(Vector3.Zero, (s, p) => s + p) / points.Count;
    }

    /// <summary>The camera placed from its target, angles and distance; a frame drawn, the names placed again.</summary>
    private void Refresh()
    {
        _pitch = Math.Clamp(_pitch, -1.45f, 1.45f);
        _back = Offset();
        _cameraPosition = _target + _back * _distance;
        _right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, _back));
        _up = Vector3.Cross(_back, _right);
        _view = Matrix4x4.CreateLookAt(_cameraPosition, _target, Vector3.UnitY);
        _projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView * MathF.PI / 180, (float)Aspect, 0.1f, 4000);
        _viewProjection = _view * _projection;
        _renderer.RequestNextFrameRendering();
        _labelLayer.InvalidateVisual();
    }

    private View3D Frame(int width, int height)
    {
        var accent = DeskTheme.Current.Accent;
        var background = IsLight ? new Vector3(0.97f, 0.98f, 0.99f) : new Vector3(0.015f, 0.022f, 0.03f);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView * MathF.PI / 180, width / (float)Math.Max(1, height), 0.1f, 4000);
        return new View3D(_view, projection, _cameraPosition, background,
            IsLight ? new Vector3(0.75f, 0.78f, 0.82f) : new Vector3(0.32f, 0.38f, 0.45f),
            new Vector3(accent.R / 255f, accent.G / 255f, accent.B / 255f),
            IsLight ? 0.0012f : 0.003f);
    }

    /// <summary>Where <paramref name="p"/> is on the control, and how far in front of the camera; false behind it.</summary>
    private bool Project(Vector3 p, out Point screen, out float depth)
    {
        var clip = Vector4.Transform(new Vector4(p, 1), _viewProjection);
        depth = clip.W;
        if (clip.W < 0.1f)
        {
            screen = default;
            return false;
        }
        screen = new Point((clip.X / clip.W * 0.5 + 0.5) * Bounds.Width, (0.5 - clip.Y / clip.W * 0.5) * Bounds.Height);
        return true;
    }

    /// <summary>The ray from the camera through <paramref name="point"/>.</summary>
    private (Vector3 Origin, Vector3 Direction) Ray(Point point)
    {
        var tanV = MathF.Tan(FieldOfView * MathF.PI / 360);
        var x = (float)(point.X / Math.Max(1, Bounds.Width) * 2 - 1) * tanV * (float)Aspect;
        var y = (float)(1 - point.Y / Math.Max(1, Bounds.Height) * 2) * tanV;
        return (_cameraPosition, Vector3.Normalize(-_back + _right * x + _up * y));
    }

    // ── The pointer and the keys ──

    private RelationGraphNode? NodeAt(Point point)
    {
        RelationGraphNode? best = null;
        var bestDepth = float.MaxValue;
        foreach (var (node, position) in _positions)
        {
            if (!Project(position, out var screen, out var depth) || !Project(position + _right * CubeSize * 0.75f, out var edge, out _))
                continue;
            var radius = Math.Max(8, Distance(screen, edge));
            if (Distance(screen, point) <= radius && depth < bestDepth)
                (best, bestDepth) = (node, depth);
        }
        return best;
    }

    private RelationGraphEdge? EdgeAt(Point point)
    {
        foreach (var (edge, tag) in _edgeTags)
            if (Project(tag.Position, out var screen, out _) && Distance(screen, point) < 26)
                return edge;
        return null;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        e.Handled = true;
        if (_fly)
        {
            if (properties.IsLeftButtonPressed)
                _vm.SelectedNode = NodeAt(new Point(Bounds.Width / 2, Bounds.Height / 2));
            return;
        }
        if (properties.IsLeftButtonPressed && e.ClickCount == 2)
        {
            if (NodeAt(point) is { } opened)
                NodeDoubleClicked?.Invoke(opened);
            else if (EdgeAt(point) is { } edge)
                EdgeDoubleClicked?.Invoke(edge);
            else if (ClusterAt(point) is { } cluster)
                ClusterDoubleClicked?.Invoke(cluster);
            return;
        }
        _moved = false;
        _panning = !properties.IsLeftButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _orbiting = !_panning;
        _last = point;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_fly)
            return;
        var point = e.GetPosition(this);
        if (!_moved && e.InitialPressMouseButton == MouseButton.Left && (_orbiting || _panning))
            _vm.SelectedNode = NodeAt(point);
        else if (!_moved && e.InitialPressMouseButton == MouseButton.Right && NodeAt(point) is { } clicked
                 && Data.Annotations.OfGraphNode(clicked) is { } target)
            Data.Annotations.Menu(this, target).Open(this);
        _orbiting = _panning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_fly)
        {
            var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var origin = OperatingSystem.IsWindows() ? centre : _last;
            var (dx, dy) = (point.X - origin.X, point.Y - origin.Y);
            if (Math.Abs(dx) + Math.Abs(dy) > 0.5)
            {
                Look((float)dx, (float)dy);
                Recentre();
            }
            _last = point;
            return;
        }
        if (_orbiting || _panning)
        {
            var (dx, dy) = ((float)(point.X - _last.X), (float)(point.Y - _last.Y));
            _last = point;
            _moved |= MathF.Abs(dx) + MathF.Abs(dy) > 1;
            if (_orbiting)
            {
                _yaw -= dx * 0.008f;
                _pitch += dy * 0.008f;
            }
            else
                _target += (-_right * dx + _up * dy) * (_distance * 0.0016f);
            Refresh();
            return;
        }
        var hovered = NodeAt(point);
        if (!ReferenceEquals(hovered, _hovered))
        {
            _hovered = hovered;
            Compose();
            Cursor = hovered is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, hovered is null ? null : $"{hovered.Type} · {hovered.Title} · click: details · double-click: open");
        }
        if (hovered is null && _blobs.Count > 0)
        {
            var cluster = ClusterAt(point);
            Cursor = cluster is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, cluster is null ? null : $"Cluster · {cluster.Name} · double-click: edit");
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        if (_fly)
        {
            _speedFactor = Math.Clamp(_speedFactor * (e.Delta.Y > 0 ? 1.25f : 0.8f), 0.1f, 5f);
            _vm.ReportStatus($"FPS: speed ×{_speedFactor:0.0#} (wheel: faster / slower · Shift: ×2.5 · Alt: ×0.3)", false);
            return;
        }
        ZoomBy(MathF.Pow(1.12f, (float)e.Delta.Y));
    }

    private static readonly PhysicalKey[] MoveKeys = [PhysicalKey.W, PhysicalKey.A, PhysicalKey.S, PhysicalKey.D, PhysicalKey.Space, PhysicalKey.C];
    private static readonly Key[] ArrowKeys = [Key.Up, Key.Down, Key.Left, Key.Right];

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _modifiers = e.KeyModifiers;
        if (e.PhysicalKey == PhysicalKey.F && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            SetFly(!_fly);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _fly)
        {
            SetFly(false);
            e.Handled = true;
        }
        else if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 && (MoveKeys.Contains(e.PhysicalKey) || ArrowKeys.Contains(e.Key)))
        {
            if (MoveKeys.Contains(e.PhysicalKey))
                _keys.Add(e.PhysicalKey);
            else
                _arrows.Add(e.Key);
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _modifiers = e.KeyModifiers;
        _keys.Remove(e.PhysicalKey);
        _arrows.Remove(e.Key);
    }

    private void FocusLost()
    {
        _keys.Clear();
        _arrows.Clear();
        SetFly(false);
    }

    /// <summary>
    /// At each tick, the keys held move the camera along where it looks: Z / W or ↑ forward, S or ↓ back, Q / A or ← left,
    /// D or → right (by their place on the keyboard), Space up, C down, Shift faster, Alt slower.
    /// </summary>
    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var delta = (float)Math.Min(0.1, now - _lastTick);
        _lastTick = now;
        if (_keys.Count == 0 && _arrows.Count == 0)
            return;
        var forward = -_back;
        var direction = Vector3.Zero;
        if (_keys.Contains(PhysicalKey.W) || _arrows.Contains(Key.Up)) direction += forward;
        if (_keys.Contains(PhysicalKey.S) || _arrows.Contains(Key.Down)) direction -= forward;
        if (_keys.Contains(PhysicalKey.D) || _arrows.Contains(Key.Right)) direction += _right;
        if (_keys.Contains(PhysicalKey.A) || _arrows.Contains(Key.Left)) direction -= _right;
        if (_keys.Contains(PhysicalKey.Space)) direction += Vector3.UnitY;
        if (_keys.Contains(PhysicalKey.C)) direction -= Vector3.UnitY;
        if (direction.LengthSquared() < 1e-6f)
            return;
        var modifier = _modifiers.HasFlag(KeyModifiers.Shift) ? 2.5f : _modifiers.HasFlag(KeyModifiers.Alt) ? 0.3f : 1f;
        var speed = Math.Clamp(_distance * 0.25f, 3, 80) * _speedFactor * modifier;
        _target += Vector3.Normalize(direction) * speed * delta;
        Refresh();
    }

    /// <summary>Turns the camera where it stands (FPS): the mouse to the right turns right, up looks up.</summary>
    private void Look(float dx, float dy)
    {
        var position = _target + Offset() * _distance;
        _yaw -= dx * MouseSensitivity;
        _pitch = Math.Clamp(_pitch + dy * MouseSensitivity, -1.45f, 1.45f);
        _target = position - Offset() * _distance;
        Refresh();
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    /// <summary>The pointer back in the middle of the view (Windows), so that the mouse can turn without end.</summary>
    private void Recentre()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var centre = this.PointToScreen(new Point(Bounds.Width / 2, Bounds.Height / 2));
        SetCursorPos(centre.X, centre.Y);
    }

    /// <summary>Starts or ends the FPS mode: the pointer hidden in the middle of the view (it aims the camera), or given back.</summary>
    public void SetFly(bool on)
    {
        if (on == _fly)
            return;
        _fly = on;
        _crosshair.IsVisible = _flyHint.IsVisible = on;
        Cursor = on ? new Cursor(StandardCursorType.None) : Cursor.Default;
        if (on)
        {
            Focus();
            _last = new Point(Bounds.Width / 2, Bounds.Height / 2);
            Recentre();
        }
        _vm.ReportStatus(on ? "FPS: the mouse aims, Z Q S D move · F or Escape gives the mouse back" : "FPS off: the mouse is back", false);
        FlyChanged?.Invoke(on);
    }

    // ── The capture ──

    /// <summary>Writes the 3D graph as shown (the shapes and the names) to <paramref name="path"/> as a PNG picture.</summary>
    public Task<bool> SaveCaptureAsync(string path)
    {
        var done = new TaskCompletionSource<bool>();
        _renderer.Capture((rgba, width, height) => Dispatcher.UIThread.Post(() => done.TrySetResult(Write(path, rgba, width, height))));
        return done.Task;
    }

    private bool Write(string path, byte[] rgba, int width, int height)
    {
        try
        {
            using var picture = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var pixels = picture.GetPixelSpan();
            var row = width * 4;
            // OpenGL reads from the bottom row.
            for (var y = 0; y < height; y++)
                rgba.AsSpan((height - 1 - y) * row, row).CopyTo(pixels.Slice(y * row, row));
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            using (var names = new RenderTargetBitmap(new PixelSize(width, height), new Avalonia.Vector(96 * scaling, 96 * scaling)))
            {
                names.Render(_labelLayer);
                using var stream = new MemoryStream();
                names.Save(stream);
                stream.Position = 0;
                using var overlay = SKBitmap.Decode(stream);
                using var canvas = new SKCanvas(picture);
                if (overlay is not null)
                    canvas.DrawBitmap(overlay, 0, 0);
            }
            using var image = SKImage.FromBitmap(picture);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            data.SaveTo(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    // ── The names ──

    /// <summary>A name in the scene: its text, where it is, the height of its letters in the world, its colour, whether in the display font, its width before wrapping (in letters).</summary>
    private sealed class Tag(string text, Vector3 position, float height, Color color, bool display, double wrapEms = 0)
    {
        public Vector3 Position { get; } = position;
        public float Height { get; } = height;
        private Geometry? _geometry;
        private Rect _bounds;

        /// <summary>The outline of the letters, laid out once at <see cref="LabelFontSize"/>, centred on the origin.</summary>
        public (Geometry? Geometry, Rect Bounds) Shape()
        {
            if (_geometry is not null || string.IsNullOrEmpty(text))
                return (_geometry, _bounds);
            var family = Application.Current?.TryGetResource(display ? "DisplayFont" : "BodyFont", null, out var f) == true && f is FontFamily font ? font : FontFamily.Default;
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(family, FontStyle.Normal, display ? FontWeight.Bold : FontWeight.SemiBold), LabelFontSize, Brushes.White)
            {
                TextAlignment = TextAlignment.Center
            };
            if (wrapEms > 0)
                formatted.MaxTextWidth = wrapEms * LabelFontSize;
            var width = wrapEms > 0 ? formatted.MaxTextWidth : formatted.WidthIncludingTrailingWhitespace;
            _geometry = formatted.BuildGeometry(new Point(-width / 2, -formatted.Height / 2));
            _bounds = _geometry?.Bounds ?? default;
            return (_geometry, _bounds);
        }

        public IBrush Fill { get; } = new SolidColorBrush(color);
        public IPen Pen { get; } = new Pen(new SolidColorBrush(Outline), LabelFontSize * 0.16, lineJoin: PenLineJoin.Round);
    }

    /// <summary>The names over the 3D picture: each faces the camera, its size by its distance, the farthest drawn first.</summary>
    private sealed class LabelLayer(RelationGraph3D owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (owner._tags.Count == 0 || Bounds.Width < 2 || Bounds.Height < 2)
                return;
            var tanV = MathF.Tan(FieldOfView * MathF.PI / 360);
            var visible = new List<(Tag Tag, Point Screen, float Depth)>();
            foreach (var tag in owner._tags)
                if (owner.Project(tag.Position, out var screen, out var depth)
                    && screen.X > -400 && screen.X < Bounds.Width + 400 && screen.Y > -100 && screen.Y < Bounds.Height + 100)
                    visible.Add((tag, screen, depth));
            foreach (var (tag, screen, depth) in visible.OrderByDescending(v => v.Depth))
            {
                // The height of its letters on the screen, in pixels.
                var pixels = tag.Height * (Bounds.Height / 2) / (tanV * depth);
                if (pixels < 4)
                    continue;
                var (geometry, _) = tag.Shape();
                if (geometry is null)
                    continue;
                var scale = pixels / LabelFontSize;
                using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(screen.X, screen.Y)))
                {
                    context.DrawGeometry(null, tag.Pen, geometry);
                    context.DrawGeometry(tag.Fill, null, geometry);
                }
            }
        }
    }
}
