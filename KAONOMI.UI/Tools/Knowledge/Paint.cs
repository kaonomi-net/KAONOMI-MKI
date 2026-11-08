using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The colours of the theme as colours (with an opacity) and the flat 2D drawing of the canvases of the knowledge tools:
/// lines, polylines, polygons, circles, arcs, rectangles, dashed lines and texts laid out in a width (cached).
/// </summary>
internal static class Paint
{
    // ── Colours ──

    private static Color Of(IBrush brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;

    public static Color Cyan => Of(Ui.Accent);
    public static Color Amber => Of(Ui.Warm);
    public static Color Red => Of(Ui.Red);
    public static Color Green => Of(Ui.Green);
    public static Color Edge => Of(Ui.Edge);
    public static Color Text => Of(Ui.Text);
    public static Color TextBright => Of(Ui.TextBright);
    public static Color Muted => Of(Ui.Muted);
    public static Color Field => Of(Ui.Field);
    public static Color Panel => Of(Ui.Panel);

    /// <summary>True with a light theme (a light panel).</summary>
    public static bool IsLight
    {
        get
        {
            var c = Panel;
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.5;
        }
    }

    /// <summary>The background of the canvases.</summary>
    public static Color Background => IsLight ? Rgb(0.985, 0.99, 0.995) : Rgb(0.02, 0.03, 0.04);

    /// <summary>A solid panel colour (handles, labels).</summary>
    public static Color PanelSolid => IsLight ? Colors.White : Rgb(0.05, 0.07, 0.09);

    public static Color Hex(string hex) => Color.Parse(hex);

    public static Color Rgb(double r, double g, double b, double a = 1) =>
        Color.FromArgb(Byte(a), Byte(r), Byte(g), Byte(b));

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    /// <summary><paramref name="color"/> with the opacity <paramref name="alpha"/> (0 to 1).</summary>
    public static Color Alpha(Color color, double alpha) => Color.FromArgb(Byte(alpha), color.R, color.G, color.B);

    /// <summary>The opacity of <paramref name="color"/> multiplied by <paramref name="factor"/>.</summary>
    public static Color Fade(Color color, double factor) => Color.FromArgb(Byte(color.A / 255.0 * factor), color.R, color.G, color.B);

    public static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Lighter by <paramref name="amount"/> (toward white), darker when negative (toward black).</summary>
    public static Color Lighten(Color c, double amount) =>
        amount >= 0 ? Lerp(c, Color.FromArgb(c.A, 255, 255, 255), amount) : Lerp(c, Color.FromArgb(c.A, 0, 0, 0), -amount);

    public static IBrush Brush(Color color) => new ImmutableSolidColorBrush(color);

    public static IPen Pen(Color color, double width, double dash = 0) =>
        new ImmutablePen(new ImmutableSolidColorBrush(color), width,
            dash > 0 ? new ImmutableDashStyle([dash / Math.Max(0.5, width), dash / Math.Max(0.5, width)], 0) : null,
            PenLineCap.Round, PenLineJoin.Round);

    // ── Shapes ──

    public static void Line(DrawingContext context, Point a, Point b, Color color, double width = 1) =>
        context.DrawLine(Pen(color, width), a, b);

    public static void Dashed(DrawingContext context, Point a, Point b, Color color, double width, double dash) =>
        context.DrawLine(Pen(color, width, dash), a, b);

    public static void Polyline(DrawingContext context, IReadOnlyList<Point> points, Color color, double width, bool closed = false)
    {
        if (points.Count < 2)
            return;
        context.DrawGeometry(null, Pen(color, width), Geometry(points, closed, false));
    }

    public static void Polygon(DrawingContext context, IReadOnlyList<Point> points, Color fill)
    {
        if (points.Count < 3)
            return;
        context.DrawGeometry(Brush(fill), null, Geometry(points, true, true));
    }

    public static StreamGeometry Geometry(IReadOnlyList<Point> points, bool closed, bool filled)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(points[0], filled);
        for (var i = 1; i < points.Count; i++)
            g.LineTo(points[i]);
        g.EndFigure(closed);
        return geometry;
    }

    public static void Circle(DrawingContext context, Point center, double radius, Color fill) =>
        context.DrawEllipse(Brush(fill), null, center, radius, radius);

