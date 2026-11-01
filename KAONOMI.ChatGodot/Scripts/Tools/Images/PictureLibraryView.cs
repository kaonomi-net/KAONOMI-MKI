using System;
using System.IO;
using System.Linq;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Images;

/// <summary>
/// /pictures: the folders of the library (the path from its root, the parent folder, the sub-folders), the pictures of
/// the folder as thumbnails (filtered by name, sorted, sized by a slider, by pages with Show more), the selected one
/// previewed with its metadata over it while the pointer is there, previous / next, its details, Rename, Rotate left /
/// right, Open, Show in folder, Copy path and Trash. A double-click on a thumbnail or on the preview opens the picture
/// in the image analysis window. Keys on a thumbnail: Enter opens, Delete (⌘⌫) moves to the trash, [ / ] rotate,
/// ← / → previous / next; F5 reads the folder again. Pictures dropped on the chat from the Finder open in the image
/// analysis window, a folder of the library dropped opens it; a thumbnail can be dragged inside the chat.
/// </summary>
[ToolView(typeof(PictureLibraryViewModel), "Picture library", "🖼", 1400, 880)]
public sealed class PictureLibraryView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (PictureLibraryViewModel)viewModel;
        void OpenAnalysis() => ImageKit.OpenAnalysis(shell, vm.CreateAnalysis, vm.SelectedPicture?.File.Path, () => Bind.Run(vm.OpenInSystemCommand));

        // ── Header: path, folder actions; search, sort, size ──
        var crumbs = new HBoxContainer();
        crumbs.AddThemeConstantOverride("separation", 0);
        Bind.Items(crumbs, vm.Breadcrumbs, folder =>
        {
            var button = Eve.Eve.Button(folder.Name, tooltip: folder.Path);
            button.Flat = true;
            button.AddThemeColorOverride("font_color", Eve.Eve.TextBright);
            button.Pressed += () => Bind.Run(vm.OpenFolderCommand, folder);
            DropTarget(button, vm, folder);
            return Ui.Row(0, Eve.Eve.Text_("›", Eve.Eve.Muted, 13), button);
        });
        var title = Eve.Eve.Caption("Pictures", Eve.Eve.TextBright, 15);
        title.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var top = Ui.Row(8,
            title,
            Ui.Command("↑", vm.UpCommand, tooltip: "Parent folder"),
            Ui.Fill(new ScrollContainer { VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 34) }.With(crumbs)),
            Ui.Command("OPEN FOLDER", vm.OpenFolderInSystemCommand, tooltip: "Open this folder in the Finder / Explorer"),
            Ui.Command("⟳", vm.RefreshCommand, tooltip: "Read the folder again (F5)"));

        var search = Ui.Line(vm, nameof(vm.SearchText), "Filter the pictures of this folder by name…");
        var sort = Bind.Options(new OptionButton { CustomMinimumSize = new Vector2(150, 0) }, vm.SortModes, vm, nameof(vm.SortMode));
        var size = Bind.Range(new HSlider { MinValue = 90, MaxValue = 320, Step = 1, CustomMinimumSize = new Vector2(180, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, TooltipText = "Size of the thumbnails" },
            vm, nameof(vm.ThumbnailSize));
        var filters = Ui.Row(8, search, Eve.Eve.Caption("Sort"), sort, Eve.Eve.Caption("Size"), size);
        foreach (var caption in filters.GetChildren().OfType<Label>())
            caption.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var header = Ui.Toolbar(Ui.Margin(Ui.Column(8, top, filters), 6, 0, 6, 0));

        // ── Folders ──
        var folders = new HFlowContainer();
        folders.AddThemeConstantOverride("h_separation", 8);
        folders.AddThemeConstantOverride("v_separation", 8);
        Bind.Items(folders, vm.Folders, folder =>
        {
            var button = Eve.Eve.Button("📁  " + folder.Name, tooltip: folder.Path);
            button.CustomMinimumSize = new Vector2(170, 40);
            button.Alignment = HorizontalAlignment.Left;
            button.ClipText = true;
            button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            button.Pressed += () => Bind.Run(vm.OpenFolderCommand, folder);
            DropTarget(button, vm, folder);
            return button;
        });
        var folderScroll = Ui.Scroll(folders);
        folderScroll.SizeFlagsVertical = Control.SizeFlags.Fill;
        // As high as its folders, up to 150 pixels (then it scrolls).
        folders.SortChildren += () => folderScroll.CustomMinimumSize = new Vector2(0, Math.Min(150, folders.GetCombinedMinimumSize().Y));
        Bind.Visible(folderScroll, vm, nameof(vm.HasFolders));

        // ── Thumbnails ──
        var grid = new HFlowContainer();
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        var gridScroll = Ui.Scroll(grid);
        Bind.Items(grid, vm.Pictures, item => Thumbnail(vm, item, gridScroll, OpenAnalysis));
        var empty = Ui.Placeholder("No picture here");
        empty.MouseFilter = Control.MouseFilterEnum.Ignore;
        Bind.Visible(empty, vm, nameof(vm.IsEmpty));
        var gridArea = new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        gridScroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        empty.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        gridArea.AddChild(gridScroll);
        gridArea.AddChild(empty);
        var more = Bind.Visible(Ui.Command("", vm.ShowMoreCommand), vm, nameof(vm.HasMore));
        Bind.Text(more, vm, nameof(vm.MoreLabel));
        more.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        var left = Ui.Column(8, folderScroll, gridArea, more);

        // ── The selected picture ──
        var right = Ui.Column(8, Preview(vm, OpenAnalysis), Ui.Swap(vm, nameof(vm.SelectedPicture), value => value is PictureItemViewModel item ? Details(vm, item) : null));
        right.GetChild<Control>(1).SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;

        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var leftMargin = Ui.Margin(left, 4, 6, 4, 0);
        leftMargin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        leftMargin.CustomMinimumSize = new Vector2(240, 0);
        split.AddChild(leftMargin);
        split.AddChild(Ui.Margin(right, 4, 6, 4, 0));
        right.CustomMinimumSize = new Vector2(380, 0);

        // ── Status ──
        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var library = Eve.Eve.Text_($"· library: {vm.RootDirectory}", Eve.Eve.Muted with { A = 0.7f }, 11);
        library.ClipText = true;
        library.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        library.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var statusRow = status.GetChild<HBoxContainer>(0);
        statusRow.GetChildren().OfType<Label>().Last().SizeFlagsStretchRatio = 2;
        statusRow.AddChild(library);

        var page = Ui.Column(4, header, split, status);

        // The confirmation of the trash, with a dialog of the chat.
        vm.ConfirmAsync = (message, label) => Dialogs.Confirm(label, message, label);
        Bind.OnGone(page, () => vm.ConfirmAsync = (_, _) => System.Threading.Tasks.Task.FromResult(false));

        ImageKit.Keys(page, shell, key =>
        {
            if (key.Keycode != Key.F5)
                return false;
            Bind.Run(vm.RefreshCommand);
            return true;
        });

        // Pictures dropped from the Finder open in the image analysis window; a folder of the library opens.
        ImageKit.FilesDropped(page, shell, files =>
        {
            foreach (var file in files)
            {
                if (Directory.Exists(file) && Path.GetFullPath(file).StartsWith(vm.RootDirectory, StringComparison.Ordinal))
                {
                    Bind.Run(vm.OpenFolderCommand, new PictureFolder(Path.GetFileName(file), Path.GetFullPath(file)));
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
        void Lit(bool on) => button.Modulate = on ? new Color(0.65f, 1.35f, 1.5f) : Colors.White;
        bool CanDrop(Vector2 at, Variant data)
        {
            var target = System.IO.Path.GetFullPath(folder.Path);
            // At least one picture not in that folder already.
            var can = ImageKit.PicturePaths(data).Any(p => PictureLibrary.IsPicture(p)
                && !string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(p)), target, StringComparison.OrdinalIgnoreCase));
            Lit(can);
            return can;
        }
        void Drop(Vector2 at, Variant data)
        {
            Lit(false);
            vm.MoveToFolder(ImageKit.PicturePaths(data), folder);
        }
        button.SetDragForwarding(new Callable(), Callable.From<Vector2, Variant, bool>(CanDrop), Callable.From<Vector2, Variant>(Drop));
        button.MouseExited += () => Lit(false);
        button.TooltipText = folder.Path + "\nDrop pictures here to move them into this folder";
    }

    /// <summary>A thumbnail of the grid: the picture (its format until it is decoded), a progress while it loads, its name.</summary>
    private static Control Thumbnail(PictureLibraryViewModel vm, PictureItemViewModel item, ScrollContainer scroll, Action openAnalysis)
    {
        var tile = new PictureTile { TooltipText = item.Name, DragPath = () => item.File.Path };
        var picture = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        ImageKit.BindBitmap(picture, item, nameof(item.Thumbnail));
        var format = Eve.Eve.Caption(item.Format, Eve.Eve.Muted, 13);
        format.HorizontalAlignment = HorizontalAlignment.Center;
        format.VerticalAlignment = VerticalAlignment.Center;
        format.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        Bind.Visible(format, item, nameof(item.HasThumbnail), invert: true);
        var loading = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(40, 3), MouseFilter = Control.MouseFilterEnum.Ignore };
        loading.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        loading.Position += new Vector2(-20, -10);
        Bind.Visible(loading, item, nameof(item.IsLoading));
        var frame = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
        frame.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Raised(0.03f), new Color(0, 0, 0, 0), 0, 0));
        picture.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        frame.AddChild(picture);
        frame.AddChild(format);
        frame.AddChild(loading);
        var name = Eve.Eve.Text_(item.Name, Eve.Eve.Text, 11);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.MouseFilter = Control.MouseFilterEnum.Ignore;
        var column = Ui.Column(4, frame, name);
        column.MouseFilter = Control.MouseFilterEnum.Ignore;
        tile.AddChild(column);

        Bind.When(tile, vm, () =>
        {
            var side = (float)vm.ThumbnailSize;
            frame.CustomMinimumSize = new Vector2(side, side);
            name.CustomMinimumSize = new Vector2(side, 0);
            column.CustomMinimumSize = new Vector2(side, 0);
            tile.CustomMinimumSize = Vector2.Zero;
            tile.Size = Vector2.Zero;
        }, nameof(vm.ThumbnailSize));
        Bind.When(tile, vm, () =>
        {
            var selected = ReferenceEquals(vm.SelectedPicture, item);
            if (selected && !tile.Selected)
                Callable.From(() =>
                {
                    if (GodotObject.IsInstanceValid(tile) && GodotObject.IsInstanceValid(scroll))
                        scroll.EnsureControlVisible(tile);
                }).CallDeferred();
            tile.Selected = selected;
        }, nameof(vm.SelectedPicture));

        tile.Clicked += () => vm.SelectedPicture = item;
        // A right click on a picture offers to annotate it or to make it the wallpaper.
        tile.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } right)
            {
                vm.SelectedPicture = item;
                Data.Annotations.ShowMenu(tile, right.Position,
                    new AnnotationTarget(AnnotationTargetTypes.Image, item.File.Path, item.Name));
            }
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
        tile.KeyPressed = key =>
        {
            switch (key.Keycode)
            {
                case Key.Enter or Key.KpEnter:
                    Bind.Run(vm.OpenInSystemCommand);
                    return true;
                case Key.Delete:
                case Key.Backspace when ImageKit.Command(key):
                    Bind.Run(vm.DeleteCommand);
                    return true;
                case Key.Left:
                    Bind.Run(vm.PreviousCommand);
                    return true;
                case Key.Right:
                    Bind.Run(vm.NextCommand);
                    return true;
            }
            if (key.Keycode == Key.Bracketleft || key.Unicode == '[')
            {
                Bind.Run(vm.RotateLeftCommand);
                return true;
            }
            if (key.Keycode == Key.Bracketright || key.Unicode == ']')
            {
                Bind.Run(vm.RotateRightCommand);
                return true;
            }
            return false;
        };
        return tile;
    }

    /// <summary>
    /// The preview of the selected picture: previous / next over it, the hint "Metadata on hover", and every metadata
    /// of the picture over it while the pointer is there (the wheel scrolls them); a double-click opens the analysis.
    /// </summary>
    private static Control Preview(PictureLibraryViewModel vm, Action openAnalysis)
    {
        var area = new HoverPanel { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true, MouseFilter = Control.MouseFilterEnum.Stop };
        area.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.35f), Eve.Eve.Edge with { A = 0.35f }, 0));
        area.TooltipText = "Double-click: open in the image analysis window";
        var holder = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        area.AddChild(holder);

        var picture = Ui.Picture(minHeight: 200);
        picture.MouseFilter = Control.MouseFilterEnum.Ignore;
        picture.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        picture.OffsetLeft = picture.OffsetTop = 6;
        picture.OffsetRight = picture.OffsetBottom = -6;
        ImageKit.BindBitmap(picture, vm, nameof(vm.Preview));
        holder.AddChild(picture);

        var loading = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(80, 3), MouseFilter = Control.MouseFilterEnum.Ignore };
        loading.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        loading.Position -= new Vector2(40, 0);
        Bind.Visible(loading, vm, nameof(vm.IsPreviewLoading));
        holder.AddChild(loading);

        var none = Ui.Placeholder("Select a picture");
        none.MouseFilter = Control.MouseFilterEnum.Ignore;
        none.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        Bind.Visible(none, vm, nameof(vm.HasSelection), invert: true);
        holder.AddChild(none);

        // The hint, while the pointer is elsewhere.
        var hint = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        hint.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.6f), Eve.Eve.Edge with { A = 0.4f }, 4));
        hint.AddChild(Eve.Eve.Text_("ⓘ Metadata on hover", Eve.Eve.TextBright, 11));
        hint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        hint.GrowHorizontal = Control.GrowDirection.Begin;
        hint.OffsetRight = -8;
        hint.OffsetTop = 8;
        holder.AddChild(hint);

        // Every metadata, over the picture while the pointer is there.
        var metadata = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        metadata.AddThemeStyleboxOverride("panel", Eve.Eve.Box(new Color(0.05f, 0.06f, 0.07f, 0.92f), Eve.Eve.Edge with { A = 0.4f }, 12));
        metadata.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        metadata.OffsetLeft = metadata.OffsetTop = 6;
        metadata.OffsetRight = metadata.OffsetBottom = -6;
        var metadataScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, MouseFilter = Control.MouseFilterEnum.Pass };
        metadata.AddChild(metadataScroll);
        Bind.When(metadataScroll, vm, () =>
        {
            foreach (var child in metadataScroll.GetChildren())
                child.QueueFree();
            // The overlay is dark whatever the theme (it is over the picture): light keys and values, whatever the theme.
            var content = ImageKit.Metadata(vm.Metadata, Eve.Eve.Amber, 150, new Color(0.7f, 0.75f, 0.8f), new Color(0.95f, 0.97f, 1f));
            content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            metadataScroll.AddChild(content);
        }, nameof(vm.Metadata));
        holder.AddChild(metadata);

        // Previous / next.
        Button Step(string text, System.Windows.Input.ICommand command, string tooltip, bool rightSide)
        {
            var button = Ui.Command(text, command, tooltip: tooltip);
            button.AddThemeFontSizeOverride("font_size", 22);
            button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Sunken(0.55f), Eve.Eve.Edge with { A = 0.4f }, 8));
            button.SetAnchorsAndOffsetsPreset(rightSide ? Control.LayoutPreset.CenterRight : Control.LayoutPreset.CenterLeft);
            button.GrowHorizontal = rightSide ? Control.GrowDirection.Begin : Control.GrowDirection.End;
            button.GrowVertical = Control.GrowDirection.Both;
            if (rightSide)
                button.OffsetRight = -6;
            else
                button.OffsetLeft = 6;
            Bind.Visible(button, vm, nameof(vm.HasSelection));
            return button;
        }
        holder.AddChild(Step("‹", vm.PreviousCommand, "Previous picture", false));
        holder.AddChild(Step("›", vm.NextCommand, "Next picture", true));

        void Refresh()
        {
            metadata.Visible = vm.HasMetadata && area.Hovered;
            hint.Visible = vm.HasMetadata && !area.Hovered;
        }
        area.HoverChanged += _ => Refresh();
        Bind.When(area, vm, Refresh, nameof(vm.HasMetadata));
        area.DoubleClicked += openAnalysis;
        return area;
    }

    /// <summary>The details and actions of the selected picture.</summary>
    private static Control Details(PictureLibraryViewModel vm, PictureItemViewModel item)
    {
        var name = Eve.Eve.Text_(item.Name, Eve.Eve.TextBright, 14, wrap: true);
        var meta = Ui.Row(10, Eve.Eve.Text_(item.Format, Eve.Eve.Muted, 11), Eve.Eve.Text_(item.SizeLabel, Eve.Eve.Muted, 11), Eve.Eve.Text_(item.DateLabel, Eve.Eve.Muted, 11));
        var dimensions = Ui.Label(vm, nameof(vm.Dimensions), Eve.Eve.Muted, 11);
        var path = Eve.Eve.Text_(item.File.Path, Eve.Eve.Muted with { A = 0.8f }, 10, wrap: true);

        var rename = Ui.Line(vm, nameof(vm.NewName));
        rename.TextSubmitted += _ => Bind.Run(vm.RenameCommand);
        var renameRow = Ui.Row(6, rename, Ui.Command("RENAME", vm.RenameCommand));

        var rotate = new HFlowContainer();
        rotate.AddThemeConstantOverride("h_separation", 6);
        rotate.AddChild(Ui.Command("⟲ ROTATE LEFT", vm.RotateLeftCommand, tooltip: "Turn the picture a quarter to the left and save it ([)"));
        rotate.AddChild(Ui.Command("ROTATE RIGHT ⟳", vm.RotateRightCommand, tooltip: "Turn the picture a quarter to the right and save it (])"));

        var actions = new HFlowContainer();
        actions.AddThemeConstantOverride("h_separation", 6);
        actions.AddThemeConstantOverride("v_separation", 6);
        actions.AddChild(Ui.Command("OPEN", vm.OpenInSystemCommand, primary: true, tooltip: "Open with the default application (Enter on a thumbnail)"));
        actions.AddChild(Ui.Command("SHOW IN FOLDER", vm.RevealCommand, tooltip: "Show the file in the Finder / Explorer"));
        actions.AddChild(Ui.Action("COPY PATH", () =>
        {
            Dialogs.Copy(item.File.Path);
            vm.Status = $"Path copied: {item.File.Path}";
        }));
        actions.AddChild(Ui.Command("🗑 TRASH", vm.DeleteCommand, tooltip: "Move to the trash (Delete)"));

        return Ui.Column(4, name, meta, dimensions, path, Ui.Margin(Eve.Eve.Caption("Rename"), 0, 8, 0, 0), renameRow, Ui.Margin(rotate, 0, 6, 0, 0), actions);
    }
}

internal static class ControlExtensions
{
    /// <summary><paramref name="parent"/> with <paramref name="child"/> added.</summary>
    public static T With<T>(this T parent, Control child) where T : Node
    {
        parent.AddChild(child);
        return parent;
    }

    /// <summary><paramref name="control"/> after <paramref name="change"/>.</summary>
    public static T Also<T>(this T control, Action<T> change) where T : Node
    {
        change(control);
        return control;
    }
}
