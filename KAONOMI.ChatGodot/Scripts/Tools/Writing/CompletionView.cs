using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.Chat.Views;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Dashboards;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// /completion: the completions of Kaonomi. Toolbar: the search (and, or, ( ), "exact words"; Enter or F5) and the number
/// of results. Left, the advanced search: where the text is searched, the period (from / to, typed or picked, and its
/// presets), the order, the completions with code only, the facets (models, languages, topics, collections: a click
/// ticks or unticks a value, the values ticked of a facet joined by or), CLEAR FILTERS, EXPORT CSV / JSON. Middle, three
/// tabs: RESULTS (the filters in use, then the completions: title, details, sizes and duration), DASHBOARD (key figures,
/// the activity over time — a click on a bar keeps that hour, day or week —, the languages, the topics — a click ticks
/// the topic —, the histograms of the durations and of the sizes, the activity by weekday and hour) and WORD CLOUD (a
/// click searches the term). Right, the completion selected: its details, COPY, DELETE, and its prompt, its completion
/// (Markdown), its raw completion and its metadata.
/// </summary>
[ToolView(typeof(CompletionWindowViewModel), "Completions", "☁", 1500, 920)]
public sealed class CompletionView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (CompletionWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;

        // ── The toolbar ──
        var size = Bind.Options(new OptionButton { TooltipText = "Largest number of completions read from the search engine" }, vm.SizeChoices, vm, nameof(vm.Size));
        var search = Ui.SearchBar(vm, nameof(vm.Term), vm.SearchCommand,
            "Search the completions: words, and, or, ( ), \"exact words\" · empty: all, with the filters of the left");
        search.AddChild(size);
        search.MoveChild(size, 1);
        var searchBox = (LineEdit)search.GetChild(0);

        var center = new TabContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        Tab(center, "📋 RESULTS", BuildResults(vm));
        Tab(center, "📊 DASHBOARD", BuildDashboard(vm));
        Tab(center, "☁ WORD CLOUD", BuildCloud(vm));

        var body = Ui.SplitH(BuildFilters(vm), Ui.SplitH(Ui.Margin(center, 4, 6, 4, 4), BuildSelected(vm), 0), 310);
        body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        // The completion selected keeps about a third of the width.
        var inner = (HSplitContainer)body.GetChild(1);
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(inner))
                inner.SplitOffset = (int)(inner.Size.X * 0.55f);
        }).CallDeferred();

        var page = Ui.Column(6, Ui.Toolbar(search), body, WritingUi.StatusBar(vm, nameof(vm.Status), vm, nameof(vm.IsBusy)));
        WritingUi.Shortcuts(page, key => key.Keycode == Key.F5 && WritingUi.Run(vm.SearchCommand));
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(searchBox))
                searchBox.GrabFocus();
        }).CallDeferred();
        return page;
    }

    private static void Tab(TabContainer tabs, string title, Control content)
    {
        tabs.AddChild(content);
        tabs.SetTabTitle(tabs.GetTabCount() - 1, title);
    }

    // ── The advanced search ──

    private static Control BuildFilters(CompletionWindowViewModel vm)
    {
        var presets = WritingUi.Flow(4, 4, vm.PeriodPresets.Select(p => (Control)Ui.Action(p, () => vm.SetPeriodCommand.Execute(p), tooltip: $"The period: {p.ToLowerInvariant()}")).ToArray());
        var column = Ui.Column(10,
            Eve.Eve.Caption("ADVANCED SEARCH", Eve.Eve.Cyan, 12),
            Ui.Field("Search the text in", WritingUi.Choice(vm, nameof(vm.SearchIn), vm.SearchInChoices)),
            Ui.Field("From", WritingUi.DateField(vm, nameof(vm.From))),
            Ui.Field("To (the whole day)", WritingUi.DateField(vm, nameof(vm.To))),
            presets,
            Ui.Field("Order", WritingUi.Choice(vm, nameof(vm.Sort), vm.SortChoices)),
            Ui.Check("Only the completions with code", vm, nameof(vm.CodeOnly), "Completions holding a block of code (```)"),
            Facets("MODELS", vm, vm.ModelFacets),
            Facets("LANGUAGES", vm, vm.LanguageFacets),
            Facets("TOPICS", vm, vm.TopicFacets),
            Facets("COLLECTIONS", vm, vm.CollectionFacets),
            Ui.Margin(Eve.Eve.Separator(), 0, 4, 0, 4),
            Ui.Command("✕ CLEAR FILTERS", vm.ClearFiltersCommand, tooltip: "The period, the facets, the order and the code filter off (the text stays)"),
            Ui.Row(6,
                Ui.Fill(Ui.Action("⬇ CSV", () => vm.ExportCommand.Execute("csv"), tooltip: $"The results as CSV (Excel), in {CompletionWindowViewModel.ExportDirectory}")),
                Ui.Fill(Ui.Action("⬇ JSON", () => vm.ExportCommand.Execute("json"), tooltip: $"The results as JSON, in {CompletionWindowViewModel.ExportDirectory}")),
                Ui.Action("📁", () => OS.ShellShowInFileManager(vm.LastExport ?? CompletionWindowViewModel.ExportDirectory), tooltip: "Show the last export in its folder")));
        var scroll = Ui.Scroll(Ui.Margin(column, 8, 6, 12, 8));
        scroll.CustomMinimumSize = new Vector2(290, 0);
        return scroll;
    }

    /// <summary>A facet: its values, each a button with its count and its share of the results; a click ticks or unticks it.</summary>
    private static Control Facets(string caption, CompletionWindowViewModel vm, ObservableCollection<FacetValue> values)
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var none = Eve.Eve.Text_("—", Eve.Eve.Muted, 11);
        Bind.Items(list, values, value =>
        {
            var button = new Button
            {
                Text = $"{(value.Selected ? "☑" : "☐")}  {value.Label}",
                Alignment = HorizontalAlignment.Left,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                TooltipText = $"{value.Label}: {value.Count} of {value.Total} result(s) ({value.Share:0}%) · click to {(value.Selected ? "untick" : "keep only the ticked values")}",
                FocusMode = Control.FocusModeEnum.None,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand
            };
            button.AddThemeFontSizeOverride("font_size", 12);
            if (value.Selected)
                Eve.Eve.StylePrimary(button);
            button.Pressed += () => vm.ToggleFacetCommand.Execute(value);
            var count = Eve.Eve.Text_(value.Count.ToString(), Eve.Eve.Muted, 11);
            count.CustomMinimumSize = new Vector2(38, 0);
            count.HorizontalAlignment = HorizontalAlignment.Right;
            return Ui.Column(1, Ui.Row(4, button, count), DashUi.Share(value.Share, 3));
        }, none);
        return Ui.Column(4, Eve.Eve.Caption(caption, Eve.Eve.Cyan, 11), list, none);
    }

    // ── The results ──

    private static Control BuildResults(CompletionWindowViewModel vm)
    {
        var filters = Ui.Label(vm, nameof(vm.ActiveFilters), Eve.Eve.Muted, 11, wrap: true);
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("No completion");
        Bind.Items(list, vm.Results, item => ResultRow(vm, item), empty);
        return Ui.Column(6, Ui.Margin(filters, 4, 6, 4, 0), Ui.FillHeight(Ui.Scroll(list)), empty);
    }

    /// <summary>A result: its title (the first line of the prompt), its details, its sizes and duration.</summary>
    private static Control ResultRow(CompletionWindowViewModel vm, CompletionItemViewModel item)
    {
        var figures = Eve.Eve.Text_(item.Figures, item.HasCode ? Eve.Eve.Amber : Eve.Eve.Cyan, 11);
        figures.HorizontalAlignment = HorizontalAlignment.Right;
        var title = WritingUi.Clamp(Eve.Eve.Text_(item.Title, Eve.Eve.TextBright, 13), 2);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var content = Ui.Column(2,
            Ui.Row(8, title, figures),
            WritingUi.Clamp(Eve.Eve.Text_(item.Summary, Eve.Eve.Muted, 11), 1));
        return WritingUi.ResultRow(vm, nameof(vm.SelectedResult), () => ReferenceEquals(vm.SelectedResult, item), () => vm.SelectedResult = item, content, 46);
    }

    // ── The dashboard ──

    private static Control BuildDashboard(CompletionWindowViewModel vm)
    {
        var kpis = Ui.Grid(3, 8, 8);
        foreach (var kpi in new[]
                 {
                     DashUi.Kpi(vm, "COMPLETIONS", nameof(vm.CountText), nameof(vm.CountDetails)),
                     DashUi.Kpi(vm, "PERIOD", nameof(vm.PeriodText), nameof(vm.PeriodDetails)),
                     DashUi.Kpi(vm, "MODELS", nameof(vm.ModelsText), nameof(vm.ModelsDetails)),
                     DashUi.Kpi(vm, "WORDS / COMPLETION", nameof(vm.WordsText), nameof(vm.WordsDetails)),
                     DashUi.Kpi(vm, "DURATION", nameof(vm.DurationText), nameof(vm.DurationDetails), Eve.Eve.Amber),
                     DashUi.Kpi(vm, "SPEED", nameof(vm.SpeedText), nameof(vm.SpeedDetails), Eve.Eve.Green)
                 })
        {
            kpi.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            kpis.AddChild(kpi);
        }

        // The activity over time: a click on a bar keeps that hour, day or week.
        var timeline = new BarChart { CustomMinimumSize = new Vector2(0, 170), Clickable = true };
        Bind.When(timeline, vm, () => timeline.Bars = vm.Timeline, nameof(vm.Timeline));
        timeline.Clicked += bar => vm.ZoomToCommand.Execute(bar);
        var timelineCaption = Ui.Label(vm, nameof(vm.TimelineUnit), Eve.Eve.Muted, 11, format: unit => $"Completions per {unit} · a click on a bar keeps only that {unit}");

        // The languages (pie and legend) and the topics (a click ticks the topic).
        var pie = new PieChartControl { CustomMinimumSize = new Vector2(150, 150) };
        var legend = new VBoxContainer();
        Bind.When(pie, vm, () =>
        {
            pie.Slices = vm.Languages;
            foreach (var child in legend.GetChildren())
                child.QueueFree();
            foreach (var slice in vm.Languages)
                legend.AddChild(LegendItem(slice));
        }, nameof(vm.Languages));
        var topics = new BarChart { Horizontal = true, Clickable = true, BarColor = Eve.Eve.Green };
        Bind.When(topics, vm, () => topics.Bars = vm.TopicBars, nameof(vm.TopicBars));
        topics.Clicked += bar => vm.ToggleFacetCommand.Execute(new FacetValue(CompletionWindowViewModel.TopicField, bar.Label == "(none)" ? string.Empty : bar.Label, 0, 0, false));

        var durations = new BarChart { BarColor = Eve.Eve.Amber, CustomMinimumSize = new Vector2(0, 140) };
        Bind.When(durations, vm, () => durations.Bars = vm.DurationBars, nameof(vm.DurationBars));
        var lengths = new BarChart { BarColor = DashUi.Blue, CustomMinimumSize = new Vector2(0, 140) };
        Bind.When(lengths, vm, () => lengths.Bars = vm.LengthBars, nameof(vm.LengthBars));

        var heatmap = new WeekHourHeatmap();
        Bind.When(heatmap, vm, () => heatmap.Counts = vm.WeekHours, nameof(vm.WeekHours));

        Control Half(Control a, Control b)
        {
            a.SizeFlagsHorizontal = b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            return Ui.Row(8, a, b);
        }
        var column = Ui.Column(10,
            kpis,
            DashUi.Card(Ui.Column(4, timelineCaption, timeline), "ACTIVITY"),
            Half(DashUi.Card(Ui.Row(12, pie, Ui.Fill(legend)), "LANGUAGES"), DashUi.Card(topics, "TOPICS (a click ticks the topic)")),
            Half(DashUi.Card(durations, "DURATIONS (seconds)"), DashUi.Card(lengths, "SIZES OF THE COMPLETIONS (words)")),
            DashUi.Card(heatmap, "BY WEEKDAY AND HOUR"));
        return Ui.Scroll(Ui.Margin(column, 4, 8, 12, 8));
    }

    // ── The word cloud ──

    private static Control BuildCloud(CompletionWindowViewModel vm)
    {
        var cloud = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        cloud.AddThemeConstantOverride("h_separation", 4);
        cloud.AddThemeConstantOverride("v_separation", 2);
        var noTerm = Ui.Placeholder("No term");
        Bind.Items(cloud, vm.Cloud, item => Word(vm, item), noTerm);
        return Panel(Ui.Column(0, Ui.FillHeight(Ui.Scroll(Ui.Column(0, cloud))), noTerm));
    }

    // ── The completion selected ──

    private static Control BuildSelected(CompletionWindowViewModel vm)
    {
        var title = Ui.Label(vm, nameof(vm.SelectedResult), Eve.Eve.TextBright, 13, wrap: true, format: v => (v as CompletionItemViewModel)?.Title ?? "No completion selected");
        var summary = Ui.Label(vm, nameof(vm.SelectedResult), Eve.Eve.Muted, 11, wrap: true, format: v => v is CompletionItemViewModel i ? $"{i.Summary}\n{i.Figures}" : string.Empty);
        var actions = Ui.Row(6,
            Ui.Action("⎘ PROMPT", () => Dialogs.Copy(vm.SelectedResult?.Entry.Prompt ?? string.Empty), tooltip: "Copy the prompt"),
            Ui.Action("⎘ COMPLETION", () => Dialogs.Copy(vm.SelectedResult?.Entry.Completion ?? string.Empty), tooltip: "Copy the completion"),
            Ui.Spring(),
            Ui.Command("DELETE", vm.DeleteCommand, tooltip: "Delete this completion from Kaonomi"));

        var prompt = ReadOnly();
        var completion = Ui.Markdown();
        completion.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        completion.SelectionEnabled = true;
        var raw = ReadOnly();
        var metadata = ReadOnly();
        metadata.AddThemeFontOverride("font", Eve.Eve.Mono);
        metadata.AddThemeFontSizeOverride("font_size", 12);
        Bind.When(prompt, vm, () =>
        {
            var entry = vm.SelectedResult?.Entry;
            prompt.Text = entry?.Prompt ?? string.Empty;
            Ui.SetMarkdown(completion, entry?.Completion ?? string.Empty);
            raw.Text = entry?.RawCompletion is { Length: > 0 } r ? r : entry?.Completion ?? string.Empty;
            metadata.Text = vm.SelectedMetadata + (vm.SelectedTerms.Length > 0 ? "\n\nMost frequent terms:\n" + vm.SelectedTerms : string.Empty);
        }, nameof(vm.SelectedResult));

        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        Tab(tabs, "COMPLETION", Ui.Scroll(Ui.Margin(completion, 8)));
        Tab(tabs, "PROMPT", prompt);
        Tab(tabs, "RAW", raw);
        Tab(tabs, "METADATA", metadata);

        var column = Ui.Column(6, title, summary, actions, tabs);
        column.CustomMinimumSize = new Vector2(380, 0);
        return Ui.Margin(column, 6, 8, 8, 6);
    }

    private static PanelContainer Panel(Control content)
    {
        var panel = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.25f), Eve.Eve.Edge with { A = 0.3f }, 12));
        panel.AddChild(content);
        return panel;
    }

    private static TextEdit ReadOnly() => new()
    {
        Editable = false,
        WrapMode = TextEdit.LineWrappingMode.Boundary,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        ContextMenuEnabled = true
    };

    /// <summary>A term of the cloud, at its size and in its colour; a click searches it.</summary>
    private static Button Word(CompletionWindowViewModel vm, WordCloudItem item)
    {
        var button = new Button { Text = item.Term, TooltipText = item.Tip, Flat = true, MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var color = AvaloniaBridge.ToColor(item.Brush);
        button.AddThemeFontSizeOverride("font_size", (int)Math.Round(item.FontSize));
        button.AddThemeFontOverride("font", Bold);
        button.AddThemeColorOverride("font_color", color);
        button.AddThemeColorOverride("font_hover_color", color.Lightened(0.3f));
        button.AddThemeColorOverride("font_pressed_color", Eve.Eve.Ink);
        var hover = Eve.Eve.Box(new Color(0.5f, 0.5f, 0.5f, 0.1f), new Color(0, 0, 0, 0), 0, 0);
        hover.ContentMarginLeft = hover.ContentMarginRight = 5;
        var normal = Eve.Eve.Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 0, 0);
        normal.ContentMarginLeft = normal.ContentMarginRight = 5;
        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", hover);
        Bind.Command(button, vm.SearchTermCommand, () => item.Term);
        return button;
    }

    private static readonly SystemFont Bold = new() { FontNames = Eve.Eve.Body.FontNames, FontWeight = 600, Fallbacks = [Eve.Eve.Emoji] };

    /// <summary>A line of the legend: the colour of the slice, its label, value and share.</summary>
    private static Control LegendItem(PieSlice slice)
    {
        var swatch = new ColorRect { Color = AvaloniaBridge.ToColor(slice.Brush), CustomMinimumSize = new Vector2(10, 10), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        return Ui.Row(5, swatch, Eve.Eve.Text_($"{slice.Label}  {slice.Value} ({slice.Share:P0})", Eve.Eve.Text, 12));
    }
}

