using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace KAONOMI.UI.Desk;

/// <summary>
/// The view of a view model that has no view written by hand: built from what the view model offers, by reflection.
/// Its commands are buttons, its properties that can be set are fields (text, number, check box, list of an enum), the
/// others are shown, and each of its collections is a list, bound to the <c>Selected…</c> property of its type when
/// there is one, the fields of the item selected under it. Plain, but every tool of the chat opens and works.
/// </summary>
public static class AutoView
{
    private static readonly HashSet<string> Hidden = ["HasErrors", "IsBusy", "Errors"];

    /// <summary>"GhostWriterWindowViewModel" → "Ghost Writer".</summary>
    public static string TitleOf(Type type)
    {
        var name = Regex.Replace(type.Name, "(Window)?(Dialog)?ViewModel$", string.Empty);
        return Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
    }

    public static Control Build(object viewModel, Shell shell)
    {
        var column = new StackPanel { Spacing = 10, Margin = new Thickness(12) };
        column.Children.Add(Ui.Label("Generated view: the commands and fields of the tool, without its own layout.", Ui.Muted, 11, wrap: true));

        var commands = Commands(viewModel);
        if (commands.Children.Count > 0)
            column.Children.Add(commands);
        if (Form(viewModel, Ui.Muted) is { } form)
            column.Children.Add(form);
        foreach (var list in Lists(viewModel))
            column.Children.Add(list);
        return Ui.Scroll(column);
    }

