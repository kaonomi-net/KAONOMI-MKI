using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// /relation: a search on the entities (the ones of the ontology the server finds, the elements whose title holds the
/// text; empty: the most linked elements; Enter or Search) and, at its right, the largest number of elements shown,
/// ✎ PEN (draw around elements: a cluster), Show clusters, Fit, + and − (zoom), ⏱ TIMELINE, ↻ Refresh (applies the
/// filters and reads the titles of the elements again), 📷 PNG (the graph as shown, twice its size on screen, or the
/// timeline); the filters of the dates (From, To, each optional) and of the confidence (-100 to 100), the relations
/// outside them drawn transparent; the graph (see <see cref="RelationGraphCanvas"/>) with + (search an entity of the
/// ontology and add it), and the details of the selected element on the right. A double-click on an element opens its
/// picture (image analysis window) or its entity (Entity window), on a link its relation (the relation popup); the red
/// string drawn between the link icons of the cards opens the relation popup for a new link. 3D shows the same graph in 3D
/// (<see cref="RelationGraph3D"/>), with the double slider of its dates, the height of the probability and the FPS mode
/// under it.
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

        // The search, the number of elements, the pen, Fit, zoom, timeline, Refresh, capture.
        var term = Ui.Edit(vm, nameof(vm.Term), "Search the entities: words, and, or, not, ( ), \"exact phrase\" · e.g. hinton or (lecun and not prix) · empty: the most linked elements");
        term.MinWidth = 220;
        term.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Ui.Run(vm.SearchCommand);
                e.Handled = true;
            }
        };
        // Right of the search: the largest number of elements shown (a change searches again).
        var nodeCount = Ui.Combo(vm.NodeCountChoices, vm, nameof(vm.MaxNodes));
        nodeCount.MinWidth = 80;
        ToolTip.SetTip(nodeCount, "The largest number of elements shown: the entities found first, then their neighbours, the most linked first");
        // Timeline: the elements as horizontal ribbons on a grid of time, created the first time and shown instead of the graph.
        RelationTimeline? timeline = null;
        Control? viewTimeline = null;
        var modeTimeline = new ToggleButton { Content = "⏱ TIMELINE" };
        ToolTip.SetTip(modeTimeline, "The elements on a timeline: a ribbon per element from its start date to its end date, on a grid of time from the oldest start to the most recent end; the double slider under it chooses the dates shown · drag an end of a ribbon: the date of the entity · click again: back to the graph");
        bool TimelineShown() => timeline is not null && viewTimeline is { IsVisible: true };
        // 3D: the same graph in 3D, created the first time and shown instead of the 2D one.
        RelationGraph3D? graph3D = null;
        Control? view3D = null;
        var mode3D = new ToggleButton { Content = "3D" };
        ToolTip.SetTip(mode3D, "The graph in 3D: a shape per element; drag: turn around · right drag: move · wheel: zoom · Z Q S D: move · F: FPS · click again: back to 2D");
        bool Shown3D() => graph3D is not null && view3D is { IsVisible: true };
        var capture = Ui.Button("📷 PNG", () =>
        {
            if (Shown3D())
                _ = Capture3DAsync(vm, graph3D!);
            else if (TimelineShown())
                _ = CaptureTimelineAsync(vm, timeline!);
            else
                _ = CaptureAsync(vm, graph);
        }, "Save what is shown (the 2D graph, the 3D graph or the timeline) as a PNG picture (Pictures/kaonomi/relations by default)");
        // The pen: draw around some elements, then name the cluster they make.
        var pen = new ToggleButton { Content = "✎ PEN", Focusable = false };
        ToolTip.SetTip(pen, "Draw around some elements with the mouse: the elements inside the drawing make a cluster, named in the popup that opens · right click or Escape: remove the drawing · click again: back to the hand");
        pen.IsCheckedChanged += (_, _) =>
        {
            var on = pen.IsChecked == true;
            graph.PenMode = on;
            if (!on)
                graph.ClearPen();
            graph.Cursor = new Cursor(on ? StandardCursorType.Cross : StandardCursorType.Arrow);
        };
        graph.ClusterDrawn += (members, path) =>
            ClusterDialog.Open(shell, members, path, saved: () =>
            {
                graph.ClearPen();
                pen.IsChecked = false;
                vm.ShowClusters = true;
                _ = vm.LoadClustersAsync();
            }, closed: graph.ClearPen);
        graph.ClusterDoubleClicked += cluster => ClusterDialog.Open(shell, cluster, saved: () => _ = vm.LoadClustersAsync());
        var showClusters = Ui.Check("Show clusters", vm, nameof(vm.ShowClusters));
        ToolTip.SetTip(showClusters, "The clusters stored on the server drawn around their elements (the drawing of the pen, smoothed, or the hull of the elements when they moved out of it)");
        var toolbar = KnowledgeUi.Toolbar(KnowledgeUi.FillRow(6, [], term,
            Ui.Caption("Nodes"),
            nodeCount,
            Ui.Command("SEARCH", vm.SearchCommand, primary: true),
            pen,
            showClusters,
            Ui.Button("FIT", () => { if (Shown3D()) graph3D!.Fit(); else graph.Fit(); }, "Show the whole graph"),
            Ui.Button("+", () => { if (Shown3D()) graph3D!.ZoomBy(1.25f); else graph.ZoomBy(1.25); }, "Zoom in (or the mouse wheel)"),
            Ui.Button("−", () => { if (Shown3D()) graph3D!.ZoomBy(1 / 1.25f); else graph.ZoomBy(1 / 1.25); }, "Zoom out (or the mouse wheel)"),
            mode3D,
            modeTimeline,
            Ui.Command("↻ REFRESH", vm.RefreshTitlesCommand, tooltip: "Apply the filters (dates, confidence) and read the titles of the elements again (for an entity of the ontology its name, for an image analysis its title)"),
            capture));

        // The filters: the dates of the relations, and the confidence.
        var dateCheck = Ui.Check("Filter by dates", vm, nameof(vm.DateFilterEnabled));
        ToolTip.SetTip(dateCheck, "The relations whose period (From – To) does not meet this one are drawn transparent; one of the two dates can stay empty · applied by Search and ↻ Refresh");
        var from = DateBox.Bound(vm, nameof(vm.FilterFrom), "No date: no start");
        var to = DateBox.Bound(vm, nameof(vm.FilterTo), "No date: no end");
        var dateFields = KnowledgeUi.FillRow(8, [Ui.Label("From", Ui.Text, 12)], from, Ui.Label("To", Ui.Text, 12), to);
        dateFields.ColumnDefinitions[3].Width = GridLength.Star;
        dateFields.Bind(InputElement.IsEnabledProperty, vm, nameof(vm.DateFilterEnabled));
        var dateFilter = KnowledgeUi.Card(Ui.Column(6, dateCheck, dateFields), margin: 8);

        var confidenceCheck = Ui.Check("Filter by confidence in the relation", vm, nameof(vm.ConfidenceFilterEnabled));
        ToolTip.SetTip(confidenceCheck, "From 0 to 100: the relations at least as probable as the slider; from -100 to 0: the relations at most as probable · the others are drawn transparent · applied by Search and ↻ Refresh");
        var confidenceText = Ui.Bound(vm, nameof(vm.MinProbabilityText), Ui.Green, 12);
        confidenceText.Bind(Visual.OpacityProperty, vm, nameof(vm.ConfidenceFilterEnabled), BindingMode.OneWay, v => v is true ? 1.0 : 0.45);
        var slider = new Slider { Minimum = -100, Maximum = 100, SmallChange = 1, LargeChange = 10, TickFrequency = 1, IsSnapToTickEnabled = true }
            .Bind(RangeBase.ValueProperty, vm, nameof(vm.MinProbability), BindingMode.TwoWay)
            .Bind(InputElement.IsEnabledProperty, vm, nameof(vm.ConfidenceFilterEnabled));
        ToolTip.SetTip(slider, "0 to 100: at least this probable · -100 to 0: at most this probable");
        var confidenceFilter = KnowledgeUi.Card(Ui.Column(2,
            KnowledgeUi.FillRow(6, [], confidenceCheck, confidenceText),
            slider,
            KnowledgeUi.Marks("-100 · Impossible", "0 · To evaluate", "Certain · 100")), margin: 8);
        var filters = KnowledgeUi.Pair(10, dateFilter, confidenceFilter);
        filters.Margin = new Thickness(8, 0);

        // The graph, with + at its top left.
        var add = Ui.Button("+", primary: true, tooltip: "Add an entity of the ontology to the graph (then draw its relations from the link icon of its card)");
        add.Width = add.Height = 40;
        add.FontSize = 22;
        add.Padding = new Thickness(0);
        add.HorizontalContentAlignment = HorizontalAlignment.Center;
        add.VerticalContentAlignment = VerticalAlignment.Center;
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.VerticalAlignment = VerticalAlignment.Top;
        add.Margin = new Thickness(12);
        var graphHolder = new Panel { ClipToBounds = true };
        graphHolder.Children.Add(graph);
        graphHolder.Children.Add(add);
        graphHolder.Children.Add(WorkingOverlay(vm));
        var graphFrame = KnowledgeUi.Card(graphHolder, margin: 1);

        // The details of the selected element.
        var details = KnowledgeUi.Selectable(vm, nameof(vm.SelectedDetails), Ui.Text, 12);
        var side = KnowledgeUi.Card(Ui.Dock(
            (new TextBlock { Text = "ELEMENT", Foreground = Paint.Brush(Paint.Hex("#E11D48")), FontSize = 10, Margin = new Thickness(0, 0, 0, 8) }, Dock.Top),
            (Ui.Scroll(details), Dock.Top)), margin: 12);
        var split = KnowledgeUi.SplitRight(graphFrame, side, 300, 220);
        split.Margin = new Thickness(8, 0);

        var status = KnowledgeUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsWorking));
        var page = Ui.Dock((toolbar, Dock.Top), (filters, Dock.Top), (status, Dock.Bottom), (split, Dock.Top));
        toolbar.Margin = new Thickness(0, 0, 0, 8);
        filters.Margin = new Thickness(8, 0, 8, 8);
        status.Margin = new Thickness(0, 8, 0, 0);

        // The popups opened from the graph give the graph back when they close (as windows owned by it).
        void BackToGraph() => KnowledgeUi.BringBack(shell, page);
        // The relation popup closed (saved, deleted or cancelled): the graph is searched again, the entities added staying.
        void Refreshed()
        {
            Ui.OnMain(() => Ui.Run(vm.SearchCommand));
            BackToGraph();
        }
        // A double-click on a link opens its relation: a new version saved there shows in the graph.
        graph.EdgeDoubleClicked += edge => RelationDialog.Open(shell, edge.Relation, () => { }, Refreshed);
        // The red string drawn between the link icons of the elements: the relation popup of a new link.
        graph.RelationRequested += (source, destinations) =>
            RelationDialog.Open(shell, RelationGraphViewModel.LinkEndOf(source), destinations.Select(RelationGraphViewModel.LinkEndOf).ToList(), null,
                () => { }, () =>
                {
                    graph.EndString();
                    Refreshed();
                });
        mode3D.IsCheckedChanged += (_, _) =>
        {
            var on = mode3D.IsChecked == true;
            // One of 3D and the timeline at a time.
            if (on && modeTimeline.IsChecked == true)
                modeTimeline.IsChecked = false;
            if (on && graph3D is null)
            {
                graph3D = new RelationGraph3D(vm);
                var shown3D = graph3D;
                var range = new DateRangeSlider();
                ToolTip.SetTip(range, "The dates shown in 3D: drag the left handle from the oldest date, the right one from the newest · double-click: all the dates");
                range.RangeChanged += (start, end, whole) => shown3D.SetRange(start, end, whole);
                graph3D.TimeBoundsChanged += (oldest, newest, start, end) => range.SetBounds(oldest, newest, start, end);
                // The vertical scale of the probability: from flat (0) to three times the usual height.
                var heightText = Ui.Label("×1.0", Ui.Green, 12);
                heightText.MinWidth = 44;
                var height = new Slider { Minimum = 0, Maximum = 3, SmallChange = 0.1, LargeChange = 0.5, TickFrequency = 0.1, IsSnapToTickEnabled = true, Value = 1, MinWidth = 260 };
                ToolTip.SetTip(height, "The vertical scale of the probability (+100 low, -100 high): 0 flat · 1 usual · 3 three times higher · double-click: back to 1");
                height.PropertyChanged += (_, e) =>
                {
                    if (e.Property != RangeBase.ValueProperty)
                        return;
                    heightText.Text = $"×{height.Value:0.0}";
                    shown3D.HeightScale = (float)height.Value;
                };
                height.DoubleTapped += (_, _) => height.Value = 1;
                // FPS: the mouse aims the camera, Z Q S D move it (F in the view does the same; F or Escape gives the mouse back).
                var fly = new ToggleButton { Content = "🎮 FPS", Focusable = false };
                ToolTip.SetTip(fly, "Move like in a first-person game: the mouse aims, Z Q S D (W A S D) or the arrows move along where you look, Space / C up and down, Shift faster, a click selects the element under the cross · F or Escape gives the mouse back (Z Q S D also move the camera without FPS when the 3D view has the focus)");
                var syncing = false;
                fly.IsCheckedChanged += (_, _) =>
                {
                    if (!syncing)
                        shown3D.SetFly(fly.IsChecked == true);
                };
                graph3D.FlyChanged += on3D =>
                {
                    syncing = true;
                    fly.IsChecked = on3D;
                    syncing = false;
                };
                var heightRow = Ui.Row(8, fly, Ui.Caption("Probability height", Ui.Text, 10), height, heightText);
                heightRow.Margin = new Thickness(10, 0, 10, 6);
                range.Margin = new Thickness(10, 2, 10, 2);
                view3D = Ui.Dock((heightRow, Dock.Bottom), (range, Dock.Bottom), (graph3D, Dock.Top));
                graphHolder.Children.Insert(1, view3D);
                graph3D.NodeDoubleClicked += node => _ = vm.OpenNodeAsync(node);
                graph3D.EdgeDoubleClicked += edge => RelationDialog.Open(shell, edge.Relation, () => { }, Refreshed);
                graph3D.ClusterDoubleClicked += cluster => ClusterDialog.Open(shell, cluster, saved: () => _ = vm.LoadClustersAsync());
            }
            if (view3D is not null)
                view3D.IsVisible = on;
            if (on)
                graph3D!.Rebuild();
            graph.IsVisible = !on && modeTimeline.IsChecked != true;
            vm.ReportStatus(on ? "3D: Z Q S D / arrows: move · F: FPS (the mouse aims) · drag: turn around · right drag: move · wheel: zoom · click: details · double-click: open"
                : "2D graph", false);
        };
        modeTimeline.IsCheckedChanged += (_, _) =>
        {
            var on = modeTimeline.IsChecked == true;
            if (on && mode3D.IsChecked == true)
                mode3D.IsChecked = false;
            if (on && timeline is null)
            {
                timeline = new RelationTimeline(vm);
                var range = new DateRangeSlider();
                ToolTip.SetTip(range, "The dates shown on the timeline: drag the left handle from the oldest date, the right one from the newest · double-click: all the dates");
                var shown = timeline;
                range.RangeChanged += (start, end, whole) => shown.SetRange(start, end, whole);
                timeline.TimeBoundsChanged += (oldest, newest, start, end) => range.SetBounds(oldest, newest, start, end);
                // The cluster to work on: its dates and those of its members on the timeline, UPDATE saves them.
                var clusterPick = new ComboBox { MinWidth = 260 };
                ToolTip.SetTip(clusterPick, "The elements of the graph with the dates of the ontology, or a cluster stored on the server: its row and its members with the dates of the cluster (from, to), dragged on the timeline");
                var update = Ui.Button("💾 UPDATE", primary: true, tooltip: "Save the cluster with the dates changed on the timeline");
                update.IsEnabled = false;
                var reload = Ui.Button("↻", () => _ = vm.LoadClustersAsync(), "Read the clusters again");
                var edited = Ui.Label(string.Empty, Ui.Warm, 11);
                string? chosenId = null;
                var filling = false;
                void FillClusters()
                {
                    filling = true;
                    var items = new List<string> { "All elements · dates of the ontology" };
                    items.AddRange(vm.Clusters.Select(c => $"◎ {c.Name} · {c.Members.Count} member(s)"));
                    clusterPick.ItemsSource = items;
                    // The cluster chosen before stays chosen (its new copy from the server), else the elements.
                    var index = chosenId is null ? -1 : vm.Clusters.ToList().FindIndex(c => c.Id == chosenId);
                    clusterPick.SelectedIndex = index + 1;
                    filling = false;
                    shown.SetCluster(index >= 0 ? vm.Clusters[index] : null);
                    update.IsEnabled = false;
                    edited.Text = string.Empty;
                }
                clusterPick.SelectionChanged += (_, _) =>
                {
                    if (filling)
                        return;
                    var index = clusterPick.SelectedIndex;
                    var chosen = index >= 1 && index - 1 < vm.Clusters.Count ? vm.Clusters[index - 1] : null;
                    chosenId = chosen?.Id;
                    shown.SetCluster(chosen);
                    update.IsEnabled = false;
                    edited.Text = string.Empty;
                };
                timeline.ClusterEdited += _ =>
                {
                    update.IsEnabled = true;
                    edited.Text = "dates changed · not saved";
                };
                update.Click += async (_, _) =>
                {
                    if (shown.Cluster is not { } cluster)
                        return;
                    update.IsEnabled = false;
                    var ok = await vm.SaveClusterAsync(cluster);
                    Ui.OnMain(() =>
                    {
                        update.IsEnabled = !ok;
                        if (ok)
                            edited.Text = string.Empty;
                    });
                };
                timeline.ClusterDoubleClicked += cluster => ClusterDialog.Open(shell, cluster, saved: () => _ = vm.LoadClustersAsync());
                Action clustersChanged = () => Ui.OnMain(FillClusters);
                vm.ClustersChanged += clustersChanged;
                KnowledgeUi.OnGone(clusterPick, () => vm.ClustersChanged -= clustersChanged);
                FillClusters();
                _ = vm.LoadClustersAsync();
                var clusterRow = Ui.Row(8, Ui.Caption("Cluster", Ui.Warm, 11), clusterPick, reload, update, edited);
                clusterRow.Margin = new Thickness(10, 6, 10, 0);
                range.Margin = new Thickness(10, 2, 10, 6);
                viewTimeline = Ui.Dock((clusterRow, Dock.Top), (range, Dock.Bottom), (timeline, Dock.Top));
                graphHolder.Children.Insert(1, viewTimeline);
                timeline.Rebuild();
                timeline.NodeDoubleClicked += node => _ = vm.OpenNodeAsync(node);
                // An end of a ribbon dragged: the dates of the entity are saved on the server, the graph follows.
                timeline.PeriodEdited += (node, start, end) => _ = vm.SetPeriodAsync(node, start, end);
            }
            else if (on)
                timeline!.Rebuild();
            if (viewTimeline is not null)
                viewTimeline.IsVisible = on;
            graph.IsVisible = !on && mode3D.IsChecked != true;
            vm.ReportStatus(on ? "Timeline: a ribbon per element from its start date to its end date · drag an end of a ribbon: its date · the slider under it: the dates shown · wheel: scroll · click: details · double-click: open"
                : "2D graph", false);
        };
        // A double-click on an element opens its picture or its entity.
        graph.NodeDoubleClicked += node => _ = vm.OpenNodeAsync(node);

        // The search of the entities (+): one at a time, brought to the front when it is open already.
        var pickerKey = $"relation-entity-picker#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(vm)}";
        add.Click += (_, _) =>
        {
            if (shell.Deck.Find(pickerKey) is { } open)
                shell.Deck.BringToFront(open);
            else
                EntityPicker.Open(shell, pickerKey, vm.FindEntitiesAsync, entity => Ui.OnMain(() => vm.AddEntity(entity))).Closed += BackToGraph;
        };

        // The windows opened from the graph: an image analysis, the Entity windows of its ontology.
        Action<ImageAnalysisViewModel> image = analysis => Ui.OnMain(() => shell.Open(analysis));
        Action<OntologyWindowViewModel> ontology = created => Ui.OnMain(() => EntityWindows.Attach(created, shell, page));
        vm.ImageRequested += image;
        vm.OntologyCreated += ontology;
        KnowledgeUi.OnGone(page, () =>
        {
            vm.ImageRequested -= image;
            vm.OntologyCreated -= ontology;
        });

        KnowledgeUi.FocusLater(term);
        return page;
    }

    /// <summary>Asks where to save the 3D graph as shown and writes it as a PNG picture.</summary>
    private static async Task Capture3DAsync(RelationGraphViewModel vm, RelationGraph3D graph)
    {
        Directory.CreateDirectory(CaptureDirectory);
        var path = await Dialogs.SaveFile("Save the 3D relation graph as a PNG picture", $"relations-3d-{DateTime.Now:yyyyMMdd-HHmmss}.png", ["*.png ; PNG picture"], CaptureDirectory);
        if (string.IsNullOrEmpty(path))
            return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            path += ".png";
        if (!await graph.SaveCaptureAsync(path))
        {
            vm.ReportStatus("The graph could not be saved: " + path, true);
            return;
        }
        vm.ReportStatus($"3D graph saved: {path}", false);
        ActivityLog.Info("Relation", $"3D graph captured: {path}");
    }

    /// <summary>Asks where to save the timeline as shown and writes it as a PNG picture.</summary>
    private static async Task CaptureTimelineAsync(RelationGraphViewModel vm, RelationTimeline timeline)
    {
        Directory.CreateDirectory(CaptureDirectory);
        var path = await Dialogs.SaveFile("Save the timeline as a PNG picture", $"relations-timeline-{DateTime.Now:yyyyMMdd-HHmmss}.png", ["*.png ; PNG picture"], CaptureDirectory);
        if (string.IsNullOrEmpty(path))
            return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            path += ".png";
        if (!timeline.SaveCapture(path))
        {
            vm.ReportStatus("The timeline could not be saved: " + path, true);
            return;
        }
        vm.ReportStatus($"Timeline saved: {path}", false);
        ActivityLog.Info("Relation", $"Timeline captured: {path}");
    }

    /// <summary>Asks where to save the graph as shown and writes it as a PNG picture (twice the size on screen).</summary>
    private static async Task CaptureAsync(RelationGraphViewModel vm, RelationGraphCanvas graph)
    {
        Directory.CreateDirectory(CaptureDirectory);
        var path = await Dialogs.SaveFile("Save the relation graph as a PNG picture", $"relations-{DateTime.Now:yyyyMMdd-HHmmss}.png", ["*.png ; PNG picture"], CaptureDirectory);
        if (string.IsNullOrEmpty(path))
            return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            path += ".png";
        if (!graph.SaveCapture(path))
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
        var caption = Ui.Label(string.Empty, Ui.TextBright, 13);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Width = 320, Height = 8, MinHeight = 8 };
        var panel = new Border
        {
            Background = Paint.Brush(Paint.Alpha(Paint.PanelSolid, 0.95)),
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = Ui.Column(10, caption, bar)
        };
        Ui.When(panel, vm, () =>
        {
            panel.IsVisible = vm.IsWorking;
            bar.IsIndeterminate = !vm.IsLayingOut;
            bar.Value = vm.LayoutProgress;
            caption.Text = vm.IsLayingOut ? $"Placing the elements… {vm.LayoutProgress:0} %" : "Searching the entities and their relations…";
        }, nameof(vm.IsWorking), nameof(vm.IsLayingOut), nameof(vm.LayoutProgress));
        return panel;
    }
}
