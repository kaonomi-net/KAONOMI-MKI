using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// The tree of the ontology (a Godot <see cref="Tree"/>), kept in sync with <see cref="OntologyWindowViewModel.RootItems"/>:
/// one row per occurrence (a node with virtual parents shows at each place it is attached), ◆ for a class and ● for an
/// instance, its name (bold cyan when it matches the filter, italic under a virtual link, faded when deleted), then its
/// entity type and its badges (⇢ linked, deleted, new, modified, orphan). The expanded state and the selection are the
/// ones of the view model, both ways.
/// </summary>
public partial class OntologyTree : Tree
{
    private readonly OntologyWindowViewModel? _vm;
    private readonly Dictionary<TreeItem, OntologyTreeItem> _items = [];
    private readonly Dictionary<OntologyTreeItem, TreeItem> _rows = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<OntologyNodeViewModel, List<TreeItem>> _byNode = new(ReferenceEqualityComparer.Instance);
    private bool _updating;
    private bool _rebuildQueued;

    /// <summary>The row clicked last and whether it was expanded before the click (a double-click puts it back).</summary>
    private (OntologyTreeItem Item, bool WasExpanded)? _lastTap;

    /// <summary>True when the fold arrow of a row was used during the current click (the click must not toggle it again).</summary>
    private bool _arrowUsed;

    private static readonly SystemFont Bold = new() { FontNames = Eve.Eve.Body.FontNames, FontWeight = 700, Fallbacks = [Eve.Eve.Emoji] };
    private static readonly SystemFont Italic = new() { FontNames = Eve.Eve.Body.FontNames, FontItalic = true, Fallbacks = [Eve.Eve.Emoji] };

    public OntologyTree()
    {
    }

