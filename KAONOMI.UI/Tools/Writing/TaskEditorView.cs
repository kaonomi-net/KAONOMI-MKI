using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>
/// The editor of a task of the kanban (/project, a double-click on a card), in a window of its own: title, type, status,
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

        var name = WritingUi.Line(vm, nameof(vm.Name));
        var types = WritingUi.Cells(2,
            WritingUi.Field("Type", WritingUi.Choice(vm, nameof(vm.Type), vm.Types)),
            WritingUi.Field("Status", WritingUi.Choice(vm, nameof(vm.TaskStatus), vm.Statuses)));
        var dates = WritingUi.Cells(2,
            WritingUi.Field("Start", WritingUi.DateField(vm, nameof(vm.Start))),
            WritingUi.Field("End", WritingUi.DateField(vm, nameof(vm.End))));
        var fields = Ui.Column(8,
            WritingUi.Field("Title *", name),
            types,
            WritingUi.Field("Description", WritingUi.Lines(vm, nameof(vm.Description), 110)),
            dates,
            WritingUi.Field("Security constraints", WritingUi.Lines(vm, nameof(vm.Security), 44)),
            WritingUi.Field("Legal constraints", WritingUi.Lines(vm, nameof(vm.Legal), 44)),
            WritingUi.Field("Data constraints", WritingUi.Lines(vm, nameof(vm.Data), 44)),
            WritingUi.Field("Comment", WritingUi.Lines(vm, nameof(vm.Comment), 44)));

        var origin = WritingUi.Clamp(Ui.Label(vm.Origin, Ui.Muted, 11), 1);
        ToolTip.SetTip(origin, vm.Origin);
        var header = Ui.Column(2, Ui.Label("EDIT TASK", Ui.TextBright, 14, bold: true), origin);

        var status = Ui.Bound(vm, nameof(vm.Status), Ui.Text, 12, wrap: true);
        Ui.When(status, vm, () => status.Foreground = vm.IsError ? Ui.Red : Ui.Text, nameof(vm.IsError));
        Ui.When(status, vm, () => status.IsVisible = !string.IsNullOrEmpty(vm.Status), nameof(vm.Status));
        var right = Ui.Row(8,
            WritingUi.Command("CANCEL", vm.CancelCommand, tooltip: "Close without saving (Escape)"),
            WritingUi.Command("SAVE", vm.SaveCommand, primary: true, tooltip: "Saves the task (Ctrl+S / ⌘S) and reloads the board"));
        var buttons = Ui.Dock((right, Dock.Right),
            (WritingUi.Command("DELETE", vm.DeleteCommand, tooltip: "Marks the task deleted (isDeleted = true): it leaves the board, nothing is erased"), Dock.Left));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        status.Margin = new Thickness(0, 6, 0, 0);

        var top = Ui.Column(6, header, WritingUi.Separator());
        top.Margin = new Thickness(0, 0, 0, 6);
        var page = WritingUi.Margin(WritingUi.Page([top], Ui.Scroll(WritingUi.Margin(fields, 0, 0, 10, 0)), status, buttons), 14);

        // Save, delete and cancel close the window.
        void OnClose(bool changed) => Ui.OnMain(() => shell.Close(vm));
        vm.CloseRequested += OnClose;
        WritingUi.OnGone(page, () => vm.CloseRequested -= OnClose);

        WritingUi.Shortcuts(page, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SaveCommand);
            if (key.Key == Key.Escape)
                return WritingUi.Run(vm.CancelCommand);
            return false;
        });
        WritingUi.FocusWhenShown(name);
        return page;
    }
}
