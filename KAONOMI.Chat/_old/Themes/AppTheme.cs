using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using KAONOMI.Chat.Views;

namespace KAONOMI.Chat.Themes;

/// <summary>Themes selectable with the <c>/theme:…</c> chat commands.</summary>
public enum AppThemeKind
{
    /// <summary>Default variant: follows the Windows light/dark setting.</summary>
    Default,

    /// <summary>
    /// Light theme of www.kaonomi.net (the theme used when none is configured): pale grid paper background, white
    /// panels, turquoise accent of the links, gold separators like the underlines of the titles, near-black text.
    /// </summary>
    Kaonomi,

    /// <summary>Fluent dark theme.</summary>
    Black,

    /// <summary>Dark theme with neon magenta/cyan accents and city / circuit background images.</summary>
    Cyberpunk,

    /// <summary>Dark theme with phosphor-green accents, green monospace text and digital-rain background images.</summary>
    Matrix,

    /// <summary>Light, macOS-inspired theme: blue accent, frosted panels, laptop and pastel wallpaper backgrounds.</summary>
    Apple,

    /// <summary>Light, Windows 11-inspired theme: Windows blue accent, Mica-like panels, laptop and blue ribbon backgrounds.</summary>
    Windows,

    /// <summary>Dark, Ubuntu-colored theme: orange accent, aubergine panels, Tux-style penguin and aubergine backgrounds.</summary>
    Linux,

    /// <summary>Sci-fi HUD theme: every window is see-through at 30% (background shown at 70% opacity).</summary>
    Glass,

    /// <summary>Dark Swift-blue theme: Swift logo over the interbank network linking world financial centres.</summary>
    Swift,

    /// <summary>Sci-fi theme built on the picture of Mari, a young robot girl: cyan accent, ivory text, slate panels.</summary>
    Mari,

    /// <summary>J.A.R.V.I.S.-inspired theme (Iron Man): armor picture, red-orange hologram HUD, red accent, gold lines.</summary>
    Jarvis,

    /// <summary>Light theme in the style of Kandinsky: abstract painting on cream canvas, ultramarine accent, ink text.</summary>
    Kandinsky,

    /// <summary>Light theme with the Pepper robot: white studio, LED-blue accent.</summary>
    Pepper,

    /// <summary>Dark theme inspired by HAL 9000 (2001: A Space Odyssey): red lens on black, red accent.</summary>
    Hal9000,

    /// <summary>Steampunk theme, Studio Ghibli-like: illustrated portrait, painted sky with airship and brass gears, copper accent.</summary>
    Steam
}

/// <summary>
/// Applies a theme to every window at once through application-level resources and styles,
/// so windows that are already open and windows opened later are all affected.
/// </summary>
public static class AppTheme
{
    private const string AssetRoot = "avares://KAONOMI.Chat/Assets/Themes/";

