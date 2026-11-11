using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// The editor of a task of the kanban (/kanban, a double-click on a card), in a window of its own: title, type, status,
/// description, start, end, security, legal and data constraints, comment, the origin of the task, the status; Delete
/// (marks the task deleted, confirmed), Cancel (Escape) and Save (⌘/Ctrl S) close the window.
/// </summary>
[ToolView(typeof(TaskEditorViewModel), "Task", "📝", 560, 760, PerObject = true)]
public sealed class TaskEditorView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (TaskEditorViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;

        var name = Ui.Line(vm, nameof(vm.Name));
        var types = WritingUi.Cells(2,
            Ui.Field("Type", WritingUi.Choice(vm, nameof(vm.Type), vm.Types)),
            Ui.Field("Status", WritingUi.Choice(vm, nameof(vm.TaskStatus), vm.Statuses)));
        var dates = WritingUi.Cells(2,
            Ui.Field("Start", WritingUi.DateField(vm, nameof(vm.Start))),
            Ui.Field("End", WritingUi.DateField(vm, nameof(vm.End))));
        var fields = Ui.Column(8,
            Ui.Field("Title *", name),
            types,
            Ui.Field("Description", Ui.Lines(vm, nameof(vm.Description), 110)),
            dates,
            Ui.Field("Security constraints", Ui.Lines(vm, nameof(vm.Security), 44)),
            Ui.Field("Legal constraints", Ui.Lines(vm, nameof(vm.Legal), 44)),
            Ui.Field("Data constraints", Ui.Lines(vm, nameof(vm.Data), 44)),
            Ui.Field("Comment", Ui.Lines(vm, nameof(vm.Comment), 44)));

        var origin = WritingUi.Clamp(Eve.Eve.Text_(vm.Origin, Eve.Eve.Muted, 11), 1);
        var header = Ui.Column(2, Eve.Eve.Caption("Edit task", Eve.Eve.TextBright, 14), origin);

        var status = Ui.Label(vm, nameof(vm.Status), Eve.Eve.Text, 12, wrap: true);
        Bind.When(status, vm, () => status.AddThemeColorOverride("font_color", vm.IsError ? Eve.Eve.Red : Eve.Eve.Text), nameof(vm.IsError));
        Bind.When(status, vm, () => status.Visible = !string.IsNullOrEmpty(vm.Status), nameof(vm.Status));
        var buttons = Ui.Row(8,
            Ui.Command("DELETE", vm.DeleteCommand, tooltip: "Marks the task deleted (isDeleted = true): it leaves the board, nothing is erased"),
            Ui.Spring(),
            Ui.Command("CANCEL", vm.CancelCommand, tooltip: "Close without saving (Escape)"),
            Ui.Command("SAVE", vm.SaveCommand, primary: true, tooltip: "Saves the task (Ctrl+S / ⌘S) and reloads the board"));

        var page = Ui.Margin(Ui.Column(8, header, Eve.Eve.Separator(), Ui.FillHeight(Ui.Scroll(Ui.Margin(fields, 0, 0, 10, 0))), status, buttons), 14);

        // Save, delete and cancel close the window.
        void OnClose(bool changed) => Bind.OnMain(() => shell.Close(vm));
        vm.CloseRequested += OnClose;
        Bind.OnGone(page, () => vm.CloseRequested -= OnClose);

        WritingUi.Shortcuts(page, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SaveCommand);
            if (key.Keycode == Key.Escape)
                return WritingUi.Run(vm.CancelCommand);
            return false;
        });
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(name) && name.IsVisibleInTree())
                name.GrabFocus();
        }).CallDeferred();
        return page;
    }
}
