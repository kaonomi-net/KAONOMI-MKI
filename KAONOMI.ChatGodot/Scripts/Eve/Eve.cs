using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The look of the chat, in the style of the interface of EVE Online: deep space behind dark translucent panels with
/// thin grey edges, small spaced capitals for the headers, a cold cyan for what is active and selected, amber for what
/// needs attention, flat buttons with a hairline edge. The fonts are the ones of the system (no file to ship).
/// </summary>
public static class Eve
{
    // ── The theme ──

    /// <summary>The themes of the chat: EVE (dark, the default), Kaonomi (light, in the style of www.kaonomi.net), Matrix (the green of the film) and Ghost (Ghost in the Shell).</summary>
    public static IReadOnlyList<Palette> Palettes { get; } = [Palette.EveDark, Palette.Kaonomi, Palette.Matrix, Palette.Ghost, Palette.Analyst, Palette.Minimal, Palette.Defense, Palette.Police, Palette.Trading];

    /// <summary>The theme in use.</summary>
    public static Palette Current { get; private set; } = Palette.EveDark;

    /// <summary>Makes <paramref name="palette"/> the theme in use (the interface is then built again by the shell).</summary>
    public static void Use(Palette palette)
    {
        Current = palette;
        _shared = null;
        _body = null;
        _display = null;
    }

    /// <summary>The theme named <paramref name="name"/> (EVE when there is none).</summary>
    public static Palette Find(string? name) =>
        Palettes.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Palette.EveDark;

    // ── Colors (of the theme in use) ──
    public static Color Space => Current.Space;
    public static Color Panel => Current.Panel;
    public static Color PanelSolid => Current.PanelSolid;
    public static Color Header => Current.Header;
    public static Color Field => Current.Field;
    public static Color Edge => Current.Edge;
    public static Color EdgeBright => Current.EdgeBright;
    public static Color Text => Current.Text;
    public static Color TextBright => Current.TextBright;
    public static Color Muted => Current.Muted;
    /// <summary>The accent: what is active and selected (cyan in EVE, the turquoise of kaonomi.net).</summary>
    public static Color Cyan => Current.Accent;
    /// <summary>What needs attention (amber in EVE, the gold of the logo of kaonomi.net).</summary>
    public static Color Amber => Current.Warm;
    public static Color Red => Current.Red;
    public static Color Green => Current.Green;
    /// <summary>True for a light theme (dark text on light panels).</summary>
    public static bool IsLight => Current.IsLight;

    /// <summary>
    /// The strongest text, for what is hovered, pressed or selected: white in a dark theme, the darkest text in a light
    /// one (white there would vanish on the light panels).
    /// </summary>
    public static Color Ink => IsLight ? TextBright : Colors.White;

    /// <summary>A sunken shade (a field, a bar, a list) of strength <paramref name="alpha"/>: black glass in a dark theme, a faint grey in a light one.</summary>
    public static Color Sunken(float alpha) => IsLight ? new Color(0.11f, 0.15f, 0.2f, alpha * 0.22f) : new Color(0, 0, 0, alpha);

    /// <summary>A raised shade (a button, a hovered row) of strength <paramref name="alpha"/>: white glass in a dark theme, a faint grey in a light one.</summary>
    public static Color Raised(float alpha) => IsLight ? new Color(0, 0, 0, alpha * 0.8f) : new Color(1, 1, 1, alpha);

    // ── Fonts ──

    /// <summary>The emoji of the icons and of the texts (the colour font of the system).</summary>
    public static readonly SystemFont Emoji = new()
    {
        FontNames = ["Apple Color Emoji", "Segoe UI Emoji", "Noto Color Emoji", "Segoe UI Symbol"]
    };

    private static SystemFont? _body;
    private static FontVariation? _display;

    /// <summary>The text: the font of the theme, of the system (the emoji from <see cref="Emoji"/>).</summary>
    public static SystemFont Body => _body ??= new SystemFont
    {
        FontNames = Current.BodyFonts,
        FontWeight = 400,
        Antialiasing = TextServer.FontAntialiasing.Gray,
        Hinting = TextServer.Hinting.Light,
        Fallbacks = [Emoji]
    };

    /// <summary>The headers: the display font of the theme, semi-bold, spaced.</summary>
    public static FontVariation Display => _display ??= new FontVariation
    {
        BaseFont = new SystemFont { FontNames = Current.DisplayFonts, FontWeight = Current.DisplayWeight, Fallbacks = [Emoji] },
        SpacingGlyph = 2
    };

