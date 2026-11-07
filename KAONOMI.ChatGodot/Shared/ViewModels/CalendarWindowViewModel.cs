using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A line in a day of the month or of its list: an occurrence of an event, or an alarm of /alarm ringing that day.</summary>
public sealed record CalendarItem(string Time, string Title, string Color, CalendarEvent? Event, bool IsAlarm)
{
    public string Text => string.IsNullOrEmpty(Time) ? Title : $"{Time} {Title}";
}

/// <summary>A day of the grid of the month: its number, whether it is of the month shown, today, selected, and its first lines.</summary>
public sealed record CalendarDay(DateTime Date, bool IsCurrentMonth, bool IsToday, bool IsSelected, IReadOnlyList<CalendarItem> Items)
{
    public const int Shown = 3;

    public int Number => Date.Day;

    public IEnumerable<CalendarItem> FirstItems => Items.Take(Shown);

    public int More => Math.Max(0, Items.Count - Shown);
}

/// <summary>
/// /calendar: the month (Monday first, 6 weeks), ◀ ▶ and TODAY; each day shows its events and the alarms of /alarm
/// ringing that day. The selected day lists them on the right, with the editor of the selected event: title, day, start
/// and end (or the whole day), repeat (never, every day, week, month, year), reminder (rung by the chat as the alarms
/// are), colour and notes. Every change is kept at once in ~/kaonomi/calendar.json (<see cref="CalendarStore"/>).
/// </summary>
public partial class CalendarWindowViewModel : ViewModelBase
{
    private readonly CalendarStore _store;
    private readonly AlarmClock _alarms;

    public CalendarWindowViewModel() : this(CalendarStore.Shared, AlarmClock.Shared)
    {
    }

    public CalendarWindowViewModel(CalendarStore store, AlarmClock alarms)
    {
        _store = store;
        _alarms = alarms;
        _selectedDay = DateTime.Today;
        _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        Refresh();
    }

    /// <summary>The first day of the month shown.</summary>
    [ObservableProperty]
    private DateTime _month;

    [ObservableProperty]
    private DateTime _selectedDay;

    [ObservableProperty]
    private CalendarEventEntryViewModel? _selectedEvent;

    [ObservableProperty]
    private IReadOnlyList<CalendarDay> _days = [];

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>The events and alarms of the selected day.</summary>
    public ObservableCollection<CalendarItem> DayItems { get; } = [];

    public string MonthTitle => Capitalize(Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture));

    public string SelectedDayTitle => Capitalize(SelectedDay.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture));

    /// <summary>Mon … Sun, in the language of the machine.</summary>
    public static IReadOnlyList<string> WeekDays { get; } = Enumerable.Range(0, 7)
        .Select(i => Capitalize(CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)((i + 1) % 7))))
        .ToList();

    partial void OnMonthChanged(DateTime value)
    {
        OnPropertyChanged(nameof(MonthTitle));
        Refresh();
    }

    partial void OnSelectedDayChanged(DateTime value)
    {
        OnPropertyChanged(nameof(SelectedDayTitle));
        // The event edited stays when it is of that day too.
        if (SelectedEvent is { } edited && !_store.OnDay(value).Any(x => x.Event == edited.Model))
            SelectedEvent = null;
        Refresh();
    }

    [RelayCommand]
    private void Previous() => Month = Month.AddMonths(-1);

    [RelayCommand]
    private void Next() => Month = Month.AddMonths(1);

    [RelayCommand]
    private void Today()
    {
        Month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        SelectedDay = DateTime.Today;
    }

    /// <summary>A click on a day: it is selected (a day of another month shows that month).</summary>
    public void Select(DateTime day)
    {
        if (day.Month != Month.Month || day.Year != Month.Year)
            Month = new DateTime(day.Year, day.Month, 1);
        SelectedDay = day.Date;
    }

    /// <summary>A click on a line of the day: its event in the editor (an alarm opens nothing here).</summary>
    public void Open(CalendarItem item)
    {
        if (item.Event is { } e)
            SelectedEvent = new CalendarEventEntryViewModel(_store, e);
    }

    /// <summary>NEW EVENT, or a double-click on a day: an event of an hour at 09:00 on the selected day.</summary>
    [RelayCommand]
    private void NewEvent()
    {
        var created = _store.Add(SelectedDay, TimeSpan.FromHours(9));
        SelectedEvent = new CalendarEventEntryViewModel(_store, created);
        Refresh();
    }

    [RelayCommand]
    private void DeleteEvent()
    {
        if (SelectedEvent is not { } entry)
            return;
        _store.Remove(entry.Model);
        SelectedEvent = null;
        Refresh();
    }

    /// <summary>The grid of the month and the list of the day built again (after a change of the events or of the alarms).</summary>
    public void Refresh()
    {
        var today = DateTime.Today;
        var first = Month.AddDays(-(((int)Month.DayOfWeek + 6) % 7));
        // The alarms ringing each day, from their rules alone (not from the rings already given).
        var rules = _alarms.Alarms.Where(a => a.Enabled).Select(a => (Alarm: a, Rule: Rule(a))).ToList();
        Days = Enumerable.Range(0, 42).Select(i =>
        {
            var day = first.AddDays(i);
            return new CalendarDay(day, day.Month == Month.Month, day == today, day == SelectedDay, ItemsOn(day, rules));
        }).ToList();
        DayItems.Clear();
        foreach (var item in ItemsOn(SelectedDay, rules))
            DayItems.Add(item);
        var count = _store.Events.Count;
        Status = count == 0 ? "No event: NEW EVENT (or a double-click on a day) adds one" : $"{count} event(s) · {DayItems.Count} on {SelectedDay:d}";
    }

    private List<CalendarItem> ItemsOn(DateTime day, List<(Alarm Alarm, Alarm Rule)> rules)
    {
        var items = _store.OnDay(day)
            .Select(x => new CalendarItem(x.Event.AllDay ? string.Empty : x.Start.ToString("HH:mm"), x.Event.Title, x.Event.Color, x.Event, false))
            .ToList();
        foreach (var (alarm, rule) in rules)
            if (AlarmClock.NextRing(rule, day.AddTicks(-1)) is { } ring && ring.Date == day)
                items.Add(new CalendarItem(ring.ToString("HH:mm"), "⏰ " + alarm.Label, "Amber", null, true));
        return items.OrderBy(i => i.Time).ThenBy(i => i.Title).ToList();
    }

    /// <summary>A copy of <paramref name="alarm"/> without its rings given or its snooze: when it rings by its rule.</summary>
    private static Alarm Rule(Alarm alarm) => new()
    {
        Hour = alarm.Hour, Minute = alarm.Minute, Enabled = true, Repeat = alarm.Repeat, Days = alarm.Days,
        EveryWeeks = alarm.EveryWeeks, Date = alarm.Date
    };

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}

