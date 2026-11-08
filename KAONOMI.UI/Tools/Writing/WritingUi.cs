using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
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
using KAONOMI.Chat.Services;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>
/// The helpers of the writing tools (the Avalonia counterpart of the Eve Ui / Bind helpers and of WritingUi of the Godot
/// chat): fields under a caption, texts bound both ways, lists of items kept in step with an observable collection, the
/// holder of the editor of the item selected, the rows of the result lists, a date field with its calendar, a text with
/// known choices, the status bar, the keyboard shortcuts of a window. Flat controls only.
/// </summary>
public static class WritingUi
{
    // ── Colours not in the theme ──

    public static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#E0A030"));
    public static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#2B9BF4"));

    // ── Reflection (the property of a view model by name) ──

    public static object? Get(object source, string name) =>
        source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);

    public static void Set(object source, string name, object? value)
    {
        var property = source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || !property.CanWrite)
            return;
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        try
        {
            if (value is not null && !type.IsInstanceOfType(value))
                value = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            if (!Equals(property.GetValue(source), value))
                property.SetValue(source, value);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or TargetInvocationException)
        {
        }
    }

    /// <summary>Runs <paramref name="action"/> once the layout of the current pass is done (focus, scroll).</summary>
    public static void Later(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

    /// <summary>Gives the focus to <paramref name="control"/> once it is shown.</summary>
    public static T FocusWhenShown<T>(T control) where T : Control
    {
        void Attached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            control.AttachedToVisualTree -= Attached;
            Later(() => control.Focus());
        }
        control.AttachedToVisualTree += Attached;
        return control;
    }

    /// <summary>Calls <paramref name="cleanup"/> when <paramref name="owner"/> leaves the tree (its window closed).</summary>
    public static void OnGone(Control owner, Action cleanup) => owner.DetachedFromVisualTree += (_, _) => cleanup();

    // ── Layout ──

    /// <summary>A small caption above <paramref name="field"/>.</summary>
    public static StackPanel Field(string caption, Control field)
    {
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        return Ui.Column(3, Ui.Caption(caption), field);
    }

    /// <summary>A grid of <paramref name="columns"/> columns of the same width holding <paramref name="cells"/>.</summary>
    public static Grid Cells(int columns, params Control[] cells)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 10 };
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (var i = 0; i < cells.Length; i++)
        {
            if (i % columns == 0)
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(cells[i], i / columns);
            Grid.SetColumn(cells[i], i % columns);
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    /// <summary>A row of <paramref name="children"/> going on to the next line when the window is narrow.</summary>
    public static WrapPanel Flow(double spacing, params Control[] children)
    {
        var flow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            child.Margin = new Thickness(0, 0, spacing, spacing / 2);
            child.VerticalAlignment = VerticalAlignment.Center;
            flow.Children.Add(child);
        }
        return flow;
    }

    /// <summary>Two panes side by side, the bar between them can be moved; <paramref name="leftWidth"/> 0: proportions.</summary>
    public static Grid SplitH(Control left, Control right, double leftWidth, double leftStar = 1, double rightStar = 1, double minLeft = 200, double minRight = 200)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(leftWidth > 0
            ? new ColumnDefinition(leftWidth, GridUnitType.Pixel) { MinWidth = minLeft }
            : new ColumnDefinition(leftStar, GridUnitType.Star) { MinWidth = minLeft });
        grid.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(rightStar, GridUnitType.Star) { MinWidth = minRight });
        var splitter = new GridSplitter { Background = Ui.Edge, Width = 1, HorizontalAlignment = HorizontalAlignment.Center, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(splitter);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A DockPanel: <paramref name="top"/> items docked at the top, <paramref name="bottom"/> at the bottom, <paramref name="fill"/> filling.</summary>
    public static DockPanel Page(Control[] top, Control fill, params Control[] bottom)
    {
        var panel = new DockPanel { LastChildFill = true };
        foreach (var control in top)
        {
            DockPanel.SetDock(control, Dock.Top);
            panel.Children.Add(control);
        }
        foreach (var control in Enumerable.Reverse(bottom))
        {
            DockPanel.SetDock(control, Dock.Bottom);
            panel.Children.Add(control);
        }
        panel.Children.Add(fill);
        return panel;
    }

    public static Border Margin(Control child, double left, double top, double right, double bottom) =>
        new() { Child = child, Padding = new Thickness(left, top, right, bottom) };

    public static Border Margin(Control child, double all = 8) => Margin(child, all, all, all, all);

    public static Border Separator() => new() { Height = 1, Background = Ui.Edge, Margin = new Thickness(0, 2) };

    /// <summary>A header strip at the top of a tool (search, actions).</summary>
    public static Border Toolbar(Control content) => new()
    {
        Child = content,
        Background = Ui.Header,
        BorderBrush = Ui.Edge,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(6, 6)
    };

    /// <summary>A dark panel with a hairline edge, with an optional caption on top (the cards of the dashboards).</summary>
    public static Border Card(Control content, string? caption = null, double padding = 10)
    {
        Control child = content;
        if (caption is not null)
        {
            var title = Ui.Caption(caption);
            ToolTip.SetTip(title, caption);
            child = Ui.Dock((title, Dock.Top), (content, Dock.Top));
            title.Margin = new Thickness(0, 0, 0, 6);
        }
        return Ui.Box(child, padding);
    }

    /// <summary>A key figure: its caption, the value (large) and a line of details.</summary>
    public static Border Kpi(object source, string caption, string value, string details, IBrush? color = null)
    {
        var big = Ui.Bound(source, value, color ?? Ui.TextBright, 26);
        var line = Ui.Bound(source, details, Ui.Muted, 11);
        line.Bind(ToolTip.TipProperty, source, details);
        return Card(Ui.Column(2, Ui.Caption(caption), big, line));
    }

    /// <summary>A thin bar showing a share (0–100).</summary>
    public static ProgressBar Share(double percent, double height = 4) => new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = percent,
        Height = height,
        MinHeight = height,
        Foreground = Ui.Accent,
        Background = Ui.Field,
        IsHitTestVisible = false
    };

    /// <summary>A text shown in the middle of an empty area.</summary>
    public static TextBlock Placeholder(string text)
    {
        var label = Ui.Label(text, Ui.Muted, 13, wrap: true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextAlignment = TextAlignment.Center;
        label.Margin = new Thickness(12);
        return label;
    }

    /// <summary>A label cut at <paramref name="lines"/> lines with an ellipsis.</summary>
    public static TextBlock Clamp(TextBlock label, int lines)
    {
        label.TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        label.MaxLines = lines;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        return label;
    }

    /// <summary>A small coloured tag (new, modified…), shown while <paramref name="visibleWhen"/> is true.</summary>
    public static Border Badge(string text, IBrush color, object? source = null, string? visibleWhen = null)
    {
        var badge = new Border
        {
            BorderBrush = color,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Label(text.ToUpperInvariant(), color, 9)
        };
        if (source is not null && visibleWhen is not null)
            badge.Visible(source, visibleWhen);
        return badge;
    }

    /// <summary>A coloured dot shown while <paramref name="name"/> is true.</summary>
    public static TextBlock Dot(IBrush color, object source, string name) => Ui.Label("●", color, 10).Visible(source, name);

    // ── Texts ──

    /// <summary>A single-line text bound both ways to <paramref name="name"/>.</summary>
    public static TextBox Line(object source, string name, string placeholder = "") =>
        Ui.Edit(source, name, placeholder.Length > 0 ? placeholder : null);

    /// <summary>A multi-line text bound both ways to <paramref name="name"/>, <paramref name="height"/> high at least.</summary>
    public static TextBox Lines(object source, string name, double height = 80, string placeholder = "", bool mono = false)
    {
        var box = Ui.Edit(source, name, placeholder.Length > 0 ? placeholder : null, multiline: true, height: height);
        box.MaxHeight = Math.Max(height * 3, 420);
        box.AcceptsTab = false;
        if (mono)
            box.FontFamily = Ui.Mono;
        return box;
    }

    /// <summary>A read-only text (a log, a raw answer) that can be selected and copied.</summary>
    public static TextBox ReadOnly(bool mono = false, double size = 13) => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = mono ? Ui.Mono : FontFamily.Default,
        FontSize = size,
        VerticalContentAlignment = VerticalAlignment.Top
    };

    /// <summary>A label showing <paramref name="name"/> through <paramref name="format"/>.</summary>
    public static TextBlock Text(object source, string name, IBrush? color = null, double size = 13, bool wrap = false, Func<object?, string>? format = null)
    {
        var label = Ui.Label(string.Empty, color, size, wrap);
        return format is null
            ? label.Bind(TextBlock.TextProperty, source, name)
            : label.Bind(TextBlock.TextProperty, source, name, BindingMode.OneWay, v => format(v));
    }

    // ── Choices ──

    /// <summary>A drop-down of <paramref name="items"/> bound both ways to the property <paramref name="name"/>.</summary>
    public static ComboBox Choice<T>(object source, string name, IEnumerable<T> items, Func<T, string>? label = null)
    {
        var list = items as IList ?? items.ToList();
        return Ui.Combo(list, source, name, label is null ? null : v => v is T t ? label(t) : v?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// A text bound to <paramref name="name"/> with a ▾ button offering <paramref name="choices"/> (those containing what
    /// is typed first).
    /// </summary>
    public static DockPanel TextWithChoices(object source, string name, IEnumerable<string> choices, string placeholder = "")
    {
        var edit = Line(source, name, placeholder);
        var menu = new MenuFlyout();
        var button = new Button { Content = "▾", Focusable = false, Flyout = menu };
        ToolTip.SetTip(button, "Choose a known value");
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var typed = (edit.Text ?? string.Empty).Trim();
            var shown = choices.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => typed.Length > 0 && c.Contains(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            if (shown.Count == 0)
                menu.Items.Add(new MenuItem { Header = "(no known value)", IsEnabled = false });
            foreach (var choice in shown)
            {
                var value = choice;
                var item = new MenuItem { Header = value };
                item.Click += (_, _) => Set(source, name, value);
                menu.Items.Add(item);
            }
        };
        button.Margin = new Thickness(4, 0, 0, 0);
        return Ui.Dock((button, Dock.Right), (edit, Dock.Left));
    }

    /// <summary>
    /// A date bound both ways to the <see cref="DateTime"/>? property <paramref name="name"/>: typed as yyyy-MM-dd
    /// (empty: no date), or picked in the calendar of the 📅 button (Today, Clear).
    /// </summary>
    public static DockPanel DateField(INotifyPropertyChanged source, string name)
    {
        var edit = new TextBox { PlaceholderText = "yyyy-mm-dd", MinWidth = 100 };
        DateTime? Current() => Get(source, name) switch
        {
            DateTime d => d,
            DateTimeOffset o => o.LocalDateTime,
            _ => null
        };
        void Show() => edit.Text = Current() is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
        void Commit()
        {
            var text = (edit.Text ?? string.Empty).Trim();
            if (text.Length == 0)
                Set(source, name, null);
            else if (DateTime.TryParseExact(text, ["yyyy-MM-dd", "yyyy-M-d", "d/M/yyyy", "dd/MM/yyyy", "d.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                     || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
                Set(source, name, (DateTime?)date.Date);
            Show();
        }
        Ui.When(edit, source, Show, name);
        edit.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        edit.LostFocus += (_, _) => Commit();

        var calendar = new Avalonia.Controls.Calendar { SelectionMode = CalendarSelectionMode.SingleDate, FirstDayOfWeek = DayOfWeek.Monday };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var updating = false;
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (updating || calendar.SelectedDate is not { } picked)
                return;
            Set(source, name, (DateTime?)picked.Date);
            flyout.Hide();
        };
        var today = Ui.Button("TODAY", () => { Set(source, name, (DateTime?)DateTime.Today); flyout.Hide(); });
        var clear = Ui.Button("CLEAR", () => { Set(source, name, null); flyout.Hide(); }, "No date");
        var footer = Ui.Dock((clear, Dock.Right), (today, Dock.Left));
        flyout.Content = Ui.Column(6, calendar, footer);
        flyout.Opening += (_, _) =>
        {
            updating = true;
            var current = Current();
            calendar.SelectedDate = current?.Date;
            calendar.DisplayDate = current ?? DateTime.Today;
            updating = false;
        };
        var button = new Button { Content = "📅", Focusable = false, Flyout = flyout, Margin = new Thickness(4, 0, 0, 0) };
        ToolTip.SetTip(button, "Pick the date in a calendar");
        return Ui.Dock((button, Dock.Right), (edit, Dock.Left));
    }

    // ── Commands ──

    /// <summary>A button running <paramref name="command"/> (disabled while it cannot run), with the parameter given by <paramref name="parameter"/>.</summary>
    public static Button Command(string text, ICommand? command, bool primary = false, string? tooltip = null, Func<object?>? parameter = null)
    {
        var button = Ui.Button(text, null, tooltip, primary);
        if (command is null)
            return button;
        if (parameter is null)
        {
            button.Command = command;
            return button;
        }
        void Refresh() => Ui.OnMain(() => button.IsEnabled = command.CanExecute(parameter()));
        EventHandler handler = (_, _) => Refresh();
        command.CanExecuteChanged += handler;
        OnGone(button, () => command.CanExecuteChanged -= handler);
        button.Click += (_, _) => Ui.Run(command, parameter());
        Refresh();
        return button;
    }

    /// <summary>Runs <paramref name="command"/> (when it can run) and says it was used.</summary>
    public static bool Run(ICommand? command, object? parameter = null)
    {
        Ui.Run(command, parameter);
        return true;
    }

    /// <summary>Enables <paramref name="control"/> while the bool <paramref name="name"/> is true (false with <paramref name="invert"/>).</summary>
    public static T Enabled<T>(T control, object source, string name, bool invert = false) where T : Control =>
        control.Bind(InputElement.IsEnabledProperty, source, name, BindingMode.OneWay, v => (v is true) != invert);

    // ── Collections ──

    /// <summary>
    /// Fills <paramref name="panel"/> with a control per item of <paramref name="items"/> (built by <paramref name="build"/>),
    /// again whenever the collection changes (the controls of the items kept are kept); <paramref name="empty"/> is shown when
    /// there is none.
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
                {
                    built.Remove(item);
                    panel.Children.Remove(control);
                }
            for (var index = 0; index < list.Count; index++)
            {
                var item = list[index]!;
                if (!built.TryGetValue(item, out var control))
                {
                    control = build(item);
                    built[item] = control;
                }
                var at = panel.Children.IndexOf(control);
                if (at == index)
                    continue;
                if (at >= 0)
                    panel.Children.RemoveAt(at);
                panel.Children.Insert(Math.Min(index, panel.Children.Count), control);
            }
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

    /// <summary>
    /// A holder whose content is built again by <paramref name="build"/> from the value of <paramref name="name"/> whenever
    /// it changes (the editor of the selected item); <paramref name="build"/> may return null (nothing shown).
    /// </summary>
    public static ContentControl Swap(INotifyPropertyChanged source, string name, Func<object?, Control?> build)
    {
        var holder = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        object? shown = null;
        var first = true;
        Ui.When(holder, source, () =>
        {
            var value = Get(source, name);
            if (!first && ReferenceEquals(value, shown))
                return;
            first = false;
            shown = value;
            holder.Content = build(value);
        }, name);
        return holder;
    }

    /// <summary>
    /// A row of a list of results: <paramref name="content"/> in a flat frame, outlined while <paramref name="isSelected"/> is
    /// true (checked when <paramref name="selected"/> of <paramref name="source"/> changes); a click calls <paramref name="select"/>.
    /// </summary>
    public static Border ResultRow(INotifyPropertyChanged source, string selected, Func<bool> isSelected, Action select, Control content, double minHeight = 56)
    {
        var row = new Border
        {
            Child = content,
            MinHeight = minHeight,
            Padding = new Thickness(8, 6),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 0, 2)
        };
        var hover = false;
        void Paint()
        {
            var on = isSelected();
            row.Background = on ? Ui.Header : hover ? Ui.Panel : Ui.Field;
            row.BorderBrush = on ? Ui.Accent : hover ? Ui.Muted : Ui.Edge;
        }
        row.PointerEntered += (_, _) => { hover = true; Paint(); };
        row.PointerExited += (_, _) => { hover = false; Paint(); };
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed && e.ClickCount == 1)
                select();
        };
        Ui.When(row, source, Paint, selected);
        return row;
    }

    /// <summary>Calls <paramref name="action"/> on a double-click on <paramref name="control"/>.</summary>
    public static T OnDoubleClick<T>(T control, Action action) where T : Control
    {
        control.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.ClickCount == 2 && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            {
                action();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        return control;
    }

    // ── Status bar ──

    /// <summary>
    /// The status bar of a tool: a progress while <paramref name="busy"/> of <paramref name="busySource"/> (or of the view
    /// model) is true, a red dot while <paramref name="error"/> is true, the first line of <paramref name="status"/> (the
    /// whole of it in the tooltip), then <paramref name="extra"/> at the right.
    /// </summary>
    public static Border StatusBar(INotifyPropertyChanged source, string status, string? error = "IsError", string? busy = null,
        INotifyPropertyChanged? busySource = null, params Control[] extra)
    {
        var row = new DockPanel { LastChildFill = true };
        foreach (var control in Enumerable.Reverse(extra))
        {
            control.Margin = new Thickness(10, 0, 0, 0);
            control.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(control, Dock.Right);
            row.Children.Add(control);
        }
        if (busy is not null)
        {
            var progress = new ProgressBar { IsIndeterminate = true, Width = 60, MinWidth = 60, Height = 4, MinHeight = 4, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            progress.Visible(busySource ?? source, busy);
            DockPanel.SetDock(progress, Dock.Left);
            row.Children.Add(progress);
        }
        var text = Ui.Label(string.Empty, Ui.Muted, 11);
        if (error is not null && source.GetType().GetProperty(error) is not null)
        {
            var dot = Dot(Ui.Red, source, error);
            dot.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            Ui.When(text, source, () => text.Foreground = Get(source, error) is true ? Ui.Red : Ui.Muted, error);
        }
        Ui.When(text, source, () =>
        {
            var message = Get(source, status)?.ToString() ?? string.Empty;
            var first = message.Split('\n')[0].Trim();
            text.Text = first;
            ToolTip.SetTip(text, message.Length > first.Length ? message : null);
        }, status);
        row.Children.Add(text);
        return new Border
        {
            Child = row,
            Background = Ui.Header,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 3),
            MinHeight = 24
        };
    }

    /// <summary>
    /// The search bar of a tool: a text bound to <paramref name="term"/> (Enter runs <paramref name="search"/>), the Search
    /// button, then <paramref name="more"/>. The text box is given back to take the focus.
    /// </summary>
    public static DockPanel SearchBar(object source, string term, ICommand? search, string placeholder, out TextBox box, params Control[] more)
    {
        box = Line(source, term, placeholder);
        var edit = box;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                // The binding writes the text on each change: the term is there already.
                Ui.Run(search);
                e.Handled = true;
            }
        };
        var buttons = Ui.Row(6, Ui.Command("SEARCH", search!, primary: true));
        foreach (var control in more)
            buttons.Children.Add(control);
        buttons.Margin = new Thickness(6, 0, 0, 0);
        return Ui.Dock((buttons, Dock.Right), (edit, Dock.Left));
    }

    // ── Keyboard ──

    /// <summary>
    /// Keyboard shortcuts of <paramref name="owner"/>: <paramref name="handler"/> receives the keys pressed while the focus is
    /// in it; true when it used the key.
    /// </summary>
    public static void Shortcuts(Control owner, Func<KeyEventArgs, bool> handler) =>
        owner.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!e.Handled && handler(e))
                e.Handled = true;
        }, RoutingStrategies.Tunnel);

    /// <summary>True for Ctrl (Windows / Linux) or ⌘ (macOS) with <paramref name="key"/>, without Shift and Alt.</summary>
    public static bool IsCommand(KeyEventArgs e, Key key) =>
        e.Key == key && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
                     && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);

    public static bool HasCommandKey(KeyEventArgs e) =>
        e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

    // ── Windows of the desktop ──

    /// <summary>The window of the desktop holding <paramref name="control"/>.</summary>
    public static DeskWindow? WindowOf(Control control) => control.FindAncestorOfType<DeskWindow>();

    /// <summary>
    /// Shows <paramref name="title"/> in the header of the window holding <paramref name="control"/> (the window keeps its
    /// key and its name in the task bar).
    /// </summary>
    public static void SetTitle(Control control, string title)
    {
        if (WindowOf(control) is not { } window)
            return;
        var prefix = $"{window.Icon}  ";
        var header = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text?.StartsWith(prefix, StringComparison.Ordinal) == true);
        if (header is not null)
            header.Text = prefix + title;
    }

    /// <summary>Calls <paramref name="update"/> once <paramref name="control"/> is in its window (and at each attachment).</summary>
    public static void WhenInWindow(Control control, Action update) => control.AttachedToVisualTree += (_, _) => Later(update);

    /// <summary>Closes the window of the desktop holding <paramref name="control"/>.</summary>
    public static void CloseWindowOf(Shell shell, Control control)
    {
        if (WindowOf(control) is { } window)
            shell.Deck.Close(window);
    }

    /// <summary>The confirmation asked by a view model (message, label of the confirming button), in a dialog of the chat.</summary>
    public static System.Threading.Tasks.Task<bool> Confirm(string message, string label) =>
        Dialogs.Confirm(label, message, label, "Cancel");

    // ── Files and links ──

    public static readonly string[] PictureFilters =
        ["*.jpg,*.jpeg,*.png,*.gif,*.webp,*.bmp,*.heic,*.heif,*.tif,*.tiff ; Pictures", "* ; All files"];

    /// <summary>Shows <paramref name="path"/> (a file: selected) in the file manager of the system.</summary>
    public static void ShowInFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (System.IO.File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                else if (System.IO.Directory.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
            }
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", System.IO.File.Exists(path) ? ["-R", path] : [path]) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("xdg-open", [System.IO.File.Exists(path) ? System.IO.Path.GetDirectoryName(path) ?? path : path]) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ActivityLog.Error("Writing", $"Folder not shown: {path}", ex);
        }
    }

    /// <summary>A web link in the browser of the chat; anything else with the system (refused when it could run something).</summary>
    public static void OpenWeb(string link)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            PictureLibrary.OpenWeb(link);
        else
            PictureLibrary.OpenWithSystem(link);
    }

    /// <summary>"www.x.org" → "https://www.x.org"; a mail address → mailto:; the rest as it is (null when empty).</summary>
    public static string? NormalizeLink(string? link)
    {
        var text = link?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            return "https://" + text;
        if (!text.Contains(':') && text.Contains('@') && !text.Contains(' '))
            return "mailto:" + text;
        return text;
    }
}