    private static IEnumerable<PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && !Hidden.Contains(p.Name))
            .OrderBy(p => p.MetadataToken);

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
               || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(DateOnly) || type == typeof(TimeOnly);
    }

    private static string Name(string property) => Regex.Replace(Regex.Replace(property, "Command$", string.Empty), "(?<=[a-z0-9])(?=[A-Z])", " ");

    /// <summary>The commands without a parameter, as buttons (disabled while they cannot run).</summary>
    private static WrapPanel Commands(object viewModel)
    {
        var panel = new WrapPanel();
        foreach (var property in Properties(viewModel.GetType()).Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType)))
        {
            if (property.GetValue(viewModel) is not ICommand command)
                continue;
            // A command typed with a parameter (RelayCommand<T>) needs an item: offered in the lists instead.
            if (command.GetType().IsGenericType && command.GetType().GetGenericArguments().FirstOrDefault() is { } argument && argument != typeof(object))
                continue;
            var button = new Button { Content = Name(property.Name), Command = command, Margin = new Thickness(0, 0, 6, 6) };
            panel.Children.Add(button);
        }
        return panel;
    }

    /// <summary>The scalar properties of <paramref name="source"/>: fields for those that can be set, text for the others.</summary>
    private static Control? Form(object source, IBrush labels)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 4, ColumnSpacing = 10 };
        var row = 0;
        foreach (var property in Properties(source.GetType()).Where(p => IsScalar(p.PropertyType)))
        {
            var editor = Editor(source, property);
            if (editor is null)
                continue;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Ui.Label(Name(property.Name), labels, 11);
            Grid.SetRow(label, row);
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            grid.Children.Add(label);
            grid.Children.Add(editor);
            row++;
        }
        return row == 0 ? null : grid;
    }

    private static Control? Editor(object source, PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var canWrite = property.CanWrite && property.SetMethod?.IsPublic == true;
        var path = property.Name;
        if (!canWrite)
        {
            // A long text (a report, a result) wraps.
            return Ui.Label(string.Empty, Ui.Text, 12, wrap: true).Bind(TextBlock.TextProperty, source, path, BindingMode.OneWay, v => Format(v));
        }
        if (type == typeof(bool))
            return new CheckBox().Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, source, path, BindingMode.TwoWay);
        if (type.IsEnum)
            return new ComboBox { ItemsSource = Enum.GetValues(type), MinWidth = 160 }
                .Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, source, path, BindingMode.TwoWay);
        if (type == typeof(int) || type == typeof(double) || type == typeof(float) || type == typeof(long) || type == typeof(decimal))
            return new NumericUpDown { MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left, FormatString = type == typeof(int) || type == typeof(long) ? "0" : "0.###" }
                .Bind(NumericUpDown.ValueProperty, source, path, BindingMode.TwoWay);
        if (type == typeof(string))
        {
            var long_ = Regex.IsMatch(path, "Text|Content|Prompt|Body|Markdown|Description|Notes?|Script|Json|Query|Code|Answer|Result", RegexOptions.IgnoreCase);
            var box = new TextBox { AcceptsReturn = long_, TextWrapping = long_ ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = long_ ? 90 : 0, MaxHeight = 360 };
            return box.Bind(TextBox.TextProperty, source, path, BindingMode.TwoWay);
        }
        // Dates and times: as text, kept as they are when typed wrong.
        return new TextBox().Bind(TextBox.TextProperty, source, path, BindingMode.TwoWay);
    }

    private static string Format(object? value) => value switch
    {
        null => "—",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm"),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm"),
        double d => d.ToString("0.###"),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>The collections of the view model, each a list (bound to its <c>Selected…</c> property), the fields of the item selected under it.</summary>
    private static IEnumerable<Control> Lists(object viewModel)
    {
        var properties = Properties(viewModel.GetType()).ToList();
        foreach (var property in properties.Where(p => p.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(p.PropertyType)))
        {
            if (property.GetValue(viewModel) is not IEnumerable items)
                continue;
            var element = property.PropertyType.GetInterfaces().Append(property.PropertyType)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0] ?? typeof(object);
            // A list of choices for a field (the models, the languages…) is already the field's: kept short.
            var selected = properties.FirstOrDefault(p => p.Name.StartsWith("Selected", StringComparison.Ordinal) && p.CanWrite
                && p.PropertyType != typeof(object) && p.PropertyType.IsAssignableFrom(element) && element != typeof(string)
                && (p.Name == "Selected" + Singular(property.Name) || properties.Count(q => q.Name.StartsWith("Selected") && q.PropertyType.IsAssignableFrom(element)) == 1));

            var list = new ListBox
            {
                ItemsSource = items,
                MaxHeight = 320,
                ItemTemplate = new FuncDataTemplate<object?>((item, _) => new TextBlock { Text = Summary(item), TextTrimming = TextTrimming.CharacterEllipsis })
            };
            if (selected is not null)
                list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, viewModel, selected.Name, BindingMode.TwoWay);
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(Ui.Caption($"{Name(property.Name)}"));
            block.Children.Add(Ui.Box(list, 0, Ui.Field));
            // The fields of the item selected (an entry of the tool: its title, its text…).
            if (!IsScalar(element) && element != typeof(object))
            {
                var detail = new ContentControl();
                list.SelectionChanged += (_, _) =>
                {
                    detail.Content = list.SelectedItem is { } item && !IsScalar(item.GetType())
                        ? Ui.Box(new StackPanel { Spacing = 6, Children = { ItemCommands(item), Form(item, Ui.Muted) ?? new Panel() } }, 8, Ui.Header)
                        : null;
                };
                block.Children.Add(detail);
            }
            yield return block;
        }
    }

    private static Control ItemCommands(object item) => Commands(item);

    private static string Singular(string name) => name.EndsWith("ies") ? name[..^3] + "y" : name.EndsWith('s') ? name[..^1] : name;

    private static readonly string[] SummaryNames = ["Title", "Name", "Label", "DisplayName", "Header", "Text", "Summary", "Description", "Content", "Prompt", "Question", "Path", "Url"];

    private static string Summary(object? item)
    {
        if (item is null)
            return string.Empty;
        var type = item.GetType();
        if (IsScalar(type))
            return Format(item);
        foreach (var name in SummaryNames)
            if (type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { } p && p.PropertyType == typeof(string) && p.GetValue(item) is string { Length: > 0 } text)
                return text.ReplaceLineEndings(" ");
        var shown = item.ToString() ?? string.Empty;
        return shown == type.FullName ? type.Name : shown;
    }
}