    /// <summary>Monospace font of the code and of the numbers.</summary>
    public static readonly SystemFont Mono = new()
    {
        FontNames = ["SF Mono", "Menlo", "Cascadia Mono", "Consolas", "DejaVu Sans Mono", "monospace"],
        Antialiasing = TextServer.FontAntialiasing.Lcd
    };

    // ── Boxes ──

    /// <summary>A flat box of <paramref name="back"/> with a hairline edge of <paramref name="edge"/>.</summary>
    public static StyleBoxFlat Box(Color back, Color? edge = null, int margin = 8, int border = 1)
    {
        var box = new StyleBoxFlat { BgColor = back, BorderColor = edge ?? Edge, AntiAliasing = false };
        box.SetBorderWidthAll(edge is null && border == 1 ? 1 : border);
        box.SetContentMarginAll(margin);
        return box;
    }

    /// <summary>The panel of a window: dark glass, a hairline edge, a faint light along its top.</summary>
    public static StyleBoxFlat WindowBox(bool active)
    {
        var box = Box(Panel, active ? EdgeBright with { A = 0.7f } : Edge, 0);
        box.BorderWidthTop = 1;
        box.ShadowColor = new Color(0, 0, 0, IsLight ? 0.18f : 0.55f);
        box.ShadowSize = 18;
        return box;
    }

