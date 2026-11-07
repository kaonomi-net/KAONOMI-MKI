using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// /alarm: the alarm clock. The alarms on the left (time, label, when, on / off), the selected one editable on the
/// right: label, hour and minute, once on a date or each week on the days ticked (every day, Mon–Fri and weekend in
/// one click; one week in 1 to 4), ringtone (▶ TEST), snooze and whether the avatar says the label. Every change is kept
/// at once in ~/kaonomi/alarms.json (<see cref="AlarmClock"/>); the chat rings them even when this window is closed.
/// </summary>
public partial class AlarmWindowViewModel : ViewModelBase
{
    private readonly AlarmClock _clock;

    public AlarmWindowViewModel() : this(AlarmClock.Shared)
    {
    }

    public AlarmWindowViewModel(AlarmClock clock)
    {
        _clock = clock;
        foreach (var alarm in clock.Alarms.OrderBy(a => a.Time))
            Alarms.Add(new AlarmEntryViewModel(clock, alarm));
        SelectedAlarm = Alarms.FirstOrDefault();
        RefreshStatus();
    }

    /// <summary>Raised by ▶ TEST: the view plays the ringtone named for a few seconds.</summary>
    public event Action<string>? TestRequested;

    public ObservableCollection<AlarmEntryViewModel> Alarms { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(TestCommand))]
    private AlarmEntryViewModel? _selectedAlarm;

    [ObservableProperty]
    private string _status = string.Empty;

    public bool HasNoAlarm => Alarms.Count == 0;

    [RelayCommand]
    private void New()
    {
        var entry = new AlarmEntryViewModel(_clock, _clock.Add());
        Alarms.Add(entry);
        SelectedAlarm = entry;
        OnPropertyChanged(nameof(HasNoAlarm));
        RefreshStatus();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (SelectedAlarm is not { } entry)
            return;
        var index = Alarms.IndexOf(entry);
        _clock.Remove(entry.Model);
        Alarms.Remove(entry);
        SelectedAlarm = Alarms.Count == 0 ? null : Alarms[Math.Min(index, Alarms.Count - 1)];
        OnPropertyChanged(nameof(HasNoAlarm));
        RefreshStatus();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Test()
    {
        if (SelectedAlarm is { } entry)
            TestRequested?.Invoke(entry.Ringtone);
    }

    private bool HasSelection() => SelectedAlarm is not null;

    /// <summary>The status bar: how many alarms are on and the next ring.</summary>
    public void RefreshStatus()
    {
        foreach (var entry in Alarms)
            entry.RefreshNext();
        var now = DateTime.Now;
        var on = Alarms.Count(a => a.Enabled);
        var next = _clock.Upcoming(now).FirstOrDefault();
        Status = Alarms.Count == 0
            ? "No alarm: NEW adds one"
            : next.Alarm is null
                ? $"{Alarms.Count} alarm(s), none on"
                : $"{on} alarm(s) on of {Alarms.Count} · next: {next.Alarm.Label} {AlarmEntryViewModel.When(next.Ring, now)}";
    }
}

/// <summary>An alarm of the list and of the editor: every change goes to <see cref="AlarmClock"/> at once.</summary>
public partial class AlarmEntryViewModel : ViewModelBase
{
    private readonly AlarmClock _clock;

    public AlarmEntryViewModel(AlarmClock clock, Alarm alarm)
    {
        _clock = clock;
        Model = alarm;
        RefreshNext();
    }

    public Alarm Model { get; }

    public static string[] Repeats { get; } = ["Once", "Weekly"];

    public static string[] WeekIntervals { get; } = ["Every week", "1 week in 2", "1 week in 3", "1 week in 4"];

    /// <summary>"00" … "23", for the drop-down of the hour.</summary>
    public static string[] Hours { get; } = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray();

    /// <summary>"00" … "59", for the drop-down of the minute.</summary>
    public static string[] Minutes { get; } = Enumerable.Range(0, 60).Select(m => m.ToString("00")).ToArray();

    /// <summary>The hour as "07" (the drop-down).</summary>
    public string HourText
    {
        get => Model.Hour.ToString("00");
        set
        {
            if (int.TryParse(value, out var hour))
                Hour = hour;
        }
    }

    /// <summary>The minute as "05" (the drop-down).</summary>
    public string MinuteText
    {
        get => Model.Minute.ToString("00");
        set
        {
            if (int.TryParse(value, out var minute))
                Minute = minute;
        }
    }

    public string Label
    {
        get => Model.Label;
        set => Change(() => Model.Label = value);
    }

    public int Hour
    {
        get => Model.Hour;
        set => Change(() => Model.Hour = Math.Clamp(value, 0, 23));
    }

