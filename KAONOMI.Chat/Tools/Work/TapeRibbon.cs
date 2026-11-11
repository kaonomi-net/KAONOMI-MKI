using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.Chat.Views;
using KAONOMI.UI.Desk;
using Environment = System.Environment;

namespace KAONOMI.UI.Tools.Work;

/// <summary>
/// The ribbon of the rich text editor (<see cref="TapeEditor"/>) in the manner of Word, shared by /work and the journal:
/// its tabs (Home, Insert, View) of named groups, and the groups every window with the editor has. Font: family, size,
/// B I U S, color and clear formatting of the selection; Paragraph: the alignment; Styles: Normal, Title, Subtitle,
/// Heading 1 to 3; Pictures: insert, generate with Fooocus, crop the selected picture; Links; Code: insert, generate with
/// the LLM (a double-click on a block of code edits it); Zoom (also Ctrl / ⌘ + wheel). The right click on the editor:
/// correct the spelling, rewrite for readability, evaluate, translate (by the LLM of <see cref="ITapeAssistant"/>),
/// annotate, cut, copy, paste. The window adds its own groups (the documents of /work…) around these.
/// </summary>
public sealed class TapeRibbon
{
    private const string DefaultFontName = "Default";
    private static readonly int[] Sizes = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72];

    /// <summary>Colors offered for the text (null: the default color of the tape).</summary>
    private static readonly string?[] TextColors =
        [null, "#000000", "#4B5563", "#9CA3AF", "#DC2626", "#EA580C", "#D97706", "#CA8A04", "#16A34A", "#059669",
         "#0891B2", "#2563EB", "#4F46E5", "#7C3AED", "#C026D3", "#DB2777"];

    /// <summary>Zoom steps of − and +, 50 % to 300 %.</summary>
    private static readonly double[] ZoomSteps = [0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3];

    /// <summary>Zoom of the page of each kind of window (/work, the journal), kept while the chat runs.</summary>
    private static readonly Dictionary<string, double> Zooms = new();

    private readonly TapeEditor _editor;
    private readonly Overlay _overlay;
    private readonly Shell _shell;
    private readonly Panel _host;
    private readonly TextBlock _status;
    private readonly ITapeAssistant? _assistant;
    private readonly string _logCategory;
    private readonly string _subject;
    private readonly string _zoomKey;
    private readonly Func<string, string> _cropDirectory;

    // The ribbon controls the toolbar keeps in step with the caret (null while their group is not built).
    private readonly Dropdown _fontBox = new() { Width = 170 };
    private readonly Dropdown _sizeBox = new() { Width = 72 };
    private readonly Dropdown _paragraphBox = new() { Width = 150 };
    private readonly List<string> _fonts = [];
    private List<int> _sizes = [.. Sizes];
    private bool _hasFont, _hasStyles;
    private ToggleButton? _bold, _italic, _underline, _strike;
    private ToggleButton? _alignLeft, _alignCenter, _alignRight;
    private Border? _colorBar;
    private Button? _link, _crop;
    private TextBlock? _cropLabel;
    private Button? _zoomOut, _zoomIn, _zoomLabel;
    private bool _updating;
    private int _statusVersion;

    /// <summary>
    /// The ribbon of <paramref name="editor"/>: its flyouts in <paramref name="overlay"/>, the waiting popups over
    /// <paramref name="host"/>, its short messages in <paramref name="status"/>; the LLM and Fooocus of
    /// <paramref name="assistant"/> (none: their tools are hidden); <paramref name="logCategory"/> and
    /// <paramref name="subject"/> ("the tape") name it in the activity log; <paramref name="zoomKey"/> keeps its zoom;
    /// a cropped picture is written in <paramref name="cropDirectory"/> of the path of the picture.
    /// </summary>
    public TapeRibbon(TapeEditor editor, Overlay overlay, Shell shell, Panel host, TextBlock status, ITapeAssistant? assistant,
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
        ToolTip.SetTip(_fontBox, "Font of the selection");
        ToolTip.SetTip(_sizeBox, "Size of the selection");
        ToolTip.SetTip(_paragraphBox, "Style of the paragraph: Normal, Title, Subtitle, Heading 1, 2, 3 (Ctrl / ⌘ + Alt / ⌥ + 1, 2, 3; 0 for Normal)");

        _editor.Zoom = Zoom;
        _editor.OpenLink = OpenLink;
        _editor.CropImage = CropImageAsync;
        _editor.SelectionChanged += UpdateToolbar;
        _editor.CropChanged += UpdateToolbar;
        _editor.ContextMenuRequested += ShowContextMenu;
        _editor.EditCodeRequested += OnEditCode;
        _editor.ZoomRequested += StepZoom;
    }

    private double Zoom
    {
        get => Zooms.GetValueOrDefault(_zoomKey, 1);
        set => Zooms[_zoomKey] = value;
    }

    /// <summary>The LLM can be asked (evaluate, translate, rewrite, generate code).</summary>
    public bool CanEvaluate => _assistant?.CanEvaluate == true;

    // ───────────── Ribbon ─────────────

    /// <summary>The ribbon: its tabs as a line of buttons (the one in use underlined), under it the groups of the tab.</summary>
    public Control Build(params (string Name, Control[] Groups)[] tabs) => Build(null, tabs);

    /// <summary>The ribbon, with <paramref name="trailing"/> (the status of the window) at the right of its tabs.</summary>
    public Control Build(Control? trailing, params (string Name, Control[] Groups)[] tabs)
    {
        var strips = tabs.Select(tab => (Control)Ui.Row(6, tab.Groups)).ToArray();
        var holder = new Panel();
        foreach (var strip in strips)
            holder.Children.Add(strip);
        var tabButtons = new List<Button>();
        var line = new DockPanel { LastChildFill = true };
        var tabRow = Ui.Row(2);
        void Select(int index)
        {
            for (var i = 0; i < strips.Length; i++)
            {
                strips[i].IsVisible = i == index;
                TabLook(tabButtons[i], i == index);
            }
        }
        for (var i = 0; i < tabs.Length; i++)
        {
            var index = i;
            var tab = new Button
            {
                Content = tabs[i].Name.ToUpperInvariant(),
                Focusable = false,
                FontSize = 11,
                MinHeight = 26,
                Padding = new Thickness(12, 4),
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(0)
            };
            tab.Click += (_, _) => Select(index);
            tabButtons.Add(tab);
            tabRow.Children.Add(tab);
        }
        DockPanel.SetDock(tabRow, Avalonia.Controls.Dock.Left);
        line.Children.Add(tabRow);
        if (trailing is not null)
        {
            trailing.VerticalAlignment = VerticalAlignment.Center;
            trailing.HorizontalAlignment = HorizontalAlignment.Right;
            trailing.Margin = new Thickness(12, 0, 4, 0);
            line.Children.Add(trailing);
        }
        Select(0);
        var ribbonScroll = new ScrollViewer
        {
            Content = holder,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MinHeight = 100
        };

        var panel = new Border
        {
            Background = Ui.Header,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 4, 10, 6),
            Child = Ui.Column(4, line, ribbonScroll)
        };
        ApplyZoom();
        UpdateToolbar();
        return panel;
    }

    /// <summary>A tab of the ribbon: capitals, the one in use bright with a line under it.</summary>
    private static void TabLook(Button tab, bool active)
    {
        tab.Foreground = active ? Ui.TextBright : Ui.Muted;
        tab.BorderBrush = active ? Ui.Accent : Brushes.Transparent;
        tab.BorderThickness = new Thickness(0, 0, 0, 2);
    }

    /// <summary>A group of the ribbon: its tools in a framed box, its name in small capitals under it.</summary>
    public static Control Group(string name, bool last, params Control[] tools)
    {
        foreach (var tool in tools)
            tool.VerticalAlignment = VerticalAlignment.Center;
        var row = Ui.Row(4, tools);
        row.VerticalAlignment = VerticalAlignment.Center;
        var well = new Border
        {
            Child = row,
            Padding = new Thickness(6),
            Background = Ui.Panel,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            MinHeight = 74
        };
        var label = Ui.Caption(name, Ui.Muted, 9);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var column = Ui.Column(3, well, label);
        column.MinHeight = 90;
        column.Margin = new Thickness(2, 2, 2, 0);
        return column;
    }

    /// <summary>A big tool: its icon above its name (the name is the <see cref="Control.Tag"/> of the button).</summary>
    public static Button BigTool(string icon, string label, string tooltip, Action? action = null, IBrush? iconColor = null)
    {
        var glyph = Ui.Label(icon, iconColor ?? Ui.TextBright, 22);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var name = Ui.Label(label, Ui.Text, 11);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        var button = new Button
        {
            Content = Ui.Column(2, glyph, name),
            MinWidth = 56,
            Height = 62,
            Focusable = false,
            Padding = new Thickness(7, 4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = name
        };
        ToolTip.SetTip(button, tooltip);
        RibbonShelf.Track(button);
        if (action is not null)
            button.Click += (_, _) => action();
        return button;
    }

    /// <summary>A small tool of the font and paragraph groups (a toggle button with <paramref name="toggle"/>).</summary>
    public static Button SmallTool(string text, string tooltip, Action action, bool toggle = false, FontWeight? weight = null, FontStyle? style = null)
    {
        Button button = toggle ? new ToggleButton() : new Button();
        button.Content = text;
        button.MinWidth = 32;
        button.MinHeight = 28;
        button.Padding = new Thickness(6, 2);
        button.Focusable = false;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        if (weight is { } w)
            button.FontWeight = w;
        if (style is { } s)
            button.FontStyle = s;
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>A big tool lit while its state is on (the link at the caret, the crop frame shown).</summary>
    public static void SetOn(Button button, bool on) => RibbonShelf.SetOn(button, on);

    /// <summary>Font: family, size, B I U S, color, clear.</summary>
    public Control FontGroup(bool last = false)
    {
        _hasFont = true;
        _fonts.Add(DefaultFontName);
        try
        {
            _fonts.AddRange(FontManager.Current.SystemFonts.Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.StartsWith('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // The fonts of the system cannot be listed: the default one only.
        }
        _fontBox.ItemLook = index => new DropdownLook(FontOf(_fonts[index]));
        _fontBox.SetItems(_fonts);
        _fontBox.ItemSelected += index =>
        {
            if (_updating || index < 0 || index >= _fonts.Count)
                return;
            var font = _fonts[index];
            _editor.ApplyStyle(s => s with { Font = font == DefaultFontName ? null : font });
        };
        FillSizes();
        _sizeBox.ItemSelected += index =>
        {
            if (_updating || index < 0 || index >= _sizes.Count)
                return;
            var size = _sizes[index];
            var styleSize = TapeParagraph.KindStyle(_editor.CurrentParagraphKind).Size ?? TapeEditor.DefaultSize;
            _editor.ApplyStyle(s => s with { Size = Math.Abs(size - styleSize) < 0.01 ? null : size });
        };
        ToggleButton bold = null!, italic = null!, underline = null!, strike = null!;
        bold = (ToggleButton)SmallTool("B", "Bold (Ctrl / ⌘ + B)", () => _editor.ApplyStyle(s => s with { Bold = bold.IsChecked == true }), true, FontWeight.ExtraBold);
        italic = (ToggleButton)SmallTool("I", "Italic (Ctrl / ⌘ + I)", () => _editor.ApplyStyle(s => s with { Italic = italic.IsChecked == true }), true, style: FontStyle.Italic);
        italic.FontFamily = new FontFamily("Georgia, Times New Roman, serif");
        underline = (ToggleButton)SmallTool("U̲", "Underline (Ctrl / ⌘ + U)", () => _editor.ApplyStyle(s => s with { Underline = underline.IsChecked == true }), true);
        strike = (ToggleButton)SmallTool("a̶b̶", "Strikethrough", () => _editor.ApplyStyle(s => s with { Strike = strike.IsChecked == true }), true);
        (_bold, _italic, _underline, _strike) = (bold, italic, underline, strike);
        _colorBar = new Border { Width = 16, Height = 3, Background = WorkPalette.BrushOf(WorkPalette.DefaultText), HorizontalAlignment = HorizontalAlignment.Center };
        var colorGlyph = new TextBlock { Text = "A", FontWeight = FontWeight.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center };
        var color = SmallTool("A", "Font color of the selection", () => { });
        color.Content = Ui.Column(0, colorGlyph, _colorBar);
        color.Click += (_, _) => ShowColorFlyout(color);
        var clear = SmallTool("A✕", "Clear all formatting of the selection (font, size, styles, color)", () => _editor.ApplyStyle(_ => TapeStyle.Plain));
        clear.Foreground = Ui.Red;
        return Group("Font", last, Ui.Column(4, Ui.Row(4, _fontBox, _sizeBox), Ui.Row(1, bold, italic, underline, strike, color, clear)));
    }

    /// <summary>Paragraph: left, centered, right.</summary>
    public Control ParagraphGroup(bool last = false)
    {
        _alignLeft = (ToggleButton)SmallTool("≡◂", "Align left", () => Align(TapeAlignment.Left), true);
        _alignCenter = (ToggleButton)SmallTool("≡", "Center", () => Align(TapeAlignment.Center), true);
        _alignRight = (ToggleButton)SmallTool("▸≡", "Align right", () => Align(TapeAlignment.Right), true);
        return Group("Paragraph", last, _alignLeft, _alignCenter, _alignRight);
    }

    /// <summary>Styles: the style of the paragraph, each shown as it looks.</summary>
    public Control StylesGroup(bool last = false)
    {
        _hasStyles = true;
        _paragraphBox.ItemLook = index =>
        {
            var style = TapeParagraph.KindStyle(Enum.GetValues<TapeParagraphKind>()[index]);
            return new DropdownLook(null, Math.Min(style.Size ?? 13, 20), WorkPalette.Adapt(style.Color, WorkPalette.ColorOf(Ui.TextBright)), style.Bold);
        };
        _paragraphBox.SetItems(Enum.GetValues<TapeParagraphKind>().Select(ParagraphKindName));
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
        generate.IsVisible = _assistant?.CanGenerateImages == true;
        _crop = BigTool("✂", "Crop", "Crop the selected picture: drag the frame or its handles, then Enter (or click ✓ Crop) to crop, Escape to cancel; the original file is kept", () =>
        {
            if (_editor.IsCropping)
                _editor.ApplyCrop();
            else
                _editor.StartCrop();
        });
        _cropLabel = _crop.Tag as TextBlock;
        return Group("Pictures", last, pictures, generate, _crop);
    }

    /// <summary>Links: 🔗 makes the selection a link.</summary>
    public Control LinksGroup(bool last = false)
    {
        _link = BigTool("🔗", "Link", "Link: makes the selected text a clickable link (Ctrl / ⌘ + click opens it)");
        _link.Click += (_, _) => ShowLinkFlyout(_link);
        return Group("Links", last, _link);
    }

    /// <summary>Code: insert code, generate code with the LLM (hidden without it).</summary>
    public Control CodeGroup(bool last = true)
    {
        var code = BigTool("</>", "Code", "Insert code at the caret, shown with the syntax coloring of its language (a double-click on a block of code edits it)", InsertCode, Ui.Accent);
        var generateCode = BigTool("✨</>", "Generate", "Generate code with the LLM from a prompt, then insert it at the caret", OnGenerateCode, Ui.Accent);
        generateCode.IsVisible = CanEvaluate;
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
        _zoomLabel.MinWidth = 64;
        _zoomIn = SmallTool("+", "Zoom in the page (Ctrl / ⌘ + wheel up)", () => StepZoom(1));
        ApplyZoom();
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
    private static readonly Dictionary<string, FontFamily> FontPreviews = new();

    private static FontFamily FontOf(string name)
    {
        if (name == DefaultFontName)
            return WorkPalette.Text;
        if (!FontPreviews.TryGetValue(name, out var font))
            FontPreviews[name] = font = new FontFamily(name);
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
            if (_bold is not null) _bold.IsChecked = style.Bold;
            if (_italic is not null) _italic.IsChecked = style.Italic;
            if (_underline is not null) _underline.IsChecked = style.Underline;
            if (_strike is not null) _strike.IsChecked = style.Strike;
            if (_colorBar is not null)
                _colorBar.Background = WorkPalette.BrushOf(WorkPalette.Adapt(style.Color, WorkPalette.DefaultText));
            if (_link is not null)
                SetOn(_link, style.Link is not null);
            if (_crop is not null)
            {
                _crop.IsEnabled = _editor.IsImageSelected || _editor.IsCropping;
                SetOn(_crop, _editor.IsCropping);
                if (_cropLabel is not null)
                    _cropLabel.Text = _editor.IsCropping ? "✓ Crop" : "Crop";
            }
            var alignment = _editor.CurrentAlignment;
            if (_alignLeft is not null) _alignLeft.IsChecked = alignment == TapeAlignment.Left;
            if (_alignCenter is not null) _alignCenter.IsChecked = alignment == TapeAlignment.Center;
            if (_alignRight is not null) _alignRight.IsChecked = alignment == TapeAlignment.Right;
        }
        finally
        {
            _updating = false;
        }
    }

    // ───────────── Status ─────────────

    /// <summary>A short message in the status label, gone after a few seconds.</summary>
    public void ShowStatus(string message, IBrush? color = null, bool keep = false)
    {
        _status.Text = message;
        _status.Foreground = color ?? Ui.Green;
        var version = ++_statusVersion;
        if (keep)
            return;
        WorkUi.After(5, () =>
        {
            if (_statusVersion == version && _status.Text == message)
                _status.Text = string.Empty;
        });
    }

    /// <summary>Removes the message of the status label.</summary>
    public void ClearStatus()
    {
        _statusVersion++;
        _status.Text = string.Empty;
    }

    // ───────────── Flyouts ─────────────

    private void ShowColorFlyout(Control anchor)
    {
        var grid = new UniformGrid { Columns = 4 };
        foreach (var color in TextColors)
        {
            var shown = color is null ? Brushes.Transparent : WorkPalette.BrushOf(WorkPalette.Adapt(color, WorkPalette.DefaultText));
            var swatch = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(3),
                Padding = new Thickness(0),
                Focusable = false,
                Background = shown,
                BorderBrush = Ui.Edge,
                BorderThickness = new Thickness(1),
                Content = color is null ? "∅" : null,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(swatch, color ?? "Default color");
            var value = color;
            swatch.Click += (_, _) =>
            {
                _overlay.Close();
                _editor.ApplyStyle(s => s with { Color = value });
            };
            grid.Children.Add(swatch);
        }
        _overlay.Show(Ui.Column(6, Ui.Caption("Font color"), grid), anchor);
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
        var address = new TextBox { Width = 320, PlaceholderText = "https://… or a file path", Text = current ?? string.Empty };
        var hint = Ui.Label(selected
            ? "The selected text becomes a link to this address; Ctrl / ⌘ + click opens it."
            : "Nothing is selected: the address is added at the caret as a link.", Ui.Muted, 11, wrap: true);
        hint.Width = 320;
        void Apply()
        {
            _overlay.Close();
            _editor.SetLink(address.Text);
        }
        var apply = Ui.Button("APPLY", Apply, primary: true);
        var open = Ui.Button("OPEN", () =>
        {
            _overlay.Close();
            if (_editor.CurrentLink is { } link)
                OpenLink(link);
        });
        var remove = Ui.Button("REMOVE", () =>
        {
            _overlay.Close();
            _editor.SetLink(null);
        });
        open.IsEnabled = remove.IsEnabled = current is not null;
        address.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                Apply();
            }
        };
        _overlay.Show(Ui.Column(8, Ui.Caption("Link address"), address, hint, Ui.Row(6, apply, open, remove)), anchor);
        WorkUi.Later(() =>
        {
            address.Focus();
            address.SelectAll();
        });
    }

    // ───────────── Right-click menu ─────────────

    private void ShowContextMenu(Point at)
    {
        var hasText = _editor.HasSelectedContent && _editor.SelectedText.Length > 0;
        var busy = _editor.IsBusy;
        var shortcut = WorkUi.ShortcutPrefix;
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
            Annotate(_editor.SelectedText.Trim())));
        items.Add(new Overlay.MenuItem("Cut", _editor.HasSelectedContent, () => _editor.Copy(cut: true), shortcut + "X"));
        items.Add(new Overlay.MenuItem("Copy", _editor.HasSelectedContent, () => _editor.Copy(cut: false), shortcut + "C"));
        items.Add(new Overlay.MenuItem("Paste", true, _editor.Paste, shortcut + "V"));
        Overlay.Menu(items, _editor);
    }

    /// <summary>
    /// 📝 Annotate: the annotation of the text selected, written in the window of the editor (its view model gives the
    /// context of the note, as in the Godot chat).
    /// </summary>
    private void Annotate(string text)
    {
        var window = WorkUi.WindowOf(_editor);
        var title = window?.Title ?? "Work";
        var provider = window?.ViewModel as INoteContextProvider ?? (window is null ? _shell.Chat : null);
        var windowName = provider?.NoteWindowName ?? title;
        var target = AnnotationTarget.Text(text, title);
        var context = NoteContext.Build(provider, windowName);
        _shell.Open(new AnnotationDialogViewModel(KaonomiAssistantService.Shared, target, windowName, context) { SourceLink = window?.Key },
            title: $"Annotate · {target.Title ?? windowName}");
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
            ShowStatus("No LLM available for the evaluation", Ui.Red);
            return;
        }
        var model = ModelName;
        _editor.IsBusy = true;
        ShowStatus($"🧐 Evaluating with {model}…", Ui.Muted, keep: true);
        string? markdown = null, error = null;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var busy = BusyPopup.Show(_host, "Evaluation", $"{model} evaluates the text…", cancel);
        var cancelled = false;
        try
        {
            markdown = await assistant.EvaluateAsync(text, cancel.Token);
        }
        catch (OperationCanceledException) when (busy.IsCancelledByUser)
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
            _editor.IsBusy = false;
            ClearStatus();
        }
        if (cancelled)
        {
            ShowStatus("Evaluation cancelled", Ui.Muted);
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
            ShowStatus("No LLM available for the translation", Ui.Red);
            return;
        }
        var model = ModelName;
        _editor.IsBusy = true;
        ShowStatus($"🌐 Translating into {language} with {model}…", Ui.Muted, keep: true);
        string? translation = null, error = null;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var busy = BusyPopup.Show(_host, "Translation", $"{model} translates the text into {language}…", cancel);
        var cancelled = false;
        try
        {
            translation = await assistant.TranslateAsync(text, language, cancel.Token);
        }
        catch (OperationCanceledException) when (busy.IsCancelledByUser)
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
            _editor.IsBusy = false;
            ClearStatus();
        }
        if (cancelled)
        {
            ShowStatus("Translation cancelled", Ui.Muted);
            return;
        }
        WorkWindows.Translation(_shell, text, language, model, translation, error,
            result => _editor.ReplaceSelection(selection, result),
            result => _editor.InsertAfter(selection, result));
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
            ShowStatus("No LLM available for the rewriting", Ui.Red);
            return;
        }
        var spelling = mode == TapeEditor.RewriteSpelling;
        var model = ModelName;
        _editor.IsBusy = true;
        ShowStatus(spelling ? $"✏️ Correcting the spelling with {model}…" : $"✨ Rewriting for readability with {model}…", Ui.Muted, keep: true);
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
                busy.Message = $"Language: {language} · {model} checks and corrects the spelling…";
            }
            result = await assistant.RewriteAsync(text, mode, cancel.Token, language);
        }
        catch (OperationCanceledException) when (busy.IsCancelledByUser)
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
            _editor.IsBusy = false;
            ClearStatus();
        }
        if (cancelled)
        {
            ShowStatus(spelling ? "Spelling correction cancelled" : "Rewriting cancelled", Ui.Muted);
            return;
        }
        WorkWindows.Translation(_shell, text, "", model, result, error,
            corrected => _editor.ReplaceSelection(selection, corrected),
            corrected => _editor.InsertAfter(selection, corrected),
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
            _editor.InsertImages([path]);
            ShowStatus("✓ Image inserted");
        });
    }

    /// <summary>The part <paramref name="part"/> (0 to 1) of the picture, saved as a PNG (name-crop-date.png; the original kept).</summary>
    private async Task<string?> CropImageAsync(string path, Rect part)
    {
        Bitmap? image = null;
        try
        {
            if (System.IO.File.Exists(path))
            {
                try
                {
                    image = new Bitmap(path);
                }
                catch (Exception)
                {
                    // A format Avalonia does not read (HEIC…): decoded by the system below.
                }
            }
            image ??= await PictureLibrary.DecodeAsync(path, 4096, CancellationToken.None);
            if (image is null)
            {
                ShowStatus($"Not cropped: {System.IO.Path.GetFileName(path)} cannot be read", Ui.Red);
                return null;
            }
            var pixelWidth = image.PixelSize.Width;
            var pixelHeight = image.PixelSize.Height;
            var width = Math.Max(1, (int)Math.Round(part.Width * pixelWidth));
            var height = Math.Max(1, (int)Math.Round(part.Height * pixelHeight));
            var x = Math.Clamp((int)Math.Round(part.X * pixelWidth), 0, pixelWidth - 1);
            var y = Math.Clamp((int)Math.Round(part.Y * pixelHeight), 0, pixelHeight - 1);
            width = Math.Min(width, pixelWidth - x);
            height = Math.Min(height, pixelHeight - y);
            var directory = _cropDirectory(path);
            System.IO.Directory.CreateDirectory(directory);
            var file = System.IO.Path.Combine(directory, $"{System.IO.Path.GetFileNameWithoutExtension(path)}-crop-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            using (var target = new RenderTargetBitmap(new PixelSize(width, height)))
            {
                var dpi = image.Dpi;
                var scaleX = dpi.X / 96;
                var scaleY = dpi.Y / 96;
                using (var context = target.CreateDrawingContext())
                    context.DrawImage(image, new Rect(x / scaleX, y / scaleY, width / scaleX, height / scaleY), new Rect(0, 0, width, height));
                target.Save(file, PngBitmapEncoderOptions.Default);
            }
            ShowStatus("✓ Cropped");
            ActivityLog.Info(_logCategory, $"Picture of {_subject} cropped: {file} ({width}×{height}, from {path})");
            return file;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowStatus($"Not cropped: {ex.Message}", Ui.Red);
            return null;
        }
        finally
        {
            image?.Dispose();
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
                PictureLibrary.OpenWithSystem(uri.ToString());
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
                ShowStatus($"Link not opened: {reason}", Ui.Red);
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
            ShowStatus($"Link not opened: {ex.Message}", Ui.Red);
        }
    }

    // ───────────── Code ─────────────

    /// <summary>The </> window: code inserted at the caret.</summary>
    public void InsertCode() =>
        WorkWindows.Code(_shell, "Insert code", "Insert", "python", string.Empty, (language, code) => _editor.InsertCode(language, code));

    private void OnGenerateCode()
    {
        if (_assistant is not { } assistant)
            return;
        WorkWindows.Code(_shell, $"Generate code · {ModelName}", "Insert", "auto", string.Empty, (language, code) =>
        {
            _editor.InsertCode(language, code);
            ShowStatus("✓ Code inserted");
        }, assistant.GenerateCodeAsync);
    }

    private void OnEditCode(string language, string code, Func<string, string, bool> replace) =>
        WorkWindows.Code(_shell, "Edit code", "Update", language, code, (edited, text) =>
        {
            if (!replace(edited, text))
                ShowStatus($"Not updated: {_subject} changed meanwhile", Ui.Warm);
        });

    // ───────────── Zoom ─────────────

    private void ApplyZoom()
    {
        _editor.Zoom = Zoom;
        if (_zoomLabel is null || _zoomOut is null || _zoomIn is null)
            return;
        _zoomLabel.Content = $"{Zoom * 100:0} %";
        _zoomOut.IsEnabled = Zoom > ZoomSteps[0] + 0.001;
        _zoomIn.IsEnabled = Zoom < ZoomSteps[^1] - 0.001;
    }

    private void StepZoom(int direction)
    {
        var zoom = Zoom;
        var next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > zoom + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < zoom - 0.001, ZoomSteps[0]);
        if (Math.Abs(next - zoom) < 0.001)
            return;
        Zoom = next;
        ApplyZoom();
    }
}
