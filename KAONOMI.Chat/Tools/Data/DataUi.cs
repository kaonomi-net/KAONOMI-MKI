using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using KAONOMI.UI.Chat;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// The pieces the views of the data tools and of the dashboards are built with (the Avalonia counterparts of the
/// helpers of the Godot chat: <c>Eve.Ui</c>, <c>Bind</c>, <c>DashUi</c>, <c>WritingUi</c>): a toolbar, a status bar, a
/// search bar, a placeholder, a badge, a captioned field, a list following a collection, a holder rebuilt from a
/// property, a split, a card, a key figure, a busy bar, a dot, a share bar, a folding section. Flat, no effects.
/// </summary>
internal static class DataUi
{
    // ── Colours of the charts (those of the Avalonia charts of the chat) ──

    public static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#2B9BF4"));
    public static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#F28C38"));
    public static readonly IBrush ErrorRed = new SolidColorBrush(Color.Parse("#E81123"));
    public static readonly IBrush ErrorLight = new SolidColorBrush(Color.Parse("#F25A66"));
    public static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#E6B450"));
    public static readonly IBrush Gold = new SolidColorBrush(Color.Parse("#D4A017"));

    /// <summary><paramref name="brush"/> at <paramref name="opacity"/> (a selection, a badge fill).</summary>
    public static IBrush Fade(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;

    // ── Values ──

    public static object? Get(object source, string name) =>
        source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);

    public static void Set(object source, string name, object? value)
    {
        if (source.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not { CanWrite: true } property)
            return;
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (value is not null && !type.IsInstanceOfType(value))
        {
            try
            {
                value = type.IsEnum ? Enum.ToObject(type, value) : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                return;
            }
        }
        property.SetValue(source, value);
    }

    /// <summary>
    /// Calls <paramref name="subscribe"/> now and <paramref name="unsubscribe"/> once <paramref name="owner"/> leaves the
    /// window tree (again <paramref name="subscribe"/> if it comes back: a window brought to the front).
    /// </summary>
    public static void Subscribe(Control owner, Action subscribe, Action unsubscribe)
    {
        var on = true;
        subscribe();
        owner.DetachedFromVisualTree += (_, _) =>
        {
            if (!on)
                return;
            on = false;
            unsubscribe();
        };
        owner.AttachedToVisualTree += (_, _) =>
        {
            if (on)
                return;
            on = true;
            subscribe();
        };
    }

    /// <summary>Calls <paramref name="changed"/> (on the thread of the interface) whenever <paramref name="items"/> changes, while <paramref name="owner"/> is shown.</summary>
    public static void OnCollection(Control owner, IEnumerable items, Action changed)
    {
        if (items is not INotifyCollectionChanged observable)
            return;
        NotifyCollectionChangedEventHandler handler = (_, _) => Ui.OnMain(changed);
        Subscribe(owner, () => observable.CollectionChanged += handler, () => observable.CollectionChanged -= handler);
    }

    // ── Text ──

    public static TextBlock Text(string text, IBrush? color = null, double size = 13, bool wrap = false, bool bold = false, bool mono = false)
    {
        var label = Ui.Label(text, color, size, wrap, bold);
        if (mono)
            label.FontFamily = Ui.Mono;
        return label;
    }

    /// <summary>A label showing the property <paramref name="name"/> of <paramref name="source"/> (through <paramref name="format"/>).</summary>
    public static TextBlock Bound(object source, string name, IBrush? color = null, double size = 13, bool wrap = false, Func<object?, string>? format = null)
    {
        var label = Ui.Label(string.Empty, color, size, wrap);
        return label.Bind(TextBlock.TextProperty, source, name, BindingMode.OneWay, v => format?.Invoke(v) ?? v?.ToString() ?? string.Empty);
    }

    /// <summary>The colour of <paramref name="label"/>: red while the bool property <paramref name="error"/> is true.</summary>
    public static T ErrorColor<T>(T label, object source, string error, IBrush? normal = null) where T : TextBlock =>
        label.Bind(TextBlock.ForegroundProperty, source, error, BindingMode.OneWay, v => v is true ? Ui.Red : normal ?? Ui.Muted);

