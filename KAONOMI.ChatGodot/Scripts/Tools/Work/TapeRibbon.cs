using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Environment = System.Environment;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.Chat.Views;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// The ribbon of the rich text editor (<see cref="TapeEditor"/>) in the manner of Word, shared by /work and the journal:
/// its tabs (Home, Insert, View) of named groups, and the groups every window with the editor has. Font: family, size,
/// B I U S, color and clear formatting of the selection; Paragraph: the alignment; Styles: Normal, Title, Subtitle,
/// Heading 1 to 3; Pictures: insert, generate with Fooocus, crop the selected picture; Links; Code: insert, generate with
/// the LLM (a double-click on a block of code edits it); Zoom (also Ctrl / ⌘ + wheel). The right click on the editor:
/// correct the spelling, rewrite for readability, evaluate, translate (by the LLM of <see cref="ITapeAssistant"/>), cut,
/// copy, paste. The window adds its own groups (the documents of /work…) around these.
/// </summary>
internal sealed class TapeRibbon
{
    private const string DefaultFontName = "Default";
    private static readonly int[] Sizes = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72];

    /// <summary>Colors offered for the text (null: the default color of the tape).</summary>
    private static readonly string?[] TextColors =
        [null, "#000000", "#4B5563", "#9CA3AF", "#DC2626", "#EA580C", "#D97706", "#CA8A04", "#16A34A", "#059669",
         "#0891B2", "#2563EB", "#4F46E5", "#7C3AED", "#C026D3", "#DB2777"];

    /// <summary>Zoom steps of − and +, 50 % to 300 %.</summary>
    private static readonly float[] ZoomSteps = [0.5f, 0.67f, 0.75f, 0.8f, 0.9f, 1, 1.1f, 1.25f, 1.5f, 1.75f, 2, 2.5f, 3];

    /// <summary>Zoom of the page of each kind of window (/work, the journal), kept while the chat runs.</summary>
    private static readonly Dictionary<string, float> Zooms = new();

    private readonly TapeEditor _editor;
    private readonly Overlay _overlay;
    private readonly Shell _shell;
    private readonly Control _host;
    private readonly Label _status;
    private readonly ITapeAssistant? _assistant;
    private readonly string _logCategory;
    private readonly string _subject;
    private readonly string _zoomKey;
    private readonly Func<string, string> _cropDirectory;

    // The ribbon controls the toolbar keeps in step with the caret (null while their group is not built).
    private readonly Dropdown _fontBox = new() { CustomMinimumSize = new Vector2(150, 22), TooltipText = "Font of the selection" };
    private readonly Dropdown _sizeBox = new() { CustomMinimumSize = new Vector2(54, 22), TooltipText = "Size of the selection" };
    private readonly Dropdown _paragraphBox = new() { CustomMinimumSize = new Vector2(124, 22), TooltipText = "Style of the paragraph: Normal, Title, Subtitle, Heading 1, 2, 3 (Ctrl / ⌘ + Alt / ⌥ + 1, 2, 3; 0 for Normal)" };
    private readonly List<string> _fonts = [];
    private List<int> _sizes = [.. Sizes];
    private bool _hasFont, _hasStyles;
    private Button? _bold, _italic, _underline, _strike;
    private Button? _alignLeft, _alignCenter, _alignRight;
    private ColorRect? _colorBar;
    private Button? _link, _crop;
    private Label? _cropLabel;
    private Button? _zoomOut, _zoomIn, _zoomLabel;
    private bool _updating;

    /// <summary>
    /// The ribbon of <paramref name="editor"/>: its flyouts and menus in <paramref name="overlay"/>, the waiting popups
    /// over <paramref name="host"/>, its short messages in <paramref name="status"/>; the LLM and Fooocus of
    /// <paramref name="assistant"/> (none: their tools are hidden); <paramref name="logCategory"/> and
    /// <paramref name="subject"/> ("the tape") name it in the activity log; <paramref name="zoomKey"/> keeps its zoom;
    /// a cropped picture is written in <paramref name="cropDirectory"/> of the path of the picture.
    /// </summary>
    public TapeRibbon(TapeEditor editor, Overlay overlay, Shell shell, Control host, Label status, ITapeAssistant? assistant,
        string logCategory, string subject, string zoomKey, Func<string, string> cropDirectory)
    {
        _editor = editor;
        _overlay = overlay;
        _shell = shell;
        _host = host;
        _status = status;
        _assistant = assistant;
        _logCategory = logCategory;
        _subject = subject;
        _zoomKey = zoomKey;
        _cropDirectory = cropDirectory;

        _editor.Zoom = Zoom;
        _editor.OpenLink = OpenLink;
        _editor.CropImage = CropImageAsync;
        _editor.SelectionChanged += UpdateToolbar;
        _editor.CropChanged += UpdateToolbar;
        _editor.ContextMenuRequested += ShowContextMenu;
        _editor.EditCodeRequested += OnEditCode;
        _editor.ZoomRequested += StepZoom;
    }

    private float Zoom
    {
        get => Zooms.GetValueOrDefault(_zoomKey, 1);
        set => Zooms[_zoomKey] = value;
    }

    /// <summary>The LLM can be asked (evaluate, translate, rewrite, generate code).</summary>
    public bool CanEvaluate => _assistant?.CanEvaluate == true;

    // ───────────── Ribbon ─────────────

    /// <summary>
    /// The ribbon, in the manner of a toolbar of macOS: its tabs as a segmented control in the middle (the one in use a
    /// light pill), under it the groups of the tab side by side, separated by hairlines (scrolled sideways when the window
    /// is narrow); compact tools that only show a soft fill under the pointer.
    /// </summary>
    public Control Build(params (string Name, Control[] Groups)[] tabs) => Build(null, tabs);

    /// <summary>The ribbon, with <paramref name="trailing"/> (the status of the window) at the right of its tabs.</summary>
    public Control Build(Control? trailing, params (string Name, Control[] Groups)[] tabs)
    {
        var strips = tabs.Select(tab => (Control)Ui.Row(2, tab.Groups)).ToArray();
        var holder = new MarginContainer();
        for (var i = 0; i < strips.Length; i++)
        {
            strips[i].Visible = i == 0;
            holder.AddChild(strips[i]);
        }
        var tabButtons = new List<Button>();
        // The tabs as a segmented control: a rounded groove, the tab in use a raised pill in it.
        var segments = Ui.Row(0);
        var segmented = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var groove = MacBox(Eve.Eve.IsLight ? new Color(0, 0, 0, 0.065f) : new Color(1, 1, 1, 0.07f), 7, 2, 2);
        segmented.AddThemeStyleboxOverride("panel", groove);
        segmented.AddChild(segments);
        var line = Ui.Row(2);
        line.AddChild(Ui.Spring());
        line.AddChild(segmented);
        line.AddChild(Ui.Spring());
        Control panel = null!;
        // The window of the ribbon is kept wide enough to show the widest tab whole (measured once the ribbon is in
        // the tree, and again when a tab is chosen: a hidden strip may not know its size yet).
        void Fit()
        {
            if (!panel.IsInsideTree() || _shell.WindowOf(panel) is not { } window)
                return;
            var widest = strips.Max(strip => strip.GetCombinedMinimumSize().X);
            if (widest < 10)
                return;
            _shell.Deck.RequireMinSize(window, new Vector2(widest + 44, 240));
        }
        void Select(int index)
        {
            for (var i = 0; i < strips.Length; i++)
            {
                strips[i].Visible = i == index;
                TabLook(tabButtons[i], i == index);
            }
            if (panel is not null)
                Callable.From(Fit).CallDeferred();
        }
        for (var i = 0; i < tabs.Length; i++)
        {
            var index = i;
            var tab = new Button { Text = tabs[i].Name, FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand, CustomMinimumSize = new Vector2(72, 20) };
            tab.AddThemeFontOverride("font", Eve.Eve.Body);
            tab.AddThemeFontSizeOverride("font_size", 11);
            tab.Pressed += () => Select(index);
            tabButtons.Add(tab);
            segments.AddChild(tab);
        }
        if (trailing is not null)
        {
            trailing.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            line.AddChild(trailing);
        }
        Select(0);
        var ribbonScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 66) };
        ribbonScroll.AddChild(holder);

        // The toolbar: a light unified bar, a hairline under it.
        panel = new PanelContainer();
        var box = Eve.Eve.Box(Eve.Eve.IsLight ? new Color(0.965f, 0.965f, 0.97f, 0.92f) : new Color(1, 1, 1, 0.04f), Eve.Eve.Edge with { A = 0f }, 0, 0);
        box.BorderWidthBottom = 1;
        box.BorderColor = Eve.Eve.IsLight ? new Color(0, 0, 0, 0.1f) : new Color(1, 1, 1, 0.1f);
        box.ContentMarginLeft = box.ContentMarginRight = 8;
        box.ContentMarginTop = 5;
        box.ContentMarginBottom = 4;
        panel.AddThemeStyleboxOverride("panel", box);
        panel.AddChild(Ui.Column(5, line, ribbonScroll));
        panel.Ready += () => Callable.From(Fit).CallDeferred();
        ApplyZoom();
        UpdateToolbar();
        return panel;
    }

    /// <summary>A segment of the tabs: the one in use a raised pill (white, a soft shadow), the others plain text.</summary>
    private static void TabLook(Button tab, bool active)
    {
        var ink = Eve.Eve.TextBright;
        StyleBoxFlat look;
        if (active)
        {
            look = MacBox(Eve.Eve.IsLight ? Colors.White : new Color(1, 1, 1, 0.16f), 5, 12, 2);
            look.ShadowColor = new Color(0, 0, 0, Eve.Eve.IsLight ? 0.14f : 0.35f);
            look.ShadowSize = 2;
            look.ShadowOffset = new Vector2(0, 1);
        }
        else
            look = MacBox(new Color(0, 0, 0, 0), 5, 12, 2);
        var hover = active ? look : MacBox(HoverFill, 5, 12, 2);
        tab.AddThemeStyleboxOverride("normal", look);
        tab.AddThemeStyleboxOverride("hover", hover);
        tab.AddThemeStyleboxOverride("pressed", look);
        tab.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        tab.AddThemeColorOverride("font_color", active ? ink : Eve.Eve.Text);
        tab.AddThemeColorOverride("font_hover_color", ink);
        tab.AddThemeColorOverride("font_pressed_color", ink);
    }

    /// <summary>The soft fill of a tool under the pointer, and while it is pressed or on (grey, as on macOS).</summary>
    private static Color HoverFill => Eve.Eve.IsLight ? new Color(0, 0, 0, 0.06f) : new Color(1, 1, 1, 0.08f);

    private static Color PressFill => Eve.Eve.IsLight ? new Color(0, 0, 0, 0.13f) : new Color(1, 1, 1, 0.17f);

    /// <summary>A rounded box without edge: <paramref name="radius"/>, margins across and up and down.</summary>
    private static StyleBoxFlat MacBox(Color back, int radius, int across, int upDown)
    {
        var box = new StyleBoxFlat { BgColor = back, AntiAliasing = true };
        box.SetCornerRadiusAll(radius);
        box.ContentMarginLeft = box.ContentMarginRight = across;
        box.ContentMarginTop = box.ContentMarginBottom = upDown;
        return box;
    }

    /// <summary>The looks of a tool: nothing at rest, a soft fill under the pointer, a deeper one pressed or on.</summary>
    private static void ToolLook(Button button, int radius)
    {
        button.AddThemeStyleboxOverride("normal", MacBox(new Color(0, 0, 0, 0), radius, 4, 2));
        button.AddThemeStyleboxOverride("hover", MacBox(HoverFill, radius, 4, 2));
        button.AddThemeStyleboxOverride("pressed", MacBox(PressFill, radius, 4, 2));
        button.AddThemeStyleboxOverride("hover_pressed", MacBox(PressFill, radius, 4, 2));
        button.AddThemeStyleboxOverride("disabled", MacBox(new Color(0, 0, 0, 0), radius, 4, 2));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
    }

    /// <summary>A group of the ribbon: its tools side by side, its name small and grey under them, a hairline after it.</summary>
    public static Control Group(string name, bool last, params Control[] tools)
    {
        foreach (var tool in tools)
            tool.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var row = Ui.Row(2, tools);
        row.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        row.Alignment = BoxContainer.AlignmentMode.Center;
        var label = Eve.Eve.Text_(name, Eve.Eve.Muted, 9);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var column = Ui.Column(1, row, label);
        column.CustomMinimumSize = new Vector2(0, 62);
        var group = Ui.Row(0, Ui.Margin(column, 6, 0, 6, 0));
        if (!last)
        {
            var hairline = new ColorRect
            {
                Color = Eve.Eve.IsLight ? new Color(0, 0, 0, 0.1f) : new Color(1, 1, 1, 0.1f),
                CustomMinimumSize = new Vector2(1, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            group.AddChild(Ui.Margin(hairline, 0, 6, 0, 14));
        }
        return group;
    }

    /// <summary>A big tool: its icon above its name.</summary>
    public static Button BigTool(string icon, string label, string tooltip, Action? action = null, Color? iconColor = null)
    {
        var button = new Button { TooltipText = tooltip, FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        ToolLook(button, 6);
        var glyph = Eve.Eve.Text_(icon, iconColor ?? Eve.Eve.TextBright, 16);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var name = Eve.Eve.Text_(label, Eve.Eve.Text, 10);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Name = "Label";
        var width = Math.Max(Eve.Eve.Body.GetStringSize(label, HorizontalAlignment.Left, -1, 10).X, Eve.Eve.Body.GetStringSize(icon, HorizontalAlignment.Left, -1, 16).X);
        button.CustomMinimumSize = new Vector2(Math.Max(40, width + 12), 42);
        button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var column = Ui.Column(0, glyph, name);
        column.Alignment = BoxContainer.AlignmentMode.Center;
        column.MouseFilter = Control.MouseFilterEnum.Ignore;
        glyph.MouseFilter = name.MouseFilter = Control.MouseFilterEnum.Ignore;
        column.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(column);
        button.MouseEntered += () => name.AddThemeColorOverride("font_color", Eve.Eve.TextBright);
        button.MouseExited += () => name.AddThemeColorOverride("font_color", Eve.Eve.Text);
        if (action is not null)
            button.Pressed += action;
        return button;
    }

    /// <summary>A small tool of the font and paragraph groups.</summary>
    public static Button SmallTool(string text, string tooltip, Action action, bool toggle = false, Font? font = null)
    {
        var button = new Button { Text = text, TooltipText = tooltip, ToggleMode = toggle, CustomMinimumSize = new Vector2(24, 22), FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        if (font is not null)
            button.AddThemeFontOverride("font", font);
        button.AddThemeFontSizeOverride("font_size", 12);
        // Nothing at rest, a soft grey fill under the pointer, a deeper one while on or pressed.
        ToolLook(button, 5);
        button.AddThemeColorOverride("font_pressed_color", Eve.Eve.TextBright);
        button.AddThemeColorOverride("font_hover_pressed_color", Eve.Eve.TextBright);
        button.Pressed += action;
        return button;
    }

    /// <summary>A big tool lit in cyan while its state is on (the link at the caret, the crop frame shown).</summary>
    public static void SetOn(Button button, bool on)
    {
        button.AddThemeStyleboxOverride("normal", MacBox(on ? PressFill : new Color(0, 0, 0, 0), 6, 4, 2));
    }

    /// <summary>Font: family, size, B I U S, color, clear.</summary>
    public Control FontGroup(bool last = false)
    {
        _hasFont = true;
        _fonts.Add(DefaultFontName);
        _fonts.AddRange(OS.GetSystemFonts().Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('.')).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        _fontBox.SetItems(_fonts);
        _fontBox.ItemLook = index => (FontOf(_fonts[index]), null, null);
        _fontBox.ItemSelected += index =>
        {
            if (_updating || index < 0 || index >= _fonts.Count)
                return;
            var font = _fonts[(int)index];
            _editor.ApplyStyle(s => s with { Font = font == DefaultFontName ? null : font });
        };
        FillSizes();
        _sizeBox.ItemSelected += index =>
        {
            if (_updating || index < 0 || index >= _sizes.Count)
                return;
            var size = _sizes[(int)index];
            var styleSize = TapeParagraph.KindStyle(_editor.CurrentParagraphKind).Size ?? TapeEditor.DefaultSize;
            _editor.ApplyStyle(s => s with { Size = Math.Abs(size - styleSize) < 0.01 ? null : size });
        };
        Button bold = null!, italic = null!, underline = null!, strike = null!;
        bold = SmallTool("B", "Bold (Ctrl / ⌘ + B)", () => _editor.ApplyStyle(s => s with { Bold = bold.ButtonPressed }), true,
            new SystemFont { FontNames = Eve.Eve.Body.FontNames, FontWeight = 800 });
        italic = SmallTool("I", "Italic (Ctrl / ⌘ + I)", () => _editor.ApplyStyle(s => s with { Italic = italic.ButtonPressed }), true,
            new SystemFont { FontNames = ["Georgia", "Times New Roman", "serif"], FontItalic = true });
        underline = SmallTool("U̲", "Underline (Ctrl / ⌘ + U)", () => _editor.ApplyStyle(s => s with { Underline = underline.ButtonPressed }), true);
        strike = SmallTool("a̶b̶", "Strikethrough", () => _editor.ApplyStyle(s => s with { Strike = strike.ButtonPressed }), true);
        (_bold, _italic, _underline, _strike) = (bold, italic, underline, strike);
        var color = SmallTool("A", "Font color of the selection", () => { }, font: new SystemFont { FontNames = Eve.Eve.Body.FontNames, FontWeight = 800 });
        _colorBar = new ColorRect { Color = WorkPalette.DefaultText, CustomMinimumSize = new Vector2(12, 2), MouseFilter = Control.MouseFilterEnum.Ignore };
        _colorBar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        _colorBar.Position = new Vector2(6, 18);
        color.AddChild(_colorBar);
        color.Pressed += () => ShowColorFlyout(color);
        var clear = SmallTool("A✕", "Clear all formatting of the selection (font, size, styles, color)", () => _editor.ApplyStyle(_ => TapeStyle.Plain));
        clear.AddThemeColorOverride("font_color", Eve.Eve.Red);
        return Group("Font", last, Ui.Column(3, Ui.Row(4, _fontBox, _sizeBox), Ui.Row(1, bold, italic, underline, strike, color, clear)));
    }

    /// <summary>Paragraph: left, centered, right.</summary>
    public Control ParagraphGroup(bool last = false)
    {
        _alignLeft = SmallTool("⫷", "Align left", () => Align(TapeAlignment.Left), true);
        _alignCenter = SmallTool("☰", "Center", () => Align(TapeAlignment.Center), true);
        _alignRight = SmallTool("⫸", "Align right", () => Align(TapeAlignment.Right), true);
        _alignLeft.Text = "≡◂";
        _alignCenter.Text = "≡";
        _alignRight.Text = "▸≡";
        return Group("Paragraph", last, _alignLeft, _alignCenter, _alignRight);
    }

    /// <summary>Styles: the style of the paragraph, each shown as it looks.</summary>
    public Control StylesGroup(bool last = false)
    {
        _hasStyles = true;
        _paragraphBox.SetItems(Enum.GetValues<TapeParagraphKind>().Select(ParagraphKindName));
        _paragraphBox.ItemLook = index =>
        {
            var style = TapeParagraph.KindStyle(Enum.GetValues<TapeParagraphKind>()[index]);
            return (style.Bold ? new SystemFont { FontNames = Eve.Eve.Body.FontNames, FontWeight = 700 } : null,
                (int)Math.Min(style.Size ?? 13, 20), WorkPalette.Adapt(style.Color, Eve.Eve.TextBright));
        };
        _paragraphBox.ItemSelected += index =>
        {
            if (!_updating)
                _editor.SetParagraphKind(Enum.GetValues<TapeParagraphKind>()[index]);
        };
        return Group("Styles", last, _paragraphBox);
    }

    /// <summary>
    /// Pictures: 🖼 (<paramref name="insertPictures"/>, the window decides where they come from), ✨🖼 generated with
    /// Fooocus and inserted at the caret (hidden without Fooocus), ✂ Crop the selected picture.
    /// </summary>
    public Control PicturesGroup(string picturesTooltip, Action insertPictures, string generateTooltip, bool last = false)
    {
        var pictures = BigTool("🖼", "Pictures", picturesTooltip, insertPictures);
        var generate = BigTool("✨🖼", "Generate", generateTooltip, OnGenerateImage);
        generate.Visible = _assistant?.CanGenerateImages == true;
        _crop = BigTool("✂", "Crop", "Crop the selected picture: drag the frame or its handles, then Enter (or click ✓ Crop) to crop, Escape to cancel; the original file is kept", () =>
        {
            if (_editor.IsCropping)
                _editor.ApplyCrop();
            else
                _editor.StartCrop();
        });
        _cropLabel = _crop.FindChild("Label", true, false) as Label ?? new Label();
        return Group("Pictures", last, pictures, generate, _crop);
    }

    /// <summary>Links: 🔗 makes the selection a link.</summary>
    public Control LinksGroup(bool last = false)
    {
        _link = BigTool("🔗", "Link", "Link: makes the selected text a clickable link (Ctrl / ⌘ + click opens it)");
        _link.Pressed += () => ShowLinkFlyout(_link);
        return Group("Links", last, _link);
    }

    /// <summary>Code: insert code, generate code with the LLM (hidden without it).</summary>
    public Control CodeGroup(bool last = true)
    {
        var code = BigTool("</>", "Code", "Insert code at the caret, shown with the syntax coloring of its language (a double-click on a block of code edits it)", InsertCode, Eve.Eve.Cyan);
        var generateCode = BigTool("✨</>", "Generate", "Generate code with the LLM from a prompt, then insert it at the caret", OnGenerateCode, Eve.Eve.Cyan);
        generateCode.Visible = CanEvaluate;
        return Group("Code", last, code, generateCode);
    }

    /// <summary>Zoom: −, the zoom (click: 100 %), +.</summary>
    public Control ZoomGroup(bool last = true)
    {
        _zoomOut = SmallTool("−", "Zoom out the page (Ctrl / ⌘ + wheel down)", () => StepZoom(-1));
        _zoomLabel = SmallTool("100 %", "Zoom of the page; click to come back to 100 %", () =>
        {
            Zoom = 1;
            ApplyZoom();
        });
        _zoomLabel.CustomMinimumSize = new Vector2(50, 22);
        _zoomIn = SmallTool("+", "Zoom in the page (Ctrl / ⌘ + wheel up)", () => StepZoom(1));
        return Group("Zoom", last, _zoomOut, _zoomLabel, _zoomIn);
    }

    private static string ParagraphKindName(TapeParagraphKind kind) => kind switch
    {
        TapeParagraphKind.Heading1 => "Heading 1",
        TapeParagraphKind.Heading2 => "Heading 2",
        TapeParagraphKind.Heading3 => "Heading 3",
        _ => kind.ToString()
    };

    private void FillSizes() => _sizeBox.SetItems(_sizes.Select(size => size.ToString()));

    /// <summary>The fonts of the list, each shown in itself (made once).</summary>
    private static readonly Dictionary<string, Font> FontPreviews = new();

    private static Font FontOf(string name)
    {
        if (name == DefaultFontName)
            return new SystemFont { FontNames = WorkPalette.TextNames };
        if (!FontPreviews.TryGetValue(name, out var font))
            FontPreviews[name] = font = new SystemFont { FontNames = [name], Fallbacks = [Eve.Eve.Body] };
        return font;
    }

    private void Align(TapeAlignment alignment)
    {
        _editor.SetAlignment(alignment);
        UpdateToolbar();
    }

    /// <summary>The toolbar shows the formatting at the caret (or of the selection).</summary>
    public void UpdateToolbar()
    {
        _updating = true;
        try
        {
            if (_hasStyles)
                _paragraphBox.Selected = Array.IndexOf(Enum.GetValues<TapeParagraphKind>(), _editor.CurrentParagraphKind);
            var style = _editor.EffectiveStyle;
            if (_hasFont)
            {
                var font = style.Font ?? DefaultFontName;
                if (!_fonts.Contains(font))
                {
                    _fonts.Insert(1, font);
                    _fontBox.SetItems(_fonts);
                }
                _fontBox.Selected = _fonts.IndexOf(font);
                var size = (int)Math.Round(style.Size ?? TapeEditor.DefaultSize);
                if (!_sizes.Contains(size))
                {
                    _sizes = Sizes.Append(size).Order().ToList();
                    FillSizes();
                }
                _sizeBox.Selected = _sizes.IndexOf(size);
            }
            _bold?.SetPressedNoSignal(style.Bold);
            _italic?.SetPressedNoSignal(style.Italic);
            _underline?.SetPressedNoSignal(style.Underline);
            _strike?.SetPressedNoSignal(style.Strike);
            if (_colorBar is not null)
                _colorBar.Color = WorkPalette.Adapt(style.Color, WorkPalette.DefaultText);
            if (_link is not null)
                SetOn(_link, style.Link is not null);
            if (_crop is not null)
            {
                _crop.Disabled = !(_editor.IsImageSelected || _editor.IsCropping);
                _crop.Modulate = _crop.Disabled ? new Color(1, 1, 1, 0.4f) : Colors.White;
                SetOn(_crop, _editor.IsCropping);
                _cropLabel!.Text = _editor.IsCropping ? "✓ Crop" : "Crop";
            }
            var alignment = _editor.CurrentAlignment;
            _alignLeft?.SetPressedNoSignal(alignment == TapeAlignment.Left);
            _alignCenter?.SetPressedNoSignal(alignment == TapeAlignment.Center);
            _alignRight?.SetPressedNoSignal(alignment == TapeAlignment.Right);
        }
        finally
        {
            _updating = false;
        }
    }

    // ───────────── Status ─────────────

    /// <summary>A short message in the status label, gone after a few seconds.</summary>
    public void ShowStatus(string message, Color? color = null, bool keep = false)
    {
        // The window may be gone when a save, an export or the LLM answers.
        if (!GodotObject.IsInstanceValid(_status))
            return;
        _status.Text = message;
        _status.AddThemeColorOverride("font_color", color ?? Eve.Eve.Green);
        if (keep || !_status.IsInsideTree())
            return;
        _status.GetTree().CreateTimer(5).Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(_status) && _status.Text == message)
                _status.Text = string.Empty;
        };
    }

    /// <summary>Removes the message of the status label.</summary>
    public void ClearStatus()
    {
        if (GodotObject.IsInstanceValid(_status))
            _status.Text = string.Empty;
    }

    // ───────────── Flyouts ─────────────

    private void ShowColorFlyout(Control anchor)
    {
        var grid = Ui.Grid(4, 6, 6);
        foreach (var color in TextColors)
        {
            var swatch = new Button { CustomMinimumSize = new Vector2(24, 24), TooltipText = color ?? "Default color", FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
            var shown = color is null ? new Color(0, 0, 0, 0) : WorkPalette.Adapt(color, WorkPalette.DefaultText);
            var box = Eve.Eve.Box(shown, Eve.Eve.EdgeBright, 0);
            swatch.AddThemeStyleboxOverride("normal", box);
            swatch.AddThemeStyleboxOverride("hover", Eve.Eve.Box(shown, Eve.Eve.Cyan, 0, 2));
            if (color is null)
                swatch.Text = "∅";
            var value = color;
            swatch.Pressed += () =>
            {
                _overlay.Close();
                _editor.ApplyStyle(s => s with { Color = value });
            };
            grid.AddChild(swatch);
        }
        _overlay.Show(Ui.Column(6, Eve.Eve.Caption("Font color"), grid), anchor);
    }

    /// <summary>The 🔗 panel of the Links group (also Ctrl / ⌘ + K in the journal).</summary>
    public void ShowLinkFlyout() => ShowLinkFlyout(_link ?? (Control)_editor);

    /// <summary>
    /// The 🔗 panel: the address of the link of the selection (the whole link selected when the caret is in one),
    /// applied with Enter or Apply, removed with Remove, opened with Open; with nothing selected, the address is added at
    /// the caret as a link.
    /// </summary>
    private void ShowLinkFlyout(Control anchor)
    {
        var selected = _editor.SelectLinkAtCaret();
        var current = _editor.CurrentLink;
        var address = new LineEdit { CustomMinimumSize = new Vector2(320, 0), PlaceholderText = "https://… or a file path", Text = current ?? string.Empty };
        var hint = Eve.Eve.Text_(selected
            ? "The selected text becomes a link to this address; Ctrl / ⌘ + click opens it."
            : "Nothing is selected: the address is added at the caret as a link.", Eve.Eve.Muted, 11, wrap: true);
        hint.CustomMinimumSize = new Vector2(320, 0);
        void Apply()
        {
            _overlay.Close();
            _editor.SetLink(address.Text);
        }
        var apply = Ui.Action("APPLY", Apply, primary: true);
        var open = Ui.Action("OPEN", () =>
        {
            _overlay.Close();
            if (_editor.CurrentLink is { } link)
                OpenLink(link);
        });
        var remove = Ui.Action("REMOVE", () =>
        {
            _overlay.Close();
            _editor.SetLink(null);
        });
        open.Disabled = remove.Disabled = current is null;
        address.TextSubmitted += _ => Apply();
        _overlay.Show(Ui.Column(8, Eve.Eve.Caption("Link address"), address, hint, Ui.Row(6, apply, open, remove)), anchor);
        Callable.From(() =>
        {
            address.GrabFocus();
            address.SelectAll();
        }).CallDeferred();
    }

    // ───────────── Right-click menu ─────────────

    private void ShowContextMenu(Vector2 at)
    {
        var hasText = _editor.HasSelectedContent && _editor.SelectedText.Length > 0;
        var busy = _editor.IsBusy;
        var shortcut = OS.GetName() == "macOS" ? "⌘" : "Ctrl+";
        var items = new List<Overlay.MenuItem?>
        {
            new(hasText ? "✏️ Correct the spelling" : "✏️ Correct the spelling (select text first)", hasText && !busy,
                () => Rewrite(_editor.SelectedText, TapeEditor.RewriteSpelling, _editor.CaptureSelection())),
            new(hasText ? "✨ Rewrite for readability" : "✨ Rewrite for readability (select text first)", hasText && !busy,
                () => Rewrite(_editor.SelectedText, TapeEditor.RewriteReadability, _editor.CaptureSelection())),
            null,
            new(busy ? "🧐 Evaluating…" : hasText ? "🧐 Evaluate the selection" : "🧐 Evaluate (select text first)", hasText && !busy,
                () => Evaluate(_editor.SelectedText)),
            new(hasText ? "🌐 Translate the selection into" : "🌐 Translate (select text first)", false, null)
        };
        foreach (var (language, label) in TapeEditor.TranslationLanguages)
            items.Add(new Overlay.MenuItem(label, hasText && !busy, () => Translate(_editor.SelectedText, language, _editor.CaptureSelection()), Indent: 1));
        items.Add(null);
        items.Add(new Overlay.MenuItem(hasText ? "📝 Annotate the selection…" : "📝 Annotate (select text first)", hasText, () =>
            Data.Annotations.Open(_editor, AnnotationTarget.Text(_editor.SelectedText.Trim(), Shell.Current?.WindowOf(_editor)?.Title ?? "Work"))));
        items.Add(new Overlay.MenuItem("Cut", _editor.HasSelectedContent, () => _editor.Copy(cut: true), shortcut + "X"));
        items.Add(new Overlay.MenuItem("Copy", _editor.HasSelectedContent, () => _editor.Copy(cut: false), shortcut + "C"));
        items.Add(new Overlay.MenuItem("Paste", true, _editor.Paste, shortcut + "V"));
        items.Add(null);
        items.Add(new Overlay.MenuItem(Data.VoiceDictation.IsOpenOn(_editor) ? "🎤 Stop voice dictation" : "🎤 Voice dictation", true,
            () => Data.VoiceDictation.Toggle(_editor)));
        _overlay.Menu(items, _editor.GlobalPosition - _overlay.GlobalPosition + at);
    }

    // ───────────── LLM ─────────────

    private string ModelName => _assistant?.CurrentModelName ?? "LLM";

    /// <summary>
    /// 🧐 Evaluate: the LLM criticizes the text and suggests improvements, with the waiting popup; the result opens in a
    /// window of its own.
    /// </summary>
    private async void Evaluate(string text)
    {
        if (_editor.IsBusy || text.Length == 0)
            return;
        if (_assistant is not { CanEvaluate: true } assistant)
        {
            ShowStatus("No LLM available for the evaluation", Eve.Eve.Red);
            return;
        }
        var model = ModelName;
        _editor.IsBusy = true;
        ShowStatus($"🧐 Evaluating with {model}…", Eve.Eve.Muted, keep: true);
        string? markdown = null, error = null;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        // The waiting popup (an animation, what is done, the time elapsed, Cancel), as for the spelling correction.
        var busy = BusyPopup.Show(_host, "Evaluation", $"{model} evaluates the text…", cancel);
        var cancelled = false;
        try
        {
            markdown = await assistant.EvaluateAsync(text, cancel.Token);
        }
        catch (OperationCanceledException) when (GodotObject.IsInstanceValid(busy) && busy.IsCancelledByUser)
        {
            cancelled = true;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException
                                       or OperationCanceledException or System.Text.Json.JsonException)
        {
            error = ex is OperationCanceledException ? "no answer after 10 minutes" : ex.Message;
            ActivityLog.Error(_logCategory, $"Evaluation of {_subject} failed", ex);
        }
        finally
        {
            busy.Finish();
            if (GodotObject.IsInstanceValid(_editor))
            {
                _editor.IsBusy = false;
                ClearStatus();
            }
        }
        if (cancelled)
        {
            ShowStatus("Evaluation cancelled", Eve.Eve.Muted);
            return;
        }
        WorkWindows.Evaluation(_shell, text, model, markdown, error);
    }

    /// <summary>
    /// 🌐 Translate: the LLM translates the selection, with the waiting popup; its window can replace the selection or
    /// insert below it.
    /// </summary>
    private async void Translate(string text, string language, TapeEditor.TapeSelection selection)
    {
        if (_editor.IsBusy || text.Length == 0)
            return;
        if (_assistant is not { CanEvaluate: true } assistant)
        {
            ShowStatus("No LLM available for the translation", Eve.Eve.Red);
            return;
        }
        var model = ModelName;
        _editor.IsBusy = true;
        ShowStatus($"🌐 Translating into {language} with {model}…", Eve.Eve.Muted, keep: true);
        string? translation = null, error = null;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        // The waiting popup (an animation, what is done, the time elapsed, Cancel), as for the spelling correction.
        var busy = BusyPopup.Show(_host, "Translation", $"{model} translates the text into {language}…", cancel);
        var cancelled = false;
        try
        {
            translation = await assistant.TranslateAsync(text, language, cancel.Token);
        }
        catch (OperationCanceledException) when (GodotObject.IsInstanceValid(busy) && busy.IsCancelledByUser)
        {
            cancelled = true;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException
                                       or OperationCanceledException or System.Text.Json.JsonException)
        {
            error = ex is OperationCanceledException ? "no answer after 10 minutes" : ex.Message;
            ActivityLog.Error(_logCategory, $"Translation of {_subject} into {language} failed", ex);
        }
        finally
        {
            busy.Finish();
            if (GodotObject.IsInstanceValid(_editor))
            {
                _editor.IsBusy = false;
                ClearStatus();
            }
        }
        if (cancelled)
        {
            ShowStatus("Translation cancelled", Eve.Eve.Muted);
            return;
        }
        WorkWindows.Translation(_shell, text, language, model, translation, error,
            result => GodotObject.IsInstanceValid(_editor) && _editor.ReplaceSelection(selection, result),
            result => GodotObject.IsInstanceValid(_editor) && _editor.InsertAfter(selection, result));
    }

    /// <summary>
    /// ✏️ Correct the spelling (the language identified first) / ✨ Rewrite for readability, with the waiting popup; the
    /// result opens in a window where it can be changed, then replace the selection or go below it.
    /// </summary>
    private async void Rewrite(string text, string mode, TapeEditor.TapeSelection selection)
    {
        if (_editor.IsBusy || text.Length == 0)
            return;
        if (_assistant is not { CanEvaluate: true } assistant)
        {
            ShowStatus("No LLM available for the rewriting", Eve.Eve.Red);
            return;
        }
        var spelling = mode == TapeEditor.RewriteSpelling;
        var model = ModelName;
        _editor.IsBusy = true;
        ShowStatus(spelling ? $"✏️ Correcting the spelling with {model}…" : $"✨ Rewriting for readability with {model}…", Eve.Eve.Muted, keep: true);
        string? result = null, error = null, language = null;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var busy = BusyPopup.Show(_host, spelling ? "Spelling correction" : "Rewriting",
            spelling ? $"{model} identifies the language of the text…" : $"{model} rewrites the text for readability…", cancel);
        var cancelled = false;
        try
        {
            if (spelling)
            {
                language = await assistant.DetectLanguageAsync(text, cancel.Token);
                if (GodotObject.IsInstanceValid(busy))
                    busy.Message = $"Language: {language} · {model} checks and corrects the spelling…";
            }
            result = await assistant.RewriteAsync(text, mode, cancel.Token, language);
        }
        catch (OperationCanceledException) when (GodotObject.IsInstanceValid(busy) && busy.IsCancelledByUser)
        {
            cancelled = true;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or InvalidOperationException
                                       or OperationCanceledException or System.Text.Json.JsonException)
        {
            error = ex is OperationCanceledException ? "no answer after 10 minutes" : ex.Message;
            ActivityLog.Error(_logCategory, $"Rewriting ({mode}) of {_subject} failed", ex);
        }
        finally
        {
            busy.Finish();
            if (GodotObject.IsInstanceValid(_editor))
            {
                _editor.IsBusy = false;
                ClearStatus();
            }
        }
        if (cancelled)
        {
            ShowStatus(spelling ? "Spelling correction cancelled" : "Rewriting cancelled", Eve.Eve.Muted);
            return;
        }
        WorkWindows.Translation(_shell, text, "", model, result, error,
            corrected => GodotObject.IsInstanceValid(_editor) && _editor.ReplaceSelection(selection, corrected),
            corrected => GodotObject.IsInstanceValid(_editor) && _editor.InsertAfter(selection, corrected),
            spelling ? $"✏️ Spelling correction{(language is null ? string.Empty : " · " + language)}" : $"✨ Rewriting · « {WritingAssistant.ReadabilityPrompt} »",
            spelling ? "Spelling correction" : "Rewriting");
    }

    // ───────────── Pictures ─────────────

    /// <summary>✨🖼: the image window (Fooocus); Insert puts the image in the editor at the caret.</summary>
    private void OnGenerateImage()
    {
        if (_assistant is not { } assistant)
            return;
        WorkWindows.ImageGeneration(_shell, "Generate an image · Fooocus", assistant.GenerateImageAsync, path =>
        {
            if (!GodotObject.IsInstanceValid(_editor))
                return;
            _editor.InsertImages([path]);
            ShowStatus("✓ Image inserted");
        });
    }

    /// <summary>The part <paramref name="part"/> (0 to 1) of the picture, saved as a PNG (name-crop-date.png; the original kept).</summary>
    private async Task<string?> CropImageAsync(string path, Rect2 part)
    {
        try
        {
            var image = new Image();
            if (!System.IO.File.Exists(path) || image.Load(path) != Error.Ok)
            {
                using var bitmap = await PictureLibrary.DecodeAsync(path, 4096, CancellationToken.None);
                image = AvaloniaBridge.ToTexture(bitmap)?.GetImage();
            }
            if (image is null || image.IsEmpty())
            {
                ShowStatus($"Not cropped: {System.IO.Path.GetFileName(path)} cannot be read", Eve.Eve.Red);
                return null;
            }
            var width = Math.Max(1, (int)Math.Round(part.Size.X * image.GetWidth()));
            var height = Math.Max(1, (int)Math.Round(part.Size.Y * image.GetHeight()));
            var x = Math.Clamp((int)Math.Round(part.Position.X * image.GetWidth()), 0, image.GetWidth() - 1);
            var y = Math.Clamp((int)Math.Round(part.Position.Y * image.GetHeight()), 0, image.GetHeight() - 1);
            var cropped = image.GetRegion(new Rect2I(x, y, Math.Min(width, image.GetWidth() - x), Math.Min(height, image.GetHeight() - y)));
            var directory = _cropDirectory(path);
            System.IO.Directory.CreateDirectory(directory);
            var file = System.IO.Path.Combine(directory, $"{System.IO.Path.GetFileNameWithoutExtension(path)}-crop-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            if (cropped.SavePng(file) != Error.Ok)
                throw new System.IO.IOException("The cropped picture could not be written.");
            ShowStatus("✓ Cropped");
            ActivityLog.Info(_logCategory, $"Picture of {_subject} cropped: {file} ({cropped.GetWidth()}×{cropped.GetHeight()}, from {path})");
            return file;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowStatus($"Not cropped: {ex.Message}", Eve.Eve.Red);
            return null;
        }
    }

    /// <summary>
    /// A link: a web (or mail) address in the browser at once; anything else (a file, a folder, another scheme) only
    /// once the user said so, and never a program, a script, a shortcut or a share of the network
    /// (<see cref="PictureLibrary.IsSafeToOpen"/>): the text of the page may come from the server, an agent or the model.
    /// </summary>
    public async void OpenLink(string link)
    {
        try
        {
            string target;
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme is "http" or "https" or "mailto")
            {
                OS.ShellOpen(uri.ToString());
                ActivityLog.Info(_logCategory, $"Link of {_subject} opened: {link}");
                return;
            }
            target = Uri.TryCreate(link, UriKind.Absolute, out uri) && !uri.IsFile
                ? link
                : link.StartsWith('~')
                    ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), link.TrimStart('~', '/', '\\'))
                    : JournalMarkdown.LocalPath(link);
            if (!PictureLibrary.IsSafeToOpen(target, out var reason))
            {
                ShowStatus($"Link not opened: {reason}", Eve.Eve.Red);
                ActivityLog.Error(_logCategory, $"Link of {_subject} refused ({reason}): {link}");
                return;
            }
            var shown = target.Length > 160 ? target[..160] + "…" : target;
            if (!await Dialogs.Confirm("Open outside the chat", $"This link is not a web page: it would be opened by the system.\n\n{shown}", "Open"))
                return;
            PictureLibrary.OpenWithSystem(target);
            ActivityLog.Info(_logCategory, $"Link of {_subject} opened after confirmation: {link}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            ShowStatus($"Link not opened: {ex.Message}", Eve.Eve.Red);
        }
    }

    // ───────────── Code ─────────────

    /// <summary>The </> window: code inserted at the caret.</summary>
    public void InsertCode() =>
        WorkWindows.Code(_shell, "Insert code", "Insert", "python", string.Empty, (language, code) =>
        {
            if (GodotObject.IsInstanceValid(_editor))
                _editor.InsertCode(language, code);
        });

    private void OnGenerateCode()
    {
        if (_assistant is not { } assistant)
            return;
        WorkWindows.Code(_shell, $"Generate code · {ModelName}", "Insert", "auto", string.Empty, (language, code) =>
        {
            if (!GodotObject.IsInstanceValid(_editor))
                return;
            _editor.InsertCode(language, code);
            ShowStatus("✓ Code inserted");
        }, assistant.GenerateCodeAsync);
    }

    private void OnEditCode(string language, string code, Func<string, string, bool> replace) =>
        WorkWindows.Code(_shell, "Edit code", "Update", language, code, (edited, text) =>
        {
            if (!replace(edited, text) && GodotObject.IsInstanceValid(_status))
                ShowStatus($"Not updated: {_subject} changed meanwhile", Eve.Eve.Amber);
        });

    // ───────────── Zoom ─────────────

    private void ApplyZoom()
    {
        _editor.Zoom = Zoom;
        if (_zoomLabel is null || _zoomOut is null || _zoomIn is null)
            return;
        _zoomLabel.Text = $"{Zoom * 100:0} %";
        _zoomOut.Disabled = Zoom <= ZoomSteps[0] + 0.001f;
        _zoomIn.Disabled = Zoom >= ZoomSteps[^1] - 0.001f;
    }

    private void StepZoom(int direction)
    {
        var zoom = Zoom;
        var next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > zoom + 0.001f, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < zoom - 0.001f, ZoomSteps[0]);
        if (Math.Abs(next - zoom) < 0.001f)
            return;
        Zoom = next;
        ApplyZoom();
    }
}