/// <summary>The parts shared by the editors of the entries of the search engine (ideas, intels, collection plans).</summary>
public static class EntryParts
{
    /// <summary>The id of the entry (selectable, to be copied) and its date under it.</summary>
    public static Control Identity(object entry, string id = "IdLabel", string date = "DateLabel")
    {
        var idBox = new TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 12,
            Padding = new Thickness(0),
            MinHeight = 0,
            MinWidth = 160
        };
        idBox.Bind(TextBox.TextProperty, entry, id);
        return Ui.Column(0, idBox, Ui.Bound(entry, date, Ui.Muted, 10));
    }

    /// <summary>The status of the entry (its red dot while in error), the whole of it in the tooltip.</summary>
    public static Control Status(object entry, string status = "Status", string error = "IsError")
    {
        var label = Ui.Bound(entry, status, Ui.Text, 12);
        label.Bind(ToolTip.TipProperty, entry, status);
        label.MinWidth = 80;
        label.TextAlignment = TextAlignment.Right;
        return Ui.Row(6, WritingUi.Dot(Ui.Red, entry, error), label);
    }

    /// <summary>The list of results on the left: the rows in a scroll, "No results" when empty.</summary>
    public static Control List<TItem>(IEnumerable<TItem> items, Func<TItem, Control> row, string empty, double minWidth = 300)
    {
        var list = new StackPanel { Spacing = 0, Margin = new Thickness(6, 6, 8, 6) };
        var none = WritingUi.Placeholder(empty);
        none.VerticalAlignment = VerticalAlignment.Top;
        WritingUi.Items(list, items, row, none);
        return new Panel { MinWidth = minWidth, Children = { Ui.Scroll(list), none } };
    }

    /// <summary>The editor of an entry: its header, a line, its fields in a scroll.</summary>
    public static Control Editor(Control header, Control fields, params Control[] more)
    {
        var top = new List<Control> { WritingUi.Margin(header, 8, 6, 8, 0) };
        top.AddRange(more.Select(m => (Control)WritingUi.Margin(m, 8, 0, 8, 0)));
        top.Add(WritingUi.Separator());
        return WritingUi.Page(top.ToArray(), Ui.Scroll(WritingUi.Margin(fields, 8, 6, 14, 12)));
    }
}

/// <summary>The files dropped from the file manager of the system on a window.</summary>
public static class WritingFiles
{
    /// <summary>The local paths of the files of the drop <paramref name="e"/>.</summary>
    public static IReadOnlyList<string> Dropped(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return [];
        return files.Select(f => Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(f)).OfType<string>().ToList();
    }
}
