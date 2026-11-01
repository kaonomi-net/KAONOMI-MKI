using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A node found by the search of the Entity window: the node and its place in the tree.</summary>
public sealed record EntitySearchHit(OntologyNodeViewModel Node, string Path)
{
    /// <summary>"in root › parent", or "root node".</summary>
    public string PathLabel => Path.Length == 0 ? "root node" : "in " + Path;
}

/// <summary>
/// The Entity window of the ontology: the editor of one node (opened by a double-click in the tree, Edit, Add root
/// node, Add child node or Clone) and a search on every node of the ontology; a hit opens in the editor.
/// </summary>
public partial class EntityWindowViewModel : ViewModelBase, INoteContextProvider
{
    private CancellationTokenSource? _searchCts;

    public EntityWindowViewModel(OntologyWindowViewModel owner) => Owner = owner;

    /// <summary>The ontology window: the tree, the entity types and the confidentiality levels.</summary>
    public OntologyWindowViewModel Owner { get; }

    /// <summary>The node in the editor, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNode), nameof(NodePath), nameof(WindowTitle))]
    private OntologyNodeViewModel? _node;

    public bool HasNode => Node is not null;

    /// <summary>The title of the window: "Entity-" and the name of the node (as it is typed).</summary>
    public string WindowTitle => Node is null ? "Entity" : $"Entity-{Node.DisplayName}";

    /// <summary>The place of the node in the tree.</summary>
    public string NodePath => Node is null ? string.Empty : EntitySearchHitPath(Node);

    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    private string _searchStatus = "Search an entity (all fields, accents ignored) · Enter";

    /// <summary>The nodes found by the search.</summary>
    public ObservableCollection<EntitySearchHit> Results { get; } = [];

    /// <summary>The selected hit: its node opens in the editor of this window.</summary>
    [ObservableProperty]
    private EntitySearchHit? _selectedResult;

    partial void OnSelectedResultChanged(EntitySearchHit? value)
    {
        if (value is null)
            return;
        // The hit is shown in this window; the tree selects it too.
        Node = value.Node;
        Owner.SelectedNode = value.Node;
    }

    partial void OnNodeChanged(OntologyNodeViewModel? oldValue, OntologyNodeViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnNodePropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnNodePropertyChanged;
        if (SelectedResult is { } hit && hit.Node != newValue)
            SelectedResult = null;
    }

    private void OnNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OntologyNodeViewModel.DisplayName) or nameof(OntologyNodeViewModel.Name))
            OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>Searches the nodes (local and server) and lists them.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        if (Term.Trim().Length == 0)
        {
            Results.Clear();
            SearchStatus = "Type a text to search";
            return;
        }
        SearchStatus = "Searching…";
        List<OntologyNodeViewModel> nodes;
        bool serverAnswered;
        try
        {
            (nodes, serverAnswered) = await Owner.FindNodesAsync(Term, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cleared, or a newer search: this one has nothing to say.
            return;
        }
        if (cts.IsCancellationRequested)
            return;
        Results.Clear();
        foreach (var node in nodes)
            Results.Add(new EntitySearchHit(node, EntitySearchHitPath(node)));
        SearchStatus = (nodes.Count == 0 ? $"No entity matches \"{Term.Trim()}\"" : $"{nodes.Count} entit{(nodes.Count == 1 ? "y" : "ies")}")
                       + (serverAnswered ? string.Empty : " · server search unavailable (local result)");
    }

    /// <summary>Empties the search.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        _searchCts?.Cancel();
        Term = string.Empty;
        Results.Clear();
        SearchStatus = "Search an entity (all fields, accents ignored) · Enter";
    }

    private static string EntitySearchHitPath(OntologyNodeViewModel node) => OntologyWindowViewModel.PathOf(node);

    /// <inheritdoc />
    public string NoteWindowName => "Entity";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        // The id and name of the entity at the top: the id of the object of a link, its title in /relation.
        id = Node?.Id,
        name = Node?.DisplayName,
        entityType = Node?.EntityType,
        searchTerm = Term,
        node = Node?.ToEntry(),
        nodePath = NodePath
    };
}
