using System.Collections.Generic;
namespace KAONOMI.Core.Models;

/// <summary>
/// A link drawn by the user between windows of the chat (a red string from the link icon of one window through the
/// link icons of one or more others), stored in the <c>relation</c> index with all its versions: each save of the link
/// adds a version to <see cref="versions"/> (the last one is the current one) instead of replacing it.
/// <see cref="category"/> is "interdata" for a link between the data of windows.
/// </summary>
public class Relation : EntityBase, ISoftDeletable, ITagged
{
    /// <summary>The tags: #TOPIC, !ALERT, ?QUESTION, &amp;LINK, in upper case, no space (<see cref="TagRules"/>).</summary>
    public List<string> Tags { get; set; } = new List<string>();

    /// <summary>Kind of relation: "interdata" for a link between the data of windows.</summary>
    public string category { get; set; } = "interdata";

    /// <summary>The versions of the relation, oldest first: the last one is the current one.</summary>
    public List<RelationVersion> versions { get; set; } = [];

    /// <summary>Soft delete: the relation is kept but left out of the searches.</summary>
    public bool isDeleted { get; set; }
}

/// <summary>
/// A version of a <see cref="Relation"/>: its number (1, 2, 3…), when and by whom it was saved (<c>date</c>,
/// <c>userName</c> of <see cref="EntityBase"/>), its nature, title, description and probability, the window it starts
/// from (<see cref="source"/>) and the windows it goes to (<see cref="destinations"/>, in the order of the string).
/// </summary>
public class RelationVersion : EntityBase
{
    /// <summary>Number of the version, from 1.</summary>
    public int version { get; set; }

    /// <summary>Nature of the link (e.g. "cause of", "same person", "illustrates", "contradicts").</summary>
    public string? nature { get; set; }

    /// <summary>Title of the link.</summary>
    public string? title { get; set; }

    /// <summary>Description of the link.</summary>
    public string? description { get; set; }

    /// <summary>
    /// How probable the link is, from -100 (absolutely impossible) to 100 (absolutely certain); 0: still to evaluate.
    /// </summary>
    public int probability { get; set; }

    /// <summary>Optional date from which the link holds (null: not given).</summary>
    public DateTime? dateFrom { get; set; }

    /// <summary>Optional date until which the link holds (null: not given).</summary>
    public DateTime? dateTo { get; set; }

    /// <summary>The window the string starts from.</summary>
    public RelationEnd source { get; set; } = new();

    /// <summary>The windows the string goes to, in its order (one or more).</summary>
    public List<RelationEnd> destinations { get; set; } = [];
}

/// <summary>
/// One end of a <see cref="RelationVersion"/>: the window, the id of the object shown in it (when there is one), what
/// the user wrote about it, and the data of the window when the link was made (JSON).
/// </summary>
public class RelationEnd : EntityBase
{
    /// <summary>Name of the window (Ideas, YouTube, Image analysis, Calculator…).</summary>
    public string? windowName { get; set; }

    /// <summary>Id of the object shown in the window (an idea, a document, an image analysis…), when there is one.</summary>
    public string? objectId { get; set; }

    /// <summary>
    /// Title of the object (for an entity of the ontology its name), shown in the relation window and in the graph of
    /// <c>/relation</c>; updated by Refresh in the relation window.
    /// </summary>
    public string? title { get; set; }

    /// <summary>What the user wrote about this end of the link.</summary>
    public string? information { get; set; }

    /// <summary>The data of the window when the link was made, as JSON.</summary>
    public string? context { get; set; }
}
