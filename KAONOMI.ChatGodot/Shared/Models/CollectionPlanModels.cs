using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Document of the <c>/collectionplan</c> APIs (<c>CollectionPlan</c> on the server side): a collection plan of the web
/// scraper.
/// </summary>
public sealed class CollectionPlanEntry
{
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>Left out of the body when null so that the server generates the id of a new document.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    /// <summary>Creation date of the plan (set by the server when missing).</summary>
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>The websites the scraper collects, each with its URLs.</summary>
    [JsonPropertyName("websites")]
    public List<WebsiteEntry>? Websites { get; set; }
}

/// <summary>A website of a collection plan (<c>Website</c> on the server side).</summary>
public sealed class WebsiteEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("urlProcesses")]
    public List<UrlProcessEntry>? UrlProcesses { get; set; }
}

/// <summary>A URL of a website and how the scraper processes it (<c>UrlProcess</c> on the server side).</summary>
public sealed class UrlProcessEntry
{
    /// <summary>The modes of the scraper: the page only, the pages it links to, or pages picked at random.</summary>
    public static readonly string[] Modes = ["fixed", "explore", "random"];

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>One of <see cref="Modes"/>.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; set; }
}
