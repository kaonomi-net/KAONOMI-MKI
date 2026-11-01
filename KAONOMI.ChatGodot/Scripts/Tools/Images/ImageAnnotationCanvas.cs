using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Images;

/// <summary>
/// The picture of the image analysis window, at its zoom, with its annotations: numbered rectangles (amber, the selected
/// one red and thicker) placed on the original picture and turned with it, their comment beside their number. In
/// annotate mode, dragging draws a new rectangle (dashed blue while drawn); otherwise a click on a rectangle selects it.
/// Ctrl / ⌘ + wheel zooms; a double-click is raised for the picture-only mode.
/// </summary>
public partial class ImageAnnotationCanvas : Control
{
    private static readonly Color Stroke = Color.FromHtml("#F59E0B");
    private static readonly Color SelectedStroke = Color.FromHtml("#EF4444");
    private static readonly Color Draft = Color.FromHtml("#3B82F6");
    private static readonly Color FillColor = Color.FromHtml("#F59E0B22");

    private readonly ImageAnalysisViewModel? _viewModel;
    private Texture2D? _texture;
    private Vector2? _dragStart;
    private Vector2 _dragEnd;

    public ImageAnnotationCanvas()
    {
    }

    public ImageAnnotationCanvas(ImageAnalysisViewModel viewModel)
    {
        _viewModel = viewModel;
        ClipContents = true;
        FocusMode = FocusModeEnum.Click;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        PropertyChangedEventHandler changed = (_, e) => Bind.OnMain(() =>
        {
            if (!IsInstanceValid(this))
                return;
            if (e.PropertyName is nameof(ImageAnalysisViewModel.Zoom))
                Resize();
            if (e.PropertyName is nameof(ImageAnalysisViewModel.IsAnnotating))
                MouseDefaultCursorShape = viewModel.IsAnnotating ? CursorShape.Cross : CursorShape.Arrow;
            QueueRedraw();
        });
        NotifyCollectionChangedEventHandler annotations = (_, e) => Bind.OnMain(() =>
        {
            foreach (ImageAnnotationViewModel annotation in e.NewItems ?? Array.Empty<object>())
                annotation.PropertyChanged += (_, _) => Bind.OnMain(() =>
                {
                    if (IsInstanceValid(this))
                        QueueRedraw();
                });
            if (IsInstanceValid(this))
                QueueRedraw();
        });
        viewModel.PropertyChanged += changed;
        viewModel.Annotations.CollectionChanged += annotations;
        foreach (var annotation in viewModel.Annotations)
            annotation.PropertyChanged += (_, _) => Bind.OnMain(() =>
            {
                if (IsInstanceValid(this))
                    QueueRedraw();
            });
        Bind.OnGone(this, () =>
        {
            viewModel.PropertyChanged -= changed;
            viewModel.Annotations.CollectionChanged -= annotations;
        });
    }

    /// <summary>A rectangle has been drawn (its comment is to be written).</summary>
    public event Action<ImageAnnotationViewModel>? AnnotationCreated;

    /// <summary>A double-click on the picture.</summary>
    public event Action? DoubleClicked;

    /// <summary>Pixels of the picture shown (its texture), 0 before it is loaded.</summary>
    public Vector2 PixelSize => _texture?.GetSize() ?? Vector2.Zero;

    /// <summary>The picture as shown (set by the view when the bitmap of the view model is converted).</summary>
    public Texture2D? Texture
    {
        get => _texture;
        set
        {
            _texture = value;
            Resize();
            QueueRedraw();
        }
    }

    /// <summary>Size of the picture as shown (its pixels times the zoom).</summary>
    private Vector2 PictureSize => _viewModel is null ? Vector2.Zero : PixelSize * (float)_viewModel.Zoom;

    private void Resize()
    {
        CustomMinimumSize = PictureSize;
        Size = PictureSize;
    }

