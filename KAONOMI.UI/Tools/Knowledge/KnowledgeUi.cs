using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// Small pieces shared by the views of the knowledge tools (ontology, entity, relations, diagrams): a context menu built
/// from commands, a selectable text, a card, the keyboard shortcuts of a window, a status bar, a toolbar, the window
/// holding a control (its title changed), an annotation, a picture of a control saved as PNG.
/// </summary>
internal static class KnowledgeUi
{
    // ── Texts ──

    /// <summary>A read-only text that can be selected and copied, bound to <paramref name="path"/>.</summary>
    public static SelectableTextBlock Selectable(object source, string path, IBrush? color = null, double size = 12)
    {
        var text = Selectable(string.Empty, color, size);
        text.Bind(TextBlock.TextProperty, source, path, BindingMode.OneWay);
        return text;
    }

    /// <summary>A read-only text that can be selected and copied.</summary>
    public static SelectableTextBlock Selectable(string text, IBrush? color = null, double size = 12) => new()
    {
        Text = text,
        Foreground = color ?? Ui.Text,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>A text shown in the middle of an empty area.</summary>
    public static TextBlock Placeholder(string text)
    {
        var label = Ui.Label(text, Ui.Muted, 13, wrap: true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextAlignment = TextAlignment.Center;
        label.IsHitTestVisible = false;
        label.Margin = new Thickness(20);
        return label;
    }

    /// <summary>A label cut with an ellipsis (one line), its whole text in its tooltip.</summary>
    public static TextBlock Clip(TextBlock label)
    {
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        return label;
    }

    // ── Layout ──

    /// <summary>A panel with a hairline edge around <paramref name="content"/>.</summary>
    public static Border Card(Control content, IBrush? edge = null, double margin = 10, double border = 1) => new()
    {
        Child = content,
        Padding = new Thickness(margin),
        Background = Ui.Panel,
        BorderBrush = edge ?? Ui.Edge,
        BorderThickness = new Thickness(border),
        CornerRadius = new CornerRadius(2)
    };

    /// <summary>Children laid over each other, each filling the whole area (the first one at the back).</summary>
    public static Panel Overlay(params Control[] children)
    {
        var panel = new Panel();
        panel.Children.AddRange(children);
        return panel;
    }

    /// <summary>A small caption above <paramref name="field"/>.</summary>
    public static StackPanel Field(string caption, Control field)
    {
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        return Ui.Column(3, Ui.Caption(caption), field);
    }

    /// <summary>Two (or more) controls side by side, sharing the width.</summary>
    public static Grid Pair(double spacing, params Control[] children)
    {
        var grid = new Grid { ColumnSpacing = spacing };
        for (var i = 0; i < children.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(children[i], i);
            grid.Children.Add(children[i]);
        }
        return grid;
    }

    /// <summary>A row of controls where <paramref name="fill"/> takes the free width (the others at its left and right, in order).</summary>
    public static Grid FillRow(double spacing, Control[] left, Control fill, params Control[] right)
    {
        var grid = new Grid { ColumnSpacing = spacing };
        var column = 0;
        void Add(Control control, GridLength width)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(width));
            Grid.SetColumn(control, column++);
            control.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(control);
        }
        foreach (var c in left)
            Add(c, GridLength.Auto);
        Add(fill, GridLength.Star);
        foreach (var c in right)
            Add(c, GridLength.Auto);
        return grid;
    }

