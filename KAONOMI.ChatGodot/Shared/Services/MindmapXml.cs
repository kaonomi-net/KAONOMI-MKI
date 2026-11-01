using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The mind map as FreeMind XML (<c>.mm</c>, read by FreeMind, Freeplane, XMind…): <c>map</c>, then one <c>node</c>
/// per node (its TEXT, its ID, its children inside it; POSITION left/right on the children of the root; an <c>edge</c>
/// with the COLOR of the branch; the note as <c>richcontent TYPE="NOTE"</c>), with the place of each node in the X and
/// Y attributes of Kaonomi (ignored by the other tools). Read back from any <c>.mm</c> file: a node without a place is
/// laid out by the tree (<see cref="MindmapDiagram.Layout"/>); a rich text (HTML) is kept as its text.
/// </summary>
public static partial class MindmapXml
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ── Writing ──

    public static string Write(MindmapDiagram map)
    {
        var root = map.Root ?? throw new InvalidDataException("The map has no root");
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("map", new XAttribute("version", "1.0.1"),
                new XComment(" To view this file, download free mind mapping software FreeMind from http://freemind.sourceforge.net "),
                Element(map, root, 0)));
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false) };
        using var text = new StringWriter(Invariant);
        using (var writer = XmlWriter.Create(text, settings))
            document.Save(writer);
        return text.ToString();
    }

    private static XElement Element(MindmapDiagram map, MindmapNode node, int depth)
    {
        var element = new XElement("node", new XAttribute("ID", node.Id), new XAttribute("TEXT", node.Text));
        if (depth == 1)
            element.Add(new XAttribute("POSITION", node.X < (map.Root?.X ?? 0) ? "left" : "right"));
        element.Add(new XAttribute("X", Number(node.X)), new XAttribute("Y", Number(node.Y)));
        if (node.Color is { Length: > 0 } color)
            element.Add(new XElement("edge", new XAttribute("COLOR", color)));
        if (node.Note.Length > 0)
            element.Add(new XElement("richcontent", new XAttribute("TYPE", "NOTE"),
                new XElement("html", new XElement("head"), new XElement("body", node.Note.Split('\n').Select(line => new XElement("p", line))))));
        foreach (var child in map.Children(node.Id))
            element.Add(Element(map, child, depth + 1));
        return element;
    }

    private static string Number(float value) => Math.Round(value, 1).ToString(Invariant);

    // ── Reading ──

    /// <summary>Reads a map from FreeMind XML. Throws when the text is not a map.</summary>
    public static MindmapDiagram Read(string xml)
    {
        var document = XDocument.Parse(xml);
        var mapElement = document.Root ?? throw new InvalidDataException("The file is empty");
        if (mapElement.Name.LocalName != "map")
            throw new InvalidDataException($"Not a FreeMind file: the root is <{mapElement.Name.LocalName}>, not <map>");
        var rootElement = mapElement.Elements().FirstOrDefault(e => e.Name.LocalName == "node") ?? throw new InvalidDataException("The map has no node");
        var map = new MindmapDiagram();
        var placed = true;
        Add(rootElement, null, 0);
        if (map.Root is null)
            throw new InvalidDataException("The map has no root");
        if (!placed)
            map.Layout();
        // The children of the root without a colour get one, so that each branch has its own.
        var branches = map.Children(map.Root.Id);
        for (var i = 0; i < branches.Count; i++)
            branches[i].Color ??= MindmapDiagram.Palette[i % MindmapDiagram.Palette.Length];
        return map;

        void Add(XElement element, string? parentId, int depth)
        {
            var id = (string?)element.Attribute("ID");
            if (string.IsNullOrWhiteSpace(id) || map.Node(id) is not null)
                id = map.NewId();
            var text = (string?)element.Attribute("TEXT");
            if (text is null)
            {
                var rich = element.Elements().FirstOrDefault(e => e.Name.LocalName == "richcontent" && ((string?)e.Attribute("TYPE") ?? "NODE") == "NODE");
                text = rich is not null ? PlainText(rich) : string.Empty;
            }
            var noteElement = element.Elements().FirstOrDefault(e => e.Name.LocalName == "richcontent" && (string?)e.Attribute("TYPE") == "NOTE");
            var node = new MindmapNode
            {
                Id = id,
                Text = text,
                ParentId = parentId,
                Note = noteElement is not null ? PlainText(noteElement) : string.Empty,
                Color = (string?)element.Elements().FirstOrDefault(e => e.Name.LocalName == "edge")?.Attribute("COLOR")
            };
            if (Float(element, "X") is { } x && Float(element, "Y") is { } y)
            {
                node.X = x;
                node.Y = y;
            }
            else
                placed = false;
            map.Nodes.Add(node);
            foreach (var child in element.Elements().Where(e => e.Name.LocalName == "node"))
                Add(child, id, depth + 1);
        }
    }

    /// <summary>The text of a rich content: its paragraphs on their lines, the tags removed.</summary>
    private static string PlainText(XElement rich)
    {
        var body = rich.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ?? rich;
        var lines = body.Elements().Where(e => e.Name.LocalName is "p" or "li" or "div" or "h1" or "h2" or "h3").Select(e => Spaces().Replace(e.Value, " ").Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : Spaces().Replace(body.Value, " ").Trim();
    }

    private static float? Float(XElement element, string attribute) =>
        float.TryParse((string?)element.Attribute(attribute), NumberStyles.Float, Invariant, out var value) ? value : null;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
