using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Picture library (<c>/pictures</c>): folders, thumbnails and the selected picture.</summary>
public partial class PictureLibraryView : UserControl
{
    private PictureLibraryViewModel? _viewModel;

    /// <summary>Press on a thumbnail, kept until the pointer moves far enough to start dragging the picture.</summary>
    private PointerPressedEventArgs? _press;
    private PictureItemViewModel? _pressedPicture;
    private Point _pressPoint;

    /// <summary>Initializes the view: Enter opens, Delete moves to the trash, [ / ] rotate, F5 refreshes, double-click opens.</summary>
    public PictureLibraryView()
    {
        InitializeComponent();
        PictureGrid.DoubleTapped += (_, _) => _viewModel?.OpenInSystemCommand.Execute(null);
        PictureGrid.KeyDown += (_, e) =>
        {
            if (_viewModel is not { } viewModel)
                return;
            if (e.Key == Key.Enter && viewModel.OpenInSystemCommand.CanExecute(null))
            {
                viewModel.OpenInSystemCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && viewModel.DeleteCommand.CanExecute(null))
            {
                viewModel.DeleteCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.OemOpenBrackets && viewModel.RotateLeftCommand.CanExecute(null))
            {
                viewModel.RotateLeftCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.OemCloseBrackets && viewModel.RotateRightCommand.CanExecute(null))
            {
                viewModel.RotateRightCommand.Execute(null);
                e.Handled = true;
            }
        };
        // A thumbnail dragged to the journal or to the calculator tape (Edit mode) becomes an image line there.
        PictureGrid.AddHandler(PointerPressedEvent, (_, e) =>
        {
            var props = e.GetCurrentPoint(PictureGrid).Properties;
            _pressedPicture = props.IsLeftButtonPressed && (e.Source as Control)?.DataContext is PictureItemViewModel picture ? picture : null;
            _press = _pressedPicture is null ? null : e;
            _pressPoint = e.GetPosition(PictureGrid);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        PictureGrid.AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        PictureGrid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F5 && _viewModel is { } viewModel)
            {
                viewModel.RefreshCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.ConfirmAsync = (_, _) => System.Threading.Tasks.Task.FromResult(false);
        _viewModel = DataContext as PictureLibraryViewModel;
        if (_viewModel is not null)
            _viewModel.ConfirmAsync = (message, label) => ConfirmDialog.AskAsync((Window)TopLevel.GetTopLevel(this)!, message, label);
    }

    /// <summary>Starts dragging the pressed picture once the pointer has moved a few pixels.</summary>
    private async void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressedPicture is not { } picture)
            return;
        var delta = e.GetPosition(PictureGrid) - _pressPoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
            return;
        _press = null;
        var path = picture.File.Path;
        var files = new List<IStorageItem>();
        if (TopLevel.GetTopLevel(this)?.StorageProvider is { } storage && await storage.TryGetFileFromPathAsync(path) is { } file)
            files.Add(file);
        var result = await DragDrop.DoDragDropAsync(press, PictureDragDrop.Create([path], files), DragDropEffects.Copy);
        if (result != DragDropEffects.None && _viewModel is { } viewModel)
            viewModel.Status = $"Picture dropped: {picture.Name}";
    }

    /// <summary>Copies the path of the selected picture to the clipboard.</summary>
    private async void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedPicture is { } picture && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(picture.File.Path);
            _viewModel.Status = $"Path copied: {picture.File.Path}";
        }
    }
}
