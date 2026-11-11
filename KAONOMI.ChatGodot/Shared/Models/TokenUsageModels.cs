using System;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>Token consumption of one LLM call, from <c>GET /tokensusage/last</c> (<c>TokensUsage</c> on the server side).</summary>
public sealed class TokenUsageEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>The project the document is attached to (the id of a <c>Project</c>, chosen by its name in the header of the window), or null.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    /// <summary>Prompt + completion tokens.</summary>
    [JsonIgnore]
    public int TotalTokens => PromptTokens + CompletionTokens;
}
