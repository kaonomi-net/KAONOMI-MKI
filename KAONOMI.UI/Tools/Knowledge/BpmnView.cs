using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// /bpmnai: AIMN (from BPMN), the editor of diagrams of AI processes in the BPMN 2.0 notation. Header: the file, NEW,
/// IMPORT, EXPORT, EXPORT AS, UNDO, REDO, DELETE, PEN and "Show clusters" (clusters drawn around elements, as in the
/// graph of relations), FIT, 1:1, the zoom, PNG, COPY XML. Left: the palette of the basic elements (events, activities,
/// gateways, artifacts…), the pointer and the connect tool (an element is dragged onto the canvas, or clicked then
/// placed by a click). Middle: the canvas (<see cref="BpmnCanvas"/>). Right: the properties of the selection (its kind,
/// its id, its name), the name of the process and a comment, then the search engine: SAVE (the diagram, its XML and its
/// comment, in the bpmn index through the Kaonomi server; a new version each time), DELETE, the search of the diagrams
/// and their list (a click opens one), and a reminder of the gestures. Status bar: what was done last, the count of the
/// elements. The diagrams are also saved as BPMN 2.0 XML files (with their diagram interchange) in ~/kaonomi/bpmn.
/// The same view shows /infra (<see cref="InfraWindowViewModel"/>, a <see cref="BpmnWindowViewModel"/>) when asked.
/// </summary>
[ToolView(typeof(BpmnWindowViewModel), "AIMN (from BPMN)", "🔀", 1240, 780)]
public sealed class BpmnView : IToolView
{
    private static readonly string[] BpmnFilters = ["*.bpmn ; BPMN 2.0 diagram", "*.xml ; XML"];

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (BpmnWindowViewModel)viewModel;
        var canvas = new BpmnCanvas(vm);

        // ── The header ──
        var file = Ui.Label(string.Empty, Ui.Muted, 11);
        Ui.When(file, vm, () =>
        {
            file.Text = (vm.FilePath ?? vm.FileName) + (vm.IsDirty ? " •" : "");
            ToolTip.SetTip(file, vm.FilePath ?? "Not saved yet");
        }, nameof(vm.FilePath), nameof(vm.IsDirty));

        var zoom = Ui.Label("100 %", Ui.Muted, 11);
        zoom.MinWidth = 44;
        zoom.TextAlignment = TextAlignment.Right;
        canvas.ViewChanged += () => zoom.Text = $"{canvas.ZoomPercent} %";

