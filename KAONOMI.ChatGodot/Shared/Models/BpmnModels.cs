using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>The basic elements of BPMN 2.0 the editor of <c>/bpmnai</c> (AIMN) draws.</summary>
public enum BpmnElementKind
{
    StartEvent,
    MessageStartEvent,
    TimerStartEvent,
    EventCollector,
    EndEvent,
    Task,
    UserTask,
    AgentTask,
    VirtualAgentTask,
    SubProcess,
    ExclusiveGateway,
    ParallelGateway,
    InclusiveGateway,
    DataObject,
    SearchEngine,
    Graphbase,
    Database,
    VectorDatabase,
    Rag,
    Llm,
    Model,
    RestApi,
    Mcp,
    TextAnnotation,

    // ── The elements of the infrastructure diagrams (/infra) ──
    LlmCloud,
    LlmGateway,
    DgxSpark,
    JetsonNano,
    Pc,
    MacStudio,
    MacBookPro,
    MacMini,
    Server,
    RackServer,
    RaspberryPi,
    Arduino,
    RobotPepper,
    Tablet,
    Smartphone,
    Camera,
    WifiCamera,
    Microphone,
    Sensor
}

/// <summary>What a diagram of the editor draws: an AI process in BPMN (/bpmnai), or an infrastructure (/infra).</summary>
public enum BpmnNotation
{
    Process,
    Infrastructure
}

/// <summary>The kind of a connection: a sequence flow between flow nodes, an association to a data object or an annotation.</summary>
public enum BpmnFlowKind
{
    SequenceFlow,
    Association
}

/// <summary>What the editor shows of each kind of element: its family, its name, the glyph of its palette button.</summary>
public static class BpmnElementKinds
{
    public static readonly BpmnElementKind[] All = Enum.GetValues<BpmnElementKind>();

    /// <summary>The kinds of the palette of a notation: the BPMN elements for a process, the machines and devices (and the text annotation) for an infrastructure.</summary>
    public static BpmnElementKind[] Of(BpmnNotation notation) => notation == BpmnNotation.Infrastructure
        ? All.Where(k => k.IsInfra()).Append(BpmnElementKind.TextAnnotation).ToArray()
        : All.Where(k => !k.IsInfra()).ToArray();

    /// <summary>An element of the infrastructure diagrams: a machine, a device or a service of an installation.</summary>
    public static bool IsInfra(this BpmnElementKind kind) => kind >= BpmnElementKind.LlmCloud;

    /// <summary>The picture of an element of an infrastructure, in its card and in the palette.</summary>
    public static string Icon(this BpmnElementKind kind) => kind switch
    {
        BpmnElementKind.LlmCloud => "☁",
        BpmnElementKind.LlmGateway => "⛩",
        BpmnElementKind.DgxSpark => "⚡",
        BpmnElementKind.JetsonNano => "🟩",
        BpmnElementKind.Pc => "🖥",
        BpmnElementKind.MacStudio => "🍎",
        BpmnElementKind.MacBookPro => "💻",
        BpmnElementKind.MacMini => "🍏",
        BpmnElementKind.Server => "🗃",
        BpmnElementKind.RackServer => "🗄",
        BpmnElementKind.RaspberryPi => "🍓",
        BpmnElementKind.Arduino => "♾",
        BpmnElementKind.RobotPepper => "🤖",
        BpmnElementKind.Tablet => "📱",
        BpmnElementKind.Smartphone => "📱",
        BpmnElementKind.Camera => "📷",
        BpmnElementKind.WifiCamera => "📹",
        BpmnElementKind.Microphone => "🎤",
        BpmnElementKind.Sensor => "🌡",
        _ => "▢"
    };

    /// <summary>The short word written in the card of an element of an infrastructure, under its picture (two that share a picture differ by it).</summary>
    public static string Badge(this BpmnElementKind kind) => kind switch
    {
        BpmnElementKind.LlmCloud => "LLM",
        BpmnElementKind.LlmGateway => "GATEWAY",
        BpmnElementKind.DgxSpark => "DGX",
        BpmnElementKind.JetsonNano => "JETSON",
        BpmnElementKind.Pc => "PC",
        BpmnElementKind.MacStudio => "STUDIO",
        BpmnElementKind.MacBookPro => "MBP",
        BpmnElementKind.MacMini => "MINI",
        BpmnElementKind.Server => "SERVER",
        BpmnElementKind.RackServer => "RACK",
        BpmnElementKind.RaspberryPi => "PI",
        BpmnElementKind.Arduino => "ARDUINO",
        BpmnElementKind.RobotPepper => "PEPPER",
        BpmnElementKind.Tablet => "TABLET",
        BpmnElementKind.Smartphone => "PHONE",
        BpmnElementKind.Camera => "CAM",
        BpmnElementKind.WifiCamera => "WI-FI",
        BpmnElementKind.Microphone => "MIC",
        BpmnElementKind.Sensor => "SENSOR",
        _ => string.Empty
    };

