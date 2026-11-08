using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using KAONOMI.Chat.Services;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Work;

/// <summary>
/// The colors of /work on the dark page. The tape keeps the colors of the Avalonia chat (dark text on a white page):
/// the dark ones are shown lightened here (the same hue, the lightness turned over), the document itself is unchanged,
/// so that the Word export and the other version keep their look.
/// </summary>
public static class WorkPalette
{
    /// <summary>The page of the tape: a dark panel a little deeper than the others.</summary>
    public static readonly Color Page = Color.FromRgb(5, 8, 11);

    /// <summary>The text without a color of its own.</summary>
    public static readonly Color DefaultText = Color.FromRgb(219, 227, 232);

    /// <summary>The links (a light blue on the dark page).</summary>
    public static readonly Color Link = Color.FromRgb(115, 173, 255);

    /// <summary>The font of the blocks of code.</summary>
    public static readonly FontFamily Mono = new("Cascadia Mono, SF Mono, Menlo, Consolas, DejaVu Sans Mono, monospace");

    /// <summary>The font of the text of the tape when none is chosen: Arial (the code keeps <see cref="Mono"/>).</summary>
    public const string TextNames = "Arial, Helvetica Neue, Liberation Sans, DejaVu Sans, sans-serif";

    public static readonly FontFamily Text = new(TextNames);

    public static IBrush PageBrush { get; } = new SolidColorBrush(Page);

    private static readonly Dictionary<string, Color> Cache = new();
    private static readonly Dictionary<Color, IBrush> Brushes = new();

    /// <summary>A brush of <paramref name="color"/> (made once).</summary>
    public static IBrush BrushOf(Color color)
    {
        if (!Brushes.TryGetValue(color, out var brush))
            Brushes[color] = brush = new SolidColorBrush(color);
        return brush;
    }

    /// <summary>The color of a brush of the theme.</summary>
    public static Color ColorOf(IBrush brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;

    /// <summary>The color <paramref name="hex"/> (#RRGGBB) shown on the dark page; <paramref name="fallback"/> when null or unreadable.</summary>
    public static Color Adapt(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;
        if (Cache.TryGetValue(hex, out var cached))
            return cached;
        var color = Color.TryParse(hex.Trim(), out var parsed) ? Adapt(parsed) : fallback;
        Cache[hex] = color;
        return color;
    }

    /// <summary>A dark color made light (same hue and saturation, lightness turned over, at least 0.62); the light ones are kept.</summary>
    public static Color Adapt(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        if (luminance >= 0.42)
            return color;
        var (h, s, l) = ToHsl(r, g, b);
        l = Math.Clamp(1 - l, 0.62, 0.93);
        return FromHsl(h, s, l, color.A);
    }

    /// <summary><paramref name="color"/> lightened by <paramref name="amount"/> (0 to 1, towards white).</summary>
    public static Color Lighten(Color color, double amount) => Color.FromArgb(color.A,
        (byte)(color.R + (255 - color.R) * amount), (byte)(color.G + (255 - color.G) * amount), (byte)(color.B + (255 - color.B) * amount));

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-5)
            return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r)
            h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g)
            h = (b - r) / d + 2;
        else
            h = (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l, byte a)
    {
        if (s < 1e-5)
            return Color.FromArgb(a, (byte)(l * 255), (byte)(l * 255), (byte)(l * 255));
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Color.FromArgb(a, (byte)(Hue(p, q, h + 1.0 / 3) * 255), (byte)(Hue(p, q, h) * 255), (byte)(Hue(p, q, h - 1.0 / 3) * 255));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }

    /// <summary>The color of each character of <paramref name="code"/> (null: the default color).</summary>
    public static Color?[] CodeColors(string? language, string code)
    {
        var colors = new Color?[code.Length];
        if (string.IsNullOrEmpty(language) || code.Length == 0)
            return colors;
        IReadOnlyList<CodeSpan> spans;
        try
        {
            spans = CodeHighlighter.Highlight(language, code);
        }
        catch (Exception)
        {
            return colors;
        }
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            var color = Adapt(span.Color, DefaultText);
            for (var k = Math.Max(0, span.Start); k < Math.Min(code.Length, span.Start + span.Length); k++)
                colors[k] = color;
        }
        return colors;
    }

    /// <summary>The code in the syntax coloring of its language, as runs of a text block (the preview of the code window).</summary>
    public static void CodeInlines(Avalonia.Controls.Documents.InlineCollection inlines, string? language, string code)
    {
        inlines.Clear();
        var colors = CodeColors(language, code);
        var i = 0;
        while (i < code.Length)
        {
            var start = i;
            var color = colors[i];
            while (i < code.Length && colors[i] == color)
                i++;
            inlines.Add(new Avalonia.Controls.Documents.Run(code[start..i]) { Foreground = BrushOf(color ?? DefaultText) });
        }
    }
}
