using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// /ontology (port of <c>OntologyView.axaml</c>): the tree of every node of the ontology. The filter applies as you type
/// (all fields, accents ignored; Enter: search now and select the first match; Esc: clear), Search, Clear, Add root
/// node, Expand all, Collapse all, Show deleted, ↻ Refresh (F5). A click on a node expands or collapses its children, a
/// double-click (or Enter) opens it in a new Entity window; right click: Edit, Add child node, Add root node, Clone,
/// Attach to another node (then click the virtual parent; Esc cancels), Detach (on a virtual link), Export, Import,
/// Delete, Undelete, Undelete with descendants, Show deleted nodes. Ctrl+S / ⌘S saves the selected node.
/// </summary>
[ToolView(typeof(OntologyWindowViewModel), "Ontology", "🌳", 1100, 760)]
public sealed class OntologyView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (OntologyWindowViewModel)viewModel;

        // The dialogs the view model asks for.
        vm.ConfirmAsync = (message, confirm) => Dialogs.Confirm("Ontology", message, confirm);
        vm.SaveTextFileAsync = SaveTextFileAsync;
        vm.OpenTextFileAsync = OpenTextFileAsync;

        // The toolbar.
        var term = Ui.Line(vm, nameof(vm.Term), "Filter nodes as you type (all fields, accents ignored) · Enter: first match · Esc: clear");
        term.TextSubmitted += _ => Bind.Run(vm.SearchCommand);
        term.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                Bind.Run(vm.ClearSearchCommand);
                term.AcceptEvent();
            }
        };
        var showDeleted = Bind.Check(Eve.Eve.Button("SHOW DELETED"), vm, nameof(vm.ShowDeleted));
        term.CustomMinimumSize = new Vector2(240, 0);
        var toolbar = Ui.Toolbar(Ui.Column(6, Ui.Row(6,
            term,
            Ui.Command("SEARCH", vm.SearchCommand, primary: true, tooltip: "Search now (local + server) and select the first match"),
            Ui.Command("CLEAR", vm.ClearSearchCommand, tooltip: "Show the whole tree again")), Ui.Row(6,
            Ui.Spring(),
            Ui.Command("ADD ROOT NODE…", vm.AddRootCommand),
            Ui.Command("EXPAND ALL", vm.ExpandAllCommand),
            Ui.Command("COLLAPSE ALL", vm.CollapseAllCommand),
            showDeleted,
            Ui.Command("↻ REFRESH", vm.RefreshCommand, tooltip: "Reload all nodes from the server (F5). Unsaved changes are lost."))));

        // Attach mode: the next node clicked becomes a virtual parent.
        var banner = new PanelContainer();
        banner.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Cyan with { A = 0.22f }, Eve.Eve.Cyan with { A = 0.8f }, 8));
        var prompt = Ui.Label(vm, nameof(vm.AttachPrompt), Eve.Eve.TextBright, 13, wrap: true);
        prompt.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        banner.AddChild(Ui.Row(8, prompt, Ui.Command("CANCEL", vm.CancelAttachCommand)));
        Bind.Visible(banner, vm, nameof(vm.IsAttaching));

        var tree = new OntologyTree(vm);
        var empty = Ui.Placeholder("No node · use “Add root node” or right-click a node");
        empty.MouseFilter = Control.MouseFilterEnum.Ignore;
        Bind.Visible(empty, vm, nameof(vm.HasNodes), invert: true);

        // The status bar, and how to use the tree.
        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        var hint = Eve.Eve.Text_("· Click a node to expand it · double-click to open it in a new Entity window · right-click: edit, add a child, clone, delete or restore", Eve.Eve.Muted, 11);
        hint.ClipText = true;
        hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        hint.SizeFlagsStretchRatio = 1.4f;
        if (status.GetChild(0) is HBoxContainer statusRow)
            statusRow.AddChild(hint);

        var page = Ui.Column(6, toolbar, banner, Ui.FillHeight(KnowledgeUi.Overlay(tree, empty)), status);

        // The Entity windows of this ontology.
        EntityWindows.Attach(vm, shell, page);

        KnowledgeUi.Shortcuts(page, key =>
        {
            if (KnowledgeUi.IsCommand(key, Key.S))
                return KnowledgeUi.TryRun(vm.SelectedNode?.SaveCommand);
            if (key.Keycode == Key.F5)
                return KnowledgeUi.TryRun(vm.RefreshCommand);
            if (key.Keycode == Key.Escape && vm.IsAttaching)
                return KnowledgeUi.TryRun(vm.CancelAttachCommand);
            return false;
        });

        // The tree is fetched once, when the window opens.
        Bind.Run(vm.RefreshCommand);
        return page;
    }

    private static readonly string[] JsonFilter = ["*.json ; JSON"];

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
