using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// One occurrence of a node in the ontology tree. A node attached to virtual parents (<c>nodevirtualeftchildrenId</c>)
/// has several occurrences: one at its real place and one under each virtual parent, each with all its children.
/// </summary>
public partial class OntologyTreeItem : ViewModelBase
{
    /// <summary>Creates the occurrence of <paramref name="node"/>; <paramref name="virtualParent"/> is set for a virtual link.</summary>
    public OntologyTreeItem(OntologyNodeViewModel node, OntologyNodeViewModel? virtualParent, bool isInVirtualBranch)
    {
        Node = node;
        VirtualParent = virtualParent;
        IsInVirtualBranch = isInVirtualBranch;
    }

    /// <summary>The node shown (shared by all its occurrences).</summary>
    public OntologyNodeViewModel Node { get; }

    /// <summary>Virtual parent this occurrence is attached under, or null at the node's real place.</summary>
    public OntologyNodeViewModel? VirtualParent { get; }

    /// <summary>True for the occurrence created by a virtual link.</summary>
    public bool IsVirtualLink => VirtualParent is not null;

    /// <summary>True for every occurrence shown under a virtual link (the linked node and its children).</summary>
    public bool IsInVirtualBranch { get; }

    /// <summary>Tooltip of the "linked" badge.</summary>
    public string VirtualLinkTip => $"Virtual link: \"{Node.DisplayName}\" is attached under \"{VirtualParent?.DisplayName}\" (its real place is elsewhere)";

    /// <summary>Header of the detach menu item.</summary>
    public string DetachHeader => $"Detach from \"{VirtualParent?.DisplayName}\"";

    /// <summary>Visible children of this occurrence (real and virtual ones).</summary>
    public ObservableCollection<OntologyTreeItem> Children { get; } = [];

    /// <summary>Expanded state, shared by every occurrence of the node.</summary>
    public bool IsExpanded
    {
        get => Node.IsExpanded;
        set
        {
            if (Node.IsExpanded == value)
                return;
            Node.IsExpanded = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Removes this virtual link (the node stays at its real place and under its other virtual parents).</summary>
    [RelayCommand]
    private Task DetachAsync() => VirtualParent is null ? Task.CompletedTask : Node.Owner.DetachAsync(Node, VirtualParent);
}
