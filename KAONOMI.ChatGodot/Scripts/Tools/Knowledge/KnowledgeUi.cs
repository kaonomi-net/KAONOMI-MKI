using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Godot;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// Small pieces shared by the views of the knowledge tools (ontology, entity, relations): a context menu built from
/// commands, a selectable text, an overlay, the keyboard shortcuts of a window, an optional date with its calendar.
/// </summary>
internal static class KnowledgeUi
{
    /// <summary>A read-only text that can be selected and copied, bound to <paramref name="name"/>.</summary>
    public static RichTextLabel Selectable(INotifyPropertyChanged source, string name, Color? color = null, int size = 12)
    {
        var label = Selectable(string.Empty, color, size);
        Bind.Text(label, source, name);
        return label;
    }

    /// <summary>A read-only text that can be selected and copied.</summary>
    public static RichTextLabel Selectable(string text, Color? color = null, int size = 12)
    {
        var label = new RichTextLabel
        {
            BbcodeEnabled = false,
            FitContent = true,
            ScrollActive = false,
            SelectionEnabled = true,
            ContextMenuEnabled = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Text = text
        };
        label.AddThemeFontSizeOverride("normal_font_size", size);
        label.AddThemeColorOverride("default_color", color ?? Eve.Eve.Text);
        return label;
    }

    /// <summary>Children laid over each other, each filling the whole area (the first one at the back).</summary>
    public static MarginContainer Overlay(params Control[] children)
    {
        var holder = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        foreach (var child in children)
            holder.AddChild(child);
        return holder;
    }

    /// <summary>A panel in the look of the EVE windows (dark glass, hairline edge), around <paramref name="content"/>.</summary>
    public static PanelContainer Card(Control content, Color? edge = null, int margin = 10, int border = 1)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.22f), edge ?? Eve.Eve.Edge with { A = 0.45f }, margin, border));
        panel.AddChild(content);
        return panel;
    }

    /// <summary>
    /// Runs <paramref name="handler"/> on the keys of the window holding <paramref name="owner"/> that its controls did
    /// not use (Ctrl+S, F5, Escape…), while the window is in front; the handler returns true when it used the key.
    /// </summary>
    public static void Shortcuts(Control owner, Func<InputEventKey, bool> handler)
    {
        var scope = new ShortcutScope { Handler = handler };
        owner.AddChild(scope);
    }

    /// <summary>
    /// Runs <paramref name="release"/> when <paramref name="owner"/> is freed. Not on TreeExiting: the desktop moves the
    /// viewport of a window out of the tree and back each time it goes to the back and comes to the front again.
    /// </summary>
    public static void OnFreed(Node owner, Action release) => owner.AddChild(new Lifetime { Release = release });

    /// <summary>True for Ctrl+<paramref name="key"/> (and ⌘+<paramref name="key"/> on macOS).</summary>
    public static bool IsCommand(InputEventKey key, Key code) =>
        key.Keycode == code && (key.CtrlPressed || key.MetaPressed) && !key.AltPressed;

    /// <summary>Runs <paramref name="command"/> with no parameter when it can run; true when it ran.</summary>
    public static bool TryRun(ICommand? command)
    {
        if (command?.CanExecute(null) != true)
            return false;
        command.Execute(null);
        return true;
    }

    /// <summary>"yyyy-MM-dd" of an optional date.</summary>
    public static string Format(DateTime? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>Runs its action when it is freed with its parent (see <see cref="KnowledgeUi.OnFreed"/>).</summary>
public partial class Lifetime : Node
{
    public Lifetime()
    {
    }

    internal Action? Release { get; set; }

    public override void _Notification(int what)
    {
        if (what == NotificationPredelete)
        {
            Release?.Invoke();
            Release = null;
        }
    }
}

/// <summary>Catches the keys the controls of a window did not use, while the window is in front (see <see cref="KnowledgeUi.Shortcuts"/>).</summary>
public partial class ShortcutScope : Node
{
    public ShortcutScope()
    {
    }

    internal Func<InputEventKey, bool>? Handler { get; set; }

    public override void _ShortcutInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key || Handler is null || GetParent() is not Control owner || !owner.IsVisibleInTree())
            return;
        // The active window of the deck only (every window receives every key), and not while the chat is typed in.
        if (Shell.Current is { } shell && !shell.HasKeyboard(owner))
            return;
        if (Handler(key))
            GetViewport().SetInputAsHandled();
    }
}

