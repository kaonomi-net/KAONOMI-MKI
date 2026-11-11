using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Body of <c>POST /aiassistant</c> (<c>DialogGenericRequest</c> on the server side).
/// </summary>
public sealed class AssistantRequest
{
    [JsonPropertyName("model")]
    /// <summary>Ollama model name passed through to the server.</summary>
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    /// <summary>Conversation history including an optional system message.</summary>
    public required IReadOnlyList<AssistantMessage> Messages { get; init; }

    [JsonPropertyName("stream")]
    /// <summary>When true, the server may stream tokens (currently unused by the client).</summary>
    public bool Stream { get; init; }
}

/// <summary>One chat turn in the assistant API (role and content).</summary>
public sealed record AssistantMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// Chat-completion style answer of <c>/aiassistant</c> (<c>DialogGenericResponse</c> on the server side).
/// </summary>
public sealed class AssistantResponse
{
    [JsonPropertyName("id")]
    /// <summary>Server-assigned completion identifier, if any.</summary>
    public string? Id { get; set; }

    [JsonPropertyName("model")]
    /// <summary>Model that produced the answer.</summary>
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    /// <summary>Completion choices; the client reads the first message.</summary>
    public List<AssistantChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    /// <summary>Token counts when reported by the backend.</summary>
    public AssistantUsage? Usage { get; set; }
}

/// <summary>Single completion choice in an assistant response.</summary>
public sealed class AssistantChoice
{
    [JsonPropertyName("index")]
    /// <summary>Zero-based index of this choice.</summary>
    public int Index { get; set; }

    [JsonPropertyName("message")]
    /// <summary>Assistant message content for this choice.</summary>
    public AssistantMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    /// <summary>Reason the model stopped generating, if provided.</summary>
    public string? FinishReason { get; set; }
}

/// <summary>Token usage statistics for one assistant call.</summary>
public sealed class AssistantUsage
{
    [JsonPropertyName("prompt_tokens")]
    /// <summary>Tokens consumed by the prompt.</summary>
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    /// <summary>Tokens generated in the completion.</summary>
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    /// <summary>Sum of prompt and completion tokens.</summary>
    public int TotalTokens { get; set; }
}