    public override void _Draw()
    {
        if (_viewModel is null || _texture is null)
            return;
        var size = PictureSize;
        DrawTextureRect(_texture, new Rect2(Vector2.Zero, size), false);
        var font = GetThemeDefaultFont();
        var adjustments = _viewModel.Adjustments;
        foreach (var annotation in _viewModel.Annotations)
        {
            var shown = ImagePixels.ToView(annotation.Rect, adjustments);
            var rect = new Rect2((float)(shown.X * size.X), (float)(shown.Y * size.Y), (float)(shown.Width * size.X), (float)(shown.Height * size.Y));
            var selected = ReferenceEquals(annotation, _viewModel.SelectedAnnotation);
            DrawRect(rect, FillColor);
            DrawRect(rect, selected ? SelectedStroke : Stroke, false, selected ? 3 : 2);
            var number = annotation.Number.ToString(CultureInfo.InvariantCulture);
            var numberSize = font.GetStringSize(number, HorizontalAlignment.Left, -1, 12);
            var badge = new Rect2(rect.Position, new Vector2(Math.Max(18, numberSize.X + 8), 18));
            DrawRect(badge, selected ? SelectedStroke : Stroke);
            DrawString(font, new Vector2(badge.Position.X + (badge.Size.X - numberSize.X) / 2, badge.Position.Y + 13), number, HorizontalAlignment.Left, -1, 12, Colors.White);
            if (annotation.Comment.Length > 0 && rect.Size.X > 40)
            {
                var width = Math.Max(20, rect.Size.X - badge.Size.X - 8);
                var line = annotation.Comment.Replace('\n', ' ');
                var textSize = font.GetStringSize(line, HorizontalAlignment.Left, -1, 11);
                var shownWidth = Math.Min(width, textSize.X);
                DrawRect(new Rect2(badge.End.X + 2, rect.Position.Y, shownWidth + 6, 18), Eve.Eve.Sunken(0.67f));
                DrawString(font, new Vector2(badge.End.X + 5, rect.Position.Y + 13), line, HorizontalAlignment.Left, shownWidth, 11, Colors.White,
                    TextServer.JustificationFlag.None);
            }
        }
        if (_dragStart is { } start)
        {
            var draft = new Rect2(start, _dragEnd - start).Abs();
            DrawRect(draft, Draft with { A = 0.13f });
            DrawDashedRect(draft, Draft);
        }
    }

    private void DrawDashedRect(Rect2 r, Color color)
    {
        var corners = new[] { r.Position, new Vector2(r.End.X, r.Position.Y), r.End, new Vector2(r.Position.X, r.End.Y) };
        for (var i = 0; i < 4; i++)
            DrawDashedLine(corners[i], corners[(i + 1) % 4], color, 2, 4);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (_viewModel is null)
            return;
        switch (e)
        {
            // Ctrl / ⌘ + wheel zooms (the wheel alone scrolls).
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel when ZoomKey(wheel):
                Bind.Run(wheel.ButtonIndex == MouseButton.WheelUp ? _viewModel.ZoomInCommand : _viewModel.ZoomOutCommand);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }:
                _dragStart = null;
                DoubleClicked?.Invoke();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                GrabFocus();
                var position = Clamp(press.Position);
                if (_viewModel.IsAnnotating)
                {
                    _dragStart = _dragEnd = position;
                    QueueRedraw();
                }
                else
                    _viewModel.SelectedAnnotation = AnnotationAt(position);
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _dragStart is not null:
                _dragEnd = Clamp(motion.Position);
                QueueRedraw();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release when _dragStart is { } start:
                _dragStart = null;
                QueueRedraw();
                var rect = new Rect2(start, Clamp(release.Position) - start).Abs();
                var size = PictureSize;
                AcceptEvent();
                if (rect.Size.X < 6 || rect.Size.Y < 6 || size.X <= 0)
                    return;
                var shares = new Avalonia.Rect(rect.Position.X / size.X, rect.Position.Y / size.Y, rect.Size.X / size.X, rect.Size.Y / size.Y);
                AnnotationCreated?.Invoke(_viewModel.AddAnnotation(shares));
                break;
        }
    }

    /// <summary>True when the key of the zoom is held with the wheel: ⌘ on macOS (or Ctrl), Ctrl elsewhere.</summary>
    public static bool ZoomKey(InputEventWithModifiers e) => OperatingSystem.IsMacOS() ? e.MetaPressed || e.CtrlPressed : e.CtrlPressed;

    private Vector2 Clamp(Vector2 p)
    {
        var size = PictureSize;
        return new Vector2(Math.Clamp(p.X, 0, size.X), Math.Clamp(p.Y, 0, size.Y));
    }

    /// <summary>The annotation under <paramref name="position"/> (the smallest one when they overlap).</summary>
    /// <summary>The annotation drawn under <paramref name="position"/> (coordinates of the canvas), or null.</summary>
    public ImageAnnotationViewModel? PartAt(Vector2 position) => AnnotationAt(position);

    private ImageAnnotationViewModel? AnnotationAt(Vector2 position)
    {
        if (_viewModel is null)
            return null;
        var size = PictureSize;
        var adjustments = _viewModel.Adjustments;
        ImageAnnotationViewModel? found = null;
        var smallest = double.MaxValue;
        foreach (var annotation in _viewModel.Annotations)
        {
            var shown = ImagePixels.ToView(annotation.Rect, adjustments);
            var rect = new Rect2((float)(shown.X * size.X), (float)(shown.Y * size.Y), (float)(shown.Width * size.X), (float)(shown.Height * size.Y));
            if (rect.HasPoint(position) && rect.Size.X * rect.Size.Y < smallest)
                (found, smallest) = (annotation, rect.Size.X * rect.Size.Y);
        }
        return found;
    }
}
