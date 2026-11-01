using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Data;

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
        vm.CloseRequested += () => Bind.OnMain(() => shell.Close(vm));
        var text = Ui.Lines(vm, nameof(vm.Text), 150, "Type your note…");
        Bind.Enabled(text, vm, nameof(vm.IsSent), invert: true);
        var context = new TextEdit { Text = vm.Context, Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(0, 120), Visible = false };
        context.AddThemeFontOverride("font", Eve.Eve.Mono);
        context.AddThemeFontSizeOverride("font_size", 11);
        var show = new CheckButton { Text = "Context sent with the note (no user or machine name)" };
        show.Toggled += on => context.Visible = on;
        var cancel = Ui.Action("CANCEL", () => shell.Close(vm));
        var save = Ui.Command("SAVE", vm.SaveCommand, primary: true);
        text.CallDeferred(Control.MethodName.GrabFocus);
        return Ui.Column(10,
            Eve.Eve.Text_($"Your note about the {vm.WindowName} window:", Eve.Eve.Text, 13, wrap: true),
            Ui.FillHeight(text),
            show,
            context,
            Ui.Row(8, Ui.Fill(Ui.Label(vm, nameof(vm.Status), Eve.Eve.Muted, 12, wrap: true)), cancel, save));
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