    public OntologyTree(OntologyWindowViewModel vm)
    {
        _vm = vm;
        Columns = 2;
        HideRoot = true;
        SelectMode = SelectModeEnum.Row;
        AllowRmbSelect = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SetColumnExpand(0, true);
        SetColumnExpandRatio(0, 3);
        SetColumnExpand(1, true);
        SetColumnExpandRatio(1, 2);
        SetColumnClipContent(1, true);

        NotifyCollectionChangedEventHandler changed = (_, _) => Bind.OnMain(QueueRebuild);
        vm.RootItems.CollectionChanged += changed;
        PropertyChangedEventHandler selected = (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedTreeItem))
                Bind.OnMain(ShowSelection);
        };
        vm.PropertyChanged += selected;
        KnowledgeUi.OnFreed(this, () =>
        {
            vm.RootItems.CollectionChanged -= changed;
            vm.PropertyChanged -= selected;
            Unwatch();
        });

        ItemSelected += OnItemSelected;
        ItemCollapsed += OnItemCollapsed;
        ItemActivated += OnItemActivated;
        GuiInput += OnGuiInput;
        Rebuild();
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued)
            return;
        _rebuildQueued = true;
        Callable.From(() =>
        {
            _rebuildQueued = false;
            if (IsInstanceValid(this))
                Rebuild();
        }).CallDeferred();
    }

    /// <summary>Builds the rows again from the occurrences of the view model.</summary>
    private void Rebuild()
    {
        if (_vm is null)
            return;
        _updating = true;
        var scroll = GetScroll();
        Unwatch();
        Clear();
        _items.Clear();
        _rows.Clear();
        var root = CreateItem();
        foreach (var item in _vm.RootItems)
            Add(root, item);
        foreach (var node in _byNode.Keys)
            node.PropertyChanged += OnNodeChanged;
        _updating = false;
        ShowSelection();
        if (_vm.SelectedTreeItem is null)
            ScrollTo(scroll);
    }

    private void ScrollTo(Vector2 scroll)
    {
        foreach (var child in GetChildren(true))
            if (child is VScrollBar bar)
                bar.Value = scroll.Y;
    }

    private void Add(TreeItem parent, OntologyTreeItem item)
    {
        var row = CreateItem(parent);
        _items[row] = item;
        _rows[item] = row;
        if (!_byNode.TryGetValue(item.Node, out var list))
            _byNode[item.Node] = list = [];
        list.Add(row);
        row.SetCellMode(1, TreeItem.TreeCellMode.Custom);
        row.SetCustomDrawCallback(1, Callable.From<TreeItem, Rect2>(DrawBadges));
        Show(row, item);
        foreach (var child in item.Children)
            Add(row, child);
        row.Collapsed = !item.IsExpanded;
    }

    private void Unwatch()
    {
        foreach (var node in _byNode.Keys)
            node.PropertyChanged -= OnNodeChanged;
        _byNode.Clear();
    }

    /// <summary>A node changed (its name, its state, its expansion): its rows are shown again.</summary>
    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e) => Bind.OnMain(() =>
    {
        if (!IsInstanceValid(this) || sender is not OntologyNodeViewModel node || !_byNode.TryGetValue(node, out var rows))
            return;
        _updating = true;
        foreach (var row in rows)
            if (IsInstanceValid(row) && _items.TryGetValue(row, out var item))
            {
                Show(row, item);
                if (row.Collapsed == item.IsExpanded)
                    row.Collapsed = !item.IsExpanded;
            }
        _updating = false;
        QueueRedraw();
    });

    /// <summary>The name of a row: ◆ / ●, its name, in the font and colour of its state.</summary>
    private static void Show(TreeItem row, OntologyTreeItem item)
    {
        var node = item.Node;
        row.SetText(0, (node.IsOntologyClass ? "◆  " : "●  ") + node.DisplayName);
        row.SetTooltipText(0, (node.IsOntologyClass ? "Ontology class" : "Instance") + (item.IsVirtualLink ? "\n" + item.VirtualLinkTip : string.Empty));
        var color = node.IsMatch ? Eve.Eve.Cyan : Eve.Eve.Text;
        if (node.IsDeleted)
            color = color with { A = 0.5f };
        row.SetCustomColor(0, color);
        if (node.IsMatch)
            row.SetCustomFont(0, Bold);
        else if (item.IsInVirtualBranch)
            row.SetCustomFont(0, Italic);
        else
            row.SetCustomFont(0, null);
        row.SetTooltipText(1, node.IsOrphan ? "The parent id does not match any loaded node" : item.IsVirtualLink ? item.VirtualLinkTip : string.Empty);
    }

    /// <summary>Draws the entity type and the badges of a row in its second column.</summary>
    private void DrawBadges(TreeItem row, Rect2 rect)
    {
        if (!_items.TryGetValue(row, out var item))
            return;
        var node = item.Node;
        var font = Eve.Eve.Body;
        const int size = 11;
        var x = rect.Position.X + 4;
        var baseline = rect.Position.Y + (rect.Size.Y + font.GetAscent(size) - font.GetDescent(size)) / 2;
        var alpha = node.IsDeleted ? 0.5f : 1f;
        if (node.HasEntityType)
        {
            DrawString(font, new Vector2(x, baseline), node.EntityType, HorizontalAlignment.Left, -1, size, Eve.Eve.Muted with { A = alpha });
            x += font.GetStringSize(node.EntityType, fontSize: size).X + 8;
        }
        void Badge(string text, Color color)
        {
            const int badgeSize = 9;
            var width = font.GetStringSize(text, fontSize: badgeSize).X + 10;
            var box = new Rect2(x, rect.Position.Y + rect.Size.Y / 2 - 8, width, 16);
            DrawRect(box, color with { A = 0.22f });
            DrawRect(box, color with { A = 0.7f }, false, 1);
            DrawString(font, new Vector2(x + 5, box.Position.Y + 12), text, HorizontalAlignment.Left, -1, badgeSize, color.Lightened(0.2f));
            x += width + 5;
        }
        if (item.IsVirtualLink)
            Badge("⇢ linked", new Color(0.36f, 0.55f, 0.94f));
        if (node.IsDeleted)
            Badge("deleted", Eve.Eve.Red);
        if (node.IsNew)
            Badge("new", Eve.Eve.Green);
        if (node.IsDirty)
            Badge("modified", Eve.Eve.Amber);
        if (node.IsOrphan)
            Badge("orphan", Eve.Eve.Muted);
    }

    /// <summary>Selects the row of the occurrence selected in the view model.</summary>
    private void ShowSelection()
    {
        if (_vm is null || !IsInstanceValid(this))
            return;
        _updating = true;
        if (_vm.SelectedTreeItem is { } item && _rows.TryGetValue(item, out var row) && IsInstanceValid(row))
        {
            if (!row.IsSelected(0))
                row.Select(0);
            ScrollToItem(row);
        }
        else
            DeselectAll();
        _updating = false;
    }

    private void OnItemSelected()
    {
        if (_updating || _vm is null || GetSelected() is not { } row || !_items.TryGetValue(row, out var item))
            return;
        if (!_vm.IsAttaching)
            _vm.SelectedTreeItem = item;
    }

    private void OnItemCollapsed(TreeItem row)
    {
        if (_updating || !_items.TryGetValue(row, out var item))
            return;
        _arrowUsed = true;
        item.IsExpanded = !row.Collapsed;
    }

    /// <summary>A double-click on a row (or Enter) opens its node in an Entity window.</summary>
    private void OnItemActivated()
    {
        if (_vm is not { IsAttaching: false } || GetSelected() is not { } row || !_items.TryGetValue(row, out var item))
            return;
        _vm.EditNode(item.Node);
    }

    private void OnGuiInput(InputEvent e)
    {
        if (_vm is null || e is not InputEventMouseButton { Pressed: true } click)
            return;
        var row = GetItemAtPosition(click.Position);
        var item = row is not null && _items.TryGetValue(row, out var found) ? found : null;

        // Attach mode: the clicked node becomes the virtual parent (the click does not change the selection).
        if (_vm.IsAttaching && click.ButtonIndex == MouseButton.Left)
        {
            AcceptEvent();
            if (item is not null)
                _ = _vm.CompleteAttachAsync(item.Node);
            return;
        }

        if (click.ButtonIndex == MouseButton.Right)
        {
            AcceptEvent();
            if (item is not null)
            {
                _vm.SelectedTreeItem = item;
                NodeMenu(item).Show(this, click.Position);
            }
            else
                TreeMenu().Show(this, click.Position);
            return;
        }

        if (click.ButtonIndex != MouseButton.Left || item is null)
            return;

        if (click.DoubleClick)
        {
            // The double-click opens the node (ItemActivated): its expansion stays as it was before.
            if (_lastTap is { } tap && ReferenceEquals(tap.Item, item))
                item.IsExpanded = tap.WasExpanded;
            _lastTap = null;
            return;
        }

        // A click expands the children of the node, or collapses them (not when the fold arrow did it already).
        _arrowUsed = false;
        _lastTap = (item, item.IsExpanded);
        Callable.From(() =>
        {
            if (!IsInstanceValid(this) || _arrowUsed)
                return;
            if (item.Children.Count > 0)
                item.IsExpanded = !item.IsExpanded;
        }).CallDeferred();
    }

    /// <summary>The menu of a node.</summary>
    private ContextMenu NodeMenu(OntologyTreeItem item)
    {
        var node = item.Node;
        var menu = new ContextMenu()
            .Item("Edit…", node.EditCommand, tooltip: "Open the node in the Entity window (or double-click it)")
            .Item(Data.Annotations.MenuText, () => Data.Annotations.Open(this, node.AnnotationTarget),
                !node.IsNew, "Write an annotation of this entity, with its tags")
            .Item("Add child node…", node.AddChildCommand)
            .Item("Add root node…", _vm!.AddRootCommand)
            .Item("Clone…", node.CloneCommand, tooltip: "Add an unsaved copy of this node next to it, in the Entity window")
            .Separator()
            .Item("Attach to another node…", node.AttachCommand, tooltip: "Also show this node, with all its children, under another node (virtual parent)");
        if (item.IsVirtualLink)
            menu.Item(item.DetachHeader, item.DetachCommand, tooltip: "Remove this virtual link (the node stays at its real place)");
        return menu
            .Separator()
            .Item("Export…", node.ExportCommand, tooltip: "Save this node and its descendants to a JSON file, without ids")
            .Item("Import…", node.ImportCommand, tooltip: "Create the nodes of a JSON export as children of this node (new ids)")
            .Separator()
            .Item("Delete (isDeleted = true)", node.DeleteCommand, tooltip: "Also flags every descendant as deleted")
            .Item("Undelete (isDeleted = false)", node.RestoreCommand, tooltip: "Restores this node and its deleted ancestors")
            .Item("Undelete with descendants", node.RestoreWithDescendantsCommand, tooltip: "Restores this node, its deleted ancestors and all its deleted descendants")
            .Separator()
            .Check("Show deleted nodes", _vm.ShowDeleted, on => _vm.ShowDeleted = on);
    }

    /// <summary>The menu of the empty part of the tree.</summary>
    private ContextMenu TreeMenu() => new ContextMenu()
        .Item("Add root node…", _vm!.AddRootCommand)
        .Item("Expand all", _vm.ExpandAllCommand)
        .Item("Collapse all", _vm.CollapseAllCommand)
        .Separator()
        .Check("Show deleted nodes", _vm.ShowDeleted, on => _vm.ShowDeleted = on)
        .Item("↻ Refresh", _vm.RefreshCommand, tooltip: "Reload all nodes from the server (F5). Unsaved changes are lost.");
}
