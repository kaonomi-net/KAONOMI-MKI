using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Daily;

/// <summary>
/// The small helpers of the views of the "daily" tools (alarms, calendar, games), in the manner of the helpers of the
/// Godot chat (Eve/Ui.cs, Writing/WritingUi.cs): a toolbar, a status bar, a field with its caption, a list of controls
/// kept in step with an observable collection, a content swapped with a property, a row that selects, shortcuts.
/// </summary>
internal static class DailyUi
{
    /// <summary>The bar at the top of a tool: <paramref name="left"/> from the left, <paramref name="right"/> at the right.</summary>
    public static Border Toolbar(Control[] left, Control[]? right = null)
    {
        var dock = new DockPanel { LastChildFill = true };
        if (right is { Length: > 0 })
        {
            var r = Ui.Row(8, right);
            r.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(r, Avalonia.Controls.Dock.Right);
            dock.Children.Add(r);
        }
        var l = Ui.Row(8, left);
        l.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(l);
        return new Border
        {
            Child = dock,
            Background = Ui.Header,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6)
        };
    }

    /// <summary>The bar at the bottom of a tool: the text of <paramref name="status"/>, red while <paramref name="error"/> is true.</summary>
    public static Border StatusBar(INotifyPropertyChanged source, string status, string? error = null)
    {
        var label = Ui.Bound(source, status, Ui.Muted, 11);
        if (error is not null)
            label.Bind(TextBlock.ForegroundProperty, source, error, BindingMode.OneWay, v => v is true ? Ui.Red : Ui.Muted);
        return new Border
        {
            Child = label,
            Background = Ui.Header,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 4)
        };
    }

    /// <summary>A field with its caption above.</summary>
    public static StackPanel Field(string caption, Control field) => Ui.Column(3, Ui.Caption(caption), field);

    /// <summary>A text of a free space ("No alarm: ✚ NEW adds one").</summary>
    public static TextBlock Placeholder(string text)
    {
        var label = Ui.Label(text, Ui.Muted, 13, wrap: true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextAlignment = TextAlignment.Center;
        label.Margin = new Thickness(16);
        return label;
    }

    public static Border Separator() => new() { Height = 1, Background = Ui.Edge, Margin = new Thickness(0, 4) };

    /// <summary>A text in the monospaced font.</summary>
    public static TextBlock Mono(string text, IBrush? color = null, double size = 13)
    {
        var label = Ui.Label(text, color, size);
        label.FontFamily = Ui.Mono;
        return label;
    }

    /// <summary>A small coloured badge ("TRAINING").</summary>
    public static Border Badge(string text, IBrush color) => new()
    {
        Child = Ui.Label(text, color, 10, bold: true),
        BorderBrush = color,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(2),
        Padding = new Thickness(6, 1),
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>A drop-down of <paramref name="items"/> bound both ways to the string property <paramref name="name"/>.</summary>
    public static ComboBox Choice(object source, string name, IEnumerable<string> items, double minWidth = 120)
    {
        var combo = Ui.Combo(items.ToList(), source, name);
        combo.MinWidth = minWidth;
        return combo;
    }

    /// <summary>A toggle button bound both ways to the bool property <paramref name="name"/>.</summary>
    public static ToggleButton Toggle(string text, object source, string name, string? tooltip = null)
    {
        var button = new ToggleButton { Content = text, Focusable = false };
        if (tooltip is not null)
            ToolTip.SetTip(button, tooltip);
        return button.Bind(ToggleButton.IsCheckedProperty, source, name, BindingMode.TwoWay);
    }

    /// <summary>Two panes side by side with a splitter between them, the left one <paramref name="leftWidth"/> wide at first.</summary>
    public static Grid SplitH(Control left, Control right, double leftWidth)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{leftWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)},4,*") };
        var splitter = new GridSplitter { Background = Ui.Edge, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(splitter);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>
    /// A page: the rows given, the one at <paramref name="fill"/> taking the space left (a Grid of Auto rows and a star row).
    /// </summary>
    public static Grid Page(int fill, params Control[] rows)
    {
        var grid = new Grid();
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(i == fill ? GridLength.Star : GridLength.Auto));
            Grid.SetRow(rows[i], i);
            grid.Children.Add(rows[i]);
        }
        return grid;
    }

    /// <summary>
    /// Fills <paramref name="panel"/> with a control per item of <paramref name="items"/> (built by <paramref name="build"/>,
    /// kept for the same item), again whenever the collection changes; <paramref name="empty"/> shown while there is none.
    /// </summary>
    public static T Items<T, TItem>(T panel, IEnumerable<TItem> items, Func<TItem, Control> build, Control? empty = null) where T : Panel
    {
        var built = new Dictionary<object, Control>(ReferenceEqualityComparer.Instance);
        void Rebuild()
        {
            var list = items.Where(i => i is not null).ToList();
            var keep = new HashSet<object>(list.Cast<object>(), ReferenceEqualityComparer.Instance);
            foreach (var (item, control) in built.ToList())
                if (!keep.Contains(item))
                    built.Remove(item);
            var controls = new List<Control>();
            foreach (var item in list)
            {
                if (!built.TryGetValue(item!, out var control))
                {
                    control = build(item);
                    built[item!] = control;
                }
                controls.Add(control);
            }
            panel.Children.Clear();
            panel.Children.AddRange(controls);
            if (empty is not null)
                empty.IsVisible = list.Count == 0;
        }
        if (items is INotifyCollectionChanged observable)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => Ui.OnMain(Rebuild);
            observable.CollectionChanged += handler;
            panel.DetachedFromVisualTree += (_, _) => observable.CollectionChanged -= handler;
            panel.AttachedToVisualTree += (_, _) =>
            {
                observable.CollectionChanged -= handler;
                observable.CollectionChanged += handler;
                Rebuild();
            };
        }
        Rebuild();
        return panel;
    }

    /// <summary>A content built again by <paramref name="build"/> from the property <paramref name="name"/> whenever it changes.</summary>
    public static ContentControl Swap(INotifyPropertyChanged source, string name, Func<object?, Control?> build)
    {
        var holder = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        object? shown = new();
        Ui.When(holder, source, () =>
        {
            var value = source.GetType().GetProperty(name)?.GetValue(source);
            if (ReferenceEquals(value, shown))
                return;
            shown = value;
            holder.Content = build(value);
        }, name);
        return holder;
    }

    /// <summary>
    /// A row of a list that selects its item when clicked, lit while <paramref name="isSelected"/> (read again when
    /// the property <paramref name="selected"/> of <paramref name="source"/> changes).
    /// </summary>
    public static Border SelectRow(INotifyPropertyChanged source, string selected, Func<bool> isSelected, Action select, Control content, double minHeight = 40)
    {
        var row = new Border
        {
            Child = content,
            MinHeight = minHeight,
            Padding = new Thickness(8, 4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        void Paint()
        {
            var on = isSelected();
            row.Background = on ? Ui.Field : Brushes.Transparent;
            row.BorderBrush = on ? Ui.Accent : Brushes.Transparent;
        }
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
                select();
        };
        Ui.When(row, source, Paint, selected);
        return row;
    }

    /// <summary>
    /// The keys of a tool: <paramref name="handler"/> gets each key pressed inside <paramref name="owner"/> (true: handled).
    /// The owner takes the focus when shown, so that its keys work before anything is clicked.
    /// </summary>
    public static void Shortcuts(Control owner, Func<KeyEventArgs, bool> handler, bool tunnel = false)
    {
        owner.Focusable = true;
        owner.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!e.Handled && handler(e))
                e.Handled = true;
        }, tunnel ? RoutingStrategies.Tunnel : RoutingStrategies.Bubble);
        owner.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(owner)?.FocusManager?.GetFocusedElement() is not Visual focused || !owner.IsVisualAncestorOf(focused))
                owner.Focus();
        });
    }

    /// <summary>The key <paramref name="key"/> with Ctrl (⌘ on a Mac).</summary>
    public static bool IsCommand(KeyEventArgs e, Key key) =>
        e.Key == key && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta));

    /// <summary>A timer running while <paramref name="owner"/> is shown.</summary>
    public static DispatcherTimer Every(Control owner, TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => tick();
        owner.AttachedToVisualTree += (_, _) => timer.Start();
        owner.DetachedFromVisualTree += (_, _) => timer.Stop();
        if (owner.IsAttachedToVisualTree())
            timer.Start();
        return timer;
    }

    /// <summary>Runs <paramref name="detach"/> when <paramref name="owner"/> leaves the visual tree, <paramref name="attach"/> when it comes back.</summary>
    public static void Hook(Control owner, Action attach, Action detach)
    {
        var on = true;
        attach();
        owner.DetachedFromVisualTree += (_, _) =>
        {
            if (!on)
                return;
            on = false;
            detach();
        };
        owner.AttachedToVisualTree += (_, _) =>
        {
            if (on)
                return;
            on = true;
            attach();
        };
    }

    // ── Drawing ──

    public static SolidColorBrush Solid(byte r, byte g, byte b, byte a = 255) => new(Color.FromArgb(a, r, g, b));

    public static SolidColorBrush Solid(string hex) => new(Color.Parse(hex));

    public static Color ColorOf(IBrush brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;

    public static FormattedText Text(string text, double size, IBrush brush, bool bold = false, FontFamily? family = null) =>
        new(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family ?? FontFamily.Default, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), Math.Max(1, size), brush);

    /// <summary>Draws <paramref name="text"/> with its top-left at <paramref name="at"/>, centred in <paramref name="width"/> when given.</summary>
    public static void DrawText(DrawingContext context, string text, Point at, double size, IBrush brush, double width = -1, bool bold = false, FontFamily? family = null)
    {
        var formatted = Text(text, size, brush, bold, family);
        var x = width > 0 ? at.X + (width - formatted.Width) / 2 : at.X;
        context.DrawText(formatted, new Point(x, at.Y));
    }

    public static StreamGeometry Polygon(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(points[0], true);
            for (var i = 1; i < points.Count; i++)
                g.LineTo(points[i]);
            g.EndFigure(true);
        }
        return geometry;
    }
}
