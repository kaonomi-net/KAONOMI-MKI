using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Work;

/// <summary>
/// The look of the big tools of the ribbon of the tape. The Godot chat drew a 3D shelf of glass slabs under them (a
/// scene with lights and a shader); this version is flat: a tool is a plain tile, with an edge while the pointer is on
/// it, and lit in the accent color while its state is on (the link at the caret, the crop frame shown).
/// </summary>
public static class RibbonShelf
{
    /// <summary>Gives <paramref name="tool"/> the look of a tile of the ribbon.</summary>
    public static void Track(Button tool)
    {
        tool.Background = Brushes.Transparent;
        tool.BorderBrush = Brushes.Transparent;
        tool.BorderThickness = new Thickness(1);
        tool.CornerRadius = new CornerRadius(2);
        tool.PointerEntered += (_, _) =>
        {
            if (!IsOn(tool))
                tool.BorderBrush = Ui.Edge;
        };
        tool.PointerExited += (_, _) =>
        {
            if (!IsOn(tool))
                tool.BorderBrush = Brushes.Transparent;
        };
    }

    private static bool IsOn(Button tool) => tool.Classes.Contains("on");

    /// <summary>Lights <paramref name="tool"/> while its state is on.</summary>
    public static void SetOn(Button tool, bool on)
    {
        tool.Classes.Set("on", on);
        tool.Background = on ? Ui.Header : Brushes.Transparent;
        tool.BorderBrush = on ? Ui.Accent : Brushes.Transparent;
    }
}