/// <summary>
/// The pie chart of the languages (the PieChart of the Avalonia chat): one slice per value, from the top clockwise,
/// each with a thin gap in the colour of the background; a single slice is a full disc; nothing without a value. The
/// slice under the pointer shows its legend as tooltip.
/// </summary>
public partial class PieChartControl : Control
{
    private IReadOnlyList<PieSlice> _slices = [];

    public PieChartControl()
    {
        MouseFilter = MouseFilterEnum.Pass;
    }

    /// <summary>The slices to draw.</summary>
    public IReadOnlyList<PieSlice> Slices
    {
        get => _slices;
        set
        {
            _slices = value ?? [];
            QueueRedraw();
        }
    }

    /// <summary>Colour of the gaps between the slices.</summary>
    public Color Gap { get; set; } = new(0.02f, 0.03f, 0.04f);

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
            QueueRedraw();
    }

    public override void _Draw()
    {
        var slices = _slices.Where(s => s.Value > 0).ToList();
        var total = slices.Sum(s => s.Value);
        if (total <= 0)
            return;
        var radius = Math.Max(1, Math.Min(Size.X, Size.Y) / 2 - 2);
        var center = Size / 2;
        if (slices.Count == 1)
        {
            DrawCircle(center, radius, AvaloniaBridge.ToColor(slices[0].Brush), true, -1, true);
            return;
        }
        var start = -Mathf.Pi / 2;
        foreach (var slice in slices)
        {
            var sweep = Mathf.Tau * slice.Value / total;
            var points = new List<Vector2> { center };
            var steps = Math.Max(2, (int)(sweep / Mathf.Tau * 96));
            for (var i = 0; i <= steps; i++)
                points.Add(center + Vector2.FromAngle(start + sweep * i / steps) * radius);
            DrawColoredPolygon(points.ToArray(), AvaloniaBridge.ToColor(slice.Brush));
            start += sweep;
        }
        // The gaps: a line from the centre at the start of each slice.
        start = -Mathf.Pi / 2;
        foreach (var slice in slices)
        {
            DrawLine(center, center + Vector2.FromAngle(start) * (radius + 1), Gap, 2, true);
            start += Mathf.Tau * slice.Value / total;
        }
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        var slices = _slices.Where(s => s.Value > 0).ToList();
        var total = slices.Sum(s => s.Value);
        var offset = atPosition - Size / 2;
        if (total <= 0 || offset.Length() > Math.Min(Size.X, Size.Y) / 2)
            return string.Empty;
        var angle = Mathf.PosMod(offset.Angle() + Mathf.Pi / 2, Mathf.Tau);
        var start = 0f;
        foreach (var slice in slices)
        {
            var sweep = Mathf.Tau * slice.Value / total;
            if (angle >= start && angle < start + sweep)
                return $"{slice.Label}  {slice.Value} ({slice.Share:P0})";
            start += sweep;
        }
        return string.Empty;
    }
}