        var title = Ui.Label(vm.EditorTitle.ToUpperInvariant(), Ui.TextBright, 14, bold: true);
        var buttons = KnowledgeUi.Flow(6,
            Ui.Button("✚ NEW", () => _ = NewAsync(vm), "An empty diagram (the changes not saved are asked about)"),
            Ui.Button("📂 IMPORT", () => _ = OpenAsync(vm), "Import a BPMN 2.0 file (.bpmn, .xml)"),
            Ui.Button("💾 EXPORT", () => _ = SaveAsync(vm, saveAs: false), $"Export the diagram as BPMN 2.0 XML (in {vm.FilesDirectory} by default)", primary: true),
            Ui.Button("EXPORT AS", () => _ = SaveAsync(vm, saveAs: true), "Export the diagram to another file"),
            Ui.Command("↶ UNDO", vm.UndoCommand, tooltip: "Undo the last change (⌘Z)"),
            Ui.Command("↷ REDO", vm.RedoCommand, tooltip: "Redo (⇧⌘Z)"),
            Ui.Command("✕ DELETE", vm.DeleteSelectedCommand, tooltip: "Remove the element or the flow selected (⌫)"));
        // The pen of the clusters and "Show clusters", as in the graph of relations.
        foreach (var control in ClusterPen.Toolbar(shell, canvas))
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            buttons.Children.Add(control);
        }
        foreach (var control in new Control[]
                 {
                     Ui.Button("⤢ FIT", canvas.Fit, "The whole diagram in the window (⌘1)"),
                     Ui.Button("1:1", canvas.ResetView, "Zoom 100 % (⌘0)"),
                     zoom,
                     Ui.Button("🖼 PNG", () => _ = SavePngAsync(vm, canvas), $"Save the canvas as a PNG picture, in {vm.PicturesFolder}"),
                     Ui.Button("⎘ COPY XML", () =>
                     {
                         Dialogs.Copy(vm.ToXml());
                         vm.SetStatus("The BPMN 2.0 XML of the diagram is in the clipboard", false);
                     }, "Copy the BPMN 2.0 XML of the diagram")
                 })
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            buttons.Children.Add(control);
        }
        var header = KnowledgeUi.Toolbar(Ui.Column(4, KnowledgeUi.FillRow(10, [title], file), buttons));

        // ── The palette, the canvas, the properties ──
        var palette = BuildPalette(vm, canvas);
        var properties = BuildProperties(vm);
        var canvasFrame = Ui.Box(canvas, 0, Ui.Field);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*,300"), ColumnSpacing = 8, Margin = new Thickness(8) };
        Grid.SetColumn(canvasFrame, 1);
        Grid.SetColumn(properties, 2);
        body.Children.Add(palette);
        body.Children.Add(canvasFrame);
        body.Children.Add(properties);

        var summary = Ui.Bound(vm, nameof(vm.Summary), Ui.Muted, 11);
        var status = KnowledgeUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy), summary);

        // The body takes the whole height between the header and the status bar.
        var page = Ui.Dock((header, Dock.Top), (status, Dock.Bottom), (body, Dock.Top));
        KnowledgeUi.FollowTitle(page, vm, () => vm.EditorTitle + " · " + vm.FileName + (vm.IsDirty ? " •" : ""), nameof(vm.FilePath), nameof(vm.IsDirty));
        KnowledgeUi.FocusLater(canvas);
        return page;
    }

    /// <summary>The palette: the pointer, the connect tool, then the basic elements by group; the tool chosen is lit.</summary>
    private static Control BuildPalette(BpmnWindowViewModel vm, BpmnCanvas canvas)
    {
        var column = new StackPanel { Spacing = 3 };
        Control Caption(string text, double top) => new TextBlock { Text = text.ToUpperInvariant(), Foreground = Ui.Accent, FontSize = 11, Margin = new Thickness(4, top, 4, 2) };

        column.Children.Add(Caption("Tools", 4));
        column.Children.Add(Tool("↖", "Select", "Select, move, pan (Escape)", vm, () => vm.Placing is null && !vm.Connecting, () => vm.SelectToolCommand.Execute(null)));
        column.Children.Add(Tool("→", "Connect", vm.IsInfrastructure
            ? "Click the source element, then the target: a link (its name: the network, the protocol…), or an association to an annotation"
            : "Click the source element, then the target: a sequence flow, or an association to a data object or an annotation", vm, () => vm.Connecting, vm.ToggleConnect));

        string? group = null;
        foreach (var kind in vm.Kinds)
        {
            if (kind.Group() != group)
            {
                group = kind.Group();
                column.Children.Add(Caption(group, 10));
            }
            var k = kind;
            column.Children.Add(Tool(Glyph(kind), kind.Title(), $"Drag it onto the canvas, or click it then click the canvas, to place a {kind.Title().ToLowerInvariant()} (Shift kept: several)",
                vm, () => vm.Placing == k, () => vm.Place(k), k, canvas));
        }

        var tool = Ui.Bound(vm, nameof(vm.ToolName), Ui.Muted, 10, wrap: true);
        tool.Margin = new Thickness(4, 10, 4, 4);
        column.Children.Add(tool);
        return new ScrollViewer { Content = column, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    /// <summary>
    /// A button of the palette: its glyph, its name; lit while <paramref name="active"/>. The button of an element
    /// (<paramref name="kind"/>) can be dragged onto the canvas, which places the element where it is dropped.
    /// </summary>
    private static Button Tool(string glyph, string name, string tooltip, BpmnWindowViewModel vm, Func<bool> active, Action pressed,
        BpmnElementKind? kind = null, BpmnCanvas? canvas = null)
    {
        var button = new Button
        {
            Content = $"{glyph}  {name}",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            FontSize = 12,
            Focusable = false,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(button, tooltip);
        var dragged = false;
        button.Click += (_, _) =>
        {
            if (!dragged)
                pressed();
            dragged = false;
        };
        if (kind is { } k && canvas is not null)
        {
            // A drag of the button onto the canvas places the element where it is dropped.
            Point? start = null;
            button.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                dragged = false;
                start = e.GetCurrentPoint(button).Properties.IsLeftButtonPressed ? e.GetPosition(button) : null;
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
            button.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
            {
                if (start is not { } from)
                    return;
                if (!dragged && Paint.Distance(e.GetPosition(button), from) > 6)
                {
                    dragged = true;
                    button.Cursor = new Cursor(StandardCursorType.DragCopy);
                }
                if (dragged)
                    canvas.PaletteDragOver(k, e.GetPosition(canvas));
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            button.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
            {
                if (start is null)
                    return;
                start = null;
                button.Cursor = new Cursor(StandardCursorType.Hand);
                if (dragged)
                    canvas.PaletteDrop(k, e.GetPosition(canvas));
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
            button.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) =>
            {
                if (start is null || !dragged)
                    return;
                start = null;
                button.Cursor = new Cursor(StandardCursorType.Hand);
            }, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
        }
        Ui.When(button, vm, () => KnowledgeUi.Light(button, active()), nameof(vm.Placing), nameof(vm.Connecting));
        return button;
    }

    private static string Glyph(BpmnElementKind kind) => kind switch
    {
        BpmnElementKind.StartEvent => "○",
        BpmnElementKind.MessageStartEvent => "✉",
        BpmnElementKind.TimerStartEvent => "⏰",
        BpmnElementKind.EventCollector => "📥",
        BpmnElementKind.EndEvent => "●",
        BpmnElementKind.Task => "▭",
        BpmnElementKind.UserTask => "👤",
        BpmnElementKind.AgentTask => "🤖",
        BpmnElementKind.VirtualAgentTask => "👾",
        BpmnElementKind.SubProcess => "⊞",
        BpmnElementKind.ExclusiveGateway => "◇✕",
        BpmnElementKind.ParallelGateway => "◇+",
        BpmnElementKind.InclusiveGateway => "◇○",
        BpmnElementKind.DataObject => "📄",
        BpmnElementKind.SearchEngine => "🔎",
        BpmnElementKind.Graphbase => "🕸",
        BpmnElementKind.Database => "🗄",
        BpmnElementKind.VectorDatabase => "🧮",
        BpmnElementKind.Rag => "🔍",
        BpmnElementKind.Llm => "🧠",
        BpmnElementKind.Model => "🧊",
        BpmnElementKind.RestApi => "🌐",
        BpmnElementKind.Mcp => "🔌",
        BpmnElementKind.TextAnnotation => "📝",
        _ when kind.IsInfra() => kind.Icon(),
        _ => "▭"
    };

    /// <summary>The properties of the selection and of the process, the comment, the search engine, the gestures of the canvas.</summary>
    private static Control BuildProperties(BpmnWindowViewModel vm)
    {
        var column = new StackPanel { Spacing = 8 };
        column.Children.Add(Ui.Caption("Selection", Ui.Accent, 11));
        var kind = Ui.Bound(vm, nameof(vm.SelectedKind), Ui.TextBright, 13);
        kind.FontWeight = FontWeight.SemiBold;
        column.Children.Add(kind);
        var id = Ui.Bound(vm, nameof(vm.SelectedId), Ui.Muted, 11);
        id.FontFamily = Ui.Mono;
        column.Children.Add(id);
        var name = Ui.Edit(vm, nameof(vm.SelectedName), "The name (an annotation: its text; a flow: its condition)");
        name.Bind(InputElement.IsEnabledProperty, vm, nameof(vm.HasSelection));
        column.Children.Add(KnowledgeUi.Field("Name", name));
        column.Children.Add(Ui.Command("✕ DELETE", vm.DeleteSelectedCommand, tooltip: "Remove the selection (⌫)"));

        column.Children.Add(KnowledgeUi.Separator());
        column.Children.Add(Ui.Caption(vm.Subject, Ui.Accent, 11));
        column.Children.Add(KnowledgeUi.Field($"Name of the {vm.Subject} (the title of the diagram)", Ui.Edit(vm, nameof(vm.ProcessName), vm.IsInfrastructure ? "Infrastructure" : "Process")));
        column.Children.Add(KnowledgeUi.Field("Comment", Ui.Edit(vm, nameof(vm.Description), "A comment on the diagram: what it models, its state, its questions… saved with it in the search engine", multiline: true, height: 90)));

        // ── The search engine: the diagram saved there, the diagrams found ──
        column.Children.Add(KnowledgeUi.Separator());
        column.Children.Add(Ui.Caption("Search engine", Ui.Accent, 11));
        column.Children.Add(Ui.Bound(vm, nameof(vm.DiagramInfo), Ui.Muted, 11));
        column.Children.Add(Ui.Row(6,
            Ui.Command("☁ SAVE", vm.SaveToServerCommand, tooltip: "Save the diagram (its XML and its comment) in the search engine: a new document the first time (its id the slug of the name of the process), a new version after", primary: true),
            Ui.Command("✕ DELETE", vm.DeleteFromServerCommand, tooltip: "Mark the diagram deleted in the search engine (it is never erased; the canvas keeps it)")));
        var search = Ui.Edit(vm, nameof(vm.Search), "Search the diagrams (title, comment, elements)… · Enter");
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Ui.Run(vm.SearchCommand);
                e.Handled = true;
            }
        };
        column.Children.Add(KnowledgeUi.FillRow(6, [], search, Ui.Command("⟳", vm.SearchCommand, tooltip: "Search (Enter); empty: every diagram, by title")));
        column.Children.Add(EntryList(vm, vm.Diagrams, "No diagram in the search engine yet", nameof(vm.DiagramId),
            entry => vm.DiagramId is not null && vm.DiagramId == entry.Id, entry => Ui.Run(vm.OpenEntryCommand, entry),
            entry => (entry.Title, $"v{entry.Version} · {entry.DateLabel}", entry.Summary)));

        column.Children.Add(KnowledgeUi.Separator());
        column.Children.Add(Ui.Caption("Gestures", Ui.Accent, 11));
        column.Children.Add(Ui.Label(
            "Drag an element of the palette onto the canvas: places it where it is dropped. Or click it in the palette, then click the canvas (Shift kept: several).\n" +
            "Drag an element: moves it (snapped to the grid); drag the background: pans; wheel: zooms.\n" +
            "The → handle of the selection, dragged onto another element: joins them (a sequence flow; an association to a data object or an annotation). The ▪ handle resizes.\n" +
            "Double-click or ⏎: renames. Right click: the menu. ⌫: deletes. Arrows: nudge. ⌘Z / ⇧⌘Z: undo, redo. Escape: cancels.",
            Ui.Muted, 10, wrap: true));
        return Ui.Scroll(column);
    }

    /// <summary>
    /// The list of the documents of the search engine (the diagrams, the maps): a row per entry (its title, its version
    /// and date, its comment or its elements), the one open outlined; a click opens one.
    /// </summary>
    internal static Control EntryList<T>(System.ComponentModel.INotifyPropertyChanged vm, System.Collections.ObjectModel.ObservableCollection<T> entries, string empty,
        string selectedProperty, Func<T, bool> isSelected, Action<T> open, Func<T, (string Title, string Date, string Summary)> describe) where T : class
    {
        var list = new ItemsControl
        {
            ItemsSource = entries,
            ItemTemplate = new FuncDataTemplate<T>((entry, _) =>
            {
                if (entry is null)
                    return new Panel();
                var (title, date, summary) = describe(entry);
                var row = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 2),
                    Padding = new Thickness(8, 4),
                    Content = Ui.Column(1,
                        KnowledgeUi.FillRow(6, [], Ui.Label(title, Ui.TextBright, 13), Ui.Label(date, Ui.Muted, 10)),
                        Ui.Label(summary, Ui.Text, 11))
                };
                ToolTip.SetTip(row, summary.Length > 0 ? $"{title}\n{summary}" : title);
                row.Click += (_, _) => open(entry);
                Ui.When(row, vm, () =>
                {
                    var on = isSelected(entry);
                    row.BorderBrush = on ? Ui.Accent : Ui.Edge;
                    row.BorderThickness = new Thickness(on ? 2 : 1);
                }, selectedProperty);
                return row;
            })
        };
        var placeholder = KnowledgeUi.Placeholder(empty);
        void Refresh() => placeholder.IsVisible = entries.Count == 0;
        NotifyCollectionChangedEventHandler changed = (_, _) => Ui.OnMain(Refresh);
        entries.CollectionChanged += changed;
        KnowledgeUi.OnGone(list, () => entries.CollectionChanged -= changed);
        Refresh();
        var box = Ui.Box(Ui.Column(2, list, placeholder), 4, Ui.Field);
        box.MinHeight = 120;
        return box;
    }

    // ── New, open, save ──

    private static async Task NewAsync(BpmnWindowViewModel vm)
    {
        if (vm.IsDirty && !await Dialogs.Confirm("New diagram", "The changes of the diagram are not saved. Start a new diagram anyway?", "New diagram"))
            return;
        vm.NewCommand.Execute(null);
    }

    private static async Task OpenAsync(BpmnWindowViewModel vm)
    {
        if (vm.IsDirty && !await Dialogs.Confirm("Open a diagram", "The changes of the diagram are not saved. Open another diagram anyway?", "Open"))
            return;
        Directory.CreateDirectory(vm.FilesDirectory);
        var path = await Dialogs.OpenFile("Open a BPMN 2.0 diagram", BpmnFilters, Path.GetDirectoryName(vm.FilePath) ?? vm.FilesDirectory);
        if (path is null)
            return;
        await vm.LoadAsync(path);
    }

    private static async Task SaveAsync(BpmnWindowViewModel vm, bool saveAs)
    {
        var path = vm.FilePath;
        if (saveAs || path is null)
        {
            Directory.CreateDirectory(vm.FilesDirectory);
            var suggested = path is not null ? Path.GetFileName(path) : KnowledgeUi.Slug(vm.ProcessName, "diagram") + ".bpmn";
            path = await Dialogs.SaveFile("Save the BPMN 2.0 diagram", suggested, BpmnFilters, Path.GetDirectoryName(path) ?? vm.FilesDirectory);
            if (path is null)
                return;
            if (!Path.HasExtension(path))
                path += ".bpmn";
        }
        await vm.SaveAsync(path);
    }

    /// <summary>The canvas as it is shown, saved as a PNG picture in Pictures/kaonomi/bpmn (the folder is proposed; it is created).</summary>
    private static async Task SavePngAsync(BpmnWindowViewModel vm, BpmnCanvas canvas)
    {
        var suggested = (vm.FilePath is not null ? Path.GetFileNameWithoutExtension(vm.FilePath) : KnowledgeUi.Slug(vm.ProcessName, "diagram")) + ".png";
        await KnowledgeUi.AskAndSavePng(canvas, "Save the diagram as a picture", suggested, vm.PicturesFolder, 1, vm.SetStatus);
    }
}
