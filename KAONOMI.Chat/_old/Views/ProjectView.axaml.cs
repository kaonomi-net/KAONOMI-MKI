using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>
/// Kanban of the tasks (<c>/project</c>, <c>/nproject</c>): one column per status, a card per task. A card dragged to
/// another column takes its status; a double-click on a card opens the popup editing its task.
/// </summary>
public partial class ProjectView : UserControl
{
    /// <summary>Distance the pointer moves, pressed on a card, before the card is dragged (a click or double-click below it).</summary>
    private const double DragThreshold = 6;

    /// <summary>Text of the dragged data: the id of the task after it (other apps receive it as text).</summary>
    private const string DragPrefix = "kaonomi-task:";

    private PointerPressedEventArgs? _press;
    private Point _pressPoint;
    private KanbanCardViewModel? _pressedCard;

    /// <summary>The card being dragged (the drag and drop stays in this view).</summary>
    private KanbanCardViewModel? _dragged;

    private KanbanColumnViewModel? _dropTarget;

    /// <summary>Initializes the view: drag and drop of the cards between the columns.</summary>
    public ProjectView()
    {
        InitializeComponent();
        // The title box gets the focus when the form of a new task opens.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ProjectWindowViewModel viewModel)
                viewModel.PropertyChanged += OnViewModelPropertyChanged;
        };

        Board.AddHandler(PointerPressedEvent, OnBoardPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Board.AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        Board.AddHandler(PointerMovedEvent, OnBoardPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Board.AddHandler(DragDrop.DragOverEvent, OnBoardDragOver);
        Board.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropTarget(null));
        Board.AddHandler(DragDrop.DropEvent, OnBoardDrop);
    }

    /// <summary>The view model of type <typeparamref name="T"/> of the element under the pointer or one of its parents.</summary>
    private static T? Under<T>(object? source) where T : class =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<StyledElement>().Select(e => e.DataContext).OfType<T>().FirstOrDefault();

    /// <summary>Remembers the card pressed with the left button (it is dragged once the pointer moves).</summary>
    private void OnBoardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (!e.GetCurrentPoint(Board).Properties.IsLeftButtonPressed || e.ClickCount > 1 || Under<KanbanCardViewModel>(e.Source) is not { } card)
            return;
        _press = e;
        _pressPoint = e.GetPosition(Board);
        _pressedCard = card;
    }

    /// <summary>Starts dragging the pressed card once the pointer has moved a few pixels.</summary>
    private async void OnBoardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressedCard is not { } card)
            return;
        var delta = e.GetPosition(Board) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _press = null;
        _dragged = card;
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(DragPrefix + card.Task.Id));
        try
        {
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        finally
        {
            _dragged = null;
            SetDropTarget(null);
        }
    }

    /// <summary>A card over another column: that column is highlighted and the card can be dropped on it.</summary>
    private void OnBoardDragOver(object? sender, DragEventArgs e)
    {
        var column = _dragged is null ? null : Under<KanbanColumnViewModel>(e.Source);
        var accepted = column is not null && !column.Cards.Contains(_dragged!);
        e.DragEffects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(accepted ? column : null);
        e.Handled = true;
    }

    /// <summary>The card dropped on a column takes the status of the column.</summary>
    private async void OnBoardDrop(object? sender, DragEventArgs e)
    {
        var card = _dragged;
        var column = Under<KanbanColumnViewModel>(e.Source);
        SetDropTarget(null);
        e.Handled = true;
        if (card is null || column is null || DataContext is not ProjectWindowViewModel viewModel)
            return;
        e.DragEffects = DragDropEffects.Move;
        await viewModel.MoveAsync(card, column);
    }

    private void SetDropTarget(KanbanColumnViewModel? column)
    {
        if (ReferenceEquals(_dropTarget, column))
            return;
        if (_dropTarget is not null)
            _dropTarget.IsDropTarget = false;
        _dropTarget = column;
        if (column is not null)
            column.IsDropTarget = true;
    }

    /// <summary>A double-click on a card opens the popup editing its task; the board is reloaded after a change.</summary>
    private async void OnCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: KanbanCardViewModel card } || DataContext is not ProjectWindowViewModel viewModel
            || TopLevel.GetTopLevel(this) is not Window owner || viewModel.Edit(card) is not { } editor)
            return;
        e.Handled = true;
        if (await TaskEditorWindow.EditAsync(owner, editor))
            await viewModel.ReloadAfterChangeAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectWindowViewModel.IsCreating) && sender is ProjectWindowViewModel { IsCreating: true })
            Dispatcher.UIThread.Post(() => NameBox.Focus());
    }
}
