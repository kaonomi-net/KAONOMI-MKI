using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Work;

/// <summary>The look of an item of a <see cref="Dropdown"/>: its font, size, color and weight (null: the usual ones).</summary>
public readonly record struct DropdownLook(FontFamily? Font = null, double? Size = null, Color? Color = null, bool Bold = false);

/// <summary>
/// A drop-down list of /work (font, size, paragraph style, language…), a combo box of strings whose items can each be
/// shown in their own font, size and color (the fonts in themselves, the paragraph styles as they look).
/// <see cref="ItemSelected"/> is raised when the user picks an item, not when <see cref="Selected"/> is set by code.
/// </summary>
public sealed class Dropdown : ComboBox
{
    private readonly List<string> _items = [];
    private bool _updating;

    protected override Type StyleKeyOverride => typeof(ComboBox);

    public Dropdown()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        MaxDropDownHeight = 360;
        SelectionChanged += (_, _) =>
        {
            if (!_updating && SelectedIndex >= 0)
                ItemSelected?.Invoke(SelectedIndex);
        };
    }

    /// <summary>Raised when the user picks an item (not when <see cref="Selected"/> is set by code).</summary>
    public event Action<int>? ItemSelected;

    /// <summary>The look of an item in the list (set before <see cref="SetItems"/>).</summary>
    public Func<int, DropdownLook>? ItemLook { get; set; }

    public IReadOnlyList<string> Texts => _items;

    public void SetItems(IEnumerable<string> items, int selected = -1)
    {
        _updating = true;
        try
        {
            _items.Clear();
            _items.AddRange(items);
            var built = new List<ComboBoxItem>(_items.Count);
            for (var i = 0; i < _items.Count; i++)
            {
                var item = new ComboBoxItem { Content = _items[i] };
                if (ItemLook?.Invoke(i) is { } look)
                {
                    if (look.Font is { } font)
                        item.FontFamily = font;
                    if (look.Size is { } size)
                        item.FontSize = size;
                    if (look.Color is { } color)
                        item.Foreground = WorkPalette.BrushOf(color);
                    if (look.Bold)
                        item.FontWeight = FontWeight.Bold;
                }
                built.Add(item);
            }
            ItemsSource = built;
            SelectedIndex = selected >= 0 && selected < _items.Count ? selected : -1;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>The item chosen (-1: none); setting it does not raise <see cref="ItemSelected"/>.</summary>
    public int Selected
    {
        get => SelectedIndex;
        set
        {
            _updating = true;
            try
            {
                SelectedIndex = value >= 0 && value < _items.Count ? value : -1;
            }
            finally
            {
                _updating = false;
            }
        }
    }

    public string? SelectedText => SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex] : null;
}
