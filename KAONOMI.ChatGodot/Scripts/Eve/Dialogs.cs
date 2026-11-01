using System;
using System.Threading.Tasks;
using Godot;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The dialogs of the chat: the file pickers of the system (save, open one or several files, a folder), and small
/// dialogs in the look of the chat (a message, a confirmation, a text to type), awaited as tasks.
/// </summary>
public static class Dialogs
{
    private static Node Root => Shell.Current.Root;

    /// <summary>
    /// Asks where to save a file: <paramref name="filters"/> like <c>"*.png ; PNG picture"</c>. Null when cancelled.
    /// </summary>
    public static Task<string?> SaveFile(string title, string suggestedName, string[] filters, string? folder = null) =>
        Pick(title, FileDialog.FileModeEnum.SaveFile, filters, folder, suggestedName).ContinueWith(t => t.Result is { Length: > 0 } r ? r[0] : null, TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>Asks for a file to open. Null when cancelled.</summary>
    public static Task<string?> OpenFile(string title, string[] filters, string? folder = null) =>
        Pick(title, FileDialog.FileModeEnum.OpenFile, filters, folder).ContinueWith(t => t.Result is { Length: > 0 } r ? r[0] : null, TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>Asks for files to open (empty when cancelled).</summary>
    public static Task<string[]> OpenFiles(string title, string[] filters, string? folder = null) =>
        Pick(title, FileDialog.FileModeEnum.OpenFiles, filters, folder);

    /// <summary>Asks for a folder. Null when cancelled.</summary>
    public static Task<string?> OpenFolder(string title, string? folder = null) =>
        Pick(title, FileDialog.FileModeEnum.OpenDir, [], folder).ContinueWith(t => t.Result is { Length: > 0 } r ? r[0] : null, TaskScheduler.FromCurrentSynchronizationContext());

    private static Task<string[]> Pick(string title, FileDialog.FileModeEnum mode, string[] filters, string? folder, string? name = null)
    {
        var done = new TaskCompletionSource<string[]>();
        var dialog = new FileDialog
        {
            Title = title,
            FileMode = mode,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Filters = filters
        };
        if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
            dialog.CurrentDir = folder;
        if (name is not null)
            dialog.CurrentFile = name;
        void Finish(string[] result)
        {
            done.TrySetResult(result);
            dialog.QueueFree();
        }
        dialog.FileSelected += path => Finish([path]);
        dialog.FilesSelected += paths => Finish(paths);
        dialog.DirSelected += path => Finish([path]);
        dialog.Canceled += () => Finish([]);
        Root.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
        return done.Task;
    }

    /// <summary>Shows a message (OK closes it).</summary>
    public static Task Message(string title, string text) => Confirm(title, text, "OK", null);

    /// <summary>Asks a question: true for <paramref name="ok"/>, false for <paramref name="cancel"/> (or a close).</summary>
    public static Task<bool> Confirm(string title, string text, string ok = "OK", string? cancel = "Cancel")
    {
        var done = new TaskCompletionSource<bool>();
        var dialog = new ConfirmationDialog { Title = title.ToUpperInvariant(), DialogText = text, OkButtonText = ok, DialogAutowrap = true, MinSize = new Vector2I(460, 0) };
        if (cancel is null)
            dialog.GetCancelButton().Visible = false;
        else
            dialog.CancelButtonText = cancel;
        dialog.Confirmed += () => { done.TrySetResult(true); dialog.QueueFree(); };
        dialog.Canceled += () => { done.TrySetResult(false); dialog.QueueFree(); };
        Root.AddChild(dialog);
        dialog.PopupCentered();
        return done.Task;
    }

    /// <summary>Asks for a text (null when cancelled); <paramref name="multiline"/> gives a larger box.</summary>
    public static Task<string?> Ask(string title, string text, string initial = "", bool multiline = false)
    {
        var done = new TaskCompletionSource<string?>();
        var dialog = new ConfirmationDialog { Title = title.ToUpperInvariant(), OkButtonText = "OK", MinSize = new Vector2I(520, 0) };
        var column = new VBoxContainer();
        // A wrapped label needs its width: without it Godot measures it as narrow as can be, very tall, and the
        // dialog grows down out of the screen.
        var label = Eve.Text_(text, Eve.Text, 13, wrap: true);
        label.CustomMinimumSize = new Vector2(480, 0);
        column.AddChild(label);
        Control field;
        Func<string> value;
        if (multiline)
        {
            var edit = new TextEdit { Text = initial, CustomMinimumSize = new Vector2(480, 120), WrapMode = TextEdit.LineWrappingMode.Boundary };
            field = edit;
            value = () => edit.Text;
        }
        else
        {
            var edit = new LineEdit { Text = initial, CustomMinimumSize = new Vector2(480, 0) };
            edit.TextSubmitted += _ => { done.TrySetResult(edit.Text); dialog.QueueFree(); };
            field = edit;
            value = () => edit.Text;
        }
        column.AddChild(field);
        dialog.AddChild(column);
        dialog.Confirmed += () => { done.TrySetResult(value()); dialog.QueueFree(); };
        dialog.Canceled += () => { done.TrySetResult(null); dialog.QueueFree(); };
        Root.AddChild(dialog);
        // The size of its content, not more.
        dialog.ResetSize();
        dialog.PopupCentered();
        field.GrabFocus();
        return done.Task;
    }

    /// <summary>Puts <paramref name="text"/> on the clipboard.</summary>
    public static void Copy(string text) => DisplayServer.ClipboardSet(text);
}
