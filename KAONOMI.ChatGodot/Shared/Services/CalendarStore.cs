using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The events of /calendar, shared by its window and by the ringer of the chat (which rings their reminders even when
/// the window is closed): kept in ~/kaonomi/calendar.json, the occurrences of the repeated events in a range of days,
/// the reminders due, SNOOZE. A reminder missed while the chat was closed still rings within
/// <see cref="AlarmClock.Grace"/> of its time.
/// </summary>
public sealed class CalendarStore
{
    /// <summary>The colours an event can take.</summary>
    public static readonly string[] Colors = ["Cyan", "Green", "Amber", "Red", "Grey"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private static CalendarStore? _shared;

    private readonly string _path;
    private readonly List<CalendarEvent> _events;

    public CalendarStore(string path)
    {
        _path = path;
        _events = Load(path);
    }

    /// <summary>The events of the chat (~/kaonomi/calendar.json).</summary>
    public static CalendarStore Shared => _shared ??= new CalendarStore(Path.Combine(ChatSettings.KaonomiHome, "calendar.json"));

    /// <summary>Raised when an event is added, changed or removed, or a reminder rung or snoozed.</summary>
    public event Action? Changed;

    public IReadOnlyList<CalendarEvent> Events => _events;

    /// <summary>A new event on <paramref name="day"/> at <paramref name="time"/> (an hour long); made at <paramref name="at"/>.</summary>
    public CalendarEvent Add(DateTime day, TimeSpan time, DateTime? at = null)
    {
        var created = new CalendarEvent { Start = day.Date + time, LastReminded = at ?? DateTime.Now };
        _events.Add(created);
        Save();
        return created;
    }

    public void Remove(CalendarEvent removed)
    {
        if (_events.Remove(removed))
            Save();
    }

    /// <summary>After a change in <paramref name="changed"/> at <paramref name="at"/>: only the reminders after the change ring.</summary>
    public void Update(CalendarEvent changed, DateTime? at = null)
    {
        changed.SnoozedUntil = null;
        changed.LastReminded = at ?? DateTime.Now;
        Save();
    }

    /// <summary>The starts of the occurrences of <paramref name="e"/> from <paramref name="from"/> (included) to <paramref name="to"/> (excluded), in order.</summary>
    public static IEnumerable<DateTime> Occurrences(CalendarEvent e, DateTime from, DateTime to)
    {
        var first = e.AllDay ? e.Start.Date : e.Start;
        // An event that started before the range but is still going on counts too.
        var length = e.AllDay ? TimeSpan.FromDays(1) : (e.Duration > TimeSpan.Zero ? e.Duration : TimeSpan.Zero);
        if (e.Repeat == CalendarRepeat.None)
        {
            if (first < to && first + length > from || first >= from && first < to)
                yield return first;
            yield break;
        }
        // The first occurrence that may meet the range, then one after the other.
        var n = 0;
        if (e.Repeat == CalendarRepeat.Daily && from - length > first)
            n = (int)Math.Floor((from - length - first).TotalDays);
        else if (e.Repeat == CalendarRepeat.Weekly && from - length > first)
            n = (int)Math.Floor((from - length - first).TotalDays / 7);
        for (var guard = 0; guard < 5000; guard++, n++)
        {
            var start = Nth(e, first, n);
            if (start is null)
                continue;
            if (start.Value >= to)
                yield break;
            if (start.Value + length > from || start.Value >= from)
                yield return start.Value;
        }
    }

    /// <summary>The <paramref name="n"/>th occurrence of <paramref name="e"/> (null: none that month, a 31st in a short month).</summary>
    private static DateTime? Nth(CalendarEvent e, DateTime first, int n) => e.Repeat switch
    {
        CalendarRepeat.Daily => first.AddDays(n),
        CalendarRepeat.Weekly => first.AddDays(7 * n),
        // Monthly and yearly keep the day: a 31st rings only in the months with a 31st, a 29 February only in leap years.
        CalendarRepeat.Monthly => SameDay(first, first.AddMonths(n)),
        CalendarRepeat.Yearly => SameDay(first, first.AddYears(n)),
        _ => n == 0 ? first : null
    };

    private static DateTime? SameDay(DateTime first, DateTime moved) => moved.Day == first.Day ? moved : null;

    /// <summary>The occurrences of every event on <paramref name="day"/>: the whole days first, then by time.</summary>
    public IReadOnlyList<(CalendarEvent Event, DateTime Start)> OnDay(DateTime day)
    {
        var from = day.Date;
        var to = from.AddDays(1);
        return _events.SelectMany(e => Occurrences(e, from, to).Select(start => (Event: e, Start: start)))
            .OrderBy(x => x.Event.AllDay ? 0 : 1).ThenBy(x => x.Start).ThenBy(x => x.Event.Title)
            .ToList();
    }

    /// <summary>When the next reminder of <paramref name="e"/> rings after <paramref name="after"/>, with the start of its occurrence.</summary>
    public static (DateTime Ring, DateTime Start)? NextReminder(CalendarEvent e, DateTime after)
    {
        if (!e.HasReminder)
            return null;
        if (e.SnoozedUntil is { } snoozed && snoozed > after)
            return (snoozed, snoozed);
        if (e.LastReminded is { } given && given > after)
            after = given;
        var before = TimeSpan.FromMinutes(e.ReminderMinutes);
        // An event of the whole day is reminded from 09:00 of its day.
        var shift = e.AllDay ? TimeSpan.FromHours(9) : TimeSpan.Zero;
        foreach (var start in Occurrences(e, after + before - shift, after.AddYears(2)))
        {
            var ring = start + shift - before;
            if (ring > after)
                return (ring, start);
        }
        return null;
    }

    /// <summary>The reminders whose time has come at <paramref name="now"/> (within <see cref="AlarmClock.Grace"/>), marked as given.</summary>
    public IReadOnlyList<(CalendarEvent Event, DateTime Start)> Due(DateTime now)
    {
        var due = new List<(CalendarEvent, DateTime)>();
        foreach (var e in _events)
        {
            if (NextReminder(e, now - AlarmClock.Grace) is not { } next || next.Ring > now)
                continue;
            e.LastReminded = next.Ring;
            e.SnoozedUntil = null;
            due.Add((e, next.Start));
        }
        if (due.Count > 0)
            Save();
        return due;
    }

    /// <summary>SNOOZE: the reminder of <paramref name="e"/> rings again in <paramref name="minutes"/> minutes.</summary>
    public void Snooze(CalendarEvent e, DateTime now, int minutes = 5)
    {
        e.SnoozedUntil = now.AddMinutes(Math.Clamp(minutes, 1, 60));
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_events, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Calendar", "The events could not be saved", ex);
        }
        Changed?.Invoke();
    }

    private static List<CalendarEvent> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<CalendarEvent>>(File.ReadAllText(path), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Calendar", $"{path} could not be read", ex);
            return [];
        }
    }
}
