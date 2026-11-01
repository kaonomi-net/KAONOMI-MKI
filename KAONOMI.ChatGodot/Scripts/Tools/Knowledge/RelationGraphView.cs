using System;
using System.IO;
using System.Linq;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// /relation (port of <c>RelationGraphView.axaml</c>): a search on the entities (the ones of the ontology the server finds,
/// the elements whose title holds the text; empty: the most linked elements; Enter or Search) and, at its right, the
/// largest number of elements shown (50 to 250), Fit, ↻ Refresh (applies the filters
/// and reads the titles of the elements again), 📷 PNG (the graph as shown, twice its size on screen); the filters of the
/// dates (From, To, each optional) and of the confidence (-100 to 100), the relations outside them drawn transparent;
/// the graph (see <see cref="RelationGraphCanvas"/>) with + (search an entity of the ontology and add it), and the
/// details of the selected element on the right. A double-click on an element opens its picture (image analysis
/// window) or its entity (Entity window), on a link its relation (the relation popup); the red string drawn between
/// the link icons of the cards opens the relation popup for a new link.
/// </summary>
[ToolView(typeof(RelationGraphViewModel), "Relations", "🕸", 1250, 820)]
public sealed class RelationGraphView : IToolView
{
    /// <summary>Default folder of the captures of the graph: the Pictures folder of the system, kaonomi/relations.</summary>
    public static string CaptureDirectory => Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "relations");

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (RelationGraphViewModel)viewModel;
        var graph = new RelationGraphCanvas(vm);

        // The search, Fit, Refresh, capture.
        var term = Ui.Line(vm, nameof(vm.Term), "Search the entities: words, and, or, not, ( ), \"exact phrase\" · e.g. hinton or (lecun and not prix) · empty: the most linked elements");
        term.CustomMinimumSize = new Vector2(220, 0);
        term.TextSubmitted += _ => Bind.Run(vm.SearchCommand);
        // Right of the search: the largest number of elements shown (a change searches again).
        var nodeCount = Bind.Options(new OptionButton { TooltipText = "The largest number of elements shown: the entities found first, then their neighbours, the most linked first" },
            vm.NodeCountChoices, vm, nameof(vm.MaxNodes));
        var nodeCountBox = Ui.Row(4, Eve.Eve.Caption("NODES"), nodeCount);
        // 3D: the same graph with a cube per element, created the first time (graph3D) and shown instead of the 2D one.
        RelationGraph3D? graph3D = null;
        // Timeline: the elements as horizontal ribbons on a grid of time, created the first time and shown instead of the 2D graph.
        RelationTimeline? timeline = null;
        Control? viewTimeline = null;
        // The 3D graph with its double slider of dates under it.
        Control? view3D = null;
        var mode3D = new Button { Text = "3D", ToggleMode = true, TooltipText = "The graph in 3D: a cube per element; drag: turn around · right drag: move · wheel: zoom · click again: back to 2D", MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        var modeTimeline = new Button { Text = "⏱ TIMELINE", ToggleMode = true, TooltipText = "The elements on a timeline: a ribbon per element from its start date to its end date, on a grid of time from the oldest start to the most recent end; the double slider under it chooses the dates shown · drag an end of a ribbon: the date of the entity · click again: back to 2D", MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        var capture = Ui.Action("📷 PNG", () =>
        {
            if (graph3D is { Visible: true } shown3D && view3D is { Visible: true })
                _ = Capture3DAsync(vm, shown3D);
            else if (timeline is { Visible: true } shownTimeline && viewTimeline is { Visible: true })
                _ = CaptureTimelineAsync(vm, shownTimeline);
            else
                _ = CaptureAsync(vm, graph);
        }, tooltip: "Save what is shown (the 2D graph, the 3D graph or the timeline) as a PNG picture (Pictures/kaonomi/relations by default)");
        // The pen: draw around some elements, then name the cluster they make.
        var pen = new Button { Text = "✎ PEN", ToggleMode = true, FocusMode = Control.FocusModeEnum.None, TooltipText = "Draw around some elements with the mouse (2D): the elements inside the drawing make a cluster, named in the popup that opens · right click or Escape: remove the drawing · click again: back to the hand" };
        pen.Toggled += on =>
        {
            graph.PenMode = on;
            if (!on)
                graph.ClearPen();
            graph.MouseDefaultCursorShape = on ? Control.CursorShape.Cross : Control.CursorShape.Arrow;
        };
        graph.ClusterDrawn += (members, path) =>
        {
            ClusterDialog.Open(shell, members, path, saved: () =>
            {
                graph.ClearPen();
                pen.ButtonPressed = false;
                vm.ShowClusters = true;
                _ = vm.LoadClustersAsync();
            }, closed: graph.ClearPen);
        };
        graph.ClusterDoubleClicked += cluster => ClusterDialog.Open(shell, cluster, saved: () => _ = vm.LoadClustersAsync());
        var showClusters = Ui.Check("Show clusters", vm, nameof(vm.ShowClusters), "The clusters stored on the server drawn around their elements (2D: the drawing of the pen, smoothed, or the hull of the elements when they moved out of it · 3D: a translucent coloured blob)");
        var toolbar = Ui.Toolbar(Ui.Row(6,
            term,
            nodeCountBox,
            Ui.Command("SEARCH", vm.SearchCommand, primary: true),
            pen,
            showClusters,
            Ui.Action("FIT", () =>
            {
                if (graph3D is { Visible: true } shown3D && view3D is { Visible: true })
                    shown3D.Fit();
                else
                    graph.Fit();
            }, tooltip: "Show the whole graph"),
            Ui.Action("+", () =>
            {
                if (graph3D is { Visible: true } shown3D && view3D is { Visible: true })
                    shown3D.ZoomBy(1.25f);
                else
                    graph.ZoomBy(1.25f);
            }, tooltip: "Zoom in (or the mouse wheel, a pinch of the trackpad)"),
            Ui.Action("−", () =>
            {
                if (graph3D is { Visible: true } shown3D && view3D is { Visible: true })
                    shown3D.ZoomBy(1 / 1.25f);
                else
                    graph.ZoomBy(1 / 1.25f);
            }, tooltip: "Zoom out (or the mouse wheel, a pinch of the trackpad)"),
            mode3D,
            modeTimeline,
            Ui.Command("↻ REFRESH", vm.RefreshTitlesCommand, tooltip: "Apply the filters (dates, confidence) and read the titles of the elements again (for an entity of the ontology its name, for an image analysis its title)"),
            capture));

        // The filters: the dates of the relations, and the confidence.
        var dateCheck = Ui.Check("Filter by dates", vm, nameof(vm.DateFilterEnabled),
            "The relations whose period (From – To) does not meet this one are drawn transparent; one of the two dates can stay empty · applied by Search and ↻ Refresh");
        var from = DateBox.Bound(vm, nameof(vm.FilterFrom), "No date: no start");
        var to = DateBox.Bound(vm, nameof(vm.FilterTo), "No date: no end");
        var dateFields = Ui.Row(8, Eve.Eve.Text_("From", Eve.Eve.Text, 12), Ui.Fill(from), Eve.Eve.Text_("To", Eve.Eve.Text, 12), Ui.Fill(to));
        Bind.Enabled(dateFields, vm, nameof(vm.DateFilterEnabled));
        var dateFilter = KnowledgeUi.Card(Ui.Column(6, dateCheck, dateFields), margin: 8);

        var confidenceCheck = Ui.Check("Filter by confidence in the relation", vm, nameof(vm.ConfidenceFilterEnabled),
            "From 0 to 100: the relations at least as probable as the slider; from -100 to 0: the relations at most as probable · the others are drawn transparent · applied by Search and ↻ Refresh");
        confidenceCheck.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var confidenceText = Ui.Label(vm, nameof(vm.MinProbabilityText), Eve.Eve.Green, 12);
        Bind.When(confidenceText, vm, () => confidenceText.Modulate = vm.ConfidenceFilterEnabled ? Colors.White : new Color(1, 1, 1, 0.45f), nameof(vm.ConfidenceFilterEnabled));
        var slider = Bind.Range(new HSlider { MinValue = -100, MaxValue = 100, Step = 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "0 to 100: at least this probable · -100 to 0: at most this probable" }, vm, nameof(vm.MinProbability));
        Bind.When(slider, vm, () =>
        {
            slider.Editable = vm.ConfidenceFilterEnabled;
            slider.Modulate = vm.ConfidenceFilterEnabled ? Colors.White : new Color(1, 1, 1, 0.45f);
        }, nameof(vm.ConfidenceFilterEnabled));
        var confidenceFilter = KnowledgeUi.Card(Ui.Column(2,
            Ui.Row(6, confidenceCheck, confidenceText),
            slider,
            RelationDialog.Marks("-100 · Impossible", "0 · To evaluate", "Certain · 100")), margin: 8);
        var filters = Ui.Row(10, Ui.Fill(dateFilter), Ui.Fill(confidenceFilter));

        // The graph, with + at its top left.
        var add = Eve.Eve.Button("+", primary: true, tooltip: "Add an entity of the ontology to the graph (then draw its relations from the link icon of its card)");
        add.CustomMinimumSize = new Vector2(40, 40);
        add.AddThemeFontSizeOverride("font_size", 22);
        add.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        add.Position = new Vector2(12, 12);
        var graphHolder = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, ClipContents = true };
        graph.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        graphHolder.AddChild(graph);
        graphHolder.AddChild(add);
        graphHolder.AddChild(WorkingOverlay(vm));
        var graphFrame = KnowledgeUi.Card(graphHolder, margin: 1);

        // The details of the selected element.
        var details = KnowledgeUi.Selectable(vm, nameof(vm.SelectedDetails), Eve.Eve.Text, 12);
        var side = KnowledgeUi.Card(Ui.Column(8, Eve.Eve.Caption("Element", new Color("#E11D48")), Ui.FillHeight(Ui.Scroll(details))), margin: 12);
        side.CustomMinimumSize = new Vector2(280, 0);
        var split = new HSplitContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        graphFrame.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        side.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        split.AddChild(graphFrame);
        split.AddChild(side);

        var page = Ui.Column(8, toolbar, Ui.Margin(filters, 8, 0, 8, 0), Ui.FillHeight(Ui.Margin(split, 8, 0, 8, 0)),
            Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsWorking)));

        // The popups opened from the graph give the graph back when they close (as windows owned by it).
        void BackToGraph() => Bind.OnMain(() =>
        {
            if (GodotObject.IsInstanceValid(page) && shell.WindowOf(page) is { } window && shell.Deck.Windows.Contains(window))
                shell.Deck.BringToFront(window);
        });
        // The relation popup closed (saved, deleted or cancelled): the graph is searched again, the entities added staying.
        void Refreshed()
        {
            Bind.OnMain(() => Bind.Run(vm.SearchCommand));
            BackToGraph();
        }
        // A double-click on a link opens its relation: a new version saved there shows in the graph.
        graph.EdgeDoubleClicked += edge => RelationDialog.Open(shell, edge.Relation, () => { }, Refreshed);
        // The red string drawn between the link icons of the elements: the relation popup of a new link.
        graph.RelationRequested += (source, destinations) =>
            RelationDialog.Open(shell, RelationGraphViewModel.LinkEndOf(source), destinations.Select(RelationGraphViewModel.LinkEndOf).ToList(), null,
                () => { }, () =>
                {
                    if (GodotObject.IsInstanceValid(graph))
                        graph.EndString();
                    Refreshed();
                });
        mode3D.Toggled += on =>
        {
            // One of 3D and timeline at a time.
            if (on && modeTimeline.ButtonPressed)
                modeTimeline.ButtonPressed = false;
            if (on && graph3D is null)
            {
                graph3D = new RelationGraph3D(vm) { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
                var range = new DateRangeSlider();
                var shown3D = graph3D;
                range.RangeChanged += (from, to, whole) => shown3D.SetRange(from, to, whole);
                graph3D.TimeBoundsChanged += (oldest, newest, from, to) => range.SetBounds(oldest, newest, from, to);
                // The vertical scale of the probability: from flat (0) to three times the usual height.
                var heightText = new Label { Text = "×1.0", CustomMinimumSize = new Vector2(44, 0) };
                heightText.AddThemeColorOverride("font_color", Eve.Eve.Green);
                var height = new HSlider
                {
                    MinValue = 0, MaxValue = 3, Step = 0.1, Value = 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    TooltipText = "The vertical scale of the probability (+100 low, -100 high): 0 flat · 1 usual · 3 three times higher · double-click: back to 1"
                };
                height.ValueChanged += value =>
                {
                    heightText.Text = $"×{value:0.0}";
                    shown3D.HeightScale = (float)value;
                };
                height.GuiInput += e =>
                {
                    if (e is InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left })
                        height.Value = 1;
                };
                // FPS: the mouse aims the camera, Z Q S D move it (F in the view does the same; F or Escape gives the mouse back).
                var fly = new Button { Text = "🎮 FPS", ToggleMode = true, FocusMode = Control.FocusModeEnum.None, TooltipText = "Move like in a first-person game: the mouse aims, Z Q S D (W A S D) or the arrows move along where you look, Space / C up and down, Shift faster, a click selects the element under the cross · F or Escape gives the mouse back (Z Q S D also move the camera without FPS when the 3D view has the focus)" };
                fly.Toggled += on => shown3D.SetFly(on);
                graph3D.FlyChanged += on => fly.SetPressedNoSignal(on);
                var heightRow = Ui.Row(8, fly, new Label { Text = "PROBABILITY HEIGHT" }, height, heightText);
                view3D = Ui.Column(4, graph3D, Ui.Margin(range, 10, 2, 10, 2), Ui.Margin(heightRow, 10, 0, 10, 6));
                view3D.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                graphHolder.AddChild(view3D);
                graph3D.Rebuild();
                graphHolder.MoveChild(add, -1);
                graph3D.NodeDoubleClicked += node => _ = vm.OpenNodeAsync(node);
                graph3D.EdgeDoubleClicked += edge => RelationDialog.Open(shell, edge.Relation, () => { }, Refreshed);
                graph3D.ClusterDoubleClicked += cluster => ClusterDialog.Open(shell, cluster, saved: () => _ = vm.LoadClustersAsync());
            }
            else if (on)
                graph3D!.Rebuild();
            if (view3D is not null)
                view3D.Visible = on;
            graph.Visible = !on && !modeTimeline.ButtonPressed;
            vm.ReportStatus(on ? "3D: Z Q S D / arrows: move · F: FPS (the mouse aims) · drag: turn around · right drag: move · wheel: zoom · click: details · double-click: open"
                : "2D graph", false);
        };
        modeTimeline.Toggled += on =>
        {
            if (on && mode3D.ButtonPressed)
                mode3D.ButtonPressed = false;
            if (on && timeline is null)
            {
                timeline = new RelationTimeline(vm) { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
                var range = new DateRangeSlider { TooltipText = "The dates shown on the timeline: drag the left handle from the oldest date, the right one from the newest · double-click: all the dates" };
                var shown = timeline;
                range.RangeChanged += (from, to, whole) => shown.SetRange(from, to, whole);
                timeline.TimeBoundsChanged += (oldest, newest, from, to) => range.SetBounds(oldest, newest, from, to);
                // The cluster to work on: its dates and those of its members on the timeline, UPDATE saves them.
                var clusterPick = new OptionButton { CustomMinimumSize = new Vector2(260, 0), TooltipText = "The elements of the graph with the dates of the ontology, or a cluster stored on the server: its row and its members with the dates of the cluster (from, to), dragged on the timeline" };
                var update = Eve.Eve.Button("💾 UPDATE", primary: true, tooltip: "Save the cluster with the dates changed on the timeline");
                update.Disabled = true;
                var reload = Ui.Action("↻", () => _ = vm.LoadClustersAsync(), tooltip: "Read the clusters again");
                var edited = Eve.Eve.Text_(string.Empty, Eve.Eve.Amber, 11);
                var chosenId = (string?)null;
                void FillClusters()
                {
                    clusterPick.Clear();
                    clusterPick.AddItem("All elements · dates of the ontology");
                    foreach (var c in vm.Clusters)
                        clusterPick.AddItem($"◎ {c.Name} · {c.Members.Count} member(s)");
                    // The cluster chosen before stays chosen (its new copy from the server), else the elements.
                    var index = chosenId is null ? -1 : vm.Clusters.ToList().FindIndex(c => c.Id == chosenId);
                    clusterPick.Selected = index + 1;
                    timeline?.SetCluster(index >= 0 ? vm.Clusters[index] : null);
                    update.Disabled = true;
                    edited.Text = string.Empty;
                }
                clusterPick.ItemSelected += index =>
                {
                    var chosen = index >= 1 && index - 1 < vm.Clusters.Count ? vm.Clusters[(int)index - 1] : null;
                    chosenId = chosen?.Id;
                    shown.SetCluster(chosen);
                    update.Disabled = true;
                    edited.Text = string.Empty;
                };
                timeline.ClusterEdited += _ =>
                {
                    update.Disabled = false;
                    edited.Text = "dates changed · not saved";
                };
                update.Pressed += () =>
                {
                    if (shown.Cluster is not { } cluster)
                        return;
                    update.Disabled = true;
                    _ = vm.SaveClusterAsync(cluster).ContinueWith(t => Bind.OnMain(() =>
                    {
                        if (!GodotObject.IsInstanceValid(update)) return;
                        update.Disabled = t.Result;
                        if (t.Result) edited.Text = string.Empty;
                    }));
                };
                timeline.ClusterDoubleClicked += cluster => ClusterDialog.Open(shell, cluster, saved: () => _ = vm.LoadClustersAsync());
                Action clustersChanged = () => Bind.OnMain(() => { if (GodotObject.IsInstanceValid(clusterPick)) FillClusters(); });
                vm.ClustersChanged += clustersChanged;
                KnowledgeUi.OnFreed(clusterPick, () => vm.ClustersChanged -= clustersChanged);
                FillClusters();
                _ = vm.LoadClustersAsync();
                var clusterRow = Ui.Row(8, Eve.Eve.Caption("CLUSTER", Eve.Eve.Amber, 11), clusterPick, reload, update, edited);
                viewTimeline = Ui.Column(4, Ui.Margin(clusterRow, 10, 6, 10, 0), timeline, Ui.Margin(range, 10, 2, 10, 6));
                viewTimeline.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                graphHolder.AddChild(viewTimeline);
                timeline.Rebuild();
                graphHolder.MoveChild(add, -1);
                timeline.NodeDoubleClicked += node => _ = vm.OpenNodeAsync(node);
                // An end of a ribbon dragged: the dates of the entity are saved on the server, the graph follows.
                timeline.PeriodEdited += (node, start, end) => _ = vm.SetPeriodAsync(node, start, end);
            }
            else if (on)
                timeline!.Rebuild();
            if (viewTimeline is not null)
                viewTimeline.Visible = on;
            graph.Visible = !on && !mode3D.ButtonPressed;
            vm.ReportStatus(on ? "Timeline: a ribbon per element from its start date to its end date · drag an end of a ribbon: its date · the slider under it: the dates shown · wheel: scroll · click: details · double-click: open"
                : "2D graph", false);
        };
        // A double-click on an element opens its picture or its entity.
        graph.NodeDoubleClicked += node => _ = vm.OpenNodeAsync(node);

        // The search of the entities (+): one at a time, brought to the front when it is open already.
        var pickerKey = $"relation-entity-picker#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(vm)}";
        add.Pressed += () =>
        {
            if (shell.Deck.Find(pickerKey) is { } open)
                shell.Deck.BringToFront(open);
            else
                EntityPicker.Open(shell, pickerKey, vm.FindEntitiesAsync, entity => Bind.OnMain(() => vm.AddEntity(entity))).Closed += BackToGraph;
        };

        // The windows opened from the graph: an image analysis, the Entity windows of its ontology.
        Action<ImageAnalysisViewModel> image = analysis => Bind.OnMain(() => shell.Open(analysis));
        Action<OntologyWindowViewModel> ontology = created => Bind.OnMain(() => EntityWindows.Attach(created, shell, page));
        vm.ImageRequested += image;
        vm.OntologyCreated += ontology;
        KnowledgeUi.OnFreed(page, () =>
        {
            vm.ImageRequested -= image;
            vm.OntologyCreated -= ontology;
        });

        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(term) && term.IsInsideTree())
                term.GrabFocus();
        }).CallDeferred();
        return page;
    }

    /// <summary>Asks where to save the 3D graph as shown and writes it as a PNG picture.</summary>
    private static async System.Threading.Tasks.Task Capture3DAsync(RelationGraphViewModel vm, RelationGraph3D graph)
    {
        Directory.CreateDirectory(CaptureDirectory);
        var path = await Dialogs.SaveFile("Save the 3D relation graph as a PNG picture", $"relations-3d-{DateTime.Now:yyyyMMdd-HHmmss}.png", ["*.png ; PNG picture"], CaptureDirectory);
        if (string.IsNullOrEmpty(path) || !GodotObject.IsInstanceValid(graph))
            return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            path += ".png";
        if (graph.Capture() is not { } image || image.SavePng(path) != Error.Ok)
        {
            vm.ReportStatus("The graph could not be saved: " + path, true);
            return;
        }
        vm.ReportStatus($"Graph saved: {path}", false);
        ActivityLog.Info("Relation", $"3D graph captured: {path}");
    }

    /// <summary>Asks where to save the timeline as shown and writes it as a PNG picture.</summary>
    private static async System.Threading.Tasks.Task CaptureTimelineAsync(RelationGraphViewModel vm, RelationTimeline timeline)
    {
        Directory.CreateDirectory(CaptureDirectory);
        var path = await Dialogs.SaveFile("Save the timeline as a PNG picture", $"relations-timeline-{DateTime.Now:yyyyMMdd-HHmmss}.png", ["*.png ; PNG picture"], CaptureDirectory);
        if (string.IsNullOrEmpty(path) || !GodotObject.IsInstanceValid(timeline))
            return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            path += ".png";
        var image = await timeline.CaptureAsync();
        if (image is null || image.SavePng(path) != Error.Ok)
        {
            vm.ReportStatus("The timeline could not be saved: " + path, true);
            return;
        }
        vm.ReportStatus($"Timeline saved: {path}", false);
        ActivityLog.Info("Relation", $"Timeline captured: {path}");
    }

    /// <summary>Asks where to save the graph as shown and writes it as a PNG picture (twice the size on screen).</summary>
    private static async System.Threading.Tasks.Task CaptureAsync(RelationGraphViewModel vm, RelationGraphCanvas graph)
    {
        Directory.CreateDirectory(CaptureDirectory);
        var path = await Dialogs.SaveFile("Save the relation graph as a PNG picture", $"relations-{DateTime.Now:yyyyMMdd-HHmmss}.png", ["*.png ; PNG picture"], CaptureDirectory);
        if (string.IsNullOrEmpty(path) || !GodotObject.IsInstanceValid(graph))
            return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            path += ".png";
        await SaveCapture(vm, graph, path);
    }

    /// <summary>Writes the graph as shown to <paramref name="path"/> and reports it in the status bar.</summary>
    internal static async System.Threading.Tasks.Task SaveCapture(RelationGraphViewModel vm, RelationGraphCanvas graph, string path)
    {
        var image = await graph.CaptureAsync();
        if (image is null || image.SavePng(path) != Error.Ok)
        {
            vm.ReportStatus("The graph could not be saved: " + path, true);
            return;
        }
        vm.ReportStatus($"Graph saved: {path}", false);
        ActivityLog.Info("Relation", $"Graph captured: {path}");
    }

    /// <summary>
    /// A panel in the middle of the graph while it works: a moving bar during the search, then the share of the layout
    /// done while the elements are placed.
    /// </summary>
    private static Control WorkingOverlay(RelationGraphViewModel vm)
    {
        var caption = Eve.Eve.Text_("", Eve.Eve.TextBright, 13);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        var bar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(320, 8) };
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", Eve.Eve.Box((Eve.Eve.IsLight ? Colors.White : new Color(0.05f, 0.07f, 0.09f)) with { A = 0.95f }, Eve.Eve.Edge, 16, 1));
        panel.AddChild(Ui.Column(10, caption, bar));
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        center.AddChild(panel);
        Bind.When(center, vm, () =>
        {
            center.Visible = vm.IsWorking;
            bar.Indeterminate = !vm.IsLayingOut;
            bar.Value = vm.LayoutProgress;
            caption.Text = vm.IsLayingOut ? $"Placing the elements… {vm.LayoutProgress:0} %" : "Searching the entities and their relations…";
        }, nameof(vm.IsWorking), nameof(vm.IsLayingOut), nameof(vm.LayoutProgress));
        return center;
    }
}
