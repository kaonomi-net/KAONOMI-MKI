using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Themes;

/// <summary>
/// The screen of the themes (🎨 at the bottom of the bar of commands), as in the Godot chat: one card per theme with a
/// small preview drawn in its colours (its page, a panel with a title, a text and a button, its colours of state), its
/// name, its description and APPLY (applied at once, kept for the next start; the windows stay as they are).
/// </summary>
public static class ThemeScreen
{
    /// <summary>The key of its window.</summary>
    public const string Key = "themes";

    public const string Icon = "🎨";

    /// <summary>Builds the screen; it follows the theme applied (from here or with /theme:name).</summary>
    public static Control Build()
    {
        var cards = new WrapPanel { Orientation = Orientation.Horizontal };
        void Fill()
        {
            cards.Children.Clear();
            foreach (var palette in DeskTheme.All)
                cards.Children.Add(Card(palette));
        }
        Fill();
        Action<Palette> changed = _ => Fill();
        var column = Ui.Column(10,
            Ui.Caption("Choose the look of the chat", Ui.TextBright, 13),
            Ui.Label("The theme is kept for the next start. The windows open stay open. /theme:name in the chat applies one too.", Ui.Muted, 12, wrap: true),
            cards);
        column.Margin = new Thickness(14);
        var scroll = Ui.Scroll(column);
        scroll.AttachedToVisualTree += (_, _) => DeskTheme.Changed += changed;
        scroll.DetachedFromVisualTree += (_, _) => DeskTheme.Changed -= changed;
        return scroll;
    }

    private static Control Card(Palette palette)
    {
        var inUse = palette == DeskTheme.Current;
        var apply = Ui.Button(inUse ? "IN USE" : "APPLY", () => DeskTheme.Apply(palette), primary: !inUse);
        apply.IsEnabled = !inUse;
        apply.HorizontalAlignment = HorizontalAlignment.Left;
        var description = Ui.Label(palette.Description, Ui.Muted, 12, wrap: true);
        return new Border
        {
            Width = 316,
            Margin = new Thickness(0, 0, 14, 14),
            Padding = new Thickness(10),
            Background = Ui.Field,
            BorderBrush = inUse ? Ui.Accent : Ui.Edge,
            BorderThickness = new Thickness(inUse ? 2 : 1),
            Child = Ui.Column(8, Preview(palette), Ui.Caption(palette.Name, inUse ? Ui.Accent : Ui.TextBright, 14), description, apply)
        };
    }

    /// <summary>A small picture of the theme drawn with its own colours and fonts.</summary>
    private static Control Preview(Palette palette)
    {
        var display = new FontFamily(string.Join(", ", palette.DisplayFonts));
        TextBlock Label(string text, Color color, double size, bool title = false) => new()
        {
            Text = text,
            Foreground = new SolidColorBrush(color),
            FontSize = size,
            FontFamily = title ? display : new FontFamily(string.Join(", ", palette.BodyFonts)),
            FontWeight = title ? (FontWeight)palette.DisplayWeight : FontWeight.Normal
        };
        var chip = new Border
        {
            Background = new SolidColorBrush(palette.Accent),
            Padding = new Thickness(8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Label("SEND", palette.IsLight ? Colors.White : palette.Space, 10, title: true)
        };
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        foreach (var color in new[] { palette.Accent, palette.Warm, palette.Green, palette.Red, palette.Muted })
            swatches.Children.Add(new Border { Width = 22, Height = 8, Background = new SolidColorBrush(color) });
        var panel = new Border
        {
            Background = new SolidColorBrush(palette.PanelSolid),
            BorderBrush = new SolidColorBrush(palette.Accent),
            BorderThickness = new Thickness(1, 3, 1, 1),
            Padding = new Thickness(10),
            Child = Ui.Column(4, Label("KAONOMI", palette.TextBright, 13, title: true), Label("The chat, the tools, the desk", palette.Text, 10), chip, swatches)
        };
        return new Border
        {
            Height = 130,
            Padding = new Thickness(12),
            Background = new SolidColorBrush(palette.Space),
            BorderBrush = new SolidColorBrush(palette.Edge),
            BorderThickness = new Thickness(1),
            Child = panel
        };
    }
}
