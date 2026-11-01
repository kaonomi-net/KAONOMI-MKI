using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Godot;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// A node watching the input of the window it is in (each window of the desktop has its own viewport, so only the window
/// in use receives the keys) and the end of the drags and drops.
/// </summary>
public partial class Hooks : Node
{
    /// <summary>A key pressed; true when it was handled (the event then stops there).</summary>
    public Func<InputEventKey, bool>? Key { get; set; }

    /// <summary>Raised when a drag and drop ends (dropped or not).</summary>
    public Action? DragEnded { get; set; }

    public Hooks()
    {
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key && Key?.Invoke(key) == true)
            GetViewport().SetInputAsHandled();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationDragEnd)
            DragEnded?.Invoke();
    }
}

/// <summary>The helpers of the writing tools: shortcuts, the rows of the result lists, a date field, a text with choices.</summary>
public static class WritingUi
{
    /// <summary>
    /// Keyboard shortcuts of <paramref name="owner"/>: <paramref name="handler"/> receives the keys pressed while the
    /// focus is in it (or nowhere); true when it used the key.
    /// </summary>
    public static Hooks Shortcuts(Control owner, Func<InputEventKey, bool> handler)
    {
        var hooks = new Hooks
        {
            Key = key =>
            {
                if (!owner.IsVisibleInTree())
                    return false;
                // The active window of the deck only (every window receives every key), and not while the chat is typed in.
                if (Shell.Current is { } shell && !shell.HasKeyboard(owner))
                    return false;
                var focus = owner.GetViewport()?.GuiGetFocusOwner();
                if (focus is not null && focus != owner && !owner.IsAncestorOf(focus))
                    return false;
                return handler(key);
            }
        };
        owner.AddChild(hooks);
        return hooks;
    }

    /// <summary>A row of <paramref name="children"/> going on to the next line when the window is narrow.</summary>
    public static HFlowContainer Flow(int hSpacing, int vSpacing, params Control[] children)
    {
        var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", hSpacing);
        flow.AddThemeConstantOverride("v_separation", vSpacing);
        foreach (var child in children)
        {
            if (child is Button)
                child.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            flow.AddChild(child);
        }
        return flow;
    }

    /// <summary>A grid of <paramref name="columns"/> columns of the same width holding <paramref name="cells"/>.</summary>
    public static GridContainer Cells(int columns, params Control[] cells)
    {
        var grid = Ui.Grid(columns, 12, 10);
        foreach (var cell in cells)
        {
            cell.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            grid.AddChild(cell);
        }
        return grid;
    }

    /// <summary>True for Ctrl (Windows / Linux) or ⌘ (macOS) with <paramref name="key"/>, without Shift and Alt.</summary>
    public static bool IsCommand(InputEventKey e, Key key) =>
        e.Keycode == key && e.IsCommandOrControlPressed() && !e.ShiftPressed && !e.AltPressed;

    /// <summary>Runs <paramref name="command"/> (when it can run) and says it was used.</summary>
    public static bool Run(ICommand? command, object? parameter = null)
    {
        Bind.Run(command, parameter);
        return true;
    }

