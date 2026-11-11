using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// An annotation of an object of the application (<c>Annotation</c> on the server, index <c>annotation</c>): its text,
/// its tags (<c>#SOFTWARE</c>…), the object annotated and the context it was written in (window and data, as for a white
/// note). Saved with <c>PUT /annotation</c>.
/// </summary>
public sealed class AnnotationEntry
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("appName")]
    public string AppName { get; set; } = "KAONOMI.Chat";

    /// <summary>The project the document is attached to (the id of a <c>Project</c>, chosen by its name in the header of the window), or null.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Date { get; set; }

    [JsonPropertyName("text")]
    /// <summary>The text of the annotation.</summary>
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    /// <summary>The title of the annotation.</summary>
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    /// <summary>Its tags, #TOPIC in upper case, '-' inside one (the server normalizes them).</summary>
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("events")]
    /// <summary>Its events, @EVENT in upper case, '-' inside one.</summary>
    public List<string> Events { get; set; } = [];

    [JsonPropertyName("signals")]
    /// <summary>Its signals, !SIGNAL in upper case, '-' inside one.</summary>
    public List<string> Signals { get; set; } = [];

    [JsonPropertyName("targetType")]
    /// <summary>What is annotated (<see cref="AnnotationTargetTypes"/>).</summary>
    public string TargetType { get; set; } = string.Empty;

    [JsonPropertyName("targetId")]
    /// <summary>The id of the object annotated (an entity id, the path of a picture or of a sound, a video id…).</summary>
    public string? TargetId { get; set; }

    [JsonPropertyName("targetTitle")]
    /// <summary>The title of the object annotated.</summary>
    public string? TargetTitle { get; set; }

    [JsonPropertyName("excerpt")]
    /// <summary>The part annotated: the text selected, or what the part of the object is.</summary>
    public string? Excerpt { get; set; }

    [JsonPropertyName("context")]
    /// <summary>Window name and the data being worked on, as indented JSON text.</summary>
    public string Context { get; set; } = string.Empty;

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }
}

/// <summary>The kinds of objects annotated (<see cref="AnnotationEntry.TargetType"/>), as on the server.</summary>
public static class AnnotationTargetTypes
{
    public const string Entity = "Entity";
    public const string TextBlock = "TextBlock";
    public const string Image = "Image";
    public const string Video = "Video";
    public const string Audio = "Audio";
    public const string ProjectItem = "ProjectItem";
    public const string Idea = "Idea";
    /// <summary>An element, a flow or the whole of a diagram of /bpmnai or /infra (id: file#element).</summary>
    public const string DiagramElement = "DiagramElement";
    /// <summary>A topic, or the whole, of a mind map of /mindmap (id: file#topic).</summary>
    public const string MindmapTopic = "MindmapTopic";
}

/// <summary>
/// The object a right click asks to annotate: its kind, id, title, the part of it annotated (the text selected) and,
/// for an entity of the ontology, its entity type (offered as a tag).
/// </summary>
public sealed record AnnotationTarget(string Type, string? Id, string? Title, string? Excerpt = null, string? EntityType = null)
{
    /// <summary>A text selected (in an editor, a message, a description…).</summary>
    public static AnnotationTarget Text(string selection, string? title, string? id = null) =>
        new(AnnotationTargetTypes.TextBlock, id, title, selection);
}