    /// <summary>Themes decorated with background images, accent colors and translucent panels.</summary>
    private static readonly Dictionary<AppThemeKind, ImageTheme> ImageThemes = new()
    {
        [AppThemeKind.Kaonomi] = new ImageTheme(
            MainImage: "kaonomi-grid.jpg",
            OtherImage: "kaonomi-paper.jpg",
            // Turquoise of the links of kaonomi.net, darkened so that white text on accent buttons stays readable.
            Accent: ["#1A8FA8", "#2EB7D7", "#6FCFE4", "#A9E3F0", "#157489", "#105A6B", "#0B404C"],
            Chrome: "#E6FFFFFF",
            Bubble: "#F2EEF5FA",
            // Gold of the underlines of the titles and of the logo.
            Separator: "#59B18D1D",
            Foreground: "#212529",
            // Rubik is the font of the site; Inter (bundled) when it is not installed.
            FontFamily: "Rubik, Inter",
            IsLight: true,
            // Light band like the page, clock in the gold of the logo, date in the brown of the Kaonomi figure.
            BandBackground: "#F3F8FC",
            BandClock: "#B18D1D",
            BandDate: "#887255"),

        [AppThemeKind.Cyberpunk] = new ImageTheme(
            MainImage: "cyberpunk-city.jpg",
            OtherImage: "cyberpunk-circuit.jpg",
            Accent: ["#FF2A6D", "#FF5C8E", "#FF8AAE", "#FFB8CE", "#D91E5B", "#B0154A", "#870E38"],
            Chrome: "#B80A0518",
            Bubble: "#A0140A2A",
            Separator: "#8005D9E8"),

        [AppThemeKind.Matrix] = new ImageTheme(
            MainImage: "matrix-rain.jpg",
            OtherImage: "matrix-code.jpg",
            Accent: ["#00C832", "#00FF41", "#5CFF85", "#A8FFBE", "#00A028", "#00781E", "#005014"],
            Chrome: "#C0000A02",
            Bubble: "#B0001A08",
            Separator: "#8000FF41",
            Foreground: "#B8FFC8",
            FontFamily: "Cascadia Mono, Consolas, Menlo, Courier New, monospace"),

        [AppThemeKind.Apple] = new ImageTheme(
            MainImage: "apple-macbook.jpg",
            OtherImage: "apple-wallpaper.jpg",
            Accent: ["#007AFF", "#3395FF", "#66AFFF", "#99CAFF", "#0062CC", "#004A99", "#003166"],
            Chrome: "#C8FFFFFF",
            Bubble: "#E0FFFFFF",
            Separator: "#26000000",
            Foreground: "#1D1D1F",
            // Closest to Apple's San Francisco available on Windows.
            FontFamily: "Segoe UI Variable Text, Segoe UI, Helvetica Neue, Inter",
            IsLight: true),

        [AppThemeKind.Windows] = new ImageTheme(
            MainImage: "windows-laptop.jpg",
            OtherImage: "windows-ribbons.jpg",
            Accent: ["#0067C0", "#2B88D8", "#62A8E8", "#99CCF5", "#005A9E", "#004578", "#003056"],
            Chrome: "#D0F3F3F3",
            Bubble: "#E6FFFFFF",
            Separator: "#1F000000",
            Foreground: "#1B1B1B",
            // Windows 11 system font.
            FontFamily: "Segoe UI Variable Text, Segoe UI, Helvetica Neue",
            IsLight: true),

        [AppThemeKind.Linux] = new ImageTheme(
            MainImage: "linux-tux.jpg",
            OtherImage: "linux-aubergine.jpg",
            Accent: ["#E95420", "#EE7543", "#F29667", "#F7B89A", "#C7461A", "#A13814", "#7A2A0F"],
            Chrome: "#C82C001E",
            Bubble: "#B03D0A2B",
            Separator: "#80E95420",
            // Ubuntu font when installed, Segoe UI otherwise.
            FontFamily: "Ubuntu, Segoe UI, Helvetica Neue"),

        [AppThemeKind.Glass] = new ImageTheme(
            MainImage: "glass-hud.jpg",
            OtherImage: "glass-grid.jpg",
            // Base kept darker than the HUD cyan so white text on accent buttons stays readable.
            Accent: ["#00A6C0", "#00C8E0", "#00E5FF", "#80F2FF", "#008599", "#006373", "#00424D"],
            // 30% transparency = 70% opacity (0xB3) for bars and bubbles, like the background.
            Chrome: "#B3061428",
            Bubble: "#B3102A40",
            Separator: "#8000E5FF",
            Foreground: "#D8F6FF",
            FontFamily: "Bahnschrift, Segoe UI, Avenir Next Condensed, Helvetica Neue",
            BackgroundOpacity: 0.7,
            SeeThrough: true),

        [AppThemeKind.Swift] = new ImageTheme(
            MainImage: "swift-network.jpg",
            OtherImage: "swift-mesh.jpg",
            // Swift logo blue (#00579C) and lighter tints for a dark background.
            Accent: ["#0A6ACB", "#3388DD", "#66A6E8", "#99C4F2", "#00579C", "#00447A", "#003058"],
            Chrome: "#C8001A3A",
            Bubble: "#B0002E5C",
            Separator: "#8078BEFF"),

        [AppThemeKind.Mari] = new ImageTheme(
            MainImage: "mari-face.jpg",
            OtherImage: "mari-bokeh.jpg",
            // Cyan of the chest light; ivory text like the suit.
            Accent: ["#1E9FC0", "#2BB8D9", "#66CFE6", "#A3E3F1", "#17809B", "#116176", "#0B4251"],
            Chrome: "#B8121A22",
            Bubble: "#A81A2630",
            Separator: "#803FC8E8",
            Foreground: "#F2EEE6",
            FontFamily: "Segoe UI Variable Display, Segoe UI, Helvetica Neue"),

        [AppThemeKind.Jarvis] = new ImageTheme(
            MainImage: "jarvis-armor.jpg",
            OtherImage: "jarvis-hud.jpg",
            // Iron Man red for the accent, gold for the lines, warm white text over the slate HUD.
            Accent: ["#C62828", "#E53935", "#EF5350", "#FF8A80", "#A51F1F", "#7F1717", "#5A1010"],
            Chrome: "#C0141E26",
            Bubble: "#B01E2A34",
            Separator: "#80E6B45A",
            Foreground: "#F3E9DC",
            FontFamily: "Bahnschrift, Segoe UI, Avenir Next Condensed, Helvetica Neue"),

        [AppThemeKind.Kandinsky] = new ImageTheme(
            MainImage: "kandinsky-painting.jpg",
            OtherImage: "kandinsky-composition.jpg",
            // Ultramarine accent, cream canvas panels, ink text; Century Gothic for a Bauhaus-like geometric feel.
            Accent: ["#1F4E99", "#3B6BB5", "#6A90CC", "#9DB6E0", "#183E7A", "#122F5C", "#0C1F3D"],
            Chrome: "#D8F1E9D6",
            Bubble: "#E8F6F0E2",
            Separator: "#661E1A16",
            Foreground: "#1E1A16",
            FontFamily: "Century Gothic, Segoe UI, Avenir Next, Futura",
            IsLight: true),

        [AppThemeKind.Pepper] = new ImageTheme(
            MainImage: "pepper-robot.jpg",
            OtherImage: "pepper-studio.jpg",
            // Blue of Pepper's LEDs; white frosted panels over the white studio, near-black text.
            Accent: ["#1A8FD6", "#3DA5E3", "#73BFEC", "#A8D8F4", "#1472AB", "#0F5680", "#0A3A56"],
            Chrome: "#D9FFFFFF",
            Bubble: "#E6F4F6F8",
            Separator: "#26000000",
            Foreground: "#1C1C1E",
            FontFamily: "Segoe UI Variable Display, Segoe UI, Helvetica Neue",
            IsLight: true),

        [AppThemeKind.Hal9000] = new ImageTheme(
            MainImage: "hal9000-eye.jpg",
            OtherImage: "hal9000-console.jpg",
            // HAL's red lens; near-black panels, light grey text.
            Accent: ["#D32F2F", "#E53935", "#EF5350", "#FF8A80", "#B71C1C", "#8E1414", "#600D0D"],
            Chrome: "#C80A0A0C",
            Bubble: "#B0181012",
            Separator: "#80FF3B30",
            Foreground: "#EDE6E6",
            FontFamily: "Segoe UI, Helvetica Neue"),

        [AppThemeKind.Steam] = new ImageTheme(
            MainImage: "steam-portrait.jpg",
            OtherImage: "steam-sky.jpg",
            // Copper of the boiler tank, brass lines, cream text in a Victorian-feeling serif.
            Accent: ["#C8582A", "#D9703F", "#E6926A", "#F0B697", "#A84720", "#843818", "#5F2810"],
            Chrome: "#C02A2622",
            Bubble: "#B8342C24",
            Separator: "#80C9A063",
            Foreground: "#F2E6D0",
            FontFamily: "Constantia, Georgia"),
    };

