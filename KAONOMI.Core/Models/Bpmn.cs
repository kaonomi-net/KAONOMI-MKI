using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KAONOMI.Core.Models;

/// <summary>
/// A BPMN 2.0 diagram of the chat (<c>/bpmn</c>), one document of the <c>bpmn</c> index. Its id is the slug of its
/// title (the title of the process), given at its creation and kept when the title changes. It holds the diagram as
/// BPMN 2.0 XML (with its diagram interchange) and a free comment; the server keeps its version (one more at each
/// save), its date of creation, the names of its elements (read from the XML, for the search) and a summary.
/// Never erased (<see cref="ISoftDeletable"/>); the tags follow <see cref="TagRules"/>.
/// </summary>
public partial class Bpmn : EntityBase, ISoftDeletable, ITagged
{
    /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
    public bool isDeleted { get; set; } = false;

    /// <summary>The comment written with the diagram (free text).</summary>
    public string description { get; set; } = string.Empty;
    /// <summary>The diagram, as BPMN 2.0 XML.</summary>
    public string xml { get; set; } = string.Empty;
    /// <summary>The names of the elements and flows of the diagram (read from the XML by the server, for the search).</summary>
    public List<string> elements { get; set; } = new List<string>();
    /// <summary>The start of the comment, then the elements (set by the server).</summary>
    public string summary { get; set; } = string.Empty;
    /// <summary>Who wrote the last version.</summary>
    public string author { get; set; } = string.Empty;
    /// <summary>When the diagram was created (<see cref="EntityBase.date"/> is the date of its last version).</summary>
    public DateTime? created { get; set; }
    /// <summary>The version of the diagram: 1 at its creation, one more at each save.</summary>
    public int version { get; set; }

    /// <summary>The slug of a title, as for the pages of the wiki ("diagram" when nothing is left).</summary>
    public static string Slug(string? text)
    {
        var slug = Wiki.Slug(text);
        return slug == "page" ? "diagram" : slug;
    }

    /// <summary>
    /// The diagram made ready to be saved: the title trimmed, the id the slug of the title for a new diagram (an id
    /// given by the client is made a slug too), the elements read from the XML, the summary from the comment and them.
    /// </summary>
    public void Normalize()
    {
        title = (title ?? string.Empty).Trim();
        description = (description ?? string.Empty).Trim();
        xml ??= string.Empty;
        id = string.IsNullOrWhiteSpace(id) || Guid.TryParse(id, out _) ? Slug(title) : Slug(id);
        if (title.Length == 0)
            title = id;
        elements = ElementNames(xml);
        var text = description.Length > 0 ? description : string.Join(", ", elements);
        text = Spaces().Replace(text, " ").Trim();
        summary = text.Length > 240 ? text[..240].TrimEnd() + "…" : text;
    }

    /// <summary>The names of the elements of the BPMN XML (<c>title="…"</c> of every element but the process and the definitions), without duplicates.</summary>
    public static List<string> ElementNames(string? xml)
    {
        var names = new List<string>();
        foreach (Match match in NamedElement().Matches(xml ?? string.Empty))
        {
            var tag = match.Groups["tag"].Value;
            if (tag is "definitions" or "process" or "collaboration" or "participant" or "lane" or "laneSet")
                continue;
            var name = System.Net.WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();
            if (name.Length > 0 && !names.Contains(name))
                names.Add(name);
        }
        return names;
    }

    [GeneratedRegex(@"<(?:[A-Za-z0-9_]+:)?(?<tag>[A-Za-z]+)\b[^>]*?\sname=""(?<name>[^""]*)""", RegexOptions.Singleline)]
    private static partial Regex NamedElement();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
