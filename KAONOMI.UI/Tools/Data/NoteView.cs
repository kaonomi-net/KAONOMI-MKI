using Avalonia;
using Avalonia.Controls;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// The note about a window (📝 in its header, or in the chat): the note typed, the context sent with it (the data of the
/// window, no user or machine name), Save; the window closes by itself once the server has the note.
/// </summary>
[ToolView(typeof(NoteDialogViewModel), "Note", "📝", 560, 460, PerObject = true)]
public sealed class NoteView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (NoteDialogViewModel)viewModel;
        vm.CloseRequested += () => Ui.OnMain(() => shell.Close(vm));
        var text = DataUi.FocusWhenShown(DataUi.Lines(vm, nameof(vm.Text), 150, "Type your note…"));
        DataUi.Enabled(text, vm, nameof(vm.IsSent), invert: true);
        var context = DataUi.ReadOnly(vm.Context ?? string.Empty, 120);
        context.MaxHeight = 220;
        var show = DataUi.Reveal("Context sent with the note (no user or machine name)", context);
        var buttons = DataUi.Bar([], DataUi.Bound(vm, nameof(vm.Status), Ui.Muted, 12, wrap: true),
            DataUi.Action("CANCEL", () => shell.Close(vm)),
            DataUi.Command("SAVE", vm.SaveCommand, primary: true));
        var title = Ui.Label($"Your note about the {vm.WindowName} window:", Ui.Text, 13, wrap: true);
        title.Margin = new Thickness(0, 0, 0, 8);
        show.Margin = new Thickness(0, 8, 0, 4);
        buttons.Margin = new Thickness(0, 8, 0, 0);
        var page = Ui.Dock((title, Dock.Top), (buttons, Dock.Bottom), (context, Dock.Bottom), (show, Dock.Bottom), (text, Dock.Top));
        page.Margin = new Thickness(12);
        return page;
    }

    /// <summary>Opens the note about the window named <paramref name="name"/>, whose data is given by <paramref name="provider"/>.</summary>
    public static void Open(Shell shell, string name, object? provider)
    {
        var source = provider as INoteContextProvider;
        var windowName = source?.NoteWindowName ?? name;
        var context = NoteContext.Build(source, windowName);
        shell.Open(new NoteDialogViewModel(KaonomiAssistantService.Shared, windowName, context), title: $"Note · {windowName}");
    }
}
