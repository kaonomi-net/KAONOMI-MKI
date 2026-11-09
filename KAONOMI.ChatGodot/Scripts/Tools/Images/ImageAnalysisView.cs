using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Images;

/// <summary>
/// The analysis of one picture (a double-click in /pictures, a click in /fooocus), one window per picture named
/// Image-&lt;title&gt;. The toolbar: brightness and contrast, Monochrome, Binarize (and its threshold), Edges, Invert,
/// turn left / right, mirror left-right / top-bottom, zoom − / + and Fit, ▭ Annotate, ↺ Reset, Open, ✨ Analyse (the
/// vision model), ⬇ Export PNG (with the annotations), 💾 Save. The picture at its zoom with its numbered rectangles
/// (drawn in annotate mode, selected by a click; Ctrl / ⌘ + wheel zooms); below it the comments (the previous ones and a
/// new one) and the AI analysis; on the right the title, the place (GPS metadata, map), the annotations with their
/// comments and the metadata. A double-click on the picture shows it alone in the window (again, or Esc, to come back).
/// Keys: Ctrl / ⌘ + S saves, Ctrl / ⌘ + = / − zoom.
/// </summary>
[ToolView(typeof(ImageAnalysisViewModel), "Image", "🔬", 1500, 950, PerObject = true)]
public sealed class ImageAnalysisView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ImageAnalysisViewModel)viewModel;

        // ── The picture ──
        var canvas = new ImageAnnotationCanvas(vm);
        var center = new CenterContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        center.AddChild(Ui.Margin(canvas, 12));
        var scroller = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        scroller.AddChild(center);
        scroller.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel && ImageAnnotationCanvas.ZoomKey(wheel))
            {
                Bind.Run(wheel.ButtonIndex == MouseButton.WheelUp ? vm.ZoomInCommand : vm.ZoomOutCommand);
                scroller.AcceptEvent();
            }
        };
        var pictureCard = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipContents = true };
        pictureCard.AddThemeStyleboxOverride("panel", Eve.Eve.Box(new Color(0.09f, 0.1f, 0.12f, 0.9f), Eve.Eve.Edge with { A = 0.35f }, 0));
        pictureCard.AddChild(scroller);
        var loading = Ui.Label(vm, nameof(vm.Status), Eve.Eve.Muted, 12);
        loading.HorizontalAlignment = HorizontalAlignment.Center;
        loading.VerticalAlignment = VerticalAlignment.Center;
        loading.MouseFilter = Control.MouseFilterEnum.Ignore;
        pictureCard.AddChild(loading);

        // The bitmap of the view model (redrawn at each change of the filters) shown by the canvas; the first one fits.
        var fitted = false;
        var holder = new TextureRect { Visible = false };
        ImageKit.BindBitmap(holder, vm, nameof(vm.Shown), texture =>
        {
            canvas.Texture = texture;
            loading.Visible = texture is null;
            if (texture is not null && !fitted)
            {
                fitted = true;
                // At its real size when asked (scrolled in the window), else the whole picture in view.
                if (vm.OpenAtActualSize)
                    vm.Zoom = 1;
                else
                    FitLater(canvas, scroller, vm);
            }
        });
        pictureCard.AddChild(holder);

        // ── Toolbar ──
        var toolbar = new HFlowContainer();
        toolbar.AddThemeConstantOverride("h_separation", 8);
        toolbar.AddThemeConstantOverride("v_separation", 6);

        Label Value(string name)
        {
            var label = Ui.Label(vm, name, Eve.Eve.Muted, 11);
            label.CustomMinimumSize = new Vector2(28, 0);
            label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            return label;
        }
        HSlider Slider(string name, int min, int max, int width, string tooltip) =>
            Bind.Range(new HSlider { MinValue = min, MaxValue = max, Step = 1, CustomMinimumSize = new Vector2(width, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, TooltipText = tooltip }, vm, name);
        Button Big(Button button)
        {
            button.AddThemeFontSizeOverride("font_size", 16);
            button.CustomMinimumSize = new Vector2(32, 0);
            return button;
        }
        Label Icon(string text, string tooltip)
        {
            var label = Eve.Eve.Text_(text, Eve.Eve.Text, 15);
            label.TooltipText = tooltip;
            label.MouseFilter = Control.MouseFilterEnum.Pass;
            label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            return label;
        }

        toolbar.AddChild(ImageKit.Group(
            Icon("☀", "Brightness"), Slider(nameof(vm.Brightness), -100, 100, 110, "Brightness (-100 to 100)"), Value(nameof(vm.Brightness)),
            Icon("◐", "Contrast"), Slider(nameof(vm.Contrast), -100, 100, 110, "Contrast (-100 to 100)"), Value(nameof(vm.Contrast))));
        var threshold = Bind.Visible(Slider(nameof(vm.Threshold), 0, 255, 90, "Threshold of the binarization (0 to 255)"), vm, nameof(vm.Binarize));
        toolbar.AddChild(ImageKit.Group(
            ImageKit.Toggle("Monochrome", vm, nameof(vm.Monochrome), "Shades of grey"),
            ImageKit.Toggle("Binarize", vm, nameof(vm.Binarize), "Black or white, cut at the threshold"),
            threshold,
            ImageKit.Toggle("Edges", vm, nameof(vm.Edges), "Extract the edges of the shapes (Sobel)"),
            ImageKit.Toggle("Invert", vm, nameof(vm.Invert), "The negative of the picture")));
        toolbar.AddChild(ImageKit.Group(
            Big(Ui.Command("⟲", vm.RotateLeftCommand, tooltip: "Turn a quarter to the left")),
            Big(Ui.Command("⟳", vm.RotateRightCommand, tooltip: "Turn a quarter to the right")),
            Big(ImageKit.Toggle("⇋", vm, nameof(vm.FlipHorizontal), "Mirror left / right")),
            Big(ImageKit.Toggle("⇵", vm, nameof(vm.FlipVertical), "Mirror top / bottom"))));
        var zoom = Ui.Label(vm, nameof(vm.ZoomText), Eve.Eve.TextBright, 12);
        zoom.CustomMinimumSize = new Vector2(48, 0);
        zoom.HorizontalAlignment = HorizontalAlignment.Center;
        zoom.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        toolbar.AddChild(ImageKit.Group(
            Big(Ui.Command("−", vm.ZoomOutCommand, tooltip: OperatingSystem.IsMacOS() ? "Zoom out (⌘ + wheel down, ⌘ −)" : "Zoom out (Ctrl + wheel down, Ctrl −)")),
            zoom,
            Big(Ui.Command("+", vm.ZoomInCommand, tooltip: OperatingSystem.IsMacOS() ? "Zoom in (⌘ + wheel up, ⌘ =)" : "Zoom in (Ctrl + wheel up, Ctrl =)")),
            Ui.Action("Fit", () => Fit(canvas, scroller, vm), tooltip: "The whole picture in the window")));
        toolbar.AddChild(ImageKit.Group(
            ImageKit.Toggle("▭ Annotate", vm, nameof(vm.IsAnnotating), "Draw rectangles on the picture to annotate a part of it; write their comment in the list on the right"),
            Ui.Command("↺ Reset", vm.ResetViewCommand, tooltip: "Remove the brightness, contrast, filters and turns"),
            Ui.Command("Open", vm.OpenInSystemCommand, tooltip: "Open the picture with the viewer of the system")));
        toolbar.AddChild(ImageKit.Group(
            Ui.Command("✨ ANALYSE", vm.AnalyseCommand, primary: true,
                tooltip: $"Send the picture (as shown) to {vm.VisionModel} (visionmodel in config.yml): a detailed description, and where it could have been taken"),
            Ui.Action("⬇ EXPORT PNG", () => _ = Export(vm), primary: true,
                tooltip: "Save the picture as shown (brightness, contrast, filters, orientation) with its annotation rectangles, as a PNG file"),
            Ui.Command("💾 SAVE", vm.SaveCommand, primary: true,
                tooltip: "Save the analysis (settings, annotations, comments, AI analysis, place, actions, link to the picture) in the imageanalysis index (Ctrl / ⌘ + S)")));
        var toolbarPanel = Ui.Toolbar(toolbar);

        // ── Comments and AI analysis ──
        var previous = Ui.Column(5);
        var noComment = Eve.Eve.Text_("No comment yet", Eve.Eve.Muted, 12);
        Bind.Items(previous, vm.PreviousComments, Comment, noComment);
        var previousColumn = Ui.Column(4, noComment, previous);
        var previousScroll = Ui.Scroll(previousColumn);
        previousScroll.SizeFlagsVertical = Control.SizeFlags.Fill;
        // As high as the previous comments need, up to 110 pixels (then they scroll).
        void SizeComments() => previousScroll.CustomMinimumSize = new Vector2(0, Math.Min(110, Math.Max(20, previousColumn.GetCombinedMinimumSize().Y)));
        previousColumn.MinimumSizeChanged += SizeComments;
        previousColumn.Ready += SizeComments;
        var newComment = Ui.Lines(vm, nameof(vm.NewComment), 64, "A new comment: added to the previous ones at the next Save (they are never removed)");
        newComment.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var comments = ImageKit.Card("Comments", previousScroll, newComment);
        comments.GetChild<Control>(0).SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var analysing = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(80, 4), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        Bind.Visible(analysing, vm, nameof(vm.IsAnalysing));
        var aiText = Ui.Lines(vm, nameof(vm.AiAnalysis), 64,
            "✨ Analyse writes here a detailed description of the picture by the vision model, and where it could have been taken (it can be changed before Save)");
        aiText.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        aiText.AddThemeFontSizeOverride("font_size", 12);
        var ai = ImageKit.Card(null, Ui.Row(8, Ui.Fill(Eve.Eve.Caption("AI analysis", Eve.Eve.Amber)), analysing), aiText);
        ai.GetChild<Control>(0).SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        comments.SizeFlagsHorizontal = ai.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var commentsArea = Ui.Row(10, comments, ai);
        commentsArea.CustomMinimumSize = new Vector2(0, 160);

        var pictureColumn = new VSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SplitOffsets = [-260] };
        pictureColumn.AddChild(pictureCard);
        pictureColumn.AddChild(commentsArea);
        commentsArea.SizeFlagsVertical = Control.SizeFlags.Fill;
        pictureCard.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // ── Side panel: title, place, annotations, metadata ──
        var titleBox = Ui.Line(vm, nameof(vm.ImageTitle), vm.FileName);
        titleBox.TooltipText = "Title of the picture (by default its file name), saved with the analysis; the window is named Image-<title>";
        var location = ImageKit.Card("Location",
            Ui.Row(8, Ui.Line(vm, nameof(vm.Latitude), "Latitude (e.g. 48.8584)"), Ui.Line(vm, nameof(vm.Longitude), "Longitude (e.g. 2.2945)")),
            Ui.Line(vm, nameof(vm.LocationName), "Place (city, address, landmark…)"),
            Ui.Row(6,
                Ui.Command("📍 From GPS metadata", vm.LocationFromMetadataCommand, tooltip: "Take the position written by the camera in the picture"),
                Ui.Command("🗺 Show on map", vm.ShowOnMapCommand, tooltip: "Open the place on OpenStreetMap")));

        var annotationList = Ui.Column(6);
        var noAnnotation = Eve.Eve.Text_("▭ Annotate, then draw a rectangle on the picture", Eve.Eve.Muted, 12, wrap: true);
        var commentBoxes = new System.Collections.Generic.Dictionary<ImageAnnotationViewModel, TextEdit>();
        Bind.Items(annotationList, vm.Annotations, a => AnnotationRow(vm, a, commentBoxes), noAnnotation);
        var removeAnnotation = Ui.Command("Remove", vm.DeleteAnnotationCommand, tooltip: "Remove the selected rectangle");
        removeAnnotation.AddThemeFontSizeOverride("font_size", 11);
        var annotations = ImageKit.Card(null, Ui.Row(8, Ui.Fill(Eve.Eve.Caption("Annotations", Eve.Eve.Amber)), removeAnnotation), noAnnotation, annotationList);

        var metadataHolder = Ui.Column(0);
        void FillMetadata()
        {
            foreach (var child in metadataHolder.GetChildren())
                child.QueueFree();
            metadataHolder.AddChild(ImageKit.Metadata(vm.Metadata, Eve.Eve.TextBright));
        }
        vm.Metadata.CollectionChanged += OnMetadata;
        void OnMetadata(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Bind.OnMain(() =>
        {
            if (GodotObject.IsInstanceValid(metadataHolder))
                FillMetadata();
        });
        Bind.OnGone(metadataHolder, () => vm.Metadata.CollectionChanged -= OnMetadata);
        FillMetadata();
        var metadata = ImageKit.Card("Metadata", Ui.Label(vm, nameof(vm.Summary), Eve.Eve.TextBright, 12, wrap: true), metadataHolder);

        var side = Ui.Scroll(Ui.Margin(Ui.Column(10, ImageKit.Card("Title", titleBox), location, annotations, metadata), 0, 0, 8, 8));
        side.CustomMinimumSize = new Vector2(380, 0);

        side.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        var main = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        main.AddChild(pictureColumn);
        main.AddChild(side);

        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var page = Ui.Column(8, toolbarPanel, main, status);

        // A rectangle drawn: its comment gets the keyboard.
        canvas.AnnotationCreated += annotation => Callable.From(() =>
        {
            if (commentBoxes.TryGetValue(annotation, out var box) && GodotObject.IsInstanceValid(box))
            {
                box.GrabFocus();
                side.EnsureControlVisible(box);
            }
        }).CallDeferred();

        // A double-click on the picture shows it alone; again, or Esc, brings the window back.
        var pictureOnly = false;
        void SetPictureOnly(bool on)
        {
            if (pictureOnly == on)
                return;
            pictureOnly = on;
            toolbarPanel.Visible = status.Visible = side.Visible = commentsArea.Visible = !on;
            pictureColumn.DraggerVisibility = on ? SplitContainer.DraggerVisibilityEnum.HiddenCollapsed : SplitContainer.DraggerVisibilityEnum.Visible;
            main.DraggerVisibility = pictureColumn.DraggerVisibility;
            FitLater(canvas, scroller, vm);
        }
        canvas.DoubleClicked += () => SetPictureOnly(!pictureOnly);
        // A right click on the picture offers to annotate it (the part drawn under the pointer, when there is one) or to
        // make it the wallpaper.
        canvas.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } right)
                Data.Annotations.ShowMenu(canvas, right.Position, vm.AnnotationTargetFor(canvas.PartAt(right.Position)), vm.ImagePath);
        };

        ImageKit.Keys(page, shell, key =>
        {
            if (key.Keycode == Key.Escape && pictureOnly)
            {
                SetPictureOnly(false);
                return true;
            }
            if (!ImageKit.Command(key))
                return false;
            switch (key.Keycode)
            {
                case Key.S:
                    Bind.Run(vm.SaveCommand);
                    return true;
                case Key.Equal or Key.Plus or Key.KpAdd:
                    Bind.Run(vm.ZoomInCommand);
                    return true;
                case Key.Minus or Key.KpSubtract:
                    Bind.Run(vm.ZoomOutCommand);
                    return true;
            }
            return false;
        });

        // The window is named Image-<title>.
        void Rename()
        {
            if (shell.WindowOf(page) is { } window && window.Title != vm.Title)
                window.Title = vm.Title;
        }
        page.Ready += Rename;
        Bind.When(page, vm, Rename, nameof(vm.Title));
        return page;
    }

    /// <summary>A previous comment: its date and author, its text.</summary>
    private static Control Comment(ImageCommentItem comment)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Raised(0.03f), Eve.Eve.Edge with { A = 0.2f }, 5));
        panel.AddChild(Ui.Column(2,
            Eve.Eve.Text_($"{comment.Date.ToLocalTime():yyyy-MM-dd HH:mm}", Eve.Eve.Muted, 10),
            Eve.Eve.Text_(comment.Text, Eve.Eve.Text, 12, wrap: true)));
        return panel;
    }

    /// <summary>An annotation of the list: its number, its comment (selecting it selects the rectangle).</summary>
    private static Control AnnotationRow(ImageAnalysisViewModel vm, ImageAnnotationViewModel annotation, System.Collections.Generic.Dictionary<ImageAnnotationViewModel, TextEdit> boxes)
    {
        var badge = new PanelContainer { CustomMinimumSize = new Vector2(24, 22), SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };
        var number = Ui.Label(annotation, nameof(annotation.Number), Colors.White, 12);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        badge.AddChild(number);
        var comment = Ui.Lines(annotation, nameof(annotation.Comment), 44, "Comment of this part of the picture");
        boxes[annotation] = comment;
        Bind.OnGone(comment, () => boxes.Remove(annotation));
        comment.AddThemeFontSizeOverride("font_size", 12);
        comment.ScrollFitContentHeight = true;
        comment.FocusEntered += () => vm.SelectedAnnotation = annotation;
        var row = new PanelContainer();
        row.AddChild(Ui.Row(8, badge, comment));
        row.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                vm.SelectedAnnotation = annotation;
        };
        Bind.When(row, vm, () =>
        {
            var selected = ReferenceEquals(vm.SelectedAnnotation, annotation);
            var color = selected ? Color.FromHtml("#EF4444") : Color.FromHtml("#F59E0B");
            badge.AddThemeStyleboxOverride("panel", Eve.Eve.Box(color, color, 2));
            row.AddThemeStyleboxOverride("panel", selected
                ? Eve.Eve.Box(Eve.Eve.Cyan with { A = 0.12f }, Eve.Eve.Cyan with { A = 0.6f }, 4)
                : Eve.Eve.Box(new Color(0, 0, 0, 0), new Color(0, 0, 0, 0), 4));
        }, nameof(vm.SelectedAnnotation));
        return row;
    }

    /// <summary>The zoom that shows the whole picture in the window.</summary>
    private static void Fit(ImageAnnotationCanvas canvas, ScrollContainer scroller, ImageAnalysisViewModel vm)
    {
        var pixels = canvas.PixelSize;
        if (pixels.X <= 0 || pixels.Y <= 0)
            return;
        var width = Math.Max(100, scroller.Size.X - 30);
        var height = Math.Max(100, scroller.Size.Y - 30);
        vm.Zoom = Math.Round(Math.Min(width / pixels.X, height / pixels.Y), 3);
    }

    /// <summary><see cref="Fit"/> once the layout is done (after a change of the window).</summary>
    private static async void FitLater(ImageAnnotationCanvas canvas, ScrollContainer scroller, ImageAnalysisViewModel vm)
    {
        if (!canvas.IsInsideTree())
            await canvas.ToSignal(canvas, Node.SignalName.Ready);
        for (var i = 0; i < 2 && GodotObject.IsInstanceValid(canvas); i++)
            await canvas.ToSignal(canvas.GetTree(), SceneTree.SignalName.ProcessFrame);
        if (GodotObject.IsInstanceValid(canvas) && GodotObject.IsInstanceValid(scroller))
            Fit(canvas, scroller, vm);
    }

    /// <summary>
    /// ⬇ Export PNG: a save dialog (in the folder of the picture, named after it with -analysis), then the picture as
    /// shown now, at its own size, with its annotation rectangles, written as PNG (whatever the extension typed).
    /// </summary>
    private static async Task Export(ImageAnalysisViewModel vm)
    {
        if (vm.Shown is null)
        {
            vm.ExportFailed("the picture is not loaded yet");
            return;
        }
        var path = await Dialogs.SaveFile("Export the picture with its annotations (PNG)",
            Path.GetFileNameWithoutExtension(vm.ImagePath) + "-analysis.png", ["*.png ; PNG picture"], Path.GetDirectoryName(vm.ImagePath));
        if (string.IsNullOrEmpty(path))
            return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            path += ".png";
        try
        {
            vm.Exported(path, AnnotationExport.ExportPng(vm, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            vm.ExportFailed(ex.Message);
        }
    }
}