    /// <summary>A side-by-side split, the left part <paramref name="leftWidth"/> wide at first (the bar between them moves).</summary>
    public static Grid SplitH(Control left, Control right, double leftWidth, double minLeft = 160)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(leftWidth, GridUnitType.Pixel) { MinWidth = minLeft });
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star) { MinWidth = 160 });
        var splitter = new GridSplitter { Width = 5, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(splitter);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A side-by-side split, the right part <paramref name="rightWidth"/> wide at first.</summary>
    public static Grid SplitRight(Control left, Control right, double rightWidth, double minRight = 200)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star) { MinWidth = 200 });
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(rightWidth, GridUnitType.Pixel) { MinWidth = minRight });
        var splitter = new GridSplitter { Width = 6, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(splitter);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A row of <paramref name="children"/> going on to the next line when the window is narrow.</summary>
    public static WrapPanel Flow(double spacing, params Control[] children)
    {
        var panel = new WrapPanel { ItemSpacing = spacing, LineSpacing = spacing };
        foreach (var child in children)
        {
            child.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(child);
        }
        return panel;
    }

    /// <summary>A horizontal rule.</summary>
    public static Border Separator() => new() { Height = 1, Background = Ui.Edge, Margin = new Thickness(0, 6, 0, 2) };

    /// <summary>A header strip at the top of a tool (search, actions).</summary>
    public static Border Toolbar(Control content) => new()
    {
        Child = content,
        Background = Ui.Header,
        BorderBrush = Ui.Edge,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(6, 6)
    };

    /// <summary>
    /// The status bar of a tool: a progress while <paramref name="busy"/> is true (when given), a red dot while
    /// <paramref name="error"/> is true (when given), the first line of <paramref name="status"/> (the whole in its
    /// tooltip), then <paramref name="more"/> at the right.
    /// </summary>
    public static Border StatusBar(INotifyPropertyChanged source, string status, string? error = "IsError", string? busy = null, params Control[] more)
    {
        var row = new DockPanel { LastChildFill = true };
        foreach (var control in more)
        {
            DockPanel.SetDock(control, Dock.Right);
            control.Margin = new Thickness(8, 0, 0, 0);
            control.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(control);
        }
        if (busy is not null)
        {
            var progress = new ProgressBar { IsIndeterminate = true, Width = 60, Height = 4, MinHeight = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            progress.Visible(source, busy);
            DockPanel.SetDock(progress, Dock.Left);
            row.Children.Add(progress);
        }
        var text = Ui.Label(string.Empty, Ui.Muted, 11);
        if (error is not null && source.GetType().GetProperty(error) is not null)
        {
            var dot = Ui.Label("●", Ui.Red, 10);
            dot.Margin = new Thickness(0, 0, 6, 0);
            dot.Visible(source, error);
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
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(8, 3)
        };
    }

    /// <summary>A small coloured tag (deleted, new, modified…), shown while <paramref name="visibleWhen"/> of <paramref name="source"/> is true.</summary>
    public static Border Badge(string text, Color color, object? source = null, string? visibleWhen = null)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(Paint.Alpha(color, 0.22)),
            BorderBrush = new SolidColorBrush(Paint.Alpha(color, 0.7)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 9, Foreground = new SolidColorBrush(Paint.Lighten(color, 0.2)) }
        };
        if (source is not null && visibleWhen is not null)
            badge.Visible(source, visibleWhen);
        return badge;
    }

    /// <summary>Three small marks under a slider: left, middle, right.</summary>
    public static Grid Marks(string left, string middle, string right)
    {
        TextBlock Mark(string text, TextAlignment alignment) => new() { Text = text, Foreground = Ui.Muted, FontSize = 10, TextAlignment = alignment, TextTrimming = TextTrimming.CharacterEllipsis };
        return Pair(0, Mark(left, TextAlignment.Left), Mark(middle, TextAlignment.Center), Mark(right, TextAlignment.Right));
    }

    /// <summary>A button lit (the look of a primary button) while <paramref name="active"/> is true; call the returned action to refresh it.</summary>
    public static void Light(Button button, bool active) => button.Classes.Set("primary", active);

    // ── Values ──

    /// <summary>The value of the property <paramref name="name"/> of <paramref name="source"/>.</summary>
    public static object? Get(object source, string name) => source.GetType().GetProperty(name)?.GetValue(source);

    /// <summary>"yyyy-MM-dd" of an optional date.</summary>
    public static string Format(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Runs <paramref name="command"/> with no parameter when it can run; true when it ran.</summary>
    public static bool TryRun(ICommand? command)
    {
        if (command?.CanExecute(null) != true)
            return false;
        command.Execute(null);
        return true;
    }

    // ── Keys ──

    /// <summary>
    /// Runs <paramref name="handler"/> on the keys pressed in <paramref name="owner"/> that its controls did not use
    /// (Ctrl+S, F5, Escape…); the handler returns true when it used the key.
    /// </summary>
    public static void Shortcuts(Control owner, Func<KeyEventArgs, bool> handler) =>
        owner.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!e.Handled && handler(e))
                e.Handled = true;
        }, RoutingStrategies.Bubble);

    /// <summary>True for Ctrl+<paramref name="key"/> (and ⌘+<paramref name="key"/> on macOS), without Alt.</summary>
    public static bool IsCommand(KeyEventArgs e, Key key) =>
        e.Key == key && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt);

    /// <summary>Gives the focus to <paramref name="control"/> once it is shown.</summary>
    public static void FocusLater(Control control, bool selectAll = false)
    {
        void Focus()
        {
            if (control.IsAttachedToVisualTree() && control.IsEffectivelyVisible)
            {
                control.Focus();
                if (selectAll && control is TextBox box)
                    box.SelectAll();
            }
        }
        if (control.IsAttachedToVisualTree())
            Dispatcher.UIThread.Post(Focus, DispatcherPriority.Background);
        else
        {
            void Attached(object? sender, VisualTreeAttachmentEventArgs e)
            {
                control.AttachedToVisualTree -= Attached;
                Dispatcher.UIThread.Post(Focus, DispatcherPriority.Background);
            }
            control.AttachedToVisualTree += Attached;
        }
    }

    /// <summary>Runs <paramref name="release"/> once <paramref name="owner"/> leaves the screen (its window closed).</summary>
    public static void OnGone(Control owner, Action release)
    {
        var done = false;
        owner.DetachedFromVisualTree += (_, _) =>
        {
            // A window only minimised or moved comes back: wait a little before releasing.
            Dispatcher.UIThread.Post(() =>
            {
                if (done || owner.IsAttachedToVisualTree())
                    return;
                done = true;
                release();
            }, DispatcherPriority.Background);
        };
    }

    // ── Windows ──

    /// <summary>The window of the desktop holding <paramref name="control"/>, or null.</summary>
    public static DeskWindow? WindowOf(Control? control) =>
        control?.FindAncestorOfType<DeskWindow>(true) is { } window ? window
        : control is null ? null
        : Shell.Current?.Deck.Windows.FirstOrDefault(w => ReferenceEquals(w.Content, control));

    /// <summary>Shows <paramref name="title"/> in the header (and the task bar button) of <paramref name="window"/>.</summary>
    public static void SetTitle(DeskWindow? window, string title)
    {
        if (window is null)
            return;
        var shown = $"{window.Icon}  {title}";
        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
            if (text.Text is { } current && current.StartsWith(window.Icon + "  ", StringComparison.Ordinal) && !window.Content.IsVisualAncestorOf(text))
            {
                text.Text = shown;
                break;
            }
        if (window.TaskButton is { } task)
            task.Content = $"{window.Icon} {title}";
    }

    /// <summary>Follows <paramref name="title"/> in the title of the window holding <paramref name="owner"/>.</summary>
    public static void FollowTitle(Control owner, INotifyPropertyChanged source, Func<string> title, params string[] properties)
    {
        void Apply() => SetTitle(WindowOf(owner), title());
        Ui.When(owner, source, Apply, properties);
        owner.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
    }

    /// <summary>Brings the window holding <paramref name="owner"/> to the front (when it is still open).</summary>
    public static void BringBack(Shell shell, Control owner) => Ui.OnMain(() =>
    {
        if (WindowOf(owner) is { } window && shell.Deck.Windows.Contains(window))
            shell.Deck.BringToFront(window);
    });

    // ── Annotations ──

    /// <summary>The item of the context menus.</summary>
    public const string AnnotateText = "📝 Annotate…";

    /// <summary>Opens the annotation of <paramref name="target"/>, written in the window holding <paramref name="origin"/>.</summary>
    public static void Annotate(Control? origin, AnnotationTarget target)
    {
        if (Shell.Current is not { } shell)
            return;
        var window = WindowOf(origin);
        var provider = window?.ViewModel as INoteContextProvider ?? (window is null ? shell.Chat as INoteContextProvider : null);
        var title = window?.Title ?? "Chat";
        var windowName = provider?.NoteWindowName ?? title;
        var context = NoteContext.Build(provider, windowName);
        shell.Open(new AnnotationDialogViewModel(KaonomiAssistantService.Shared, target, windowName, context) { SourceLink = window?.Key },
            title: $"Annotate · {target.Title ?? windowName}");
    }

    /// <summary>Shows a menu with "📝 Annotate…" at the pointer over <paramref name="at"/>.</summary>
    public static void AnnotateMenu(Control at, AnnotationTarget target) =>
        new Menu().Item(AnnotateText, () => Annotate(at, target)).Show(at);

    // ── Pictures ──

    /// <summary>
    /// Draws <paramref name="control"/> as it is shown into a PNG file (<paramref name="scale"/> times its size on
    /// screen); false when it cannot be drawn.
    /// </summary>
    public static bool SavePng(Control control, string path, double scale = 1)
    {
        var size = control.Bounds.Size;
        if (size.Width < 1 || size.Height < 1)
            return false;
        try
        {
            var pixels = new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
            using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
            bitmap.Render(control);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
#pragma warning disable CS0618 // The PNG encoder by default.
            bitmap.Save(path);
#pragma warning restore CS0618
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ActivityLog.Error("UI", "The picture could not be saved: " + path, ex);
            return false;
        }
    }

    /// <summary>Asks where to save a picture of <paramref name="control"/> (PNG) in <paramref name="folder"/>; the path saved, or null.</summary>
    public static async Task<string?> AskAndSavePng(Control control, string title, string suggested, string folder, double scale, Action<string, bool> report)
    {
        Directory.CreateDirectory(folder);
        var path = await Dialogs.SaveFile(title, suggested, ["*.png ; PNG picture"], folder);
        if (string.IsNullOrEmpty(path))
            return null;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            path += ".png";
        if (!SavePng(control, path, scale))
        {
            report("The picture could not be saved: " + path, true);
            return null;
        }
        report($"Picture saved: {path}", false);
        return path;
    }

    /// <summary>A file name made of <paramref name="name"/> (letters and digits, the rest as dashes).</summary>
    public static string Slug(string name, string fallback)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        return slug.Length == 0 ? fallback : slug;
    }
}

