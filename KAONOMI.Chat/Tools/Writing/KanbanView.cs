using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>
/// /kanban: the kanban of the tasks, one column per status (its colour, its count, "No task"), a card per task (title, type
/// badge in its colour, the description as tooltip). A card dragged to another column takes its status (the column under the
/// pointer is outlined); a double-click on a card opens the task editor in its own window (the board is reloaded after a save
/// or a delete). + New task (⌘/Ctrl N) shows the form of a new task on the right (title, type, status, description, start,
/// end, security, legal and data constraints, comment) with Cancel and Create; Refresh (F5) loads the tasks again.
/// </summary>
[ToolView(typeof(KanbanWindowViewModel), "Kanban", "📋", 1400, 820)]
public sealed class KanbanView : IToolView
{
    private const double ColumnWidth = 180;

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (KanbanWindowViewModel)viewModel;

        var title = Ui.Label("PROJECT · TASKS", Ui.TextBright, 15, bold: true);
        var actions = Ui.Row(8,
            WritingUi.Command("+ NEW TASK", vm.NewTaskCommand, primary: true, tooltip: "Creates a task (Ctrl+N / ⌘N)"),
            WritingUi.Command("REFRESH", vm.RefreshCommand, tooltip: "Loads the tasks again (F5)"));
        var header = WritingUi.Toolbar(WritingUi.Margin(Ui.Dock((actions, Dock.Right), (title, Dock.Left)), 8, 0, 8, 0));