    /// <summary>A label of the header of a section: small spaced capitals, muted.</summary>
    public static Label Caption(string text, Color? color = null, int size = 11)
    {
        var label = new Label { Text = text.ToUpperInvariant() };
        label.AddThemeFontOverride("font", Display);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? Muted);
        return label;
    }

    /// <summary>A plain label.</summary>
    public static Label Text_(string text, Color? color = null, int size = 13, bool wrap = false)
    {
        var label = new Label { Text = text };
        if (wrap)
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c)
            label.AddThemeColorOverride("font_color", c);
        return label;
    }

    /// <summary>A flat button; <paramref name="primary"/> gives it the cyan of the main action.</summary>
    public static Button Button(string text, bool primary = false, string? tooltip = null)
    {
        var button = new Button { Text = text, TooltipText = tooltip ?? string.Empty, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        if (primary)
            StylePrimary(button);
        return button;
    }

    /// <summary>The cyan of the main action on <paramref name="button"/>.</summary>
    public static void StylePrimary(BaseButton button)
    {
        button.AddThemeStyleboxOverride("normal", Box(Cyan with { A = 0.18f }, Cyan with { A = 0.75f }, 6));
        button.AddThemeStyleboxOverride("hover", Box(Cyan with { A = 0.32f }, Cyan, 6));
        button.AddThemeStyleboxOverride("pressed", Box(Cyan with { A = 0.5f }, Cyan, 6));
        button.AddThemeColorOverride("font_color", TextBright);
        button.AddThemeColorOverride("font_focus_color", TextBright);
        button.AddThemeColorOverride("font_hover_color", Ink);
        button.AddThemeColorOverride("font_pressed_color", Ink);
        button.AddThemeColorOverride("font_hover_pressed_color", Ink);
    }

    /// <summary>A thin horizontal line between sections.</summary>
    public static Control Separator()
    {
        var line = new ColorRect { Color = Edge with { A = 0.35f }, CustomMinimumSize = new Vector2(0, 1), MouseFilter = Control.MouseFilterEnum.Ignore };
        return line;
    }

    private static Theme? _shared;

    /// <summary>The theme of the chat, built once: the window and every window of the desktop (their viewports do not inherit it).</summary>
    public static Theme Shared => _shared ??= Build();

    /// <summary>
    /// The theme of every control: dark fields with hairline edges, flat grey buttons lit in cyan under the pointer,
    /// thin scroll bars, cyan selections, small text.
    /// </summary>
    public static Theme Build()
    {
        var theme = new Theme { DefaultFont = Body, DefaultFontSize = 13 };
        theme.SetColor("font_color", "Label", Text);
        theme.SetFont("mono_font", "RichTextLabel", Mono);
        theme.SetFont("bold_font", "RichTextLabel", new SystemFont { FontNames = Body.FontNames, FontWeight = 700 });
        theme.SetColor("default_color", "RichTextLabel", Text);
        theme.SetColor("selection_color", "RichTextLabel", Cyan with { A = 0.3f });

        // Buttons.
        theme.SetStylebox("normal", "Button", Box(Raised(0.04f), Edge, 6));
        theme.SetStylebox("hover", "Button", Box(Cyan with { A = 0.12f }, Cyan with { A = 0.8f }, 6));
        theme.SetStylebox("pressed", "Button", Box(Cyan with { A = 0.28f }, Cyan, 6));
        theme.SetStylebox("disabled", "Button", Box(Raised(0.015f), Edge with { A = 0.25f }, 6));
        theme.SetStylebox("focus", "Button", new StyleBoxEmpty());
        theme.SetColor("font_color", "Button", Text);
        // The button that has the focus (the OK of a dialog when it opens): without this, the light grey of Godot.
        theme.SetColor("font_focus_color", "Button", Text);
        theme.SetColor("font_hover_color", "Button", TextBright);
        theme.SetColor("font_pressed_color", "Button", Ink);
        theme.SetColor("font_hover_pressed_color", "Button", Ink);
        theme.SetColor("font_disabled_color", "Button", Muted with { A = 0.5f });
        theme.SetColor("icon_normal_color", "Button", Text);
        theme.SetColor("icon_hover_color", "Button", Cyan);

        // Fields.
        var field = Box(Field, Edge with { A = 0.45f }, 6);
        var focus = Box(Field, Cyan with { A = 0.8f }, 6);
        foreach (var type in new[] { "LineEdit", "TextEdit", "CodeEdit" })
        {
            theme.SetStylebox("normal", type, field);
            theme.SetStylebox("focus", type, focus);
            theme.SetStylebox("read_only", type, field);
            theme.SetColor("font_color", type, TextBright);
            theme.SetColor("font_placeholder_color", type, Muted with { A = 0.8f });
            theme.SetColor("caret_color", type, Cyan);
            theme.SetColor("selection_color", type, Cyan with { A = 0.3f });
            theme.SetColor("font_readonly_color", type, Text);
            theme.SetColor("background_color", type, new Color(0, 0, 0, 0));
        }

        // Drop-downs and menus.
        foreach (var state in new[] { "normal", "focus" })
            theme.SetStylebox(state, "OptionButton", Box(Field, Edge with { A = 0.5f }, 6));
        theme.SetStylebox("hover", "OptionButton", Box(Cyan with { A = 0.1f }, Cyan with { A = 0.8f }, 6));
        theme.SetStylebox("pressed", "OptionButton", Box(Cyan with { A = 0.2f }, Cyan, 6));
        theme.SetColor("font_color", "OptionButton", TextBright);
        theme.SetColor("font_focus_color", "OptionButton", TextBright);
        theme.SetColor("font_hover_color", "OptionButton", Ink);
        theme.SetColor("font_pressed_color", "OptionButton", Ink);
        theme.SetColor("font_hover_pressed_color", "OptionButton", Ink);
        theme.SetStylebox("panel", "PopupMenu", Box(PanelSolid, EdgeBright with { A = 0.6f }, 4));
        theme.SetStylebox("hover", "PopupMenu", Box(Cyan with { A = 0.22f }, Cyan with { A = 0f }, 2, 0));
        theme.SetColor("font_color", "PopupMenu", Text);
        theme.SetColor("font_hover_color", "PopupMenu", Ink);
        theme.SetColor("font_separator_color", "PopupMenu", Muted);
        theme.SetStylebox("panel", "PopupPanel", Box(PanelSolid, EdgeBright with { A = 0.6f }, 6));

        // Lists and trees.
        var list = Box(Field, Edge with { A = 0.35f }, 4);
        var selected = Box(Cyan with { A = 0.2f }, Cyan with { A = 0f }, 2, 0);
        selected.BorderWidthLeft = 2;
        selected.BorderColor = Cyan;
        foreach (var type in new[] { "ItemList", "Tree" })
        {
            theme.SetStylebox("panel", type, list);
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
            theme.SetStylebox("selected", type, selected);
            theme.SetStylebox("selected_focus", type, selected);
            theme.SetStylebox("cursor", type, new StyleBoxEmpty());
            theme.SetStylebox("cursor_unfocused", type, new StyleBoxEmpty());
            theme.SetStylebox("hovered", type, Box(Raised(0.04f), new Color(0, 0, 0, 0), 2, 0));
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_selected_color", type, Ink);
            theme.SetColor("font_hovered_color", type, TextBright);
            // A selected item under the pointer: without these, Godot's own white text and box (invisible in a light theme).
            theme.SetColor("font_hovered_selected_color", type, Ink);
            theme.SetStylebox("hovered_selected", type, selected);
            theme.SetStylebox("hovered_selected_focus", type, selected);
            theme.SetColor("guide_color", type, Edge with { A = 0.25f });
        }
        theme.SetColor("relationship_line_color", "Tree", Edge with { A = 0.4f });
        theme.SetStylebox("title_button_normal", "Tree", Box(Header, Edge with { A = 0.4f }, 4));

        // Tabs.
        var tab = Box(Raised(0.03f), Edge with { A = 0.3f }, 6);
        tab.ContentMarginLeft = tab.ContentMarginRight = 12;
        var tabSelected = Box(Cyan with { A = 0.14f }, Cyan with { A = 0f }, 6, 0);
        tabSelected.BorderWidthBottom = 2;
        tabSelected.BorderColor = Cyan;
        tabSelected.ContentMarginLeft = tabSelected.ContentMarginRight = 12;
        theme.SetStylebox("tab_unselected", "TabContainer", tab);
        theme.SetStylebox("tab_hovered", "TabContainer", Box(Cyan with { A = 0.08f }, Edge with { A = 0.3f }, 6));
        theme.SetStylebox("tab_selected", "TabContainer", tabSelected);
        theme.SetStylebox("panel", "TabContainer", Box(Sunken(0.15f), Edge with { A = 0.25f }, 8));
        theme.SetColor("font_selected_color", "TabContainer", Ink);
        theme.SetColor("font_unselected_color", "TabContainer", Muted);
        theme.SetColor("font_hovered_color", "TabContainer", TextBright);
        theme.SetFont("font", "TabContainer", Display);
        theme.SetFontSize("font_size", "TabContainer", 11);

        // Check boxes, sliders, progress.
        theme.SetColor("font_color", "CheckBox", Text);
        theme.SetColor("font_hover_color", "CheckBox", TextBright);
        theme.SetColor("font_pressed_color", "CheckBox", TextBright);
        theme.SetColor("font_hover_pressed_color", "CheckBox", TextBright);
        theme.SetColor("font_focus_color", "CheckBox", Text);
        theme.SetColor("font_color", "CheckButton", Text);
        theme.SetColor("font_pressed_color", "CheckButton", TextBright);
        theme.SetColor("font_hover_color", "CheckButton", TextBright);
        theme.SetColor("font_hover_pressed_color", "CheckButton", TextBright);
        theme.SetColor("font_focus_color", "CheckButton", Text);
        foreach (var state in new[] { "normal", "hover", "pressed", "focus", "hover_pressed" })
        {
            theme.SetStylebox(state, "CheckBox", new StyleBoxEmpty());
            theme.SetStylebox(state, "CheckButton", new StyleBoxEmpty());
        }
        var track = new StyleBoxFlat { BgColor = Edge with { A = 0.35f }, ContentMarginTop = 1, ContentMarginBottom = 1 };
        var filled = new StyleBoxFlat { BgColor = Cyan with { A = 0.8f }, ContentMarginTop = 1, ContentMarginBottom = 1 };
        theme.SetStylebox("slider", "HSlider", track);
        theme.SetStylebox("grabber_area", "HSlider", filled);
        theme.SetStylebox("grabber_area_highlight", "HSlider", filled);
        theme.SetStylebox("background", "ProgressBar", Box(Field, Edge with { A = 0.35f }, 0));
        theme.SetStylebox("fill", "ProgressBar", new StyleBoxFlat { BgColor = Cyan with { A = 0.75f } });
        theme.SetColor("font_color", "ProgressBar", TextBright);
        theme.SetFontSize("font_size", "ProgressBar", 10);

        // Scroll bars: a thin grey grabber, cyan when used.
        foreach (var type in new[] { "VScrollBar", "HScrollBar" })
        {
            var grabber = new StyleBoxFlat { BgColor = Edge with { A = 0.6f } };
            grabber.SetCornerRadiusAll(1);
            theme.SetStylebox("grabber", type, grabber);
            theme.SetStylebox("grabber_highlight", type, new StyleBoxFlat { BgColor = EdgeBright });
            theme.SetStylebox("grabber_pressed", type, new StyleBoxFlat { BgColor = Cyan });
            var scroll = new StyleBoxFlat { BgColor = Sunken(0.2f) };
            if (type == "VScrollBar")
                scroll.ContentMarginLeft = scroll.ContentMarginRight = 2;
            else
                scroll.ContentMarginTop = scroll.ContentMarginBottom = 2;
            theme.SetStylebox("scroll", type, scroll);
        }

        // Tooltips, panels, dialogs, split bars.
        theme.SetStylebox("panel", "TooltipPanel", Box(PanelSolid, EdgeBright with { A = 0.6f }, 6));
        theme.SetColor("font_color", "TooltipLabel", Text);
        theme.SetFontSize("font_size", "TooltipLabel", 12);
        theme.SetStylebox("panel", "PanelContainer", Box(Sunken(0.18f), Edge with { A = 0.3f }, 8));
        theme.SetStylebox("panel", "Panel", Box(Sunken(0.18f), Edge with { A = 0.3f }, 0));
        theme.SetStylebox("panel", "AcceptDialog", Box(PanelSolid, EdgeBright with { A = 0.6f }, 10));
        theme.SetStylebox("embedded_border", "Window", Box(PanelSolid, EdgeBright with { A = 0.6f }, 0));
        theme.SetColor("title_color", "Window", TextBright);
        theme.SetFont("title_font", "Window", Display);
        theme.SetConstant("separation", "HSplitContainer", 6);
        theme.SetConstant("separation", "VSplitContainer", 6);
        theme.SetConstant("separation", "VBoxContainer", 6);
        theme.SetConstant("separation", "HBoxContainer", 6);
        return theme;
    }
}

