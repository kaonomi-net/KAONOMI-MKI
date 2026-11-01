using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// The relation graph in 3D (the 3D button of /relation): a sphere per element, in the colour of its entity type for
/// an entity of the ontology (else of its window), its name above it with its entity type (or its window) under the name; a line per link from the source to the destination, coloured by the probability of the relation (red to
/// green, grey to evaluate), with an arrowhead and the title of the relation at its middle; the links that do not meet
/// the filters are transparent. The depth is the time: the oldest elements (the date of their first relation) at the
/// back, the most recent in front, a timeline under the graph with its dates, a thin line from each sphere down to its
/// date. An entity of the ontology with a start date or an end date has a 3D arrow along the time, under its sphere: from
/// its start date (its sphere there too) to its end date, or to the day the graph is built when it has no end; with an
/// end date only, the arrow comes out of the past, its start hidden in a fade from 10% before the oldest start date (or
/// the oldest date) of the graph. The height is the probability, from -100 to +100 (the mean of the probabilities of the
/// relations of the element): the higher the probability, the lower the sphere; a scale beside the timeline gives it.
/// Across, the elements are laid out by forces (they push each other away, the links pull their ends together),
/// from the places of the 2D graph. Nothing moves until the user does: drag to turn around the graph, right drag (or
/// Shift + drag) to move it, the wheel to come closer; a click selects an element (its details show on the right), a
/// double-click opens it, a double-click on a link title opens its relation. With "Show clusters", each cluster stored
/// on the server is a translucent coloured blob (a potato: a metaball around its elements and the spokes from their
/// centre, so that it is always in one piece) with its name above it; a double-click on the blob (not on an element)
/// opens the popup of the cluster.
/// </summary>
public partial class RelationGraph3D : SubViewportContainer
{
    private const float Unit = 0.06f;      // world units per pixel of the 2D layout
    private const float CubeSize = 3.2f;
    /// <summary>World units per point of probability at the scale 1: +100 at -25 (low), -100 at +25 (high).</summary>
    private const float HeightUnit = 0.25f;
    private float _heightScale = 1;

    private readonly RelationGraphViewModel _vm;
    private readonly SubViewport _viewport = new() { OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X, HandleInputLocally = false };
    private readonly Camera3D _camera = new() { Fov = 50, Far = 4000 };
    private readonly Node3D _scene = new();
    private readonly Dictionary<RelationGraphNode, (MeshInstance3D Cube, Label3D Label, StandardMaterial3D Material)> _cubes = [];
    private readonly Dictionary<RelationGraphNode, Vector3> _positions = [];
    private readonly Dictionary<RelationGraphNode, Vector3> _velocities = [];
    private readonly List<(RelationGraphEdge Edge, Label3D Label)> _edgeLabels = [];
    private MeshInstance3D _lines = null!;
    private ImmediateMesh _lineMesh = null!;
    /// <summary>The depth of each element: its date, the oldest at the back.</summary>
    private readonly Dictionary<RelationGraphNode, float> _depths = [];
    /// <summary>The height of each element: its probability, the higher the lower.</summary>
    private readonly Dictionary<RelationGraphNode, float> _heights = [];
    private readonly List<Label3D> _timelineLabels = [];
    private DateTimeOffset _oldest, _newest;
    /// <summary>The dates shown (the double slider under the graph); null: all of them, from the oldest to the newest.</summary>
    private DateTimeOffset? _rangeFrom, _rangeTo;

    /// <summary>
    /// Raised after each build with the dates of the whole graph (oldest, newest) and the ones shown (from, to), for the
    /// double slider under it.
    /// </summary>
    public event Action<DateTimeOffset, DateTimeOffset, DateTimeOffset, DateTimeOffset>? TimeBoundsChanged;

    /// <summary>
    /// Shows the elements of the dates from <paramref name="from"/> to <paramref name="to"/> only (their date, or their
    /// period for an entity, meets it), the time axis going from one to the other; the whole graph when it is all of it.
    /// The camera stays where it is.
    /// </summary>
    public void SetRange(DateTimeOffset from, DateTimeOffset to, bool whole)
    {
        (_rangeFrom, _rangeTo) = whole ? ((DateTimeOffset?)null, (DateTimeOffset?)null) : (from, to);
        Build(fit: false);
    }

    /// <summary>
    /// The vertical scale of the probability (1: from -25 to +25 world units; 0: everything at the same height); the
    /// graph is laid out again, the camera following its centre (it stays where it was on the screen).
    /// </summary>
    public float HeightScale
    {
        get => _heightScale;
        set
        {
            if (Mathf.IsEqualApprox(_heightScale, value))
                return;
            _heightScale = value;
            if (_vm.Nodes.Count == 0)
                return;
            var before = Centre();
            Build(fit: false);
            _target += Centre() - before;
            PlaceCamera();
        }
    }

    /// <summary>The arrows of the periods of the entities (start date → end date), each drawn just under its sphere.</summary>
    private readonly Dictionary<RelationGraphNode, Node3D> _arrows = [];
    private float _depthSpan, _timelineY;

    // Camera: it turns around the target at a distance.
    private Vector3 _target;
    // Seen from the front and a little from the side and above: the depth (the time) shows.
    private float _yaw = 0.55f, _pitch = 0.3f, _distance = 60;
    private bool _orbiting, _panning, _moved;

    // ── Moving like in a first-person game ──

    /// <summary>
    /// The FPS mode (F): the mouse is captured and aims the camera where it stands, the keys move it; F or Escape gives
    /// the mouse back.
    /// </summary>
    private bool _fly;

