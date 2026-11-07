using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>When an alarm rings again: once (on its date), or each week on the days ticked.</summary>
public enum AlarmRepeat
{
    Once,
    Weekly
}

/// <summary>The days of the week an alarm rings on (<see cref="AlarmRepeat.Weekly"/>).</summary>
[Flags]
public enum AlarmDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    Every = Weekdays | Weekend
}

/// <summary>
/// An alarm of /alarm: its time, its label, on or off, when it rings again (once on a date, or on the days ticked of
/// every week, or of one week in two, three or four), its ringtone, its snooze and whether the avatar says its label.
/// Kept in ~/kaonomi/alarms.json.
/// </summary>
public sealed class Alarm
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label { get; set; } = "Wake up";

    public int Hour { get; set; } = 7;

    public int Minute { get; set; }

    public bool Enabled { get; set; } = true;

    public AlarmRepeat Repeat { get; set; } = AlarmRepeat.Weekly;

    /// <summary>The days ticked (<see cref="AlarmRepeat.Weekly"/>).</summary>
    public AlarmDays Days { get; set; } = AlarmDays.Weekdays;

    /// <summary>1: every week, 2: one week in two… (<see cref="AlarmRepeat.Weekly"/>), counted from the week of <see cref="Date"/>.</summary>
    public int EveryWeeks { get; set; } = 1;

    /// <summary>The day of an alarm rung <see cref="AlarmRepeat.Once"/>; for a weekly one, a day of its first week.</summary>
    public DateTime Date { get; set; } = DateTime.Today;

    public string Ringtone { get; set; } = "Bell";

    public int SnoozeMinutes { get; set; } = 5;

    /// <summary>The avatar says the label when it rings.</summary>
    public bool Speak { get; set; } = true;

    /// <summary>The last time it rang (a ring is never given twice).</summary>
    public DateTime? LastRung { get; set; }

    /// <summary>Snoozed: it rings again then.</summary>
    public DateTime? SnoozedUntil { get; set; }

    [JsonIgnore]
    public TimeSpan Time => new(Hour, Minute, 0);

    public static AlarmDays DayOf(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => AlarmDays.Monday,
        DayOfWeek.Tuesday => AlarmDays.Tuesday,
        DayOfWeek.Wednesday => AlarmDays.Wednesday,
        DayOfWeek.Thursday => AlarmDays.Thursday,
        DayOfWeek.Friday => AlarmDays.Friday,
        DayOfWeek.Saturday => AlarmDays.Saturday,
        _ => AlarmDays.Sunday
    };
}
