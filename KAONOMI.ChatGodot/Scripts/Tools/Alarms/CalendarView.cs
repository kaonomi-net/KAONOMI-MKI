using System;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Writing;

namespace KAONOMI.ChatGodot.Tools.Alarms;

/// <summary>
/// /calendar: header ◀ the month ▶, TODAY, ✚ NEW EVENT, DELETE. On the left the month, Monday first, six weeks: each day
/// its number and its first three lines (events in their colour, the alarms of /alarm in amber, "+2" for the others),
/// today ringed, the selected day lit; a click selects a day, a double-click adds an event on it. On the right the
/// selected day: its events and alarms (a click opens an event), then the editor of the event: title, day, whole day or
/// start and end, repeat, reminder, colour, notes. Every change is kept at once; the chat rings the reminders as it rings
/// the alarms, even when this window is closed.
/// </summary>
[ToolView(typeof(CalendarWindowViewModel), "Calendar", "📅", 1200, 760)]
public sealed class CalendarView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (CalendarWindowViewModel)viewModel;

        var title = Ui.Label(vm, nameof(vm.MonthTitle), Eve.Eve.TextBright, 18);
        title.CustomMinimumSize = new Vector2(220, 0);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var header = Ui.Toolbar(Ui.Row(8,
            Ui.Command("◀", vm.PreviousCommand, tooltip: "The month before"),
            title,
            Ui.Command("▶", vm.NextCommand, tooltip: "The month after"),
            Ui.Command("TODAY", vm.TodayCommand, tooltip: "Back to this month and to today"),
            Ui.Spring(),
            Ui.Command("✚ NEW EVENT", vm.NewEventCommand, primary: true, tooltip: "A new event on the selected day (Ctrl+N / ⌘N; or a double-click on a day)"),
            Ui.Command("DELETE", vm.DeleteEventCommand, tooltip: "Delete the event in the editor")));

        // The month: the names of the days, then 6 rows of 7 days, built again with the month and the events.
        var names = Ui.Grid(7, 4, 0);
        foreach (var name in CalendarWindowViewModel.WeekDays)
        {
            var label = Eve.Eve.Caption(name, Eve.Eve.Muted, 11);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            names.AddChild(label);
        }
        var grid = Ui.Grid(7, 4, 4);
        grid.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        Bind.When(grid, vm, () =>
        {
            foreach (var child in grid.GetChildren())
                child.QueueFree();
            foreach (var day in vm.Days)
                grid.AddChild(DayCell(vm, day));
        }, nameof(vm.Days));
        var month = Ui.Margin(Ui.Column(4, names, grid), 8);
        month.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // The day: its lines, then the editor.
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("Nothing this day: ✚ NEW EVENT");
        empty.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        Bind.Items(list, vm.DayItems, item => ItemRow(vm, item), empty);
        var editor = Ui.Swap(vm, nameof(vm.SelectedEvent), value => value is CalendarEventEntryViewModel entry ? Editor(entry) : null);
        var day = Ui.Column(8,
            Ui.Label(vm, nameof(vm.SelectedDayTitle), Eve.Eve.TextBright, 15),
            list, empty,
            Eve.Eve.Separator(),
            editor);
        var right = Ui.Scroll(Ui.Margin(day, 10, 8, 12, 8));
        right.CustomMinimumSize = new Vector2(380, 0);

        var page = Ui.Column(0, header, Ui.SplitH(month, right, 720), Ui.StatusBar(vm, nameof(vm.Status), error: null));
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // The events and the alarms changed elsewhere (a reminder rung, /alarm): the month and the day read again.
        Action changed = () => Bind.OnMain(vm.Refresh);
        CalendarStore.Shared.Changed += changed;
        AlarmClock.Shared.Changed += changed;
        Bind.OnGone(page, () =>
        {
            CalendarStore.Shared.Changed -= changed;
            AlarmClock.Shared.Changed -= changed;
        });
        WritingUi.Shortcuts(page, key => WritingUi.IsCommand(key, Key.N) && WritingUi.Run(vm.NewEventCommand));
        return page;
    }

    /// <summary>A day of the month: its number, its first lines, "+n" for the others.</summary>
    private static Control DayCell(CalendarWindowViewModel vm, CalendarDay day)
    {
        var cell = new Button
        {
            ToggleMode = true,
            ButtonPressed = day.IsSelected,
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(70, 74),
            ClipContents = true,
            TooltipText = day.Date.ToString("D")
        };
        var fill = day.IsCurrentMonth ? Eve.Eve.Sunken(0.15f) : Eve.Eve.Sunken(0.04f);
        cell.AddThemeStyleboxOverride("normal", Eve.Eve.Box(fill, Eve.Eve.Edge with { A = day.IsToday ? 0.9f : 0.15f }, 4));
        cell.AddThemeStyleboxOverride("pressed", Eve.Eve.Box(Eve.Eve.Cyan with { A = 0.22f }, Eve.Eve.Cyan with { A = 0.8f }, 4));
        cell.AddThemeStyleboxOverride("hover", Eve.Eve.Box(Eve.Eve.Cyan with { A = 0.1f }, Eve.Eve.Edge with { A = 0.4f }, 4));

        var number = Eve.Eve.Text_(day.Number.ToString(), day.IsToday ? Eve.Eve.Cyan : day.IsCurrentMonth ? Eve.Eve.TextBright : Eve.Eve.Muted, day.IsToday ? 15 : 13);
        if (day.IsToday)
            number.AddThemeFontOverride("font", Eve.Eve.Display);
        var lines = Ui.Column(1, number);
        foreach (var item in day.FirstItems)
        {
            var line = Eve.Eve.Text_(item.Text, ColorOf(item.Color), 10);
            line.ClipText = true;
            line.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            line.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            lines.AddChild(line);
        }
        if (day.More > 0)
            lines.AddChild(Eve.Eve.Text_($"+{day.More}", Eve.Eve.Muted, 10));
        var content = Ui.Margin(lines, 5, 3, 5, 3);
        WritingUi.IgnoreMouse(content);
        content.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        cell.AddChild(content);
        if (!day.IsCurrentMonth)
            cell.Modulate = new Color(1, 1, 1, 0.55f);

        cell.Pressed += () => vm.Select(day.Date);
        cell.GuiInput += e =>
        {
            if (e is InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left })
            {
                vm.Select(day.Date);
                Bind.Run(vm.NewEventCommand);
            }
        };
        return cell;
    }

    /// <summary>A line of the selected day: its time, its title in its colour; a click opens its event.</summary>
    private static Control ItemRow(CalendarWindowViewModel vm, CalendarItem item)
    {
        var time = Eve.Eve.Text_(string.IsNullOrEmpty(item.Time) ? "all day" : item.Time, Eve.Eve.Muted, 12);
        time.AddThemeFontOverride("font", Eve.Eve.Mono);
        time.CustomMinimumSize = new Vector2(64, 0);
        var mark = new ColorRect { Color = ColorOf(item.Color), CustomMinimumSize = new Vector2(4, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var title = WritingUi.Clamp(Eve.Eve.Text_(item.Title, Eve.Eve.TextBright, 13), 1);
        var row = Ui.Row(8, mark, time, title);
        var button = WritingUi.ResultRow(vm, nameof(vm.SelectedEvent), () => item.Event is not null && vm.SelectedEvent?.Model == item.Event, () => vm.Open(item), row, 32);
        if (item.IsAlarm)
            button.TooltipText = "An alarm of /alarm";
        return button;
    }

    /// <summary>The editor of an event.</summary>
    private static Control Editor(CalendarEventEntryViewModel entry)
    {
        OptionButton Choice(string name, string[] items, int width)
        {
            var choice = WritingUi.Choice(entry, name, items);
            choice.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            choice.CustomMinimumSize = new Vector2(width, 0);
            choice.FocusMode = Control.FocusModeEnum.Click;
            return choice;
        }
        var times = Bind.Visible(Ui.Row(6,
            Choice(nameof(entry.StartHour), CalendarEventEntryViewModel.Hours, 70), Eve.Eve.Text_(":", Eve.Eve.TextBright, 16),
            Choice(nameof(entry.StartMinute), CalendarEventEntryViewModel.Minutes, 70),
            Eve.Eve.Text_("  →  ", Eve.Eve.Muted, 14),
            Choice(nameof(entry.EndHour), CalendarEventEntryViewModel.Hours, 70), Eve.Eve.Text_(":", Eve.Eve.TextBright, 16),
            Choice(nameof(entry.EndMinute), CalendarEventEntryViewModel.Minutes, 70)), entry, nameof(entry.HasTime));

        var fields = Ui.Column(10,
            Eve.Eve.Caption("EVENT", Eve.Eve.Amber, 11),
            Ui.Field("Title", Ui.Line(entry, nameof(entry.Title), "Meeting, birthday, doctor…")),
            WritingUi.Cells(2,
                Ui.Field("Day (yyyy-MM-dd)", Ui.Line(entry, nameof(entry.Date), "yyyy-MM-dd")),
                Ui.Field(" ", Ui.Check("All day", entry, nameof(entry.AllDay)))),
            Ui.Field("Time", times),
            WritingUi.Cells(2,
                Ui.Field("Repeat", Choice(nameof(entry.Repeat), CalendarEventEntryViewModel.Repeats, 0)),
                Ui.Field("Reminder", Choice(nameof(entry.Reminder), CalendarEventEntryViewModel.Reminders, 0))),
            Ui.Field("Colour", Choice(nameof(entry.Color), CalendarEventEntryViewModel.Colors, 0)),
            Ui.Field("Notes", Ui.Lines(entry, nameof(entry.Notes), 90)));
        return fields;
    }

    /// <summary>The colour of an event (its name) in the theme.</summary>
    public static Color ColorOf(string name) => name switch
    {
        "Green" => Eve.Eve.Green,
        "Amber" => Eve.Eve.Amber,
        "Red" => Eve.Eve.Red,
        "Grey" => Eve.Eve.Muted,
        _ => Eve.Eve.Cyan
    };
}
