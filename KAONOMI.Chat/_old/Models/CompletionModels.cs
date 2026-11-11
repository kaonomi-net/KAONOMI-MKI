using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>A prompt and its completion sent to <c>PUT /completion</c> (<c>Completion</c> on the server side).</summary>
public sealed class CompletionEntry
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

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("completion")]
    public string Completion { get; set; } = string.Empty;

    /// <summary>The completion without its text formatting (Markdown removed, see <c>PlainText.FromMarkdown</c>).</summary>
    [JsonPropertyName("rawCompletion")]
    public string RawCompletion { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>FR or EN.</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    /// <summary>The <c>"topic"</c> of the <c>.prompt.json</c> file, when it has one.</summary>
    [JsonPropertyName("topic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Topic { get; set; }

    [JsonPropertyName("collectionName")]
    public string CollectionName { get; set; } = "generic";

    [JsonPropertyName("metadatas")]
    public List<CompletionMetaData> Metadatas { get; set; } = [];

    /// <summary>Occurrences of each term of the prompt (<c>storedIn</c> = PROMPT) and of the completion (COMPLETION).</summary>
    [JsonPropertyName("wordCounts")]
    public List<WordCountByTerm> WordCounts { get; set; } = [];
}

/// <summary>A term and its number of occurrences in the prompt or in the completion (<c>WordCountByTerm</c> on the server side).</summary>
public sealed class WordCountByTerm
{
    /// <summary>Value of <see cref="StoredIn"/> for a term of the prompt.</summary>
    public const string Prompt = "PROMPT";

    /// <summary>Value of <see cref="StoredIn"/> for a term of the completion.</summary>
    public const string Completion = "COMPLETION";

    [JsonPropertyName("term")]
    public string Term { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary><see cref="Prompt"/> or <see cref="Completion"/>.</summary>
    [JsonPropertyName("storedIn")]
    public string StoredIn { get; set; } = string.Empty;
}

/// <summary>Typed key / value of a completion (<c>MetaData</c> on the server side).</summary>
public sealed class CompletionMetaData
{
    [JsonPropertyName("Key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("Value_String")]
    public string ValueString { get; set; } = string.Empty;

    [JsonPropertyName("Value_Int")]
    public int ValueInt { get; set; }

    [JsonPropertyName("Value_Double")]
    public double ValueDouble { get; set; }
}
