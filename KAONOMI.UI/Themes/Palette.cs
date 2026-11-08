using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using KAONOMI.Chat.Services;

namespace KAONOMI.UI.Themes;

/// <summary>
/// A theme of the desk, as in the Godot chat (<c>Scripts/Eve/Eve.cs</c>): its colours, its fonts, the shader of its
/// animated background (the <c>.gdshader</c> of the Godot chat, translated to GLSL by <see cref="GodotShader"/>) and the
/// picture under it when it has one.
/// </summary>
public sealed record Palette(
    string Name,
    string Description,
    bool IsLight,
    Color Space, Color Panel, Color PanelSolid, Color Header, Color Field,
    Color Edge, Color EdgeBright, Color Text, Color TextBright, Color Muted,
    Color Accent, Color Warm, Color Red, Color Green,
    string[] BodyFonts, string[] DisplayFonts, int DisplayWeight,
    string Background,
    string? Picture = null)
{
    /// <summary>The file of the shader of the background (Shaders next to the program).</summary>
    public string ShaderPath => Path.Combine(AppContext.BaseDirectory, "Shaders", Background);

    /// <summary>The file of the picture under the shader (Assets next to the program), null without one.</summary>
    public string? PicturePath => Picture is null ? null : Path.Combine(AppContext.BaseDirectory, "Assets", Picture);

    /// <summary>A colour of the Godot chat: its channels from 0 to 1.</summary>
    private static Color C(float r, float g, float b, float a = 1) =>
        Color.FromArgb((byte)Math.Round(a * 255), (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

    /// <summary>A colour of the Godot chat written #RRGGBB or #RRGGBBAA (the alpha last, as in Godot).</summary>
    private static Color H(string html)
    {
        var hex = html.TrimStart('#');
        var alpha = hex.Length == 8 ? Convert.ToByte(hex[6..], 16) : (byte)255;
        return Color.FromArgb(alpha, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    public static readonly Palette EveDark = new(
        "EVE", "Dark · deep space and translucent panels, in the style of EVE Online", false,
        Space: C(0.012f, 0.018f, 0.026f),
        Panel: C(0.035f, 0.045f, 0.055f, 0.82f),
        PanelSolid: C(0.045f, 0.055f, 0.066f, 0.97f),
        Header: C(0.09f, 0.105f, 0.12f, 0.95f),
        Field: C(0.0f, 0.0f, 0.0f, 0.35f),
        Edge: C(0.36f, 0.42f, 0.47f, 0.55f),
        EdgeBright: C(0.62f, 0.69f, 0.74f, 0.85f),
        Text: C(0.82f, 0.85f, 0.87f),
        TextBright: C(0.94f, 0.96f, 0.97f),
        Muted: C(0.55f, 0.59f, 0.63f),
        Accent: C(0.36f, 0.76f, 0.88f),
        Warm: C(0.93f, 0.64f, 0.22f),
        Red: C(0.85f, 0.29f, 0.24f),
        Green: C(0.40f, 0.74f, 0.42f),
        BodyFonts: ["Eurostile", "Bahnschrift", "Helvetica Neue", "Segoe UI", "Arial"],
        DisplayFonts: ["Eurostile", "Bahnschrift", "Avenir Next Condensed", "Helvetica Neue", "Arial"],
        DisplayWeight: 600,
        Background: "eve_space.gdshader");

    public static readonly Palette Kaonomi = new(
        "Kaonomi", "Light · white paper, turquoise and gold, in the style of www.kaonomi.net", true,
        Space: H("#F3F8FC"),
        Panel: H("#FFFFFFFA"),
        PanelSolid: H("#FFFFFF"),
        Header: H("#F4F6F8"),
        Field: H("#FFFFFF"),
        Edge: H("#CBD3DA"),
        EdgeBright: H("#7D8B96"),
        Text: H("#212529"),
        TextBright: H("#0B0D0F"),
        Muted: H("#6C757D"),
        Accent: H("#1A8FA8"),
        Warm: H("#B18D1D"),
        Red: H("#C0392B"),
        Green: H("#2E8B57"),
        BodyFonts: ["Rubik", "Inter", "Helvetica Neue", "Segoe UI", "Arial"],
        DisplayFonts: ["Rubik", "Inter", "Helvetica Neue", "Arial"],
        DisplayWeight: 700,
        Background: "kaonomi_paper.gdshader");

    public static readonly Palette Matrix = new(
        "Matrix", "Dark · the digital rain, phosphor green on black and the monospace of a terminal, in the style of the film", false,
        Space: C(0.0f, 0.016f, 0.004f),
        Panel: C(0.0f, 0.04f, 0.012f, 0.92f),
        PanelSolid: C(0.004f, 0.05f, 0.016f, 0.97f),
        Header: C(0.01f, 0.09f, 0.03f, 0.95f),
        Field: C(0.0f, 0.0f, 0.0f, 0.45f),
        Edge: C(0.0f, 0.55f, 0.18f, 0.5f),
        EdgeBright: C(0.2f, 0.85f, 0.35f, 0.85f),
        Text: C(0.72f, 0.95f, 0.75f),
        TextBright: C(0.85f, 1.0f, 0.88f),
        Muted: C(0.40f, 0.70f, 0.45f),
        Accent: C(0.0f, 1.0f, 0.25f),
        Warm: C(0.75f, 0.95f, 0.2f),
        Red: C(0.95f, 0.3f, 0.25f),
        Green: C(0.2f, 0.9f, 0.35f),
        BodyFonts: ["Menlo", "SF Mono", "Consolas", "Cascadia Mono", "DejaVu Sans Mono", "Courier New"],
        DisplayFonts: ["Menlo", "SF Mono", "Consolas", "Cascadia Mono", "Courier New"],
        DisplayWeight: 700,
        Background: "matrix_rain.gdshader");

    public static readonly Palette Ghost = new(
        "Ghost", "Dark · Niihama at night from a rooftop, cyan and magenta neons in the rain, after Ghost in the Shell", false,
        Space: C(0.03f, 0.08f, 0.13f),
        Panel: C(0.03f, 0.085f, 0.135f, 0.86f),
        PanelSolid: C(0.04f, 0.1f, 0.155f, 0.97f),
        Header: C(0.06f, 0.16f, 0.22f, 0.95f),
        Field: C(0.0f, 0.02f, 0.045f, 0.5f),
        Edge: C(0.2f, 0.6f, 0.65f, 0.5f),
        EdgeBright: C(0.45f, 0.9f, 0.95f, 0.85f),
        Text: C(0.8f, 0.9f, 0.92f),
        TextBright: C(0.93f, 0.98f, 1.0f),
        Muted: C(0.48f, 0.65f, 0.70f),
        Accent: C(0.18f, 0.88f, 0.85f),
        Warm: C(1.0f, 0.31f, 0.64f),
        Red: C(0.98f, 0.3f, 0.4f),
        Green: C(0.3f, 0.9f, 0.6f),
        BodyFonts: ["Helvetica Neue", "Inter", "Segoe UI", "Arial"],
        DisplayFonts: ["Avenir Next Condensed", "Bahnschrift", "Eurostile", "Helvetica Neue", "Arial"],
        DisplayWeight: 700,
        Background: "ghost_city.gdshader",
        Picture: "ghost-city.jpg");

    public static readonly Palette Analyst = new(
        "Analyst", "Dark · the desk of an intelligence analyst: low contrast slate, steel blue and muted amber, made for long hours", false,
        Space: C(0.11f, 0.13f, 0.16f),
        Panel: C(0.15f, 0.175f, 0.21f, 0.92f),
        PanelSolid: C(0.16f, 0.185f, 0.22f, 0.98f),
        Header: C(0.2f, 0.23f, 0.27f, 0.97f),
        Field: C(0.09f, 0.105f, 0.13f, 0.75f),
        Edge: C(0.3f, 0.35f, 0.41f, 0.6f),
        EdgeBright: C(0.48f, 0.55f, 0.63f, 0.85f),
        Text: C(0.87f, 0.88f, 0.90f),
        TextBright: C(0.9f, 0.91f, 0.92f),
        Muted: C(0.68f, 0.71f, 0.75f),
        Accent: C(0.5f, 0.7f, 0.84f),
        Warm: C(0.85f, 0.64f, 0.28f),
        Red: C(0.82f, 0.43f, 0.39f),
        Green: C(0.5f, 0.71f, 0.54f),
        BodyFonts: ["Inter", "Helvetica Neue", "Segoe UI", "Arial"],
        DisplayFonts: ["Inter", "Helvetica Neue", "Segoe UI", "Arial"],
        DisplayWeight: 600,
        Background: "analyst_desk.gdshader");

    public static readonly Palette Minimal = new(
        "Minimal", "Light · white and light greys, anthracite text and a single black accent, nothing more", true,
        Space: H("#FAFAF9"),
        Panel: H("#FFFFFFF7"),
        PanelSolid: H("#FFFFFF"),
        Header: H("#FAFAFA"),
        Field: H("#F6F6F5"),
        Edge: H("#E4E4E2"),
        EdgeBright: H("#B9B9B6"),
        Text: H("#3A3A3A"),
        TextBright: H("#141414"),
        Muted: H("#8C8C8A"),
        Accent: H("#1A1A1A"),
        Warm: H("#B07A2A"),
        Red: H("#C2483D"),
        Green: H("#3F8A5C"),
        BodyFonts: ["Helvetica Neue", "Inter", "SF Pro Text", "Segoe UI", "Arial"],
        DisplayFonts: ["Helvetica Neue", "Inter", "SF Pro Display", "Segoe UI", "Arial"],
        DisplayWeight: 500,
        Background: "minimal_paper.gdshader");

    public static readonly Palette Defense = new(
        "Defense", "Dark · a command post: olive and khaki, sand text, tactical green and signal amber on a topographic map", false,
        Space: C(0.075f, 0.085f, 0.06f),
        Panel: C(0.1f, 0.115f, 0.08f, 0.88f),
        PanelSolid: C(0.115f, 0.13f, 0.09f, 0.97f),
        Header: C(0.17f, 0.19f, 0.13f, 0.96f),
        Field: C(0.04f, 0.045f, 0.03f, 0.6f),
        Edge: C(0.42f, 0.45f, 0.3f, 0.55f),
        EdgeBright: C(0.66f, 0.68f, 0.5f, 0.85f),
        Text: C(0.89f, 0.88f, 0.76f),
        TextBright: C(0.95f, 0.93f, 0.82f),
        Muted: C(0.66f, 0.65f, 0.53f),
        Accent: C(0.58f, 0.78f, 0.38f),
        Warm: C(0.95f, 0.68f, 0.22f),
        Red: C(0.86f, 0.33f, 0.26f),
        Green: C(0.48f, 0.74f, 0.42f),
        BodyFonts: ["DIN Alternate", "Bahnschrift", "Helvetica Neue", "Segoe UI", "Arial"],
        DisplayFonts: ["DIN Condensed", "Bahnschrift Condensed", "Avenir Next Condensed", "Arial Narrow", "Arial"],
        DisplayWeight: 700,
        Background: "defense_map.gdshader");

    public static readonly Palette Police = new(
        "Police", "Dark · a dispatch center at night: navy blue, police blue and badge gold, patrols and incidents on the city map", false,
        Space: C(0.035f, 0.06f, 0.12f),
        Panel: C(0.05f, 0.08f, 0.15f, 0.88f),
        PanelSolid: C(0.06f, 0.09f, 0.17f, 0.97f),
        Header: C(0.08f, 0.13f, 0.24f, 0.96f),
        Field: C(0.01f, 0.02f, 0.05f, 0.6f),
        Edge: C(0.3f, 0.4f, 0.6f, 0.55f),
        EdgeBright: C(0.55f, 0.66f, 0.85f, 0.85f),
        Text: C(0.86f, 0.89f, 0.94f),
        TextBright: C(0.97f, 0.98f, 1.0f),
        Muted: C(0.54f, 0.60f, 0.71f),
        Accent: C(0.32f, 0.58f, 1.0f),
        Warm: C(0.9f, 0.74f, 0.32f),
        Red: C(0.94f, 0.26f, 0.3f),
        Green: C(0.36f, 0.78f, 0.52f),
        BodyFonts: ["Helvetica Neue", "Inter", "Segoe UI", "Arial"],
        DisplayFonts: ["Avenir Next Condensed", "Futura", "Bahnschrift", "Arial Narrow", "Arial"],
        DisplayWeight: 800,
        Background: "police_dispatch.gdshader");

    public static readonly Palette Trading = new(
        "Trading", "Dark · a trading floor of crypto and stocks: graphite, exchange yellow, green rises and red falls, candlesticks scrolling", false,
        Space: H("#0B0E11"),
        Panel: H("#161A1EE6"),
        PanelSolid: H("#181A20F8"),
        Header: H("#1E2329F5"),
        Field: H("#0B0E11B3"),
        Edge: H("#2B3139C0"),
        EdgeBright: H("#474D57"),
        Text: H("#EAECEF"),
        TextBright: H("#FFFFFF"),
        Muted: H("#949CA8"),
        Accent: H("#F0B90B"),
        Warm: H("#F7931A"),
        Red: H("#F6465D"),
        Green: H("#0ECB81"),
        BodyFonts: ["Inter", "IBM Plex Sans", "Helvetica Neue", "Segoe UI", "Arial"],
        DisplayFonts: ["DIN Alternate", "Bahnschrift", "Helvetica Neue", "Arial"],
        DisplayWeight: 700,
        Background: "trading_floor.gdshader");
}

/// <summary>
/// The themes of the desk and the one in use. A theme is applied at once, without building the interface again: the
/// brushes of <c>Styles/Kaonomi.axaml</c>, which every view uses (<c>Ui.Accent</c>, <c>Ui.Panel</c>…), are recoloured in
/// place, the fonts follow the window, the Fluent controls go light or dark, and the background shader is replaced. The
/// choice is kept in <c>~/kaonomi/ui-theme.txt</c> (the Godot chat keeps its own).
/// </summary>
public static class DeskTheme
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "kaonomi", "ui-theme.txt");

    /// <summary>The themes, in the order of the Godot chat.</summary>
    public static IReadOnlyList<Palette> All { get; } =
        [Palette.EveDark, Palette.Kaonomi, Palette.Matrix, Palette.Ghost, Palette.Analyst, Palette.Minimal, Palette.Defense, Palette.Police, Palette.Trading];

    /// <summary>The theme in use.</summary>
    public static Palette Current { get; private set; } = Palette.EveDark;

    /// <summary>Raised once a theme is applied.</summary>
    public static event Action<Palette>? Changed;

    /// <summary>The theme named <paramref name="name"/> (EVE for none, "eve", "default" or "white" of the old names).</summary>
    public static Palette Find(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Palette.EveDark;

    /// <summary>The theme chosen last (EVE the first time).</summary>
    public static Palette Saved()
    {
        try
        {
            return File.Exists(FilePath) ? Find(File.ReadAllText(FilePath)) : Palette.EveDark;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Palette.EveDark;
        }
    }

    /// <summary>Applies <paramref name="palette"/> to the whole interface; <paramref name="save"/>: kept for the next start.</summary>
    public static void Apply(Palette palette, bool save = true)
    {
        if (Application.Current is not { } app)
            return;
        Current = palette;
        foreach (var (key, color) in new[]
                 {
                     ("Space", palette.Space), ("Panel", palette.Panel), ("Header", palette.Header), ("Field", palette.Field),
                     ("Edge", palette.Edge), ("Text", palette.Text), ("TextBright", palette.TextBright), ("Muted", palette.Muted),
                     ("Accent", palette.Accent), ("Warm", palette.Warm), ("Red", palette.Red), ("Green", palette.Green)
                 })
        {
            // The brush every view holds is recoloured: what is shown changes at once.
            if (app.TryGetResource(key, null, out var value) && value is SolidColorBrush brush)
                brush.Color = color;
            else
                app.Resources[key] = new SolidColorBrush(color);
        }
        app.Resources["PanelSolid"] = new SolidColorBrush(palette.PanelSolid);
        app.Resources["EdgeBright"] = new SolidColorBrush(palette.EdgeBright);
        // The accent of the Fluent controls (selections, check boxes, sliders, focus).
        app.Resources["SystemAccentColor"] = palette.Accent;
        app.Resources["SystemAccentColorDark1"] = Shade(palette.Accent, -0.15);
        app.Resources["SystemAccentColorDark2"] = Shade(palette.Accent, -0.3);
        app.Resources["SystemAccentColorDark3"] = Shade(palette.Accent, -0.45);
        app.Resources["SystemAccentColorLight1"] = Shade(palette.Accent, 0.15);
        app.Resources["SystemAccentColorLight2"] = Shade(palette.Accent, 0.3);
        app.Resources["SystemAccentColorLight3"] = Shade(palette.Accent, 0.45);
        // On the accent: dark text on a light accent, white on a dark one.
        app.Resources["OnAccent"] = new SolidColorBrush(Luminance(palette.Accent) > 0.5 ? Color.FromRgb(5, 8, 11) : Colors.White);
        app.Resources["BodyFont"] = new FontFamily(string.Join(", ", palette.BodyFonts));
        app.Resources["DisplayFont"] = new FontFamily(string.Join(", ", palette.DisplayFonts));
        app.Resources["DisplayWeight"] = (FontWeight)palette.DisplayWeight;
        app.RequestedThemeVariant = palette.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        if (save)
            Save(palette);
        Changed?.Invoke(palette);
    }

    private static void Save(Palette palette)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, palette.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Theme", $"The theme was not saved: {ex.Message}");
        }
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    /// <summary><paramref name="color"/> lighter (amount above 0) or darker (below 0).</summary>
    private static Color Shade(Color color, double amount)
    {
        byte Channel(byte v) => (byte)Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromRgb(Channel(color.R), Channel(color.G), Channel(color.B));
    }
}