        // The board: one column per status, all of the same width; it scrolls sideways when the window is too narrow.
        var drag = new DragState();
        var board = new UniformGrid { Rows = 1 };
        WritingUi.Items(board, vm.Columns, column => Column(vm, shell, column, drag));
        var boardScroll = new ScrollViewer
        {
            Content = board,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        void Fit() => board.Width = Math.Max(boardScroll.Bounds.Width, board.Children.Count * (ColumnWidth + 10));
        boardScroll.SizeChanged += (_, _) => Fit();
        board.Children.CollectionChanged += (_, _) => Fit();

        // The form of a new task.
        var name = WritingUi.Line(vm, nameof(vm.NewName), "What has to be done");
        var form = NewTaskForm(vm, name);
        Ui.When(form, vm, () =>
        {
            if (vm.IsCreating)
                WritingUi.Later(() => name.Focus());
        }, nameof(vm.IsCreating));

        var body = Ui.Dock((form, Dock.Right), (WritingUi.Margin(boardScroll, 7, 12, 7, 12), Dock.Left));
        var page = WritingUi.Page([header], body, WritingUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)));
        WritingUi.Shortcuts(page, key =>
        {
            if (key.Key == Key.F5)
                return WritingUi.Run(vm.RefreshCommand);
            if (WritingUi.IsCommand(key, Key.N))
                return WritingUi.Run(vm.NewTaskCommand);
            if (key.Key == Key.Escape && drag.Card is not null)
            {
                drag.End();
                return true;
            }
            return false;
        });
        return page;
    }

    /// <summary>The card dragged, where it started, and the column it is over (outlined).</summary>
    private sealed class DragState
    {
        public KanbanCardViewModel? Card { get; set; }
        public Point Start { get; set; }
        public bool Dragging { get; set; }
        public KanbanColumnViewModel? Target { get; private set; }
        public Dictionary<Control, KanbanColumnViewModel> Columns { get; } = [];

        public void SetTarget(KanbanColumnViewModel? column)
        {
            if (ReferenceEquals(Target, column))
                return;
            if (Target is not null)
                Target.IsDropTarget = false;
            Target = column;
            if (column is not null)
                column.IsDropTarget = true;
        }

        /// <summary>The column under the pointer (that does not hold the card already).</summary>
        public KanbanColumnViewModel? ColumnAt(PointerEventArgs e)
        {
            foreach (var (control, column) in Columns)
            {
                if (!control.IsVisible || TopLevel.GetTopLevel(control) is null)
                    continue;
                var at = e.GetPosition(control);
                if (new Rect(control.Bounds.Size).Contains(at))
                    return Card is { } card && column.Cards.Contains(card) ? null : column;
            }
            return null;
        }

        /// <summary>The drag ended (dropped or not).</summary>
        public void End()
        {
            SetTarget(null);
            Card = null;
            Dragging = false;
        }
    }

    /// <summary>A column: its colour line, title and count, its cards; a card dropped on it takes its status.</summary>
    private static Control Column(KanbanWindowViewModel vm, Shell shell, KanbanColumnViewModel column, DragState drag)
    {
        var line = new Border { Background = column.Accent, Height = 4 };
        var count = new Border
        {
            Background = column.Accent,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Bound(column, nameof(column.Count), Brushes.White, 12)
        };
        var title = Ui.Dock((count, Dock.Right), (Ui.Label(column.Title.ToUpperInvariant(), Ui.TextBright, 12, bold: true), Dock.Left));
        title.Margin = new Thickness(12, 10, 12, 8);

        var cards = new StackPanel { Spacing = 8 };
        var empty = Ui.Label("No task", Ui.Muted, 12);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Margin = new Thickness(0, 12, 0, 0);
        WritingUi.Items(cards, column.Cards, card => Card(vm, shell, card, drag), empty);
        var scroll = Ui.Scroll(WritingUi.Margin(Ui.Column(0, empty, cards), 10, 0, 10, 4));

        var panel = new Border
        {
            MinWidth = 170,
            Margin = new Thickness(0, 0, 10, 0),
            ClipToBounds = true,
            Child = WritingUi.Page([line, title], scroll)
        };
        Ui.When(panel, column, () =>
        {
            panel.Background = column.IsDropTarget ? Ui.Header : Ui.Field;
            panel.BorderBrush = column.IsDropTarget ? Ui.Accent : Ui.Edge;
            panel.BorderThickness = new Thickness(column.IsDropTarget ? 2 : 1);
        }, nameof(column.IsDropTarget));
        drag.Columns[panel] = column;
        panel.DetachedFromVisualTree += (_, _) => drag.Columns.Remove(panel);
        panel.AttachedToVisualTree += (_, _) => drag.Columns[panel] = column;
        return panel;
    }

    /// <summary>
    /// A card: its title and type badge, the description as tooltip; dragged to another column, double-click to edit its task.
    /// </summary>
    private static Control Card(KanbanWindowViewModel vm, Shell shell, KanbanCardViewModel card, DragState drag)
    {
        var title = Ui.Label(card.Title, Ui.TextBright, 13, wrap: true);
        var badge = new Border
        {
            Background = card.TypeBrush,
            Padding = new Thickness(6, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Ui.Label(card.Type ?? string.Empty, Brushes.White, 11)
        };
        var panel = new Border
        {
            Child = Ui.Column(6, title, badge),
            Background = Ui.Panel,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        if (!string.IsNullOrEmpty(card.Description))
            ToolTip.SetTip(panel, card.Description);
        panel.PointerEntered += (_, _) => panel.BorderBrush = Ui.Accent;
        panel.PointerExited += (_, _) => panel.BorderBrush = drag.Card == card ? Ui.Accent : Ui.Edge;

        panel.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(panel);
            if (!point.Properties.IsLeftButtonPressed)
                return;
            if (e.ClickCount == 2)
            {
                // A double-click edits the task in its own window; the board is reloaded after a change.
                drag.End();
                e.Handled = true;
                if (vm.Edit(card) is not { } editor)
                    return;
                editor.CloseRequested += async changed =>
                {
                    if (changed)
                        await vm.ReloadAfterChangeAsync();
                };
                shell.Open(editor, title: $"Task · {card.Title}");
                return;
            }
            drag.Card = card;
            drag.Dragging = false;
            drag.Start = e.GetPosition(null);
            e.Pointer.Capture(panel);
            e.Handled = true;
        };
        panel.PointerMoved += (_, e) =>
        {
            if (drag.Card != card)
                return;
            var now = e.GetPosition(null);
            if (!drag.Dragging && Math.Abs(now.X - drag.Start.X) + Math.Abs(now.Y - drag.Start.Y) < 6)
                return;
            drag.Dragging = true;
            panel.Opacity = 0.6;
            drag.SetTarget(drag.ColumnAt(e));
        };
        panel.PointerReleased += async (_, e) =>
        {
            if (drag.Card != card)
                return;
            e.Pointer.Capture(null);
            panel.Opacity = 1;
            var target = drag.Dragging ? drag.ColumnAt(e) : null;
            drag.End();
            panel.BorderBrush = Ui.Edge;
            if (target is not null)
                await vm.MoveAsync(card, target);
        };
        panel.PointerCaptureLost += (_, _) =>
        {
            panel.Opacity = 1;
            if (drag.Card == card)
                drag.End();
        };
        return panel;
    }

    /// <summary>The form of a new task (shown while the view model is creating one).</summary>
    private static Control NewTaskForm(KanbanWindowViewModel vm, TextBox name)
    {
        var types = WritingUi.Cells(2,
            WritingUi.Field("Type", WritingUi.Choice(vm, nameof(vm.NewType), vm.Types)),
            WritingUi.Field("Status", WritingUi.Choice(vm, nameof(vm.NewTaskStatus), vm.NewStatuses)));
        var dates = WritingUi.Cells(2,
            WritingUi.Field("Start", WritingUi.DateField(vm, nameof(vm.NewStart))),
            WritingUi.Field("End", WritingUi.DateField(vm, nameof(vm.NewEnd))));
        var fields = Ui.Column(8,
            WritingUi.Field("Title *", name),
            types,
            WritingUi.Field("Description", WritingUi.Lines(vm, nameof(vm.NewDescription), 90)),
            dates,
            WritingUi.Field("Security constraints", WritingUi.Lines(vm, nameof(vm.NewSecurity), 44)),
            WritingUi.Field("Legal constraints", WritingUi.Lines(vm, nameof(vm.NewLegal), 44)),
            WritingUi.Field("Data constraints", WritingUi.Lines(vm, nameof(vm.NewData), 44)),
            WritingUi.Field("Comment", WritingUi.Lines(vm, nameof(vm.NewComment), 44)),
            WritingUi.Field("Tags", WritingUi.Line(vm, nameof(vm.NewTags), MetaTags.TagsHint)),
            WritingUi.Field("Events", WritingUi.Line(vm, nameof(vm.NewEvents), MetaTags.EventsHint)),
            WritingUi.Field("Signals", WritingUi.Line(vm, nameof(vm.NewSignals), MetaTags.SignalsHint)));
        var buttons = Ui.Row(8,
            WritingUi.Command("CANCEL", vm.CancelNewCommand),
            WritingUi.Command("CREATE", vm.SaveNewCommand, primary: true, tooltip: "Saves the task (PUT /task) and reloads the board"));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 8, 0, 0);
        var caption = Ui.Label("NEW TASK", Ui.TextBright, 13, bold: true);
        caption.Margin = new Thickness(0, 0, 0, 8);
        var panel = Ui.Box(WritingUi.Page([caption], Ui.Scroll(WritingUi.Margin(fields, 0, 4, 10, 4)), buttons), 14, Ui.Header);
        panel.Width = 380;
        var holder = WritingUi.Margin(panel, 0, 12, 12, 12);
        return holder.Visible(vm, nameof(vm.IsCreating));
    }
}
