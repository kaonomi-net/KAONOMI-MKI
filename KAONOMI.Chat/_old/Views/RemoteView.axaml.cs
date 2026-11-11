using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>
/// Remote control of the chatbot (<c>/remote</c>, <c>/nremote</c>): what it says (or a script of
/// <c>~/kaonomi/script</c>), its voice, effects, system prompt and 3D animation, the presets and the Piper voices.
/// </summary>
public partial class RemoteView : UserControl
{
    private static readonly FilePickerFileType TextFiles = new("Script (text)") { Patterns = ["*.txt"] };

    /// <summary>Initializes the view (Ctrl+S saves the script).</summary>
    public RemoteView()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                OnSaveScript(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
    }

    private RemoteWindowViewModel? ViewModel => DataContext as RemoteWindowViewModel;

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SayBox.Focus();
    }

    /// <summary>The script folder (created when missing), for the pickers.</summary>
    private async Task<IStorageFolder?> ScriptFolderAsync(IStorageProvider storage, RemoteWindowViewModel viewModel)
    {
        Directory.CreateDirectory(viewModel.ScriptDirectory);
        return await storage.TryGetFolderFromPathAsync(viewModel.ScriptDirectory);
    }

    /// <summary>Opens a script of ~/kaonomi/script into the text.</summary>
    private async void OnOpenScript(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a script",
            AllowMultiple = false,
            SuggestedStartLocation = await ScriptFolderAsync(storage, viewModel),
            FileTypeFilter = [TextFiles, new FilePickerFileType("All files") { Patterns = ["*"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            viewModel.OpenScript(path);
    }

    /// <summary>Saves the script into its file, or asks for a name the first time.</summary>
    private void OnSaveScript(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        if (viewModel.ScriptPath is { } path)
            viewModel.SaveScript(path);
        else
            OnSaveScriptAs(sender, e);
    }

    /// <summary>Saves the script as a .txt file of ~/kaonomi/script chosen in the save dialog.</summary>
    private async void OnSaveScriptAs(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the script",
            SuggestedFileName = viewModel.ScriptPath is { } current ? Path.GetFileName(current) : $"script-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            SuggestedStartLocation = await ScriptFolderAsync(storage, viewModel),
            DefaultExtension = "txt",
            ShowOverwritePrompt = true,
            FileTypeChoices = [TextFiles]
        });
        if (file?.TryGetLocalPath() is { } path)
            viewModel.SaveScript(Path.HasExtension(path) ? path : path + ".txt");
    }
}
