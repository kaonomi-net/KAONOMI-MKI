using System;
using System.ComponentModel;
using Godot;
using KAONOMI.Chat.Services;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The pieces the views are made of, in the look of the chat: rows and columns with spacing, a search bar, a captioned
/// field, a status bar, badges, Markdown, a split view, a content swapped with a property (the selected item).
/// </summary>
public static class Ui
{
    /// <summary>A row of <paramref name="children"/>, <paramref name="spacing"/> apart.</summary>
    public static HBoxContainer Row(int spacing, params Control[] children)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", spacing);
        foreach (var child in children)
            row.AddChild(child);
        return row;
    }

    /// <summary>A column of <paramref name="children"/>, <paramref name="spacing"/> apart.</summary>
    public static VBoxContainer Column(int spacing, params Control[] children)
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", spacing);
        foreach (var child in children)
            column.AddChild(child);
        return column;
    }

    /// <summary><paramref name="control"/> taking the free width (and height when <paramref name="vertical"/>).</summary>
    public static T Fill<T>(T control, bool vertical = false) where T : Control
    {
        control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (vertical)
            control.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return control;
    }

    /// <summary><paramref name="control"/> taking the free height.</summary>
    public static T FillHeight<T>(T control) where T : Control
    {
        control.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return control;
    }

    /// <summary>A margin of <paramref name="all"/> pixels around <paramref name="child"/>.</summary>
    public static MarginContainer Margin(Control child, int all = 8) => Margin(child, all, all, all, all);

    public static MarginContainer Margin(Control child, int left, int top, int right, int bottom)
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", left);
        margin.AddThemeConstantOverride("margin_top", top);
        margin.AddThemeConstantOverride("margin_right", right);
        margin.AddThemeConstantOverride("margin_bottom", bottom);
        margin.AddChild(child);
        return margin;
    }

    /// <summary>An empty space taking the free width (pushes what follows to the right).</summary>
    public static Control Spring() => new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>A vertical scroll around <paramref name="child"/> (no horizontal scroll).</summary>
    public static ScrollContainer Scroll(Control child)
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        child.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(child);
        return scroll;
    }

    /// <summary>A side-by-side split, the left part <paramref name="leftWidth"/> wide at first (the bar between them moves).</summary>
    public static HSplitContainer SplitH(Control left, Control right, int leftWidth)
    {
        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SplitOffset = leftWidth };
        left.CustomMinimumSize = left.CustomMinimumSize with { X = Math.Max(left.CustomMinimumSize.X, 160) };
        split.AddChild(left);
        split.AddChild(right);
        return split;
    }

    /// <summary>A split one above the other, the top part <paramref name="topHeight"/> high at first.</summary>
    public static VSplitContainer SplitV(Control top, Control bottom, int topHeight)
    {
        var split = new VSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SplitOffset = topHeight };
        split.AddChild(top);
        split.AddChild(bottom);
        return split;
    }

    /// <summary>A small caption above <paramref name="field"/>.</summary>
    public static VBoxContainer Field(string caption, Control field)
    {
        field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        // The column takes its share of the width too (in a grid, a row, a split).
        return Fill(Column(3, Eve.Caption(caption), field));
    }

    /// <summary>A single-line text bound both ways to <paramref name="name"/>.</summary>
    public static LineEdit Line(INotifyPropertyChanged source, string name, string placeholder = "") =>
        Bind.Edit(new LineEdit { PlaceholderText = placeholder, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, source, name);

    /// <summary>A multi-line text bound both ways to <paramref name="name"/>, <paramref name="height"/> high at least.</summary>
    public static TextEdit Lines(INotifyPropertyChanged source, string name, int height = 80, string placeholder = "", bool mono = false)
    {
        var edit = new TextEdit
        {
            PlaceholderText = placeholder,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, height),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        if (mono)
            edit.AddThemeFontOverride("font", Eve.Mono);
        return Bind.Edit(edit, source, name);
    }

    /// <summary>A check box bound both ways to <paramref name="name"/>.</summary>
    public static CheckBox Check(string text, INotifyPropertyChanged source, string name, string tooltip = "") =>
        Bind.Check(new CheckBox { Text = text, TooltipText = tooltip }, source, name);

    /// <summary>A button running <paramref name="command"/>.</summary>
    public static Button Command(string text, System.Windows.Input.ICommand? command, bool primary = false, string tooltip = "", Func<object?>? parameter = null) =>
        Bind.Command(Eve.Button(text, primary, tooltip), command, parameter);

    /// <summary>A button running <paramref name="action"/>.</summary>
    public static Button Action(string text, Action action, bool primary = false, string tooltip = "")
    {
        var button = Eve.Button(text, primary, tooltip);
        button.Pressed += action;
        return button;
    }

    /// <summary>A label bound to <paramref name="name"/>.</summary>
    public static Label Label(INotifyPropertyChanged source, string name, Color? color = null, int size = 13, bool wrap = false, Func<object?, string>? format = null) =>
        Bind.Text(Eve.Text_("", color, size, wrap), source, name, format);

    /// <summary>A small coloured tag (deleted, new, modified…), shown while <paramref name="visibleWhen"/> is true.</summary>
    public static PanelContainer Badge(string text, Color color, INotifyPropertyChanged? source = null, string? visibleWhen = null)
    {
        var badge = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
        badge.AddThemeStyleboxOverride("panel", Eve.Box(color with { A = 0.22f }, color with { A = 0.7f }, 0));
        var label = Eve.Caption(text, color.Lightened(0.2f), 9);
        badge.AddChild(Margin(label, 5, 1, 5, 1));
        if (source is not null && visibleWhen is not null)
            Bind.Visible(badge, source, visibleWhen);
        return badge;
    }

    /// <summary>
    /// The search bar of a tool: a text bound to <paramref name="term"/> (Enter runs <paramref name="search"/>), the
    /// Search button, then <paramref name="more"/> buttons.
    /// </summary>
    public static HBoxContainer SearchBar(INotifyPropertyChanged source, string term, System.Windows.Input.ICommand? search, string placeholder, params Control[] more)
    {
        var edit = Line(source, term, placeholder);
        edit.TextSubmitted += _ => Bind.Run(search);
        var row = Row(6, edit, Command("SEARCH", search, primary: true));
        foreach (var control in more)
            row.AddChild(control);
        return row;
    }

    /// <summary>
    /// The status bar of a tool: a progress while <paramref name="busy"/> is true (when given), a red dot while
    /// <paramref name="error"/> is true (when given), the text of <paramref name="status"/>.
    /// </summary>
    public static PanelContainer StatusBar(INotifyPropertyChanged source, string status, string? error = "IsError", string? busy = null)
    {
        var panel = new PanelContainer();
        var box = Eve.Box(Eve.Sunken(0.25f), Eve.Edge with { A = 0f }, 0, 0);
        box.BorderWidthTop = 1;
        box.BorderColor = Eve.Edge with { A = 0.4f };
        box.ContentMarginLeft = box.ContentMarginRight = 8;
        box.ContentMarginTop = box.ContentMarginBottom = 3;
        panel.AddThemeStyleboxOverride("panel", box);
        var row = Row(8);
        if (busy is not null)
        {
            var progress = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(60, 4), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            Bind.Visible(progress, source, busy);
            row.AddChild(progress);
        }
        // One line: a long message (a server error with its stack trace) is cut, whole in the tooltip.
        var text = Eve.Text_("", Eve.Muted, 11);
        text.ClipText = true;
        text.MaxLinesVisible = 1;
        text.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        text.MouseFilter = Control.MouseFilterEnum.Pass;
        Bind.When(text, source, () =>
        {
            var message = Bind.Get(source, status)?.ToString() ?? string.Empty;
            var first = message.Split('\n')[0].Trim();
            text.Text = first;
            text.TooltipText = message.Length > first.Length ? message : string.Empty;
        }, status);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (error is not null && source.GetType().GetProperty(error) is not null)
        {
            var dot = Eve.Text_("●", Eve.Red, 10);
            Bind.Visible(dot, source, error);
            row.AddChild(dot);
            Bind.When(text, source, () => text.AddThemeColorOverride("font_color", Bind.Get(source, error) is true ? Eve.Red : Eve.Muted), error);
        }
        row.AddChild(text);
        panel.AddChild(row);
        return panel;
    }

    /// <summary>A header strip at the top of a tool (search, actions).</summary>
    public static PanelContainer Toolbar(Control content)
    {
        var panel = new PanelContainer();
        var box = Eve.Box(Eve.Raised(0.025f), Eve.Edge with { A = 0f }, 0, 0);
        box.BorderWidthBottom = 1;
        box.BorderColor = Eve.Edge with { A = 0.35f };
        box.ContentMarginLeft = box.ContentMarginRight = 4;
        box.ContentMarginTop = box.ContentMarginBottom = 6;
        panel.AddThemeStyleboxOverride("panel", box);
        panel.AddChild(content);
        return panel;
    }

    /// <summary>A read-only rich text showing <paramref name="markdown"/> (LaTeX turned into Unicode symbols).</summary>
    public static RichTextLabel Markdown(string markdown = "")
    {
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            SelectionEnabled = true,
            ContextMenuEnabled = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        SetMarkdown(label, markdown);
        return label;
    }

    /// <summary>Shows <paramref name="markdown"/> in <paramref name="label"/>.</summary>
    public static void SetMarkdown(RichTextLabel label, string? markdown) =>
        label.Text = MarkdownBbcode.Convert(LatexText.Convert(markdown ?? string.Empty));

    /// <summary>A Markdown view of the string property <paramref name="name"/>.</summary>
    public static RichTextLabel Markdown(INotifyPropertyChanged source, string name)
    {
        var label = Markdown();
        Bind.When(label, source, () => SetMarkdown(label, Bind.Get(source, name)?.ToString()), name);
        return label;
    }

    /// <summary>A text shown in the middle of an empty area.</summary>
    public static Label Placeholder(string text)
    {
        var label = Eve.Text_(text, Eve.Muted, 13, wrap: true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return label;
    }

    /// <summary>
    /// A holder whose content is built again by <paramref name="build"/> from the value of <paramref name="name"/>
    /// whenever it changes (the editor of the selected item); <paramref name="build"/> may return null (nothing shown).
    /// </summary>
    public static MarginContainer Swap(INotifyPropertyChanged source, string name, Func<object?, Control?> build)
    {
        var holder = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        object? shown = null;
        var first = true;
        Bind.When(holder, source, () =>
        {
            var value = Bind.Get(source, name);
            if (!first && ReferenceEquals(value, shown))
                return;
            first = false;
            shown = value;
            foreach (var child in holder.GetChildren())
                child.QueueFree();
            if (build(value) is { } content)
            {
                content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
                holder.AddChild(content);
            }
        }, name);
        return holder;
    }

    /// <summary>A grid of <paramref name="columns"/> columns.</summary>
    public static GridContainer Grid(int columns, int hSpacing = 10, int vSpacing = 6)
    {
        var grid = new GridContainer { Columns = columns, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", hSpacing);
        grid.AddThemeConstantOverride("v_separation", vSpacing);
        return grid;
    }

    /// <summary>A picture (keeps its proportions in its area).</summary>
    public static TextureRect Picture(Texture2D? texture = null, int minHeight = 120) => new()
    {
        Texture = texture,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        CustomMinimumSize = new Vector2(0, minHeight),
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
    };
}