    public static void Ring(DrawingContext context, Point center, double radius, Color color, double width) =>
        context.DrawEllipse(null, Pen(color, width), center, radius, radius);

    /// <summary>An arc from the angle <paramref name="from"/> to <paramref name="to"/> (radians, clockwise on the screen).</summary>
    public static void Arc(DrawingContext context, Point center, double radius, double from, double to, Color color, double width, int segments = 24)
    {
        var points = new Point[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var a = from + (to - from) * i / segments;
            points[i] = new Point(center.X + Math.Cos(a) * radius, center.Y + Math.Sin(a) * radius);
        }
        Polyline(context, points, color, width);
    }

    public static void Rect(DrawingContext context, Rect rect, Color fill) => context.FillRectangle(Brush(fill), rect);

    public static void Outline(DrawingContext context, Rect rect, Color color, double width = 1) =>
        context.DrawRectangle(null, Pen(color, width), rect);

    public static void Round(DrawingContext context, Rect rect, double radius, Color? fill, Color? stroke = null, double width = 1) =>
        context.DrawRectangle(fill is { } f ? Brush(f) : null, stroke is { } s ? Pen(s, width) : null, rect, radius, radius);

    public static void DashedRect(DrawingContext context, Rect rect, Color color, double width, double dash) =>
        context.DrawRectangle(null, Pen(color, width, dash), rect);

    // ── Geometry ──

    public static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static double Length(Vector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

    public static Vector Normalized(Vector v)
    {
        var length = Length(v);
        return length < 1e-9 ? new Vector(1, 0) : v / length;
    }

    public static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    public static Rect Grow(Rect r, double by) => new(r.X - by, r.Y - by, Math.Max(0, r.Width + 2 * by), Math.Max(0, r.Height + 2 * by));

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var length = ab.X * ab.X + ab.Y * ab.Y;
        if (length < 1e-9)
            return Distance(p, a);
        var t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / length, 0, 1);
        return Distance(p, new Point(a.X + ab.X * t, a.Y + ab.Y * t));
    }

    public static double PosMod(double a, double b) => ((a % b) + b) % b;

    // ── Texts ──

    public static readonly Typeface Body = new(FontFamily.Default);
    public static readonly Typeface SemiBold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    public static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    public static readonly Typeface Italic = new(FontFamily.Default, FontStyle.Italic);
    public static readonly Typeface Mono = new(Ui.Mono);

    private static readonly Dictionary<(string, int, int, int, Typeface, Color, TextAlignment), FormattedText> Texts = [];

    /// <summary>
    /// <paramref name="text"/> laid out in <paramref name="width"/> (0: no limit) on at most <paramref name="lines"/>
    /// lines with an ellipsis, in <paramref name="color"/>; cached.
    /// </summary>
    public static FormattedText Layout(string text, double size, Color color, double width = 0, int lines = 1, Typeface? typeface = null, TextAlignment alignment = TextAlignment.Left)
    {
        var face = typeface ?? Body;
        var em = Math.Max(1, Math.Round(size * 2) / 2);
        var key = (text, (int)(em * 2), (int)width, lines, face, color, alignment);
        if (Texts.TryGetValue(key, out var formatted))
            return formatted;
        if (Texts.Count > 4000)
            Texts.Clear();
        formatted = new FormattedText(text ?? string.Empty, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, em, Brush(color))
        {
            TextAlignment = alignment,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = Math.Max(1, lines)
        };
        if (width > 0)
            formatted.MaxTextWidth = Math.Max(1, width);
        Texts[key] = formatted;
        return formatted;
    }

    /// <summary>Draws <paramref name="text"/> with its top left at <paramref name="at"/>; its size.</summary>
    public static Size Write(DrawingContext context, string text, Point at, double size, Color color, double width = 0, int lines = 1,
        Typeface? typeface = null, TextAlignment alignment = TextAlignment.Left)
    {
        var formatted = Layout(text, size, color, width, lines, typeface, alignment);
        context.DrawText(formatted, at);
        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    /// <summary>The size of <paramref name="text"/> laid out as <see cref="Text"/> would draw it.</summary>
    public static Size Measure(string text, double size, double width = 0, int lines = 1, Typeface? typeface = null)
    {
        var formatted = Layout(text, size, Colors.White, width, lines, typeface);
        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }
}
