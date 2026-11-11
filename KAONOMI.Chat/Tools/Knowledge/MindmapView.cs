using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// /mindmap: the mind mapping window, with the window of /bpmnai as its model. Header: the file, NEW, IMPORT, EXPORT,
/// EXPORT AS, UNDO, REDO, CHILD, SIBLING, DELETE, LAYOUT, PEN and "Show clusters" (clusters drawn around topics, as in
/// the graph of relations), FIT, 1:1, the zoom, PNG (in Pictures/kaonomi/mindmap), COPY XML. Middle: the canvas
/// (<see cref="MindmapCanvas"/>). Right: the topic selected (its text, its note, the colour of its branch), the map (its
/// title, a comment), the search engine (SAVE, DELETE, the search of the maps and their list) and a reminder of the
/// gestures. Status bar: what was done last, the count of the topics. The maps are saved as FreeMind XML (.mm, read by
/// FreeMind, Freeplane, XMind) in ~/kaonomi/mindmap.
/// </summary>
[ToolView(typeof(MindmapWindowViewModel), "Mind map", "🧠", 1240, 780)]
public sealed class MindmapView : IToolView
{
    private static readonly string[] MapFilters = ["*.mm ; FreeMind mind map", "*.xml ; XML"];

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (MindmapWindowViewModel)viewModel;
        var canvas = new MindmapCanvas(vm);

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

