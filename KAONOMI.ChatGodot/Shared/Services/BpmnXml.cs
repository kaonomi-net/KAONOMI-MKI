using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The geometry of the shapes of a BPMN diagram, shared by the editor (which draws them) and the XML (which writes the
/// waypoints of the flows): an event is a circle, a gateway a diamond, everything else a rectangle.
/// </summary>
public static class BpmnGeometry
{
    /// <summary>
    /// The point of the border of <paramref name="shape"/> on the line from its centre towards (<paramref name="towardX"/>,
    /// <paramref name="towardY"/>): where a flow leaves it or reaches it.
    /// </summary>
    public static (float X, float Y) Anchor(BpmnShape shape, float towardX, float towardY)
    {
        var cx = shape.CenterX;
        var cy = shape.CenterY;
        var dx = towardX - cx;
        var dy = towardY - cy;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.001f)
            return (cx, cy);
        dx /= length;
        dy /= length;
        var hw = shape.Width / 2;
        var hh = shape.Height / 2;
        if (shape.Kind.IsEvent())
        {
            var r = Math.Min(hw, hh);
            return (cx + dx * r, cy + dy * r);
        }
        if (shape.Kind.IsGateway())
        {
            // The diamond |x|/hw + |y|/hh = 1.
            var t = 1 / (Math.Abs(dx) / hw + Math.Abs(dy) / hh);
            return (cx + dx * t, cy + dy * t);
        }
        var tx = Math.Abs(dx) < 0.0001f ? float.MaxValue : hw / Math.Abs(dx);
        var ty = Math.Abs(dy) < 0.0001f ? float.MaxValue : hh / Math.Abs(dy);
        var s = Math.Min(tx, ty);
        return (cx + dx * s, cy + dy * s);
    }

    /// <summary>True when the point is inside the shape (its circle, its diamond or its rectangle).</summary>
    public static bool Contains(BpmnShape shape, float x, float y)
    {
        var dx = x - shape.CenterX;
        var dy = y - shape.CenterY;
        var hw = shape.Width / 2;
        var hh = shape.Height / 2;
        if (shape.Kind.IsEvent())
        {
            var r = Math.Min(hw, hh);
            return dx * dx + dy * dy <= r * r;
        }
        if (shape.Kind.IsGateway())
            return Math.Abs(dx) / hw + Math.Abs(dy) / hh <= 1;
        return Math.Abs(dx) <= hw && Math.Abs(dy) <= hh;
    }
}

/// <summary>
/// BPMN 2.0 XML: the diagram written as <c>bpmn:definitions</c> with its process (the elements, the sequence flows
/// and the associations) and its <c>bpmndi:BPMNDiagram</c> (the bounds of every shape, the waypoints of every edge),
/// as the other tools (bpmn.io, Camunda…) read it; and read back, the elements it draws taken from any process (the
/// other kinds mapped to the nearest basic one, the pools and lanes left out), laid out from the diagram interchange
/// when there is one, in a grid otherwise.
/// </summary>
public static class BpmnXml
{
    public static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    public static readonly XNamespace BpmnDi = "http://www.omg.org/spec/BPMN/20100524/DI";
    public static readonly XNamespace Dc = "http://www.omg.org/spec/DD/20100524/DC";
    public static readonly XNamespace Di = "http://www.omg.org/spec/DD/20100524/DI";
    /// <summary>The namespace of the attributes of Kaonomi (<c>kaonomi:type</c> on the agents and the stores).</summary>
    public static readonly XNamespace Kaonomi = "http://kaonomi.net/bpmn";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ── Writing ──

