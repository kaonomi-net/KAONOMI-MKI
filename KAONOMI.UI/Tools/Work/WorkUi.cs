using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Work;

/// <summary>The small helpers of the views of /work: the window holding a control, its title, the look of a few controls.</summary>
public static class WorkUi
{
    /// <summary>The window of the desktop holding <paramref name="control"/> (null while it is not shown).</summary>
    public static DeskWindow? WindowOf(Control? control) => control?.FindAncestorOfType<DeskWindow>();

    /// <summary>True when <paramref name="window"/> is the one in front.</summary>
    public static bool IsFront(Shell shell, DeskWindow window) => ReferenceEquals(shell.Deck.Front, window);

    /// <summary>
    /// Changes the title shown in the header of <paramref name="window"/> (the title of a desk window is fixed when it
    /// opens: the text of its header is changed instead).
    /// </summary>
    public static void SetTitle(DeskWindow window, string title)
    {
        var header = $"{window.Icon}  ";
        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
            if (text.Text is { } current && current.StartsWith(header, StringComparison.Ordinal))
            {
                text.Text = header + title;
                return;
            }
    }

    /// <summary>Runs <paramref name="action"/> once the current layout pass is over.</summary>
    public static void Later(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

    /// <summary>Runs <paramref name="action"/> after <paramref name="seconds"/>.</summary>
    public static void After(double seconds, Action action) => DispatcherTimer.RunOnce(action, TimeSpan.FromSeconds(seconds));

    /// <summary>An empty space taking the free width of a dock panel (pushes what follows to the right).</summary>
    public static Control Spring() => new Panel { HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>A row with <paramref name="fill"/> taking the free width and <paramref name="right"/> at its right.</summary>
    public static DockPanel FillRow(double spacing, Control fill, params Control[] right)
    {
        var dock = new DockPanel { LastChildFill = true };
        var row = Ui.Row(spacing, right);
        row.Margin = new Thickness(spacing, 0, 0, 0);
        DockPanel.SetDock(row, Avalonia.Controls.Dock.Right);
        dock.Children.Add(row);
        dock.Children.Add(fill);
        return dock;
    }

    /// <summary>A caption above <paramref name="field"/>.</summary>
    public static StackPanel Field(string caption, Control field) => Ui.Column(3, Ui.Caption(caption), field);

    /// <summary>A heading of a result window.</summary>
    public static TextBlock Heading(string text) => Ui.Label(text, Ui.TextBright, 18, bold: true);

    /// <summary>A text shown in the middle of an empty area.</summary>
    public static TextBlock Placeholder(string text)
    {
        var label = Ui.Label(text, Ui.Muted, 13, wrap: true);
        label.TextAlignment = TextAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }

    /// <summary>A horizontal rule.</summary>
    public static Border Separator() => new() { Height = 1, Background = Ui.Edge, Margin = new Thickness(0, 2) };

    /// <summary>True for the key of the commands: ⌘ on macOS, Ctrl elsewhere.</summary>
    public static bool IsCommand(Avalonia.Input.KeyModifiers modifiers) =>
        OperatingSystem.IsMacOS() ? modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta) : modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control);

    /// <summary>The "Ctrl+" or "⌘" in front of the shortcuts shown.</summary>
    public static string ShortcutPrefix => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
}
