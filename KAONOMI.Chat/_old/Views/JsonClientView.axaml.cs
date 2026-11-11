using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Sends HTTP JSON requests and loads saved presets.</summary>
public partial class JsonClientView : UserControl
{
    /// <summary>Initializes the view and binds Enter on the URL field to send.</summary>
    public JsonClientView()
    {
        InitializeComponent();
        UrlBox.KeyDown += OnUrlKeyDown;
    }

    private JsonClientWindowViewModel? ViewModel => DataContext as JsonClientWindowViewModel;

    /// <summary>File pickers of the window hosting this view.</summary>
    private IStorageProvider Storage => TopLevel.GetTopLevel(this)!.StorageProvider;

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        UrlBox.Focus();
    }

    /// <summary>Sends the request when Enter is pressed in the URL box.</summary>
    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { } viewModel)
            return;

        e.Handled = true;
        if (viewModel.SendCommand.CanExecute(null))
            viewModel.SendCommand.Execute(null);
    }

    /// <summary>Opens a saved JSON request file from the collection folder.</summary>
    private async void OnOpenQueryClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        Directory.CreateDirectory(viewModel.CollectionDirectory);
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open request",
            AllowMultiple = false,
            SuggestedStartLocation = await Storage.TryGetFolderFromPathAsync(viewModel.CollectionDirectory),
            FileTypeFilter =
            [
                new FilePickerFileType("JSON request") { Patterns = ["*.json"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });

        if (files is [var file] && file.TryGetLocalPath() is { } path)
            await viewModel.LoadQueryAsync(path);
    }
}