    /// <summary>The cross in the middle of the view while the mouse aims (FPS mode), and the reminder of the keys.</summary>
    private readonly Label _crosshair = new() { Text = "+", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
    private readonly Label _flyHint = new()
    {
        Text = "FPS · mouse: aim · Z Q S D (W A S D) or arrows: move · Space / C: up / down · Shift: faster · Alt: slower · wheel: speed · click: select · F or Escape: release the mouse",
        MouseFilter = MouseFilterEnum.Ignore, Visible = false
    };

    /// <summary>Raised when the FPS mode starts or ends (the toolbar button follows it).</summary>
    public event Action<bool>? FlyChanged;

    /// <summary>True while the mouse aims the camera (FPS mode).</summary>
    public bool Flying => _fly;
    private RelationGraphNode? _hovered;

    /// <summary>Raised by a double-click on an element.</summary>
    public event Action<RelationGraphNode>? NodeDoubleClicked;

    /// <summary>Raised by a double-click on the title of a link.</summary>
    public event Action<RelationGraphEdge>? EdgeDoubleClicked;

    /// <summary>Raised by a double-click on the blob of a cluster (shown by "Show clusters").</summary>
    public event Action<KAONOMI.Chat.Models.ClusterEntry>? ClusterDoubleClicked;

    /// <summary>The blobs of the clusters: the cluster, its mesh, its name, the centres of its spokes and the scale of its field.</summary>
    private readonly List<(KAONOMI.Chat.Models.ClusterEntry Cluster, MeshInstance3D Blob, Label3D Label, Vector3 Centre, List<Vector3> Members, float Sigma)> _blobs = [];

    public RelationGraph3D() : this(new RelationGraphViewModel())
    {
    }

    public RelationGraph3D(RelationGraphViewModel vm)
    {
        _vm = vm;
        Stretch = true;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.Click;
        AddChild(_viewport);
        _viewport.AddChild(_scene);
        _scene.AddChild(_camera);
        _camera.Current = true;
        // Over the picture of the scene: the cross and the keys of the FPS mode.
        _crosshair.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _crosshair.AddThemeFontSizeOverride("font_size", 26);
        _crosshair.AddThemeColorOverride("font_color", Eve.Eve.Cyan);
        AddChild(_crosshair);
        _flyHint.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        _flyHint.Position = new Vector2(10, 8);
        _flyHint.AddThemeFontSizeOverride("font_size", 11);
        _flyHint.AddThemeColorOverride("font_color", Eve.Eve.TextBright);
        _flyHint.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        AddChild(_flyHint);

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Eve.Eve.IsLight ? new Color(0.97f, 0.98f, 0.99f) : new Color(0.015f, 0.022f, 0.03f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Eve.Eve.IsLight ? new Color(0.75f, 0.78f, 0.82f) : new Color(0.32f, 0.38f, 0.45f),
            AmbientLightEnergy = 1f,
            GlowEnabled = !Eve.Eve.IsLight,
            GlowIntensity = 0.6f,
            GlowBloom = 0.08f,
            FogEnabled = true,
            FogLightColor = Eve.Eve.IsLight ? new Color(0.97f, 0.98f, 0.99f) : new Color(0.015f, 0.022f, 0.03f),
            FogDensity = Eve.Eve.IsLight ? 0.0012f : 0.003f
        };
        _scene.AddChild(new WorldEnvironment { Environment = environment });
        var sun = new DirectionalLight3D { LightEnergy = 1.1f, RotationDegrees = new Vector3(-50, 35, 0) };
        _scene.AddChild(sun);
        var fill = new DirectionalLight3D { LightEnergy = 0.35f, RotationDegrees = new Vector3(30, -140, 0), LightColor = Eve.Eve.Cyan };
        _scene.AddChild(fill);

        _lineMesh = new ImmediateMesh();
        _lines = new MeshInstance3D
        {
            Mesh = _lineMesh,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha
            }
        };
        _scene.AddChild(_lines);

        Action graphChanged = () => Bind.OnMain(Rebuild);
        Action titlesChanged = () => Bind.OnMain(UpdateTitles);
        Action<RelationGraphNode> nodeAdded = _ => Bind.OnMain(Rebuild);
        vm.GraphChanged += graphChanged;
        vm.TitlesChanged += titlesChanged;
        vm.NodeAdded += nodeAdded;
        vm.PeriodsChanged += graphChanged;
        Bind.OnGone(this, () =>
        {
            vm.GraphChanged -= graphChanged;
            vm.TitlesChanged -= titlesChanged;
            vm.NodeAdded -= nodeAdded;
            vm.PeriodsChanged -= graphChanged;
        });
        Bind.When(this, vm, UpdateSelection, nameof(RelationGraphViewModel.SelectedNode));
        Rebuild();
    }

    // ── Building ──

    /// <summary>The spheres, labels and links of the graph now, laid out from the places of the 2D graph.</summary>
    public void Rebuild() => Build(fit: true);

