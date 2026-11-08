using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// /pictures: the folders of the library (the path from its root, the parent folder, the sub-folders), the pictures of
/// the folder as thumbnails (filtered by name, sorted, sized by a slider, by pages with Show more), the selected one
/// previewed with its metadata over it while the pointer is there, previous / next, its details, Rename, Rotate left /
/// right, Open, Show in folder, Copy path and Trash. A double-click on a thumbnail or on the preview opens the picture
/// in the image analysis window. Keys on a thumbnail: Enter opens, Delete (⌘⌫) moves to the trash, [ / ] rotate,
/// ← / → previous / next; F5 reads the folder again. Pictures dropped on the window from the Finder open in the image
/// analysis window, a folder of the library dropped opens it; a thumbnail can be dragged inside the chat or onto a
/// folder (it moves there); a right click annotates a picture.
/// </summary>
[ToolView(typeof(PictureLibraryViewModel), "Picture library", "🖼", 1400, 880)]
public sealed class PictureLibraryView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (PictureLibraryViewModel)viewModel;
        void OpenAnalysis() => ImageKit.OpenAnalysis(shell, vm.CreateAnalysis, vm.SelectedPicture?.File.Path, () => Ui.Run(vm.OpenInSystemCommand));

        // ── Header: path, folder actions; search, sort, size ──
        var crumbs = Kit.Items(vm.Breadcrumbs, folder =>
        {
            var button = Ui.Button(folder.Name, () => Ui.Run(vm.OpenFolderCommand, folder), folder.Path);
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Foreground = Ui.TextBright;
            button.Padding = new Thickness(4, 2);
            DropTarget(button, vm, folder);
            return Ui.Row(0, Ui.Label("›", Ui.Muted, 13), button);
        }, () => new StackPanel { Orientation = Orientation.Horizontal });
        var top = Kit.FillRow(8,
            [Ui.Label("Pictures", Ui.TextBright, 15, bold: true), Ui.Command("↑", vm.UpCommand, tooltip: "Parent folder")],
            Kit.ScrollH(crumbs, showBar: false),
            Ui.Command("OPEN FOLDER", vm.OpenFolderInSystemCommand, tooltip: "Open this folder in the Finder / Explorer"),
            Ui.Command("⟳", vm.RefreshCommand, tooltip: "Read the folder again (F5)"));

        var search = Ui.Edit(vm, nameof(vm.SearchText), "Filter the pictures of this folder by name…");
        var sort = Kit.Options(vm.SortModes, vm, nameof(vm.SortMode));
        sort.Width = 150;
        var size = Kit.Slider(vm, nameof(vm.ThumbnailSize), 90, 320, 1, "Size of the thumbnails", 180);
        var filters = Kit.FillRow(8, [], search, Ui.Caption("Sort"), sort, Ui.Caption("Size"), size);
        var header = Kit.Toolbar(Ui.Column(8, top, filters));

        // ── Folders ──
        var folders = Kit.Items(vm.Folders, folder =>
        {
            var button = Ui.Button(string.Empty, () => Ui.Run(vm.OpenFolderCommand, folder));
            button.Content = Ui.Label("📁  " + folder.Name);
            button.Width = 170;
            button.Height = 40;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            DropTarget(button, vm, folder);
            return button;
        }, () => new WrapPanel { ItemSpacing = 8, LineSpacing = 8 });
        // As high as its folders, up to 150 pixels (then it scrolls).
        var folderScroll = new ScrollViewer { Content = folders, MaxHeight = 150 }.Visible(vm, nameof(vm.HasFolders));

        // ── Thumbnails ──
        var grid = Kit.Items(vm.Pictures, item => Thumbnail(vm, item, OpenAnalysis), () => new WrapPanel { ItemSpacing = 6, LineSpacing = 6 });
        var empty = Kit.Placeholder("No picture here").Visible(vm, nameof(vm.IsEmpty));
        var gridArea = new Grid { Children = { Ui.Scroll(grid), empty } };
        var more = Ui.Command(string.Empty, vm.ShowMoreCommand).Visible(vm, nameof(vm.HasMore));
        more.Bind(ContentControl.ContentProperty, vm, nameof(vm.MoreLabel));
        more.HorizontalAlignment = HorizontalAlignment.Center;
        var left = Kit.Rows("Auto,*,Auto", folderScroll, gridArea, more);
        folderScroll.Margin = new Thickness(0, 0, 0, 8);
        more.Margin = new Thickness(0, 8, 0, 0);

        // ── The selected picture ──
        var details = Kit.Swap(vm, nameof(vm.SelectedPicture), value => value is PictureItemViewModel item ? Details(vm, item) : null);
        details.Margin = new Thickness(0, 8, 0, 0);
        var right = Kit.Rows("*,Auto", Preview(vm, OpenAnalysis), details);

        var split = Kit.SplitH(Kit.Margin(left, 4, 6, 4, 0), Kit.Margin(right, 4, 6, 4, 0), "*", "420", 240, 380);

        // ── Status ──
        var library = Ui.Label($"· library: {vm.RootDirectory}", Ui.Muted, 11);
        library.MaxWidth = 480;
        var status = Kit.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), right: library);

        var page = Kit.Rows("Auto,*,Auto", header, split, status);

        // The confirmation of the trash, with a dialog of the chat.
        vm.ConfirmAsync = (message, label) => Dialogs.Confirm(label, message, label);
        page.DetachedFromVisualTree += (_, _) => vm.ConfirmAsync = (_, _) => System.Threading.Tasks.Task.FromResult(false);
        page.AttachedToVisualTree += (_, _) => vm.ConfirmAsync = (message, label) => Dialogs.Confirm(label, message, label);

        Kit.Keys(page, e =>
        {
            if (e.Key != Key.F5)
                return false;
            Ui.Run(vm.RefreshCommand);
            return true;
        });

        // Pictures dropped from the Finder open in the image analysis window; a folder of the library opens.
        ImageKit.FilesDropped(page, files =>
        {
            foreach (var file in files)
            {
                if (Directory.Exists(file) && Path.GetFullPath(file).StartsWith(vm.RootDirectory, StringComparison.Ordinal))
                {
                    Ui.Run(vm.OpenFolderCommand, new PictureFolder(Path.GetFileName(file), Path.GetFullPath(file)));
                    return;
                }
            }
            var pictures = files.Where(f => File.Exists(f) && PictureLibrary.IsPicture(f)).ToList();
            foreach (var picture in pictures)
                ImageKit.OpenAnalysis(shell, vm.CreateAnalysis, picture, () => PictureLibrary.OpenWithSystem(picture));
            if (pictures.Count == 0)
                vm.Status = "Only pictures (or a folder of the library) can be dropped here";
        });
        return page;
    }

    /// <summary>
    /// A folder (of the folders, or of the path at the top) that takes the pictures dragged onto it: lit while they are
    /// over it, they move into it once dropped.
    /// </summary>
    private static void DropTarget(Button button, PictureLibraryViewModel vm, PictureFolder folder)
    {
        var normal = button.BorderBrush;
        void Lit(bool on) => button.BorderBrush = on ? Ui.Accent : normal;
        bool CanDrop(IDataTransfer data)
        {
            var target = Path.GetFullPath(folder.Path);
            // At least one picture not in that folder already.
            return ImageKit.PicturePaths(data).Any(p => PictureLibrary.IsPicture(p)
                && !string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), target, StringComparison.OrdinalIgnoreCase));
        }
        DragDrop.SetAllowDrop(button, true);
        button.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var can = CanDrop(e.DataTransfer);
            Lit(can);
            e.DragEffects = can ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        });
        button.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Lit(false));
        button.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            Lit(false);
            e.Handled = true;
            if (CanDrop(e.DataTransfer))
            {
                e.DragEffects = DragDropEffects.Move;
                vm.MoveToFolder(ImageKit.PicturePaths(e.DataTransfer), folder);
            }
        });
        ToolTip.SetTip(button, folder.Path + "\nDrop pictures here to move them into this folder");
    }

    /// <summary>A thumbnail of the grid: the picture (its format until it is decoded), a progress while it loads, its name.</summary>
    private static Control Thumbnail(PictureLibraryViewModel vm, PictureItemViewModel item, Action openAnalysis)
    {
        var tile = new PictureTile { DragPath = () => item.File.Path };
        ToolTip.SetTip(tile, item.Name);
        var picture = new Image { Stretch = Stretch.UniformToFill };
        picture.Bind(Image.SourceProperty, item, nameof(item.Thumbnail));
        var format = Ui.Caption(item.Format, Ui.Muted, 13);
        format.HorizontalAlignment = HorizontalAlignment.Center;
        format.Visible(item, nameof(item.HasThumbnail), invert: true);
        var loading = new ProgressBar { IsIndeterminate = true, Width = 40, Height = 3, MinHeight = 3, MinWidth = 40, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) }
            .Visible(item, nameof(item.IsLoading));
        var frame = new Border { ClipToBounds = true, Background = Ui.Panel, Child = new Grid { Children = { picture, format, loading } } };
        var name = Ui.Label(item.Name, Ui.Text, 11);
        tile.Child = Ui.Column(4, frame, name);

        Ui.When(tile, vm, () =>
        {
            var side = vm.ThumbnailSize;
            frame.Width = frame.Height = side;
            name.Width = side;
        }, nameof(vm.ThumbnailSize));
        Ui.When(tile, vm, () =>
        {
            var selected = ReferenceEquals(vm.SelectedPicture, item);
            if (selected && !tile.Selected)
                Dispatcher.UIThread.Post(() => tile.BringIntoView(), DispatcherPriority.Background);
            tile.Selected = selected;
        }, nameof(vm.SelectedPicture));

        tile.Clicked += () => vm.SelectedPicture = item;
        // A right click on a picture offers to annotate it.
        tile.RightClicked += () =>
        {
            vm.SelectedPicture = item;
            Kit.AnnotateMenu(tile, new AnnotationTarget(AnnotationTargetTypes.Image, item.File.Path, item.Name));
        };
        tile.DoubleClicked += () =>
        {
            vm.SelectedPicture = item;
            openAnalysis();
        };
        // Dropped elsewhere (another window); moved to a folder, the status of the move stays.
        tile.Dropped += path =>
        {
            if (File.Exists(path))
                vm.Status = $"Picture dropped: {item.Name}";
        };
        tile.KeyPressed = e =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Ui.Run(vm.OpenInSystemCommand);
                    return true;
                case Key.Delete:
                case Key.Back when Kit.CommandHeld(e.KeyModifiers):
                    Ui.Run(vm.DeleteCommand);
                    return true;
                case Key.Left:
                    Ui.Run(vm.PreviousCommand);
                    return true;
                case Key.Right:
                    Ui.Run(vm.NextCommand);
                    return true;
            }
            return false;
        };
        // [ and ] as typed, whatever the keyboard layout: rotate left / right.
        tile.TextInput += (_, e) =>
        {
            if (e.Text == "[")
                Ui.Run(vm.RotateLeftCommand);
            else if (e.Text == "]")
                Ui.Run(vm.RotateRightCommand);
            else
                return;
            e.Handled = true;
        };
        return tile;
    }

    /// <summary>
    /// The preview of the selected picture: previous / next over it, the hint "Metadata on hover", and every metadata
    /// of the picture over it while the pointer is there (the wheel scrolls them); a double-click opens the analysis.
    /// </summary>
    private static Control Preview(PictureLibraryViewModel vm, Action openAnalysis)
    {
        var picture = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6) };
        picture.Bind(Image.SourceProperty, vm, nameof(vm.Preview));

        var loading = new ProgressBar { IsIndeterminate = true, Width = 80, MinWidth = 80, Height = 3, MinHeight = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            .Visible(vm, nameof(vm.IsPreviewLoading));
        var none = Kit.Placeholder("Select a picture").Visible(vm, nameof(vm.HasSelection), invert: true);

        // The hint, while the pointer is elsewhere.
        var hint = new Border
        {
            Child = Ui.Label("ⓘ Metadata on hover", Ui.TextBright, 11),
            Background = Ui.Header,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            IsHitTestVisible = false
        };

        // Every metadata, over the picture while the pointer is there (a dark panel whatever the theme).
        var metadataContent = new ContentControl();
        var metadata = new Border
        {
            Child = new ScrollViewer { Content = metadataContent },
            Background = new SolidColorBrush(Color.FromRgb(13, 15, 18)),
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Margin = new Thickness(6)
        };
        Ui.When(metadata, vm, () =>
            metadataContent.Content = ImageKit.Metadata(vm.Metadata, Ui.Warm, 150, new SolidColorBrush(Color.FromRgb(179, 191, 204)), new SolidColorBrush(Color.FromRgb(242, 247, 255))),
            nameof(vm.Metadata));

        // Previous / next.
        Button Step(string text, ICommand command, string tooltip, bool rightSide)
        {
            var button = Ui.Command(text, command, tooltip: tooltip);
            button.FontSize = 22;
            button.Background = Ui.Header;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.HorizontalAlignment = rightSide ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            button.Margin = new Thickness(6);
            return button.Visible(vm, nameof(vm.HasSelection));
        }

        var area = new Border
        {
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = new Grid
            {
                Children =
                {
                    picture, loading, none, hint, metadata,
                    Step("‹", vm.PreviousCommand, "Previous picture", false),
                    Step("›", vm.NextCommand, "Next picture", true)
                }
            }
        };
        ToolTip.SetTip(area, "Double-click: open in the image analysis window");

        void Refresh()
        {
            metadata.IsVisible = vm.HasMetadata && area.IsPointerOver;
            hint.IsVisible = vm.HasMetadata && !area.IsPointerOver;
        }
        area.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsPointerOverProperty)
                Refresh();
        };
        Ui.When(area, vm, Refresh, nameof(vm.HasMetadata));
        area.DoubleTapped += (_, e) =>
        {
            if (e.Source is not Button && (e.Source as Visual)?.FindAncestorOfType<Button>() is null)
                openAnalysis();
        };
        return area;
    }

    /// <summary>The details and actions of the selected picture.</summary>
    private static Control Details(PictureLibraryViewModel vm, PictureItemViewModel item)
    {
        var name = Ui.Label(item.Name, Ui.TextBright, 14, wrap: true);
        var meta = Ui.Row(10, Ui.Label(item.Format, Ui.Muted, 11), Ui.Label(item.SizeLabel, Ui.Muted, 11), Ui.Label(item.DateLabel, Ui.Muted, 11));
        var dimensions = Ui.Bound(vm, nameof(vm.Dimensions), Ui.Muted, 11);
        var path = Ui.Label(item.File.Path, Ui.Muted, 10, wrap: true);

        var rename = Ui.Edit(vm, nameof(vm.NewName));
        rename.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Ui.Run(vm.RenameCommand);
                e.Handled = true;
            }
        };
        var renameRow = Kit.FillRow(6, [], rename, Ui.Command("RENAME", vm.RenameCommand));
        var caption = Ui.Caption("Rename");
        caption.Margin = new Thickness(0, 8, 0, 0);

        var rotate = Kit.Flow(6,
            Ui.Command("⟲ ROTATE LEFT", vm.RotateLeftCommand, tooltip: "Turn the picture a quarter to the left and save it ([)"),
            Ui.Command("ROTATE RIGHT ⟳", vm.RotateRightCommand, tooltip: "Turn the picture a quarter to the right and save it (])"));
        rotate.Margin = new Thickness(0, 6, 0, 0);

        var actions = Kit.Flow(6,
            Ui.Command("OPEN", vm.OpenInSystemCommand, tooltip: "Open with the default application (Enter on a thumbnail)", primary: true),
            Ui.Command("SHOW IN FOLDER", vm.RevealCommand, tooltip: "Show the file in the Finder / Explorer"),
            Ui.Button("COPY PATH", () =>
            {
                Dialogs.Copy(item.File.Path);
                vm.Status = $"Path copied: {item.File.Path}";
            }),
            Ui.Command("🗑 TRASH", vm.DeleteCommand, tooltip: "Move to the trash (Delete)"));

        return Ui.Column(4, name, meta, dimensions, path, caption, renameRow, rotate, actions);
    }
}
