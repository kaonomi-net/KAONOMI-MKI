using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The Entity windows of an ontology: each node opened (a double-click in the tree, Edit, Add root node, Add child node,
/// Clone, or a double-click on an entity of the <c>/relation</c> graph) gets its own window of the desktop; a node
/// already open brings its window to the front. The title of a window follows the name of its node ("Entity-…"). The
/// windows of a node never saved close when it is discarded.
/// </summary>
internal sealed class EntityWindows
{
    private readonly OntologyWindowViewModel _ontology;
    private readonly Shell _shell;
    private readonly List<(EntityWindowViewModel ViewModel, DeskWindow Window)> _windows = [];

    private EntityWindows(OntologyWindowViewModel ontology, Shell shell)
    {
        _ontology = ontology;
        _shell = shell;
        _ontology.EditRequested += OnEditRequested;
        _ontology.UnsavedNodeRemoved += OnUnsavedNodeRemoved;
    }

    /// <summary>
    /// Opens the Entity windows of <paramref name="ontology"/> until <paramref name="owner"/> is gone (the windows
    /// already open stay when it leaves).
    /// </summary>
    public static void Attach(OntologyWindowViewModel ontology, Shell shell, Control owner)
    {
        var host = new EntityWindows(ontology, shell);
        KnowledgeUi.OnGone(owner, host.Detach);
    }

    private void Detach()
    {
        _ontology.EditRequested -= OnEditRequested;
        _ontology.UnsavedNodeRemoved -= OnUnsavedNodeRemoved;
    }

    private void OnEditRequested(OntologyNodeViewModel node) => Ui.OnMain(() => Show(node));

    private void Show(OntologyNodeViewModel node)
    {
        if (_windows.FirstOrDefault(w => w.ViewModel.Node == node) is { Window: not null } open && _shell.Deck.Windows.Contains(open.Window))
        {
            _shell.Deck.BringToFront(open.Window);
            EntityView.FocusName(open.ViewModel);
            return;
        }

        var viewModel = new EntityWindowViewModel(_ontology) { Node = node };
        var window = _shell.Open(viewModel, title: viewModel.WindowTitle);
        var entry = (viewModel, window);
        _windows.Add(entry);

        void OnTitle(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(EntityWindowViewModel.WindowTitle) or null or "")
                Ui.OnMain(() => KnowledgeUi.SetTitle(window, viewModel.WindowTitle));
        }
        viewModel.PropertyChanged += OnTitle;
        window.Closed += () =>
        {
            viewModel.PropertyChanged -= OnTitle;
            _windows.Remove(entry);
        };
        EntityView.FocusName(viewModel);
    }

    private void OnUnsavedNodeRemoved(OntologyNodeViewModel node) => Ui.OnMain(() =>
    {
        foreach (var (_, window) in _windows.Where(w => w.ViewModel.Node == node).ToList())
            _shell.Deck.Close(window);
    });
}