/// <summary>
/// A context menu: items running commands (disabled while they cannot run) or actions, captions, check items,
/// separators; shown at the pointer over a control.
/// </summary>
internal sealed class Menu
{
    private readonly ContextMenu _menu = new();

    /// <summary>An item running <paramref name="command"/>.</summary>
    public Menu Item(string text, ICommand? command, object? parameter = null, string tooltip = "") =>
        Item(text, () => Ui.Run(command, parameter), command?.CanExecute(parameter) == true, tooltip);

    /// <summary>An item running <paramref name="action"/>; <paramref name="gesture"/> written at its right.</summary>
    public Menu Item(string text, Action action, bool enabled = true, string tooltip = "", string? gesture = null, int indent = 0)
    {
        var item = new MenuItem { Header = new string(' ', indent * 4) + text, IsEnabled = enabled };
        if (gesture is not null)
            item.Header = new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = gesture, Foreground = Ui.Muted, Margin = new Thickness(24, 0, 0, 0), [DockPanel.DockProperty] = Dock.Right },
                    new TextBlock { Text = new string(' ', indent * 4) + text }
                }
            };
        item.Click += (_, _) => action();
        if (tooltip.Length > 0)
            ToolTip.SetTip(item, tooltip);
        _menu.Items.Add(item);
        return this;
    }

    /// <summary>A caption (not an action).</summary>
    public Menu Caption(string text)
    {
        _menu.Items.Add(new MenuItem { Header = new TextBlock { Text = text.ToUpperInvariant(), Foreground = Ui.Accent, FontSize = 10 }, IsEnabled = false });
        return this;
    }

    /// <summary>A check item showing <paramref name="on"/>; a click calls <paramref name="toggled"/> with the new state.</summary>
    public Menu Check(string text, bool on, Action<bool> toggled)
    {
        var item = new MenuItem { Header = text, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
        item.Click += (_, _) => toggled(!on);
        _menu.Items.Add(item);
        return this;
    }

    public Menu Separator()
    {
        if (_menu.Items.Count > 0 && _menu.Items[^1] is not Avalonia.Controls.Separator)
            _menu.Items.Add(new Avalonia.Controls.Separator());
        return this;
    }

    /// <summary>Shows the menu at the pointer over <paramref name="at"/>.</summary>
    public void Show(Control at)
    {
        _menu.Placement = PlacementMode.Pointer;
        _menu.Open(at);
    }
}
