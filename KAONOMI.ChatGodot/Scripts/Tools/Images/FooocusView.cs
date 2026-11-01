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
/// /fooocus: on the left the request (prompt, negative prompt, the styles filtered and ticked, performance, size, number
/// of images, seed, model), ✨ Generate (Ctrl / ⌘ + Enter) and Stop (Esc) while it runs; on the right the selected image
/// (a click opens it in the image analysis window), its prompt and details, ↺ Reuse, Open, 📁 Folder, and the gallery
/// of the folder (a click selects, a double-click opens the image analysis). The images can be dragged inside the chat;
/// pictures dropped on the chat from the Finder open in the image analysis window.
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
            ImageKit.OpenAnalysis(shell, vm.CreateAnalysis, image.Path, () => Bind.Run(vm.OpenImageCommand));
        }

        // ── The request ──
        var prompt = Ui.Lines(vm, nameof(vm.Prompt), 120, "What the image shows, in English: a cute robot girl with pink headphones in a neon space station…");
        var negative = Ui.Lines(vm, nameof(vm.NegativePrompt), 56, "What it must not show (optional)");

        var styles = Ui.Column(0);
        void FillStyles()
        {
            if (!GodotObject.IsInstanceValid(styles))
                return;
            foreach (var child in styles.GetChildren())
            {
                styles.RemoveChild(child);
                child.QueueFree();
            }
            foreach (var style in vm.VisibleStyles)
            {
                var check = Ui.Check(style.Name, style, nameof(style.IsSelected));
                check.CustomMinimumSize = new Vector2(0, 26);
                styles.AddChild(check);
            }
        }
        Bind.When(styles, vm, FillStyles, nameof(vm.VisibleStyles));
        System.Collections.Specialized.NotifyCollectionChangedEventHandler stylesChanged = (_, _) => Bind.OnMain(FillStyles);
        vm.Styles.CollectionChanged += stylesChanged;
        Bind.OnGone(styles, () => vm.Styles.CollectionChanged -= stylesChanged);
        var stylesFrame = new PanelContainer { CustomMinimumSize = new Vector2(0, 190) };
        stylesFrame.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.25f), Eve.Eve.Edge with { A = 0.3f }, 6));
        stylesFrame.AddChild(Ui.Scroll(styles));

        var performance = Bind.Options(new OptionButton { TooltipText = "Speed: about a minute per image; Quality: twice as long; Extreme Speed, Lightning, Hyper-SD: a few seconds (a LoRA downloaded the first time)" },
            vm.Performances, vm, nameof(vm.Performance));
        var ratio = Bind.Options(new OptionButton(), vm.AspectRatios, vm, nameof(vm.AspectRatio), r => r.Replace('*', '×'));
        var number = Bind.Number(new SpinBox { MinValue = 1, MaxValue = 8, Step = 1, Rounded = true }, vm, nameof(vm.ImageNumber));
        var seed = Ui.Line(vm, nameof(vm.Seed));
        seed.TooltipText = "-1: a new random seed each time; a seed makes the same image again";
        var model = Bind.Options(new OptionButton { TooltipText = "Model of Fooocus (none chosen: its default model)" }, vm.Models, vm, nameof(vm.Model));

        var options1 = Ui.Grid(2, 12);
        options1.AddChild(Ui.Fill(Ui.Field("Performance", performance)));
        options1.AddChild(Ui.Fill(Ui.Field("Size (width × height)", ratio)));
        options1.AddChild(Ui.Fill(Ui.Field("Images", number)));
        options1.AddChild(Ui.Fill(Ui.Field("Seed", seed)));

        var generate = Ui.Command("✨ GENERATE", vm.GenerateCommand, primary: true, tooltip: "Generate the images (Ctrl / ⌘ + Enter)");
        generate.CustomMinimumSize = new Vector2(0, 40);
        generate.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var stop = Bind.Visible(Ui.Command("STOP", vm.CancelCommand, tooltip: "Stop waiting (Escape)"), vm, nameof(vm.IsGenerating));
        stop.CustomMinimumSize = new Vector2(70, 40);
        var progress = Bind.Visible(new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 4) }, vm, nameof(vm.IsGenerating));

        var form = Ui.Column(10,
            Ui.Field("Prompt", prompt),
            Ui.Field("Negative prompt", negative),
            Ui.Column(4,
                Eve.Eve.Caption("Styles"),
                Ui.Label(vm, nameof(vm.SelectedStylesText), Eve.Eve.Muted, 11, wrap: true),
                Ui.Line(vm, nameof(vm.StyleFilter), "Filter the styles (photo, anime, cinematic…)"),
                stylesFrame),
            options1,
            Ui.Field("Model", model),
            Ui.Margin(Ui.Row(8, generate, stop), 0, 8, 0, 0),
            progress);
        var left = Ui.Scroll(Ui.Margin(form, 6, 4, 12, 12));
        left.CustomMinimumSize = new Vector2(400, 0);

        // ── The selected image ──
        var previewTile = new PictureTile
        {
            Framed = false,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true,
            TooltipText = "Click to open the image in the image analysis window (filters, annotations, AI analysis); drag it inside the chat to insert it",
            DragPath = () => vm.SelectedImage?.Path
        };
        previewTile.Refresh();
        previewTile.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.3f), Eve.Eve.Edge with { A = 0.3f }, 0));
        var area = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        previewTile.AddChild(area);
        var preview = Ui.Picture(minHeight: 200);
        preview.MouseFilter = Control.MouseFilterEnum.Ignore;
        preview.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        ImageKit.BindBitmap(preview, vm, nameof(vm.Preview));
        area.AddChild(preview);
        var none = Ui.Placeholder("The images generated with Fooocus appear here");
        none.MouseFilter = Control.MouseFilterEnum.Ignore;
        none.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        area.AddChild(none);
        void RefreshNone() => none.Visible = vm.Images.Count == 0;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler imagesChanged = (_, _) => Bind.OnMain(() =>
        {
            if (GodotObject.IsInstanceValid(none))
                RefreshNone();
        });
        vm.Images.CollectionChanged += imagesChanged;
        Bind.OnGone(none, () => vm.Images.CollectionChanged -= imagesChanged);
        RefreshNone();
        previewTile.Tapped += () => OpenAnalysis(vm.SelectedImage);
        previewTile.Dropped += path => vm.Status = $"Image dropped: {Path.GetFileName(path)}";

        var caption = Ui.Label(vm, nameof(vm.SelectedImage), Eve.Eve.TextBright, 13, wrap: true, format: v => (v as FooocusImageViewModel)?.Caption ?? string.Empty);
        caption.MaxLinesVisible = 2;
        var details = Ui.Label(vm, nameof(vm.SelectedImage), Eve.Eve.Muted, 11, format: v => (v as FooocusImageViewModel)?.Details ?? string.Empty);
        details.ClipText = true;
        details.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var info = Ui.Row(8,
            // The prompt and the details take the free width.
            Ui.Fill(Ui.Column(2, caption, details).Also(c => c.CustomMinimumSize = new Vector2(200, 0))),
            Ui.Command("↺ REUSE", vm.ReuseCommand, tooltip: "Put the prompt, styles, size, performance, seed and model of this image back in the form"),
            Ui.Command("OPEN", vm.OpenImageCommand, tooltip: "Open the image with the viewer of the system"),
            Ui.Command("📁 FOLDER", vm.OpenFolderCommand, tooltip: vm.Folder));

        // ── The gallery ──
        var gallery = new HBoxContainer();
        gallery.AddThemeConstantOverride("separation", 6);
        var galleryScroll = new ScrollContainer { VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 170) };
        galleryScroll.AddChild(gallery);
        Bind.Items(gallery, vm.Images, image => Thumbnail(vm, image, galleryScroll, OpenAnalysis));

        var right = Ui.Column(6, Eve.Eve.Caption("Fooocus · images", Eve.Eve.Text, 12), previewTile, info, galleryScroll);
        left.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        var rightMargin = Ui.Margin(right, 4, 4, 4, 0);
        rightMargin.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        split.AddChild(left);
        split.AddChild(rightMargin);
        var page = Ui.Column(6, split, Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsGenerating)));

        // Ctrl / ⌘ + Enter generates, Esc stops waiting.
        ImageKit.Keys(page, shell, key =>
        {
            if (key.Keycode is Key.Enter or Key.KpEnter && ImageKit.Command(key))
            {
                Bind.Run(vm.GenerateCommand);
                return true;
            }
            if (key.Keycode == Key.Escape && vm.CancelCommand.CanExecute(null))
            {
                Bind.Run(vm.CancelCommand);
                return true;
            }
            return false;
        });
        // TextEdit takes Ctrl + Enter for a new line: the prompt gives it to Generate.
        prompt.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } key && ImageKit.Command(key))
            {
                Bind.Run(vm.GenerateCommand);
                prompt.AcceptEvent();
            }
        };
        page.Ready += () => prompt.GrabFocus();

        // Pictures dropped from the Finder open in the image analysis window.
        ImageKit.FilesDropped(page, shell, files =>
        {
            foreach (var file in files.Where(f => File.Exists(f) && PictureLibrary.IsPicture(f)))
                ImageKit.OpenAnalysis(shell, vm.CreateAnalysis, file, () => PictureLibrary.OpenWithSystem(file));
        });
        return page;
    }

    /// <summary>An image of the gallery: a click selects it, a double-click opens the image analysis.</summary>
    private static Control Thumbnail(FooocusWindowViewModel vm, FooocusImageViewModel image, ScrollContainer scroll, Action<FooocusImageViewModel?> openAnalysis)
    {
        var tile = new PictureTile { TooltipText = image.Caption, DragPath = () => image.Path, CustomMinimumSize = new Vector2(180, 140) };
        var picture = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(170, 130)
        };
        ImageKit.BindBitmap(picture, image, nameof(image.Thumbnail));
        tile.AddChild(picture);
        tile.Clicked += () => vm.SelectedImage = image;
        tile.DoubleClicked += () => openAnalysis(image);
        // A right click offers to annotate the image or to make it the wallpaper.
        tile.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } right && image.Path is { } path)
            {
                vm.SelectedImage = image;
                Data.Annotations.ShowMenu(tile, right.Position, new AnnotationTarget(AnnotationTargetTypes.Image, path, image.Caption));
            }
        };
        tile.Dropped += path => vm.Status = $"Image dropped: {Path.GetFileName(path)}";
        tile.KeyPressed = key =>
        {
            var index = vm.Images.IndexOf(image);
            if (key.Keycode is Key.Left or Key.Right && vm.Images.Count > 0)
            {
                var next = vm.Images[Math.Clamp(index + (key.Keycode == Key.Left ? -1 : 1), 0, vm.Images.Count - 1)];
                vm.SelectedImage = next;
                return true;
            }
            if (key.Keycode is Key.Enter or Key.KpEnter && !ImageKit.Command(key))
            {
                openAnalysis(image);
                return true;
            }
            return false;
        };
        Bind.When(tile, vm, () =>
        {
            var selected = ReferenceEquals(vm.SelectedImage, image);
            if (selected && !tile.Selected)
                Callable.From(() =>
                {
                    if (GodotObject.IsInstanceValid(tile) && GodotObject.IsInstanceValid(scroll))
                        scroll.EnsureControlVisible(tile);
                }).CallDeferred();
            tile.Selected = selected;
        }, nameof(vm.SelectedImage));
        return tile;
    }
}
