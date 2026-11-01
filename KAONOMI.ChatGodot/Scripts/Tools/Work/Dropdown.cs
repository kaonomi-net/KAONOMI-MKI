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
        AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Field, Eve.Eve.Edge with { A = 0.5f }, 6));
        AddThemeStyleboxOverride("hover", Eve.Eve.Box(Eve.Eve.Cyan with { A = 0.1f }, Eve.Eve.Cyan with { A = 0.8f }, 6));
        AddThemeColorOverride("font_color", Eve.Eve.TextBright);
        var arrow = Eve.Eve.Text_("▾", Eve.Eve.Muted, 12);
        arrow.SetAnchorsAndOffsetsPreset(LayoutPreset.RightWide);
        arrow.OffsetLeft = -18;
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
