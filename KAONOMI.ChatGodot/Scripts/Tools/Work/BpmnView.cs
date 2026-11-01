using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Writing;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// /bpmnai: AIMN (from BPMN), the editor of diagrams of AI processes in the BPMN 2.0 notation. Header: the file, NEW, IMPORT, EXPORT, EXPORT AS, UNDO, REDO, DELETE,
/// PEN and "Show clusters" (clusters drawn around elements, as in the graph of relations), FIT, 100 %, PNG, COPY XML. Left: the palette of the basic elements (events, activities, gateways, artifacts), the pointer and
/// the connect tool. Middle: the canvas (<see cref="BpmnCanvas"/>). Right: the properties of the selection (its kind,
/// its id, its name), the name of the process and a comment, then the search engine: SAVE (the diagram, its XML and
/// its comment, in the bpmn index through the Kaonomi server; a new version each time), DELETE, the search of the
/// diagrams and their list (a click opens one), and a reminder of the gestures. Status bar: what was done last, the
/// count of the elements. The diagrams are also saved as BPMN 2.0 XML files (with their diagram interchange) in ~/kaonomi/bpmn.
/// </summary>
[ToolView(typeof(BpmnWindowViewModel), "AIMN (from BPMN)", "🔀", 1240, 780)]
public sealed class BpmnView : IToolView
{
    private static readonly string[] BpmnFilters = ["*.bpmn ; BPMN 2.0 diagram", "*.xml ; XML"];

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (BpmnWindowViewModel)viewModel;
        var overlay = new Overlay();
        var canvas = new BpmnCanvas(vm, overlay);

        // ── The header ──
        var file = EveStyle.Text_("", EveStyle.Muted, 11);
        file.ClipText = true;
        file.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        file.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Bind.When(file, vm, () =>
        {
            file.Text = (vm.FilePath ?? vm.FileName) + (vm.IsDirty ? " •" : "");
            file.TooltipText = vm.FilePath ?? "Not saved yet";
        }, nameof(vm.FilePath), nameof(vm.IsDirty));

        var zoom = EveStyle.Text_("100 %", EveStyle.Muted, 11);
        zoom.CustomMinimumSize = new Vector2(44, 0);
        zoom.HorizontalAlignment = HorizontalAlignment.Right;
        canvas.Draw += () => zoom.Text = $"{canvas.ZoomPercent} %";

        var header = Ui.Toolbar(Ui.Row(8, [
            EveStyle.Caption(vm.EditorTitle, EveStyle.TextBright, 14),
            file,
            Ui.Action("✚ NEW", () => _ = NewAsync(vm), tooltip: "An empty diagram (the changes not saved are asked about)"),
            Ui.Action("📂 IMPORT", () => _ = OpenAsync(vm), tooltip: "Import a BPMN 2.0 file (.bpmn, .xml)"),
            Ui.Action("💾 EXPORT", () => _ = SaveAsync(vm, saveAs: false), primary: true, tooltip: $"Export the diagram as BPMN 2.0 XML (in {vm.FilesDirectory} by default)"),
            Ui.Action("EXPORT AS", () => _ = SaveAsync(vm, saveAs: true), tooltip: "Export the diagram to another file"),
            Ui.Command("↶ UNDO", vm.UndoCommand, tooltip: "Undo the last change (⌘Z)"),
            Ui.Command("↷ REDO", vm.RedoCommand, tooltip: "Redo (⇧⌘Z)"),
            Ui.Command("✕ DELETE", vm.DeleteSelectedCommand, tooltip: "Remove the element or the flow selected (⌫)"),
            // The pen of the clusters and "Show clusters", as in the graph of relations.
            .. Knowledge.ClusterPen.Toolbar(shell, canvas),
            Ui.Action("⤢ FIT", canvas.Fit, tooltip: "The whole diagram in the window (⌘1)"),
            Ui.Action("1:1", canvas.ResetView, tooltip: "Zoom 100 % (⌘0)"),
            zoom,
            Ui.Action("🖼 PNG", () => _ = SavePngAsync(vm, canvas), tooltip: $"Save the canvas as a PNG picture, in {vm.PicturesFolder}"),
            Ui.Action("⎘ COPY XML", () =>
            {
                Dialogs.Copy(vm.ToXml());
                vm.SetStatus("The BPMN 2.0 XML of the diagram is in the clipboard", false);
            }, tooltip: "Copy the BPMN 2.0 XML of the diagram")]));

        // ── The palette ──
        var palette = BuildPalette(vm);

        // ── The properties ──
        var properties = BuildProperties(vm);

