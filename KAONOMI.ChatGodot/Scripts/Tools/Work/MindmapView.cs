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
/// /mindmap: the mind mapping window, with the window of /bpmnai as its model. Header: the file, NEW, IMPORT, EXPORT,
/// EXPORT AS, UNDO, REDO, CHILD, SIBLING, DELETE, LAYOUT, PEN and "Show clusters" (clusters drawn around topics, as in
/// the graph of relations), FIT, 100 %, PNG (in Pictures/kaonomi/mindmap), COPY XML.
/// Middle: the canvas (<see cref="MindmapCanvas"/>). Right: the topic selected (its text, its note, the colour of its
/// branch), the map (its title, a comment), the search engine (SAVE, DELETE, the search of the maps and their list)
/// and a reminder of the gestures. Status bar: what was done last, the count of the topics. The maps are saved as
/// FreeMind XML (.mm, read by FreeMind, Freeplane, XMind) in ~/kaonomi/mindmap.
/// </summary>
[ToolView(typeof(MindmapWindowViewModel), "Mind map", "🧠", 1240, 780)]
public sealed class MindmapView : IToolView
{
    private static readonly string[] MapFilters = ["*.mm ; FreeMind mind map", "*.xml ; XML"];

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (MindmapWindowViewModel)viewModel;
        var overlay = new Overlay();
        var canvas = new MindmapCanvas(vm, overlay);

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
            EveStyle.Caption("Mind map", EveStyle.TextBright, 14),
            file,
            Ui.Action("✚ NEW", () => _ = NewAsync(vm), tooltip: "A new map, its central topic alone (the changes not saved are asked about)"),
            Ui.Action("📂 IMPORT", () => _ = OpenAsync(vm), tooltip: "Import a FreeMind file (.mm)"),
            Ui.Action("💾 EXPORT", () => _ = SaveAsync(vm, saveAs: false), primary: true, tooltip: "Export the map as FreeMind XML (in ~/kaonomi/mindmap by default)"),
            Ui.Action("EXPORT AS", () => _ = SaveAsync(vm, saveAs: true), tooltip: "Export the map to another file"),
            Ui.Command("↶ UNDO", vm.UndoCommand, tooltip: "Undo the last change (⌘Z)"),
            Ui.Command("↷ REDO", vm.RedoCommand, tooltip: "Redo (⇧⌘Z)"),
            Ui.Command("✚ CHILD", vm.AddChildOfSelectionCommand, tooltip: "Add a topic under the selection (Tab)"),
            Ui.Command("✚ SIBLING", vm.AddSiblingOfSelectionCommand, tooltip: "Add a topic beside the selection (Enter)"),
            Ui.Command("✕ DELETE", vm.DeleteSelectedCommand, tooltip: "Remove the topic selected and its branch (⌫)"),
            Ui.Command("⟲ LAYOUT", vm.LayoutCommand, tooltip: "Lay the whole map out again around the central topic"),
            // The pen of the clusters and "Show clusters", as in the graph of relations.
            .. Knowledge.ClusterPen.Toolbar(shell, canvas),
            Ui.Action("⤢ FIT", canvas.Fit, tooltip: "The whole map in the window (⌘1)"),
            Ui.Action("1:1", canvas.ResetView, tooltip: "Zoom 100 %, the central topic in the middle (⌘0)"),
            zoom,
            Ui.Action("🖼 PNG", () => _ = SavePngAsync(vm, canvas), tooltip: "Save the canvas as a PNG picture, in Pictures/kaonomi/mindmap"),
            Ui.Action("⎘ COPY XML", () =>
            {
                Dialogs.Copy(vm.ToXml());
                vm.SetStatus("The FreeMind XML of the map is in the clipboard", false);
            }, tooltip: "Copy the FreeMind XML of the map")]));

        // ── The page ──
        var canvasPanel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        canvasPanel.AddThemeStyleboxOverride("panel", EveStyle.Box(EveStyle.Sunken(0.2f), EveStyle.Edge with { A = 0.45f }, 0));
        canvasPanel.AddChild(canvas);
        var body = Ui.Row(8, canvasPanel, BuildProperties(vm));
        body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        var summary = Ui.Label(vm, nameof(vm.Summary), EveStyle.Muted, 11);
        summary.HorizontalAlignment = HorizontalAlignment.Right;
        status.GetChild<HBoxContainer>(0).AddChild(summary);

        var page = Ui.Column(0, header, Ui.FillHeight(Ui.Margin(body, 8, 8, 8, 8)), status);
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var root = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(page);
        root.AddChild(overlay);

        Bind.When(root, vm, () =>
        {
            if (shell.WindowOf(root) is { } window)
                window.Title = "Mind map · " + (vm.Title.Trim().Length > 0 ? vm.Title.Trim() : vm.FileName) + (vm.IsDirty ? " •" : "");
        }, nameof(vm.FilePath), nameof(vm.IsDirty), nameof(vm.Title));
        root.Ready += () => Callable.From(() => canvas.GrabFocus()).CallDeferred();
        return root;
    }

    /// <summary>The topic selected, the map, the search engine, the gestures.</summary>
    private static Control BuildProperties(MindmapWindowViewModel vm)
    {
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 8);

        column.AddChild(EveStyle.Caption("TOPIC", EveStyle.Cyan, 11));
        var info = Ui.Label(vm, nameof(vm.SelectedInfo), EveStyle.Muted, 11);
        column.AddChild(info);
        var text = Ui.Line(vm, nameof(vm.SelectedText), "The text of the topic");
        Bind.Enabled(text, vm, nameof(vm.HasSelection));
        column.AddChild(Ui.Field("Text", text));
        var note = Ui.Lines(vm, nameof(vm.SelectedNote), 60, "A note on the topic (kept in the file)");
        Bind.Enabled(note, vm, nameof(vm.HasSelection));
        column.AddChild(Ui.Field("Note", note));
        // The colours of the branches (for a child of the root).
        var colours = new HBoxContainer();
        colours.AddThemeConstantOverride("separation", 4);
        foreach (var color in MindmapDiagram.Palette)
        {
            var c = color;
            var swatch = new Button { CustomMinimumSize = new Vector2(24, 20), TooltipText = $"Colour of the branch: {c}", MouseDefaultCursorShape = Control.CursorShape.PointingHand, FocusMode = Control.FocusModeEnum.None };
            swatch.AddThemeStyleboxOverride("normal", EveStyle.Box(Color.FromHtml(c), Color.FromHtml(c), 4));
            swatch.AddThemeStyleboxOverride("hover", EveStyle.Box(Color.FromHtml(c), Colors.White, 4));
            swatch.AddThemeStyleboxOverride("pressed", EveStyle.Box(Color.FromHtml(c), Colors.White, 4));
            swatch.Pressed += () =>
            {
                if (vm.Selected is { } node)
                    vm.SetColor(node, c);
            };
            colours.AddChild(swatch);
        }
        var coloursField = Ui.Field("Colour of the branch", colours);
        Bind.Visible(coloursField, vm, nameof(vm.IsBranchSelected));
        column.AddChild(coloursField);
        column.AddChild(Ui.Row(6,
            Ui.Command("✚ CHILD", vm.AddChildOfSelectionCommand, tooltip: "Add a topic under the selection (Tab)"),
            Ui.Command("✚ SIBLING", vm.AddSiblingOfSelectionCommand, tooltip: "Add a topic beside the selection (Enter)"),
            Ui.Command("✕ DELETE", vm.DeleteSelectedCommand, tooltip: "Remove the topic and its branch (⌫)")));

        column.AddChild(Ui.Margin(EveStyle.Separator(), 0, 6, 0, 2));
        column.AddChild(EveStyle.Caption("MAP", EveStyle.Cyan, 11));
        column.AddChild(Ui.Field("Central topic (the title of the map)", Ui.Line(vm, nameof(vm.Title), "Central topic")));
        column.AddChild(Ui.Field("Comment", Ui.Lines(vm, nameof(vm.Description), 70, "A comment on the map: what it is about, its state… saved with it in the search engine")));

        column.AddChild(Ui.Margin(EveStyle.Separator(), 0, 6, 0, 2));
        column.AddChild(EveStyle.Caption("SEARCH ENGINE", EveStyle.Cyan, 11));
        var engine = Ui.Label(vm, nameof(vm.MapInfo), EveStyle.Muted, 11);
        engine.ClipText = true;
        engine.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        column.AddChild(engine);
        column.AddChild(Ui.Row(6,
            Ui.Command("☁ SAVE", vm.SaveToServerCommand, primary: true, tooltip: "Save the map (its XML and its comment) in the search engine: a new document the first time (its id the slug of the central topic), a new version after"),
            Ui.Command("✕ DELETE", vm.DeleteFromServerCommand, tooltip: "Mark the map deleted in the search engine (it is never erased; the canvas keeps it)")));
        var search = Ui.Line(vm, nameof(vm.Search), "Search the maps (title, comment, topics)… · Enter");
        search.TextSubmitted += _ => Bind.Run(vm.SearchCommand);
        column.AddChild(Ui.Row(6, search, Ui.Command("⟳", vm.SearchCommand, tooltip: "Search (Enter); empty: every map, by title")));
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var noMap = Ui.Placeholder("No map in the search engine yet");
        Bind.Items(list, vm.Maps, entry => MapRow(vm, entry), noMap);
        var listScroll = Ui.Scroll(Ui.Column(2, list, noMap));
        listScroll.CustomMinimumSize = new Vector2(0, 100);
        listScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        column.AddChild(listScroll);

        column.AddChild(Ui.Margin(EveStyle.Separator(), 0, 6, 0, 2));
        column.AddChild(EveStyle.Caption("GESTURES", EveStyle.Cyan, 11));
        column.AddChild(EveStyle.Text_(
            "Tab or the + handle: a child of the selection; Enter: a sibling; F2 or double-click: rename.\n" +
            "Drag a topic: moves it with its branch; dropped onto another topic: it moves under it. Drag the background: pans; wheel or pinch: zooms.\n" +
            "Right click: the menu (note, colour of the branch…). ⌫: deletes the branch. Arrows: nudge. ⌘Z / ⇧⌘Z: undo, redo.",
            EveStyle.Muted, 10, wrap: true));

        column.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        return column;
    }

    /// <summary>A map of the search engine in the list: its title, its version and date, its comment or its topics; the one open is outlined.</summary>
    private static Control MapRow(MindmapWindowViewModel vm, MindmapEntry entry)
    {
        var title = EveStyle.Text_(entry.Title, EveStyle.TextBright, 13);
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var date = EveStyle.Text_($"v{entry.Version} · {entry.DateLabel}", EveStyle.Muted, 10);
        var summary = WritingUi.Clamp(EveStyle.Text_(entry.Summary, EveStyle.Text with { A = 0.7f }, 11), 1);
        var content = Ui.Column(1, Ui.Row(6, title, date), summary);
        var button = WritingUi.ResultRow(vm, nameof(vm.MapId), () => vm.MapId is not null && vm.MapId == entry.Id,
            () => Bind.Run(vm.OpenEntryCommand, entry), content, 50);
        button.TooltipText = entry.Summary.Length > 0 ? $"{entry.Title}\n{entry.Summary}" : entry.Title;
        return button;
    }

    // ── New, open, save ──

    private static async Task NewAsync(MindmapWindowViewModel vm)
    {
        if (vm.IsDirty && !await Dialogs.Confirm("New map", "The changes of the map are not saved. Start a new map anyway?", "New map"))
            return;
        vm.NewCommand.Execute(null);
    }

    private static async Task OpenAsync(MindmapWindowViewModel vm)
    {
        if (vm.IsDirty && !await Dialogs.Confirm("Open a map", "The changes of the map are not saved. Open another map anyway?", "Open"))
            return;
        Directory.CreateDirectory(MindmapWindowViewModel.DefaultDirectory);
        var path = await Dialogs.OpenFile("Open a mind map (FreeMind)", MapFilters, Path.GetDirectoryName(vm.FilePath) ?? MindmapWindowViewModel.DefaultDirectory);
        if (path is null)
            return;
        await vm.LoadAsync(path);
    }

    private static async Task SaveAsync(MindmapWindowViewModel vm, bool saveAs)
    {
        var path = vm.FilePath;
        if (saveAs || path is null)
        {
            Directory.CreateDirectory(MindmapWindowViewModel.DefaultDirectory);
            var suggested = path is not null ? Path.GetFileName(path) : Slug(vm.Title) + ".mm";
            path = await Dialogs.SaveFile("Save the mind map (FreeMind)", suggested, MapFilters, Path.GetDirectoryName(path) ?? MindmapWindowViewModel.DefaultDirectory);
            if (path is null)
                return;
            if (!Path.HasExtension(path))
                path += ".mm";
        }
        await vm.SaveAsync(path);
    }

    /// <summary>The canvas as it is shown, saved as a PNG picture in Pictures/kaonomi/mindmap.</summary>
    private static async Task SavePngAsync(MindmapWindowViewModel vm, MindmapCanvas canvas)
    {
        Directory.CreateDirectory(MindmapWindowViewModel.PicturesDirectory);
        var suggested = (vm.FilePath is not null ? Path.GetFileNameWithoutExtension(vm.FilePath) : Slug(vm.Title)) + ".png";
        var path = await Dialogs.SaveFile("Save the map as a picture", suggested, ["*.png ; PNG picture"], MindmapWindowViewModel.PicturesDirectory);
        if (path is null)
            return;
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
        return slug.Length == 0 ? "map" : slug;
    }
}