    /// <summary>A tooltip following the string property <paramref name="name"/>.</summary>
    public static T Tip<T>(T control, object source, string name) where T : Control =>
        control.Bind(ToolTip.TipProperty, source, name, BindingMode.OneWay, v => v is string { Length: > 0 } s ? s : null);

    public static T Tip<T>(T control, string? tip) where T : Control
    {
        if (!string.IsNullOrEmpty(tip))
            ToolTip.SetTip(control, tip);
        return control;
    }

    public static TextBlock Caption(string text, IBrush? color = null, double size = 10) => Ui.Caption(text, color, size);

    /// <summary>A text in the middle of an empty area.</summary>
    public static TextBlock Placeholder(string text) => new()
    {
        Text = text,
        Foreground = Ui.Muted,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(16)
    };

    /// <summary>A small coloured tag (deleted, new, modified…), shown while <paramref name="visibleWhen"/> is true.</summary>
    public static Border Badge(string text, IBrush color, object? source = null, string? visibleWhen = null)
    {
        var badge = new Border
        {
            BorderBrush = color,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text.ToUpperInvariant(), Foreground = color, FontSize = 9 }
        };
        if (source is not null && visibleWhen is not null)
            badge.Visible(source, visibleWhen);
        return badge;
    }

    /// <summary>A thin line between parts.</summary>
    public static Border Separator() => new() { Height = 1, Background = Ui.Edge, Margin = new Thickness(0, 2) };

    /// <summary>A coloured dot shown while the bool property <paramref name="name"/> is true.</summary>
    public static TextBlock Dot(IBrush color, object source, string name, bool invert = false) =>
        Text("●", color, 10).Visible(source, name, invert);

    // ── Fields ──

    /// <summary>A caption over <paramref name="field"/>.</summary>
    public static StackPanel Field(string caption, Control field, string? tooltip = null)
    {
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        var column = Ui.Column(3, Caption(caption), field);
        return Tip(column, tooltip);
    }

    /// <summary>A caption over <paramref name="field"/>, the field taking the height left.</summary>
    public static DockPanel FieldFill(string caption, Control field) => Ui.Dock((Caption(caption), Dock.Top), (field, Dock.Top));

    /// <summary>A single-line text bound both ways to <paramref name="name"/>.</summary>
    public static TextBox Line(object source, string name, string? placeholder = null, bool mono = false)
    {
        var box = Ui.Edit(source, name, placeholder);
        if (mono)
            box.FontFamily = Ui.Mono;
        return box;
    }

    /// <summary>A multi-line text bound both ways to <paramref name="name"/>, <paramref name="height"/> high at least.</summary>
    public static TextBox Lines(object source, string name, double height = 80, string? placeholder = null, bool mono = false, bool wrap = true)
    {
        var box = Ui.Edit(source, name, placeholder, multiline: true, height: height);
        box.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        box.VerticalContentAlignment = VerticalAlignment.Top;
        if (mono)
            box.FontFamily = Ui.Mono;
        return box;
    }

    /// <summary>A read-only text (the context of a note, the data of a window), mono.</summary>
    public static TextBox ReadOnly(string text, double height = 110, double size = 11) => new()
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = Ui.Mono,
        FontSize = size,
        MinHeight = height,
        VerticalContentAlignment = VerticalAlignment.Top
    };

    public static CheckBox Check(string text, object source, string name, string? tooltip = null) => Tip(Ui.Check(text, source, name), tooltip);

    /// <summary>A toggle (a check box) that shows <paramref name="target"/> while it is ticked.</summary>
    public static CheckBox Reveal(string text, Control target, bool on = false)
    {
        target.IsVisible = on;
        var check = new CheckBox { Content = text, IsChecked = on };
        check.IsCheckedChanged += (_, _) => target.IsVisible = check.IsChecked == true;
        return check;
    }

    public static NumericUpDown Number(object source, string name, double min, double max, double step, string format = "0.##")
    {
        var number = Ui.Number(source, name, min, max, step);
        number.FormatString = format;
        number.MinWidth = 100;
        number.HorizontalAlignment = HorizontalAlignment.Stretch;
        return number;
    }

    /// <summary>A drop-down of <paramref name="items"/> bound both ways to <paramref name="selected"/>.</summary>
    public static ComboBox Options<T>(IEnumerable<T> items, object source, string selected, Func<T, string>? label = null, double minWidth = 120)
    {
        var combo = Ui.Combo(items, source, selected, label is null ? null : v => v is T t ? label(t) : string.Empty);
        combo.MinWidth = minWidth;
        combo.HorizontalAlignment = HorizontalAlignment.Left;
        return combo;
    }

    /// <summary>
    /// A text bound to <paramref name="name"/> with a ▾ button offering <paramref name="choices"/> (those containing what
    /// is typed first): the auto-complete box of the Avalonia chat.
    /// </summary>
    public static DockPanel TextWithChoices(object source, string name, IEnumerable<string> choices, string? placeholder = null)
    {
        var edit = Line(source, name, placeholder);
        var menu = new MenuFlyout();
        var button = new Button { Content = "▾", Focusable = false, Flyout = menu, Padding = new Thickness(8, 4) };
        ToolTip.SetTip(button, "Choose a known value");
        menu.Opening += (_, _) =>
        {
            var typed = (edit.Text ?? string.Empty).Trim();
            var shown = choices.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => typed.Length > 0 && c.Contains(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            var items = new List<MenuItem>();
            if (shown.Count == 0)
                items.Add(new MenuItem { Header = "(no known value)", IsEnabled = false });
            foreach (var choice in shown)
            {
                var item = new MenuItem { Header = choice };
                item.Click += (_, _) => Set(source, name, choice);
                items.Add(item);
            }
            menu.ItemsSource = items;
        };
        var row = Ui.Dock((button, Dock.Right), (edit, Dock.Left));
        button.Margin = new Thickness(4, 0, 0, 0);
        return row;
    }

    /// <summary>Red edges around <paramref name="field"/> while the bool property <paramref name="invalid"/> is true.</summary>
    public static Border InvalidEdge(Control field, object source, string invalid)
    {
        var border = new Border { Child = field, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
        return border.Bind(Border.BorderBrushProperty, source, invalid, BindingMode.OneWay, v => v is true ? Ui.Red : Brushes.Transparent);
    }

    /// <summary>Enables <paramref name="control"/> while the bool property <paramref name="name"/> is true (false with <paramref name="invert"/>).</summary>
    public static T Enabled<T>(T control, object source, string name, bool invert = false) where T : Control =>
        control.Bind(InputElement.IsEnabledProperty, source, name, BindingMode.OneWay, v => (v is true) != invert);

    // ── Buttons ──

    public static Button Action(string text, Action click, string? tooltip = null, bool primary = false) => Ui.Button(text, click, tooltip, primary);

    public static Button Command(string text, ICommand? command, string? tooltip = null, bool primary = false, object? parameter = null) =>
        command is null ? Ui.Button(text, null, tooltip, primary) : Ui.Command(text, command, parameter, tooltip, primary);

    /// <summary>True when the key of the shortcuts is held: ⌘ on macOS, Ctrl elsewhere (either is taken).</summary>
    public static bool IsCommand(KeyEventArgs e) => e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

    /// <summary>Runs <paramref name="handler"/> for the keys pressed in <paramref name="control"/>, before the control itself (a text box) handles them.</summary>
    public static void Keys(Control control, Func<KeyEventArgs, bool> handler) =>
        control.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!e.Handled && handler(e))
                e.Handled = true;
        }, RoutingStrategies.Tunnel);

    /// <summary>Ctrl / ⌘ + Enter in <paramref name="box"/> runs <paramref name="action"/>.</summary>
    public static void CtrlEnter(Control box, Action action) =>
        Keys(box, e =>
        {
            if (e.Key is not (Key.Enter or Key.Return) || !IsCommand(e))
                return false;
            action();
            return true;
        });

    /// <summary>Enter in <paramref name="box"/> runs <paramref name="command"/>.</summary>
    public static void Enter(TextBox box, ICommand? command) =>
        Keys(box, e =>
        {
            if (e.Key is not (Key.Enter or Key.Return) || e.KeyModifiers != KeyModifiers.None)
                return false;
            Ui.Run(command);
            return true;
        });

    /// <summary>Gives the focus to <paramref name="control"/> once it is shown.</summary>
    public static T FocusWhenShown<T>(T control) where T : Control
    {
        control.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => control.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        return control;
    }

    // ── Bars ──

    /// <summary>A header strip at the top of a tool (search, actions).</summary>
    public static Border Toolbar(Control content) => new()
    {
        Child = content,
        Background = Ui.Header,
        BorderBrush = Ui.Edge,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(8, 6)
    };

    /// <summary>A row: the controls docked left, <paramref name="fill"/> taking the width left, then the controls docked right.</summary>
    public static DockPanel Bar(Control[] left, Control? fill, params Control[] right)
    {
        var panel = new DockPanel { LastChildFill = true };
        foreach (var control in left)
        {
            DockPanel.SetDock(control, Dock.Left);
            control.Margin = new Thickness(0, 0, 8, 0);
            control.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(control);
        }
        foreach (var control in Enumerable.Reverse(right))
        {
            DockPanel.SetDock(control, Dock.Right);
            control.Margin = new Thickness(8, 0, 0, 0);
            control.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(control);
        }
        if (fill is not null)
        {
            fill.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(fill);
        }
        else
            panel.Children.Add(new Panel());
        return panel;
    }

    /// <summary>
    /// The search bar of a tool: a text bound to <paramref name="term"/> (Enter runs <paramref name="search"/>), the
    /// SEARCH button, then <paramref name="more"/>.
    /// </summary>
    public static DockPanel SearchBar(object source, string term, ICommand? search, string placeholder, params Control[] more) =>
        SearchBar(source, term, search, placeholder, out _, more);

    public static DockPanel SearchBar(object source, string term, ICommand? search, string placeholder, out TextBox edit, params Control[] more)
    {
        edit = Line(source, term, placeholder);
        Enter(edit, search);
        return Bar([], edit, new Control[] { Command("SEARCH", search, primary: true) }.Concat(more).ToArray());
    }

    /// <summary>
    /// The status bar of a tool: the busy bars of <paramref name="commands"/>, a progress while <paramref name="busy"/>
    /// is true, a red dot while <paramref name="error"/> is true, the first line of <paramref name="status"/> (whole in
    /// the tooltip).
    /// </summary>
    public static Border StatusBar(object source, string status, string? error = "IsError", string? busy = null, params ICommand[] commands)
    {
        var row = new DockPanel { LastChildFill = true };
        void Left(Control control)
        {
            DockPanel.SetDock(control, Dock.Left);
            control.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(control);
        }
        foreach (var command in commands)
            Left(Busy(command));
        if (busy is not null)
            Left(new ProgressBar { IsIndeterminate = true, Width = 60, MinWidth = 60, Height = 4, MinHeight = 4, VerticalAlignment = VerticalAlignment.Center }.Visible(source, busy));
        var hasError = error is not null && source.GetType().GetProperty(error) is not null;
        if (hasError)
            Left(Dot(Ui.Red, source, error!));
        var text = Ui.Label(string.Empty, Ui.Muted, 11);
        text.Bind(TextBlock.TextProperty, source, status, BindingMode.OneWay, v => (v?.ToString() ?? string.Empty).Split('\n')[0].Trim());
        text.Bind(ToolTip.TipProperty, source, status, BindingMode.OneWay, v => v is string s && s.Contains('\n') ? s : null);
        if (hasError)
            ErrorColor(text, source, error!);
        row.Children.Add(text);
        return new Border
        {
            Child = row,
            Background = Ui.Header,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 3)
        };
    }

    /// <summary>A small indeterminate bar shown while <paramref name="command"/> runs (its <c>IsRunning</c>).</summary>
    public static ProgressBar Busy(ICommand command, double width = 60)
    {
        var progress = new ProgressBar { IsIndeterminate = true, Width = width, MinWidth = width, Height = 4, MinHeight = 4, VerticalAlignment = VerticalAlignment.Center };
        if (command is INotifyPropertyChanged)
            progress.Visible(command, "IsRunning");
        else
            progress.IsVisible = false;
        return progress;
    }

    /// <summary>A thin bar showing a share (0–100).</summary>
    public static ProgressBar Share(double percent, double height = 5) => new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = percent,
        Height = height,
        MinHeight = height,
        MinWidth = 20,
        Foreground = Ui.Accent,
        Background = Ui.Field,
        CornerRadius = new CornerRadius(0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    // ── Panels ──

    /// <summary>A panel with a hairline edge around <paramref name="content"/>, with an optional caption on top.</summary>
    public static Border Card(Control content, string? caption = null, double padding = 10)
    {
        if (caption is null)
            return Ui.Box(content, padding);
        var title = Caption(caption);
        ToolTip.SetTip(title, caption);
        title.Margin = new Thickness(0, 0, 0, 6);
        return Ui.Box(Ui.Dock((title, Dock.Top), (content, Dock.Top)), padding);
    }

    /// <summary>A card whose caption is the string property <paramref name="captionProperty"/>, with <paramref name="right"/> at its right.</summary>
    public static Border Card(Control content, object source, string captionProperty, params Control[] right)
    {
        var caption = Bound(source, captionProperty, Ui.Muted, 10, format: v => (v?.ToString() ?? string.Empty).ToUpperInvariant());
        var header = Bar([], caption, right);
        header.Margin = new Thickness(0, 0, 0, 6);
        return Ui.Box(Ui.Dock((header, Dock.Top), (content, Dock.Top)), 10);
    }

    /// <summary>A key figure: its caption, the value (large) and a line of details (whole in the tooltip).</summary>
    public static Border Kpi(object source, string caption, string value, string details, IBrush? color = null, string? colorProperty = null)
    {
        var big = Bound(source, value, color ?? Ui.TextBright, 26);
        if (colorProperty is not null)
            big.Bind(TextBlock.ForegroundProperty, source, colorProperty, BindingMode.OneWay, v => v as IBrush ?? color ?? Ui.TextBright);
        var line = Tip(Bound(source, details, Ui.Muted, 11), source, details);
        return Card(Ui.Column(2, Caption(caption), big, line));
    }

    /// <summary>A section with a header that folds and unfolds <paramref name="content"/>.</summary>
    public static StackPanel Expander(string title, Control content, bool expanded, Action<bool>? toggled = null)
    {
        var header = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(2, 4) };
        var on = expanded;
        void Apply()
        {
            header.Content = new TextBlock { Text = (on ? "▾  " : "▸  ") + title.ToUpperInvariant(), Foreground = Ui.Accent, FontSize = 11, FontWeight = FontWeight.SemiBold };
            content.IsVisible = on;
        }
        header.Click += (_, _) =>
        {
            on = !on;
            Apply();
            toggled?.Invoke(on);
        };
        Apply();
        return Ui.Column(6, Separator(), header, content);
    }

    /// <summary>A left and a right part with a bar between them that moves (<paramref name="leftWidth"/> 0: halves).</summary>
    public static Grid SplitH(Control left, Control right, double leftWidth, double minLeft = 200)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                leftWidth > 0 ? new ColumnDefinition(new GridLength(leftWidth)) { MinWidth = minLeft } : new ColumnDefinition(1, GridUnitType.Star) { MinWidth = minLeft },
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 160 }
            }
        };
        var splitter = new GridSplitter { Width = 5, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(splitter);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A grid of <paramref name="columns"/> columns of the same width, filled row by row with <paramref name="cells"/>.</summary>
    public static Grid Cells(int columns, double hSpacing, double vSpacing, params Control[] cells)
    {
        var grid = new Grid { ColumnSpacing = hSpacing, RowSpacing = vSpacing };
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

    /// <summary>
    /// A holder whose content is built again by <paramref name="build"/> from the value of <paramref name="name"/>
    /// whenever it changes (the editor of the selected item); <paramref name="build"/> may return null.
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

    /// <summary>A Markdown view of the string property <paramref name="name"/>.</summary>
    public static ContentControl Markdown(INotifyPropertyChanged source, string name)
    {
        var holder = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Ui.When(holder, source, () => holder.Content = MarkdownView.Build(Get(source, name)?.ToString() ?? string.Empty), name);
        return holder;
    }

    /// <summary>
    /// A list of a control per item of <paramref name="items"/> (built by <paramref name="build"/>), following the
    /// collection; <paramref name="empty"/> is shown when there is none. Virtualized when <paramref name="virtualize"/>
    /// (then put it in a <see cref="ScrollViewer"/>: <see cref="ScrollList"/>).
    /// </summary>
    public static ItemsControl Items<T>(IEnumerable<T> items, Func<T, Control> build, Control? empty = null, double spacing = 2, bool virtualize = false)
    {
        var list = new ItemsControl
        {
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<T>((item, _) => item is null ? new Panel() : build(item), supportsRecycling: false),
            ItemsPanel = new FuncTemplate<Panel?>(() => virtualize ? new VirtualizingStackPanel() : new StackPanel { Spacing = spacing })
        };
        if (empty is not null)
        {
            void Refresh() => empty.IsVisible = !items.Any();
            OnCollection(list, items, Refresh);
            Refresh();
        }
        return list;
    }

    /// <summary>A scrolling list of <paramref name="items"/> with <paramref name="empty"/> over it when there is none.</summary>
    public static Panel ScrollList<T>(IEnumerable<T> items, Func<T, Control> build, string emptyText, double spacing = 2, bool virtualize = false)
    {
        var empty = Placeholder(emptyText);
        var list = Items(items, build, empty, spacing, virtualize);
        var scroll = Ui.Scroll(list);
        var panel = new Panel();
        panel.Children.Add(scroll);
        panel.Children.Add(empty);
        return panel;
    }

    /// <summary>
    /// A row of a list that can be chosen: a click runs <paramref name="click"/>, a double-click <paramref name="doubleClick"/>;
    /// outlined while <paramref name="isSelected"/> (read again when <paramref name="selectedProperty"/> of <paramref name="source"/> changes).
    /// </summary>
    public static Border ClickRow(Control content, Action click, Action? doubleClick = null, INotifyPropertyChanged? source = null,
        string? selectedProperty = null, Func<bool>? isSelected = null, IBrush? accent = null, double padding = 8)
    {
        var row = new Border
        {
            Child = content,
            Padding = new Thickness(padding, padding * 0.75),
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
                return;
            click();
            if (e.ClickCount == 2)
                doubleClick?.Invoke();
        };
        if (source is not null && selectedProperty is not null && isSelected is not null)
        {
            var color = accent ?? Ui.Accent;
            Ui.When(row, source, () =>
            {
                var on = isSelected();
                row.BorderBrush = on ? color : Ui.Edge;
                row.Background = on ? Fade(color, 0.16) : Ui.Field;
            }, selectedProperty);
        }
        return row;
    }

    /// <summary>A text cut to one line of <paramref name="length"/> characters at most.</summary>
    public static string Trim(string? text, int length)
    {
        var line = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length > length ? line[..length] + "…" : line;
    }

    // ── Windows ──

    /// <summary>The window of the desktop holding <paramref name="control"/>, or null.</summary>
    public static DeskWindow? WindowOf(Control? control) =>
        control?.GetVisualAncestors().OfType<DeskWindow>().FirstOrDefault() ?? (control as DeskWindow);

    /// <summary>
    /// The title of the window holding <paramref name="content"/> once it is open (the header of the window is changed;
    /// the window keeps its first title for the task bar).
    /// </summary>
    public static void TitleWhenOpen(Control content, string title)
    {
        content.AttachedToVisualTree += (_, _) =>
        {
            if (WindowOf(content) is not { } window || window.Title == title)
                return;
            var old = $"{window.Icon}  {window.Title}";
            if (window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == old) is { } header)
                header.Text = $"{window.Icon}  {title}";
        };
    }
}