    private void Build(bool fit)
    {
        foreach (var (cube, label, _) in _cubes.Values)
        {
            cube.QueueFree();
            label.QueueFree();
        }
        foreach (var (_, label) in _edgeLabels)
            label.QueueFree();
        _cubes.Clear();
        _edgeLabels.Clear();
        foreach (var label in _timelineLabels)
            label.QueueFree();
        _timelineLabels.Clear();
        foreach (var arrow in _arrows.Values)
            arrow.QueueFree();
        _arrows.Clear();
        var keep = new Dictionary<RelationGraphNode, Vector3>(_positions);
        _positions.Clear();
        _velocities.Clear();
        _depths.Clear();
        _heights.Clear();
        // The depth of the time: the oldest date at the back, the newest in front. An entity with a start date is at it.
        var today = DateTimeOffset.Now;
        var dates = _vm.Nodes.ToDictionary(n => n, n => Local(n.StartDate) ?? DateOf(n));
        var periods = _vm.Nodes.Where(n => n.StartDate is not null || n.EndDate is not null).ToList();
        var bounds = dates.Values
            .Concat(periods.Select(n => Local(n.EndDate) ?? today))
            .ToList();
        _oldest = bounds.Count == 0 ? today : bounds.Min();
        _newest = bounds.Count == 0 ? today : bounds.Max();
        // An end date without a start: the arrow comes from 10% before the oldest start date (else the oldest date),
        // its start hidden in a fade; the time axis goes back to there.
        var oldestStart = periods.Select(n => Local(n.StartDate)).Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty(_oldest).Min();
        var hiddenStart = oldestStart - TimeSpan.FromSeconds(Math.Max((_newest - _oldest).TotalSeconds, 86400) * 0.1);
        if (periods.Any(n => n.StartDate is null) && hiddenStart < _oldest)
            _oldest = hiddenStart;
        // The dates chosen on the double slider (kept within the graph): the elements whose date, or period, meets them.
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
            // Across, the place of the 2D graph (an element kept from before stays where it was); in height, its
            // probability; in depth, its date.
            _positions[node] = keep.TryGetValue(node, out var kept)
                ? kept with { Y = _heights[node], Z = _depths[node] }
                : new Vector3((float)node.X * Unit, _heights[node], _depths[node]);
            _velocities[node] = Vector3.Zero;
            var color = ColorOf(node);
            var material = new StandardMaterial3D
            {
                AlbedoColor = color,
                Metallic = 0.25f,
                Roughness = 0.4f,
                EmissionEnabled = true,
                Emission = color,
                EmissionEnergyMultiplier = 0.15f
            };
            var cube = new MeshInstance3D
            {
                // A body for a person, a CD-ROM for a software, a PCI card for a hardware, else a sphere.
                Mesh = EntityShapes.For(node.Type == RelationGraphViewModel.EntityType ? node.EntityType : null, CubeSize),
                MaterialOverride = material
            };
            _scene.AddChild(cube);
            var label = new Label3D
            {
                Text = Title(node),
                FontSize = 56,
                PixelSize = 0.02f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = Eve.Eve.IsLight ? new Color(0.1f, 0.12f, 0.14f) : new Color(0.92f, 0.95f, 0.97f),
                OutlineModulate = Eve.Eve.IsLight ? new Color(1, 1, 1, 0.9f) : new Color(0, 0, 0, 0.85f),
                OutlineSize = 10,
                Font = Eve.Eve.Body,
                NoDepthTest = false,
                Width = 900,
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            _scene.AddChild(label);
            // Under the name: its entity type (else its window), in the colour of the sphere.
            if (KindOf(node) is { } kind)
                label.AddChild(new Label3D
                {
                    Text = kind,
                    FontSize = 40,
                    PixelSize = 0.02f,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    Modulate = color.Lightened(Eve.Eve.IsLight ? 0 : 0.2f),
                    OutlineModulate = Eve.Eve.IsLight ? new Color(1, 1, 1, 0.9f) : new Color(0, 0, 0, 0.85f),
                    OutlineSize = 8,
                    Font = Eve.Eve.Display,
                    Position = new Vector3(0, -0.85f, 0)
                });
            _cubes[node] = (cube, label, material);
            if (node.StartDate is not null || node.EndDate is not null)
                _arrows[node] = Arrow(node, color, hiddenStart, today);
        }
        foreach (var edge in _vm.Edges.Where(e => !ReferenceEquals(e.From, e.To) && _positions.ContainsKey(e.From) && _positions.ContainsKey(e.To)))
        {
            var label = new Label3D
            {
                Text = edge.Label,
                FontSize = 40,
                PixelSize = 0.016f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = RelationGraphCanvas.ColorOf(edge.Relation.Current.Probability).Lightened(Eve.Eve.IsLight ? 0 : 0.25f) with { A = edge.MatchesFilters ? 1 : 0.15f },
                OutlineModulate = Eve.Eve.IsLight ? new Color(1, 1, 1, 0.9f) : new Color(0, 0, 0, 0.85f),
                OutlineSize = 8,
                Font = Eve.Eve.Body
            };
            _scene.AddChild(label);
            _edgeLabels.Add((edge, label));
        }
        UpdateSelection();
        // Laid out at once: nothing moves afterwards until the user does.
        Step(400);
        BuildClusters();
        BuildTimeline();
        if (fit)
            Fit();
        Place();
        TimeBoundsChanged?.Invoke(fullOldest, fullNewest, _oldest, _newest);
    }

    /// <summary>
    /// The date of an element: the date of its oldest relation (its first version); an element without one (an entity
    /// just added) is the most recent.
    /// </summary>
    private static DateTimeOffset DateOf(RelationGraphNode node) =>
        node.Relations
            .Select(r => r.Versions.FirstOrDefault()?.Date ?? r.Date)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .DefaultIfEmpty(DateTimeOffset.Now)
            .Min();

    /// <summary>The depth of <paramref name="date"/> on the time axis: the oldest date at the back, the newest at 0.</summary>
    private float DepthOf(DateTimeOffset date)
    {
        var span = (_newest - _oldest).TotalSeconds;
        var t = span <= 0 ? 1 : (float)Math.Clamp((date - _oldest).TotalSeconds / span, 0, 1);
        return -(1 - t) * _depthSpan;
    }