    /// <summary>The value of <c>kaonomi:type</c> of an element of an infrastructure (<c>infra.dgxSpark</c>…), written on a task.</summary>
    private static string InfraType(BpmnElementKind kind) => "infra." + char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];

    /// <summary>The element of an infrastructure of a <c>kaonomi:type</c> (<c>infra.dgxSpark</c>…), null for another type.</summary>
    private static BpmnElementKind? FromInfraType(string? type) =>
        type is { Length: > 6 } && type.StartsWith("infra.", StringComparison.Ordinal)
            && Enum.TryParse<BpmnElementKind>(type[6..], ignoreCase: true, out var kind) && kind.IsInfra() ? kind : null;

    /// <summary>The name shown for the kind (and used in the palette).</summary>
    public static string Title(this BpmnElementKind kind) => kind switch
    {
        BpmnElementKind.StartEvent => "Start event",
        BpmnElementKind.MessageStartEvent => "Message start event",
        BpmnElementKind.TimerStartEvent => "Timer start event",
        BpmnElementKind.EventCollector => "EventCollector",
        BpmnElementKind.EndEvent => "End event",
        BpmnElementKind.Task => "Task",
        BpmnElementKind.UserTask => "User task",
        BpmnElementKind.AgentTask => "Agent",
        BpmnElementKind.VirtualAgentTask => "Virtual Agent",
        BpmnElementKind.SubProcess => "Sub-process",
        BpmnElementKind.ExclusiveGateway => "Exclusive gateway (XOR)",
        BpmnElementKind.ParallelGateway => "Parallel gateway (AND)",
        BpmnElementKind.InclusiveGateway => "Inclusive gateway (OR)",
        BpmnElementKind.DataObject => "Data object",
        BpmnElementKind.SearchEngine => "SearchEngine",
        BpmnElementKind.Graphbase => "Graphbase",
        BpmnElementKind.Database => "Database",
        BpmnElementKind.VectorDatabase => "Vector Database",
        BpmnElementKind.Rag => "RAG",
        BpmnElementKind.Llm => "LLM",
        BpmnElementKind.Model => "MODEL",
        BpmnElementKind.RestApi => "REST API",
        BpmnElementKind.Mcp => "MCP",
        BpmnElementKind.TextAnnotation => "Text annotation",
        BpmnElementKind.LlmCloud => "LLM on the cloud",
        BpmnElementKind.LlmGateway => "LLM Gateway",
        BpmnElementKind.DgxSpark => "DGX Spark",
        BpmnElementKind.JetsonNano => "NVIDIA Jetson Nano",
        BpmnElementKind.Pc => "PC",
        BpmnElementKind.MacStudio => "Mac Studio",
        BpmnElementKind.MacBookPro => "MacBook Pro",
        BpmnElementKind.MacMini => "Mac mini",
        BpmnElementKind.Server => "Server",
        BpmnElementKind.RackServer => "Rack server",
        BpmnElementKind.RaspberryPi => "Raspberry Pi",
        BpmnElementKind.Arduino => "Arduino",
        BpmnElementKind.RobotPepper => "Pepper robot",
        BpmnElementKind.Tablet => "Tablet",
        BpmnElementKind.Smartphone => "Smartphone",
        BpmnElementKind.Camera => "Camera",
        BpmnElementKind.WifiCamera => "Wi-Fi camera",
        BpmnElementKind.Microphone => "Microphone",
        BpmnElementKind.Sensor => "Sensor",
        _ => kind.ToString()
    };

    /// <summary>The group of the palette the kind belongs to.</summary>
    public static string Group(this BpmnElementKind kind) => kind switch
    {
        BpmnElementKind.StartEvent or BpmnElementKind.MessageStartEvent or BpmnElementKind.TimerStartEvent or BpmnElementKind.EventCollector or BpmnElementKind.EndEvent => "Events",
        BpmnElementKind.Task or BpmnElementKind.UserTask or BpmnElementKind.AgentTask or BpmnElementKind.VirtualAgentTask or BpmnElementKind.SubProcess => "Activities",
        BpmnElementKind.ExclusiveGateway or BpmnElementKind.ParallelGateway or BpmnElementKind.InclusiveGateway => "Gateways",
        BpmnElementKind.LlmCloud or BpmnElementKind.LlmGateway or BpmnElementKind.DgxSpark or BpmnElementKind.JetsonNano => "AI",
        BpmnElementKind.Pc or BpmnElementKind.MacStudio or BpmnElementKind.MacBookPro or BpmnElementKind.MacMini or BpmnElementKind.Server or BpmnElementKind.RackServer => "Computers",
        BpmnElementKind.RaspberryPi or BpmnElementKind.Arduino or BpmnElementKind.RobotPepper => "Boards and robots",
        BpmnElementKind.Tablet or BpmnElementKind.Smartphone => "Mobile",
        BpmnElementKind.Camera or BpmnElementKind.WifiCamera or BpmnElementKind.Microphone or BpmnElementKind.Sensor => "Capture",
        _ => "Artifacts"
    };

    /// <summary>The size an element of the kind is created with (the sizes of the BPMN 2.0 notation).</summary>
    public static (float Width, float Height) DefaultSize(this BpmnElementKind kind) => kind switch
    {
        BpmnElementKind.StartEvent or BpmnElementKind.MessageStartEvent or BpmnElementKind.TimerStartEvent or BpmnElementKind.EventCollector or BpmnElementKind.EndEvent => (36, 36),
        BpmnElementKind.Task or BpmnElementKind.UserTask or BpmnElementKind.AgentTask or BpmnElementKind.VirtualAgentTask => (100, 80),
        BpmnElementKind.SubProcess => (100, 80),
        BpmnElementKind.ExclusiveGateway or BpmnElementKind.ParallelGateway or BpmnElementKind.InclusiveGateway => (50, 50),
        BpmnElementKind.DataObject => (36, 50),
        BpmnElementKind.SearchEngine or BpmnElementKind.Graphbase or BpmnElementKind.Database or BpmnElementKind.VectorDatabase or BpmnElementKind.Rag or BpmnElementKind.Llm or BpmnElementKind.Model or BpmnElementKind.RestApi or BpmnElementKind.Mcp => (50, 50),
        BpmnElementKind.TextAnnotation => (120, 40),
        _ when kind.IsInfra() => (80, 80),
        _ => (100, 80)
    };

    public static bool IsEvent(this BpmnElementKind kind) => kind.IsStartEvent() || kind is BpmnElementKind.EndEvent;
    /// <summary>A start event of any trigger (none, message, timer): no incoming sequence flow.</summary>
    public static bool IsStartEvent(this BpmnElementKind kind) => kind is BpmnElementKind.StartEvent or BpmnElementKind.MessageStartEvent or BpmnElementKind.TimerStartEvent or BpmnElementKind.EventCollector;
    /// <summary>The event definition of the kind in BPMN 2.0 XML (<c>bpmn:messageEventDefinition</c>…), null when it has none.</summary>
    public static string? EventDefinition(this BpmnElementKind kind) => kind switch
    {
        BpmnElementKind.MessageStartEvent => "messageEventDefinition",
        BpmnElementKind.TimerStartEvent => "timerEventDefinition",
        _ => null
    };
    public static bool IsActivity(this BpmnElementKind kind) => kind is BpmnElementKind.Task or BpmnElementKind.UserTask or BpmnElementKind.AgentTask or BpmnElementKind.VirtualAgentTask or BpmnElementKind.SubProcess;
    public static bool IsGateway(this BpmnElementKind kind) => kind is BpmnElementKind.ExclusiveGateway or BpmnElementKind.ParallelGateway or BpmnElementKind.InclusiveGateway;
    /// <summary>A data object or an annotation: joined to the flow nodes by associations, not by sequence flows.</summary>
    public static bool IsArtifact(this BpmnElementKind kind) => kind is BpmnElementKind.DataObject or BpmnElementKind.TextAnnotation || kind.IsStore() || kind.IsService();
    /// <summary>A store of Kaonomi (the search engine, the graph base): a data store of BPMN, drawn with its own logo.</summary>
    public static bool IsStore(this BpmnElementKind kind) => kind is BpmnElementKind.SearchEngine or BpmnElementKind.Graphbase or BpmnElementKind.Database or BpmnElementKind.VectorDatabase;
    /// <summary>A service of Kaonomi (RAG, LLM, MODEL, REST API, MCP): a data object of BPMN, drawn with its own logo.</summary>
    public static bool IsService(this BpmnElementKind kind) => kind is BpmnElementKind.Rag or BpmnElementKind.Llm or BpmnElementKind.Model or BpmnElementKind.RestApi or BpmnElementKind.Mcp;

    /// <summary>
    /// The value of the <c>kaonomi:type</c> attribute of the kinds of Kaonomi (an agent, a virtual agent, the search
    /// engine, the graph base), written on their BPMN element (a service task, a data store); null for the kinds of BPMN.
    /// </summary>
    public static string? KaonomiType(this BpmnElementKind kind) => kind switch
    {
        BpmnElementKind.EventCollector => "eventCollector",
        BpmnElementKind.AgentTask => "agent",
        BpmnElementKind.VirtualAgentTask => "virtualAgent",
        BpmnElementKind.SearchEngine => "searchEngine",
        BpmnElementKind.Graphbase => "graphbase",
        BpmnElementKind.Database => "database",
        BpmnElementKind.VectorDatabase => "vectorDatabase",
        BpmnElementKind.Rag => "rag",
        BpmnElementKind.Llm => "llm",
        BpmnElementKind.Model => "model",
        BpmnElementKind.RestApi => "restApi",
        BpmnElementKind.Mcp => "mcp",
        _ when kind.IsInfra() => InfraType(kind),
        _ => null
    };
    /// <summary>An event, an activity or a gateway: what sequence flows join.</summary>
    public static bool IsFlowNode(this BpmnElementKind kind) => !kind.IsArtifact();

    /// <summary>The name of the element in BPMN 2.0 XML (<c>bpmn:startEvent</c>…).</summary>
    public static string XmlName(this BpmnElementKind kind) => kind switch
    {
        BpmnElementKind.StartEvent or BpmnElementKind.MessageStartEvent or BpmnElementKind.TimerStartEvent or BpmnElementKind.EventCollector => "startEvent",
        BpmnElementKind.EndEvent => "endEvent",
        BpmnElementKind.Task => "task",
        BpmnElementKind.UserTask => "userTask",
        BpmnElementKind.AgentTask or BpmnElementKind.VirtualAgentTask => "serviceTask",
        BpmnElementKind.SubProcess => "subProcess",
        BpmnElementKind.ExclusiveGateway => "exclusiveGateway",
        BpmnElementKind.ParallelGateway => "parallelGateway",
        BpmnElementKind.InclusiveGateway => "inclusiveGateway",
        BpmnElementKind.DataObject => "dataObjectReference",
        BpmnElementKind.SearchEngine or BpmnElementKind.Graphbase or BpmnElementKind.Database or BpmnElementKind.VectorDatabase => "dataStoreReference",
        BpmnElementKind.Rag or BpmnElementKind.Llm or BpmnElementKind.Model or BpmnElementKind.RestApi or BpmnElementKind.Mcp => "dataObjectReference",
        BpmnElementKind.TextAnnotation => "textAnnotation",
        _ => "task"
    };

    /// <summary>
    /// The kind of an element read from BPMN 2.0 XML (the kinds not drawn fall back to the nearest one), null when it
    /// is not an element; <paramref name="eventDefinition"/> is the name of its event definition child, when it has one,
    /// <paramref name="kaonomiType"/> its <c>kaonomi:type</c> attribute (an agent, a virtual agent, the search engine, the graph base).
    /// </summary>
    public static BpmnElementKind? FromXmlName(string name, string? eventDefinition = null, string? kaonomiType = null) => name switch
    {
        // An element of an infrastructure: a task with its kaonomi:type (infra.dgxSpark…).
        "task" when FromInfraType(kaonomiType) is { } infra => infra,
        "startEvent" when kaonomiType == "eventCollector" => BpmnElementKind.EventCollector,
        "serviceTask" when kaonomiType == "agent" => BpmnElementKind.AgentTask,
        "serviceTask" when kaonomiType == "virtualAgent" => BpmnElementKind.VirtualAgentTask,
        "dataStoreReference" when kaonomiType == "searchEngine" => BpmnElementKind.SearchEngine,
        "dataStoreReference" when kaonomiType == "graphbase" => BpmnElementKind.Graphbase,
        "dataStoreReference" when kaonomiType == "database" => BpmnElementKind.Database,
        "dataStoreReference" when kaonomiType == "vectorDatabase" => BpmnElementKind.VectorDatabase,
        "dataObjectReference" when kaonomiType == "rag" => BpmnElementKind.Rag,
        "dataObjectReference" when kaonomiType == "llm" => BpmnElementKind.Llm,
        "dataObjectReference" when kaonomiType == "model" => BpmnElementKind.Model,
        "dataObjectReference" when kaonomiType == "restApi" => BpmnElementKind.RestApi,
        "dataObjectReference" when kaonomiType == "mcp" => BpmnElementKind.Mcp,
        "startEvent" when eventDefinition == "messageEventDefinition" => BpmnElementKind.MessageStartEvent,
        "startEvent" when eventDefinition == "timerEventDefinition" => BpmnElementKind.TimerStartEvent,
        "startEvent" => BpmnElementKind.StartEvent,
        // The intermediate events are not drawn: a task keeps their place and their name.
        "intermediateThrowEvent" or "intermediateCatchEvent" or "boundaryEvent" => BpmnElementKind.Task,
        "endEvent" => BpmnElementKind.EndEvent,
        "task" or "serviceTask" or "scriptTask" or "manualTask" or "sendTask" or "receiveTask" or "businessRuleTask" or "callActivity" => BpmnElementKind.Task,
        "userTask" => BpmnElementKind.UserTask,
        "subProcess" or "adHocSubProcess" or "transaction" => BpmnElementKind.SubProcess,
        "exclusiveGateway" or "eventBasedGateway" or "complexGateway" => BpmnElementKind.ExclusiveGateway,
        "parallelGateway" => BpmnElementKind.ParallelGateway,
        "inclusiveGateway" => BpmnElementKind.InclusiveGateway,
        "dataObjectReference" or "dataStoreReference" or "dataObject" => BpmnElementKind.DataObject,
        "textAnnotation" => BpmnElementKind.TextAnnotation,
        _ => null
    };
}