    public int Minute
    {
        get => Model.Minute;
        set => Change(() => Model.Minute = Math.Clamp(value, 0, 59));
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set => Change(() => Model.Enabled = value);
    }

    /// <summary>"Once" or "Weekly".</summary>
    public string Repeat
    {
        get => Model.Repeat.ToString();
        set => Change(() => Model.Repeat = Enum.TryParse<AlarmRepeat>(value, out var repeat) ? repeat : AlarmRepeat.Weekly);
    }

    public bool IsOnce => Model.Repeat == AlarmRepeat.Once;

    public bool IsWeekly => Model.Repeat == AlarmRepeat.Weekly;

    /// <summary>The date of a single ring, yyyy-MM-dd (also the first week of a weekly alarm).</summary>
    public string Date
    {
        get => Model.Date.ToString("yyyy-MM-dd");
        set
        {
            if (DateTime.TryParseExact(value?.Trim(), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date))
                Change(() => Model.Date = date);
        }
    }

    public string WeekInterval
    {
        get => WeekIntervals[Math.Clamp(Model.EveryWeeks, 1, 4) - 1];
        set => Change(() => Model.EveryWeeks = Math.Max(0, Array.IndexOf(WeekIntervals, value)) + 1);
    }

    public bool Monday { get => Has(AlarmDays.Monday); set => SetDay(AlarmDays.Monday, value); }
    public bool Tuesday { get => Has(AlarmDays.Tuesday); set => SetDay(AlarmDays.Tuesday, value); }
    public bool Wednesday { get => Has(AlarmDays.Wednesday); set => SetDay(AlarmDays.Wednesday, value); }
    public bool Thursday { get => Has(AlarmDays.Thursday); set => SetDay(AlarmDays.Thursday, value); }
    public bool Friday { get => Has(AlarmDays.Friday); set => SetDay(AlarmDays.Friday, value); }
    public bool Saturday { get => Has(AlarmDays.Saturday); set => SetDay(AlarmDays.Saturday, value); }
    public bool Sunday { get => Has(AlarmDays.Sunday); set => SetDay(AlarmDays.Sunday, value); }

    public string Ringtone
    {
        get => Model.Ringtone;
        set => Change(() => Model.Ringtone = value);
    }

    public int SnoozeMinutes
    {
        get => Model.SnoozeMinutes;
        set => Change(() => Model.SnoozeMinutes = Math.Clamp(value, 1, 60));
    }

    public bool Speak
    {
        get => Model.Speak;
        set => Change(() => Model.Speak = value);
    }

    /// <summary>"07:30".</summary>
    public string TimeLabel => $"{Model.Hour:00}:{Model.Minute:00}";

    /// <summary>"Mon–Fri", "every day", "Thu 6 Nov 2026"…</summary>
    public string WhenLabel => AlarmClock.Describe(Model);

    /// <summary>"next: tomorrow 07:30", "snoozed until 07:35", "off".</summary>
    [ObservableProperty]
    private string _nextLabel = string.Empty;

    /// <summary>Every day, Mon–Fri, weekend, no day: the days ticked in one click.</summary>
    [RelayCommand]
    private void SetDays(string which) => Change(() =>
    {
        Model.Repeat = AlarmRepeat.Weekly;
        Model.Days = which switch
        {
            "every" => AlarmDays.Every,
            "weekdays" => AlarmDays.Weekdays,
            "weekend" => AlarmDays.Weekend,
            _ => AlarmDays.None
        };
    });

    public void RefreshNext()
    {
        var now = DateTime.Now;
        NextLabel = !Model.Enabled ? "off"
            : Model.SnoozedUntil is { } snoozed && snoozed > now ? $"snoozed until {snoozed:HH:mm}"
            : AlarmClock.NextRing(Model, now) is { } next ? "next: " + When(next, now)
            : Model.Repeat == AlarmRepeat.Weekly && Model.Days == AlarmDays.None ? "no day ticked" : "date gone";
    }

    /// <summary>"today 07:30", "tomorrow 07:30", "Mon 10 Nov 07:30".</summary>
    public static string When(DateTime ring, DateTime now) =>
        (ring.Date == now.Date ? "today" : ring.Date == now.Date.AddDays(1) ? "tomorrow" : ring.ToString("ddd d MMM")) + $" {ring:HH:mm}";

    private bool Has(AlarmDays day) => Model.Days.HasFlag(day);

    private void SetDay(AlarmDays day, bool on) => Change(() => Model.Days = on ? Model.Days | day : Model.Days & ~day);

    /// <summary>Applies a change to the alarm, keeps it, and tells the view every property may have moved.</summary>
    private void Change(Action apply)
    {
        apply();
        _clock.Update(Model);
        RefreshNext();
        // Empty name: every binding of this alarm reads its value again (the days, the labels, the sections shown).
        OnPropertyChanged(string.Empty);
    }
}