        var buttons = KnowledgeUi.Flow(6,
            Ui.Button("✚ NEW", () => _ = NewAsync(vm), "A new map, its central topic alone (the changes not saved are asked about)"),
            Ui.Button("📂 IMPORT", () => _ = OpenAsync(vm), "Import a FreeMind file (.mm)"),
            Ui.Button("💾 EXPORT", () => _ = SaveAsync(vm, saveAs: false), "Export the map as FreeMind XML (in ~/kaonomi/mindmap by default)", primary: true),
            Ui.Button("EXPORT AS", () => _ = SaveAsync(vm, saveAs: true), "Export the map to another file"),
            Ui.Command("↶ UNDO", vm.UndoCommand, tooltip: "Undo the last change (⌘Z)"),
            Ui.Command("↷ REDO", vm.RedoCommand, tooltip: "Redo (⇧⌘Z)"),
            Ui.Command("✚ CHILD", vm.AddChildOfSelectionCommand, tooltip: "Add a topic under the selection (Tab)"),
            Ui.Command("✚ SIBLING", vm.AddSiblingOfSelectionCommand, tooltip: "Add a topic beside the selection (Enter)"),
            Ui.Command("✕ DELETE", vm.DeleteSelectedCommand, tooltip: "Remove the topic selected and its branch (⌫)"),
            Ui.Command("⟲ LAYOUT", vm.LayoutCommand, tooltip: "Lay the whole map out again around the central topic"));
        // The pen of the clusters and "Show clusters", as in the graph of relations.
        foreach (var control in ClusterPen.Toolbar(shell, canvas))
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            buttons.Children.Add(control);
        }
        foreach (var control in new Control[]
                 {
                     Ui.Button("⤢ FIT", canvas.Fit, "The whole map in the window (⌘1)"),
                     Ui.Button("1:1", canvas.ResetView, "Zoom 100 %, the central topic in the middle (⌘0)"),
                     zoom,
                     Ui.Button("🖼 PNG", () => _ = SavePngAsync(vm, canvas), "Save the canvas as a PNG picture, in Pictures/kaonomi/mindmap"),
                     Ui.Button("⎘ COPY XML", () =>
                     {
                         Dialogs.Copy(vm.ToXml());
                         vm.SetStatus("The FreeMind XML of the map is in the clipboard", false);
                     }, "Copy the FreeMind XML of the map")
                 })
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            buttons.Children.Add(control);
        }
        var title = Ui.Label("MIND MAP", Ui.TextBright, 14, bold: true);
        var header = KnowledgeUi.Toolbar(Ui.Column(4, KnowledgeUi.FillRow(10, [title], file), buttons));

        // ── The page ──
        var canvasFrame = Ui.Box(canvas, 0, Ui.Field);
        var properties = BuildProperties(vm);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300"), ColumnSpacing = 8, Margin = new Thickness(8) };
        Grid.SetColumn(properties, 1);
        body.Children.Add(canvasFrame);
        body.Children.Add(properties);

        var summary = Ui.Bound(vm, nameof(vm.Summary), Ui.Muted, 11);
        var status = KnowledgeUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy), summary);
        var page = Ui.Dock((header, Dock.Top), (status, Dock.Bottom), (body, Dock.Top));

        KnowledgeUi.FollowTitle(page, vm, () => "Mind map · " + (vm.Title.Trim().Length > 0 ? vm.Title.Trim() : vm.FileName) + (vm.IsDirty ? " •" : ""),
            nameof(vm.FilePath), nameof(vm.IsDirty), nameof(vm.Title));
        KnowledgeUi.FocusLater(canvas);
        return page;
    }

    /// <summary>The topic selected, the map, the search engine, the gestures.</summary>
    private static Control BuildProperties(MindmapWindowViewModel vm)
    {
        var column = new StackPanel { Spacing = 8 };

        column.Children.Add(Ui.Caption("Topic", Ui.Accent, 11));
        column.Children.Add(Ui.Bound(vm, nameof(vm.SelectedInfo), Ui.Muted, 11));
        var text = Ui.Edit(vm, nameof(vm.SelectedText), "The text of the topic");
        text.Bind(InputElement.IsEnabledProperty, vm, nameof(vm.HasSelection));
        column.Children.Add(KnowledgeUi.Field("Text", text));
        var note = Ui.Edit(vm, nameof(vm.SelectedNote), "A note on the topic (kept in the file)", multiline: true, height: 60);
        note.Bind(InputElement.IsEnabledProperty, vm, nameof(vm.HasSelection));
        column.Children.Add(KnowledgeUi.Field("Note", note));
        // The colours of the branches (for a child of the root).
        var colours = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
        foreach (var color in MindmapDiagram.Palette)
        {
            var c = color;
            var brush = Color.TryParse(c, out var parsed) ? Paint.Brush(parsed) : Ui.Accent;
            var swatch = new Button { Width = 24, Height = 20, MinHeight = 0, Padding = new Thickness(0), Background = brush, BorderBrush = brush, Focusable = false, Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(swatch, $"Colour of the branch: {c}");
            swatch.Click += (_, _) =>
            {
                if (vm.Selected is { } node)
                    vm.SetColor(node, c);
            };
            colours.Children.Add(swatch);
        }
        column.Children.Add(KnowledgeUi.Field("Colour of the branch", colours).Visible(vm, nameof(vm.IsBranchSelected)));
        column.Children.Add(KnowledgeUi.Flow(6,
            Ui.Command("✚ CHILD", vm.AddChildOfSelectionCommand, tooltip: "Add a topic under the selection (Tab)"),
            Ui.Command("✚ SIBLING", vm.AddSiblingOfSelectionCommand, tooltip: "Add a topic beside the selection (Enter)"),
            Ui.Command("✕ DELETE", vm.DeleteSelectedCommand, tooltip: "Remove the topic and its branch (⌫)")));

        column.Children.Add(KnowledgeUi.Separator());
        column.Children.Add(Ui.Caption("Map", Ui.Accent, 11));
        column.Children.Add(KnowledgeUi.Field("Central topic (the title of the map)", Ui.Edit(vm, nameof(vm.Title), "Central topic")));
        column.Children.Add(KnowledgeUi.Field("Comment", Ui.Edit(vm, nameof(vm.Description), "A comment on the map: what it is about, its state… saved with it in the search engine", multiline: true, height: 70)));

        column.Children.Add(KnowledgeUi.Separator());
        column.Children.Add(Ui.Caption("Search engine", Ui.Accent, 11));
        column.Children.Add(Ui.Bound(vm, nameof(vm.MapInfo), Ui.Muted, 11));
        column.Children.Add(Ui.Row(6,
            Ui.Command("☁ SAVE", vm.SaveToServerCommand, tooltip: "Save the map (its XML and its comment) in the search engine: a new document the first time (its id the slug of the central topic), a new version after", primary: true),
            Ui.Command("✕ DELETE", vm.DeleteFromServerCommand, tooltip: "Mark the map deleted in the search engine (it is never erased; the canvas keeps it)")));
        var search = Ui.Edit(vm, nameof(vm.Search), "Search the maps (title, comment, topics)… · Enter");
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Ui.Run(vm.SearchCommand);
                e.Handled = true;
            }
        };
        column.Children.Add(KnowledgeUi.FillRow(6, [], search, Ui.Command("⟳", vm.SearchCommand, tooltip: "Search (Enter); empty: every map, by title")));
        column.Children.Add(BpmnView.EntryList(vm, vm.Maps, "No map in the search engine yet", nameof(vm.MapId),
            entry => vm.MapId is not null && vm.MapId == entry.Id, entry => Ui.Run(vm.OpenEntryCommand, entry),
            entry => (entry.Title, $"v{entry.Version} · {entry.DateLabel}", entry.Summary)));

        column.Children.Add(KnowledgeUi.Separator());
        column.Children.Add(Ui.Caption("Gestures", Ui.Accent, 11));
        column.Children.Add(Ui.Label(
            "Tab or the + handle: a child of the selection; Enter: a sibling; F2 or double-click: rename.\n" +
            "Drag a topic: moves it with its branch; dropped onto another topic: it moves under it. Drag the background: pans; wheel: zooms.\n" +
            "Right click: the menu (note, colour of the branch…). ⌫: deletes the branch. Arrows: nudge. ⌘Z / ⇧⌘Z: undo, redo.",
            Ui.Muted, 10, wrap: true));
        return Ui.Scroll(column);
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
            var suggested = path is not null ? Path.GetFileName(path) : KnowledgeUi.Slug(vm.Title, "map") + ".mm";
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
        var suggested = (vm.FilePath is not null ? Path.GetFileNameWithoutExtension(vm.FilePath) : KnowledgeUi.Slug(vm.Title, "map")) + ".png";
        await KnowledgeUi.AskAndSavePng(canvas, "Save the map as a picture", suggested, MindmapWindowViewModel.PicturesDirectory, 1, vm.SetStatus);
    }
}