    /// <summary>
    /// A row of a list of results (in the look of the cache): a toggle button holding <paramref name="content"/>,
    /// pressed while <paramref name="isSelected"/> is true (checked when <paramref name="selected"/> of
    /// <paramref name="source"/> changes); a click calls <paramref name="select"/>.
    /// </summary>
    public static Button ResultRow(INotifyPropertyChanged source, string selected, Func<bool> isSelected, Action select, Control content, int minHeight = 56)
    {
        var button = new Button { ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, minHeight), Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Sunken(0.15f), Eve.Eve.Edge with { A = 0.15f }, 6));
        IgnoreMouse(content);
        var margin = Ui.Margin(content, 8, 6, 8, 6);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(margin);
        // The button grows with its content (a wrapped description).
        margin.MinimumSizeChanged += () => button.CustomMinimumSize = new Vector2(0, Math.Max(minHeight, margin.GetCombinedMinimumSize().Y));
        button.Pressed += select;
        Bind.When(button, source, () => button.SetPressedNoSignal(isSelected()), selected);
        return button;
    }

    /// <summary>The mouse passes through <paramref name="control"/> and its children (to the button holding them).</summary>
    public static void IgnoreMouse(Control control)
    {
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in control.GetChildren())
            if (child is Control c)
                IgnoreMouse(c);
    }

    /// <summary>A label cut at <paramref name="lines"/> lines with an ellipsis.</summary>
    public static Label Clamp(Label label, int lines)
    {
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.MaxLinesVisible = lines;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.CustomMinimumSize = new Vector2(1, 0);
        return label;
    }

    /// <summary>
    /// The status bar of a tool with a progress shown while the property <paramref name="busy"/> of
    /// <paramref name="busySource"/> (a command: IsRunning) is true.
    /// </summary>
    public static PanelContainer StatusBar(INotifyPropertyChanged source, string status, INotifyPropertyChanged? busySource = null, string busy = "IsRunning")
    {
        var bar = Ui.StatusBar(source, status, "IsError");
        if (busySource is not null && bar.GetChild(0) is HBoxContainer row)
        {
            var progress = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(60, 4), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            Bind.Visible(progress, busySource, busy);
            row.AddChild(progress);
            row.MoveChild(progress, 0);
        }
        return bar;
    }

    /// <summary>A drop-down of <paramref name="items"/> bound both ways to the string property <paramref name="name"/>.</summary>
    public static OptionButton Choice(INotifyPropertyChanged source, string name, IEnumerable<string> items) =>
        Bind.Options(new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, items.ToList(), source, name);

    /// <summary>
    /// A text bound to <paramref name="name"/> with a ▾ button offering <paramref name="choices"/> (those containing
    /// what is typed first): the equivalent of the auto-complete box of the Avalonia chat.
    /// </summary>
    public static HBoxContainer TextWithChoices(INotifyPropertyChanged source, string name, IEnumerable<string> choices, string placeholder = "")
    {
        var edit = Ui.Line(source, name, placeholder);
        var menu = new MenuButton { Text = "▾", TooltipText = "Choose a known value", Flat = false, FocusMode = Control.FocusModeEnum.None };
        var popup = menu.GetPopup();
        var shown = new List<string>();
        menu.AboutToPopup += () =>
        {
            popup.Clear();
            var typed = edit.Text.Trim();
            shown = choices.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => typed.Length > 0 && c.Contains(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            if (shown.Count == 0)
            {
                popup.AddItem("(no known value)");
                popup.SetItemDisabled(0, true);
            }
            foreach (var choice in shown)
                popup.AddItem(choice);
        };
        popup.IdPressed += id =>
        {
            var index = popup.GetItemIndex((int)id);
            if (index >= 0 && index < shown.Count)
                Bind.Set(source, name, shown[index]);
        };
        return Ui.Row(4, edit, menu);
    }

    /// <summary>
    /// A date bound both ways to the <see cref="DateTime"/>? property <paramref name="name"/>: typed as yyyy-MM-dd
    /// (empty: no date), or picked in the calendar of the 📅 button (the date picker of the Avalonia chat).
    /// </summary>
    public static HBoxContainer DateField(INotifyPropertyChanged source, string name)
    {
        var edit = new LineEdit { PlaceholderText = "yyyy-mm-dd", CustomMinimumSize = new Vector2(100, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        DateTime? Current() => Bind.Get(source, name) switch
        {
            DateTime d => d,
            DateTimeOffset o => o.LocalDateTime,
            _ => null
        };
        void Show() => edit.Text = Current() is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
        void Commit()
        {
            var text = edit.Text.Trim();
            if (text.Length == 0)
                Bind.Set(source, name, null);
            else if (DateTime.TryParseExact(text, ["yyyy-MM-dd", "yyyy-M-d", "d/M/yyyy", "dd/MM/yyyy", "d.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                     || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
                Bind.Set(source, name, (DateTime?)date.Date);
            Show();
        }
        Bind.When(edit, source, Show, name);
        edit.TextSubmitted += _ => Commit();
        edit.FocusExited += Commit;

        var button = new Button { Text = "📅", TooltipText = "Pick the date in a calendar", FocusMode = Control.FocusModeEnum.None };
        var popup = new PopupPanel();
        button.AddChild(popup);
        var month = DateTime.Today;
        var calendar = new VBoxContainer();
        popup.AddChild(Ui.Margin(calendar, 4));
        void Build()
        {
            foreach (var child in calendar.GetChildren())
                child.QueueFree();
            var first = new DateTime(month.Year, month.Month, 1);
            var title = Eve.Eve.Caption(first.ToString("MMMM yyyy", CultureInfo.InvariantCulture), Eve.Eve.TextBright, 12);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            calendar.AddChild(Ui.Row(4,
                Ui.Action("‹", () => { month = first.AddMonths(-1); Build(); }, tooltip: "Previous month"),
                title,
                Ui.Action("›", () => { month = first.AddMonths(1); Build(); }, tooltip: "Next month")));
            var grid = Ui.Grid(7, 2, 2);
            foreach (var day in new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" })
            {
                var label = Eve.Eve.Caption(day, Eve.Eve.Muted, 9);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                grid.AddChild(label);
            }
            var offset = ((int)first.DayOfWeek + 6) % 7;
            for (var i = 0; i < offset; i++)
                grid.AddChild(new Control());
            var selected = Current()?.Date;
            for (var d = 1; d <= DateTime.DaysInMonth(first.Year, first.Month); d++)
            {
                var date = new DateTime(first.Year, first.Month, d);
                var dayButton = new Button { Text = d.ToString(CultureInfo.InvariantCulture), CustomMinimumSize = new Vector2(32, 26), FocusMode = Control.FocusModeEnum.None };
                if (date == selected)
                    Eve.Eve.StylePrimary(dayButton);
                else if (date == DateTime.Today)
                    dayButton.AddThemeColorOverride("font_color", Eve.Eve.Cyan);
                dayButton.Pressed += () =>
                {
                    Bind.Set(source, name, (DateTime?)date);
                    popup.Hide();
                };
                grid.AddChild(dayButton);
            }
            calendar.AddChild(grid);
            calendar.AddChild(Ui.Row(6,
                Ui.Action("TODAY", () => { Bind.Set(source, name, (DateTime?)DateTime.Today); popup.Hide(); }),
                Ui.Spring(),
                Ui.Action("CLEAR", () => { Bind.Set(source, name, null); popup.Hide(); }, tooltip: "No date")));
        }
        button.Pressed += () =>
        {
            month = Current() ?? DateTime.Today;
            Build();
            var rect = button.GetGlobalRect();
            popup.Popup(new Rect2I((int)(rect.Position.X + rect.Size.X - 260), (int)(rect.Position.Y + rect.Size.Y + 2), 260, 0));
        };
        return Ui.Row(4, edit, button);
    }

    /// <summary>
    /// The confirmation asked by a view model (message, label of the confirming button), in a dialog of the chat.
    /// </summary>
    public static System.Threading.Tasks.Task<bool> Confirm(string message, string label) =>
        Dialogs.Confirm(label, message, label, "Cancel");
}
