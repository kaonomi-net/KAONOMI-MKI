using System;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// /kanban: the kanban of the tasks, one column per status (its colour, its count, "No task"), a card per task (title,
/// type badge in its colour, the description as tooltip). A card dragged to another column takes its status; a
/// double-click on a card opens the task editor in its own window (the board is reloaded after a save or a delete).
/// + New task (⌘/Ctrl N) shows the form of a new task on the right (title, type, status, description, start, end,
/// security, legal and data constraints, comment) with Cancel and Create; Refresh (F5) loads the tasks again.
/// </summary>
[ToolView(typeof(KanbanWindowViewModel), "Kanban", "📋", 1400, 820)]
public sealed class KanbanView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (KanbanWindowViewModel)viewModel;

        var header = Ui.Toolbar(Ui.Margin(Ui.Row(8,
            Ui.Fill(Eve.Eve.Caption("Project · tasks", Eve.Eve.TextBright, 15)),
            Ui.Command("+ NEW TASK", vm.NewTaskCommand, primary: true, tooltip: "Creates a task (Ctrl+N / ⌘N)"),
            Ui.Command("REFRESH", vm.RefreshCommand, tooltip: "Loads the tasks again (F5)")), 8, 0, 8, 0));

        // The board: one column per status, all of the same width.
        var board = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        board.AddThemeConstantOverride("separation", 10);
        var drag = new DragState();
        Bind.Items(board, vm.Columns, column => Column(vm, shell, column, drag));

        // The form of a new task.
        var name = Ui.Line(vm, nameof(vm.NewName), "What has to be done");
        var form = NewTaskForm(vm, name);
        Bind.When(form, vm, () =>
        {
            if (vm.IsCreating)
                Callable.From(() =>
                {
                    if (GodotObject.IsInstanceValid(name) && name.IsVisibleInTree())
                        name.GrabFocus();
                }).CallDeferred();
        }, nameof(vm.IsCreating));

        // The board scrolls sideways when the window is too narrow for its columns.
        var boardScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        boardScroll.AddChild(board);
        var body = Ui.Row(12, Ui.Margin(boardScroll, 7, 12, 0, 12), form);
        body.GetChild<Control>(0).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var page = Ui.Column(0, header, body, Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)));
        page.AddChild(new Hooks { DragEnded = drag.End });
        WritingUi.Shortcuts(page, key =>
        {
            if (key.Keycode == Key.F5)
                return WritingUi.Run(vm.RefreshCommand);
            if (WritingUi.IsCommand(key, Key.N))
                return WritingUi.Run(vm.NewTaskCommand);
            return false;
        });
        return page;
    }

    /// <summary>The card dragged and the column it is over (highlighted).</summary>
    private sealed class DragState
    {
        public KanbanCardViewModel? Card { get; set; }
        private KanbanColumnViewModel? _target;

        public void SetTarget(KanbanColumnViewModel? column)
        {
            if (ReferenceEquals(_target, column))
                return;
            if (_target is not null)
                _target.IsDropTarget = false;
            _target = column;
            if (column is not null)
                column.IsDropTarget = true;
        }

        /// <summary>The drag ended (dropped or not).</summary>
        public void End()
        {
            SetTarget(null);
            Card = null;
        }
    }

    /// <summary>A column: its colour line, title and count, its cards; a card dropped on it takes its status.</summary>
    private static Control Column(KanbanWindowViewModel vm, Shell shell, KanbanColumnViewModel column, DragState drag)
    {
        var accent = AvaloniaBridge.ToColor(column.Accent);
        var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1, CustomMinimumSize = new Vector2(170, 0), ClipContents = true };
        Bind.When(panel, column, () => panel.AddThemeStyleboxOverride("panel", column.IsDropTarget
            ? Eve.Eve.Box(Eve.Eve.Cyan with { A = 0.1f }, Eve.Eve.Cyan, 0, 2)
            : Eve.Eve.Box(Eve.Eve.Sunken(0.25f), Eve.Eve.Edge with { A = 0.3f }, 0)), nameof(column.IsDropTarget));

        var line = new ColorRect { Color = accent, CustomMinimumSize = new Vector2(0, 4), MouseFilter = Control.MouseFilterEnum.Ignore };
        var count = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var countBox = Eve.Eve.Box(accent, accent, 0);
        countBox.SetCornerRadiusAll(10);
        countBox.ContentMarginLeft = countBox.ContentMarginRight = 8;
        count.AddThemeStyleboxOverride("panel", countBox);
        count.AddChild(Ui.Label(column, nameof(column.Count), Colors.White, 12));
        var title = Ui.Row(6, Ui.Fill(Eve.Eve.Caption(column.Title, Eve.Eve.TextBright, 12)), count);

        var cards = new VBoxContainer();
        cards.AddThemeConstantOverride("separation", 8);
        var empty = Eve.Eve.Text_("No task", Eve.Eve.Muted with { A = 0.6f }, 12);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        Bind.Items(cards, column.Cards, card => Card(vm, shell, card, drag), empty);
        var scroll = Ui.Scroll(Ui.Margin(Ui.Column(0, Ui.Margin(empty, 0, 12, 0, 0), cards), 10, 0, 10, 4));

        var content = Ui.Column(0, line, Ui.Margin(title, 12, 10, 12, 8), scroll);
        panel.AddChild(content);

        // Drop: the whole column (its cards forward the drop to it).
        bool CanDrop(Vector2 _, Variant data)
        {
            var accepted = drag.Card is { } card && data.VariantType == Variant.Type.String && data.AsString().StartsWith(DragPrefix, StringComparison.Ordinal) && !column.Cards.Contains(card);
            drag.SetTarget(accepted ? column : null);
            return accepted;
        }
        async void Drop(Vector2 _, Variant data)
        {
            var card = drag.Card;
            drag.End();
            if (card is not null)
                await vm.MoveAsync(card, column);
        }
        panel.SetDragForwarding(new Callable(), Callable.From<Vector2, Variant, bool>(CanDrop), Callable.From<Vector2, Variant>(Drop));
        foreach (var node in new Control[] { content, scroll, cards, title })
            node.MouseFilter = Control.MouseFilterEnum.Pass;
        panel.SetMeta("canDrop", Callable.From<Vector2, Variant, bool>(CanDrop));
        panel.SetMeta("drop", Callable.From<Vector2, Variant>(Drop));
        return panel;
    }

    /// <summary>Text of the dragged data: the id of the task after it.</summary>
    private const string DragPrefix = "kaonomi-task:";

    /// <summary>
    /// A card: its title and type badge, the description as tooltip; dragged to another column (it shows under the
    /// pointer), double-click to edit its task.
    /// </summary>
    private static Control Card(KanbanWindowViewModel vm, Shell shell, KanbanCardViewModel card, DragState drag)
    {
        var panel = new PanelContainer { TooltipText = card.Description ?? string.Empty, MouseDefaultCursorShape = Control.CursorShape.Move, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", CardBox(false));
        panel.MouseEntered += () => panel.AddThemeStyleboxOverride("panel", CardBox(true));
        panel.MouseExited += () => panel.AddThemeStyleboxOverride("panel", CardBox(false));
        var title = Eve.Eve.Text_(card.Title, Eve.Eve.TextBright, 13, wrap: true);
        title.CustomMinimumSize = new Vector2(60, 0);
        var typeColor = AvaloniaBridge.ToColor(card.TypeBrush);
        var badge = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        badge.AddThemeStyleboxOverride("panel", Eve.Eve.Box(typeColor with { A = 0.85f }, typeColor, 0));
        badge.AddChild(Ui.Margin(Eve.Eve.Text_(card.Type, Colors.White, 11), 6, 1, 6, 1));
        var content = Ui.Column(6, title, badge);
        WritingUi.IgnoreMouse(content);
        panel.AddChild(content);

        // Drag: the card is the data; the drop on a column (or on a card of it) is handled by the column.
        Variant GetDrag(Vector2 _)
        {
            drag.Card = card;
            var preview = new PanelContainer { Modulate = new Color(1, 1, 1, 0.85f), CustomMinimumSize = new Vector2(Math.Max(140, panel.Size.X), 0) };
            preview.AddThemeStyleboxOverride("panel", CardBox(true));
            preview.AddChild(Eve.Eve.Text_(card.Title, Eve.Eve.TextBright, 13));
            panel.SetDragPreview(preview);
            return DragPrefix + card.Task.Id;
        }
        bool CanDrop(Vector2 position, Variant data) => ColumnOf(panel) is { } column && column.GetMeta("canDrop").AsCallable().Call(position, data).AsBool();
        void Drop(Vector2 position, Variant data)
        {
            if (ColumnOf(panel) is { } column)
                column.GetMeta("drop").AsCallable().Call(position, data);
        }
        panel.SetDragForwarding(Callable.From<Vector2, Variant>(GetDrag), Callable.From<Vector2, Variant, bool>(CanDrop), Callable.From<Vector2, Variant>(Drop));

        // A double-click edits the task in its own window; the board is reloaded after a change.
        panel.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true })
                return;
            panel.AcceptEvent();
            if (vm.Edit(card) is not { } editor)
                return;
            editor.CloseRequested += async changed =>
            {
                if (changed)
                    await vm.ReloadAfterChangeAsync();
            };
            shell.Open(editor, title: $"Task · {card.Title}");
        };
        return panel;
    }

    /// <summary>The column holding <paramref name="control"/>.</summary>
    private static Control? ColumnOf(Control control)
    {
        for (Node? node = control; node is not null; node = node.GetParent())
            if (node is Control c && c.HasMeta("canDrop"))
                return c;
        return null;
    }

    private static StyleBoxFlat CardBox(bool hover)
    {
        var box = Eve.Eve.Box(hover ? Eve.Eve.Cyan with { A = 0.08f } : Eve.Eve.Raised(0.04f), hover ? Eve.Eve.Cyan with { A = 0.7f } : Eve.Eve.Edge with { A = 0.5f }, 0);
        box.ContentMarginLeft = box.ContentMarginRight = 10;
        box.ContentMarginTop = box.ContentMarginBottom = 8;
        return box;
    }

    /// <summary>The form of a new task (shown while the view model is creating one).</summary>
    private static Control NewTaskForm(KanbanWindowViewModel vm, LineEdit name)
    {
        var types = WritingUi.Cells(2,
            Ui.Field("Type", WritingUi.Choice(vm, nameof(vm.NewType), vm.Types)),
            Ui.Field("Status", WritingUi.Choice(vm, nameof(vm.NewTaskStatus), vm.NewStatuses)));
        var dates = WritingUi.Cells(2,
            Ui.Field("Start", WritingUi.DateField(vm, nameof(vm.NewStart))),
            Ui.Field("End", WritingUi.DateField(vm, nameof(vm.NewEnd))));
        var fields = Ui.Column(8,
            Ui.Field("Title *", name),
            types,
            Ui.Field("Description", Ui.Lines(vm, nameof(vm.NewDescription), 90)),
            dates,
            Ui.Field("Security constraints", Ui.Lines(vm, nameof(vm.NewSecurity), 44)),
            Ui.Field("Legal constraints", Ui.Lines(vm, nameof(vm.NewLegal), 44)),
            Ui.Field("Data constraints", Ui.Lines(vm, nameof(vm.NewData), 44)),
            Ui.Field("Comment", Ui.Lines(vm, nameof(vm.NewComment), 44)),
            Ui.Field("Tags", Ui.Line(vm, nameof(vm.NewTags), MetaTags.TagsHint)),
            Ui.Field("Events", Ui.Line(vm, nameof(vm.NewEvents), MetaTags.EventsHint)),
            Ui.Field("Signals", Ui.Line(vm, nameof(vm.NewSignals), MetaTags.SignalsHint)));
        var buttons = Ui.Row(8, Ui.Spring(),
            Ui.Command("CANCEL", vm.CancelNewCommand),
            Ui.Command("CREATE", vm.SaveNewCommand, primary: true, tooltip: "Saves the task (PUT /task) and reloads the board"));
        var column = Ui.Column(8, Eve.Eve.Caption("New task", Eve.Eve.TextBright, 13), Ui.FillHeight(Ui.Scroll(Ui.Margin(fields, 0, 4, 10, 4))), buttons);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(360, 0) };
        panel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.3f), Eve.Eve.Edge with { A = 0.4f }, 14));
        panel.AddChild(column);
        var holder = Ui.Margin(panel, 0, 12, 12, 12);
        Bind.Visible(holder, vm, nameof(vm.IsCreating));
        return holder;
    }
}
