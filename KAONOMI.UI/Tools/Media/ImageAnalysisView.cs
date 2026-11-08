using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// The analysis of one picture (a double-click in /pictures, a click in /fooocus), one window per picture named
/// Image-&lt;title&gt;. The toolbar: brightness and contrast, Monochrome, Binarize (and its threshold), Edges, Invert,
/// turn left / right, mirror left-right / top-bottom, zoom − / + and Fit, ▭ Annotate, ↺ Reset, Open, ✨ Analyse (the
/// vision model), ⬇ Export PNG (with the annotations), 💾 Save. The picture at its zoom with its numbered rectangles
/// (drawn in annotate mode, selected by a click; Ctrl / ⌘ + wheel zooms; right click: annotate); below it the comments
/// (the previous ones and a new one) and the AI analysis; on the right the title, the place (GPS metadata, map), the
/// annotations with their comments and the metadata. A double-click on the picture shows it alone in the window (again,
/// or Esc, to come back). Keys: Ctrl / ⌘ + S saves, Ctrl / ⌘ + = / − zoom.
/// </summary>
[ToolView(typeof(ImageAnalysisViewModel), "Image", "🔬", 1500, 950, PerObject = true)]
public sealed class ImageAnalysisView : IToolView
{
    private static readonly IBrush AnnotationAmber = Kit.Brush("#F59E0B");
    private static readonly IBrush AnnotationRed = Kit.Brush("#EF4444");

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ImageAnalysisViewModel)viewModel;

        // ── The picture ──
        var canvas = new ImageAnnotationCanvas(vm) { Margin = new Thickness(12) };
        var scroller = Kit.ScrollBoth(canvas);
        scroller.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (ImageAnnotationCanvas.ZoomKey(e.KeyModifiers))
            {
                Ui.Run(e.Delta.Y > 0 ? vm.ZoomInCommand : vm.ZoomOutCommand);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        var loading = Ui.Bound(vm, nameof(vm.Status), Ui.Muted, 12);
        loading.HorizontalAlignment = HorizontalAlignment.Center;
        loading.IsHitTestVisible = false;
        var pictureCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 26, 31)),
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = new Grid { Children = { scroller, loading } }
        };

        // The bitmap of the view model (drawn again at each change of the filters) shown by the canvas; the first one fits.
        var fitted = false;
        Ui.When(canvas, vm, () =>
        {
            canvas.Bitmap = vm.Shown;
            loading.IsVisible = vm.Shown is null;
            if (vm.Shown is not null && !fitted)
            {
                fitted = true;
                FitLater(canvas, scroller, vm);
            }
        }, nameof(vm.Shown));

        // ── Toolbar ──
        TextBlock Value(string name)
        {
            var label = Ui.Bound(vm, name, Ui.Muted, 11);
            label.MinWidth = 28;
            return label;
        }
        Button Big(Button button)
        {
            button.FontSize = 16;
            button.MinWidth = 32;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            return button;
        }
        T BigToggle<T>(T toggle) where T : Avalonia.Controls.Primitives.ToggleButton
        {
            toggle.FontSize = 16;
            toggle.MinWidth = 32;
            toggle.HorizontalContentAlignment = HorizontalAlignment.Center;
            return toggle;
        }
        TextBlock Icon(string text, string tooltip)
        {
            var label = Ui.Label(text, Ui.Text, 15);
            ToolTip.SetTip(label, tooltip);
            return label;
        }

        var threshold = Kit.Slider(vm, nameof(vm.Threshold), 0, 255, 1, "Threshold of the binarization (0 to 255)", 90).Visible(vm, nameof(vm.Binarize));
        var zoom = Ui.Bound(vm, nameof(vm.ZoomText), Ui.TextBright, 12);
        zoom.MinWidth = 48;
        zoom.TextAlignment = TextAlignment.Center;
        var toolbar = Kit.Flow(8,
            Kit.Group(
                Icon("☀", "Brightness"), Kit.Slider(vm, nameof(vm.Brightness), -100, 100, 1, "Brightness (-100 to 100)", 110), Value(nameof(vm.Brightness)),
                Icon("◐", "Contrast"), Kit.Slider(vm, nameof(vm.Contrast), -100, 100, 1, "Contrast (-100 to 100)", 110), Value(nameof(vm.Contrast))),
            Kit.Group(
                Kit.Toggle("Monochrome", vm, nameof(vm.Monochrome), "Shades of grey"),
                Kit.Toggle("Binarize", vm, nameof(vm.Binarize), "Black or white, cut at the threshold"),
                threshold,
                Kit.Toggle("Edges", vm, nameof(vm.Edges), "Extract the edges of the shapes (Sobel)"),
                Kit.Toggle("Invert", vm, nameof(vm.Invert), "The negative of the picture")),
            Kit.Group(
                Big(Ui.Command("⟲", vm.RotateLeftCommand, tooltip: "Turn a quarter to the left")),
                Big(Ui.Command("⟳", vm.RotateRightCommand, tooltip: "Turn a quarter to the right")),
                BigToggle(Kit.Toggle("⇋", vm, nameof(vm.FlipHorizontal), "Mirror left / right")),
                BigToggle(Kit.Toggle("⇵", vm, nameof(vm.FlipVertical), "Mirror top / bottom"))),
            Kit.Group(
                Big(Ui.Command("−", vm.ZoomOutCommand, tooltip: OperatingSystem.IsMacOS() ? "Zoom out (⌘ + wheel down, ⌘ −)" : "Zoom out (Ctrl + wheel down, Ctrl −)")),
                zoom,
                Big(Ui.Command("+", vm.ZoomInCommand, tooltip: OperatingSystem.IsMacOS() ? "Zoom in (⌘ + wheel up, ⌘ =)" : "Zoom in (Ctrl + wheel up, Ctrl =)")),
                Ui.Button("Fit", () => Fit(canvas, scroller, vm), "The whole picture in the window")),
            Kit.Group(
                Kit.Toggle("▭ Annotate", vm, nameof(vm.IsAnnotating), "Draw rectangles on the picture to annotate a part of it; write their comment in the list on the right"),
                Ui.Command("↺ Reset", vm.ResetViewCommand, tooltip: "Remove the brightness, contrast, filters and turns"),
                Ui.Command("Open", vm.OpenInSystemCommand, tooltip: "Open the picture with the viewer of the system")),
            Kit.Group(
                Ui.Command("✨ ANALYSE", vm.AnalyseCommand, primary: true,
                    tooltip: $"Send the picture (as shown) to {vm.VisionModel} (visionmodel in config.yml): a detailed description, and where it could have been taken"),
                Ui.Button("⬇ EXPORT PNG", () => _ = Export(vm), primary: true,
                    tooltip: "Save the picture as shown (brightness, contrast, filters, orientation) with its annotation rectangles, as a PNG file"),
                Ui.Command("💾 SAVE", vm.SaveCommand, primary: true,
                    tooltip: "Save the analysis (settings, annotations, comments, AI analysis, place, actions, link to the picture) in the imageanalysis index (Ctrl / ⌘ + S)")));
        var toolbarPanel = Kit.Toolbar(toolbar);

        // ── Comments and AI analysis ──
        var previous = Kit.Items(vm.PreviousComments, Comment, () => new StackPanel { Spacing = 5 });
        var noComment = Kit.EmptyWhen(Ui.Label("No comment yet", Ui.Muted, 12), vm.PreviousComments);
        // As high as the previous comments need, up to 110 pixels (then they scroll).
        var previousScroll = new ScrollViewer { Content = Ui.Column(4, noComment, previous), MaxHeight = 110 };
        var newComment = Kit.Lines(vm, nameof(vm.NewComment), 64, "A new comment: added to the previous ones at the next Save (they are never removed)");
        newComment.VerticalContentAlignment = VerticalAlignment.Top;
        var commentsTitle = Ui.Caption("Comments", Ui.Warm);
        var comments = Ui.Box(Kit.Rows("Auto,Auto,*", commentsTitle, previousScroll, newComment), 10);
        previousScroll.Margin = new Thickness(0, 6, 0, 6);

        var analysing = new ProgressBar { IsIndeterminate = true, Width = 80, MinWidth = 80, Height = 4, MinHeight = 4, VerticalAlignment = VerticalAlignment.Center }
            .Visible(vm, nameof(vm.IsAnalysing));
        var aiText = Kit.Lines(vm, nameof(vm.AiAnalysis), 64,
            "✨ Analyse writes here a detailed description of the picture by the vision model, and where it could have been taken (it can be changed before Save)");
        aiText.FontSize = 12;
        aiText.VerticalContentAlignment = VerticalAlignment.Top;
        var aiHeader = Kit.FillRow(8, [], Ui.Caption("AI analysis", Ui.Warm), analysing);
        aiHeader.Margin = new Thickness(0, 0, 0, 6);
        var ai = Ui.Box(Kit.Rows("Auto,*", aiHeader, aiText), 10);
        var commentsArea = Kit.Columns("*,*", 10, comments, ai);

        var pictureColumn = Kit.SplitV(pictureCard, commentsArea, "*", "260", 160, 160);

        // ── Side panel: title, place, annotations, metadata ──
        var titleBox = Ui.Edit(vm, nameof(vm.ImageTitle), vm.FileName);
        ToolTip.SetTip(titleBox, "Title of the picture (by default its file name), saved with the analysis; the window is named Image-<title>");
        var location = Kit.Card("Location",
            Kit.Columns("*,*", 8, Ui.Edit(vm, nameof(vm.Latitude), "Latitude (e.g. 48.8584)"), Ui.Edit(vm, nameof(vm.Longitude), "Longitude (e.g. 2.2945)")),
            Ui.Edit(vm, nameof(vm.LocationName), "Place (city, address, landmark…)"),
            Kit.Flow(6,
                Ui.Command("📍 From GPS metadata", vm.LocationFromMetadataCommand, tooltip: "Take the position written by the camera in the picture"),
                Ui.Command("🗺 Show on map", vm.ShowOnMapCommand, tooltip: "Open the place on OpenStreetMap")));

        var commentBoxes = new Dictionary<ImageAnnotationViewModel, TextBox>();
        var annotationList = Kit.Items(vm.Annotations, a => AnnotationRow(vm, a, commentBoxes), () => new StackPanel { Spacing = 6 });
        var noAnnotation = Kit.EmptyWhen(Ui.Label("▭ Annotate, then draw a rectangle on the picture", Ui.Muted, 12, wrap: true), vm.Annotations);
        var removeAnnotation = Ui.Command("Remove", vm.DeleteAnnotationCommand, tooltip: "Remove the selected rectangle");
        removeAnnotation.FontSize = 11;
        var annotations = Kit.Card(null, Kit.FillRow(8, [], Ui.Caption("Annotations", Ui.Warm), removeAnnotation), noAnnotation, annotationList);

        var metadataHolder = new ContentControl();
        Kit.OnCollection(metadataHolder, vm.Metadata, () => metadataHolder.Content = ImageKit.Metadata(vm.Metadata, Ui.TextBright));
        var metadata = Kit.Card("Metadata", Ui.Bound(vm, nameof(vm.Summary), Ui.TextBright, 12, wrap: true), metadataHolder);

        var sideScroll = Ui.Scroll(Kit.Margin(Ui.Column(10, Kit.Card("Title", titleBox), location, annotations, metadata), 0, 0, 8, 8));

        var main = Kit.SplitH(pictureColumn, sideScroll, "*", "380", 300, 300);
        var status = Kit.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var page = Kit.Rows("Auto,*,Auto", toolbarPanel, Kit.Margin(main, 0, 8, 0, 8), status);

        // A rectangle drawn: its comment gets the keyboard.
        canvas.AnnotationCreated += annotation => Dispatcher.UIThread.Post(() =>
        {
            if (commentBoxes.TryGetValue(annotation, out var box))
            {
                box.Focus();
                box.BringIntoView();
            }
        }, DispatcherPriority.Background);

        // A double-click on the picture shows it alone; again, or Esc, brings the window back.
        var pictureOnly = false;
        var splitterColumns = (main.ColumnDefinitions[1].Width, main.ColumnDefinitions[2].Width);
        var splitterRows = (pictureColumn.RowDefinitions[1].Height, pictureColumn.RowDefinitions[2].Height);
        void SetPictureOnly(bool on)
        {
            if (pictureOnly == on)
                return;
            pictureOnly = on;
            toolbarPanel.IsVisible = status.IsVisible = sideScroll.IsVisible = commentsArea.IsVisible = !on;
            foreach (var child in main.Children)
                if (child is GridSplitter s)
                    s.IsVisible = !on;
            foreach (var child in pictureColumn.Children)
                if (child is GridSplitter s)
                    s.IsVisible = !on;
            if (on)
            {
                splitterColumns = (main.ColumnDefinitions[1].Width, main.ColumnDefinitions[2].Width);
                splitterRows = (pictureColumn.RowDefinitions[1].Height, pictureColumn.RowDefinitions[2].Height);
                main.ColumnDefinitions[2].MinWidth = pictureColumn.RowDefinitions[2].MinHeight = 0;
                main.ColumnDefinitions[1].Width = main.ColumnDefinitions[2].Width = new GridLength(0);
                pictureColumn.RowDefinitions[1].Height = pictureColumn.RowDefinitions[2].Height = new GridLength(0);
            }
            else
            {
                (main.ColumnDefinitions[1].Width, main.ColumnDefinitions[2].Width) = splitterColumns;
                (pictureColumn.RowDefinitions[1].Height, pictureColumn.RowDefinitions[2].Height) = splitterRows;
                main.ColumnDefinitions[2].MinWidth = 300;
                pictureColumn.RowDefinitions[2].MinHeight = 160;
            }
            FitLater(canvas, scroller, vm);
        }
        canvas.DoubleClicked += () => SetPictureOnly(!pictureOnly);
        // A right click on the picture offers to annotate it (the part drawn under the pointer, when there is one).
        canvas.RightClicked += at => Kit.AnnotateMenu(canvas, vm.AnnotationTargetFor(canvas.PartAt(at)));

        Kit.Keys(page, e =>
        {
            if (e.Key == Key.Escape && pictureOnly)
            {
                SetPictureOnly(false);
                return true;
            }
            if (!Kit.CommandHeld(e.KeyModifiers))
                return false;
            switch (e.Key)
            {
                case Key.S:
                    Ui.Run(vm.SaveCommand);
                    return true;
                case Key.OemPlus or Key.Add:
                    Ui.Run(vm.ZoomInCommand);
                    return true;
                case Key.OemMinus or Key.Subtract:
                    Ui.Run(vm.ZoomOutCommand);
                    return true;
            }
            return false;
        });

        // The window is named Image-<title>.
        void Rename() => Kit.Retitle(page, vm.Title);
        page.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(Rename, DispatcherPriority.Background);
        Ui.When(page, vm, Rename, nameof(vm.Title));
        return page;
    }

    /// <summary>A previous comment: its date and author, its text.</summary>
    private static Control Comment(ImageCommentItem comment) => new Border
    {
        Child = Ui.Column(2,
            Ui.Label($"{comment.Date.ToLocalTime():yyyy-MM-dd HH:mm}", Ui.Muted, 10),
            new SelectableTextBlock { Text = comment.Text, Foreground = Ui.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap }),
        BorderBrush = Ui.Edge,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(5),
        CornerRadius = new CornerRadius(2)
    };

    /// <summary>An annotation of the list: its number, its comment (selecting it selects the rectangle).</summary>
    private static Control AnnotationRow(ImageAnalysisViewModel vm, ImageAnnotationViewModel annotation, Dictionary<ImageAnnotationViewModel, TextBox> boxes)
    {
        var number = Ui.Bound(annotation, nameof(annotation.Number), Brushes.White, 12);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        var badge = new Border { Child = number, MinWidth = 24, Height = 22, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(2) };
        var comment = Kit.Lines(annotation, nameof(annotation.Comment), 44, "Comment of this part of the picture");
        comment.FontSize = 12;
        comment.AttachedToVisualTree += (_, _) => boxes[annotation] = comment;
        comment.DetachedFromVisualTree += (_, _) => boxes.Remove(annotation);
        comment.GotFocus += (_, _) => vm.SelectedAnnotation = annotation;
        var row = new Border
        {
            Child = Kit.FillRow(8, [badge], comment),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(2)
        };
        row.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (Kit.IsLeft(e, row))
                vm.SelectedAnnotation = annotation;
        }, RoutingStrategies.Tunnel);
        Ui.When(row, vm, () =>
        {
            var selected = ReferenceEquals(vm.SelectedAnnotation, annotation);
            badge.Background = selected ? AnnotationRed : AnnotationAmber;
            row.BorderBrush = selected ? Ui.Accent : Brushes.Transparent;
        }, nameof(vm.SelectedAnnotation));
        return row;
    }

    /// <summary>The zoom that shows the whole picture in the window.</summary>
    private static void Fit(ImageAnnotationCanvas canvas, ScrollViewer scroller, ImageAnalysisViewModel vm)
    {
        var pixels = canvas.PixelSize;
        if (pixels.Width <= 0 || pixels.Height <= 0)
            return;
        var width = Math.Max(100, scroller.Bounds.Width - 30);
        var height = Math.Max(100, scroller.Bounds.Height - 30);
        vm.Zoom = Math.Round(Math.Min(width / pixels.Width, height / pixels.Height), 3);
    }

    /// <summary><see cref="Fit"/> once the layout is done (after a change of the window).</summary>
    private static void FitLater(ImageAnnotationCanvas canvas, ScrollViewer scroller, ImageAnalysisViewModel vm)
    {
        void Later() => Dispatcher.UIThread.Post(() => Fit(canvas, scroller, vm), DispatcherPriority.Background);
        if (scroller.IsAttachedToVisualTree() && scroller.Bounds.Width > 0)
            Later();
        else
        {
            void Attached(object? sender, SizeChangedEventArgs e)
            {
                if (e.NewSize.Width <= 0)
                    return;
                scroller.SizeChanged -= Attached;
                Later();
            }
            scroller.SizeChanged += Attached;
        }
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
