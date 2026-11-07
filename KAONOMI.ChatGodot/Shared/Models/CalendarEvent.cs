using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>How an event of /calendar comes back.</summary>
public enum CalendarRepeat
{
    None,
    Daily,
    Weekly,
    Monthly,
    Yearly
}

/// <summary>
/// An event of /calendar: its title, its start and end (or the whole day), how it comes back (never, every day, week,
/// month or year), a reminder some minutes before (rung by the chat as the alarms are), a colour and notes. Kept in
/// ~/kaonomi/calendar.json.
/// </summary>
public sealed class CalendarEvent
{
    /// <summary>No reminder.</summary>
    public const int NoReminder = -1;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = "New event";

    /// <summary>The start of the first occurrence (its day only for an event of the whole day).</summary>
    public DateTime Start { get; set; }

    /// <summary>How long it lasts (ignored for an event of the whole day).</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromHours(1);

    public bool AllDay { get; set; }

    public CalendarRepeat Repeat { get; set; } = CalendarRepeat.None;

    /// <summary>Minutes before the start the reminder rings (0: at the start; <see cref="NoReminder"/>: none).</summary>
    public int ReminderMinutes { get; set; } = NoReminder;

    /// <summary>Cyan, Green, Amber, Red or Grey.</summary>
    public string Color { get; set; } = "Cyan";

    public string Notes { get; set; } = string.Empty;

    /// <summary>The last reminder given (a reminder is never given twice).</summary>
    public DateTime? LastReminded { get; set; }

    /// <summary>A reminder snoozed rings again then.</summary>
    public DateTime? SnoozedUntil { get; set; }

    [JsonIgnore]
    public bool HasReminder => ReminderMinutes >= 0;
}