/// <summary>
/// A theme of the chat: its colours, its fonts, the shader of its background and whether it is light.
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
    /// <summary>The file of the picture behind the desk (Assets folder next to the chat), null without one.</summary>
    public string? PicturePath => Picture is null ? null : System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", Picture);

    /// <summary>EVE Online: deep space, dark translucent panels, cold cyan, amber.</summary>
    public static readonly Palette EveDark = new(
        "EVE", "Dark · deep space and translucent panels, in the style of EVE Online", false,
        Space: new(0.012f, 0.018f, 0.026f),
        Panel: new(0.035f, 0.045f, 0.055f, 0.82f),
        PanelSolid: new(0.045f, 0.055f, 0.066f, 0.97f),
        Header: new(0.09f, 0.105f, 0.12f, 0.95f),
        Field: new(0.0f, 0.0f, 0.0f, 0.35f),
        Edge: new(0.36f, 0.42f, 0.47f, 0.55f),
        EdgeBright: new(0.62f, 0.69f, 0.74f, 0.85f),
        Text: new(0.82f, 0.85f, 0.87f),
        TextBright: new(0.94f, 0.96f, 0.97f),
        Muted: new(0.55f, 0.59f, 0.63f),
        Accent: new(0.36f, 0.76f, 0.88f),
        Warm: new(0.93f, 0.64f, 0.22f),
        Red: new(0.85f, 0.29f, 0.24f),
        Green: new(0.40f, 0.74f, 0.42f),
        BodyFonts: ["Eurostile", "Bahnschrift", "Helvetica Neue", "Segoe UI", "Arial", "sans-serif"],
        DisplayFonts: ["Eurostile", "Bahnschrift", "Avenir Next Condensed", "Helvetica Neue", "Arial"],
        DisplayWeight: 600,
        Background: "res://Shaders/eve_space.gdshader");

    /// <summary>
    /// kaonomi.net: a white page with faint networks of hexagons, black titles in spaced capitals, the turquoise of its
    /// links and the gold of its logo, the Rubik font of the site when it is installed.
    /// </summary>
    public static readonly Palette Kaonomi = new(
        "Kaonomi", "Light · white paper, turquoise and gold, in the style of www.kaonomi.net", true,
        Space: Color.FromHtml("#F3F8FC"),
        Panel: Color.FromHtml("#FFFFFFFA"),
        PanelSolid: Color.FromHtml("#FFFFFF"),
        Header: Color.FromHtml("#F4F6F8"),
        Field: Color.FromHtml("#FFFFFF"),
        Edge: Color.FromHtml("#CBD3DA"),
        EdgeBright: Color.FromHtml("#7D8B96"),
        Text: Color.FromHtml("#212529"),
        TextBright: Color.FromHtml("#0B0D0F"),
        Muted: Color.FromHtml("#6C757D"),
        Accent: Color.FromHtml("#1A8FA8"),
        Warm: Color.FromHtml("#B18D1D"),
        Red: Color.FromHtml("#C0392B"),
        Green: Color.FromHtml("#2E8B57"),
        BodyFonts: ["Rubik", "Inter", "Helvetica Neue", "Segoe UI", "Arial", "sans-serif"],
        DisplayFonts: ["Rubik", "Inter", "Helvetica Neue", "Arial"],
        DisplayWeight: 700,
        Background: "res://Shaders/kaonomi_paper.gdshader");

    /// <summary>
    /// The Matrix: the black of the screen with a faint green cast, panels of dark glass, the phosphor green of the
    /// digital rain for what is active, a pale green for the text, the monospace fonts of a terminal; the rain of
    /// glyphs falls behind the desk.
    /// </summary>
    public static readonly Palette Matrix = new(
        "Matrix", "Dark · the digital rain, phosphor green on black and the monospace of a terminal, in the style of the film", false,
        Space: new(0.0f, 0.016f, 0.004f),
        Panel: new(0.0f, 0.04f, 0.012f, 0.92f),
        PanelSolid: new(0.004f, 0.05f, 0.016f, 0.97f),
        Header: new(0.01f, 0.09f, 0.03f, 0.95f),
        Field: new(0.0f, 0.0f, 0.0f, 0.45f),
        Edge: new(0.0f, 0.55f, 0.18f, 0.5f),
        EdgeBright: new(0.2f, 0.85f, 0.35f, 0.85f),
        Text: new(0.72f, 0.95f, 0.75f),
        TextBright: new(0.85f, 1.0f, 0.88f),
        Muted: new(0.40f, 0.70f, 0.45f),
        Accent: new(0.0f, 1.0f, 0.25f),
        Warm: new(0.75f, 0.95f, 0.2f),
        Red: new(0.95f, 0.3f, 0.25f),
        Green: new(0.2f, 0.9f, 0.35f),
        BodyFonts: ["Menlo", "SF Mono", "Consolas", "Cascadia Mono", "DejaVu Sans Mono", "Courier New", "monospace"],
        DisplayFonts: ["Menlo", "SF Mono", "Consolas", "Cascadia Mono", "Courier New", "monospace"],
        DisplayWeight: 700,
        Background: "res://Shaders/matrix_rain.gdshader");

    /// <summary>
    /// Ghost in the Shell: the Major on a rooftop over Niihama at night (the picture behind the desk, with its rain and
    /// its neons drawn over it), the deep blue of that night for the panels, the cyan of its neons for what is active
    /// and their magenta for what calls, condensed capitals for the headers.
    /// </summary>
    public static readonly Palette Ghost = new(
        "Ghost", "Dark · Niihama at night from a rooftop, cyan and magenta neons in the rain, after Ghost in the Shell", false,
        Space: new(0.03f, 0.08f, 0.13f),
        Panel: new(0.03f, 0.085f, 0.135f, 0.86f),
        PanelSolid: new(0.04f, 0.1f, 0.155f, 0.97f),
        Header: new(0.06f, 0.16f, 0.22f, 0.95f),
        Field: new(0.0f, 0.02f, 0.045f, 0.5f),
        Edge: new(0.2f, 0.6f, 0.65f, 0.5f),
        EdgeBright: new(0.45f, 0.9f, 0.95f, 0.85f),
        Text: new(0.8f, 0.9f, 0.92f),
        TextBright: new(0.93f, 0.98f, 1.0f),
        Muted: new(0.48f, 0.65f, 0.70f),
        Accent: new(0.18f, 0.88f, 0.85f),
        Warm: new(1.0f, 0.31f, 0.64f),
        Red: new(0.98f, 0.3f, 0.4f),
        Green: new(0.3f, 0.9f, 0.6f),
        BodyFonts: ["Helvetica Neue", "Inter", "Segoe UI", "Arial", "sans-serif"],
        DisplayFonts: ["Avenir Next Condensed", "Bahnschrift", "Eurostile", "Helvetica Neue", "Arial"],
        DisplayWeight: 700,
        Background: "res://Shaders/ghost_city.gdshader",
        Picture: "ghost-city.jpg");

    /// <summary>
    /// The desk of an intelligence analyst, for hours of work: a dark slate a little blue (no black, no glare), panels a
    /// step lighter, an off-white text that is not pure white, a steel blue for what is active, a muted amber for what
    /// calls, a soft red and a sage green; readable sans-serif fonts at a moderate weight; a still operations wall
    /// behind the desk (a dot grid, a dim world display, a sweep too slow to catch the eye).
    /// </summary>
    public static readonly Palette Analyst = new(
        "Analyst", "Dark · the desk of an intelligence analyst: low contrast slate, steel blue and muted amber, made for long hours", false,
        Space: new(0.11f, 0.13f, 0.16f),
        Panel: new(0.15f, 0.175f, 0.21f, 0.92f),
        PanelSolid: new(0.16f, 0.185f, 0.22f, 0.98f),
        Header: new(0.2f, 0.23f, 0.27f, 0.97f),
        Field: new(0.09f, 0.105f, 0.13f, 0.75f),
        Edge: new(0.3f, 0.35f, 0.41f, 0.6f),
        EdgeBright: new(0.48f, 0.55f, 0.63f, 0.85f),
        Text: new(0.87f, 0.88f, 0.90f),
        TextBright: new(0.9f, 0.91f, 0.92f),
        Muted: new(0.68f, 0.71f, 0.75f),
        Accent: new(0.5f, 0.7f, 0.84f),
        Warm: new(0.85f, 0.64f, 0.28f),
        Red: new(0.82f, 0.43f, 0.39f),
        Green: new(0.5f, 0.71f, 0.54f),
        BodyFonts: ["Inter", "Helvetica Neue", "Segoe UI", "Arial", "sans-serif"],
        DisplayFonts: ["Inter", "Helvetica Neue", "Segoe UI", "Arial"],
        DisplayWeight: 600,
        Background: "res://Shaders/analyst_desk.gdshader");

    /// <summary>
    /// Minimal: an off-white page, white panels separated by hairlines of light grey, an anthracite text, near-black
    /// as the only accent, a soft amber, red and green for the states, a light sans-serif; a plain still background.
    /// </summary>
    public static readonly Palette Minimal = new(
        "Minimal", "Light · white and light greys, anthracite text and a single black accent, nothing more", true,
        Space: Color.FromHtml("#FAFAF9"),
        Panel: Color.FromHtml("#FFFFFFF7"),
        PanelSolid: Color.FromHtml("#FFFFFF"),
        Header: Color.FromHtml("#FAFAFA"),
        Field: Color.FromHtml("#F6F6F5"),
        Edge: Color.FromHtml("#E4E4E2"),
        EdgeBright: Color.FromHtml("#B9B9B6"),
        Text: Color.FromHtml("#3A3A3A"),
        TextBright: Color.FromHtml("#141414"),
        Muted: Color.FromHtml("#8C8C8A"),
        Accent: Color.FromHtml("#1A1A1A"),
        Warm: Color.FromHtml("#B07A2A"),
        Red: Color.FromHtml("#C2483D"),
        Green: Color.FromHtml("#3F8A5C"),
        BodyFonts: ["Helvetica Neue", "Inter", "SF Pro Text", "Segoe UI", "Arial", "sans-serif"],
        DisplayFonts: ["Helvetica Neue", "Inter", "SF Pro Display", "Segoe UI", "Arial"],
        DisplayWeight: 500,
        Background: "res://Shaders/minimal_paper.gdshader");

    /// <summary>
    /// Defense: a command post. Dark olive ground and panels, sand-coloured text, a tactical green for what is active,
    /// the amber of signals for what calls, the red of hostiles, condensed DIN capitals for the headers; the tactical
    /// map behind the desk (contour lines, a grid of map squares, unit symbols, a slow scan).
    /// </summary>
    public static readonly Palette Defense = new(
        "Defense", "Dark · a command post: olive and khaki, sand text, tactical green and signal amber on a topographic map", false,
        Space: new(0.075f, 0.085f, 0.06f),
        Panel: new(0.1f, 0.115f, 0.08f, 0.88f),
        PanelSolid: new(0.115f, 0.13f, 0.09f, 0.97f),
        Header: new(0.17f, 0.19f, 0.13f, 0.96f),
        Field: new(0.04f, 0.045f, 0.03f, 0.6f),
        Edge: new(0.42f, 0.45f, 0.3f, 0.55f),
        EdgeBright: new(0.66f, 0.68f, 0.5f, 0.85f),
        Text: new(0.89f, 0.88f, 0.76f),
        TextBright: new(0.95f, 0.93f, 0.82f),
        Muted: new(0.66f, 0.65f, 0.53f),
        Accent: new(0.58f, 0.78f, 0.38f),
        Warm: new(0.95f, 0.68f, 0.22f),
        Red: new(0.86f, 0.33f, 0.26f),
        Green: new(0.48f, 0.74f, 0.42f),
        BodyFonts: ["DIN Alternate", "Bahnschrift", "Helvetica Neue", "Segoe UI", "Arial", "sans-serif"],
        DisplayFonts: ["DIN Condensed", "Bahnschrift Condensed", "Avenir Next Condensed", "Arial Narrow", "Arial"],
        DisplayWeight: 700,
        Background: "res://Shaders/defense_map.gdshader");

    /// <summary>
    /// Police: a dispatch center at night. Navy blue panels like the uniform, a white text, the blue of the police
    /// for what is active, the gold of the badge for what calls, the red of the siren for alarms, bold condensed
    /// capitals for the headers; the city map of the dispatch behind the desk (patrols, incidents, a soft light bar).
    /// </summary>
    public static readonly Palette Police = new(
        "Police", "Dark · a dispatch center at night: navy blue, police blue and badge gold, patrols and incidents on the city map", false,
        Space: new(0.035f, 0.06f, 0.12f),
        Panel: new(0.05f, 0.08f, 0.15f, 0.88f),
        PanelSolid: new(0.06f, 0.09f, 0.17f, 0.97f),
        Header: new(0.08f, 0.13f, 0.24f, 0.96f),
        Field: new(0.01f, 0.02f, 0.05f, 0.6f),
        Edge: new(0.3f, 0.4f, 0.6f, 0.55f),
        EdgeBright: new(0.55f, 0.66f, 0.85f, 0.85f),
        Text: new(0.86f, 0.89f, 0.94f),
        TextBright: new(0.97f, 0.98f, 1.0f),
        Muted: new(0.54f, 0.60f, 0.71f),
        Accent: new(0.32f, 0.58f, 1.0f),
        Warm: new(0.9f, 0.74f, 0.32f),
        Red: new(0.94f, 0.26f, 0.3f),
        Green: new(0.36f, 0.78f, 0.52f),
        BodyFonts: ["Helvetica Neue", "Inter", "Segoe UI", "Arial", "sans-serif"],
        DisplayFonts: ["Avenir Next Condensed", "Futura", "Bahnschrift", "Arial Narrow", "Arial"],
        DisplayWeight: 800,
        Background: "res://Shaders/police_dispatch.gdshader");

    /// <summary>
    /// Trading: a trading floor of crypto and stocks, after the dark screens of the exchanges. A graphite ground and
    /// panels, a light grey text, the yellow of the exchange for what is active, the orange of bitcoin for what calls,
    /// the green of the rises and the red of the falls for the states, DIN figures for the headers; the charts of the
    /// floor behind the desk (candlesticks, a price line, the depth of the order book, a ticker).
    /// </summary>
    public static readonly Palette Trading = new(
        "Trading", "Dark · a trading floor of crypto and stocks: graphite, exchange yellow, green rises and red falls, candlesticks scrolling", false,
        Space: Color.FromHtml("#0B0E11"),
        Panel: Color.FromHtml("#161A1EE6"),
        PanelSolid: Color.FromHtml("#181A20F8"),
        Header: Color.FromHtml("#1E2329F5"),
        Field: Color.FromHtml("#0B0E11B3"),
        Edge: Color.FromHtml("#2B3139C0"),
        EdgeBright: Color.FromHtml("#474D57"),
        Text: Color.FromHtml("#EAECEF"),
        TextBright: Color.FromHtml("#FFFFFF"),
        Muted: Color.FromHtml("#949CA8"),
        Accent: Color.FromHtml("#F0B90B"),
        Warm: Color.FromHtml("#F7931A"),
        Red: Color.FromHtml("#F6465D"),
        Green: Color.FromHtml("#0ECB81"),
        BodyFonts: ["Inter", "IBM Plex Sans", "Helvetica Neue", "Segoe UI", "Arial", "sans-serif"],
        DisplayFonts: ["DIN Alternate", "Bahnschrift", "Helvetica Neue", "Arial"],
        DisplayWeight: 700,
        Background: "res://Shaders/trading_floor.gdshader");
}