    public static string Write(BpmnDiagram diagram)
    {
        var process = new XElement(Bpmn + "process", new XAttribute("id", diagram.Id), new XAttribute("isExecutable", "false"));
        if (!string.IsNullOrWhiteSpace(diagram.Name))
            process.Add(new XAttribute("name", diagram.Name));

        foreach (var shape in diagram.Shapes)
        {
            var element = new XElement(Bpmn + shape.Kind.XmlName(), new XAttribute("id", shape.Id));
            if (shape.Kind == BpmnElementKind.TextAnnotation)
            {
                element.Add(new XAttribute("textFormat", "text/plain"));
                element.Add(new XElement(Bpmn + "text", shape.Name));
            }
            else if (!string.IsNullOrWhiteSpace(shape.Name))
                element.Add(new XAttribute("name", shape.Name));
            if (shape.Kind.EventDefinition() is { } definition)
                element.Add(new XElement(Bpmn + definition, new XAttribute("id", shape.Id + "_def")));
            if (shape.Kind.KaonomiType() is { } kaonomiType)
                element.Add(new XAttribute(Kaonomi + "type", kaonomiType));
            if (shape.Kind.IsStore())
            {
                element.Add(new XAttribute("dataStoreRef", shape.Id + "_store"));
                process.Add(element);
                continue;
            }
            if (shape.Kind == BpmnElementKind.DataObject || shape.Kind.IsService())
            {
                element.Add(new XAttribute("dataObjectRef", shape.Id + "_data"));
                process.Add(element);
                process.Add(new XElement(Bpmn + "dataObject", new XAttribute("id", shape.Id + "_data")));
                continue;
            }
            if (shape.Kind.IsFlowNode())
            {
                foreach (var flow in diagram.Flows.Where(f => f.Kind == BpmnFlowKind.SequenceFlow && f.TargetId == shape.Id))
                    element.Add(new XElement(Bpmn + "incoming", flow.Id));
                foreach (var flow in diagram.Flows.Where(f => f.Kind == BpmnFlowKind.SequenceFlow && f.SourceId == shape.Id))
                    element.Add(new XElement(Bpmn + "outgoing", flow.Id));
            }
            process.Add(element);
        }
        foreach (var flow in diagram.Flows)
        {
            var element = new XElement(Bpmn + (flow.Kind == BpmnFlowKind.SequenceFlow ? "sequenceFlow" : "association"),
                new XAttribute("id", flow.Id), new XAttribute("sourceRef", flow.SourceId), new XAttribute("targetRef", flow.TargetId));
            if (flow.Kind == BpmnFlowKind.SequenceFlow && !string.IsNullOrWhiteSpace(flow.Name))
                element.Add(new XAttribute("name", flow.Name));
            process.Add(element);
        }

        var plane = new XElement(BpmnDi + "BPMNPlane", new XAttribute("id", "BPMNPlane_1"), new XAttribute("bpmnElement", diagram.Id));
        foreach (var shape in diagram.Shapes)
        {
            var di = new XElement(BpmnDi + "BPMNShape", new XAttribute("id", shape.Id + "_di"), new XAttribute("bpmnElement", shape.Id));
            if (shape.Kind == BpmnElementKind.SubProcess)
                di.Add(new XAttribute("isExpanded", "false"));
            di.Add(Bounds(shape.X, shape.Y, shape.Width, shape.Height));
            if ((shape.Kind.IsEvent() || shape.Kind.IsGateway() || shape.Kind.IsArtifact() && shape.Kind != BpmnElementKind.TextAnnotation) && !string.IsNullOrWhiteSpace(shape.Name))
                di.Add(new XElement(BpmnDi + "BPMNLabel", Bounds(shape.CenterX - 45, shape.Y + shape.Height + 5, 90, 14)));
            plane.Add(di);
        }
        foreach (var flow in diagram.Flows)
        {
            var source = diagram.Shape(flow.SourceId);
            var target = diagram.Shape(flow.TargetId);
            if (source is null || target is null)
                continue;
            var edge = new XElement(BpmnDi + "BPMNEdge", new XAttribute("id", flow.Id + "_di"), new XAttribute("bpmnElement", flow.Id));
            var from = BpmnGeometry.Anchor(source, target.CenterX, target.CenterY);
            var to = BpmnGeometry.Anchor(target, source.CenterX, source.CenterY);
            edge.Add(Waypoint(from.X, from.Y));
            edge.Add(Waypoint(to.X, to.Y));
            if (flow.Kind == BpmnFlowKind.SequenceFlow && !string.IsNullOrWhiteSpace(flow.Name))
                edge.Add(new XElement(BpmnDi + "BPMNLabel", Bounds((from.X + to.X) / 2 - 45, (from.Y + to.Y) / 2 - 20, 90, 14)));
            plane.Add(edge);
        }

        var definitions = new XElement(Bpmn + "definitions",
            new XAttribute(XNamespace.Xmlns + "bpmn", Bpmn),
            new XAttribute(XNamespace.Xmlns + "bpmndi", BpmnDi),
            new XAttribute(XNamespace.Xmlns + "dc", Dc),
            new XAttribute(XNamespace.Xmlns + "di", Di),
            new XAttribute(XNamespace.Xmlns + "kaonomi", Kaonomi),
            new XAttribute("id", "Definitions_1"),
            new XAttribute("targetNamespace", "http://kaonomi.net/bpmn"),
            new XAttribute("exporter", "KAONOMI"),
            new XAttribute("exporterVersion", "1.0"),
            process,
            diagram.Shapes.Where(s => s.Kind.IsStore()).Select(s => new XElement(Bpmn + "dataStore", new XAttribute("id", s.Id + "_store"), new XAttribute("name", s.Name))),
            new XElement(BpmnDi + "BPMNDiagram", new XAttribute("id", "BPMNDiagram_1"), plane));

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), definitions);
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using var text = new StringWriter(Invariant);
        using (var writer = XmlWriter.Create(text, settings))
            document.Save(writer);
        return text.ToString();
    }

    private static XElement Bounds(float x, float y, float width, float height) =>
        new(Dc + "Bounds", new XAttribute("x", Number(x)), new XAttribute("y", Number(y)), new XAttribute("width", Number(width)), new XAttribute("height", Number(height)));

    private static XElement Waypoint(float x, float y) => new(Di + "waypoint", new XAttribute("x", Number(x)), new XAttribute("y", Number(y)));

    private static string Number(float value) => Math.Round(value, 1).ToString(Invariant);

    // ── Reading ──

    /// <summary>Reads a diagram from BPMN 2.0 XML. Throws when the text is not BPMN.</summary>
    public static BpmnDiagram Read(string xml)
    {
        var document = XDocument.Parse(xml);
        var definitions = document.Root ?? throw new InvalidDataException("The file is empty");
        if (definitions.Name.LocalName != "definitions")
            throw new InvalidDataException($"Not a BPMN 2.0 file: the root is <{definitions.Name.LocalName}>, not <definitions>");

        var processes = definitions.Elements().Where(e => e.Name.LocalName == "process").ToList();
        if (processes.Count == 0)
            throw new InvalidDataException("The file holds no process");

        var diagram = new BpmnDiagram
        {
            Id = (string?)processes[0].Attribute("id") ?? "Process_1",
            Name = (string?)processes[0].Attribute("name") ?? string.Empty
        };

        // The bounds and waypoints of the diagram interchange, by element.
        var bounds = new Dictionary<string, (float X, float Y, float W, float H)>(StringComparer.Ordinal);
        foreach (var shape in definitions.Descendants().Where(e => e.Name.LocalName == "BPMNShape"))
        {
            var element = (string?)shape.Attribute("bpmnElement");
            var box = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "Bounds");
            if (element is null || box is null)
                continue;
            if (Float(box, "x") is { } x && Float(box, "y") is { } y && Float(box, "width") is { } w && Float(box, "height") is { } h)
                bounds[element] = (x, y, w, h);
        }

        var flows = new List<XElement>();
        foreach (var process in processes)
            Collect(process, diagram, bounds, flows);

        foreach (var element in flows)
        {
            var source = (string?)element.Attribute("sourceRef");
            var target = (string?)element.Attribute("targetRef");
            if (source is null || target is null || diagram.Shape(source) is null || diagram.Shape(target) is null)
                continue;
            var id = (string?)element.Attribute("id") ?? diagram.NewId(element.Name.LocalName == "sequenceFlow" ? "Flow" : "Association");
            if (diagram.Flows.Any(f => f.Id == id))
                continue;
            diagram.Flows.Add(new BpmnFlow
            {
                Id = id,
                SourceId = source,
                TargetId = target,
                Kind = element.Name.LocalName == "sequenceFlow" ? BpmnFlowKind.SequenceFlow : BpmnFlowKind.Association,
                Name = (string?)element.Attribute("name") ?? string.Empty
            });
        }

        LayoutUnplaced(diagram, bounds);
        return diagram;
    }

    /// <summary>The elements of <paramref name="container"/> (a process; the children of a sub-process are left out: it is drawn collapsed).</summary>
    private static void Collect(XElement container, BpmnDiagram diagram, Dictionary<string, (float X, float Y, float W, float H)> bounds, List<XElement> flows)
    {
        foreach (var element in container.Elements())
        {
            var name = element.Name.LocalName;
            if (name is "sequenceFlow" or "association")
            {
                flows.Add(element);
                continue;
            }
            if (name is "laneSet" or "lane")
            {
                // The elements of a lane are those of the process: nothing to add, their refs are read with the process.
                continue;
            }
            if (name is "dataObject" or "dataStore")
                continue; // Drawn by its dataObjectReference / dataStoreReference.
            var definition = element.Elements().Select(e => e.Name.LocalName).FirstOrDefault(n => n.EndsWith("EventDefinition", StringComparison.Ordinal));
            if (BpmnElementKinds.FromXmlName(name, definition, (string?)element.Attribute(Kaonomi + "type")) is not { } kind)
                continue;
            var id = (string?)element.Attribute("id");
            if (id is null || diagram.Shape(id) is not null)
                continue;
            var title = kind == BpmnElementKind.TextAnnotation
                ? element.Elements().FirstOrDefault(e => e.Name.LocalName == "text")?.Value.Trim() ?? string.Empty
                : (string?)element.Attribute("name") ?? string.Empty;
            var (width, height) = kind.DefaultSize();
            var shape = new BpmnShape { Id = id, Kind = kind, Name = title, X = float.NaN, Y = float.NaN, Width = width, Height = height };
            if (bounds.TryGetValue(id, out var box))
            {
                shape.X = box.X;
                shape.Y = box.Y;
                shape.Width = Math.Max(10, box.W);
                shape.Height = Math.Max(10, box.H);
            }
            diagram.Shapes.Add(shape);
        }
    }

    /// <summary>The elements without bounds in the file are laid out in a grid under the others.</summary>
    private static void LayoutUnplaced(BpmnDiagram diagram, Dictionary<string, (float X, float Y, float W, float H)> bounds)
    {
        var unplaced = diagram.Shapes.Where(s => float.IsNaN(s.X) || float.IsNaN(s.Y)).ToList();
        if (unplaced.Count == 0)
            return;
        var placed = diagram.Shapes.Where(s => !float.IsNaN(s.X) && !float.IsNaN(s.Y)).ToList();
        var top = placed.Count == 0 ? 100f : placed.Max(s => s.Y + s.Height) + 80;
        const float columnWidth = 160;
        const float rowHeight = 120;
        const int columns = 6;
        for (var i = 0; i < unplaced.Count; i++)
        {
            var shape = unplaced[i];
            var column = i % columns;
            var row = i / columns;
            shape.X = 100 + column * columnWidth + (columnWidth - shape.Width) / 2;
            shape.Y = top + row * rowHeight + (rowHeight - shape.Height) / 2;
        }
    }

    private static float? Float(XElement element, string attribute) =>
        float.TryParse((string?)element.Attribute(attribute), NumberStyles.Float, Invariant, out var value) ? value : null;
}