    /// <summary>A date of the ontology (no time zone: a day of the calendar here) as a local date.</summary>
    private static DateTimeOffset? Local(DateTime? date) =>
        date is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d.Date, DateTimeKind.Local)) : null;

    /// <summary>
    /// The 3D arrow of the period of an entity, along the time (its node is placed at the sphere, across, by
    /// <see cref="Place"/>): a shaft from the start date to the end date (the day the graph is built without an end) and
    /// a head at the end; without a start, the shaft comes from <paramref name="hiddenStart"/>, fading out towards it.
    /// The dates are written at its ends.
    /// </summary>
    private Node3D Arrow(RelationGraphNode node, Color color, DateTimeOffset hiddenStart, DateTimeOffset today)
    {
        var root = new Node3D();
        _scene.AddChild(root);
        var start = Local(node.StartDate);
        var end = Local(node.EndDate);
        var zStart = DepthOf(start ?? hiddenStart);
        var zEnd = DepthOf(end ?? today);
        if (zEnd < zStart)
            (zStart, zEnd) = (zEnd, zStart);
        // Cut by the dates shown: a start before them has no disc, an end after them no head (the arrow goes on).
        var startShown = start is { } first && first >= _oldest;
        var endShown = (end ?? today) <= _newest;
        const float head = 2.2f;
        var shaftEnd = endShown ? Math.Max(zStart, zEnd - head) : zEnd;
        var tint = color.Lightened(Eve.Eve.IsLight ? 0 : 0.15f);
        StandardMaterial3D Material(float alpha) => new()
        {
            AlbedoColor = tint with { A = alpha },
            Transparency = alpha < 1 ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
            Metallic = 0.2f,
            Roughness = 0.45f,
            EmissionEnabled = true,
            Emission = tint,
            EmissionEnergyMultiplier = 0.25f * alpha
        };
        // The cylinders and the cone are along Y: turned a quarter about X, along Z (towards the newest dates).
        void Piece(float from, float to, float alpha, float bottomRadius, float topRadius)
        {
            if (to - from < 0.01f)
                return;
            root.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { Height = to - from, BottomRadius = bottomRadius, TopRadius = topRadius, RadialSegments = 16 },
                MaterialOverride = Material(alpha),
                Position = new Vector3(0, 0, (from + to) / 2),
                RotationDegrees = new Vector3(90, 0, 0)
            });
        }
        const float radius = 0.32f;
        if (start is null)
        {
            // No start: the first 40% of the shaft fade into the past.
            const int fades = 10;
            var fadeEnd = zStart + (shaftEnd - zStart) * 0.4f;
            for (var i = 0; i < fades; i++)
            {
                var a = zStart + (fadeEnd - zStart) * i / fades;
                var b = zStart + (fadeEnd - zStart) * (i + 1) / fades;
                Piece(a, b, (i + 1f) / (fades + 1), radius, radius);
            }
            Piece(fadeEnd, shaftEnd, 1, radius, radius);
        }
        else
        {
            Piece(zStart, shaftEnd, 1, radius, radius);
            // The start: a disc at the date.
            if (startShown)
                Piece(zStart - 0.08f, zStart + 0.08f, 1, radius * 2.6f, radius * 2.6f);
        }
        if (endShown)
            Piece(zEnd - head, zEnd, 1, 0.85f, 0);
        var format = "yyyy-MM-dd";
        if (startShown)
            root.AddChild(ArrowLabel(start!.Value.ToString(format), new Vector3(0, -1.3f, zStart)));
        if (endShown)
            root.AddChild(ArrowLabel(end is { } e ? e.ToString(format) : "today", new Vector3(0, -1.3f, zEnd)));
        return root;
    }

    private static Label3D ArrowLabel(string text, Vector3 position) => new()
    {
        Text = text,
        FontSize = 40,
        PixelSize = 0.018f,
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        Modulate = Eve.Eve.IsLight ? new Color(0.15f, 0.18f, 0.22f) : new Color(0.85f, 0.9f, 0.95f),
        OutlineModulate = Eve.Eve.IsLight ? new Color(1, 1, 1, 0.9f) : new Color(0, 0, 0, 0.85f),
        OutlineSize = 8,
        Font = Eve.Eve.Body,
        Position = position
    };

    /// <summary>The probability of an element, from -100 to +100: the mean of those of its relations (0 without any).</summary>
    private static double ProbabilityOf(RelationGraphNode node) =>
        node.Relations.Count == 0 ? 0 : Math.Clamp(node.Relations.Average(r => r.Current.Probability), -100, 100);

    /// <summary>The height of a probability: the higher the probability, the lower.</summary>
    private float HeightOf(double probability) => -(float)probability * HeightUnit * _heightScale;

    /// <summary>The labels of the timeline under the graph: a date at each of its ticks, a caption at its ends.</summary>
    private void BuildTimeline()
    {
        // Under the lowest place (a probability of +100), so that it stays where it is whatever the elements.
        _timelineY = HeightOf(100) - CubeSize * 3;
        var span = _newest - _oldest;
        var format = span.TotalDays >= 2 ? "yyyy-MM-dd" : span.TotalHours >= 2 ? "dd/MM HH:mm" : "HH:mm:ss";
        var ticks = span.TotalSeconds <= 0 ? 1 : 6;
        for (var i = 0; i < ticks; i++)
        {
            var t = ticks == 1 ? 1 : i / (float)(ticks - 1);
            var date = _oldest + TimeSpan.FromSeconds(span.TotalSeconds * t);
            _timelineLabels.Add(TimelineLabel((i == 0 && ticks > 1 ? (_rangeFrom is null ? "oldest · " : "from · ") : i == ticks - 1 ? (_rangeTo is null ? "newest · " : "to · ") : "") + date.ToLocalTime().ToString(format),
                new Vector3(TimelineX, _timelineY - 1.6f, -(1 - t) * _depthSpan)));
        }
        _timelineLabels.Add(TimelineLabel("TIME →", new Vector3(TimelineX, _timelineY + 1.2f, 4)));
        // The scale of the probability, upright at the front of the timeline.
        foreach (var probability in new[] { 100, 50, 0, -50, -100 })
            _timelineLabels.Add(TimelineLabel(probability > 0 ? $"+{probability}" : $"{probability}",
                new Vector3(TimelineX - 2.6f, HeightOf(probability), 4)));
        _timelineLabels.Add(TimelineLabel("PROBABILITY", new Vector3(TimelineX, HeightOf(-100) + 2, 4)));
    }

    private Label3D TimelineLabel(string text, Vector3 position)
    {
        var label = new Label3D
        {
            Text = text,
            FontSize = 52,
            PixelSize = 0.034f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = Eve.Eve.Amber,
            OutlineModulate = Eve.Eve.IsLight ? new Color(1, 1, 1, 0.9f) : new Color(0, 0, 0, 0.85f),
            OutlineSize = 8,
            Font = Eve.Eve.Display,
            Position = position
        };
        _scene.AddChild(label);
        return label;
    }

    /// <summary>The timeline runs in depth under the middle of the graph.</summary>
    private float TimelineX => _positions.Count == 0 ? 0 : (_positions.Values.Min(p => p.X) + _positions.Values.Max(p => p.X)) / 2;

    private static string Title(RelationGraphNode node) => node.Title.Length > 48 ? node.Title[..48] + "…" : node.Title;

    /// <summary>What an element is, under its name: the entity type of an entity of the ontology, else its window.</summary>
    private static string? KindOf(RelationGraphNode node) =>
        node.Type == RelationGraphViewModel.EntityType ? node.EntityType?.ToUpperInvariant() : node.Type.ToUpperInvariant();

    /// <summary>The colours of the entity types of the ontology (the NER types of config.yml); another type gets one of the palette.</summary>
    private static readonly Dictionary<string, Color> EntityColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PERSON"] = new("#F59E0B"), ["PER"] = new("#F59E0B"),
        ["ORG"] = new("#3B82F6"), ["ORGANISATION"] = new("#3B82F6"), ["ORGANIZATION"] = new("#3B82F6"),
        ["GPE"] = new("#10B981"), ["LOC"] = new("#22C55E"), ["LOCATION"] = new("#22C55E"), ["FAC"] = new("#14B8A6"),
        ["NORP"] = new("#A855F7"),
        ["PRODUCT"] = new("#EC4899"), ["HARDWARE"] = new("#F97316"), ["SOFTWARE"] = new("#06B6D4"), ["AI"] = new("#8B5CF6"),
        ["EVENT"] = new("#EF4444"), ["WORK_OF_ART"] = new("#D946EF"), ["LAW"] = new("#64748B"), ["LANGUAGE"] = new("#0EA5E9"),
        ["DATE"] = new("#EAB308"), ["TIME"] = new("#FACC15"),
        ["PERCENT"] = new("#84CC16"), ["MONEY"] = new("#16A34A"), ["QUANTITY"] = new("#65A30D"), ["ORDINAL"] = new("#A3A3A3"), ["CARDINAL"] = new("#9CA3AF"),
        ["FOOD"] = new("#FB7185"), ["MATHEMATIC"] = new("#6366F1"), ["MATERIAL"] = new("#B45309"),
        ["MISC"] = new("#94A3B8"), ["OTHERS"] = new("#94A3B8"), ["CLASS"] = new("#475569")
    };

    /// <summary>
    /// The colour of an element: for an entity of the ontology the colour of its entity type (grey without one), for
    /// another element the colour of its window, as in the 2D graph.
    /// </summary>
    internal static Color ColorOf(RelationGraphNode node)
    {
        if (node.Type != RelationGraphViewModel.EntityType)
            return RelationGraphCanvas.ColorOf(node.Type);
        if (string.IsNullOrWhiteSpace(node.EntityType))
            return new Color("#8A939E");
        return EntityColors.TryGetValue(node.EntityType.Trim(), out var color) ? color : RelationGraphCanvas.ColorOf("entity:" + node.EntityType.Trim().ToUpperInvariant());
    }

    private void UpdateTitles()
    {
        foreach (var (node, (_, label, _)) in _cubes)
            label.Text = Title(node);
        // "Show clusters" toggled, or the clusters loaded: the blobs follow.
        BuildClusters();
    }

    // ── The clusters: translucent coloured blobs ──

    /// <summary>The radius of the blob around one element alone.</summary>
    private const float BlobRadius = CubeSize * 1.9f;
    /// <summary>The field is 1 on the spokes and 0.5 at <see cref="BlobRadius"/> from them: the surface.</summary>
    private const float BlobThreshold = 0.5f;

    /// <summary>
    /// A blob per cluster whose elements are in the graph: a metaball (a Gaussian field around each spoke from the
    /// centre of the elements to each of them, summed, cut at <see cref="BlobThreshold"/>), sampled along the rays
    /// from the centre, in the colour of the cluster and translucent, its name above it.
    /// </summary>
    private void BuildClusters()
    {
        foreach (var (_, blob, label, _, _, _) in _blobs)
        {
            blob.QueueFree();
            label.QueueFree();
        }
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
            var blob = new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = color with { A = Eve.Eve.IsLight ? 0.22f : 0.18f },
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    CullMode = BaseMaterial3D.CullModeEnum.Back,
                    Metallic = 0.1f,
                    Roughness = 0.25f,
                    EmissionEnabled = true,
                    Emission = color,
                    EmissionEnergyMultiplier = Eve.Eve.IsLight ? 0.05f : 0.35f,
                    // The rim: lighter where the blob is seen at a grazing angle, like glass.
                    RimEnabled = true,
                    Rim = 0.6f,
                    RimTint = 0.3f
                },
                // Drawn after the opaque spheres and lines (they show through the blob).
                SortingOffset = -1
            };
            _scene.AddChild(blob);
            var label = new Label3D
            {
                Text = cluster.Name + (cluster.Probability < 100 ? $" · {cluster.Probability} %" : string.Empty),
                FontSize = 64,
                PixelSize = 0.024f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = color.Lightened(Eve.Eve.IsLight ? -0.25f : 0.25f),
                OutlineModulate = Eve.Eve.IsLight ? new Color(1, 1, 1, 0.9f) : new Color(0, 0, 0, 0.85f),
                OutlineSize = 10,
                Font = Eve.Eve.Display,
                Position = top + new Vector3(0, 1.4f, 0)
            };
            _scene.AddChild(label);
            _blobs.Add((cluster, blob, label, centre, members, sigma));
        }
    }

    /// <summary>The field of a blob at <paramref name="p"/>: the sum of a Gaussian of the distance to each spoke.</summary>
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
            return p.DistanceTo(a);
        var t = Math.Clamp((p - a).Dot(ab) / length2, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>
    /// The mesh of a blob: a sphere of rings and segments around <paramref name="centre"/>, each vertex pushed along
    /// its ray to the last point where the field is at least the threshold (found by marching, then by bisection),
    /// its normal the gradient of the field; <paramref name="top"/> is its highest point (for the name).
    /// </summary>
    private static ArrayMesh BlobMesh(Vector3 centre, List<Vector3> members, float sigma, out Vector3 top)
    {
        const int rings = 40, segments = 64;
        var reach = members.Max(m => m.DistanceTo(centre)) + BlobRadius * 2.5f;
        float Field(Vector3 p) => BlobField(p, centre, members, sigma);
        Vector3 Surface(Vector3 direction)
        {
            // The last point along the ray where the field is above the threshold.
            var step = Math.Max(0.5f, reach / 120);
            var last = 0f;
            for (var t = 0f; t <= reach; t += step)
                if (Field(centre + direction * t) >= BlobThreshold)
                    last = t;
            var low = last;
            var high = last + step;
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
            return gradient.LengthSquared() < 1e-12f ? Vector3.Up : (-gradient).Normalized();
        }
        var vertices = new Vector3[(rings + 1) * (segments + 1)];
        var normals = new Vector3[vertices.Length];
        top = centre;
        for (var i = 0; i <= rings; i++)
        {
            var theta = MathF.PI * i / rings;
            for (var j = 0; j <= segments; j++)
            {
                var phi = 2 * MathF.PI * j / segments;
                var direction = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
                var p = Surface(direction);
                var k = i * (segments + 1) + j;
                vertices[k] = p;
                normals[k] = Normal(p);
                if (p.Y > top.Y)
                    top = p;
            }
        }
        var indices = new List<int>();
        for (var i = 0; i < rings; i++)
            for (var j = 0; j < segments; j++)
            {
                var a = i * (segments + 1) + j;
                var b = a + segments + 1;
                // Clockwise seen from outside (Godot's front faces).
                indices.AddRange([a, b, a + 1, a + 1, b, b + 1]);
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>The cluster whose blob is under <paramref name="point"/>: the ray from the camera crosses its field.</summary>
    private KAONOMI.Chat.Models.ClusterEntry? ClusterAt(Vector2 point)
    {
        if (_blobs.Count == 0)
            return null;
        var origin = _camera.ProjectRayOrigin(point);
        var direction = _camera.ProjectRayNormal(point);
        KAONOMI.Chat.Models.ClusterEntry? best = null;
        var bestDepth = float.MaxValue;
        foreach (var (cluster, _, _, centre, members, sigma) in _blobs)
        {
            var reach = members.Max(m => m.DistanceTo(centre)) + BlobRadius * 2.5f;
            var near = Math.Max(0, origin.DistanceTo(centre) - reach);
            var far = origin.DistanceTo(centre) + reach;
            for (var t = near; t <= far; t += 0.6f)
            {
                if (BlobField(origin + direction * t, centre, members, sigma) < BlobThreshold)
                    continue;
                if (t < bestDepth)
                {
                    best = cluster;
                    bestDepth = t;
                }
                break;
            }
        }
        return best;
    }

    private void UpdateSelection()
    {
        foreach (var (node, (cube, _, material)) in _cubes)
        {
            var selected = ReferenceEquals(node, _vm.SelectedNode);
            var hovered = ReferenceEquals(node, _hovered);
            // The neighbours of the selected element glow (1st degree more than 2nd), the others dim.
            var distance = _vm.DistanceOf(node);
            material.EmissionEnergyMultiplier = selected ? 1.4f : hovered ? 0.6f
                : distance == 1 ? 0.9f : distance == 2 ? 0.5f : _vm.HasProximity ? 0.03f : 0.15f;
            cube.Scale = Vector3.One * (selected ? 1.35f : hovered ? 1.15f : distance == 1 ? 1.2f : distance == 2 ? 1.1f : 1f);
        }
    }

    // ── Layout by forces, in 3D ──

    private void Step(int steps)
    {
        var nodes = _vm.Nodes;
        const float repulsion = 900;
        const float springLength = 16;
        const float springStrength = 0.04f;
        const float gravity = 0.006f;
        const float damping = 0.82f;
        for (var step = 0; step < steps; step++)
        {
            foreach (var a in nodes)
            {
                if (!_positions.TryGetValue(a, out var pa))
                    continue;
                var force = -pa * gravity;
                foreach (var b in nodes)
                {
                    if (ReferenceEquals(a, b) || !_positions.TryGetValue(b, out var pb))
                        continue;
                    var d = pa - pb;
                    var distance2 = Math.Max(d.LengthSquared(), 1f);
                    force += d.Normalized() * (repulsion / distance2);
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
            // Across only: the height is the probability, the depth the date.
            foreach (var node in nodes)
                if (_positions.TryGetValue(node, out var p))
                    _positions[node] = (p + _velocities[node].LimitLength(4)) with { Y = _heights[node], Z = _depths[node] };
        }
    }

    /// <summary>Zooms by <paramref name="scale"/> (above 1: closer): the camera comes nearer to the point it looks at.</summary>
    public void ZoomBy(float scale)
    {
        _distance = Math.Clamp(_distance / scale, 6, 2000);
        PlaceCamera();
    }

    /// <summary>
    /// The camera, seen from where it is turned, aimed at the middle of the graph and at the distance where all of it
    /// shows: its elements with their names, the arrows of the periods and the timeline, in the width as in the height
    /// of the view.
    /// </summary>
    public void Fit()
    {
        if (_positions.Count == 0)
        {
            _target = Vector3.Zero;
            _distance = 40;
            PlaceCamera();
            return;
        }
        // The points to show: each element with a margin (its name above, its type and arrow under it), and the ends of
        // the timeline (which run along the arrows of the periods).
        var points = new List<Vector3>();
        foreach (var p in _positions.Values)
        {
            points.Add(p + new Vector3(-CubeSize, CubeSize * 1.8f, 0));
            points.Add(p + new Vector3(CubeSize, -CubeSize * 1.4f, 0));
        }
        points.Add(new Vector3(TimelineX, _timelineY - 2.5f, -_depthSpan - 4));
        points.Add(new Vector3(TimelineX, _timelineY - 2.5f, 4));
        points.Add(new Vector3(TimelineX - 3, HeightOf(-100) + 3, 4));
        var low = points.Aggregate((a, b) => a.Min(b));
        var high = points.Aggregate((a, b) => a.Max(b));
        _target = (low + high) / 2;
        // In the axes of the camera (right, up, towards it): each point must be inside the field of view.
        _pitch = Math.Clamp(_pitch, -1.45f, 1.45f);
        var back = new Vector3(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));
        var right = Vector3.Up.Cross(back).Normalized();
        var up = back.Cross(right);
        var tanV = MathF.Tan(Mathf.DegToRad(_camera.Fov / 2));
        var aspect = Size.X > 1 && Size.Y > 1 ? Size.X / Size.Y : 1.6f;
        var tanH = tanV * aspect;
        var distance = 12f;
        foreach (var p in points)
        {
            var d = p - _target;
            var z = d.Dot(back);
            distance = Math.Max(distance, z + Math.Max(MathF.Abs(d.Dot(right)) / tanH, MathF.Abs(d.Dot(up)) / tanV));
        }
        _distance = Math.Clamp(distance * 1.05f, 12, 2000);
        PlaceCamera();
    }

    /// <summary>The points of the graph and of its timeline.</summary>
    private List<Vector3> Points() =>
        _positions.Values.Append(new Vector3(TimelineX, _timelineY, 0)).Append(new Vector3(TimelineX, _timelineY, -_depthSpan)).ToList();

    /// <summary>The centre of the graph and its timeline (the origin when it is empty).</summary>
    private Vector3 Centre()
    {
        if (_positions.Count == 0)
            return Vector3.Zero;
        var points = Points();
        return points.Aggregate(Vector3.Zero, (s, p) => s + p) / points.Count;
    }

    // ── Each frame ──

    public override void _Process(double delta)
    {
        if (_fly || HasFocus())
            Move((float)delta);
        PlaceCamera();
        Place();
    }

    /// <summary>
    /// The keys move the camera along where it looks: Z / W or ↑ forward, S or ↓ back, Q / A or ← left, D or → right
    /// (by their place on the keyboard, AZERTY or QWERTY), Space up, C down, Shift three times faster. The speed follows
    /// the size of the scene seen (the distance of the camera to its target).
    /// </summary>
    private void Move(float delta)
    {
        static bool Down(Key physical, Key other = Key.None) => Input.IsPhysicalKeyPressed(physical) || (other != Key.None && Input.IsKeyPressed(other));
        var forward = -_camera.GlobalBasis.Z;
        var right = _camera.GlobalBasis.X;
        var direction = Vector3.Zero;
        if (Down(Key.W, Key.Up))
            direction += forward;
        if (Down(Key.S, Key.Down))
            direction -= forward;
        if (Down(Key.D, Key.Right))
            direction += right;
        if (Down(Key.A, Key.Left))
            direction -= right;
        if (Down(Key.Space))
            direction += Vector3.Up;
        if (Down(Key.C))
            direction -= Vector3.Up;
        if (direction == Vector3.Zero)
            return;
        var modifier = Input.IsKeyPressed(Key.Shift) ? 2.5f : Input.IsKeyPressed(Key.Alt) ? 0.3f : 1f;
        var speed = Math.Clamp(_distance * 0.25f, 3, 80) * _speedFactor * modifier;
        _target += direction.Normalized() * speed * delta;
    }

    /// <summary>The speed of the keys chosen with the wheel in FPS mode (×0.1 to ×5).</summary>
    private float _speedFactor = 1;

    /// <summary>The wheel in FPS mode: the speed of the keys, faster or slower.</summary>
    private void ChangeSpeed(bool faster)
    {
        _speedFactor = Math.Clamp(_speedFactor * (faster ? 1.25f : 0.8f), 0.1f, 5f);
        _vm.ReportStatus($"FPS: speed ×{_speedFactor:0.0#} (wheel: faster / slower · Shift: ×2.5 · Alt: ×0.3)", false);
    }

    /// <summary>The direction from the target to the camera (unit).</summary>
    private Vector3 Offset() => new(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));

    /// <summary>Radians turned per pixel of mouse in FPS mode.</summary>
    private const float MouseSensitivity = 0.002f;

    /// <summary>Turns the camera where it stands (FPS): the mouse to the right turns right, up looks up.</summary>
    private void Look(Vector2 relative)
    {
        var position = _target + Offset() * _distance;
        _yaw -= relative.X * MouseSensitivity;
        _pitch = Math.Clamp(_pitch + relative.Y * MouseSensitivity, -1.45f, 1.45f);
        _target = position - Offset() * _distance;
    }

    /// <summary>Starts or ends the FPS mode: the mouse captured (it aims the camera) or given back.</summary>
    public void SetFly(bool on)
    {
        if (on == _fly)
            return;
        _fly = on;
        Input.MouseMode = on ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        _crosshair.Visible = _flyHint.Visible = on;
        if (on)
            GrabFocus();
        _vm.ReportStatus(on ? "FPS: the mouse aims, Z Q S D move · F or Escape gives the mouse back" : "FPS off: the mouse is back", false);
        FlyChanged?.Invoke(on);
    }

    public override void _Input(InputEvent e)
    {
        // While flying, the mouse aims and clicks select under the cross, whatever control the pointer was over.
        if (!_fly)
            return;
        switch (e)
        {
            case InputEventMouseMotion motion:
                Look(motion.Relative);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                _vm.SelectedNode = NodeAt(Size / 2);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel:
                ChangeSpeed(wheel.ButtonIndex == MouseButton.WheelUp);
                break;
            case InputEventKey { Pressed: true, Echo: false } key when key.Keycode == Key.Escape || key.PhysicalKeycode == Key.F:
                SetFly(false);
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        // The mouse is never left captured: the view hidden or closed, the chat in the background.
        long notification = what;
        var leave = notification == NotificationExitTree || notification == NotificationWMWindowFocusOut || notification == NotificationApplicationFocusOut
                    || (notification == NotificationVisibilityChanged && !IsVisibleInTree());
        if (_fly && leave)
            SetFly(false);
    }

    private void PlaceCamera()
    {
        _pitch = Math.Clamp(_pitch, -1.45f, 1.45f);
        var offset = new Vector3(MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw)) * _distance;
        // Out of the tree (the view being built), only the place: it looks at the target at the next frame.
        if (_camera.IsInsideTree())
            _camera.LookAtFromPosition(_target + offset, _target, Vector3.Up);
        else
            _camera.Position = _target + offset;
    }

    /// <summary>The spheres, labels and lines at the places of the layout.</summary>
    private void Place()
    {
        foreach (var (node, (cube, label, _)) in _cubes)
        {
            var p = _positions[node];
            cube.Position = p;
            label.Position = p + new Vector3(0, CubeSize * 1.3f, 0);
            // The arrow of its period runs in depth just under the sphere, across.
            if (_arrows.TryGetValue(node, out var arrow))
                arrow.Position = new Vector3(p.X, p.Y - CubeSize * 0.95f, 0);
        }
        _lineMesh.ClearSurfaces();
        var drawn = false;
        foreach (var edge in _vm.Edges)
        {
            if (ReferenceEquals(edge.From, edge.To) || !_positions.TryGetValue(edge.From, out var from) || !_positions.TryGetValue(edge.To, out var to))
                continue;
            if (!drawn)
            {
                _lineMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                drawn = true;
            }
            var color = RelationGraphCanvas.ColorOf(edge.Relation.Current.Probability) with { A = edge.MatchesFilters ? 0.95f : 0.12f };
            var direction = (to - from).Normalized();
            var start = from + direction * CubeSize * 0.8f;
            var end = to - direction * CubeSize * 0.9f;
            Line(start, end, color);
            // The arrowhead: four short lines from the end, back along the link.
            var side = direction.Cross(Mathf.Abs(direction.Y) > 0.9f ? Vector3.Right : Vector3.Up).Normalized();
            var up = side.Cross(direction).Normalized();
            var back = end - direction * 1.2f;
            foreach (var v in new[] { side, -side, up, -up })
                Line(end, back + v * 0.5f, color);
        }
        // The timeline under the graph, its ticks, and a thin line from each sphere down to its date.
        if (_positions.Count > 0)
        {
            if (!drawn)
            {
                _lineMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
                drawn = true;
            }
            var x = TimelineX;
            var amber = Eve.Eve.Amber;
            Line(new Vector3(x, _timelineY, -_depthSpan - 4), new Vector3(x, _timelineY, 4), amber);
            Line(new Vector3(x, _timelineY, 4), new Vector3(x - 0.7f, _timelineY, 2.6f), amber);
            Line(new Vector3(x, _timelineY, 4), new Vector3(x + 0.7f, _timelineY, 2.6f), amber);
            for (var i = 0; i < 6; i++)
            {
                var z = -_depthSpan * (1 - i / 5f);
                Line(new Vector3(x - 0.8f, _timelineY, z), new Vector3(x + 0.8f, _timelineY, z), amber);
            }
            // The scale of the probability: +100 at the bottom, -100 at the top.
            Line(new Vector3(x, HeightOf(100), 4), new Vector3(x, HeightOf(-100), 4), amber);
            for (var probability = -100; probability <= 100; probability += 50)
                Line(new Vector3(x - 0.8f, HeightOf(probability), 4), new Vector3(x + 0.8f, HeightOf(probability), 4), amber);
            var drop = (Eve.Eve.IsLight ? new Color(0.35f, 0.4f, 0.45f) : new Color(0.6f, 0.68f, 0.75f)) with { A = 0.28f };
            foreach (var (node, p) in _positions)
            {
                Line(p - new Vector3(0, CubeSize * 0.6f, 0), new Vector3(p.X, _timelineY, p.Z), drop);
                Line(new Vector3(p.X, _timelineY, p.Z), new Vector3(x, _timelineY, p.Z), drop);
            }
        }
        if (drawn)
            _lineMesh.SurfaceEnd();
        foreach (var (edge, label) in _edgeLabels)
            if (_positions.TryGetValue(edge.From, out var from) && _positions.TryGetValue(edge.To, out var to))
                label.Position = (from + to) / 2 + new Vector3(0, 0.6f, 0);
    }

    private void Line(Vector3 a, Vector3 b, Color color)
    {
        _lineMesh.SurfaceSetColor(color);
        _lineMesh.SurfaceAddVertex(a);
        _lineMesh.SurfaceSetColor(color);
        _lineMesh.SurfaceAddVertex(b);
    }

    // ── The pointer ──

    /// <summary>The element whose sphere is under <paramref name="point"/>, the nearest to the camera first.</summary>
    private RelationGraphNode? NodeAt(Vector2 point)
    {
        RelationGraphNode? best = null;
        var bestDepth = float.MaxValue;
        foreach (var (node, position) in _positions)
        {
            if (_camera.IsPositionBehind(position))
                continue;
            var screen = _camera.UnprojectPosition(position);
            var depth = _camera.GlobalPosition.DistanceTo(position);
            // The size of the sphere on the screen.
            var edge = _camera.UnprojectPosition(position + _camera.GlobalBasis.X * CubeSize * 0.75f);
            var radius = Math.Max(8, screen.DistanceTo(edge));
            if (screen.DistanceTo(point) <= radius && depth < bestDepth)
            {
                best = node;
                bestDepth = depth;
            }
        }
        return best;
    }

    /// <summary>The link whose title is under <paramref name="point"/>.</summary>
    private RelationGraphEdge? EdgeAt(Vector2 point)
    {
        foreach (var (edge, label) in _edgeLabels)
        {
            if (_camera.IsPositionBehind(label.Position))
                continue;
            if (_camera.UnprojectPosition(label.Position).DistanceTo(point) < 26)
                return edge;
        }
        return null;
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            // F: the FPS mode (the mouse aims the camera); the keys that move the camera go no further.
            case InputEventKey { Pressed: true, Echo: false } key when key.PhysicalKeycode == Key.F && !key.CtrlPressed && !key.MetaPressed:
                SetFly(!_fly);
                AcceptEvent();
                break;
            case InputEventKey key when !key.CtrlPressed && !key.MetaPressed && !key.AltPressed
                                        && (key.PhysicalKeycode is Key.W or Key.A or Key.S or Key.D or Key.Space or Key.C
                                            || key.Keycode is Key.Up or Key.Down or Key.Left or Key.Right):
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel:
            {
                var factor = wheel.Factor > 0 ? wheel.Factor : 1;
                ZoomBy(wheel.ButtonIndex == MouseButton.WheelUp ? Mathf.Pow(1.12f, factor) : 1 / Mathf.Pow(1.12f, factor));
                AcceptEvent();
                break;
            }
            // The trackpad: a pinch, or two fingers up and down, zoom too.
            case InputEventMagnifyGesture magnify:
                ZoomBy(magnify.Factor);
                AcceptEvent();
                break;
            case InputEventPanGesture pan:
                ZoomBy(Mathf.Pow(1.06f, -pan.Delta.Y));
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true, Pressed: true } click:
                if (NodeAt(click.Position) is { } opened)
                    NodeDoubleClicked?.Invoke(opened);
                else if (EdgeAt(click.Position) is { } edge)
                    EdgeDoubleClicked?.Invoke(edge);
                else if (ClusterAt(click.Position) is { } cluster)
                    ClusterDoubleClicked?.Invoke(cluster);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle } button:
                if (button.Pressed)
                {
                    _moved = false;
                    _panning = button.ButtonIndex != MouseButton.Left || button.ShiftPressed;
                    _orbiting = !_panning;
                }
                else
                {
                    // A click (no drag) selects the element under the pointer (or nothing).
                    if (!_moved && button.ButtonIndex == MouseButton.Left)
                        _vm.SelectedNode = NodeAt(button.Position);
                    // A right click (no drag) on an entity (or an image analysis) offers to annotate it.
                    else if (!_moved && button.ButtonIndex == MouseButton.Right && NodeAt(button.Position) is { } clicked
                             && Data.Annotations.OfGraphNode(clicked) is { } target)
                        Data.Annotations.ShowMenu(this, button.Position, target);
                    _orbiting = _panning = false;
                }
                AcceptEvent();
                break;
            case InputEventMouseMotion motion:
                if (_orbiting || _panning)
                {
                    _moved |= motion.Relative.Length() > 1;
                    if (_orbiting)
                    {
                        _yaw -= motion.Relative.X * 0.008f;
                        _pitch += motion.Relative.Y * 0.008f;
                    }
                    else
                    {
                        var scale = _distance * 0.0016f;
                        _target += (-_camera.GlobalBasis.X * motion.Relative.X + _camera.GlobalBasis.Y * motion.Relative.Y) * scale;
                    }
                    AcceptEvent();
                    break;
                }
                var hovered = NodeAt(motion.Position);
                if (!ReferenceEquals(hovered, _hovered))
                {
                    _hovered = hovered;
                    UpdateSelection();
                    MouseDefaultCursorShape = hovered is null ? CursorShape.Arrow : CursorShape.PointingHand;
                    TooltipText = hovered is null ? string.Empty : $"{hovered.Type} · {hovered.Title} · click: details · double-click: open";
                }
                if (hovered is null && _blobs.Count > 0)
                {
                    // Over the blob of a cluster: its name, a double-click opens it.
                    var cluster = ClusterAt(motion.Position);
                    MouseDefaultCursorShape = cluster is null ? CursorShape.Arrow : CursorShape.PointingHand;
                    TooltipText = cluster is null ? string.Empty : $"Cluster · {cluster.Name} · double-click: edit";
                }
                break;
        }
    }

    /// <summary>The picture of the 3D graph as it is shown.</summary>
    public Image? Capture() => _viewport.GetTexture()?.GetImage();
}