        // ── The page ──
        var canvasPanel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        canvasPanel.AddThemeStyleboxOverride("panel", EveStyle.Box(EveStyle.Sunken(0.2f), EveStyle.Edge with { A = 0.45f }, 0));
        canvasPanel.AddChild(canvas);
        var body = Ui.Row(8, palette, canvasPanel, properties);
        body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        var summary = Ui.Label(vm, nameof(vm.Summary), EveStyle.Muted, 11);
        summary.HorizontalAlignment = HorizontalAlignment.Right;
        status.GetChild<HBoxContainer>(0).AddChild(summary);

        // The body takes the whole height between the header and the status bar.
        var page = Ui.Column(0, header, Ui.FillHeight(Ui.Margin(body, 8, 8, 8, 8)), status);
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // The overlay (the right-click menu) over the whole page.
        var root = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(page);
        root.AddChild(overlay);

        Bind.When(root, vm, () =>
        {
            if (shell.WindowOf(root) is { } window)
                window.Title = vm.EditorTitle + " · " + vm.FileName + (vm.IsDirty ? " •" : "");
        }, nameof(vm.FilePath), nameof(vm.IsDirty));
        root.Ready += () => Callable.From(() => canvas.GrabFocus()).CallDeferred();
        return root;
    }

    /// <summary>The palette: the pointer, the connect tool, then the basic elements by group; the tool chosen is lit.</summary>
    private static Control BuildPalette(BpmnWindowViewModel vm)
    {
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(190, 0) };
        column.AddThemeConstantOverride("separation", 3);

        var select = Tool("↖", "Select", "Select, move, pan (Escape)", vm, () => vm.Placing is null && !vm.Connecting, () => vm.SelectToolCommand.Execute(null));
        var connect = Tool("→", "Connect", vm.IsInfrastructure
            ? "Click the source element, then the target: a link (its name: the network, the protocol…), or an association to an annotation"
            : "Click the source element, then the target: a sequence flow, or an association to a data object or an annotation", vm, () => vm.Connecting, vm.ToggleConnect);
        column.AddChild(Ui.Margin(EveStyle.Caption("TOOLS", EveStyle.Cyan, 11), 4, 4, 4, 2));
        column.AddChild(select);
        column.AddChild(connect);

        string? group = null;
        foreach (var kind in vm.Kinds)
        {
            if (kind.Group() != group)
            {
                group = kind.Group();
                column.AddChild(Ui.Margin(EveStyle.Caption(group.ToUpperInvariant(), EveStyle.Cyan, 11), 4, 10, 4, 2));
            }
            var k = kind;
            column.AddChild(Tool(Glyph(kind), kind.Title(), $"Drag it onto the canvas, or click it then click the canvas, to place a {kind.Title().ToLowerInvariant()} (Shift kept: several)", vm, () => vm.Placing == k, () => vm.Place(k), k));
        }

        var tool = Ui.Label(vm, nameof(vm.ToolName), EveStyle.Muted, 10, wrap: true);
        column.AddChild(Ui.Margin(tool, 4, 10, 4, 4));

        var scroll = Ui.Scroll(column);
        scroll.CustomMinimumSize = new Vector2(200, 0);
        // A fixed width on the left: the canvas takes the rest.
        scroll.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return scroll;
    }

    /// <summary>
    /// A button of the palette: its glyph, its name; lit while <paramref name="active"/>. The button of an element
    /// (<paramref name="kind"/>) can be dragged onto the canvas, which places the element where it is dropped.
    /// </summary>
    private static Button Tool(string glyph, string name, string tooltip, BpmnWindowViewModel vm, Func<bool> active, Action pressed, BpmnElementKind? kind = null)
    {
        var button = kind is { } k ? new BpmnPaletteButton(k, glyph) : new Button();
        button.Text = $"{glyph}  {name}";
        // An element of an infrastructure: its photo, small, instead of its emoji.
        if (kind is { } infra && InfraPictures.Of(infra) is { } photo)
        {
            button.Icon = photo;
            button.Text = name;
            button.AddThemeConstantOverride("icon_max_width", 26);
            button.CustomMinimumSize = new Vector2(0, 32);
        }
        button.TooltipText = tooltip;
        button.Alignment = HorizontalAlignment.Left;
        button.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        button.FocusMode = Control.FocusModeEnum.None;
        button.AddThemeFontSizeOverride("font_size", 12);
        button.Pressed += () =>
        {
            if (button is not BpmnPaletteButton { WasDragged: true })
                pressed();
        };
        Bind.When(button, vm, () =>
        {
            if (active())
                EveStyle.StylePrimary(button);
            else
            {
                button.RemoveThemeStyleboxOverride("normal");
                button.RemoveThemeStyleboxOverride("hover");
                button.RemoveThemeStyleboxOverride("pressed");
                button.RemoveThemeColorOverride("font_color");
                button.RemoveThemeColorOverride("font_hover_color");
            }
        }, nameof(vm.Placing), nameof(vm.Connecting));
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
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 8);
        column.AddChild(EveStyle.Caption("SELECTION", EveStyle.Cyan, 11));
        var kind = Ui.Label(vm, nameof(vm.SelectedKind), EveStyle.TextBright, 13);
        kind.AddThemeFontOverride("font", EveStyle.Display);
        column.AddChild(kind);
        var id = Ui.Label(vm, nameof(vm.SelectedId), EveStyle.Muted, 11);
        id.AddThemeFontOverride("font", EveStyle.Mono);
        column.AddChild(id);
        var name = Ui.Line(vm, nameof(vm.SelectedName), "The name (an annotation: its text; a flow: its condition)");
        Bind.Enabled(name, vm, nameof(vm.HasSelection));
        column.AddChild(Ui.Field("Name", name));
        column.AddChild(Ui.Command("✕ DELETE", vm.DeleteSelectedCommand, tooltip: "Remove the selection (⌫)"));

        column.AddChild(Ui.Margin(EveStyle.Separator(), 0, 6, 0, 2));
        column.AddChild(EveStyle.Caption(vm.Subject.ToUpperInvariant(), EveStyle.Cyan, 11));
        column.AddChild(Ui.Field($"Name of the {vm.Subject} (the title of the diagram)", Ui.Line(vm, nameof(vm.ProcessName), vm.IsInfrastructure ? "Infrastructure" : "Process")));
        column.AddChild(Ui.Field("Comment", Ui.Lines(vm, nameof(vm.Description), 90, "A comment on the diagram: what it models, its state, its questions… saved with it in the search engine")));

        // ── The search engine: the diagram saved there, the diagrams found ──
        column.AddChild(Ui.Margin(EveStyle.Separator(), 0, 6, 0, 2));
        column.AddChild(EveStyle.Caption("SEARCH ENGINE", EveStyle.Cyan, 11));
        var info = Ui.Label(vm, nameof(vm.DiagramInfo), EveStyle.Muted, 11);
        info.ClipText = true;
        info.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        column.AddChild(info);
        column.AddChild(Ui.Row(6,
            Ui.Command("☁ SAVE", vm.SaveToServerCommand, primary: true, tooltip: "Save the diagram (its XML and its comment) in the search engine: a new document the first time (its id the slug of the name of the process), a new version after"),
            Ui.Command("✕ DELETE", vm.DeleteFromServerCommand, tooltip: "Mark the diagram deleted in the search engine (it is never erased; the canvas keeps it)")));
        var search = Ui.Line(vm, nameof(vm.Search), "Search the diagrams (title, comment, elements)… · Enter");
        search.TextSubmitted += _ => Bind.Run(vm.SearchCommand);
        column.AddChild(Ui.Row(6, search, Ui.Command("⟳", vm.SearchCommand, tooltip: "Search (Enter); empty: every diagram, by title")));
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var noDiagram = Ui.Placeholder("No diagram in the search engine yet");
        Bind.Items(list, vm.Diagrams, entry => DiagramRow(vm, entry), noDiagram);
        var listScroll = Ui.Scroll(Ui.Column(2, list, noDiagram));
        listScroll.CustomMinimumSize = new Vector2(0, 120);
        listScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        column.AddChild(listScroll);

        column.AddChild(Ui.Margin(EveStyle.Separator(), 0, 6, 0, 2));
        column.AddChild(EveStyle.Caption("GESTURES", EveStyle.Cyan, 11));
        var help = EveStyle.Text_(
            "Drag an element of the palette onto the canvas: places it where it is dropped. Or click it in the palette, then click the canvas (Shift kept: several).\n" +
            "Drag an element: moves it (snapped to the grid); drag the background: pans; wheel or pinch: zooms.\n" +
            "The → handle of the selection, dragged onto another element: joins them (a sequence flow; an association to a data object or an annotation). The ▪ handle resizes.\n" +
            "Double-click or ⏎: renames. Right click: the menu. ⌫: deletes. Arrows: nudge. ⌘Z / ⇧⌘Z: undo, redo. Escape: cancels.",
            EveStyle.Muted, 10, wrap: true);
        column.AddChild(help);

        // A fixed width on the right: the canvas takes the rest.
        column.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        return column;
    }

    /// <summary>A diagram of the search engine in the list: its title, its version and date, its comment or its elements; the one open is outlined.</summary>
    private static Control DiagramRow(BpmnWindowViewModel vm, BpmnEntry entry)
    {
        var title = EveStyle.Text_(entry.Title, EveStyle.TextBright, 13);
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var date = EveStyle.Text_($"v{entry.Version} · {entry.DateLabel}", EveStyle.Muted, 10);
        var summary = WritingUi.Clamp(EveStyle.Text_(entry.Summary, EveStyle.Text with { A = 0.7f }, 11), 1);
        var content = Ui.Column(1, Ui.Row(6, title, date), summary);
        var button = WritingUi.ResultRow(vm, nameof(vm.DiagramId), () => vm.DiagramId is not null && vm.DiagramId == entry.Id,
            () => Bind.Run(vm.OpenEntryCommand, entry), content, 50);
        button.TooltipText = entry.Summary.Length > 0 ? $"{entry.Title}\n{entry.Summary}" : entry.Title;
        return button;
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
            var suggested = path is not null ? Path.GetFileName(path) : Slug(vm.ProcessName) + ".bpmn";
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
        Directory.CreateDirectory(vm.PicturesFolder);
        var suggested = (vm.FilePath is not null ? Path.GetFileNameWithoutExtension(vm.FilePath) : Slug(vm.ProcessName)) + ".png";
        var path = await Dialogs.SaveFile("Save the diagram as a picture", suggested, ["*.png ; PNG picture"], vm.PicturesFolder);
        if (path is null)
            return;
        // The viewport of the window, cropped to the canvas (each window of the desktop has a viewport of its own).
        await canvas.ToSignal(canvas.GetTree(), SceneTree.SignalName.ProcessFrame);
        var image = canvas.GetViewport().GetTexture().GetImage();
        var rect = canvas.GetGlobalRect();
        var region = new Rect2I((Vector2I)rect.Position, (Vector2I)rect.Size).Intersection(new Rect2I(Vector2I.Zero, image.GetSize()));
        if (region.Size.X <= 0 || region.Size.Y <= 0)
        {
            vm.SetStatus("The picture could not be taken", true);
            return;
        }
        var error = image.GetRegion(region).SavePng(path);
        vm.SetStatus(error == Error.Ok ? $"Picture saved: {path}" : $"Picture not saved: {error}", error != Error.Ok);
    }

    private static string Slug(string name)
    {
        var chars = new List<char>();
        foreach (var c in name.Trim().ToLowerInvariant())
            chars.Add(char.IsLetterOrDigit(c) ? c : '-');
        var slug = new string(chars.ToArray()).Trim('-');
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        return slug.Length == 0 ? "diagram" : slug;
    }
}