/// <summary>
/// A context menu: items running commands (disabled while they cannot run) or actions, check items, separators;
/// shown at a point of a control and freed once closed.
/// </summary>
internal sealed class ContextMenu
{
    private readonly PopupMenu _menu = new();
    private readonly List<Action> _actions = [];

    /// <summary>An item running <paramref name="command"/>.</summary>
    public ContextMenu Item(string text, ICommand? command, object? parameter = null, string tooltip = "")
    {
        var enabled = command?.CanExecute(parameter) == true;
        return Item(text, () => Bind.Run(command, parameter), enabled, tooltip);
    }

    /// <summary>An item running <paramref name="action"/>.</summary>
    public ContextMenu Item(string text, Action action, bool enabled = true, string tooltip = "")
    {
        var id = _actions.Count;
        _actions.Add(action);
        _menu.AddItem(text, id);
        var index = _menu.GetItemIndex(id);
        _menu.SetItemDisabled(index, !enabled);
        if (tooltip.Length > 0)
            _menu.SetItemTooltip(index, tooltip);
        return this;
    }

    /// <summary>A check item showing <paramref name="on"/>; a click calls <paramref name="toggled"/> with the new state.</summary>
    public ContextMenu Check(string text, bool on, Action<bool> toggled)
    {
        var id = _actions.Count;
        _actions.Add(() => toggled(!on));
        _menu.AddCheckItem(text, id);
        _menu.SetItemChecked(_menu.GetItemIndex(id), on);
        return this;
    }

    public ContextMenu Separator()
    {
        _menu.AddSeparator();
        return this;
    }

    /// <summary>Shows the menu at <paramref name="local"/> (coordinates of <paramref name="at"/>).</summary>
    public void Show(Control at, Vector2 local)
    {
        _menu.IdPressed += id =>
        {
            if (id >= 0 && id < _actions.Count)
                _actions[(int)id]();
        };
        _menu.PopupHide += () => _menu.CallDeferred(Node.MethodName.QueueFree);
        at.AddChild(_menu);
        _menu.Position = (Vector2I)(at.GetScreenTransform() * local);
        _menu.ResetSize();
        _menu.Popup();
    }
}

/// <summary>
/// An optional date (in place of the date picker of Avalonia): typed as yyyy-MM-dd (Enter or leaving the field), or
/// picked on a small calendar (📅); ✕ empties it.
/// </summary>
public partial class DateBox : HBoxContainer
{
    private readonly LineEdit _edit = new() { PlaceholderText = "(none)", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(96, 0) };
    private DateTime? _value;

    public DateBox()
    {
        AddThemeConstantOverride("separation", 3);
        AddChild(_edit);
        var pick = Eve.Eve.Button("📅", tooltip: "Pick the date on a calendar");
        pick.Pressed += ShowCalendar;
        AddChild(pick);
        var clear = Eve.Eve.Button("✕", tooltip: "No date");
        clear.Pressed += () => SetValue(null, true);
        AddChild(clear);
        _edit.TextSubmitted += _ => Parse();
        _edit.FocusExited += Parse;
    }

    /// <summary>Raised when the user changes the date.</summary>
    public event Action<DateTime?>? Changed;

    /// <summary>The date (no time), or null.</summary>
    public DateTime? Value
    {
        get => _value;
        set => SetValue(value, false);
    }