    /// <summary>Style and resources added by the active image theme, removed when the theme changes.</summary>
    private static (Styles Styles, ResourceDictionary Resources)? _active;

    private static readonly Dictionary<(string File, double Opacity), IBrush> ImageCache = [];

    /// <summary>Short description of every theme, in declaration order (used by <c>/getthemes</c>).</summary>
    public static IReadOnlyList<(AppThemeKind Kind, string Description)> All { get; } =
    [
        (AppThemeKind.Default, "Fluent theme, follows the Windows light/dark setting"),
        (AppThemeKind.Kaonomi, "Light, the style of www.kaonomi.net (default theme)"),
        (AppThemeKind.Black, "Dark theme"),
        (AppThemeKind.Cyberpunk, "Neon city, magenta and cyan accents"),
        (AppThemeKind.Matrix, "Green digital rain, monospace text"),
        (AppThemeKind.Apple, "Light, macOS-inspired, laptop on pastel wallpaper"),
        (AppThemeKind.Windows, "Light, Windows 11-inspired, blue ribbons"),
        (AppThemeKind.Linux, "Ubuntu colors, Tux-style penguin"),
        (AppThemeKind.Glass, "Sci-fi HUD, windows 30% see-through"),
        (AppThemeKind.Swift, "Swift logo over the interbank network"),
        (AppThemeKind.Mari, "Mari, the young robot girl, in a sci-fi setting"),
        (AppThemeKind.Jarvis, "J.A.R.V.I.S. / Iron Man hologram HUD"),
        (AppThemeKind.Kandinsky, "Light, abstract painting in the style of Kandinsky"),
        (AppThemeKind.Pepper, "Light, the Pepper robot in a white studio"),
        (AppThemeKind.Hal9000, "Dark, HAL 9000's red lens (2001: A Space Odyssey)"),
        (AppThemeKind.Steam, "Steampunk, Studio Ghibli-like illustration, brass and copper"),
    ];