/// <summary>
/// /infra: the editor of infrastructure diagrams, the window of /bpmnai with the machines, devices and services of an
/// installation in its palette (an LLM in the cloud, the LLM gateway, a DGX Spark, PCs and Macs, servers, a Raspberry
/// Pi, an Arduino, the Pepper robot, phones and tablets, cameras, microphones, sensors), joined by links.
/// </summary>
[ToolView(typeof(InfraWindowViewModel), "Infrastructure", "🏗", 1240, 780)]
public sealed class InfraView : IToolView
{
    public Control Build(object viewModel, Shell shell) => new BpmnView().Build(viewModel, shell);
}

/// <summary>
/// A button of the palette of /bpmnai that can be dragged onto the canvas (<see cref="BpmnCanvas"/>): the drag carries
/// the kind of the element, a card with its glyph and its name follows the pointer, and the canvas places the element
/// where it is dropped. A click (no drag) still chooses the kind to place with the next click on the canvas.
/// </summary>
public partial class BpmnPaletteButton : Button
{
    private readonly BpmnElementKind _kind;
    private readonly string _glyph;

    /// <summary>True from the start of a drag to the frame after its end: the click of the button is not a choice then.</summary>
    public bool WasDragged { get; private set; }

    public BpmnPaletteButton(BpmnElementKind kind, string glyph)
    {
        _kind = kind;
        _glyph = glyph;
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        WasDragged = true;
        var card = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        card.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.PanelSolid, Eve.Eve.Cyan, 8));
        var text = Eve.Eve.Text_($"{_glyph}  {_kind.Title()}", Eve.Eve.TextBright, 12);
        text.MouseFilter = MouseFilterEnum.Ignore;
        card.AddChild(text);
        card.Modulate = new Color(1, 1, 1, 0.9f);
        SetDragPreview(card);
        return BpmnCanvas.DragData(_kind);
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationDragEnd)
            Callable.From(() => WasDragged = false).CallDeferred();
    }
}
