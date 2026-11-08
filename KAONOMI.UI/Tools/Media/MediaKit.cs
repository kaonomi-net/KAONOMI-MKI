using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
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
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// The pieces the media views are made of (the equivalents of the Godot helpers <c>Eve.Ui</c>, <c>Bind</c> and
/// <c>DashUi</c>): a captioned field, the toolbar and the status bar of a tool, cards, badges, splits with a movable bar,
/// lists of controls built from a collection, a content swapped with a property, the keys of a window, the
/// "📝 Annotate…" menu, the opening of a file with the system.
/// </summary>
internal static class Kit
{
    // ── Reflection on the view models ──

    public static object? Get(object source, string name) =>
        source.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(source);

    // ── Layout ──

    /// <summary>A small caption above <paramref name="field"/> (the field stretched).</summary>
    public static StackPanel Field(string caption, Control field)
    {
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        return Ui.Column(3, Ui.Caption(caption), field);
    }

    /// <summary>A row whose children wrap to the next line when the room is short.</summary>
    public static WrapPanel Flow(double spacing, params Control[] children)
    {
        var flow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = spacing, LineSpacing = spacing };
        flow.Children.AddRange(children);
        return flow;
    }

    /// <summary>A row where <paramref name="fill"/> takes the free width between <paramref name="before"/> and <paramref name="after"/>.</summary>
    public static DockPanel FillRow(double spacing, Control[] before, Control fill, params Control[] after)
    {
        var dock = new DockPanel { LastChildFill = true };
        foreach (var control in before)
        {
            control.Margin = new Thickness(0, 0, spacing, 0);
            control.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(control, Dock.Left);
            dock.Children.Add(control);
        }
        foreach (var control in Enumerable.Reverse(after))
        {
            control.Margin = new Thickness(spacing, 0, 0, 0);
            control.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(control, Dock.Right);
            dock.Children.Add(control);
        }
        dock.Children.Add(fill);
        return dock;
    }

    /// <summary>A grid of rows: <paramref name="rows"/> as "Auto,*,Auto", one child per row.</summary>
    public static Grid Rows(string rows, params Control[] children)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions(rows) };
        for (var i = 0; i < children.Length; i++)
        {
            Grid.SetRow(children[i], i);
            grid.Children.Add(children[i]);
        }
        return grid;
    }

    /// <summary>A grid of columns: <paramref name="columns"/> as "Auto,*,Auto", one child per column.</summary>
    public static Grid Columns(string columns, double spacing, params Control[] children)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = spacing };
        for (var i = 0; i < children.Length; i++)
        {
            Grid.SetColumn(children[i], i);
            grid.Children.Add(children[i]);
        }
        return grid;
    }

    /// <summary>A grid of <paramref name="columns"/> columns of the same width holding <paramref name="cells"/> in order.</summary>
    public static Grid Cells(int columns, double hSpacing, double vSpacing, params Control[] cells)
    {
        var rows = (cells.Length + columns - 1) / columns;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns))),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", Math.Max(rows, 1)))),
            ColumnSpacing = hSpacing,
            RowSpacing = vSpacing
        };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i % columns);
            Grid.SetRow(cells[i], i / columns);
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    /// <summary>
    /// Two parts side by side with a movable bar between them: <paramref name="leftWidth"/> (pixels, or "*") for the
    /// left one, <paramref name="rightWidth"/> for the right one.
    /// </summary>
    public static Grid SplitH(Control left, Control right, string leftWidth, string rightWidth, double minLeft = 160, double minRight = 160)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{leftWidth},5,{rightWidth}") };
        grid.ColumnDefinitions[0].MinWidth = minLeft;
        grid.ColumnDefinitions[2].MinWidth = minRight;
        var splitter = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(splitter);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>Two parts one above the other with a movable bar between them.</summary>
    public static Grid SplitV(Control top, Control bottom, string topHeight, string bottomHeight, double minTop = 80, double minBottom = 80)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions($"{topHeight},5,{bottomHeight}") };
        grid.RowDefinitions[0].MinHeight = minTop;
        grid.RowDefinitions[2].MinHeight = minBottom;
        var splitter = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1);
        Grid.SetRow(bottom, 2);
        grid.Children.Add(top);
        grid.Children.Add(splitter);
        grid.Children.Add(bottom);
        return grid;
    }

    /// <summary>A margin around <paramref name="child"/>.</summary>
    public static Border Margin(Control child, double left, double top, double right, double bottom) =>
        new() { Child = child, Padding = new Thickness(left, top, right, bottom) };

    public static Border Margin(Control child, double all = 8) => Margin(child, all, all, all, all);

    /// <summary>A scroll in both directions.</summary>
    public static ScrollViewer ScrollBoth(Control child) => new()
    {
        Content = child,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    /// <summary>A horizontal scroll (the content keeps its width).</summary>
    public static ScrollViewer ScrollH(Control child, bool showBar = true) => new()
    {
        Content = child,
        HorizontalScrollBarVisibility = showBar ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    // ── Looks ──

    /// <summary>The header strip at the top of a tool.</summary>
    public static Border Toolbar(Control content) => new()
    {
        Child = content,
        Background = Ui.Header,
        BorderBrush = Ui.Edge,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(8, 6)
    };

    /// <summary>A text shown in the middle of an empty area.</summary>
    public static TextBlock Placeholder(string text)
    {
        var label = Ui.Label(text, Ui.Muted, 13, wrap: true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextAlignment = TextAlignment.Center;
        label.IsHitTestVisible = false;
        return label;
    }

    /// <summary>A box with a caption (in the warm colour) above <paramref name="children"/>.</summary>
    public static Border Card(string? caption, params Control[] children)
    {
        var column = Ui.Column(6);
        if (caption is not null)
            column.Children.Add(Ui.Caption(caption, Ui.Warm));
        column.Children.AddRange(children);
        return Ui.Box(column, 10);
    }

    /// <summary>A box with a muted caption above <paramref name="content"/> (the content fills the rest).</summary>
    public static Border DashCard(Control content, string? caption = null, double padding = 10)
    {
        if (caption is null)
            return Ui.Box(content, padding);
        var title = Ui.Caption(caption);
        ToolTip.SetTip(title, caption);
        var dock = Ui.Dock((title, Dock.Top), (content, Dock.Top));
        title.Margin = new Thickness(0, 0, 0, 6);
        return Ui.Box(dock, padding);
    }

    /// <summary>A group of the toolbar: a box with a thin edge around <paramref name="children"/>.</summary>
    public static Border Group(params Control[] children)
    {
        foreach (var child in children)
            child.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Child = Ui.Row(4, children),
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(4, 2)
        };
    }

    /// <summary>A toggle button bound both ways to the bool property <paramref name="name"/>.</summary>
    public static ToggleButton Toggle(string text, object source, string name, string? tooltip = null)
    {
        var toggle = new ToggleButton { Content = text };
        if (tooltip is not null)
            ToolTip.SetTip(toggle, tooltip);
        return toggle.Bind(ToggleButton.IsCheckedProperty, source, name, BindingMode.TwoWay);
    }

    /// <summary>A check box bound both ways to <paramref name="name"/>, with a tooltip.</summary>
    public static CheckBox Check(string text, object source, string name, string? tooltip = null)
    {
        var check = Ui.Check(text, source, name);
        if (tooltip is not null)
            ToolTip.SetTip(check, tooltip);
        return check;
    }

    /// <summary>A small coloured tag, shown while <paramref name="visibleWhen"/> of <paramref name="source"/> is true (when given).</summary>
    public static Border Badge(string text, IBrush color, object? source = null, string? visibleWhen = null)
    {
        var badge = new Border
        {
            Child = Ui.Label(text.ToUpperInvariant(), color, 9),
            BorderBrush = color,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(5, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (source is not null && visibleWhen is not null)
            badge.Visible(source, visibleWhen);
        return badge;
    }

    /// <summary>A thin progress bar running while <paramref name="busy"/> of <paramref name="source"/> is true.</summary>
    public static ProgressBar Busy(object source, string busy, double width = 60)
    {
        var bar = new ProgressBar { IsIndeterminate = true, Width = width, Height = 4, MinHeight = 4, VerticalAlignment = VerticalAlignment.Center };
        return bar.Visible(source, busy);
    }

    /// <summary>
    /// The status bar of a tool: a progress while <paramref name="busy"/> is true (when given; and while each of
    /// <paramref name="busySources"/> runs), a red dot while <paramref name="error"/> is true, the first line of the
    /// status (whole in the tooltip), then <paramref name="right"/>.
    /// </summary>
    public static Border StatusBar(INotifyPropertyChanged source, string status, string? error = "IsError", string? busy = null,
        Control? right = null, params object[] busySources)
    {
        var row = new DockPanel { LastChildFill = true };
        void Left(Control control)
        {
            control.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(control, Dock.Left);
            row.Children.Add(control);
        }
        foreach (var command in busySources)
            if (command.GetType().GetProperty("IsRunning") is not null)
                Left(Busy(command, "IsRunning"));
        if (busy is not null)
            Left(Busy(source, busy));
        var text = Ui.Label(string.Empty, Ui.Muted, 11);
        if (error is not null && source.GetType().GetProperty(error) is not null)
        {
            var dot = Ui.Label("●", Ui.Red, 10).Visible(source, error);
            Left(dot);
            Ui.When(text, source, () => text.Foreground = Get(source, error) is true ? Ui.Red : Ui.Muted, error);
        }
        if (right is not null)
        {
            right.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
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
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 3)
        };
    }

    // ── Collections ──

    /// <summary>
    /// A control per item of <paramref name="items"/> (built by <paramref name="build"/>), kept in step with the
    /// collection when it is observable, laid out in <paramref name="panel"/> (a column by default).
    /// </summary>
    public static ItemsControl Items<T>(IEnumerable<T> items, Func<T, Control> build, Func<Panel>? panel = null)
    {
        var control = new ItemsControl
        {
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<T>((item, _) => item is null ? new Panel() : build(item), supportsRecycling: false)
        };
        var make = panel ?? (() => new StackPanel { Spacing = 2 });
        control.ItemsPanel = new FuncTemplate<Panel?>(() => make());
        return control;
    }

    /// <summary>Calls <paramref name="changed"/> now and whenever <paramref name="collection"/> changes, while <paramref name="owner"/> is shown.</summary>
    public static void OnCollection(Control owner, INotifyCollectionChanged collection, Action changed)
    {
        void Handler(object? sender, NotifyCollectionChangedEventArgs e) => Ui.OnMain(changed);
        collection.CollectionChanged += Handler;
        owner.DetachedFromVisualTree += (_, _) => collection.CollectionChanged -= Handler;
        owner.AttachedToVisualTree += (_, _) =>
        {
            collection.CollectionChanged -= Handler;
            collection.CollectionChanged += Handler;
        };
        changed();
    }

    /// <summary>Shows <paramref name="empty"/> while <paramref name="collection"/> has no item.</summary>
    public static T EmptyWhen<T>(T empty, IEnumerable collection) where T : Control
    {
        if (collection is INotifyCollectionChanged observable)
            OnCollection(empty, observable, () => empty.IsVisible = !collection.Cast<object?>().Any());
        else
            empty.IsVisible = !collection.Cast<object?>().Any();
        return empty;
    }

    /// <summary>
    /// A drop-down of <paramref name="items"/> (named by <paramref name="label"/>) bound both ways to the property
    /// <paramref name="selected"/> of <paramref name="source"/>.
    /// </summary>
    public static ComboBox Options<T>(IEnumerable<T> items, object source, string selected, Func<T, string>? label = null, string? tooltip = null)
    {
        var combo = Ui.Combo(items, source, selected, label is null ? null : o => o is T t ? label(t) : string.Empty);
        if (tooltip is not null)
            ToolTip.SetTip(combo, tooltip);
        return combo;
    }

    /// <summary>A slider bound both ways to the number property <paramref name="name"/>.</summary>
    public static Slider Slider(object source, string name, double min, double max, double step, string? tooltip = null, double width = double.NaN)
    {
        var slider = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10, Width = width, VerticalAlignment = VerticalAlignment.Center };
        if (tooltip is not null)
            ToolTip.SetTip(slider, tooltip);
        return slider.Bind(RangeBase.ValueProperty, source, name, BindingMode.TwoWay);
    }

    /// <summary>A label showing the property <paramref name="name"/> through <paramref name="format"/>.</summary>
    public static TextBlock Text(object source, string name, Func<object?, string> format, IBrush? color = null, double size = 13, bool wrap = false) =>
        Ui.Label(string.Empty, color, size, wrap).Bind(TextBlock.TextProperty, source, name, BindingMode.OneWay, v => format(v));

    /// <summary>A multi-line text bound both ways to <paramref name="name"/>.</summary>
    public static TextBox Lines(object source, string name, double height, string? placeholder = null, bool mono = false)
    {
        var box = Ui.Edit(source, name, placeholder, multiline: true, height: height);
        if (mono)
            box.FontFamily = Ui.Mono;
        return box;
    }

    /// <summary>
    /// A holder whose content is built again by <paramref name="build"/> from the value of <paramref name="name"/>
    /// whenever it changes (to another value).
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

    // ── Commands ──

    /// <summary>A button running <paramref name="command"/> with the parameter given when it is clicked (read then).</summary>
    public static Button Command(string text, ICommand command, Func<object?> parameter, string? tooltip = null, bool primary = false)
    {
        var button = Ui.Button(text, null, tooltip, primary);
        button.Command = command;
        button.CommandParameter = parameter();
        return button;
    }

    // ── Keys ──

    /// <summary>
    /// The keyboard shortcuts of a window: <paramref name="handler"/> sees the keys pressed in <paramref name="root"/>
    /// before its controls (true: the key is used). A click on an empty part of the window gives it the keyboard.
    /// </summary>
    public static void Keys(Control root, Func<KeyEventArgs, bool> handler)
    {
        root.Focusable = true;
        root.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!e.Handled && handler(e))
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        root.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
        {
            if (TopLevel.GetTopLevel(root)?.FocusManager?.GetFocusedElement() is not Visual focused || !(ReferenceEquals(focused, root) || root.IsVisualAncestorOf(focused)))
                root.Focus();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>True for Ctrl (or ⌘ on the Mac) with <paramref name="key"/>, without Alt.</summary>
    public static bool IsCommand(KeyEventArgs e, Key key) =>
        e.Key == key && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);

    /// <summary>True when Ctrl or ⌘ is held.</summary>
    public static bool CommandHeld(KeyModifiers modifiers) => modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);

    /// <summary>True when the keyboard is in a text field (Space, Enter… belong to it).</summary>
    public static bool IsTyping(KeyEventArgs e) => e.Source is TextBox || (e.Source as Visual)?.FindAncestorOfType<TextBox>() is not null;

    /// <summary>Gives the keyboard to <paramref name="control"/> once it is shown.</summary>
    public static void FocusWhenShown(Control control)
    {
        void Later() => Avalonia.Threading.Dispatcher.UIThread.Post(() => control.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        if (control.IsAttachedToVisualTree())
            Later();
        else
        {
            void Attached(object? sender, VisualTreeAttachmentEventArgs e)
            {
                control.AttachedToVisualTree -= Attached;
                Later();
            }
            control.AttachedToVisualTree += Attached;
        }
    }

    // ── Windows ──

    /// <summary>The window of the desktop holding <paramref name="control"/>, or null.</summary>
    public static DeskWindow? WindowOf(Control control) => control.FindAncestorOfType<DeskWindow>();

    /// <summary>
    /// Shows <paramref name="title"/> in the header of the window holding <paramref name="control"/> (the window keeps
    /// its key): the title text of the header is found and changed.
    /// </summary>
    public static void Retitle(Control control, string title)
    {
        if (WindowOf(control) is not { } window)
            return;
        var header = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Tag as string == "media-title" || t.Text == $"{window.Icon}  {window.Title}");
        if (header is null)
            return;
        header.Tag = "media-title";
        header.Text = $"{window.Icon}  {title}";
    }

    // ── Annotations ──

    /// <summary>The item of the context menus.</summary>
    public const string AnnotateText = "📝 Annotate…";

    /// <summary>Opens the annotation of <paramref name="target"/>, written in the window holding <paramref name="origin"/>.</summary>
    public static void Annotate(Control? origin, AnnotationTarget target)
    {
        var shell = Shell.Current;
        if (shell is null)
            return;
        var window = origin is null ? null : WindowOf(origin);
        var provider = window?.ViewModel as INoteContextProvider ?? (window is null ? shell.Chat : null);
        var title = window?.Title ?? "Chat";
        var windowName = provider?.NoteWindowName ?? title;
        var context = NoteContext.Build(provider, windowName);
        shell.Open(new AnnotationDialogViewModel(KaonomiAssistantService.Shared, target, windowName, context) { SourceLink = window?.Key },
            title: $"Annotate · {target.Title ?? windowName}");
    }

    /// <summary>Shows the menu "📝 Annotate…" on <paramref name="at"/> for <paramref name="target"/>.</summary>
    public static void AnnotateMenu(Control at, AnnotationTarget target)
    {
        var item = new MenuItem { Header = AnnotateText };
        item.Click += (_, _) => Annotate(at, target);
        var menu = new ContextMenu { Items = { item } };
        menu.Open(at);
    }

    /// <summary>True for a press of the right button.</summary>
    public static bool IsRight(PointerPressedEventArgs e, Visual relativeTo) => e.GetCurrentPoint(relativeTo).Properties.IsRightButtonPressed;

    /// <summary>True for a press of the left button.</summary>
    public static bool IsLeft(PointerPressedEventArgs e, Visual relativeTo) => e.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed;

    // ── The system ──

    /// <summary>Opens <paramref name="path"/> (a file or an address) with the application of the system.</summary>
    public static void OpenWithSystem(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            PictureLibrary.OpenWithSystem(path);
    }

    /// <summary>Shows <paramref name="path"/> in the Finder / Explorer.</summary>
    public static void ShowInFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            PictureLibrary.Reveal(path);
    }

    // ── Colours ──

    public static Color ColorOf(IBrush brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Gray;

    public static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
