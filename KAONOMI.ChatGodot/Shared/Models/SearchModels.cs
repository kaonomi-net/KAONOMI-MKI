using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>Body of <c>POST /search</c> (<c>MultiSearchCriterias</c> on the server side): a loose search in several indices.</summary>
public sealed class SearchRequest
{
    [JsonPropertyName("term")]
    public required string Term { get; init; }

    [JsonPropertyName("indices")]
    public List<string> Indices { get; init; } = [];

    /// <summary>Hits per index at most.</summary>
    [JsonPropertyName("size")]
    public int Size { get; init; } = 10;

    /// <summary><c>POST /recent</c>: the documents dated within these minutes.</summary>
    [JsonPropertyName("minutes")]
    public int Minutes { get; init; } = 60;
}

/// <summary>An entity found by the NER in the dialogs and how many times (<c>GET /detectedentity/top</c>).</summary>
public sealed class DetectedEntityCount
{
    /// <summary>The name of the ontology, or the form written most often.</summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>PERSON, ORG, LOC, URL, DATE…, or a type of the ontology.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>How many times it was found.</summary>
    [JsonPropertyName("count")]
    public int Count { get; init; }

    /// <summary>In how many dialogs.</summary>
    [JsonPropertyName("dialogs")]
    public int Dialogs { get; init; }

    [JsonPropertyName("ontologyId")]
    public string? OntologyId { get; init; }

    [JsonPropertyName("lastSeen")]
    public DateTime LastSeen { get; init; }
}

/// <summary>A symbol found by the symbolic patterns in the dialogs and how many times (<c>GET /detectedsymbol/top</c>).</summary>
public sealed class DetectedSymbolCount
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    /// <summary>!SAD, !HAPPY, !FEAR, !DISGUST…</summary>
    [JsonPropertyName("signals")]
    public List<string> Signals { get; init; } = [];

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("dialogs")]
    public int Dialogs { get; init; }

    [JsonPropertyName("lastSeen")]
    public DateTime LastSeen { get; init; }
}

/// <summary>A tag found through the detections in the dialogs and how many times (<c>GET /detectedtag/top</c>).</summary>
public sealed class DetectedTagCount
{
    [JsonPropertyName("tag")]
    public string Tag { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("dialogs")]
    public int Dialogs { get; init; }

    /// <summary>symbolicpattern, entity, tag.</summary>
    [JsonPropertyName("origins")]
    public List<string> Origins { get; init; } = [];

    [JsonPropertyName("lastSeen")]
    public DateTime LastSeen { get; init; }
}

/// <summary>A document found by <c>POST /search</c>.</summary>
public sealed class SearchHit
{
    [JsonPropertyName("index")]
    public string Index { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("date")]
    public DateTime? Date { get; init; }

    [JsonPropertyName("appName")]
    public string AppName { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("snippet")]
    public string Snippet { get; init; } = string.Empty;

    /// <summary>The text of the document (its text fields), 4,000 characters at most.</summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>The document itself, as JSON.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "{}";
}