/// <summary>An element of the diagram: its kind, its name, and its place (the bounds of its shape, in the units of the diagram).</summary>
public sealed class BpmnShape
{
    public required string Id { get; init; }
    public required BpmnElementKind Kind { get; init; }
    public string Name { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    public float CenterX => X + Width / 2;
    public float CenterY => Y + Height / 2;

    public BpmnShape Clone() => new() { Id = Id, Kind = Kind, Name = Name, X = X, Y = Y, Width = Width, Height = Height };
}

/// <summary>A connection between two elements: a sequence flow (an arrow), or an association (a dotted line) to an artifact.</summary>
public sealed class BpmnFlow
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public BpmnFlowKind Kind { get; init; } = BpmnFlowKind.SequenceFlow;
    /// <summary>The name of a sequence flow (its condition, shown along it).</summary>
    public string Name { get; set; } = string.Empty;

    public BpmnFlow Clone() => new() { Id = Id, SourceId = SourceId, TargetId = TargetId, Kind = Kind, Name = Name };
}

/// <summary>A BPMN 2.0 diagram: one process, its elements and the flows between them.</summary>
public sealed class BpmnDiagram
{
    public string Id { get; set; } = "Process_1";
    public string Name { get; set; } = "Process";
    public List<BpmnShape> Shapes { get; } = [];
    public List<BpmnFlow> Flows { get; } = [];

