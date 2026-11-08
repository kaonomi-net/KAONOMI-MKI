using System;
using Avalonia;
using Avalonia.Controls;
using UniformGrid = Avalonia.Controls.Primitives.UniformGrid;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Daily;

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

        var title = Ui.Bound(vm, nameof(vm.MonthTitle), Ui.TextBright, 18);
        title.MinWidth = 220;
        title.TextAlignment = TextAlignment.Center;
        var header = DailyUi.Toolbar(
            [Ui.Command("◀", vm.PreviousCommand, tooltip: "The month before"),
             title,
             Ui.Command("▶", vm.NextCommand, tooltip: "The month after"),
             Ui.Command("TODAY", vm.TodayCommand, tooltip: "Back to this month and to today")],
            [Ui.Command("✚ NEW EVENT", vm.NewEventCommand, tooltip: "A new event on the selected day (Ctrl+N / ⌘N; or a double-click on a day)", primary: true),
             Ui.Command("DELETE", vm.DeleteEventCommand, tooltip: "Delete the event in the editor")]);

        // The month: the names of the days, then 6 rows of 7 days, built again with the month and the events.
        var names = new UniformGrid { Columns = 7 };
        foreach (var name in CalendarWindowViewModel.WeekDays)
        {
            var label = Ui.Caption(name, Ui.Muted, 11);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            names.Children.Add(label);
        }
        var grid = new UniformGrid { Columns = 7, Rows = 6 };
        Ui.When(grid, vm, () =>
        {
            grid.Children.Clear();
            foreach (var day in vm.Days)
                grid.Children.Add(DayCell(vm, day));
        }, nameof(vm.Days));
        var month = DailyUi.Page(1, names, grid);
        month.Margin = new Thickness(8);
        month.RowSpacing = 4;

        // The day: its lines, then the editor.
        var list = new StackPanel { Spacing = 2 };
        var empty = DailyUi.Placeholder("Nothing this day: ✚ NEW EVENT");
        empty.HorizontalAlignment = HorizontalAlignment.Left;
        empty.Margin = new Thickness(0, 4);
        DailyUi.Items(list, vm.DayItems, item => ItemRow(vm, item), empty);
        var editor = DailyUi.Swap(vm, nameof(vm.SelectedEvent), value => value is CalendarEventEntryViewModel entry ? Editor(entry) : null);
        var day = Ui.Column(8,
            Ui.Bound(vm, nameof(vm.SelectedDayTitle), Ui.TextBright, 15),
            list, empty,
            DailyUi.Separator(),
            editor);
        day.Margin = new Thickness(10, 8, 12, 8);
        var right = Ui.Scroll(day);
        right.MinWidth = 380;

        var page = DailyUi.Page(1, header, DailyUi.SplitH(month, right, 720), DailyUi.StatusBar(vm, nameof(vm.Status)));

        // The events and the alarms changed elsewhere (a reminder rung, /alarm): the month and the day read again.
        Action changed = () => Ui.OnMain(vm.Refresh);
        DailyUi.Hook(page,
            () =>
            {
                CalendarStore.Shared.Changed += changed;
                AlarmClock.Shared.Changed += changed;
            },
            () =>
            {
                CalendarStore.Shared.Changed -= changed;
                AlarmClock.Shared.Changed -= changed;
            });
        DailyUi.Shortcuts(page, key =>
        {
            if (!DailyUi.IsCommand(key, Key.N))
                return false;
            Ui.Run(vm.NewEventCommand);
            return true;
        });
        return page;
    }

    /// <summary>A day of the month: its number, its first lines, "+n" for the others.</summary>
    private static Control DayCell(CalendarWindowViewModel vm, CalendarDay day)
    {
        var number = Ui.Label(day.Number.ToString(), day.IsToday ? Ui.Accent : day.IsCurrentMonth ? Ui.TextBright : Ui.Muted, day.IsToday ? 15 : 13, bold: day.IsToday);
        var lines = Ui.Column(1, number);
        foreach (var item in day.FirstItems)
            lines.Children.Add(Ui.Label(item.Text, ColorOf(item.Color), 10));
        if (day.More > 0)
            lines.Children.Add(Ui.Label($"+{day.More}", Ui.Muted, 10));
        lines.VerticalAlignment = VerticalAlignment.Top;

        var cell = new Border
        {
            Child = lines,
            Margin = new Thickness(2),
            Padding = new Thickness(5, 3),
            MinWidth = 70,
            MinHeight = 74,
            ClipToBounds = true,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(day.IsToday || day.IsSelected ? 2 : 1),
            BorderBrush = day.IsSelected ? Ui.Accent : day.IsToday ? Ui.Accent : Ui.Edge,
            Background = day.IsSelected ? Ui.Header : day.IsCurrentMonth ? Ui.Field : Ui.Panel,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(cell, day.Date.ToString("D"));
        cell.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed)
                return;
            vm.Select(day.Date);
            if (e.ClickCount >= 2)
                Ui.Run(vm.NewEventCommand);
            e.Handled = true;
        };
        return cell;
    }

    /// <summary>A line of the selected day: its time, its title in its colour; a click opens its event.</summary>
    private static Control ItemRow(CalendarWindowViewModel vm, CalendarItem item)
    {
        var time = DailyUi.Mono(string.IsNullOrEmpty(item.Time) ? "all day" : item.Time, Ui.Muted, 12);
        time.MinWidth = 64;
        var mark = new Border { Background = ColorOf(item.Color), Width = 4, VerticalAlignment = VerticalAlignment.Stretch };
        var title = Ui.Label(item.Title, Ui.TextBright, 13);
        var row = Ui.Dock((mark, Avalonia.Controls.Dock.Left), (time, Avalonia.Controls.Dock.Left), (title, Avalonia.Controls.Dock.Left));
        mark.Margin = new Thickness(0, 0, 8, 0);
        time.Margin = new Thickness(0, 0, 8, 0);
        var button = DailyUi.SelectRow(vm, nameof(vm.SelectedEvent), () => item.Event is not null && vm.SelectedEvent?.Model == item.Event, () => vm.Open(item), row, 32);
        if (item.IsAlarm)
            ToolTip.SetTip(button, "An alarm of /alarm");
        return button;
    }

    /// <summary>The editor of an event.</summary>
    private static Control Editor(CalendarEventEntryViewModel entry)
    {
        ComboBox Choice(string name, string[] items, double width)
        {
            var choice = DailyUi.Choice(entry, name, items, width);
            if (width > 0)
                choice.HorizontalAlignment = HorizontalAlignment.Left;
            return choice;
        }
        var times = Ui.Row(6,
            Choice(nameof(entry.StartHour), CalendarEventEntryViewModel.Hours, 70), Ui.Label(":", Ui.TextBright, 16),
            Choice(nameof(entry.StartMinute), CalendarEventEntryViewModel.Minutes, 70),
            Ui.Label("  →  ", Ui.Muted, 14),
            Choice(nameof(entry.EndHour), CalendarEventEntryViewModel.Hours, 70), Ui.Label(":", Ui.TextBright, 16),
            Choice(nameof(entry.EndMinute), CalendarEventEntryViewModel.Minutes, 70)).Visible(entry, nameof(entry.HasTime));

        var allDay = Ui.Check("All day", entry, nameof(entry.AllDay));
        var fields = Ui.Column(10,
            Ui.Caption("EVENT", Ui.Warm, 11),
            DailyUi.Field("Title", Ui.Edit(entry, nameof(entry.Title), "Meeting, birthday, doctor…")),
            Cells(DailyUi.Field("Day (yyyy-MM-dd)", Ui.Edit(entry, nameof(entry.Date), "yyyy-MM-dd")), DailyUi.Field(" ", allDay)),
            DailyUi.Field("Time", times).Visible(entry, nameof(entry.HasTime)),
            Cells(DailyUi.Field("Repeat", Choice(nameof(entry.Repeat), CalendarEventEntryViewModel.Repeats, 0)),
                DailyUi.Field("Reminder", Choice(nameof(entry.Reminder), CalendarEventEntryViewModel.Reminders, 0))),
            DailyUi.Field("Colour", Choice(nameof(entry.Color), CalendarEventEntryViewModel.Colors, 0)),
            DailyUi.Field("Notes", Ui.Edit(entry, nameof(entry.Notes), multiline: true, height: 90)));
        return fields;
    }

    private static Grid Cells(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>The colour of an event (its name) in the theme.</summary>
    public static IBrush ColorOf(string name) => name switch
    {
        "Green" => Ui.Green,
        "Amber" => Ui.Warm,
        "Red" => Ui.Red,
        "Grey" => Ui.Muted,
        _ => Ui.Accent
    };
}
