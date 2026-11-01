using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>JSON body of Ollama <c>GET /api/tags</c>.</summary>
public sealed class OllamaTagsResponse
{
    [JsonPropertyName("models")]
    /// <summary>Installed models on the local Ollama instance.</summary>
    public List<OllamaModelInfo> Models { get; set; } = [];
}

/// <summary>Metadata for one model listed by Ollama.</summary>
public sealed class OllamaModelInfo
{
    [JsonPropertyName("name")]
    /// <summary>Model name including tag, e.g. <c>mistral:latest</c>.</summary>
    public string Name { get; set; } = string.Empty;
}
