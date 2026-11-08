using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The tree of the ontology, kept in sync with <see cref="OntologyWindowViewModel.RootItems"/>: one row per occurrence
/// (a node with virtual parents shows at each place it is attached), ◆ for a class and ● for an instance, its name
/// (bold in the accent colour when it matches the filter, italic under a virtual link, faded when deleted), then its
/// entity type and its badges (⇢ linked, deleted, new, modified, orphan). The expanded state and the selection are the
/// ones of the view model, both ways. A click on a row expands or collapses it, a double-click (or Enter) opens its node
/// in an Entity window; right click: the menu of the node (or of the tree on the empty part); in attach mode, a click
/// chooses the virtual parent.
/// </summary>
internal static class OntologyTree
{
    private static readonly Color Linked = Paint.Rgb(0.36, 0.55, 0.94);

    public static TreeView Build(OntologyWindowViewModel vm)
    {
        var tree = new TreeView
        {
            ItemsSource = vm.RootItems,
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncTreeDataTemplate<OntologyTreeItem>((item, _) => Row(item), item => item.Children)
        };
        // The expanded state of each row is the one of its occurrence.
        tree.Styles.Add(new Style(x => x.OfType<TreeViewItem>())
        {
            Setters = { new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(OntologyTreeItem.IsExpanded)) { Mode = BindingMode.TwoWay }) }
        });

        // The selection, both ways (not in attach mode: the click chooses the virtual parent).
        var updating = false;
        tree.SelectionChanged += (_, _) =>
        {
            if (updating || vm.IsAttaching)
                return;
            if (tree.SelectedItem is OntologyTreeItem item)
                vm.SelectedTreeItem = item;
        };
        void ShowSelection()
        {
            updating = true;
            tree.SelectedItem = vm.SelectedTreeItem;
            updating = false;
            if (vm.SelectedTreeItem is { } selected)
                Dispatcher.UIThread.Post(() =>
                {
                    if (tree.TreeContainerFromItem(selected) is Control container)
                        container.BringIntoView();
                }, DispatcherPriority.Background);
        }
        Ui.When(tree, vm, ShowSelection, nameof(vm.SelectedTreeItem));

        (OntologyTreeItem Item, bool WasExpanded)? lastTap = null;
        tree.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var point = e.GetCurrentPoint(tree);
            var source = e.Source as Visual;
            var container = source?.FindAncestorOfType<TreeViewItem>(true);
            var item = container?.DataContext as OntologyTreeItem;
            var onArrow = source?.FindAncestorOfType<ToggleButton>(true) is { } toggle && container?.IsVisualAncestorOf(toggle) == true;

            // Attach mode: the clicked node becomes the virtual parent (the click does not change the selection).
            if (vm.IsAttaching && point.Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                if (item is not null)
                    _ = vm.CompleteAttachAsync(item.Node);
                return;
            }
            if (point.Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                if (item is not null)
                {
                    vm.SelectedTreeItem = item;
                    NodeMenu(vm, tree, item).Show(tree);
                }
                else
                    TreeMenu(vm).Show(tree);
                return;
            }
            if (!point.Properties.IsLeftButtonPressed || item is null || onArrow)
                return;
            if (e.ClickCount >= 2)
            {
                // The double-click opens the node: its expansion stays as it was before.
                if (lastTap is { } tap && ReferenceEquals(tap.Item, item))
                    item.IsExpanded = tap.WasExpanded;
                lastTap = null;
                vm.EditNode(item.Node);
                e.Handled = true;
                return;
            }
            // A click expands the children of the node, or collapses them.
            lastTap = (item, item.IsExpanded);
            if (item.Children.Count > 0)
                item.IsExpanded = !item.IsExpanded;
        }, RoutingStrategies.Tunnel);
        // The double tap of a row toggles it in Avalonia: here the double-click opens the node instead.
        tree.AddHandler(InputElement.DoubleTappedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !vm.IsAttaching && vm.SelectedTreeItem is { } selected)
            {
                vm.EditNode(selected.Node);
                e.Handled = true;
            }
        };
        return tree;
    }

    /// <summary>A row: ◆ / ●, the name in the font and colour of its state, the entity type and the badges.</summary>
    private static Control Row(OntologyTreeItem item)
    {
        var node = item.Node;
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        var type = new TextBlock { Foreground = Ui.Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var badges = Ui.Row(5,
            KnowledgeUi.Badge("⇢ linked", Linked),
            KnowledgeUi.Badge("deleted", Paint.Red, node, nameof(node.IsDeleted)),
            KnowledgeUi.Badge("new", Paint.Green, node, nameof(node.IsNew)),
            KnowledgeUi.Badge("modified", Paint.Amber, node, nameof(node.IsDirty)),
            KnowledgeUi.Badge("orphan", Paint.Muted, node, nameof(node.IsOrphan)));
        badges.Children[0].IsVisible = item.IsVirtualLink;
        badges.Margin = new Thickness(8, 0, 0, 0);
        badges.VerticalAlignment = VerticalAlignment.Center;
        var row = Ui.Row(0, name, type, badges);
        row.Background = Brushes.Transparent;
        void Show()
        {
            name.Text = (node.IsOntologyClass ? "◆  " : "●  ") + node.DisplayName;
            name.Foreground = node.IsMatch ? Ui.Accent : Ui.Text;
            name.FontWeight = node.IsMatch ? FontWeight.Bold : FontWeight.Normal;
            name.FontStyle = !node.IsMatch && item.IsInVirtualBranch ? FontStyle.Italic : FontStyle.Normal;
            row.Opacity = node.IsDeleted ? 0.5 : 1;
            type.Text = node.HasEntityType ? node.EntityType : string.Empty;
            ToolTip.SetTip(name, (node.IsOntologyClass ? "Ontology class" : "Instance") + (item.IsVirtualLink ? "\n" + item.VirtualLinkTip : string.Empty));
            ToolTip.SetTip(badges, node.IsOrphan ? "The parent id does not match any loaded node" : item.IsVirtualLink ? item.VirtualLinkTip : null);
        }
        Ui.When(row, node, Show);
        return row;
    }

    /// <summary>The menu of a node.</summary>
    private static Menu NodeMenu(OntologyWindowViewModel vm, Control origin, OntologyTreeItem item)
    {
        var node = item.Node;
        var menu = new Menu()
            .Item("Edit…", node.EditCommand, tooltip: "Open the node in the Entity window (or double-click it)")
            .Item(KnowledgeUi.AnnotateText, () => KnowledgeUi.Annotate(origin, node.AnnotationTarget), !node.IsNew, "Write an annotation of this entity, with its tags")
            .Item("Add child node…", node.AddChildCommand)
            .Item("Add root node…", vm.AddRootCommand)
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
            .Check("Show deleted nodes", vm.ShowDeleted, on => vm.ShowDeleted = on);
    }

    /// <summary>The menu of the empty part of the tree.</summary>
    private static Menu TreeMenu(OntologyWindowViewModel vm) => new Menu()
        .Item("Add root node…", vm.AddRootCommand)
        .Item("Expand all", vm.ExpandAllCommand)
        .Item("Collapse all", vm.CollapseAllCommand)
        .Separator()
        .Check("Show deleted nodes", vm.ShowDeleted, on => vm.ShowDeleted = on)
        .Item("↻ Refresh", vm.RefreshCommand, tooltip: "Reload all nodes from the server (F5). Unsaved changes are lost.");
}
