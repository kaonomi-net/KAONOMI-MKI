using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// /ontology: the tree of every node of the ontology. The filter applies as you type (all fields, accents ignored;
/// Enter: search now and select the first match; Esc: clear), Search, Clear, Add root node, Expand all, Collapse all,
/// Show deleted, Import (a JSON export under the selected node), Import folder (an Export all folder under the selected node), Export all (every node, one JSON file each, into a chosen folder), ↻ Refresh (F5). A click on a node expands or collapses its children, a double-click (or Enter) opens it
/// in a new Entity window; right click: Edit, Annotate, Add child node, Add root node, Clone, Attach to another node
/// (then click the virtual parent; Esc cancels), Detach (on a virtual link), Export, Import, Delete, Undelete, Undelete
/// with descendants, Show deleted nodes. Ctrl+S / ⌘S saves the selected node.
/// </summary>
[ToolView(typeof(OntologyWindowViewModel), "Ontology", "🌳", 1100, 760)]
public sealed class OntologyView : IToolView
{
    private static readonly string[] JsonFilter = ["*.json ; JSON"];

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (OntologyWindowViewModel)viewModel;

        // The dialogs the view model asks for.
        vm.ConfirmAsync = (message, confirm) => Dialogs.Confirm("Ontology", message, confirm);
        vm.SaveTextFileAsync = SaveTextFileAsync;
        vm.OpenTextFileAsync = OpenTextFileAsync;
        vm.PickFolderAsync = title => Dialogs.OpenFolder(title);

        // The toolbar.
        var term = Ui.Edit(vm, nameof(vm.Term), "Filter nodes as you type (all fields, accents ignored) · Enter: first match · Esc: clear");
        term.MinWidth = 240;
        term.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Ui.Run(vm.SearchCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Ui.Run(vm.ClearSearchCommand);
                e.Handled = true;
            }
        };
        var showDeleted = new ToggleButton { Content = "SHOW DELETED" }.Bind(ToggleButton.IsCheckedProperty, vm, nameof(vm.ShowDeleted), BindingMode.TwoWay);
        var actions = Ui.Row(6,
            Ui.Command("ADD ROOT NODE…", vm.AddRootCommand),
            Ui.Command("EXPAND ALL", vm.ExpandAllCommand),
            Ui.Command("COLLAPSE ALL", vm.CollapseAllCommand),
            showDeleted,
            Ui.Command("IMPORT…", vm.ImportIntoSelectedCommand, tooltip: "Import a JSON export under the selected node (each node gets a new id)"),
            Ui.Command("IMPORT FOLDER…", vm.ImportFolderIntoSelectedCommand, tooltip: "Import a folder exported with Export all under the selected node: the hierarchy is rebuilt, each node gets a new id, deleted nodes are left out"),
            Ui.Command("EXPORT ALL…", vm.ExportAllCommand, tooltip: "Export every node (no filter, deleted ones included) to a folder: one JSON file per node"),
            Ui.Command("↻ REFRESH", vm.RefreshCommand, tooltip: "Reload all nodes from the server (F5). Unsaved changes are lost."));
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        var toolbar = KnowledgeUi.Toolbar(Ui.Column(6,
            KnowledgeUi.FillRow(6, [], term,
                Ui.Command("SEARCH", vm.SearchCommand, tooltip: "Search now (local + server) and select the first match", primary: true),
                Ui.Command("CLEAR", vm.ClearSearchCommand, tooltip: "Show the whole tree again")),
            actions));

        // Attach mode: the next node clicked becomes a virtual parent.
        var prompt = Ui.Bound(vm, nameof(vm.AttachPrompt), Ui.TextBright, 13, wrap: true);
        var banner = new Border
        {
            Background = Paint.Brush(Paint.Alpha(Paint.Cyan, 0.22)),
            BorderBrush = Paint.Brush(Paint.Alpha(Paint.Cyan, 0.8)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(6, 0),
            Child = KnowledgeUi.FillRow(8, [], prompt, Ui.Command("CANCEL", vm.CancelAttachCommand))
        }.Visible(vm, nameof(vm.IsAttaching));

        var tree = OntologyTree.Build(vm);
        var empty = KnowledgeUi.Placeholder("No node · use “Add root node” or right-click a node").Visible(vm, nameof(vm.HasNodes), invert: true);

        // The status bar, and how to use the tree.
        var hint = Ui.Label("· Click a node to expand it · double-click to open it in a new Entity window · right-click: edit, add a child, clone, delete or restore", Ui.Muted, 11);
        hint.MaxWidth = 640;
        var status = KnowledgeUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy), hint);

        var middle = KnowledgeUi.Overlay(Ui.Box(tree, 2, Ui.Field), empty);
        middle.Margin = new Thickness(6, 0);
        var page = Ui.Dock((toolbar, Dock.Top), (banner, Dock.Top), (status, Dock.Bottom), (middle, Dock.Top));
        toolbar.Margin = new Thickness(0, 0, 0, 6);
        status.Margin = new Thickness(0, 6, 0, 0);

        // The Entity windows of this ontology.
        EntityWindows.Attach(vm, shell, page);

        KnowledgeUi.Shortcuts(page, key =>
        {
            if (KnowledgeUi.IsCommand(key, Key.S))
                return KnowledgeUi.TryRun(vm.SelectedNode?.SaveCommand);
            if (key.Key == Key.F5)
                return KnowledgeUi.TryRun(vm.RefreshCommand);
            if (key.Key == Key.Escape && vm.IsAttaching)
                return KnowledgeUi.TryRun(vm.CancelAttachCommand);
            return false;
        });

        // The tree is fetched once, when the window opens.
        Ui.Run(vm.RefreshCommand);
        return page;
    }

    /// <summary>Asks where to save an export and writes it (UTF-8, no BOM); the file name, or null when cancelled.</summary>
    private static async Task<string?> SaveTextFileAsync(string suggestedName, string content)
    {
        var path = await Dialogs.SaveFile("Export ontology nodes", suggestedName, JsonFilter);
        if (string.IsNullOrEmpty(path))
            return null;
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            path += ".json";
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        return Path.GetFileName(path);
    }

    /// <summary>Asks for a JSON file to import and returns its text, or null when cancelled.</summary>
    private static async Task<string?> OpenTextFileAsync()
    {
        var path = await Dialogs.OpenFile("Import ontology nodes", JsonFilter);
        return string.IsNullOrEmpty(path) ? null : await File.ReadAllTextAsync(path);
    }
}
