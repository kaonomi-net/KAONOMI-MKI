using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// The picture of the image analysis window, at its zoom, with its annotations: numbered rectangles (amber, the selected
/// one red and thicker) placed on the original picture and turned with it, their comment beside their number. In
/// annotate mode, dragging draws a new rectangle (dashed blue while drawn); otherwise a click on a rectangle selects it.
/// Ctrl / ⌘ + wheel zooms; a double-click is raised for the picture-only mode, a right click for the menu.
/// </summary>
public sealed class ImageAnnotationCanvas : Control
{
    private static readonly IBrush Stroke = Kit.Brush("#F59E0B");
    private static readonly IBrush SelectedStroke = Kit.Brush("#EF4444");
    private static readonly IBrush Draft = Kit.Brush("#3B82F6");
    private static readonly IBrush FillColor = Kit.Brush("#22F59E0B");
    private static readonly IBrush CommentBack = Kit.Brush("#AA000000");

    private readonly ImageAnalysisViewModel _viewModel;
    private Bitmap? _bitmap;
    private Point? _dragStart;
    private Point _dragEnd;

    public ImageAnnotationCanvas(ImageAnalysisViewModel viewModel)
    {
        _viewModel = viewModel;
        ClipToBounds = true;
        Focusable = true;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        PropertyChangedEventHandler changed = (_, e) => Ui.OnMain(() =>
        {
            if (e.PropertyName is nameof(ImageAnalysisViewModel.Zoom))
                InvalidateMeasure();
            if (e.PropertyName is nameof(ImageAnalysisViewModel.IsAnnotating))
                Cursor = new Cursor(viewModel.IsAnnotating ? StandardCursorType.Cross : StandardCursorType.Arrow);
            InvalidateVisual();
        });
        PropertyChangedEventHandler annotationChanged = (_, _) => Ui.OnMain(InvalidateVisual);
        NotifyCollectionChangedEventHandler annotations = (_, e) => Ui.OnMain(() =>
        {
            foreach (ImageAnnotationViewModel annotation in e.NewItems ?? Array.Empty<object>())
                annotation.PropertyChanged += annotationChanged;
            foreach (ImageAnnotationViewModel annotation in e.OldItems ?? Array.Empty<object>())
                annotation.PropertyChanged -= annotationChanged;
            InvalidateVisual();
        });
        AttachedToVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged += changed;
            viewModel.Annotations.CollectionChanged += annotations;
            foreach (var annotation in viewModel.Annotations)
                annotation.PropertyChanged += annotationChanged;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged -= changed;
            viewModel.Annotations.CollectionChanged -= annotations;
            foreach (var annotation in viewModel.Annotations)
                annotation.PropertyChanged -= annotationChanged;
        };
    }

    /// <summary>A rectangle has been drawn (its comment is to be written).</summary>
    public event Action<ImageAnnotationViewModel>? AnnotationCreated;

    /// <summary>A double-click on the picture.</summary>
    public event Action? DoubleClicked;

    /// <summary>A right click on the picture, where it was (coordinates of the canvas).</summary>
    public event Action<Point>? RightClicked;

    /// <summary>Pixels of the picture shown, 0 before it is loaded.</summary>
    public Size PixelSize => _bitmap is null ? default : new Size(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height);

    /// <summary>The picture as shown (the bitmap of the view model).</summary>
    public Bitmap? Bitmap
    {
        get => _bitmap;
        set
        {
            _bitmap = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Size of the picture as shown (its pixels times the zoom).</summary>
    private Size PictureSize => PixelSize * _viewModel.Zoom;

    protected override Size MeasureOverride(Size availableSize) => PictureSize;

    private Rect Shown(ImageAnnotationViewModel annotation, Size size)
    {
        var shown = ImagePixels.ToView(annotation.Rect, _viewModel.Adjustments);
        return new Rect(shown.X * size.Width, shown.Y * size.Height, shown.Width * size.Width, shown.Height * size.Height);
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap is null)
            return;
        var size = PictureSize;
        context.DrawImage(_bitmap, new Rect(_bitmap.Size), new Rect(size));
        foreach (var annotation in _viewModel.Annotations)
        {
            var rect = Shown(annotation, size);
            var selected = ReferenceEquals(annotation, _viewModel.SelectedAnnotation);
            var stroke = selected ? SelectedStroke : Stroke;
            context.DrawRectangle(FillColor, new Pen(stroke, selected ? 3 : 2), rect);
            var number = new FormattedText(annotation.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
            var badge = new Rect(rect.X, rect.Y, Math.Max(18, number.Width + 8), 18);
            context.FillRectangle(stroke, badge);
            context.DrawText(number, new Point(badge.X + (badge.Width - number.Width) / 2, badge.Y + (badge.Height - number.Height) / 2));
            if (annotation.Comment.Length > 0 && rect.Width > 40)
            {
                var text = new FormattedText(annotation.Comment.Replace('\n', ' '), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White)
                {
                    MaxTextWidth = Math.Max(20, rect.Width - badge.Width - 8),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis
                };
                context.FillRectangle(CommentBack, new Rect(badge.Right + 2, rect.Y, text.Width + 6, 18));
                context.DrawText(text, new Point(badge.Right + 5, rect.Y + (18 - text.Height) / 2));
            }
        }
        if (_dragStart is { } start)
        {
            var draft = new Rect(start, _dragEnd).Normalize();
            context.DrawRectangle(null, new Pen(Draft, 2) { DashStyle = new DashStyle([2, 2], 0) }, draft);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // Ctrl / ⌘ + wheel zooms (the wheel alone scrolls).
        if (ZoomKey(e.KeyModifiers))
        {
            Ui.Run(e.Delta.Y > 0 ? _viewModel.ZoomInCommand : _viewModel.ZoomOutCommand);
            e.Handled = true;
            return;
        }
        base.OnPointerWheelChanged(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            RightClicked?.Invoke(e.GetPosition(this));
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            _dragStart = null;
            DoubleClicked?.Invoke();
            return;
        }
        Focus();
        var position = Clamp(e.GetPosition(this));
        if (_viewModel.IsAnnotating)
        {
            _dragStart = _dragEnd = position;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        else
            _viewModel.SelectedAnnotation = AnnotationAt(position);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is null)
            return;
        _dragEnd = Clamp(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is not { } start)
            return;
        _dragStart = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        var rect = new Rect(start, Clamp(e.GetPosition(this))).Normalize();
        var size = PictureSize;
        if (rect.Width < 6 || rect.Height < 6 || size.Width <= 0)
            return;
        var shares = new Rect(rect.X / size.Width, rect.Y / size.Height, rect.Width / size.Width, rect.Height / size.Height);
        AnnotationCreated?.Invoke(_viewModel.AddAnnotation(shares));
    }

    /// <summary>True when the key of the zoom is held with the wheel: ⌘ on macOS (or Ctrl), Ctrl elsewhere.</summary>
    public static bool ZoomKey(KeyModifiers modifiers) =>
        OperatingSystem.IsMacOS() ? modifiers.HasFlag(KeyModifiers.Meta) || modifiers.HasFlag(KeyModifiers.Control) : modifiers.HasFlag(KeyModifiers.Control);

    private Point Clamp(Point p)
    {
        var size = PictureSize;
        return new Point(Math.Clamp(p.X, 0, size.Width), Math.Clamp(p.Y, 0, size.Height));
    }

    /// <summary>The annotation drawn under <paramref name="position"/> (coordinates of the canvas), or null.</summary>
    public ImageAnnotationViewModel? PartAt(Point position) => AnnotationAt(position);

    /// <summary>The annotation under <paramref name="position"/> (the smallest one when they overlap).</summary>
    private ImageAnnotationViewModel? AnnotationAt(Point position)
    {
        var size = PictureSize;
        ImageAnnotationViewModel? found = null;
        var smallest = double.MaxValue;
        foreach (var annotation in _viewModel.Annotations)
        {
            var rect = Shown(annotation, size);
            if (rect.Contains(position) && rect.Width * rect.Height < smallest)
                (found, smallest) = (annotation, rect.Width * rect.Height);
        }
        return found;
    }
}
