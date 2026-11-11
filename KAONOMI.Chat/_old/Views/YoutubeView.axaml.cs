using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>YouTube videos (<c>/youtube</c>): the address of a video, its title and description, and the saved videos.</summary>
public partial class YoutubeView : UserControl
{
    private YoutubeWindowViewModel? _viewModel;

    /// <summary>Initializes the view: Enter in the search box searches, a double-click on a saved video opens it.</summary>
    public YoutubeView()
    {
        InitializeComponent();
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _viewModel is { } viewModel)
            {
                viewModel.SearchCommand.Execute(null);
                e.Handled = true;
            }
        };
        VideoList.DoubleTapped += (_, _) =>
        {
            if (_viewModel?.SelectedVideo is { } video)
                _viewModel.OpenCommand.Execute(video);
        };
        AttachedToVisualTree += (_, _) => UrlBox.Focus();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.ConfirmAsync = (_, _) => System.Threading.Tasks.Task.FromResult(false);
        _viewModel = DataContext as YoutubeWindowViewModel;
        if (_viewModel is not null)
            _viewModel.ConfirmAsync = (message, label) => ConfirmDialog.AskAsync((Window)TopLevel.GetTopLevel(this)!, message, label);
    }

    /// <summary>Puts the address of the clipboard in the address box.</summary>
    private async void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
            && await clipboard.TryGetTextAsync() is { Length: > 0 } text)
            _viewModel.Url = text.Trim();
    }
}
