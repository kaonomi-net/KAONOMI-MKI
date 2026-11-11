using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// A CV of <c>/cv</c> (<c>Cv</c> on the server side, the <c>cv</c> index): the person, a summary, the educations, the
/// experiences, the skills and the creations. The server keeps its version and its dates. A clone names the CV it
/// comes from and the adaptation it was rewritten for.
/// </summary>
public sealed class CvEntry
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>The project the document is attached to (the id of a <c>Project</c>, chosen by its name in the header of the window), or null.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    /// <summary>The date of the last version.</summary>
    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("lastName")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("firstName")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("addressStreet")]
    public string AddressStreet { get; set; } = string.Empty;

    [JsonPropertyName("addressNumber")]
    public string AddressNumber { get; set; } = string.Empty;

    [JsonPropertyName("addressCity")]
    public string AddressCity { get; set; } = string.Empty;

    [JsonPropertyName("addressZip")]
    public string AddressZip { get; set; } = string.Empty;

    [JsonPropertyName("addressCountry")]
    public string AddressCountry { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = string.Empty;

    [JsonPropertyName("linkedInProfile")]
    public string LinkedInProfile { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>The photo of the person: the path of its file (a copy kept in Pictures/kaonomi/cv); empty: none.</summary>
    [JsonPropertyName("photo")]
    public string Photo { get; set; } = string.Empty;

    [JsonPropertyName("educations")]
    public List<CvEducationEntry> Educations { get; set; } = [];

    [JsonPropertyName("experiences")]
    public List<CvExperienceEntry> Experiences { get; set; } = [];

    [JsonPropertyName("skills")]
    public List<string> Skills { get; set; } = [];

    [JsonPropertyName("creations")]
    public List<CvCreationEntry> Creations { get; set; } = [];

    /// <summary>The id of the CV this one was cloned from (empty: not a clone).</summary>
    [JsonPropertyName("clonedFrom")]
    public string ClonedFrom { get; set; } = string.Empty;

    /// <summary>The adaptation asked when it was cloned.</summary>
    [JsonPropertyName("adaptation")]
    public string Adaptation { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Created { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>The name of the person ("" when none is given).</summary>
    [JsonIgnore]
    public string Person => (LastName + " " + FirstName).Trim();

    /// <summary>The title of the CV, for the lists (the person, then "CV", when it has none).</summary>
    [JsonIgnore]
    public string Label => Title.Length > 0 ? Title : Person.Length > 0 ? Person : "CV";

    /// <summary>The date of the last version, for the lists.</summary>
    [JsonIgnore]
    public string DateLabel => Date is { } date ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>What a row of the list says under the title: the person, what it holds, and that it is a clone.</summary>
    [JsonIgnore]
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Person.Length > 0 && Person != Label)
                parts.Add(Person);
            parts.Add($"{Experiences.Count} exp. · {Educations.Count} edu. · {Skills.Count} skills");
            if (ClonedFrom.Length > 0)
                parts.Add("⧉ clone");
            return string.Join(" · ", parts);
        }
    }
}

public sealed class CvExperienceEntry
{
    [JsonPropertyName("company")]
    public string Company { get; set; } = string.Empty;

    [JsonPropertyName("position")]
    public string Position { get; set; } = string.Empty;

    [JsonPropertyName("startDate")]
    public DateTime? StartDate { get; set; }

    /// <summary>Null: the position held today.</summary>
    [JsonPropertyName("endDate")]
    public DateTime? EndDate { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("technologies")]
    public List<string> Technologies { get; set; } = [];
}

public sealed class CvEducationEntry
{
    [JsonPropertyName("institution")]
    public string Institution { get; set; } = string.Empty;

    [JsonPropertyName("degree")]
    public string Degree { get; set; } = string.Empty;

    [JsonPropertyName("fieldOfStudy")]
    public string FieldOfStudy { get; set; } = string.Empty;

    [JsonPropertyName("startDate")]
    public DateTime? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    public DateTime? EndDate { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

public sealed class CvCreationEntry
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// The dates of a CV as they are typed: a month (<c>2021-03</c>, <c>03/2021</c>), a year (<c>2021</c>) or a day
/// (<c>2021-03-15</c>); empty: none (an end date left empty is "today"). A year typed alone is kept as the first
/// second of that year, a month as its first day at midnight: that second tells them apart when the date is shown again.
/// </summary>
public static class CvDates
{
    private static readonly string[] Formats =
        ["yyyy-MM", "yyyy/MM", "MM/yyyy", "MM-yyyy", "M/yyyy", "yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "MMMM yyyy", "MMM yyyy"];

    /// <summary>The date typed; null when empty or not understood.</summary>
    public static DateTime? Parse(string? text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
            return null;
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.CurrentCulture })
            if (DateTime.TryParseExact(text, Formats, culture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                return DateTime.SpecifyKind(text.Length == 4 ? new DateTime(date.Year, 1, 1, 0, 0, 1) : date, DateTimeKind.Utc);
        return null;
    }

    /// <summary>True when <paramref name="text"/> is empty or a date <see cref="Parse"/> understands.</summary>
    public static bool IsValid(string? text) => string.IsNullOrWhiteSpace(text) || Parse(text) is not null;

    /// <summary>The date as it is typed: its year and its month (<c>2021-03</c>); empty for none.</summary>
    public static string Format(DateTime? date) =>
        date is { Year: > 1 } d ? d.ToString(d is { Month: 1, Day: 1, Second: 1 } ? "yyyy" : "yyyy-MM", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>A period as a CV writes it: <c>2019-03 – 2022-01</c>, <c>2022-02 – today</c>; empty when there is no date.</summary>
    public static string Period(string start, string end)
    {
        start = Format(Parse(start));
        end = Format(Parse(end));
        if (start.Length == 0 && end.Length == 0)
            return string.Empty;
        return start.Length == 0 ? end : $"{start} – {(end.Length > 0 ? end : "today")}";
    }

    /// <summary>A list typed on one line (commas, semicolons or lines between its items).</summary>
    public static List<string> Split(string? text) =>
        (text ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
