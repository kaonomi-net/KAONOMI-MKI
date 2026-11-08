using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;
using KAONOMI.UI.Tools.Alarms;

namespace KAONOMI.UI.Tools.Daily;

/// <summary>
/// /alarm: the alarm clock. Header: NEW, DELETE. On the left the alarms (the time in large digits, the label, when it
/// rings, the next ring, a switch on / off); on the right the selected one: label, hour and minute, ONCE on a date or
/// WEEKLY on the days ticked (EVERY DAY, MON–FRI, WEEKEND, NONE in one click) and one week in 1 to 4, the ringtone
/// with ▶ TEST, the snooze in minutes and "say the label". Every change is kept at once; the chat rings the alarms
/// (<see cref="AlarmRinger"/>) even when this window is closed.
/// </summary>
[ToolView(typeof(AlarmWindowViewModel), "Alarm clock", "⏰", 1000, 680)]
public sealed class AlarmView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (AlarmWindowViewModel)viewModel;

        var header = DailyUi.Toolbar(
            [Ui.Label("Alarm clock", Ui.TextBright, 14, bold: true)],
            [Ui.Command("✚ NEW", vm.NewCommand, tooltip: "A new alarm (Ctrl+N / ⌘N)", primary: true),
             Ui.Command("DELETE", vm.DeleteCommand, tooltip: "Delete the selected alarm")]);

        var list = new StackPanel { Spacing = 4, Margin = new Thickness(6) };
        var empty = DailyUi.Placeholder("No alarm: ✚ NEW adds one");
        DailyUi.Items(list, vm.Alarms, entry => Row(vm, entry), empty);
        var left = DailyUi.Page(0, Ui.Scroll(list), empty);
        left.MinWidth = 320;

        var editor = DailyUi.Swap(vm, nameof(vm.SelectedAlarm),
            value => value is AlarmEntryViewModel entry ? Editor(vm, entry) : DailyUi.Placeholder("Select an alarm, or ✚ NEW"));

        var page = DailyUi.Page(1, header, DailyUi.SplitH(left, editor, 340), DailyUi.StatusBar(vm, nameof(vm.Status)));

        // ▶ TEST: the ringtone for 4 seconds.
        CancellationTokenSource? testing = null;
        Action<string> test = name => Ui.OnMain(() =>
        {
            testing?.Cancel();
            testing = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            _ = Ringtones.RingAsync(name, testing.Token);
        });
        // The next rings move with the time and with the rings given: the labels are read again every 20 s.
        Action changed = () => Ui.OnMain(vm.RefreshStatus);
        DailyUi.Hook(page,
            () =>
            {
                vm.TestRequested += test;
                AlarmClock.Shared.Changed += changed;
            },
            () =>
            {
                vm.TestRequested -= test;
                AlarmClock.Shared.Changed -= changed;
                testing?.Cancel();
            });
        DailyUi.Every(page, TimeSpan.FromSeconds(20), vm.RefreshStatus);

        DailyUi.Shortcuts(page, key =>
        {
            if (!DailyUi.IsCommand(key, Key.N))
                return false;
            Ui.Run(vm.NewCommand);
            return true;
        });
        return page;
    }

    /// <summary>An alarm of the list: its time, label, when and next ring, and its switch.</summary>
    private static Control Row(AlarmWindowViewModel vm, AlarmEntryViewModel entry)
    {
        var time = DailyUi.Mono(string.Empty, Ui.TextBright, 26).Bind(TextBlock.TextProperty, entry, nameof(entry.TimeLabel));
        // The time dimmed while the alarm is off.
        time.Bind(TextBlock.ForegroundProperty, entry, nameof(entry.Enabled), BindingMode.OneWay, v => v is true ? Ui.TextBright : Ui.Muted);
        var text = Ui.Column(1,
            Ui.Bound(entry, nameof(entry.Label), Ui.TextBright, 13),
            Ui.Bound(entry, nameof(entry.WhenLabel), Ui.Muted, 11),
            Ui.Bound(entry, nameof(entry.NextLabel), Ui.Warm, 11));
        text.VerticalAlignment = VerticalAlignment.Center;
        var toggle = new ToggleSwitch { OnContent = null, OffContent = null, Focusable = false, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        ToolTip.SetTip(toggle, "On / off");
        toggle.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, entry, nameof(entry.Enabled), BindingMode.TwoWay);
        time.Margin = new Thickness(0, 0, 10, 0);
        var content = Ui.Dock((time, Avalonia.Controls.Dock.Left), (toggle, Avalonia.Controls.Dock.Right), (text, Avalonia.Controls.Dock.Left));
        return DailyUi.SelectRow(vm, nameof(vm.SelectedAlarm), () => ReferenceEquals(vm.SelectedAlarm, entry), () => vm.SelectedAlarm = entry, content, 64);
    }

    /// <summary>The editor of the selected alarm.</summary>
    private static Control Editor(AlarmWindowViewModel vm, AlarmEntryViewModel entry)
    {
        // Drop-downs, not spin boxes: a spin box with the focus takes the wheel and the keys meant for something else.
        var hour = DailyUi.Choice(entry, nameof(entry.HourText), AlarmEntryViewModel.Hours, 96);
        var minute = DailyUi.Choice(entry, nameof(entry.MinuteText), AlarmEntryViewModel.Minutes, 96);
        foreach (var choice in new[] { hour, minute })
        {
            choice.HorizontalAlignment = HorizontalAlignment.Left;
            choice.FontFamily = Ui.Mono;
            choice.FontSize = 22;
        }
        ToolTip.SetTip(hour, "Hour");
        ToolTip.SetTip(minute, "Minute");
        var on = Ui.Check("On", entry, nameof(entry.Enabled));
        ToolTip.SetTip(on, "The alarm rings (off: kept, but silent)");
        on.Margin = new Thickness(12, 0, 0, 0);
        var timeRow = Ui.Row(8, hour, Ui.Label(":", Ui.TextBright, 22), minute, on);

        var repeat = DailyUi.Choice(entry, nameof(entry.Repeat), AlarmEntryViewModel.Repeats);
        repeat.HorizontalAlignment = HorizontalAlignment.Left;
        repeat.MinWidth = 200;
        ToolTip.SetTip(repeat, "Once: on one day · Weekly: on the days ticked, every week or one week in 2, 3 or 4");

        // ONCE: the day.
        var once = DailyUi.Field("Day (yyyy-MM-dd)", Ui.Edit(entry, nameof(entry.Date), "yyyy-MM-dd")).Visible(entry, nameof(entry.IsOnce));

        // WEEKLY: the days, the shortcuts, the weeks.
        var days = Ui.Row(4,
            Day("Mon", entry, nameof(entry.Monday)), Day("Tue", entry, nameof(entry.Tuesday)), Day("Wed", entry, nameof(entry.Wednesday)),
            Day("Thu", entry, nameof(entry.Thursday)), Day("Fri", entry, nameof(entry.Friday)), Day("Sat", entry, nameof(entry.Saturday)),
            Day("Sun", entry, nameof(entry.Sunday)));
        var shortcuts = Ui.Row(6,
            Ui.Command("EVERY DAY", entry.SetDaysCommand, "every"),
            Ui.Command("MON–FRI", entry.SetDaysCommand, "weekdays"),
            Ui.Command("WEEKEND", entry.SetDaysCommand, "weekend"),
            Ui.Command("NONE", entry.SetDaysCommand, "none"));
        var weeks = DailyUi.Choice(entry, nameof(entry.WeekInterval), AlarmEntryViewModel.WeekIntervals);
        ToolTip.SetTip(weeks, "One week in 2, 3 or 4: counted from the week of the first day below");
        var firstWeek = Ui.Edit(entry, nameof(entry.Date), "yyyy-MM-dd");
        var weekCells = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var weeksField = DailyUi.Field("Weeks", weeks);
        var firstField = DailyUi.Field("First week (a day in it, yyyy-MM-dd)", firstWeek);
        Grid.SetColumn(firstField, 2);
        weekCells.Children.Add(weeksField);
        weekCells.Children.Add(firstField);
        var weekly = Ui.Column(8, DailyUi.Field("Days", days), shortcuts, weekCells).Visible(entry, nameof(entry.IsWeekly));

        var ringtone = DailyUi.Choice(entry, nameof(entry.Ringtone), AlarmClock.Ringtones);
        var testButton = Ui.Command("▶ TEST", vm.TestCommand, tooltip: "Hear the ringtone for 4 seconds");
        var ringRow = Ui.Dock((testButton, Avalonia.Controls.Dock.Right), (ringtone, Avalonia.Controls.Dock.Left));
        testButton.Margin = new Thickness(6, 0, 0, 0);
        var snooze = Ui.Number(entry, nameof(entry.SnoozeMinutes), 1, 60);
        snooze.FormatString = "0";
        snooze.MinWidth = 120;
        var snoozeRow = Ui.Row(6, snooze, Ui.Label("min", Ui.Muted));
        var cells = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var ringField = DailyUi.Field("Ringtone", ringRow);
        var snoozeField = DailyUi.Field("Snooze", snoozeRow);
        Grid.SetColumn(snoozeField, 2);
        cells.Children.Add(ringField);
        cells.Children.Add(snoozeField);

        var fields = Ui.Column(12,
            DailyUi.Field("Label", Ui.Edit(entry, nameof(entry.Label), "Wake up, Meeting, Medicine…")),
            DailyUi.Field("Time", timeRow),
            DailyUi.Field("Repeat", repeat),
            once,
            weekly,
            DailyUi.Separator(),
            cells,
            Ui.Check("The avatar says the label when it rings", entry, nameof(entry.Speak)),
            Ui.Bound(entry, nameof(entry.NextLabel), Ui.Warm, 13));
        fields.Margin = new Thickness(14, 12);
        return Ui.Scroll(fields);
    }

    /// <summary>A day of the week: a toggle button lit while ticked.</summary>
    private static Control Day(string text, AlarmEntryViewModel entry, string name)
    {
        var button = DailyUi.Toggle(text, entry, name, $"Ring on {text}");
        button.MinWidth = 52;
        button.MinHeight = 34;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        return button;
    }
}
