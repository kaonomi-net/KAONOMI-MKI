using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using CodeHighlighter = KAONOMI.Chat.Services.CodeHighlighter;
using Timer = Godot.Timer;
using KAONOMI.Chat.Services;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// The colors of /work on the dark EVE page. The tape keeps the colors of the Avalonia chat (dark text on a white page):
/// the dark ones are shown lightened here (the same hue, the lightness turned over), the document itself is unchanged,
/// so that the Word export and the other version keep their look.
/// </summary>
internal static class WorkPalette
{
    /// <summary>The page of the tape: a dark glass a little deeper than the panels (white paper in a light theme).</summary>
    public static Color Page => Eve.Eve.IsLight ? Colors.White : new Color(0.012f, 0.02f, 0.028f, 0.92f);

    /// <summary>The text without a color of its own.</summary>
    public static Color DefaultText => Eve.Eve.IsLight ? new Color(0.13f, 0.15f, 0.16f) : new Color(0.86f, 0.89f, 0.91f);

    /// <summary>The links (a light blue on the dark page, the blue of the Avalonia tape on a light one).</summary>
    public static Color Link => Eve.Eve.IsLight ? Color.FromHtml("#0563C1") : new Color(0.45f, 0.68f, 1f);

    /// <summary>The font of the text without a font of its own (as in the Avalonia tape: a monospace).</summary>
    public static readonly string[] MonoNames = ["Cascadia Mono", "SF Mono", "Menlo", "Consolas", "DejaVu Sans Mono", "monospace"];

    /// <summary>The font of the text of the tape when none is chosen: Arial (the code keeps <see cref="MonoNames"/>).</summary>
    public static readonly string[] TextNames = ["Arial", "Helvetica Neue", "Liberation Sans", "DejaVu Sans", "sans-serif"];

    private static readonly Dictionary<string, Color> Cache = new();

    /// <summary>The color <paramref name="hex"/> (#RRGGBB) shown on the dark page; <paramref name="fallback"/> when null or unreadable.</summary>
    public static Color Adapt(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;
        var key = (Eve.Eve.IsLight ? "L" : "D") + hex;
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        var parsed = Color.FromString(hex.Trim(), new Color(-1, -1, -1));
        var color = parsed.R < 0 ? fallback : Adapt(parsed);
        Cache[key] = color;
        return color;
    }

    /// <summary>A dark color made light (same hue and saturation, lightness turned over, at least 0.62); the light ones are kept.</summary>
    public static Color Adapt(Color color)
    {
        // On a light page the colors of the document are shown as they are.
        if (Eve.Eve.IsLight)
            return color;
        var luminance = 0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B;
        if (luminance >= 0.42f)
            return color;
        var (h, s, l) = ToHsl(color);
        l = Math.Clamp(1 - l, 0.62f, 0.93f);
        return FromHsl(h, s, l, color.A);
    }

    private static (float H, float S, float L) ToHsl(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        var l = (max + min) / 2;
        if (max - min < 1e-5f)
            return (0, 0, l);
        var d = max - min;
        var s = l > 0.5f ? d / (2 - max - min) : d / (max + min);
        float h;
        if (max == c.R)
            h = (c.G - c.B) / d + (c.G < c.B ? 6 : 0);
        else if (max == c.G)
            h = (c.B - c.R) / d + 2;
        else
            h = (c.R - c.G) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(float h, float s, float l, float a)
    {
        if (s < 1e-5f)
            return new Color(l, l, l, a);
        var q = l < 0.5f ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return new Color(Hue(p, q, h + 1f / 3), Hue(p, q, h), Hue(p, q, h - 1f / 3), a);

        static float Hue(float p, float q, float t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1f / 6) return p + (q - p) * 6 * t;
            if (t < 0.5f) return q;
            if (t < 2f / 3) return p + (q - p) * (2f / 3 - t) * 6;
            return p;
        }
    }

    /// <summary>
    /// The code in the syntax coloring of its language (<see cref="CodeHighlighter"/>, the colors made light for the dark
    /// page), as BBCode for a RichTextLabel (a preview).
    /// </summary>
    public static string CodeBbcode(string? language, string code)
    {
        var colors = CodeColors(language, code);
        var builder = new StringBuilder("[code]");
        var i = 0;
        while (i < code.Length)
        {
            var start = i;
            var color = colors[i];
            while (i < code.Length && colors[i] == color && code[i] != '\n')
                i++;
            if (i == start)
            {
                builder.Append('\n');
                i++;
                continue;
            }
            var text = code[start..i].Replace("[", "[lb]");
            builder.Append(color is { } c ? $"[color=#{c.ToHtml(false)}]{text}[/color]" : text);
        }
        return builder.Append("[/code]").ToString();
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
}
