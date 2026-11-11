using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KAONOMI.Chat.Models;

/// <summary>
/// A page of the wiki of Kaonomi (<c>Wiki</c> on the server side, the <c>wiki</c> index): its id is the slug of its
/// title, given at its creation; its content is Markdown in the format of the rich editor (a link to another page is
/// <c>[text](wiki:slug)</c>). The server keeps its version, its dates, its summary and the pages it links to.
/// </summary>
public sealed partial class WikiPageEntry
{
    /// <summary>The scheme of the links between the pages: <c>wiki:slug</c>.</summary>
    public const string LinkScheme = "wiki:";

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

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Created { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("links")]
    public List<string> Links { get; set; } = [];

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("events")]
    public List<string> Events { get; set; } = [];

    [JsonPropertyName("signals")]
    public List<string> Signals { get; set; } = [];

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>The date of the last version, for the lists.</summary>
    [JsonIgnore]
    public string DateLabel => Date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    /// <summary>
    /// The slug of <paramref name="text"/>, as the server makes it: lower case, no accent, words joined by '-', at most
    /// 80 characters ("Le serveur Kaonomi" gives "le-serveur-kaonomi"); "page" when nothing is left.
    /// </summary>
    public static string Slug(string? text)
    {
        var decomposed = (text ?? string.Empty).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }
        var slug = Dashes().Replace(builder.ToString(), "-").Trim('-');
        if (slug.Length > 80)
            slug = slug[..80].Trim('-');
        return slug.Length == 0 ? "page" : slug;
    }

    /// <summary>A title made from a slug ("le-serveur-kaonomi" gives "Le serveur kaonomi").</summary>
    public static string TitleOf(string slug)
    {
        var words = slug.Replace('-', ' ').Trim();
        return words.Length == 0 ? "New page" : char.ToUpperInvariant(words[0]) + words[1..];
    }

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex Dashes();
}
