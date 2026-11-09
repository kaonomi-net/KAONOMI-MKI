using System;
using System.Collections.Generic;
using Godot;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// A drop-down list of /work (font, size, paragraph style, language…): a button showing the choice, which opens its
/// list in a layer drawn inside the window itself. The popup of Godot's OptionButton is a sub-window, which the
/// viewport of a window of the desktop does not show; this list is a plain control of that viewport. Each item can be
/// shown in its own font, size and color (the fonts in themselves, the paragraph styles as they look).
/// </summary>
public partial class Dropdown : Button
{
    private readonly List<string> _items = [];
    private int _selected = -1;

    public Dropdown()
    {
        Alignment = HorizontalAlignment.Left;
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        ClipText = true;
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        // A rounded pop-up button in the manner of macOS: a light face, a hairline edge, a soft shadow, the chevron on the right.
        StyleBoxFlat Face(float edge)
        {
            var box = new StyleBoxFlat
            {
                BgColor = Eve.Eve.IsLight ? Colors.White : new Color(1, 1, 1, 0.08f),
                BorderColor = Eve.Eve.IsLight ? new Color(0, 0, 0, edge) : new Color(1, 1, 1, edge),
                AntiAliasing = true,
                ShadowColor = new Color(0, 0, 0, Eve.Eve.IsLight ? 0.06f : 0.2f),
                ShadowSize = 1,
                ShadowOffset = new Vector2(0, 1)
            };
            box.SetBorderWidthAll(1);
            box.SetCornerRadiusAll(6);
            box.ContentMarginLeft = 8;
            box.ContentMarginRight = 22;
            box.ContentMarginTop = box.ContentMarginBottom = 2;
            return box;
        }
        AddThemeStyleboxOverride("normal", Face(0.14f));
        AddThemeStyleboxOverride("hover", Face(0.26f));
        AddThemeStyleboxOverride("pressed", Face(0.32f));
        AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        AddThemeFontSizeOverride("font_size", 11);
        AddThemeColorOverride("font_color", Eve.Eve.TextBright);
        AddThemeColorOverride("font_hover_color", Eve.Eve.TextBright);
        var arrow = Eve.Eve.Text_("⌄", Eve.Eve.Muted, 11);
        arrow.SetAnchorsAndOffsetsPreset(LayoutPreset.RightWide);
        arrow.OffsetLeft = -16;
        arrow.OffsetRight = -6;
        arrow.VerticalAlignment = VerticalAlignment.Center;
        arrow.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(arrow);
        Pressed += Open;
    }

    /// <summary>Raised when the user picks an item (not when <see cref="Selected"/> is set by code).</summary>
    public event Action<int>? ItemSelected;

    /// <summary>The look of an item in the list: its font, size and color (null: the usual ones).</summary>
    public Func<int, (Font? Font, int? Size, Color? Color)>? ItemLook { get; set; }

    /// <summary>Most height of the list.</summary>
    public float ListHeight { get; set; } = 360;

    public IReadOnlyList<string> Items => _items;

    public void SetItems(IEnumerable<string> items, int selected = -1)
    {
        _items.Clear();
        _items.AddRange(items);
        Selected = selected;
    }

    /// <summary>The item chosen (-1: none); setting it does not raise <see cref="ItemSelected"/>.</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            _selected = value >= 0 && value < _items.Count ? value : -1;
            Text = _selected >= 0 ? _items[_selected] : string.Empty;
        }
    }

    public string? SelectedText => _selected >= 0 ? _items[_selected] : null;

    private void Open()
    {
        if (GetViewport() is not { } viewport)
            return;
        var overlay = new Overlay();
        viewport.AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 0);
        Button? current = null;
        float height = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            var index = i;
            var item = new Button
            {
                Text = _items[i],
                Flat = true,
                Alignment = HorizontalAlignment.Left,
                FocusMode = FocusModeEnum.None,
                MouseDefaultCursorShape = CursorShape.PointingHand,
                ToggleMode = true,
                ButtonPressed = i == _selected
            };
            item.AddThemeStyleboxOverride("normal", new StyleBoxEmpty { ContentMarginLeft = 8, ContentMarginRight = 12, ContentMarginTop = 3, ContentMarginBottom = 3 });
            item.AddThemeStyleboxOverride("pressed", Eve.Eve.Box(Eve.Eve.Cyan with { A = 0.2f }, Eve.Eve.Cyan with { A = 0f }, 3, 0));
            var look = ItemLook?.Invoke(i);
            height += (look?.Size ?? 13) * 1.45f + 8;
            if (look is var (font, size, color))
            {
                if (font is not null)
                    item.AddThemeFontOverride("font", font);
                if (size is { } s)
                    item.AddThemeFontSizeOverride("font_size", s);
                if (color is { } c)
                {
                    item.AddThemeColorOverride("font_color", c);
                    item.AddThemeColorOverride("font_hover_color", c.Lightened(0.15f));
                    item.AddThemeColorOverride("font_pressed_color", c.Lightened(0.15f));
                }
            }
            item.Pressed += () =>
            {
                overlay.Close();
                Selected = index;
                ItemSelected?.Invoke(index);
            };
            if (i == _selected)
                current = item;
            list.AddChild(item);
        }
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(Math.Max(Size.X, 160), Math.Min(ListHeight, Math.Max(40, height)))
        };
        scroll.AddChild(list);
        list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        overlay.Show(scroll, this, closed: () => overlay.QueueFree());
        if (current is not null)
            Callable.From(() =>
            {
                if (IsInstanceValid(scroll) && IsInstanceValid(current))
                    scroll.EnsureControlVisible(current);
            }).CallDeferred();
    }
}
