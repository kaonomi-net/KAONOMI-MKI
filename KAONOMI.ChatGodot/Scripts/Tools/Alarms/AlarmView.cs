using System;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Writing;

namespace KAONOMI.ChatGodot.Tools.Alarms;

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

        var header = Ui.Toolbar(Ui.Row(8,
            Eve.Eve.Caption("Alarm clock", Eve.Eve.TextBright, 14),
            Ui.Spring(),
            Ui.Command("✚ NEW", vm.NewCommand, primary: true, tooltip: "A new alarm (Ctrl+N / ⌘N)"),
            Ui.Command("DELETE", vm.DeleteCommand, tooltip: "Delete the selected alarm")));

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        var empty = Ui.Placeholder("No alarm: ✚ NEW adds one");
        Bind.Items(list, vm.Alarms, entry => Row(vm, entry), empty);
        var left = Ui.Column(0, Ui.FillHeight(Ui.Scroll(Ui.Margin(list, 6))), empty);
        left.CustomMinimumSize = new Vector2(320, 0);

        var editor = Ui.Swap(vm, nameof(vm.SelectedAlarm), value => value is AlarmEntryViewModel entry ? Editor(vm, entry) : Ui.Placeholder("Select an alarm, or ✚ NEW"));

        var page = Ui.Column(0, header, Ui.SplitH(left, editor, 340), Ui.StatusBar(vm, nameof(vm.Status), error: null));
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // ▶ TEST: the ringtone for 4 seconds.
        var player = new AudioStreamPlayer { VolumeDb = -6 };
        page.AddChild(player);
        Action<string> test = name => Bind.OnMain(() =>
        {
            player.Stream = Ringtones.Get(name);
            player.Play();
            page.GetTree().CreateTimer(4).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(player))
                    player.Stop();
            };
        });
        vm.TestRequested += test;
        // The next rings move with the time and with the rings given: the labels are read again every 20 s.
        Action changed = () => Bind.OnMain(vm.RefreshStatus);
        AlarmClock.Shared.Changed += changed;
        var refresh = new Timer { WaitTime = 20, Autostart = true };
        refresh.Timeout += vm.RefreshStatus;
        page.AddChild(refresh);
        Bind.OnGone(page, () =>
        {
            vm.TestRequested -= test;
            AlarmClock.Shared.Changed -= changed;
        });

        WritingUi.Shortcuts(page, key => WritingUi.IsCommand(key, Key.N) && WritingUi.Run(vm.NewCommand));
        return page;
    }

    /// <summary>An alarm of the list: its time, label, when and next ring, and its switch.</summary>
    private static Control Row(AlarmWindowViewModel vm, AlarmEntryViewModel entry)
    {
        var time = Ui.Label(entry, nameof(entry.TimeLabel), Eve.Eve.TextBright, 26);
        time.AddThemeFontOverride("font", Eve.Eve.Mono);
        var text = Ui.Column(1,
            WritingUi.Clamp(Ui.Label(entry, nameof(entry.Label), Eve.Eve.TextBright, 13), 1),
            Ui.Label(entry, nameof(entry.WhenLabel), Eve.Eve.Muted, 11),
            Ui.Label(entry, nameof(entry.NextLabel), Eve.Eve.Amber, 11));
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var content = Ui.Row(10, time, text);
        // The time dimmed while the alarm is off.
        Bind.When(content, entry, () => time.Modulate = entry.Enabled ? Colors.White : new Color(1, 1, 1, 0.35f), nameof(entry.Enabled));
        var row = WritingUi.ResultRow(vm, nameof(vm.SelectedAlarm), () => ReferenceEquals(vm.SelectedAlarm, entry), () => vm.SelectedAlarm = entry, content, 64);
        // The switch stays clickable over the row.
        var toggle = Bind.Check(new CheckButton { TooltipText = "On / off", FocusMode = Control.FocusModeEnum.None }, entry, nameof(entry.Enabled));
        toggle.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterRight);
        toggle.GrowHorizontal = Control.GrowDirection.Begin;
        toggle.OffsetRight = -6;
        row.AddChild(toggle);
        return row;
    }

    /// <summary>The editor of the selected alarm.</summary>
    private static Control Editor(AlarmWindowViewModel vm, AlarmEntryViewModel entry)
    {
        // Drop-downs, not spin boxes: a spin box with the focus takes the wheel and the keys meant for something else
        // (an hour went to 23 by itself when the voice mode of a ring came over it).
        var hour = WritingUi.Choice(entry, nameof(entry.HourText), AlarmEntryViewModel.Hours);
        var minute = WritingUi.Choice(entry, nameof(entry.MinuteText), AlarmEntryViewModel.Minutes);
        foreach (var choice in new[] { hour, minute })
        {
            choice.SizeFlagsHorizontal = Control.SizeFlags.Fill;
            choice.CustomMinimumSize = new Vector2(96, 0);
            choice.FocusMode = Control.FocusModeEnum.Click;
            choice.AddThemeFontOverride("font", Eve.Eve.Mono);
            choice.AddThemeFontSizeOverride("font_size", 22);
        }
        hour.TooltipText = "Hour";
        minute.TooltipText = "Minute";
        var timeRow = Ui.Row(8, hour, Eve.Eve.Text_(":", Eve.Eve.TextBright, 22), minute,
            Ui.Check("On", entry, nameof(entry.Enabled), "The alarm rings (off: kept, but silent)"));

        var repeat = WritingUi.Choice(entry, nameof(entry.Repeat), AlarmEntryViewModel.Repeats);
        repeat.TooltipText = "Once: on one day · Weekly: on the days ticked, every week or one week in 2, 3 or 4";

        // ONCE: the day.
        var date = Ui.Line(entry, nameof(entry.Date), "yyyy-MM-dd");
        var once = Bind.Visible(Ui.Field("Day (yyyy-MM-dd)", date), entry, nameof(entry.IsOnce));

        // WEEKLY: the days, the shortcuts, the weeks.
        var days = Ui.Row(4,
            Day("Mon", entry, nameof(entry.Monday)), Day("Tue", entry, nameof(entry.Tuesday)), Day("Wed", entry, nameof(entry.Wednesday)),
            Day("Thu", entry, nameof(entry.Thursday)), Day("Fri", entry, nameof(entry.Friday)), Day("Sat", entry, nameof(entry.Saturday)),
            Day("Sun", entry, nameof(entry.Sunday)));
        var shortcuts = Ui.Row(6,
            Ui.Command("EVERY DAY", entry.SetDaysCommand, parameter: () => "every"),
            Ui.Command("MON–FRI", entry.SetDaysCommand, parameter: () => "weekdays"),
            Ui.Command("WEEKEND", entry.SetDaysCommand, parameter: () => "weekend"),
            Ui.Command("NONE", entry.SetDaysCommand, parameter: () => "none"));
        var weeks = WritingUi.Choice(entry, nameof(entry.WeekInterval), AlarmEntryViewModel.WeekIntervals);
        weeks.TooltipText = "One week in 2, 3 or 4: counted from the week of the first day below";
        var firstWeek = Ui.Line(entry, nameof(entry.Date), "yyyy-MM-dd");
        var weekly = Bind.Visible(Ui.Column(8,
            Ui.Field("Days", days),
            shortcuts,
            WritingUi.Cells(2, Ui.Field("Weeks", weeks), Ui.Field("First week (a day in it, yyyy-MM-dd)", firstWeek))), entry, nameof(entry.IsWeekly));

        var ringtone = WritingUi.Choice(entry, nameof(entry.Ringtone), AlarmClock.Ringtones);
        var snooze = Bind.Number(new SpinBox { MinValue = 1, MaxValue = 60, Step = 1, Rounded = true, Suffix = "min", FocusMode = Control.FocusModeEnum.Click }, entry, nameof(entry.SnoozeMinutes));

        var fields = Ui.Column(12,
            Ui.Field("Label", Ui.Line(entry, nameof(entry.Label), "Wake up, Meeting, Medicine…")),
            Ui.Field("Time", timeRow),
            Ui.Field("Repeat", repeat),
            once,
            weekly,
            Eve.Eve.Separator(),
            WritingUi.Cells(2,
                Ui.Field("Ringtone", Ui.Row(6, Ui.Fill(ringtone), Ui.Command("▶ TEST", vm.TestCommand, tooltip: "Hear the ringtone for 4 seconds"))),
                Ui.Field("Snooze", snooze)),
            Ui.Check("The avatar says the label when it rings", entry, nameof(entry.Speak)),
            Ui.Label(entry, nameof(entry.NextLabel), Eve.Eve.Amber, 13));
        return Ui.Scroll(Ui.Margin(fields, 14, 12, 14, 12));
    }

    /// <summary>A day of the week: a toggle button lit while ticked.</summary>
    private static Button Day(string text, AlarmEntryViewModel entry, string name)
    {
        var button = Bind.Check(Eve.Eve.Button(text, tooltip: $"Ring on {text}"), entry, name);
        button.CustomMinimumSize = new Vector2(52, 34);
        return button;
    }
}