    public BpmnShape? Shape(string id) => Shapes.FirstOrDefault(s => s.Id == id);

    /// <summary>The flows joined to the element (from it or to it).</summary>
    public IEnumerable<BpmnFlow> FlowsOf(string shapeId) => Flows.Where(f => f.SourceId == shapeId || f.TargetId == shapeId);

    /// <summary>An id no element nor flow has: <c>StartEvent_1</c>, <c>Task_2</c>…</summary>
    public string NewId(string prefix)
    {
        var taken = Shapes.Select(s => s.Id).Concat(Flows.Select(f => f.Id)).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var id = $"{prefix}_{n}";
            if (!taken.Contains(id))
                return id;
        }
    }

    public BpmnDiagram Clone()
    {
        var copy = new BpmnDiagram { Id = Id, Name = Name };
        copy.Shapes.AddRange(Shapes.Select(s => s.Clone()));
        copy.Flows.AddRange(Flows.Select(f => f.Clone()));
        return copy;
    }
}

/// <summary>A BPMN diagram as the Kaonomi server stores it (a document of the <c>bpmn</c> index): its XML, its comment, its version.</summary>
public sealed class BpmnEntry
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>The project the document is attached to (the id of a <c>Project</c>, chosen by its name in the header of the window), or null.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

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

    /// <summary>The title: the name of the process.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>The comment written with the diagram.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>The diagram, as BPMN 2.0 XML.</summary>
    [JsonPropertyName("xml")]
    public string Xml { get; set; } = string.Empty;

    /// <summary>The names of its elements (set by the server).</summary>
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

    /// <summary>The slug of a title, as the server makes it ("diagram" when nothing is left).</summary>
    public static string Slug(string? text)
    {
        var slug = WikiPageEntry.Slug(text);
        return slug == "page" ? "diagram" : slug;
    }
}
