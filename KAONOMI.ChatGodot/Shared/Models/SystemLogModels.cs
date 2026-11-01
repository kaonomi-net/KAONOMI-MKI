using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>A measure of the <c>/system</c> window sent to <c>PUT /systemlog</c> (<c>SystemLog</c> on the server side).</summary>
public sealed class SystemLogEntry
{
    [JsonPropertyName("appName")]
    public string AppName { get; set; } = "KAONOMI.Chat";

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("machineName")]
    public string MachineName { get; set; } = Environment.MachineName;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = Environment.UserName;

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    /// <summary>CPU, GPU, RAM, VRAM or Harddisk.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>Processor / graphics card model, "RAM", "VRAM", or drive letter and disk model.</summary>
    [JsonPropertyName("systemComponent")]
    public string SystemComponent { get; set; } = string.Empty;

    /// <summary>Usage, 0–100.</summary>
    [JsonPropertyName("valueCurrentInPourcent")]
    public int ValueCurrentInPourcent { get; set; }

    /// <summary>Capacity in MB (RAM, VRAM, drives); left out for the CPU and the GPU.</summary>
    [JsonPropertyName("memoryMaxCapacityInMB")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MemoryMaxCapacityInMB { get; set; }
}
