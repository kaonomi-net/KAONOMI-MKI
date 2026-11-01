using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Tools.Images;

/// <summary>
/// ⬇ Export PNG of the image analysis window: the picture as shown (filters, turns) at its own size with its numbered
/// annotation rectangles and their comments, drawn by Avalonia (headless, Skia) exactly as the Avalonia chat does.
/// </summary>
public static class AnnotationExport
{
    private static readonly IBrush StrokeBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#22F59E0B"));

    /// <summary>
    /// Writes the picture as shown, with its annotations, as a PNG file at <paramref name="path"/> (no annotation
    /// selected: all of them drawn alike). Returns the size written.
    /// </summary>
    public static PixelSize ExportPng(ImageAnalysisViewModel viewModel, string path)
    {
        if (viewModel.Shown is not { } bitmap)
            throw new InvalidOperationException("The picture is not loaded yet.");
        var selected = viewModel.SelectedAnnotation;
        viewModel.SelectedAnnotation = null;
        try
        {
            using var target = new RenderTargetBitmap(bitmap.PixelSize, new Vector(96, 96));
            using (var context = target.CreateDrawingContext())
                // Lines and labels as thick, compared with the picture, as on a picture about 900 pixels high.
                Draw(context, viewModel, new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                    Math.Max(1, Math.Min(bitmap.PixelSize.Width, bitmap.PixelSize.Height) / 900.0));
#pragma warning disable CS0618 // The PNG encoder by default.
            target.Save(path);
#pragma warning restore CS0618
            return target.PixelSize;
        }
        finally
        {
            viewModel.SelectedAnnotation = selected;
        }
    }

    private static void Draw(DrawingContext context, ImageAnalysisViewModel viewModel, Size size, double scale)
    {
        if (viewModel.Shown is not { } bitmap)
            return;
        context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(size));
        var stroke = new Pen(StrokeBrush, 2 * scale);
        var selectedStroke = new Pen(SelectedBrush, 3 * scale);
        var adjustments = viewModel.Adjustments;
        foreach (var annotation in viewModel.Annotations)
        {
            var shown = ImagePixels.ToView(annotation.Rect, adjustments);
            var rect = new Rect(shown.X * size.Width, shown.Y * size.Height, shown.Width * size.Width, shown.Height * size.Height);
            var isSelected = ReferenceEquals(annotation, viewModel.SelectedAnnotation);
            context.DrawRectangle(Fill, isSelected ? selectedStroke : stroke, rect);
            var label = new FormattedText(annotation.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 12 * scale, Brushes.White);
            var badge = new Rect(rect.X, rect.Y, Math.Max(18 * scale, label.Width + 8 * scale), 18 * scale);
            context.DrawRectangle(isSelected ? SelectedBrush : StrokeBrush, null, badge, 3 * scale, 3 * scale);
            context.DrawText(label, new Point(badge.X + (badge.Width - label.Width) / 2, badge.Y + (badge.Height - label.Height) / 2));
            if (annotation.Comment.Length > 0 && rect.Width > 40 * scale)
            {
                var text = new FormattedText(annotation.Comment, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11 * scale, Brushes.White)
                {
                    MaxTextWidth = Math.Max(20, rect.Width - badge.Width - 8 * scale),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis
                };
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#AA000000")), null,
                    new Rect(badge.Right + 2 * scale, rect.Y, text.Width + 6 * scale, 18 * scale), 3 * scale, 3 * scale);
                context.DrawText(text, new Point(badge.Right + 5 * scale, rect.Y + (18 * scale - text.Height) / 2));
            }
        }
    }
}
