using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KAONOMI.Core.Models;

/// <summary>
/// A page of the wiki of Kaonomi (<c>/wiki</c> in the chat), one document of the <c>wiki</c> index. Its id is the slug of
/// its title, given at its creation and kept when the title changes (the links to the page stay valid). Its content
/// is Markdown in the format of the rich editor of the chat: formatting, pictures (<c>![alt|width](path)</c>) and links;
/// a link to another page is <c>[text](wiki:slug)</c>. The server keeps its version (one more at each save), its date
/// of creation, its summary (the start of its text) and the pages it links to (for the backlinks).
/// Never erased (<see cref="ISoftDeletable"/>); the tags follow <see cref="TagRules"/>.
/// </summary>
public partial class Wiki : EntityBase, ISoftDeletable, ITagged
{
    /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
    public bool isDeleted { get; set; } = false;

    /// <summary>The page, in Markdown.</summary>
    public string content { get; set; } = string.Empty;
    /// <summary>The start of its text, without formatting (set by the server).</summary>
    public string summary { get; set; } = string.Empty;
    /// <summary>Who wrote the last version.</summary>
    public string author { get; set; } = string.Empty;
    /// <summary>When the page was created (<see cref="EntityBase.date"/> is the date of its last version).</summary>
    public DateTime? created { get; set; }
    /// <summary>The version of the page: 1 at its creation, one more at each save.</summary>
    public int version { get; set; }
    /// <summary>The slugs of the pages it links to (<c>wiki:slug</c> in its content; set by the server).</summary>
    public List<string> links { get; set; } = new List<string>();

    /// <summary>
    /// The slug of <paramref title="text"/>: lower case, no accent, words joined by '-', at most 80 characters
    /// ("Le serveur Kaonomi" gives "le-serveur-kaonomi"); "page" when nothing is left.
    /// </summary>
    public static string Slug(string? text)
    {
        var decomposed = (text ?? string.Empty).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        }
        var slug = Dashes().Replace(builder.ToString(), "-").Trim('-');
        if (slug.Length > 80)
            slug = slug[..80].Trim('-');
        return slug.Length == 0 ? "page" : slug;
    }

    /// <summary>
    /// The page made ready to be saved: the title trimmed, the id the slug of the title for a new page (an id given
    /// by the client is made a slug too), the links read from the content, the summary from its text.
    /// </summary>
    public void Normalize()
    {
        title = (title ?? string.Empty).Trim();
        content ??= string.Empty;
        id = string.IsNullOrWhiteSpace(id) || Guid.TryParse(id, out _) ? Slug(title) : Slug(id);
        if (title.Length == 0)
            title = id;
        links = WikiLink().Matches(content).Select(m => m.Groups["slug"].Value.ToLowerInvariant()).Where(s => s != id).Distinct().ToList();
        summary = Summary(content);
    }

    /// <summary>The start of the text of <paramref title="markdown"/> (240 characters), without pictures, links or marks.</summary>
    public static string Summary(string markdown)
    {
        var text = Picture().Replace(markdown ?? string.Empty, " ");
        text = Link().Replace(text, "${text}");
        text = Marks().Replace(text, " ");
        text = Spaces().Replace(text, " ").Trim();
        return text.Length > 240 ? text[..240].TrimEnd() + "…" : text;
    }

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex Dashes();

    [GeneratedRegex(@"\(wiki:(?<slug>[A-Za-z0-9][A-Za-z0-9-]*)\)")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex Picture();

    [GeneratedRegex(@"\[(?<text>[^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"[#*_`>~|]+|\{[^}]*\}")]
    private static partial Regex Marks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
