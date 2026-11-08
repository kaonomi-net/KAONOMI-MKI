using System;
using System.Collections;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace KAONOMI.UI.Desk;

/// <summary>
/// The small helpers the views are written with (in C#, as the views of the Godot chat): controls in the colours of the
/// theme, and bindings to the properties of a view model (Avalonia bindings, the source given).
/// </summary>
public static class Ui
{
    // ── Colours (Styles/Kaonomi.axaml) ──

    public static IBrush Brush(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true && value is IBrush brush ? brush : Brushes.Gray;

    public static IBrush Accent => Brush("Accent");
    public static IBrush Warm => Brush("Warm");
    public static IBrush Muted => Brush("Muted");
    public static IBrush Text => Brush("Text");
    public static IBrush TextBright => Brush("TextBright");
    public static IBrush Red => Brush("Red");
    public static IBrush Green => Brush("Green");
    public static IBrush Edge => Brush("Edge");
    public static IBrush Panel => Brush("Panel");
    public static IBrush Header => Brush("Header");
    public static IBrush Field => Brush("Field");

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    // ── Controls ──

    public static TextBlock Label(string text, IBrush? color = null, double size = 13, bool wrap = false, bool bold = false) => new()
    {
        Text = text,
        Foreground = color ?? Text,
        FontSize = size,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>A header: spaced capitals in the display font of the theme (it follows a change of theme).</summary>
    public static TextBlock Caption(string text, IBrush? color = null, double size = 10)
    {
        var label = Label(text.ToUpperInvariant(), color ?? Muted, size);
        label.LetterSpacing = 1;
        label.Bind(TextBlock.FontFamilyProperty, label.GetResourceObservable("DisplayFont", v => v as FontFamily ?? FontFamily.Default));
        label.Bind(TextBlock.FontWeightProperty, label.GetResourceObservable("DisplayWeight", v => v is FontWeight w ? w : FontWeight.SemiBold));
        return label;
    }

    public static Button Button(string text, Action? click = null, string? tooltip = null, bool primary = false)
    {
        var button = new Button { Content = text };
        if (primary)
            button.Classes.Add("primary");
        if (tooltip is not null)
            ToolTip.SetTip(button, tooltip);
        if (click is not null)
            button.Click += (_, _) => click();
        return button;
    }

    public static Button Command(string text, ICommand command, object? parameter = null, string? tooltip = null, bool primary = false)
    {
        var button = Button(text, null, tooltip, primary);
        button.Command = command;
        button.CommandParameter = parameter;
        return button;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        row.Children.AddRange(children);
        return row;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var column = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        column.Children.AddRange(children);
        return column;
    }

    /// <summary>A DockPanel: the children docked in the order given, the last one filling.</summary>
    public static DockPanel Dock(params (Control Control, Avalonia.Controls.Dock Side)[] children)
    {
        var panel = new DockPanel { LastChildFill = true };
        foreach (var (control, side) in children)
        {
            DockPanel.SetDock(control, side);
            panel.Children.Add(control);
        }
        return panel;
    }

    public static Border Box(Control child, double padding = 8, IBrush? background = null, IBrush? border = null) => new()
    {
        Child = child,
        Padding = new Thickness(padding),
        Background = background ?? Panel,
        BorderBrush = border ?? Edge,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(2)
    };

    public static ScrollViewer Scroll(Control child) => new()
    {
        Content = child,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };

    // ── Bindings ──

    /// <summary>Binds <paramref name="property"/> of <paramref name="control"/> to <paramref name="path"/> of <paramref name="source"/>.</summary>
    public static T Bind<T>(this T control, AvaloniaProperty property, object source, string path, BindingMode mode = BindingMode.Default,
        Func<object?, object?>? convert = null) where T : AvaloniaObject
    {
        var binding = new Binding(path) { Source = source, Mode = mode };
        if (convert is not null)
            binding.Converter = new Avalonia.Data.Converters.FuncValueConverter<object?, object?>(convert);
        control.Bind(property, binding);
        return control;
    }

    public static TextBox Edit(object source, string path, string? placeholder = null, bool multiline = false, double height = 0)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        if (height > 0)
            box.MinHeight = height;
        return box.Bind(TextBox.TextProperty, source, path, BindingMode.TwoWay);
    }

    public static TextBlock Bound(object source, string path, IBrush? color = null, double size = 13, bool wrap = false) =>
        Label(string.Empty, color, size, wrap).Bind(TextBlock.TextProperty, source, path);

    public static CheckBox Check(string text, object source, string path) =>
        new CheckBox { Content = text }.Bind(ToggleButton_IsChecked, source, path, BindingMode.TwoWay);

    private static AvaloniaProperty ToggleButton_IsChecked => Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty;

    public static ComboBox Combo(IEnumerable items, object source, string selectedPath, Func<object?, string>? name = null)
    {
        var combo = new ComboBox { ItemsSource = items, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (name is not null)
            combo.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object?>((item, _) => new TextBlock { Text = name(item) });
        return combo.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, source, selectedPath, BindingMode.TwoWay);
    }

    public static NumericUpDown Number(object source, string path, double min, double max, double step = 1) =>
        new NumericUpDown { Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, FormatString = "0.##" }
            .Bind(NumericUpDown.ValueProperty, source, path, BindingMode.TwoWay);

    public static T Visible<T>(this T control, object source, string path, bool invert = false) where T : Control =>
        control.Bind(Visual.IsVisibleProperty, source, path, BindingMode.OneWay, v => (v is true) != invert);

    /// <summary>Runs <paramref name="update"/> now and each time one of <paramref name="properties"/> of <paramref name="source"/> changes (all when none given), while <paramref name="owner"/> is shown.</summary>
    public static void When(Control owner, INotifyPropertyChanged source, Action update, params string[] properties)
    {
        void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (properties.Length == 0 || properties.Contains(e.PropertyName))
                OnMain(update);
        }
        source.PropertyChanged += Changed;
        owner.DetachedFromVisualTree += (_, _) => source.PropertyChanged -= Changed;
        owner.AttachedToVisualTree += (_, _) =>
        {
            source.PropertyChanged -= Changed;
            source.PropertyChanged += Changed;
        };
        update();
    }

    /// <summary>Runs <paramref name="action"/> on the thread of the interface (at once when already there).</summary>
    public static void OnMain(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    /// <summary>Executes <paramref name="command"/> when it can.</summary>
    public static void Run(ICommand? command, object? parameter = null)
    {
        if (command?.CanExecute(parameter) == true)
            command.Execute(parameter);
    }
}
