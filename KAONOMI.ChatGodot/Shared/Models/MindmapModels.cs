using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>A node of a mind map: its text, its parent (null: the root, the central topic), its place, the colour of its branch.</summary>
public sealed class MindmapNode
{
    public required string Id { get; init; }
    public string Text { get; set; } = string.Empty;
    /// <summary>The id of its parent; null for the root.</summary>
    public string? ParentId { get; set; }
    /// <summary>The centre of the node, in the units of the map.</summary>
    public float X { get; set; }
    public float Y { get; set; }
    /// <summary>The colour of the branch (#rrggbb), set on the children of the root and inherited below; null: the colour of the parent.</summary>
    public string? Color { get; set; }
    /// <summary>A note on the node (shown in the properties, kept in the file).</summary>
    public string Note { get; set; } = string.Empty;

    public MindmapNode Clone() => new() { Id = Id, Text = Text, ParentId = ParentId, X = X, Y = Y, Color = Color, Note = Note };
}

/// <summary>A mind map: a tree of nodes around a root (the central topic, whose text is the title of the map).</summary>
public sealed class MindmapDiagram
{
    /// <summary>The colours of the branches, given in turn to the children of the root.</summary>
    public static readonly string[] Palette = ["#4F8EF7", "#E8A23A", "#3FBF8F", "#E0609A", "#9B7CF0", "#3BB6CF", "#E35A4F", "#8BC34A"];

    public List<MindmapNode> Nodes { get; } = [];

    public MindmapNode? Root => Nodes.FirstOrDefault(n => n.ParentId is null);

    /// <summary>The text of the root: the title of the map.</summary>
    public string Title => Root?.Text ?? string.Empty;

    public MindmapNode? Node(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public MindmapNode? Parent(MindmapNode node) => node.ParentId is { } id ? Node(id) : null;

    /// <summary>The children of a node, in their order in the map.</summary>
    public List<MindmapNode> Children(string id) => Nodes.Where(n => n.ParentId == id).ToList();

    /// <summary>The node and every node under it.</summary>
    public IEnumerable<MindmapNode> Subtree(MindmapNode node)
    {
        yield return node;
        foreach (var child in Children(node.Id))
            foreach (var below in Subtree(child))
                yield return below;
    }

    /// <summary>The depth of the node: 0 for the root, 1 for its children…</summary>
    public int Depth(MindmapNode node)
    {
        var depth = 0;
        for (var parent = Parent(node); parent is not null && depth < 1000; parent = Parent(parent))
            depth++;
        return depth;
    }

    /// <summary>The colour of the branch of the node: its own, else the one of its nearest ancestor that has one; null for the root.</summary>
    public string? BranchColor(MindmapNode node)
    {
        for (var current = node; current is not null; current = Parent(current))
            if (current.Color is { Length: > 0 } color)
                return color;
        return null;
    }

    /// <summary>An id no node has: <c>ID_1</c>, <c>ID_2</c>…</summary>
    public string NewId()
    {
        var taken = Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var id = $"ID_{n}";
            if (!taken.Contains(id))
                return id;
        }
    }

    public MindmapDiagram Clone()
    {
        var copy = new MindmapDiagram();
        copy.Nodes.AddRange(Nodes.Select(n => n.Clone()));
        return copy;
    }

    /// <summary>A map with its root alone, named <paramref name="title"/>.</summary>
    public static MindmapDiagram New(string title)
    {
        var map = new MindmapDiagram();
        map.Nodes.Add(new MindmapNode { Id = "ID_1", Text = title, X = 0, Y = 0 });
        return map;
    }

    /// <summary>
    /// Lays the whole tree out again: the root in the middle, its children to its right and to its left in turn, each
    /// subtree given a height proportional to its leaves, the levels <paramref name="levelGap"/> apart.
    /// </summary>
    public void Layout(float levelGap = 220, float leafGap = 44)
    {
        if (Root is not { } root)
            return;
        var children = Children(root.Id);
        var right = children.Where((_, i) => i % 2 == 0).ToList();
        var left = children.Where((_, i) => i % 2 == 1).ToList();
        root.X = 0;
        root.Y = 0;
        LayoutSide(right, 1);
        LayoutSide(left, -1);

        void LayoutSide(List<MindmapNode> branches, int side)
        {
            var total = branches.Sum(b => Leaves(b)) * leafGap;
            var y = -total / 2;
            foreach (var branch in branches)
            {
                var height = Leaves(branch) * leafGap;
                Place(branch, side, 1, y + height / 2, height);
                y += height;
            }
        }

        void Place(MindmapNode node, int side, int depth, float centerY, float height)
        {
            node.X = side * levelGap * depth;
            node.Y = centerY;
            var kids = Children(node.Id);
            if (kids.Count == 0)
                return;
            var y = centerY - height / 2;
            foreach (var kid in kids)
            {
                var kidHeight = Leaves(kid) * leafGap;
                Place(kid, side, depth + 1, y + kidHeight / 2, kidHeight);
                y += kidHeight;
            }
        }

        int Leaves(MindmapNode node)
        {
            var kids = Children(node.Id);
            return kids.Count == 0 ? 1 : kids.Sum(Leaves);
        }
    }
}

/// <summary>A mind map as the Kaonomi server stores it (a document of the <c>mindmap</c> index): its XML (FreeMind), its comment, its version.</summary>
public sealed class MindmapEntry
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>The date of the last version.</summary>
    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    /// <summary>The title: the text of the root.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>The comment written with the map.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>The map, as FreeMind XML.</summary>
    [JsonPropertyName("xml")]
    public string Xml { get; set; } = string.Empty;

    /// <summary>The texts of its nodes (set by the server).</summary>
    [JsonPropertyName("elements")]
    public List<string> Elements { get; set; } = [];

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Created { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>The date of the last version, for the lists.</summary>
    [JsonIgnore]
    public string DateLabel => Date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    /// <summary>The slug of a title, as the server makes it ("map" when nothing is left).</summary>
    public static string Slug(string? text)
    {
        var slug = WikiPageEntry.Slug(text);
        return slug == "page" ? "map" : slug;
    }
}
