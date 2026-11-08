using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;
using KAONOMI.Chat.Views;
using KAONOMI.UI.Chat;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>
/// /completion: the completions of Kaonomi. Toolbar: the search (and, or, ( ), "exact words"; Enter or F5) and the number of
/// results. Left, the advanced search: where the text is searched, the period (from / to, typed or picked, and its presets),
/// the order, the completions with code only, the facets (models, languages, topics, collections: a click ticks or unticks a
/// value, the values ticked of a facet joined by or), CLEAR FILTERS, EXPORT CSV / JSON. Middle, three tabs: RESULTS (the
/// filters in use, then the completions: title, details, sizes and duration), DASHBOARD (key figures, the activity over time
/// — a click on a bar keeps that hour, day or week —, the languages, the topics — a click ticks the topic —, the histograms
/// of the durations and of the sizes, the activity by weekday and hour) and WORD CLOUD (a click searches the term). Right,
/// the completion selected: its details, COPY, DELETE, and its prompt, its completion (Markdown), its raw completion and its
/// metadata.
/// </summary>
[ToolView(typeof(CompletionWindowViewModel), "Completions", "☁", 1500, 920)]
public sealed class CompletionView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (CompletionWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;

        // ── The toolbar ──
        var size = WritingUi.Choice(vm, nameof(vm.Size), vm.SizeChoices);
        size.MinWidth = 90;
        size.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(size, "Largest number of completions read from the search engine");
        var search = WritingUi.SearchBar(vm, nameof(vm.Term), vm.SearchCommand,
            "Search the completions: words, and, or, ( ), \"exact words\" · empty: all, with the filters of the left", out var searchBox, size);

        var center = new TabControl();
        center.Items.Add(new TabItem { Header = "📋 RESULTS", Content = BuildResults(vm) });
        center.Items.Add(new TabItem { Header = "📊 DASHBOARD", Content = BuildDashboard(vm) });
        center.Items.Add(new TabItem { Header = "☁ WORD CLOUD", Content = BuildCloud(vm) });

        var inner = WritingUi.SplitH(WritingUi.Margin(center, 4, 6, 4, 4), BuildSelected(vm), 0, 55, 45, 300, 340);
        var body = WritingUi.SplitH(BuildFilters(vm), inner, 310, minLeft: 240);

        var page = WritingUi.Page([WritingUi.Toolbar(search)], body, WritingUi.StatusBar(vm, nameof(vm.Status), "IsError", nameof(vm.IsBusy)));
        WritingUi.Shortcuts(page, key => key.Key == Key.F5 && WritingUi.Run(vm.SearchCommand));
        WritingUi.FocusWhenShown(searchBox);
        return page;
    }

    // ── The advanced search ──

    private static Control BuildFilters(CompletionWindowViewModel vm)
    {
        var presets = WritingUi.Flow(4, vm.PeriodPresets.Select(p => (Control)Ui.Button(p, () => vm.SetPeriodCommand.Execute(p), $"The period: {p.ToLowerInvariant()}")).ToArray());
        var csv = Ui.Button("⬇ CSV", () => vm.ExportCommand.Execute("csv"), $"The results as CSV (Excel), in {CompletionWindowViewModel.ExportDirectory}");
        var json = Ui.Button("⬇ JSON", () => vm.ExportCommand.Execute("json"), $"The results as JSON, in {CompletionWindowViewModel.ExportDirectory}");
        var folder = Ui.Button("📁", () => WritingUi.ShowInFolder(vm.LastExport ?? CompletionWindowViewModel.ExportDirectory), "Show the last export in its folder");
        csv.HorizontalAlignment = json.HorizontalAlignment = HorizontalAlignment.Stretch;
        var exports = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), ColumnSpacing = 6 };
        Grid.SetColumn(json, 1);
        Grid.SetColumn(folder, 2);
        exports.Children.Add(csv);
        exports.Children.Add(json);
        exports.Children.Add(folder);
        var codeOnly = Ui.Check("Only the completions with code", vm, nameof(vm.CodeOnly));
        ToolTip.SetTip(codeOnly, "Completions holding a block of code (```)");
        var column = Ui.Column(10,
            Ui.Caption("Advanced search", Ui.Accent, 12),
            WritingUi.Field("Search the text in", WritingUi.Choice(vm, nameof(vm.SearchIn), vm.SearchInChoices)),
            WritingUi.Field("From", WritingUi.DateField(vm, nameof(vm.From))),
            WritingUi.Field("To (the whole day)", WritingUi.DateField(vm, nameof(vm.To))),
            presets,
            WritingUi.Field("Order", WritingUi.Choice(vm, nameof(vm.Sort), vm.SortChoices)),
            codeOnly,
            Facets("MODELS", vm, vm.ModelFacets),
            Facets("LANGUAGES", vm, vm.LanguageFacets),
            Facets("TOPICS", vm, vm.TopicFacets),
            Facets("COLLECTIONS", vm, vm.CollectionFacets),
            WritingUi.Separator(),
            WritingUi.Command("✕ CLEAR FILTERS", vm.ClearFiltersCommand, tooltip: "The period, the facets, the order and the code filter off (the text stays)"),
            exports);
        var scroll = Ui.Scroll(WritingUi.Margin(column, 8, 6, 12, 8));
        scroll.MinWidth = 240;
        return scroll;
    }

    /// <summary>A facet: its values, each a button with its count and its share of the results; a click ticks or unticks it.</summary>
    private static Control Facets(string caption, CompletionWindowViewModel vm, ObservableCollection<FacetValue> values)
    {
        var list = new StackPanel { Spacing = 2 };
        var none = Ui.Label("—", Ui.Muted, 11);
        WritingUi.Items(list, values, value =>
        {
            var button = new Button
            {
                Content = WritingUi.Clamp(Ui.Label($"{(value.Selected ? "☑" : "☐")}  {value.Label}", value.Selected ? Ui.TextBright : Ui.Text, 12), 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Focusable = false,
                Padding = new Thickness(6, 2),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            if (value.Selected)
                button.Classes.Add("primary");
            ToolTip.SetTip(button, $"{value.Label}: {value.Count} of {value.Total} result(s) ({value.Share:0}%) · click to {(value.Selected ? "untick" : "keep only the ticked values")}");
            button.Click += (_, _) => vm.ToggleFacetCommand.Execute(value);
            var count = Ui.Label(value.Count.ToString(), Ui.Muted, 11);
            count.MinWidth = 38;
            count.TextAlignment = TextAlignment.Right;
            count.Margin = new Thickness(4, 0, 0, 0);
            return Ui.Column(1, Ui.Dock((count, Dock.Right), (button, Dock.Left)), WritingUi.Share(value.Share, 3));
        }, none);
        return Ui.Column(4, Ui.Caption(caption, Ui.Accent, 11), list, none);
    }

    // ── The results ──

    private static Control BuildResults(CompletionWindowViewModel vm)
    {
        var filters = Ui.Bound(vm, nameof(vm.ActiveFilters), Ui.Muted, 11, wrap: true);
        filters.Margin = new Thickness(4, 6, 4, 4);
        var list = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var empty = WritingUi.Placeholder("No completion");
        empty.VerticalAlignment = VerticalAlignment.Top;
        WritingUi.Items(list, vm.Results, item => ResultRow(vm, item), empty);
        return WritingUi.Page([filters], new Panel { Children = { Ui.Scroll(list), empty } });
    }

    /// <summary>A result: its title (the first line of the prompt), its details, its sizes and duration.</summary>
    private static Control ResultRow(CompletionWindowViewModel vm, CompletionItemViewModel item)
    {
        var figures = Ui.Label(item.Figures, item.HasCode ? WritingUi.Amber : Ui.Accent, 11);
        figures.Margin = new Thickness(8, 0, 0, 0);
        figures.VerticalAlignment = VerticalAlignment.Top;
        var title = WritingUi.Clamp(Ui.Label(item.Title, Ui.TextBright, 13), 2);
        var content = Ui.Column(2,
            Ui.Dock((figures, Dock.Right), (title, Dock.Left)),
            WritingUi.Clamp(Ui.Label(item.Summary, Ui.Muted, 11), 1));
        return WritingUi.ResultRow(vm, nameof(vm.SelectedResult), () => ReferenceEquals(vm.SelectedResult, item), () => vm.SelectedResult = item, content, 46);
    }

    // ── The dashboard ──

    private static Control BuildDashboard(CompletionWindowViewModel vm)
    {
        var kpis = WritingUi.Cells(3,
            WritingUi.Kpi(vm, "COMPLETIONS", nameof(vm.CountText), nameof(vm.CountDetails)),
            WritingUi.Kpi(vm, "PERIOD", nameof(vm.PeriodText), nameof(vm.PeriodDetails)),
            WritingUi.Kpi(vm, "MODELS", nameof(vm.ModelsText), nameof(vm.ModelsDetails)),
            WritingUi.Kpi(vm, "WORDS / COMPLETION", nameof(vm.WordsText), nameof(vm.WordsDetails)),
            WritingUi.Kpi(vm, "DURATION", nameof(vm.DurationText), nameof(vm.DurationDetails), WritingUi.Amber),
            WritingUi.Kpi(vm, "SPEED", nameof(vm.SpeedText), nameof(vm.SpeedDetails), Ui.Green));
        kpis.ColumnSpacing = kpis.RowSpacing = 8;

        // The activity over time: a click on a bar keeps that hour, day or week.
        var timeline = new BarChart { Height = 170, Clickable = true };
        Ui.When(timeline, vm, () => timeline.Bars = vm.Timeline, nameof(vm.Timeline));
        timeline.Clicked += bar => vm.ZoomToCommand.Execute(bar);
        var timelineCaption = WritingUi.Text(vm, nameof(vm.TimelineUnit), Ui.Muted, 11, format: unit => $"Completions per {unit} · a click on a bar keeps only that {unit}");

        // The languages (pie and legend) and the topics (a click ticks the topic).
        var pie = new PieChart { Width = 150, Height = 150, VerticalAlignment = VerticalAlignment.Top };
        var legend = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 0, 0) };
        Ui.When(pie, vm, () =>
        {
            pie.Slices = vm.Languages;
            legend.Children.Clear();
            foreach (var slice in vm.Languages)
                legend.Children.Add(LegendItem(slice));
        }, nameof(vm.Languages));
        var topics = new BarChart { Horizontal = true, Clickable = true, BarColor = Ui.Green, MinHeight = 40 };
        Ui.When(topics, vm, () => topics.Bars = vm.TopicBars, nameof(vm.TopicBars));
        topics.Clicked += bar => vm.ToggleFacetCommand.Execute(new FacetValue(CompletionWindowViewModel.TopicField, bar.Label == "(none)" ? string.Empty : bar.Label, 0, 0, false));

        var durations = new BarChart { BarColor = WritingUi.Amber, Height = 140 };
        Ui.When(durations, vm, () => durations.Bars = vm.DurationBars, nameof(vm.DurationBars));
        var lengths = new BarChart { BarColor = WritingUi.Blue, Height = 140 };
        Ui.When(lengths, vm, () => lengths.Bars = vm.LengthBars, nameof(vm.LengthBars));

        var heatmap = new WeekHourHeatmap();
        Ui.When(heatmap, vm, () => heatmap.Counts = vm.WeekHours, nameof(vm.WeekHours));

        static Grid Half(Control a, Control b)
        {
            var grid = WritingUi.Cells(2, a, b);
            grid.ColumnSpacing = 8;
            return grid;
        }
        var column = Ui.Column(10,
            kpis,
            WritingUi.Card(Ui.Column(4, timelineCaption, timeline), "ACTIVITY"),
            Half(WritingUi.Card(Ui.Dock((pie, Dock.Left), (legend, Dock.Left)), "LANGUAGES"), WritingUi.Card(topics, "TOPICS (a click ticks the topic)")),
            Half(WritingUi.Card(durations, "DURATIONS (seconds)"), WritingUi.Card(lengths, "SIZES OF THE COMPLETIONS (words)")),
            WritingUi.Card(heatmap, "BY WEEKDAY AND HOUR"));
        return Ui.Scroll(WritingUi.Margin(column, 4, 8, 12, 8));
    }

    // ── The word cloud ──

    private static Control BuildCloud(CompletionWindowViewModel vm)
    {
        var cloud = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12) };
        var noTerm = WritingUi.Placeholder("No term");
        WritingUi.Items(cloud, vm.Cloud, item => Word(vm, item), noTerm);
        return Ui.Box(new Panel { Children = { Ui.Scroll(cloud), noTerm } }, 0, Ui.Field);
    }

    /// <summary>A term of the cloud, at its size and in its colour; a click searches it.</summary>
    private static Button Word(CompletionWindowViewModel vm, WordCloudItem item)
    {
        var button = new Button
        {
            Content = item.Term,
            FontSize = Math.Max(8, Math.Round(item.FontSize)),
            FontWeight = FontWeight.SemiBold,
            Foreground = item.Brush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(5, 0),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(button, item.Tip);
        button.Click += (_, _) => Ui.Run(vm.SearchTermCommand, item.Term);
        return button;
    }

    /// <summary>A line of the legend: the colour of the slice, its label, value and share.</summary>
    private static Control LegendItem(PieSlice slice) =>
        Ui.Row(5, new Border { Background = slice.Brush, Width = 10, Height = 10 }, Ui.Label($"{slice.Label}  {slice.Value} ({slice.Share:P0})", Ui.Text, 12));

    // ── The completion selected ──

    private static Control BuildSelected(CompletionWindowViewModel vm)
    {
        var title = WritingUi.Text(vm, nameof(vm.SelectedResult), Ui.TextBright, 13, wrap: true, format: v => (v as CompletionItemViewModel)?.Title ?? "No completion selected");
        var summary = WritingUi.Text(vm, nameof(vm.SelectedResult), Ui.Muted, 11, wrap: true, format: v => v is CompletionItemViewModel i ? $"{i.Summary}\n{i.Figures}" : string.Empty);
        var copies = Ui.Row(6,
            Ui.Button("⎘ PROMPT", () => Dialogs.Copy(vm.SelectedResult?.Entry.Prompt ?? string.Empty), "Copy the prompt"),
            Ui.Button("⎘ COMPLETION", () => Dialogs.Copy(vm.SelectedResult?.Entry.Completion ?? string.Empty), "Copy the completion"));
        var actions = Ui.Dock((WritingUi.Command("DELETE", vm.DeleteCommand, tooltip: "Delete this completion from Kaonomi"), Dock.Right), (copies, Dock.Left));

        var prompt = WritingUi.ReadOnly();
        var completion = new ContentControl();
        var raw = WritingUi.ReadOnly();
        var metadata = WritingUi.ReadOnly(mono: true, size: 12);
        Ui.When(prompt, vm, () =>
        {
            var entry = vm.SelectedResult?.Entry;
            prompt.Text = entry?.Prompt ?? string.Empty;
            completion.Content = MarkdownView.Build(entry?.Completion ?? string.Empty);
            raw.Text = entry?.RawCompletion is { Length: > 0 } r ? r : entry?.Completion ?? string.Empty;
            metadata.Text = vm.SelectedMetadata + (vm.SelectedTerms.Length > 0 ? "\n\nMost frequent terms:\n" + vm.SelectedTerms : string.Empty);
        }, nameof(vm.SelectedResult));

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "COMPLETION", Content = Ui.Scroll(WritingUi.Margin(completion, 8)) });
        tabs.Items.Add(new TabItem { Header = "PROMPT", Content = prompt });
        tabs.Items.Add(new TabItem { Header = "RAW", Content = raw });
        tabs.Items.Add(new TabItem { Header = "METADATA", Content = metadata });

        var top = Ui.Column(6, title, summary, actions);
        top.Margin = new Thickness(0, 0, 0, 6);
        return WritingUi.Margin(WritingUi.Page([top], tabs), 6, 8, 8, 6);
    }
}
