using System;
using System.ComponentModel;
using System.Windows.Input;
using Godot;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// The pieces shared by the dashboards: a card (a captioned panel), a key figure, the busy bar of a running command, a
/// status bar with that busy bar, a red / green dot, a thin share bar, a collapsible section.
/// </summary>
internal static class DashUi
{
    /// <summary>The colours of the series of the Avalonia charts (kept so the graphs read the same).</summary>
    public static readonly Color Blue = Color.FromHtml("#2B9BF4");
    public static readonly Color Orange = Color.FromHtml("#F28C38");
    public static readonly Color ErrorRed = Color.FromHtml("#E81123");

    /// <summary>A dark panel with a hairline edge around <paramref name="content"/>, with an optional caption on top.</summary>
    public static PanelContainer Card(Control content, string? caption = null, int padding = 10)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", EveStyle.Box(Eve.Eve.Raised(0.025f), EveStyle.Edge with { A = 0.35f }, padding));
        if (caption is null)
            panel.AddChild(content);
        else
        {
            var title = EveStyle.Caption(caption);
            title.ClipText = true;
            title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            title.TooltipText = caption;
            title.MouseFilter = Control.MouseFilterEnum.Pass;
            panel.AddChild(Ui.Column(6, title, Ui.FillHeight(content)));
        }
        return panel;
    }

    /// <summary>A card whose caption is the string property <paramref name="captionProperty"/>, with <paramref name="right"/> at its right.</summary>
    public static PanelContainer Card(Control content, INotifyPropertyChanged source, string captionProperty, params Control[] right)
    {
        var caption = EveStyle.Caption("");
        Bind.Text(caption, source, captionProperty, v => (v?.ToString() ?? string.Empty).ToUpperInvariant());
        caption.ClipText = true;
        caption.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var header = Ui.Row(10, Ui.Fill(caption));
        foreach (var control in right)
            header.AddChild(control);
        return Card(Ui.Column(6, header, Ui.FillHeight(content)));
    }

    /// <summary>A key figure: its caption, the value (large) and a line of details.</summary>
    public static PanelContainer Kpi(INotifyPropertyChanged source, string caption, string value, string details, Color? color = null, string? colorProperty = null)
    {
        var big = Ui.Label(source, value, color ?? EveStyle.TextBright, 26);
        big.AddThemeFontOverride("font", EveStyle.Body);
        if (colorProperty is not null)
            Bind.When(big, source, () => big.AddThemeColorOverride("font_color", AvaloniaBridge.ToColor(Bind.Get(source, colorProperty) as Avalonia.Media.IBrush)), colorProperty);
        var line = Ui.Label(source, details, EveStyle.Muted, 11);
        line.ClipText = true;
        line.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        Bind.When(line, source, () => line.TooltipText = Bind.Get(source, details)?.ToString() ?? string.Empty, details);
        return Card(Ui.Column(2, EveStyle.Caption(caption), big, line));
    }

    /// <summary>A small indeterminate bar shown while <paramref name="command"/> runs (its <c>IsRunning</c>).</summary>
    public static ProgressBar Busy(ICommand command, int width = 60)
    {
        var progress = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(width, 4), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        if (command is INotifyPropertyChanged notify)
            Bind.Visible(progress, notify, "IsRunning");
        else
            progress.Visible = false;
        return progress;
    }

    /// <summary>The status bar of a tool (<see cref="Ui.StatusBar"/>) with the busy bar of <paramref name="commands"/> in front.</summary>
    public static PanelContainer StatusBar(INotifyPropertyChanged source, string status, string? error, params ICommand[] commands)
    {
        var bar = Ui.StatusBar(source, status, error);
        var row = bar.GetChild<HBoxContainer>(0);
        foreach (var command in commands)
        {
            var busy = Busy(command);
            row.AddChild(busy);
            row.MoveChild(busy, 0);
        }
        return bar;
    }

    /// <summary>A coloured dot shown while the bool property <paramref name="name"/> is true.</summary>
    public static Label Dot(Color color, INotifyPropertyChanged source, string name, bool invert = false) =>
        Bind.Visible(EveStyle.Text_("●", color, 10), source, name, invert);

    /// <summary>A thin bar showing a share (0–100).</summary>
    public static ProgressBar Share(double percent, int height = 5)
    {
        var bar = new ProgressBar { MinValue = 0, MaxValue = 100, Value = percent, ShowPercentage = false, CustomMinimumSize = new Vector2(0, height), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        var back = EveStyle.Box(Eve.Eve.Raised(0.06f), EveStyle.Edge with { A = 0 }, 0, 0);
        var fill = EveStyle.Box(EveStyle.Cyan with { A = 0.75f }, EveStyle.Cyan with { A = 0 }, 0, 0);
        bar.AddThemeStyleboxOverride("background", back);
        bar.AddThemeStyleboxOverride("fill", fill);
        return bar;
    }

    /// <summary>A section with a header that folds and unfolds <paramref name="content"/>.</summary>
    public static VBoxContainer Expander(string title, Control content, bool expanded, Action<bool>? toggled = null)
    {
        var header = new Button { Flat = true, Alignment = HorizontalAlignment.Left, ToggleMode = true, ButtonPressed = expanded, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        header.AddThemeFontOverride("font", EveStyle.Display);
        header.AddThemeFontSizeOverride("font_size", 11);
        header.AddThemeColorOverride("font_color", EveStyle.Cyan);
        header.AddThemeColorOverride("font_pressed_color", EveStyle.Cyan);
        void Apply(bool on)
        {
            header.Text = (on ? "▾  " : "▸  ") + title.ToUpperInvariant();
            content.Visible = on;
        }
        header.Toggled += on =>
        {
            Apply(on);
            toggled?.Invoke(on);
        };
        Apply(expanded);
        return Ui.Column(6, EveStyle.Separator(), header, content);
    }

    /// <summary>Red edges on <paramref name="edit"/> while the bool property <paramref name="invalid"/> is true.</summary>
    public static T InvalidEdge<T>(T edit, INotifyPropertyChanged source, string invalid) where T : Control
    {
        var red = EveStyle.Box(EveStyle.Field, EveStyle.Red, 6);
        Bind.When(edit, source, () =>
        {
            if (Bind.Get(source, invalid) is true)
            {
                edit.AddThemeStyleboxOverride("normal", red);
                edit.AddThemeStyleboxOverride("focus", red);
            }
            else
            {
                edit.RemoveThemeStyleboxOverride("normal");
                edit.RemoveThemeStyleboxOverride("focus");
            }
        }, invalid);
        return edit;
    }

    /// <summary>A mono single-line text bound both ways to <paramref name="name"/>.</summary>
    public static LineEdit MonoLine(INotifyPropertyChanged source, string name, string placeholder = "")
    {
        var edit = Ui.Line(source, name, placeholder);
        edit.AddThemeFontOverride("font", EveStyle.Mono);
        return edit;
    }

    /// <summary>Sets the title of the window holding <paramref name="content"/> once it is open.</summary>
    public static void TitleWhenOpen(Control content, Shell shell, string title)
    {
        void Set()
        {
            if (shell.WindowOf(content) is { } window)
                window.Title = title;
        }
        content.Ready += () => Callable.From(Set).CallDeferred();
    }
}