    /// <summary>The tooltip of the text of the date.</summary>
    public string Tip
    {
        set => _edit.TooltipText = value;
    }

    private void SetValue(DateTime? value, bool raise)
    {
        var changed = _value != value?.Date;
        _value = value?.Date;
        _edit.Text = KnowledgeUi.Format(_value);
        _edit.RemoveThemeColorOverride("font_color");
        if (raise && changed)
            Changed?.Invoke(_value);
    }

    private void Parse()
    {
        var text = _edit.Text.Trim();
        if (text.Length == 0)
        {
            SetValue(null, true);
            return;
        }
        string[] formats = ["yyyy-MM-dd", "yyyy-M-d", "dd/MM/yyyy", "d/M/yyyy", "yyyy/MM/dd"];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
            SetValue(date, true);
        else
            _edit.AddThemeColorOverride("font_color", Eve.Eve.Red);
    }

    /// <summary>A calendar of a month under the field: a click on a day sets the date.</summary>
    private void ShowCalendar()
    {
        var popup = new PopupPanel();
        popup.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.PanelSolid, Eve.Eve.EdgeBright with { A = 0.6f }, 8));
        var month = new DateTime((_value ?? DateTime.Today).Year, (_value ?? DateTime.Today).Month, 1);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        popup.AddChild(column);

        void Build()
        {
            foreach (var child in column.GetChildren())
                child.QueueFree();
            var previous = Eve.Eve.Button("‹", tooltip: "Previous month");
            var next = Eve.Eve.Button("›", tooltip: "Next month");
            var title = Eve.Eve.Caption(month.ToString("MMMM yyyy", CultureInfo.InvariantCulture), Eve.Eve.TextBright, 11);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            previous.Pressed += () => { month = month.AddMonths(-1); Build(); };
            next.Pressed += () => { month = month.AddMonths(1); Build(); };
            column.AddChild(Ui.Row(4, previous, title, next));
            var grid = Ui.Grid(7, 2, 2);
            foreach (var day in new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" })
            {
                var head = Eve.Eve.Caption(day, Eve.Eve.Muted, 9);
                head.HorizontalAlignment = HorizontalAlignment.Center;
                grid.AddChild(head);
            }
            var offset = ((int)month.DayOfWeek + 6) % 7;
            for (var i = 0; i < offset; i++)
                grid.AddChild(new Control());
            for (var d = 1; d <= DateTime.DaysInMonth(month.Year, month.Month); d++)
            {
                var date = new DateTime(month.Year, month.Month, d);
                var button = new Button { Text = d.ToString(CultureInfo.InvariantCulture), CustomMinimumSize = new Vector2(30, 24), ToggleMode = true };
                button.SetPressedNoSignal(_value == date);
                if (date == DateTime.Today)
                    button.AddThemeColorOverride("font_color", Eve.Eve.Cyan);
                button.Pressed += () =>
                {
                    SetValue(date, true);
                    popup.Hide();
                };
                grid.AddChild(button);
            }
            column.AddChild(grid);
            var today = Ui.Action("TODAY", () => { SetValue(DateTime.Today, true); popup.Hide(); });
            column.AddChild(today);
            popup.CallDeferred(Window.MethodName.ResetSize);
        }

        Build();
        popup.PopupHide += () => popup.CallDeferred(Node.MethodName.QueueFree);
        AddChild(popup);
        popup.Position = (Vector2I)(_edit.GetScreenTransform() * new Vector2(0, _edit.Size.Y + 2));
        popup.Popup();
    }

    /// <summary>A date box bound both ways to the <see cref="DateTime"/>? property <paramref name="name"/>.</summary>
    public static DateBox Bound(INotifyPropertyChanged source, string name, string tip = "")
    {
        var box = new DateBox { Tip = tip };
        Bind.When(box, source, () => box.Value = Bind.Get(source, name) as DateTime?, name);
        box.Changed += value => Bind.Set(source, name, value);
        return box;
    }
}
