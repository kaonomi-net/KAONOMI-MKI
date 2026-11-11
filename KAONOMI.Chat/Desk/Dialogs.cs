using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace KAONOMI.UI.Desk;

/// <summary>
/// The dialogs of the chat, with the same calls as those of the Godot chat: the file pickers of the system (save, open
/// one or several files, a folder; filters written <c>"*.png, *.jpg ; Pictures"</c>), and small modal dialogs (a
/// message, a confirmation, a text to type), awaited as tasks.
/// </summary>
public static class Dialogs
{
    public static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static IStorageProvider? Storage => Owner?.StorageProvider;

    private static List<FilePickerFileType> Types(string[] filters) => filters.Select(f =>
    {
        var parts = f.Split(';', 2);
        var patterns = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return new FilePickerFileType(parts.Length > 1 ? parts[1].Trim() : parts[0].Trim()) { Patterns = patterns };
    }).ToList();

    private static async Task<IStorageFolder?> Folder(string? folder) =>
        Storage is { } storage && !string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder)
            ? await storage.TryGetFolderFromPathAsync(folder)
            : null;

    /// <summary>Asks where to save a file. Null when cancelled.</summary>
    public static async Task<string?> SaveFile(string title, string suggestedName, string[] filters, string? folder = null)
    {
        if (Storage is not { } storage)
            return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = Types(filters),
            SuggestedStartLocation = await Folder(folder)
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Asks for a file to open. Null when cancelled.</summary>
    public static async Task<string?> OpenFile(string title, string[] filters, string? folder = null) =>
        (await Open(title, filters, folder, false)).FirstOrDefault();

    /// <summary>Asks for files to open (empty when cancelled).</summary>
    public static Task<string[]> OpenFiles(string title, string[] filters, string? folder = null) => Open(title, filters, folder, true);

    private static async Task<string[]> Open(string title, string[] filters, string? folder, bool many)
    {
        if (Storage is not { } storage)
            return [];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = many,
            FileTypeFilter = filters.Length == 0 ? null : Types(filters),
            SuggestedStartLocation = await Folder(folder)
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
    }

    /// <summary>Asks for a folder. Null when cancelled.</summary>
    public static async Task<string?> OpenFolder(string title, string? folder = null)
    {
        if (Storage is not { } storage)
            return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, SuggestedStartLocation = await Folder(folder) });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public static Task Message(string title, string text) => Confirm(title, text, "OK", null);

    /// <summary>A question with <paramref name="ok"/> and <paramref name="cancel"/> (none: a message); true for <paramref name="ok"/>.</summary>
    public static async Task<bool> Confirm(string title, string text, string ok = "OK", string? cancel = "Cancel")
    {
        var result = false;
        var dialog = Make(title);
        var okButton = Ui.Button(ok, () => { result = true; dialog.Close(); }, primary: true);
        okButton.IsDefault = true;
        var buttons = Ui.Row(8, okButton);
        if (cancel is not null)
        {
            var cancelButton = Ui.Button(cancel, dialog.Close);
            cancelButton.IsCancel = true;
            buttons.Children.Add(cancelButton);
        }
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new Border { Padding = new Thickness(18), Child = Ui.Column(16, Ui.Label(text, wrap: true), buttons) };
        await Show(dialog);
        return result;
    }

    /// <summary>A text to type; null when cancelled.</summary>
    public static async Task<string?> Ask(string title, string text, string initial = "", bool multiline = false)
    {
        string? result = null;
        var dialog = Make(title);
        var box = new TextBox { Text = initial, AcceptsReturn = multiline, TextWrapping = multiline ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap, MinHeight = multiline ? 140 : 0 };
        var okButton = Ui.Button("OK", () => { result = box.Text ?? string.Empty; dialog.Close(); }, primary: true);
        okButton.IsDefault = !multiline;
        var cancel = Ui.Button("Cancel", dialog.Close);
        cancel.IsCancel = true;
        var buttons = Ui.Row(8, okButton, cancel);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new Border { Padding = new Thickness(18), Child = Ui.Column(12, Ui.Label(text, wrap: true), box, buttons) };
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await Show(dialog);
        return result;
    }

    public static void Copy(string text) => _ = Owner?.Clipboard?.SetTextAsync(text);

    private static Window Make(string title) => new()
    {
        Title = title,
        Width = 480,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false
    };

    private static Task Show(Window dialog) => Owner is { IsVisible: true } owner ? dialog.ShowDialog(owner) : ShowAlone(dialog);

    private static Task ShowAlone(Window dialog)
    {
        var done = new TaskCompletionSource();
        dialog.Closed += (_, _) => done.TrySetResult();
        dialog.Show();
        return done.Task;
    }
}
