using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Displays extracted code blocks with copy and save actions.</summary>
public partial class CodeView : UserControl
{
    /// <summary>Initializes the code viewer layout.</summary>
    public CodeView()
    {
        InitializeComponent();
    }

    private CodeWindowViewModel? ViewModel => DataContext as CodeWindowViewModel;

    /// <summary>File pickers of the window hosting this view.</summary>
    private IStorageProvider Storage => TopLevel.GetTopLevel(this)!.StorageProvider;

    /// <summary>Copies the selected snippet source to the system clipboard.</summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedSnippet is not { } selected || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;

        try
        {
            await clipboard.SetTextAsync(selected.Snippet.Code);
            ViewModel.Status = "Code copied to the clipboard";
            ActivityLog.Info("Code", $"{selected.Snippet.DisplayLanguage} code copied to the clipboard ({selected.Snippet.Code.Length} chars)");
        }
        catch (Exception ex)
        {
            ViewModel.Status = $"Copy failed: {ex.Message}";
            ActivityLog.Error("Code", "Code copy to the clipboard failed", ex);
        }
    }

    /// <summary>Writes the selected snippet to a user-chosen file path.</summary>
    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedSnippet is not { } selected)
            return;

        var snippet = selected.Snippet;
        try
        {
            var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save code",
                SuggestedFileName = selected.SuggestedFileName,
                DefaultExtension = snippet.FileExtension.TrimStart('.'),
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType(snippet.DisplayLanguage) { Patterns = [$"*{snippet.FileExtension}"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] }
                ]
            });

            if (file is null)
                return;

            await using (var stream = await file.OpenWriteAsync())
            await using (var writer = new StreamWriter(stream))
            {
                if (stream.CanSeek)
                    stream.SetLength(0);

                await writer.WriteAsync(snippet.Code);
            }

            ViewModel.Status = $"Saved: {file.Name}";
            ActivityLog.Info("Code", $"{snippet.DisplayLanguage} code saved to {file.Name}");
        }
        catch (Exception ex)
        {
            ViewModel.Status = $"Save failed: {ex.Message}";
            ActivityLog.Error("Code", "Code save failed", ex);
        }
    }
}
