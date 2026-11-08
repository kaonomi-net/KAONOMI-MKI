namespace KAONOMI.Core.Models;

/// <summary>
/// A note the user attaches to any object of the application: an entity of the ontology, a block of text, an image,
/// an element of a project, an idea, a video, an audio file… (<see cref="targetType"/> and <see cref="targetId"/> say which one). It holds the
/// text of the annotation, its tags (<c>#SOFTWARE</c>, <c>#IDEA</c>…) and, as a white note does, the context it was
/// written in: the window and the data being worked on, as JSON text. The user, machine, application and date are the
/// ones of <see cref="EntityBase"/> (the date is set when it is first saved, and kept by the updates without one).
/// </summary>
public class Annotation : EntityBase
{
    /// <summary>The text of the annotation.</summary>
    public string text { get; set; } = string.Empty;

    /// <summary>
    /// What is annotated: one of <see cref="AnnotationTargetTypes"/> (Entity, TextBlock, Image, ProjectItem, Idea…), or
    /// the title of another kind of object.
    /// </summary>
    public string targetType { get; set; } = string.Empty;

    /// <summary>The id of the object annotated (the id of the entity, of the image analysis, of the idea…).</summary>
    public string? targetId { get; set; }

    /// <summary>The title of the object annotated when it was annotated (its title, the title of the picture…).</summary>
    public string? targetTitle { get; set; }

    /// <summary>For a part of an object (a block of text, a region of an image): the text or the description of that part.</summary>
    public string? excerpt { get; set; }

    /// <summary>
    /// The context the annotation was written in, as for a white note: the window and the data being worked on, as
    /// indented JSON text (<c>{ "window": …, "data": … }</c>).
    /// </summary>
    public string context { get; set; } = string.Empty;

    /// <summary>Soft delete: the annotation is kept but left out of the searches.</summary>
    public bool isDeleted { get; set; }

    /// <summary>
    /// A tag as it is stored: a <c>#</c> then the word in upper case, the spaces made underscores
    /// (<c>"software"</c> → <c>#SOFTWARE</c>, <c>"#work of art"</c> → <c>#WORK_OF_ART</c>); null for an empty tag.
    /// </summary>
    public static string? NormalizeTag(string? tag)
    {
        return TagRules.Normalize(tag, TagRules.TagPrefix);
    }

    /// <summary>Adds <paramref title="tag"/> (normalized) unless it is empty or there already.</summary>
    public void AddTag(string? tag)
    {
        if (NormalizeTag(tag) is { } normalized && !Tags.Contains(normalized, StringComparer.Ordinal))
        {
            Tags.Add(normalized);
        }
    }
}

/// <summary>The kinds of objects an <see cref="Annotation"/> is usually attached to (<see cref="Annotation.targetType"/>).</summary>
public static class AnnotationTargetTypes
{
    /// <summary>An entity of the ontology (<see cref="Ontology"/>).</summary>
    public const string Entity = "Entity";

    /// <summary>A block of text (of a document, a tape, a journal entry…): <see cref="Annotation.excerpt"/> holds it.</summary>
    public const string TextBlock = "TextBlock";

    /// <summary>An image (an <see cref="ImageAnalysis"/>, or a region of it described in <see cref="Annotation.excerpt"/>).</summary>
    public const string Image = "Image";

    /// <summary>A video (a YouTube video, a video file).</summary>
    public const string Video = "Video";

    /// <summary>An audio file (a track of the music player…).</summary>
    public const string Audio = "Audio";

    /// <summary>An element of a project (a task, a document of the project…).</summary>
    public const string ProjectItem = "ProjectItem";

    /// <summary>An idea (<see cref="Idea"/>).</summary>
    public const string Idea = "Idea";

    /// <summary>An element, a flow or the whole of a BPMN or infrastructure diagram (<c>targetId</c>: file#element).</summary>
    public const string DiagramElement = "DiagramElement";

    /// <summary>A topic, or the whole, of a mind map (<c>targetId</c>: file#topic).</summary>
    public const string MindmapTopic = "MindmapTopic";
}