/// <summary>An event in the editor: every change goes to <see cref="CalendarStore"/> at once.</summary>
public partial class CalendarEventEntryViewModel : ViewModelBase
{
    private readonly CalendarStore _store;

    public CalendarEventEntryViewModel(CalendarStore store, CalendarEvent e)
    {
        _store = store;
        Model = e;
    }

    public CalendarEvent Model { get; }

    public static string[] Repeats { get; } = ["None", "Daily", "Weekly", "Monthly", "Yearly"];

    public static string[] Reminders { get; } = ["None", "At the start", "5 min before", "15 min before", "30 min before", "1 hour before", "1 day before"];

    private static readonly int[] ReminderMinutes = [CalendarEvent.NoReminder, 0, 5, 15, 30, 60, 1440];

    public static string[] Hours => AlarmEntryViewModel.Hours;

    /// <summary>"00", "05" … "55": the minutes of the drop-downs, every 5 minutes.</summary>
    public static string[] Minutes { get; } = Enumerable.Range(0, 12).Select(m => (m * 5).ToString("00")).ToArray();

    public static string[] Colors => CalendarStore.Colors;

    public string Title
    {
        get => Model.Title;
        set => Change(() => Model.Title = value);
    }

    /// <summary>The day, yyyy-MM-dd.</summary>
    public string Date
    {
        get => Model.Start.ToString("yyyy-MM-dd");
        set
        {
            if (DateTime.TryParseExact(value?.Trim(), "yyyy-MM-dd", null, DateTimeStyles.None, out var day))
                Change(() => Model.Start = day + Model.Start.TimeOfDay);
        }
    }

    public bool AllDay
    {
        get => Model.AllDay;
        set => Change(() => Model.AllDay = value);
    }

    public bool HasTime => !Model.AllDay;

    public string StartHour
    {
        get => Model.Start.Hour.ToString("00");
        set => SetStart(value, null);
    }

    public string StartMinute
    {
        get => Nearest5(Model.Start.Minute);
        set => SetStart(null, value);
    }

    public string EndHour
    {
        get => End.Hour.ToString("00");
        set => SetEnd(value, null);
    }

    public string EndMinute
    {
        get => Nearest5(End.Minute);
        set => SetEnd(null, value);
    }

    public string Repeat
    {
        get => Model.Repeat.ToString();
        set => Change(() => Model.Repeat = Enum.TryParse<CalendarRepeat>(value, out var repeat) ? repeat : CalendarRepeat.None);
    }

    public string Reminder
    {
        get => Reminders[Math.Max(0, Array.IndexOf(ReminderMinutes, Model.ReminderMinutes))];
        set => Change(() => Model.ReminderMinutes = ReminderMinutes[Math.Max(0, Array.IndexOf(Reminders, value))]);
    }

    public string Color
    {
        get => Model.Color;
        set => Change(() => Model.Color = value);
    }

    public string Notes
    {
        get => Model.Notes;
        set => Change(() => Model.Notes = value);
    }

    private DateTime End => Model.Start + Model.Duration;

    private void SetStart(string? hour, string? minute)
    {
        var h = hour is not null && int.TryParse(hour, out var hv) ? hv : Model.Start.Hour;
        var m = minute is not null && int.TryParse(minute, out var mv) ? mv : Model.Start.Minute;
        // The event keeps its length when its start moves.
        Change(() => Model.Start = Model.Start.Date + new TimeSpan(h, m, 0));
    }

    private void SetEnd(string? hour, string? minute)
    {
        var h = hour is not null && int.TryParse(hour, out var hv) ? hv : End.Hour;
        var m = minute is not null && int.TryParse(minute, out var mv) ? mv : End.Minute;
        var end = Model.Start.Date + new TimeSpan(h, m, 0);
        // An end before the start is the next day (an evening going on after midnight).
        if (end <= Model.Start)
            end = end.AddDays(1);
        Change(() => Model.Duration = end - Model.Start);
    }

    private static string Nearest5(int minute) => (Math.Min(55, (int)Math.Round(minute / 5.0) * 5)).ToString("00");

    private void Change(Action apply)
    {
        apply();
        _store.Update(Model);
        // Empty name: every binding of this event reads its value again.
        OnPropertyChanged(string.Empty);
    }
}
