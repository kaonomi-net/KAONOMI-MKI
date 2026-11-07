using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The alarms of /alarm, shared by its window and by the ringer of the chat (which rings them even when the window is
/// closed): kept in ~/kaonomi/alarms.json, the next ring of each one, the alarms due, STOP and SNOOZE.
/// An alarm missed while the chat was closed still rings when the chat starts within <see cref="Grace"/> of its time;
/// an older one is passed over.
/// </summary>
public sealed class AlarmClock
{
    /// <summary>How late a missed ring is still given.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    /// <summary>The ringtones the ringer knows how to make.</summary>
    public static readonly string[] Ringtones = ["Bell", "Digital", "Chime", "Rising"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private static AlarmClock? _shared;

    private readonly string _path;
    private readonly List<Alarm> _alarms;

    public AlarmClock(string path)
    {
        _path = path;
        _alarms = Load(path);
    }

    /// <summary>The alarms of the chat (~/kaonomi/alarms.json).</summary>
    public static AlarmClock Shared => _shared ??= new AlarmClock(Path.Combine(ChatSettings.KaonomiHome, "alarms.json"));

    /// <summary>Raised when an alarm is added, changed, removed, rung, stopped or snoozed.</summary>
    public event Action? Changed;

    public IReadOnlyList<Alarm> Alarms => _alarms;

    public Alarm Add(DateTime? at = null)
    {
        var now = at ?? DateTime.Now;
        // Nothing before its creation rings (a new alarm at 07:00 made at 07:05 waits for tomorrow).
        var alarm = new Alarm { Date = now.Date, LastRung = now };
        _alarms.Add(alarm);
        Save();
        return alarm;
    }

    public void Remove(Alarm alarm)
    {
        if (_alarms.Remove(alarm))
            Save();
    }

    /// <summary>
    /// After a change in <paramref name="alarm"/> (time, days, on…) at <paramref name="at"/>: a snooze is forgotten,
    /// and only the rings after the change are given (the label changed at 07:03 does not ring 07:00 again; the time
    /// moved to 07:30 rings at 07:30 the same day).
    /// </summary>
    public void Update(Alarm alarm, DateTime? at = null)
    {
        alarm.SnoozedUntil = null;
        alarm.LastRung = at ?? DateTime.Now;
        Save();
    }

    /// <summary>The next time <paramref name="alarm"/> rings after <paramref name="after"/>, or null (off, or a single ring gone).</summary>
    public static DateTime? NextRing(Alarm alarm, DateTime after)
    {
        if (!alarm.Enabled)
            return null;
        if (alarm.SnoozedUntil is { } snoozed && snoozed > after)
            return snoozed;
        // A ring already given is not given again.
        if (alarm.LastRung is { } rung && rung > after)
            after = rung;
        if (alarm.Repeat == AlarmRepeat.Once)
        {
            var once = alarm.Date.Date + alarm.Time;
            return once > after ? once : null;
        }
        if (alarm.Days == AlarmDays.None)
            return null;
        var every = Math.Clamp(alarm.EveryWeeks, 1, 52);
        var firstWeek = WeekStart(alarm.Date);
        // Every day of the next weeks, up to one full cycle of weeks and a day more.
        for (var d = 0; d <= 7 * every + 1; d++)
        {
            var day = after.Date.AddDays(d);
            if (!alarm.Days.HasFlag(Alarm.DayOf(day.DayOfWeek)))
                continue;
            var weeks = (int)Math.Round((WeekStart(day) - firstWeek).TotalDays / 7);
            if (every > 1 && (weeks < 0 || weeks % every != 0))
                continue;
            var ring = day + alarm.Time;
            if (ring > after)
                return ring;
        }
        return null;
    }

    /// <summary>The next ring of every alarm on, the soonest first.</summary>
    public IEnumerable<(Alarm Alarm, DateTime Ring)> Upcoming(DateTime now) =>
        _alarms.Select(a => (Alarm: a, Ring: NextRing(a, now)))
            .Where(x => x.Ring is not null)
            .Select(x => (x.Alarm, x.Ring!.Value))
            .OrderBy(x => x.Item2);

    /// <summary>
    /// The alarms whose ring has come at <paramref name="now"/> (within <see cref="Grace"/>), marked as rung. A single
    /// ring is turned off once given; a ring missed for longer than <see cref="Grace"/> is passed over.
    /// </summary>
    public IReadOnlyList<Alarm> Due(DateTime now)
    {
        var due = new List<Alarm>();
        var changed = false;
        foreach (var alarm in _alarms)
        {
            if (!alarm.Enabled)
                continue;
            if (NextRing(alarm, now - Grace) is not { } ring || ring > now)
                continue;
            alarm.LastRung = ring;
            alarm.SnoozedUntil = null;
            if (alarm.Repeat == AlarmRepeat.Once)
                alarm.Enabled = false;
            due.Add(alarm);
            changed = true;
        }
        if (changed)
            Save();
        return due;
    }

    /// <summary>SNOOZE: <paramref name="alarm"/> rings again in its snooze minutes.</summary>
    public void Snooze(Alarm alarm, DateTime now)
    {
        alarm.SnoozedUntil = now.AddMinutes(Math.Clamp(alarm.SnoozeMinutes, 1, 60));
        // A single alarm snoozed rings again (it was turned off by its ring).
        alarm.Enabled = true;
        Save();
    }

    /// <summary>A short text of when <paramref name="alarm"/> rings: "Mon–Fri", "every day", "Sat, Sun · 1 week in 2", "Thu 6 Nov".</summary>
    public static string Describe(Alarm alarm, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (alarm.Repeat == AlarmRepeat.Once)
            return alarm.Date.ToString("ddd d MMM yyyy", culture);
        var days = alarm.Days switch
        {
            AlarmDays.None => "no day",
            AlarmDays.Every => "every day",
            AlarmDays.Weekdays => "Mon–Fri",
            AlarmDays.Weekend => "Sat, Sun",
            _ => string.Join(", ", Enum.GetValues<DayOfWeek>().OrderBy(d => ((int)d + 6) % 7)
                .Where(d => alarm.Days.HasFlag(Alarm.DayOf(d)))
                .Select(d => culture.DateTimeFormat.GetAbbreviatedDayName(d)))
        };
        return alarm.EveryWeeks > 1 ? $"{days} · 1 week in {alarm.EveryWeeks}" : days;
    }

    /// <summary>The Monday of the week of <paramref name="day"/>.</summary>
    private static DateTime WeekStart(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_alarms, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Alarm", "The alarms could not be saved", ex);
        }
        Changed?.Invoke();
    }

    private static List<Alarm> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<Alarm>>(File.ReadAllText(path), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Alarm", $"{path} could not be read", ex);
            return [];
        }
    }
}
