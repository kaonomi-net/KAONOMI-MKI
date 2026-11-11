using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Document of the <c>/task</c> APIs (<c>TaskItem</c> on the server side).
/// </summary>
public sealed class TaskEntry
{
    [JsonPropertyName("appName")]
    /// <summary>Application that created the task.</summary>
    public string? AppName { get; set; }

    /// <summary>Left out of the body when null so that the server generates the id of a new document.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    /// <summary>Creation date of the task (set by the server when missing).</summary>
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("name")]
    /// <summary>Title of the task.</summary>
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Left out of the body when null (the server keeps its default date). A DateTime, not a DateTimeOffset: the server
    /// sends <c>0001-01-01T00:00:00</c> for a task without date, which a DateTimeOffset east of UTC cannot hold.
    /// </summary>
    [JsonPropertyName("startTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? StartTime { get; set; }

    [JsonPropertyName("endTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? EndTime { get; set; }

    [JsonPropertyName("securityConstraints")]
    public string? SecurityConstraints { get; set; }

    [JsonPropertyName("legalConstraints")]
    public string? LegalConstraints { get; set; }

    [JsonPropertyName("dataConstraints")]
    public string? DataConstraints { get; set; }

    [JsonPropertyName("type")]
    /// <summary>Kind of task: bug, change, agent, infra.</summary>
    public string? Type { get; set; }

    [JsonPropertyName("status")]
    /// <summary>backlog, todo, coding, test, check, deploy or archived.</summary>
    public string? Status { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }
}
