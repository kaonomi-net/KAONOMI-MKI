using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace KAONOMI.Chat.Services;

/// <summary>Filters of the image analysis window, in the order they apply.</summary>
public enum ImageFilter
{
    /// <summary>Shades of grey.</summary>
    Monochrome,

    /// <summary>Black or white, cut at a threshold.</summary>
    Binarize,

    /// <summary>The edges of the shapes (Sobel), white on black.</summary>
    Edges,

    /// <summary>The negative.</summary>
    Invert
}

/// <summary>How the picture is shown: brightness, contrast, filters, orientation.</summary>
public sealed record ImageAdjustments(
    int Brightness, int Contrast, IReadOnlyCollection<ImageFilter> Filters, int Threshold, int Rotation, bool FlipHorizontal, bool FlipVertical);

/// <summary>
/// Pixels of a picture (BGRA, premultiplied) and the processing of the image analysis window: brightness and contrast
/// (-100 to 100), monochrome, binarization at a threshold, edges (Sobel), invert, then rotation by quarter turns and
/// mirrors. Pure C# on the pixel buffer, run in the background by the caller.
/// </summary>
public sealed class ImagePixels
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>BGRA bytes, 4 per pixel, row after row.</summary>
    public byte[] Data { get; }

    public ImagePixels(int width, int height, byte[] data)
    {
        Width = width;
        Height = height;
        Data = data;
    }

    /// <summary>The pixels of <paramref name="bitmap"/>, converted to premultiplied BGRA.</summary>
    public static ImagePixels From(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var data = new byte[size.Width * size.Height * 4];
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), data.Length, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }
        // Copied in the format of the bitmap: RGBA is turned into BGRA, straight alpha into premultiplied.
        var rgba = bitmap.Format is { } format && format == PixelFormat.Rgba8888;
        var straight = bitmap.AlphaFormat == AlphaFormat.Unpremul;
        if (rgba || straight)
            for (var i = 0; i < data.Length; i += 4)
            {
                if (rgba)
                    (data[i], data[i + 2]) = (data[i + 2], data[i]);
                if (straight)
                {
                    var alpha = data[i + 3];
                    data[i] = (byte)(data[i] * alpha / 255);
                    data[i + 1] = (byte)(data[i + 1] * alpha / 255);
                    data[i + 2] = (byte)(data[i + 2] * alpha / 255);
                }
            }
        return new ImagePixels(size.Width, size.Height, data);
    }

    /// <summary>A bitmap of these pixels, to show.</summary>
    public WriteableBitmap ToBitmap()
    {
        var bitmap = new WriteableBitmap(new PixelSize(Width, Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        for (var row = 0; row < Height; row++)
            Marshal.Copy(Data, row * Width * 4, frame.Address + row * frame.RowBytes, Width * 4);
        return bitmap;
    }

    /// <summary>The pixels in <paramref name="part"/> (shares of the width and height of the picture).</summary>
    public ImagePixels Crop(Rect part)
    {
        var x = Math.Clamp((int)Math.Floor(part.X * Width), 0, Width - 1);
        var y = Math.Clamp((int)Math.Floor(part.Y * Height), 0, Height - 1);
        var width = Math.Clamp((int)Math.Ceiling(part.Right * Width) - x, 1, Width - x);
        var height = Math.Clamp((int)Math.Ceiling(part.Bottom * Height) - y, 1, Height - y);
        var data = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(Data, ((y + row) * Width + x) * 4, data, row * width * 4, width * 4);
        return new ImagePixels(width, height, data);
    }

    /// <summary>The picture processed by <paramref name="adjustments"/> (a new buffer; this one is kept).</summary>
    public ImagePixels Apply(ImageAdjustments adjustments)
    {
        var data = (byte[])Data.Clone();
        var filters = new HashSet<ImageFilter>(adjustments.Filters);

        // Brightness and contrast, on the colors (premultiplied: scaled within the alpha).
        if (adjustments.Brightness != 0 || adjustments.Contrast != 0)
        {
            var brightness = adjustments.Brightness * 255 / 100.0;
            var c = Math.Clamp(adjustments.Contrast, -99, 100) * 255 / 100.0;
            var factor = 259 * (c + 255) / (255 * (259 - c));
            var table = new byte[256];
            for (var i = 0; i < 256; i++)
                table[i] = (byte)Math.Clamp(Math.Round(factor * (i - 128) + 128 + brightness), 0, 255);
            for (var i = 0; i < data.Length; i += 4)
            {
                var alpha = data[i + 3];
                if (alpha == 0)
                    continue;
                for (var k = 0; k < 3; k++)
                {
                    var straight = data[i + k] * 255 / alpha;
                    data[i + k] = (byte)(table[Math.Min(255, straight)] * alpha / 255);
                }
            }
        }

        if (filters.Contains(ImageFilter.Monochrome) || filters.Contains(ImageFilter.Binarize) || filters.Contains(ImageFilter.Edges))
            for (var i = 0; i < data.Length; i += 4)
            {
                var grey = (byte)Math.Round(0.0722 * data[i] + 0.7152 * data[i + 1] + 0.2126 * data[i + 2]);
                data[i] = data[i + 1] = data[i + 2] = grey;
            }

        if (filters.Contains(ImageFilter.Binarize))
            for (var i = 0; i < data.Length; i += 4)
            {
                var value = data[i] >= adjustments.Threshold * data[i + 3] / 255 ? data[i + 3] : (byte)0;
                data[i] = data[i + 1] = data[i + 2] = value;
            }

        if (filters.Contains(ImageFilter.Edges))
            data = Sobel(data, Width, Height);

        if (filters.Contains(ImageFilter.Invert))
            for (var i = 0; i < data.Length; i += 4)
                for (var k = 0; k < 3; k++)
                    data[i + k] = (byte)(data[i + 3] - data[i + k]);

        var result = new ImagePixels(Width, Height, data);
        for (var turn = 0; turn < ((adjustments.Rotation % 360) + 360) % 360 / 90; turn++)
            result = result.RotateClockwise();
        if (adjustments.FlipHorizontal)
            result = result.Flip(horizontal: true);
        if (adjustments.FlipVertical)
            result = result.Flip(horizontal: false);
        return result;
    }

    /// <summary>The edges of a grey picture: the strength of the gradient (Sobel), white on black.</summary>
    private static byte[] Sobel(byte[] grey, int width, int height)
    {
        var result = new byte[grey.Length];
        int G(int x, int y) => grey[(Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)) * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var gx = -G(x - 1, y - 1) - 2 * G(x - 1, y) - G(x - 1, y + 1) + G(x + 1, y - 1) + 2 * G(x + 1, y) + G(x + 1, y + 1);
                var gy = -G(x - 1, y - 1) - 2 * G(x, y - 1) - G(x + 1, y - 1) + G(x - 1, y + 1) + 2 * G(x, y + 1) + G(x + 1, y + 1);
                var value = (byte)Math.Min(255, Math.Sqrt(gx * gx + gy * gy));
                var i = (y * width + x) * 4;
                result[i] = result[i + 1] = result[i + 2] = value;
                result[i + 3] = 255;
            }
        return result;
    }

    private ImagePixels RotateClockwise()
    {
        var data = new byte[Data.Length];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                // (x, y) goes to (Height - 1 - y, x) in a picture Height wide.
                var from = (y * Width + x) * 4;
                var to = (x * Height + (Height - 1 - y)) * 4;
                Buffer.BlockCopy(Data, from, data, to, 4);
            }
        return new ImagePixels(Height, Width, data);
    }

    private ImagePixels Flip(bool horizontal)
    {
        var data = new byte[Data.Length];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var from = (y * Width + x) * 4;
                var to = horizontal ? (y * Width + (Width - 1 - x)) * 4 : ((Height - 1 - y) * Width + x) * 4;
                Buffer.BlockCopy(Data, from, data, to, 4);
            }
        return new ImagePixels(Width, Height, data);
    }

    /// <summary>
    /// A point of the original picture (shares of its width and height) where it shows once rotated and mirrored by
    /// <paramref name="adjustments"/>; <see cref="FromView"/> goes back.
    /// </summary>
    public static Point ToView(Point p, ImageAdjustments adjustments)
    {
        var (u, v) = (p.X, p.Y);
        for (var turn = 0; turn < ((adjustments.Rotation % 360) + 360) % 360 / 90; turn++)
            (u, v) = (1 - v, u);
        if (adjustments.FlipHorizontal)
            u = 1 - u;
        if (adjustments.FlipVertical)
            v = 1 - v;
        return new Point(u, v);
    }

    /// <summary>The point of the original picture shown at <paramref name="p"/> (shares of the shown picture).</summary>
    public static Point FromView(Point p, ImageAdjustments adjustments)
    {
        var (u, v) = (p.X, p.Y);
        if (adjustments.FlipVertical)
            v = 1 - v;
        if (adjustments.FlipHorizontal)
            u = 1 - u;
        for (var turn = 0; turn < ((adjustments.Rotation % 360) + 360) % 360 / 90; turn++)
            (u, v) = (v, 1 - u);
        return new Point(u, v);
    }

    /// <summary>A rectangle of the original picture (shares) as it shows once rotated and mirrored.</summary>
    public static Rect ToView(Rect r, ImageAdjustments adjustments)
    {
        var a = ToView(r.TopLeft, adjustments);
        var b = ToView(r.BottomRight, adjustments);
        return new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    }

    /// <summary>A rectangle of the shown picture (shares) in the original picture.</summary>
    public static Rect FromView(Rect r, ImageAdjustments adjustments)
    {
        var a = FromView(r.TopLeft, adjustments);
        var b = FromView(r.BottomRight, adjustments);
        return new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    }
}