    /// <summary>Theme currently applied (saved to config.yml on exit).</summary>
    public static AppThemeKind Current { get; private set; } = AppThemeKind.Default;

    /// <summary>Name used by the <c>/theme:&lt;name&gt;</c> command and config.yml: <c>white</c> for the default theme, otherwise the lower-case kind.</summary>
    public static string ToName(AppThemeKind kind) =>
        kind == AppThemeKind.Default ? "white" : kind.ToString().ToLowerInvariant();

    /// <summary>Parses a theme name (case-insensitive; <c>white</c> or <c>default</c> for the default theme).</summary>
    public static bool TryParse(string? name, out AppThemeKind kind)
    {
        kind = AppThemeKind.Default;
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || char.IsDigit(trimmed[0]))
            return false; // Enum.TryParse would also accept numbers.

        if (trimmed.Equals("white", StringComparison.OrdinalIgnoreCase))
            return true;

        return Enum.TryParse(trimmed, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    /// <summary>Switches the whole application to <paramref name="kind"/>.</summary>
    public static void Apply(AppThemeKind kind)
    {
        if (Application.Current is not { } app)
            return;

        Current = kind;

        if (_active is { } active)
        {
            app.Resources.MergedDictionaries.Remove(active.Resources);
            app.Styles.Remove(active.Styles);
            _active = null;
        }

        var imageTheme = ImageThemes.GetValueOrDefault(kind);
        app.RequestedThemeVariant = kind == AppThemeKind.Default ? ThemeVariant.Default
            : imageTheme is { IsLight: true } ? ThemeVariant.Light
            : ThemeVariant.Dark;

        if (imageTheme is { } theme)
        {
            var added = (Build(theme), BuildResources(theme));
            app.Resources.MergedDictionaries.Add(added.Item2);
            app.Styles.Add(added.Item1);
            _active = added;
        }
    }

    /// <summary>Overrides of Fluent resources: application resources are looked up before the FluentTheme ones.</summary>
    private static ResourceDictionary BuildResources(ImageTheme theme)
    {
        string[] accentKeys =
        [
            "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
            "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"
        ];

        var resources = new ResourceDictionary
        {
            // Header / status bars and assistant bubbles become translucent so the background shows through.
            ["SystemControlBackgroundChromeMediumLowBrush"] = Brush(theme.Chrome),
            ["SystemControlBackgroundBaseLowBrush"] = Brush(theme.Bubble),
            ["SystemControlForegroundBaseLowBrush"] = Brush(theme.Separator),
        };
        for (var i = 0; i < accentKeys.Length; i++)
            resources[accentKeys[i]] = Color.Parse(theme.Accent[i]);

        // Kaonomi band of the chat (defaults in Themes/Band.axaml).
        if (theme.BandBackground is { } band)
        {
            resources["BandBackgroundColor"] = Color.Parse(band);
            resources["BandBackgroundBrush"] = Brush(band);
        }
        if (theme.BandClock is { } clock)
            resources["BandClockBrush"] = Brush(clock);
        if (theme.BandDate is { } date)
            resources["BandDateBrush"] = Brush(date);
        return resources;
    }

    /// <summary>Main chat window gets <see cref="ImageTheme.MainImage"/>; every other window (tools, dialogs) the other one.</summary>
    private static Styles Build(ImageTheme theme)
    {
        var allWindows = new Style(x => x.Is<Window>());
        // The other image goes to every window but the chat: two matching styles setting the same property do not
        // reliably resolve in favour of the more specific one, so the main window is left out here.
        var otherWindows = new Style(x => x.Is<Window>().Not(y => y.OfType<MainWindow>()))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Image(theme.OtherImage, theme.BackgroundOpacity)) }
        };
        if (theme.SeeThrough)
        {
            // Lets the desktop show through wherever the (semi-transparent) background lets it.
            allWindows.Setters.Add(new Setter(TopLevel.TransparencyLevelHintProperty,
                new[] { WindowTransparencyLevel.Transparent }));
        }
        if (theme.Foreground is not null)
            allWindows.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, Brush(theme.Foreground)));
        if (theme.FontFamily is not null)
            allWindows.Setters.Add(new Setter(TemplatedControl.FontFamilyProperty, new FontFamily(theme.FontFamily)));

        return
        [
            allWindows,
            otherWindows,
            new Style(x => x.OfType<MainWindow>())
            {
                Setters = { new Setter(TemplatedControl.BackgroundProperty, Image(theme.MainImage, theme.BackgroundOpacity)) }
            }
        ];
    }

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    /// <summary>Full-window image brush, cropped (never stretched) to the window shape; loaded once per opacity.</summary>
    private static IBrush Image(string fileName, double opacity)
    {
        if (ImageCache.TryGetValue((fileName, opacity), out var cached))
            return cached;

        using var stream = AssetLoader.Open(new Uri(AssetRoot + fileName));
        var brush = new ImageBrush(new Bitmap(stream))
        {
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
            Opacity = opacity
        };
        ImageCache[(fileName, opacity)] = brush;
        return brush;
    }

    /// <summary>Visual definition of a theme with background images.</summary>
    /// <param name="MainImage">Background of the main chat window (file in <c>Assets/Themes</c>).</param>
    /// <param name="OtherImage">Background of every other window.</param>
    /// <param name="Accent">Accent colors: base, light 1–3, dark 1–3.</param>
    /// <param name="Chrome">Translucent header / status bar color.</param>
    /// <param name="Bubble">Translucent assistant bubble color.</param>
    /// <param name="Separator">Borders and splitters.</param>
    /// <param name="Foreground">Optional default text color.</param>
    /// <param name="FontFamily">Optional default font.</param>
    /// <param name="IsLight">True for a light theme (Fluent light variant), false for dark.</param>
    /// <param name="BackgroundOpacity">Opacity of the background images (1 = opaque).</param>
    /// <param name="SeeThrough">True to make windows transparent, so the desktop shows through the background.</param>
    /// <param name="BandBackground">Optional background of the Kaonomi band (3D face) of the chat.</param>
    /// <param name="BandClock">Optional color of the time in the band.</param>
    /// <param name="BandDate">Optional color of the date in the band.</param>
    private sealed record ImageTheme(
        string MainImage,
        string OtherImage,
        string[] Accent,
        string Chrome,
        string Bubble,
        string Separator,
        string? Foreground = null,
        string? FontFamily = null,
        bool IsLight = false,
        double BackgroundOpacity = 1.0,
        bool SeeThrough = false,
        string? BandBackground = null,
        string? BandClock = null,
        string? BandDate = null);
}
