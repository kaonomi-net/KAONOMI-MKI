using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// /fooocus: on the left the request (prompt, negative prompt, the styles filtered and ticked, performance, size, number
/// of images, seed, model), ✨ Generate (Ctrl / ⌘ + Enter) and Stop (Esc) while it runs; on the right the selected image
/// (a click opens it in the image analysis window), its prompt and details, ↺ Reuse, Open, 📁 Folder, and the gallery
/// of the folder (a click selects, a double-click opens the image analysis, a right click annotates). The images can be
/// dragged inside the chat; pictures dropped on the window from the Finder open in the image analysis window.
/// </summary>
[ToolView(typeof(FooocusWindowViewModel), "Fooocus images", "🎨", 1300, 860)]
public sealed class FooocusView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (FooocusWindowViewModel)viewModel;
        void OpenAnalysis(FooocusImageViewModel? image)
        {
            if (image is null)
                return;
            ImageKit.OpenAnalysis(shell, vm.CreateAnalysis, image.Path, () => Ui.Run(vm.OpenImageCommand));
        }

        // ── The request ──
        var prompt = Kit.Lines(vm, nameof(vm.Prompt), 120, "What the image shows, in English: a cute robot girl with pink headphones in a neon space station…");
        var negative = Kit.Lines(vm, nameof(vm.NegativePrompt), 56, "What it must not show (optional)");

        var styles = Ui.Column(0);
        void FillStyles()
        {
            styles.Children.Clear();
            foreach (var style in vm.VisibleStyles)
                styles.Children.Add(new CheckBox { Content = style.Name, MinHeight = 26 }
                    .Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, style, nameof(style.IsSelected), Avalonia.Data.BindingMode.TwoWay));
        }
        Ui.When(styles, vm, FillStyles, nameof(vm.VisibleStyles));
        Kit.OnCollection(styles, vm.Styles, FillStyles);
        var stylesFrame = new Border
        {
            Child = Ui.Scroll(styles),
            Height = 190,
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6)
        };

        var performance = Kit.Options(vm.Performances, vm, nameof(vm.Performance),
            tooltip: "Speed: about a minute per image; Quality: twice as long; Extreme Speed, Lightning, Hyper-SD: a few seconds (a LoRA downloaded the first time)");
        var ratio = Kit.Options(vm.AspectRatios, vm, nameof(vm.AspectRatio), r => r.Replace('*', '×'));
        var number = Ui.Number(vm, nameof(vm.ImageNumber), 1, 8);
        number.FormatString = "0";
        var seed = Ui.Edit(vm, nameof(vm.Seed));
        ToolTip.SetTip(seed, "-1: a new random seed each time; a seed makes the same image again");
        var model = Kit.Options(vm.Models, vm, nameof(vm.Model), tooltip: "Model of Fooocus (none chosen: its default model)");

        var options = Kit.Cells(2, 12, 6,
            Kit.Field("Performance", performance),
            Kit.Field("Size (width × height)", ratio),
            Kit.Field("Images", number),
            Kit.Field("Seed", seed));

        var generate = Ui.Command("✨ GENERATE", vm.GenerateCommand, tooltip: "Generate the images (Ctrl / ⌘ + Enter)", primary: true);
        generate.MinHeight = 40;
        generate.HorizontalAlignment = HorizontalAlignment.Stretch;
        generate.HorizontalContentAlignment = HorizontalAlignment.Center;
        generate.VerticalContentAlignment = VerticalAlignment.Center;
        var stop = Ui.Command("STOP", vm.CancelCommand, tooltip: "Stop waiting (Escape)").Visible(vm, nameof(vm.IsGenerating));
        stop.MinWidth = 70;
        stop.MinHeight = 40;
        var progress = new ProgressBar { IsIndeterminate = true, Height = 4, MinHeight = 4 }.Visible(vm, nameof(vm.IsGenerating));
        var buttons = Kit.FillRow(8, [], generate, stop);
        buttons.Margin = new Thickness(0, 8, 0, 0);

        var form = Ui.Column(10,
            Kit.Field("Prompt", prompt),
            Kit.Field("Negative prompt", negative),
            Ui.Column(4,
                Ui.Caption("Styles"),
                Ui.Bound(vm, nameof(vm.SelectedStylesText), Ui.Muted, 11, wrap: true),
                Ui.Edit(vm, nameof(vm.StyleFilter), "Filter the styles (photo, anime, cinematic…)"),
                stylesFrame),
            options,
            Kit.Field("Model", model),
            buttons,
            progress);
        var left = Ui.Scroll(Kit.Margin(form, 6, 4, 12, 12));

        // ── The selected image ──
        var previewTile = new PictureTile
        {
            Framed = false,
            ClipToBounds = true,
            DragPath = () => vm.SelectedImage?.Path,
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1)
        };
        ToolTip.SetTip(previewTile, "Click to open the image in the image analysis window (filters, annotations, AI analysis); drag it inside the chat to insert it");
        var preview = new Image { Stretch = Stretch.Uniform };
        preview.Bind(Image.SourceProperty, vm, nameof(vm.Preview));
        var none = Kit.EmptyWhen(Kit.Placeholder("The images generated with Fooocus appear here"), vm.Images);
        previewTile.Child = new Grid { Children = { preview, none } };
        previewTile.ClickReleased += () => OpenAnalysis(vm.SelectedImage);
        previewTile.Dropped += path => vm.Status = $"Image dropped: {Path.GetFileName(path)}";

        var caption = Kit.Text(vm, nameof(vm.SelectedImage), v => (v as FooocusImageViewModel)?.Caption ?? string.Empty, Ui.TextBright, 13, wrap: true);
        caption.MaxLines = 2;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        var details = Kit.Text(vm, nameof(vm.SelectedImage), v => (v as FooocusImageViewModel)?.Details ?? string.Empty, Ui.Muted, 11);
        var info = Kit.FillRow(8, [],
            // The prompt and the details take the free width.
            Ui.Column(2, caption, details),
            Ui.Command("↺ REUSE", vm.ReuseCommand, tooltip: "Put the prompt, styles, size, performance, seed and model of this image back in the form"),
            Ui.Command("OPEN", vm.OpenImageCommand, tooltip: "Open the image with the viewer of the system"),
            Ui.Command("📁 FOLDER", vm.OpenFolderCommand, tooltip: vm.Folder));

        // ── The gallery ──
        var gallery = Kit.Items(vm.Images, image => Thumbnail(vm, image, OpenAnalysis), () => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 });
        var galleryScroll = new ScrollViewer
        {
            Content = gallery,
            Height = 176,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        var right = Kit.Rows("Auto,*,Auto,Auto", Ui.Caption("Fooocus · images", Ui.Text, 12), previewTile, info, galleryScroll);
        right.RowSpacing = 6;
        var split = Kit.SplitH(left, Kit.Margin(right, 4, 4, 4, 0), "440", "*", 400, 300);
        var page = Kit.Rows("*,Auto", split, Kit.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsGenerating)));

        // Ctrl / ⌘ + Enter generates (the prompt too), Esc stops waiting.
        Kit.Keys(page, e =>
        {
            if (e.Key == Key.Enter && Kit.CommandHeld(e.KeyModifiers))
            {
                Ui.Run(vm.GenerateCommand);
                return true;
            }
            if (e.Key == Key.Escape && vm.CancelCommand.CanExecute(null))
            {
                Ui.Run(vm.CancelCommand);
                return true;
            }
            return false;
        });
        Kit.FocusWhenShown(prompt);

        // Pictures dropped from the Finder open in the image analysis window.
        ImageKit.FilesDropped(page, files =>
        {
            foreach (var file in files.Where(f => File.Exists(f) && PictureLibrary.IsPicture(f)))
                ImageKit.OpenAnalysis(shell, vm.CreateAnalysis, file, () => PictureLibrary.OpenWithSystem(file));
        });
        return page;
    }

    /// <summary>An image of the gallery: a click selects it, a double-click opens the image analysis.</summary>
    private static Control Thumbnail(FooocusWindowViewModel vm, FooocusImageViewModel image, Action<FooocusImageViewModel?> openAnalysis)
    {
        var tile = new PictureTile { DragPath = () => image.Path, Width = 180, Height = 150 };
        ToolTip.SetTip(tile, image.Caption);
        var picture = new Image { Stretch = Stretch.UniformToFill };
        picture.Bind(Image.SourceProperty, image, nameof(image.Thumbnail));
        tile.Child = new Border { Child = picture, ClipToBounds = true };
        tile.Clicked += () => vm.SelectedImage = image;
        tile.DoubleClicked += () => openAnalysis(image);
        // A right click offers to annotate the image.
        tile.RightClicked += () =>
        {
            if (image.Path is not { } path)
                return;
            vm.SelectedImage = image;
            Kit.AnnotateMenu(tile, new AnnotationTarget(AnnotationTargetTypes.Image, path, image.Caption));
        };
        tile.Dropped += path => vm.Status = $"Image dropped: {Path.GetFileName(path)}";
        tile.KeyPressed = e =>
        {
            var index = vm.Images.IndexOf(image);
            if (e.Key is Key.Left or Key.Right && vm.Images.Count > 0)
            {
                vm.SelectedImage = vm.Images[Math.Clamp(index + (e.Key == Key.Left ? -1 : 1), 0, vm.Images.Count - 1)];
                return true;
            }
            if (e.Key == Key.Enter && !Kit.CommandHeld(e.KeyModifiers))
            {
                openAnalysis(image);
                return true;
            }
            return false;
        };
        Ui.When(tile, vm, () =>
        {
            var selected = ReferenceEquals(vm.SelectedImage, image);
            if (selected && !tile.Selected)
                Dispatcher.UIThread.Post(() => tile.BringIntoView(), DispatcherPriority.Background);
            tile.Selected = selected;
        }, nameof(vm.SelectedImage));
        return tile;
    }
}
