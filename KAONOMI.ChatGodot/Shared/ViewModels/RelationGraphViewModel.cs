using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// An element of the relation graph: one end of the relations (a window and the object it showed). Ends with the same
/// object id and the same type (window) are one node, whose titles are put together.
/// </summary>
public sealed class RelationGraphNode
{
    public RelationGraphNode(string key, string type, string? objectId)
    {
        Key = key;
        Type = type;
        ObjectId = objectId;
    }

    public string Key { get; }

    /// <summary>The window of the element (Ideas, YouTube, Image analysis…).</summary>
    public string Type { get; }

    public string? ObjectId { get; }

    /// <summary>The titles of the element, one per distinct title of the ends merged into it.</summary>
    public List<string> Titles { get; } = [];

    /// <summary>The titles put together.</summary>
    public string Title => Titles.Count == 0 ? Type : string.Join(" · ", Titles);

    /// <summary>The data of the window of the element when it was linked (the first end merged into it).</summary>
    public string? Context { get; set; }

    /// <summary>What the user wrote about the element, in the relations.</summary>
    public List<string> Information { get; } = [];

    /// <summary>The relations the element is part of.</summary>
    public List<RelationEntry> Relations { get; } = [];

    // Layout (world coordinates) and motion of the force layout.
    public double X { get; set; }
    public double Y { get; set; }
    public double VelocityX { get; set; }
    public double VelocityY { get; set; }

    /// <summary>True while the node is dragged (the forces do not move it).</summary>
    public bool Pinned { get; set; }

    /// <summary>For an entity of the ontology: the date from which it holds (null: none, or not an entity).</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>For an entity of the ontology: the date until which it holds (null: none, or not an entity).</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>For an entity of the ontology: its entity type (PERSON, LOCATION…; null: none, or not an entity).</summary>
    public string? EntityType { get; set; }
}

/// <summary>A link of the graph: a relation from its source element to one of its destinations.</summary>
public sealed record RelationGraphEdge(RelationGraphNode From, RelationGraphNode To, RelationEntry Relation)
{
    /// <summary>False when the relation does not meet the filters (dates, confidence): the link is drawn transparent.</summary>
    public bool MatchesFilters { get; set; } = true;

    /// <summary>The title of the relation (else its nature).</summary>
    public string Label => Relation.Current.Title ?? Relation.Current.Nature ?? "(untitled link)";

    /// <summary>The nature of the relation (else its title): the short text written on the link in the graph.</summary>
    public string ShortLabel => string.IsNullOrWhiteSpace(Relation.Current.Nature) ? Label : Relation.Current.Nature;
}

/// <summary>
/// The relations window (<c>/relation</c>, always a popup): a search on the entities (the entities of the ontology whose
/// name, description, synonyms… match, and the elements whose title matches; empty: every element), shown as a graph
/// laid out by forces with their relations and their direct neighbours: an element per end of the relations (merged when
/// it is the same object of the same type), a link per relation from its source to each destination, with its title. At
/// most <see cref="MaxNodes"/> elements are shown: the entities found first, then their neighbours, the most linked first.
/// </summary>
public partial class RelationGraphViewModel : ViewModelBase, INoteContextProvider
{
    /// <summary>The relations read for a search (the newest ones), among which those of the entities found are kept.</summary>
    private const int RelationPool = 20000;

    /// <summary>The entities of the ontology read for a search.</summary>
    private const int EntityPool = 1000;

    /// <summary>The choices of the largest number of elements shown.</summary>
    public int[] NodeCountChoices { get; } = [50, 100, 150, 200, 250, 500, 750, 1000];

    /// <summary>The largest number of elements shown (the entities found first, then their neighbours, the most linked first).</summary>
    [ObservableProperty]
    private int _maxNodes = 150;

    partial void OnMaxNodesChanged(int value)
    {
        if (!IsBusy)
            _ = SearchAsync();
    }

    private readonly KaonomiAssistantService? _server;

    public RelationGraphViewModel() : this(null)
    {
    }

    public RelationGraphViewModel(KaonomiAssistantService? server)
    {
        _server = server;
        _ = SearchAsync();
    }

    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    private string _status = "Search the entities (empty: the most linked elements)";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(RefreshTitlesCommand))]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    private bool _isBusy;

    /// <summary>True while the view places the elements of a new graph (set by the view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    private bool _isLayingOut;

    /// <summary>How far the placing of the elements has gone, from 0 to 100 (set by the view).</summary>
    [ObservableProperty]
    private double _layoutProgress;

    /// <summary>A search or the placing of its graph is under way: the progress bars show.</summary>
    public bool IsWorking => IsBusy || IsLayingOut;

    /// <summary>The selected element (its details are shown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDetails))]
    private RelationGraphNode? _selectedNode;

    public IReadOnlyList<RelationGraphNode> Nodes { get; private set; } = [];
    public IReadOnlyList<RelationGraphEdge> Edges { get; private set; } = [];

    /// <summary>
    /// The elements near the selected one through the links of the graph, by distance: 0 the selected one, 1 its
    /// neighbours (first degree), 2 their neighbours (second degree); empty without selection. The graph lights them.
    /// </summary>
    public IReadOnlyDictionary<RelationGraphNode, int> Proximity { get; private set; } = new Dictionary<RelationGraphNode, int>();

    /// <summary>The distance of <paramref name="node"/> from the selected element (0, 1 or 2), null when farther or without selection.</summary>
    public int? DistanceOf(RelationGraphNode node) => Proximity.TryGetValue(node, out var distance) ? distance : null;

    /// <summary>True while an element is selected: the elements farther than the second degree are faded.</summary>
    public bool HasProximity => Proximity.Count > 0;

    partial void OnSelectedNodeChanged(RelationGraphNode? value)
    {
        var proximity = new Dictionary<RelationGraphNode, int>();
        if (value is not null)
        {
            // Breadth first, both ways along the links, two steps.
            var neighbours = new Dictionary<RelationGraphNode, List<RelationGraphNode>>();
            foreach (var edge in Edges)
            {
                Add(edge.From, edge.To);
                Add(edge.To, edge.From);
            }
            void Add(RelationGraphNode a, RelationGraphNode b)
            {
                if (!neighbours.TryGetValue(a, out var list))
                    neighbours[a] = list = [];
                list.Add(b);
            }
            proximity[value] = 0;
            var frontier = new List<RelationGraphNode> { value };
            for (var distance = 1; distance <= 2; distance++)
            {
                var next = new List<RelationGraphNode>();
                foreach (var node in frontier)
                    foreach (var neighbour in neighbours.GetValueOrDefault(node) ?? [])
                        if (proximity.TryAdd(neighbour, distance))
                            next.Add(neighbour);
                frontier = next;
            }
        }
        Proximity = proximity;
    }

    /// <summary>Raised when a new graph is ready (the view lays it out).</summary>
    public event Action? GraphChanged;

    /// <summary>"Show clusters": the clusters stored on the server drawn around their elements (loaded when ticked).</summary>
    [ObservableProperty]
    private bool _showClusters;

    /// <summary>The clusters stored on the server (loaded when "Show clusters" is ticked, and after a search).</summary>
    public IReadOnlyList<ClusterEntry> Clusters { get; private set; } = [];

    partial void OnShowClustersChanged(bool value)
    {
        if (value)
            _ = LoadClustersAsync();
        TitlesChanged?.Invoke();
    }

    /// <summary>Raised when the clusters have been read again (the timeline fills its list of clusters).</summary>
    public event Action? ClustersChanged;

    /// <summary>
    /// Reads the clusters of the graph again (the newest 300, the deleted ones and those drawn in /bpmnai, /infra or
    /// /mindmap left out) and redraws the graph.
    /// </summary>
    public async Task LoadClustersAsync()
    {
        if (_server is null)
            return;
        try
        {
            Clusters = (await _server.GetLastClustersAsync(300)).Where(c => ClusterCategories.IsGraph(c.Category)).ToList();
            ClustersChanged?.Invoke();
            TitlesChanged?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The clusters could not be read: " + ex.Message, true);
        }
    }

    /// <summary>Raised when the titles of the elements were read again (the view redraws them).</summary>
    public event Action? TitlesChanged;

    /// <inheritdoc />
    public string NoteWindowName => "Relations";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        searchTerm = Term,
        elementCount = Nodes.Count,
        linkCount = Edges.Count,
        selected = SelectedNode is { } node ? new { node.Type, node.ObjectId, node.Title } : null
    };

    /// <summary>", from … to …" for the optional dates of a relation, empty without them.</summary>
    private static string Dates(RelationVersionEntry r) => (r.DateFrom, r.DateTo) switch
    {
        (null, null) => string.Empty,
        ({ } from, null) => $", from {from:yyyy-MM-dd}",
        (null, { } to) => $", until {to:yyyy-MM-dd}",
        ({ } from, { } to) => $", from {from:yyyy-MM-dd} to {to:yyyy-MM-dd}"
    };

    /// <summary>The selected element: its type, id, titles, information and relations.</summary>
    public string SelectedDetails
    {
        get
        {
            if (SelectedNode is not { } node)
                return "Click an element of the graph to see its details · drag the elements · wheel: zoom · drag the background: move";
            var lines = new List<string> { node.Type, node.ObjectId is null ? "(no object id)" : $"id {node.ObjectId}", string.Empty };
            lines.AddRange(node.Titles.Select(t => "• " + t));
            if (node.Information.Count > 0)
            {
                lines.Add(string.Empty);
                lines.AddRange(node.Information.Select(i => "“" + i + "”"));
            }
            lines.Add(string.Empty);
            lines.Add($"Lit: {Proximity.Values.Count(d => d == 1)} element(s) of the 1st degree (cyan), {Proximity.Values.Count(d => d == 2)} of the 2nd degree (green)");
            lines.Add(string.Empty);
            lines.Add($"{node.Relations.Count} relation(s):");
            lines.AddRange(node.Relations.Select(r => r.Current).Select(r =>
                $"– {r.Title ?? r.Nature ?? "(untitled)"}{(r.Nature is { } nature && r.Title is not null ? $" ({nature})" : string.Empty)}, probability {(r.Probability > 0 ? "+" : string.Empty)}{r.Probability}{Dates(r)}, version {r.Version}"));
            return string.Join('\n', lines);
        }
    }

    private bool CanSearch() => !IsBusy;

    // ---- Opening the object of an element (a double-click on it) ----

    /// <summary>Makes the analysis of a picture, for its window (set by the chat).</summary>
    public Func<string, ImageAnalysisViewModel>? CreateAnalysis { get; set; }

    /// <summary>Makes an ontology, to open its entities in Entity windows (set by the chat).</summary>
    public Func<OntologyWindowViewModel>? CreateOntology { get; set; }

    /// <summary>The ontology the entities of the graph are opened from, made at the first entity opened.</summary>
    private OntologyWindowViewModel? _ontology;

    /// <summary>Raised when the ontology is made: the view opens its Entity windows.</summary>
    public event Action<OntologyWindowViewModel>? OntologyCreated;

    /// <summary>Raised to open the window of a picture.</summary>
    public event Action<ImageAnalysisViewModel>? ImageRequested;

    /// <summary>
    /// Opens the object of <paramref name="node"/>: the image analysis window of a picture, the Entity window of an
    /// entity of the ontology. Other elements have no window of their own to open.
    /// </summary>
    public async Task OpenNodeAsync(RelationGraphNode node)
    {
        try
        {
            if (string.Equals(node.Type, "Image analysis", StringComparison.OrdinalIgnoreCase))
                await OpenImageAsync(node);
            else if (node.Type.Equals("Entity", StringComparison.OrdinalIgnoreCase) || node.Type.Equals("Ontology", StringComparison.OrdinalIgnoreCase))
                await OpenEntityAsync(node);
            else
                SetStatus($"{node.Type}: a double-click opens the pictures and the entities only", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus($"{node.Title} cannot be opened: {ex.Message}", true);
        }
    }

    private async Task OpenImageAsync(RelationGraphNode node)
    {
        // The path of the picture: in the data of its window, else in its analysis on the server.
        string? path = null;
        try
        {
            if (node.Context is { Length: > 0 } context && JsonNode.Parse(context)?["data"]?["imagePath"] is JsonValue value)
                path = value.ToString();
        }
        catch (JsonException)
        {
        }
        if (string.IsNullOrWhiteSpace(path) && _server is not null && node.ObjectId is { } id)
            path = (await _server.GetImageAnalysisAsync(id))?.ImagePath;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            SetStatus($"The picture of {node.Title} is not found{(path is null ? string.Empty : ": " + path)}", true);
            return;
        }
        if (CreateAnalysis is not { } create)
        {
            SetStatus("The image analysis window is not available here", true);
            return;
        }
        ImageRequested?.Invoke(create(path));
        SetStatus($"Picture opened: {node.Title}", false);
        ActivityLog.Info("Relation", $"Picture opened from the graph: {path}");
    }

    private async Task OpenEntityAsync(RelationGraphNode node)
    {
        if (node.ObjectId is not { } id)
        {
            SetStatus($"{node.Title} has no id: its entity cannot be found", true);
            return;
        }
        if (Ontology() is not { } ontology)
            return;
        // Loaded once (the Entity windows already open keep their nodes); again when the entity is not there yet.
        if (ontology.FindNode(id) is null)
        {
            SetStatus("Loading the ontology…", false);
            await ontology.RefreshCommand.ExecuteAsync(null);
        }
        if (ontology.FindNode(id) is not { } entity)
        {
            SetStatus($"The entity {node.Title} ({id}) is not in the ontology", true);
            return;
        }
        ontology.EditNode(entity);
        SetStatus($"Entity opened: {entity.DisplayName}", false);
        ActivityLog.Info("Relation", $"Entity opened from the graph: {entity.DisplayName} ({id})");
    }

    /// <summary>The ontology of the graph (its Entity windows, the search of entities), created the first time.</summary>
    private OntologyWindowViewModel? Ontology()
    {
        if (_ontology is not null)
            return _ontology;
        if (CreateOntology is not { } create)
        {
            SetStatus("The ontology is not available here", true);
            return null;
        }
        _ontology = create();
        OntologyCreated?.Invoke(_ontology);
        return _ontology;
    }

    // ---- Entities added by hand (+), and the relations drawn between the elements ----

    /// <summary>The window name of an entity of the ontology at the end of a relation (as its Entity window says it).</summary>
    public const string EntityType = "Entity";

    /// <summary>
    /// What a right click on an element annotates: an entity of the ontology (with its entity type) or a picture (an
    /// image analysis); null for another element.
    /// </summary>
    public static AnnotationTarget? AnnotationTargetOf(RelationGraphNode node) =>
        node.Type == EntityType
            ? new AnnotationTarget(AnnotationTargetTypes.Entity, node.ObjectId, node.Title, null, node.EntityType)
            : string.Equals(node.Type, "Image analysis", StringComparison.OrdinalIgnoreCase)
                ? new AnnotationTarget(AnnotationTargetTypes.Image, node.ObjectId, node.Title)
                : null;

    /// <summary>The entities added by +: they stay through the searches, held by a relation or not.</summary>
    private readonly List<RelationGraphNode> _addedNodes = [];

    /// <summary>Raised when an element is added by hand (the view places it without laying the graph out again).</summary>
    public event Action<RelationGraphNode>? NodeAdded;

    /// <summary>Raised when the entity types and dates of the entities of the graph have been read from the server.</summary>
    public event Action? PeriodsChanged;

    /// <summary>The dates and entity type of the entities of the ontology read last, by entity id (kept for the next builds).</summary>
    private readonly Dictionary<string, (DateTime? Start, DateTime? End, string? Type)> _periods = [];

    /// <summary>
    /// The dates and entity type known for <paramref name="node"/> (an entity of the ontology) put on it; before they
    /// are read, its type is the one in the data of its window.
    /// </summary>
    private void ApplyPeriod(RelationGraphNode node)
    {
        if (node.Type != EntityType)
            return;
        if (node.ObjectId is { } id && _periods.TryGetValue(id, out var period))
            (node.StartDate, node.EndDate, node.EntityType) = period;
        else if (node.EntityType is null && node.Context is { Length: > 0 } context)
        {
            try
            {
                node.EntityType = JsonNode.Parse(context)?["data"]?["entityType"]?.GetValue<string>() is { Length: > 0 } type ? type : null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
            }
        }
    }

    /// <summary>
    /// Reads the start and end dates and the entity type of the entities of the graph (<c>GET /ontology/{id}</c>, a few
    /// at a time) and puts them on their elements; <see cref="PeriodsChanged"/> when one changed. An entity that cannot
    /// be read keeps what was known before.
    /// </summary>
    private async Task LoadPeriodsAsync()
    {
        if (_server is null)
            return;
        var entities = Nodes.Where(n => n.Type == EntityType && n.ObjectId is not null).ToList();
        if (entities.Count == 0)
            return;
        using var gate = new System.Threading.SemaphoreSlim(6);
        var read = await Task.WhenAll(entities.Select(n => n.ObjectId!).Distinct().Select(async id =>
        {
            await gate.WaitAsync();
            try
            {
                return (Id: id, Entry: await _server.GetOntologyAsync(id));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return (Id: id, Entry: (OntologyEntry?)null);
            }
            finally
            {
                gate.Release();
            }
        }));
        var changed = false;
        foreach (var (id, entry) in read)
            if (entry is not null)
                _periods[id] = (entry.StartDate, entry.EndDate, string.IsNullOrWhiteSpace(entry.EntityType) ? null : entry.EntityType.Trim());
        foreach (var node in entities)
        {
            var before = (node.StartDate, node.EndDate, node.EntityType);
            ApplyPeriod(node);
            changed |= before != (node.StartDate, node.EndDate, node.EntityType);
        }
        if (changed)
            PeriodsChanged?.Invoke();
    }

    /// <summary>
    /// Saves <paramref name="cluster"/> as it is (<c>POST /cluster/update</c>: its dates and those of its members
    /// changed on the timeline) and reads the clusters again; false when it was not saved (the status bar says why).
    /// </summary>
    public async Task<bool> SaveClusterAsync(ClusterEntry cluster)
    {
        if (_server is null)
        {
            SetStatus("No Kaonomi server: the cluster cannot be saved", true);
            return false;
        }
        try
        {
            var result = await _server.UpdateClusterAsync(cluster);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus(result.Message ?? $"The cluster {cluster.Name} was not saved", true);
                return false;
            }
            SetStatus($"Cluster {cluster.Name} saved", false);
            await LoadClustersAsync();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus($"The cluster {cluster.Name} was not saved: {ex.Message}", true);
            return false;
        }
    }

    /// <summary>
    /// Gives <paramref name="node"/> (an entity of the ontology) the start date <paramref name="start"/> and the end
    /// date <paramref name="end"/> (null: none), on the server (<c>GET /ontology/{id}</c>, then <c>POST
    /// /ontology/update</c>) and on the graph (<see cref="PeriodsChanged"/>). The dates are days: the time is dropped,
    /// and an end before the start is swapped with it. The status bar says what was done, or what went wrong.
    /// </summary>
    public async Task SetPeriodAsync(RelationGraphNode node, DateTime? start, DateTime? end)
    {
        if (node.Type != EntityType || node.ObjectId is not { } id)
            return;
        if (_server is null)
        {
            SetStatus("No Kaonomi server: the dates cannot be saved", true);
            return;
        }
        start = start?.Date;
        end = end?.Date;
        if (start is { } s && end is { } e && e < s)
            (start, end) = (e, s);
        try
        {
            var entry = await _server.GetOntologyAsync(id);
            if (entry is null)
            {
                SetStatus($"The entity {node.Title} was not found on the server", true);
                return;
            }
            entry.StartDate = start;
            entry.EndDate = end;
            var result = await _server.UpdateOntologyAsync(entry);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus(result.Message ?? $"The dates of {node.Title} were not saved", true);
                return;
            }
            _periods[id] = (start, end, string.IsNullOrWhiteSpace(entry.EntityType) ? node.EntityType : entry.EntityType.Trim());
            foreach (var other in Nodes.Where(n => n.Type == EntityType && n.ObjectId == id))
                ApplyPeriod(other);
            PeriodsChanged?.Invoke();
            SetStatus($"{node.Title}: {(start is { } a ? a.ToString("yyyy-MM-dd") : "no start")} → {(end is { } b ? b.ToString("yyyy-MM-dd") : "no end")}", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus($"The dates of {node.Title} were not saved: {ex.Message}", true);
        }
    }

    /// <summary>The entities of the ontology matching <paramref name="text"/> (the ontology is loaded the first time).</summary>
    public async Task<(List<OntologyNodeViewModel> Nodes, bool ServerAnswered)> FindEntitiesAsync(string text, System.Threading.CancellationToken cancellationToken)
    {
        if (Ontology() is not { } ontology)
            return ([], false);
        if (!ontology.HasNodes)
            await ontology.RefreshCommand.ExecuteAsync(null);
        // The instances only (every entity type): an ontology class is not an element of a relation.
        var (nodes, answered) = await ontology.FindNodesAsync(text, cancellationToken);
        return (nodes.Where(n => !n.IsOntologyClass).ToList(), answered);
    }

    /// <summary>
    /// Adds <paramref name="entity"/> to the graph as an element (selected): an entity already shown is only selected.
    /// Its data is the one of its Entity window, so that a relation drawn from it is the same as one linked from there.
    /// </summary>
    public RelationGraphNode AddEntity(OntologyNodeViewModel entity)
    {
        var key = $"{EntityType.ToLowerInvariant()}|{entity.Id}";
        if (Nodes.FirstOrDefault(n => n.Key == key) is { } shown)
        {
            SelectedNode = shown;
            SetStatus($"{entity.DisplayName} is already in the graph", false);
            return shown;
        }
        var context = new JsonObject
        {
            ["window"] = EntityType,
            ["data"] = new JsonObject
            {
                ["id"] = entity.Id,
                ["name"] = entity.DisplayName,
                ["entityType"] = entity.EntityType,
                ["nodePath"] = OntologyWindowViewModel.PathOf(entity)
            }
        };
        var node = new RelationGraphNode(key, EntityType, entity.Id) { Context = context.ToJsonString(), StartDate = entity.StartDate, EndDate = entity.EndDate,
            EntityType = string.IsNullOrWhiteSpace(entity.EntityType) ? null : entity.EntityType.Trim() };
        if (entity.Id is { } entityId)
            _periods[entityId] = (entity.StartDate, entity.EndDate, node.EntityType);
        node.Titles.Add(entity.DisplayName);
        _addedNodes.Add(node);
        Nodes = [.. Nodes, node];
        NodeAdded?.Invoke(node);
        SelectedNode = node;
        SetStatus($"Entity added: {entity.DisplayName} · draw a relation from its link icon", false);
        ActivityLog.Info("Relation", $"Entity added to the graph: {entity.DisplayName} ({entity.Id})");
        return node;
    }

    /// <summary>What an element is at one end of a new relation: its window, its object id and its data.</summary>
    public static Views.LinkEnd LinkEndOf(RelationGraphNode node) => new(
        node.Type,
        node.ObjectId,
        node.Context is { Length: > 0 } context
            ? context
            : new JsonObject { ["window"] = node.Type, ["data"] = new JsonObject { ["id"] = node.ObjectId, ["title"] = node.Title } }.ToJsonString());

    // ---- Filters: the dates of the relations and their probability ----

    /// <summary>The relations of the last search, before the filters.</summary>
    private IReadOnlyList<RelationEntry> _found = [];

    /// <summary>The titles read again by Refresh, by element (kept when the filters change).</summary>
    private readonly Dictionary<string, string> _refreshedTitles = [];

    /// <summary>True: only the relations whose dates meet the period <see cref="FilterFrom"/> – <see cref="FilterTo"/>.</summary>
    [ObservableProperty]
    private bool _dateFilterEnabled;

    /// <summary>Start of the period of the date filter; null: no start.</summary>
    [ObservableProperty]
    private DateTime? _filterFrom;

    /// <summary>End of the period of the date filter; null: no end.</summary>
    [ObservableProperty]
    private DateTime? _filterTo;

    /// <summary>
    /// The confidence of the filter, -100 (absolutely impossible) to 100 (absolutely certain): from 0, the relations at
    /// least this probable meet it; below 0, the relations at most this probable.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinProbabilityText))]
    private double _minProbability = -100;

    /// <summary>The confidence of the filter and its meaning.</summary>
    public string MinProbabilityText
    {
        get
        {
            var v = (int)Math.Round(MinProbability);
            var meaning = v switch
            {
                -100 => "absolutely impossible only",
                <= -60 => "very unlikely and below",
                <= -20 => "unlikely and below",
                < 0 => "rather unlikely and below",
                0 => "to evaluate and above",
                < 20 => "rather likely and above",
                < 60 => "likely and above",
                < 100 => "very likely and above",
                _ => "absolutely certain only"
            };
            return v < 0 ? $"≤ {v} · {meaning}" : $"≥ {(v > 0 ? "+" : string.Empty)}{v} · {meaning}";
        }
    }

    /// <summary>True: the confidence filter is on (see <see cref="MinProbability"/>).</summary>
    [ObservableProperty]
    private bool _confidenceFilterEnabled;

    /// <summary>
    /// True when the current version of <paramref name="relation"/> meets the date filter: its period (From – To, an
    /// empty end being open) meets the period of the filter (an empty end being open too). A relation without any date
    /// does not meet an active date filter.
    /// </summary>
    public bool MatchesDates(RelationEntry relation)
    {
        if (!DateFilterEnabled || (FilterFrom is null && FilterTo is null))
            return true;
        var current = relation.Current;
        if (current.DateFrom is null && current.DateTo is null)
            return false;
        var start = current.DateFrom?.Date ?? DateTime.MinValue;
        var end = current.DateTo?.Date ?? DateTime.MaxValue;
        return (FilterTo is not { } to || start <= to.Date) && (FilterFrom is not { } from || end >= from.Date);
    }

    /// <summary>
    /// True when the probability of the current version of <paramref name="relation"/> meets the confidence filter:
    /// from 0 at least the value of the slider, below 0 at most this value.
    /// </summary>
    public bool MatchesProbability(RelationEntry relation)
    {
        if (!ConfidenceFilterEnabled)
            return true;
        var value = (int)Math.Round(MinProbability);
        return value >= 0 ? relation.Current.Probability >= value : relation.Current.Probability <= value;
    }

    /// <summary>
    /// Builds the graph of the relations of the last search that meet the filters (by Search and by Refresh: changing
    /// a filter does nothing until then). False when the filters are wrong (To before From).
    /// </summary>
    private bool ApplyFilters()
    {
        if (DateFilterEnabled && FilterFrom is { } from && FilterTo is { } to && to < from)
        {
            SetStatus("The date To of the filter is before the date From", true);
            return false;
        }
        // Every relation found is drawn; those that do not meet the filters are transparent.
        var relations = _found.Where(r => r.Versions.Count > 0).ToList();
        var matching = relations.Where(r => MatchesDates(r) && MatchesProbability(r)).ToHashSet();
        Build(relations);
        foreach (var edge in Edges)
            edge.MatchesFilters = matching.Contains(edge.Relation);
        TitlesChanged?.Invoke();
        var faded = relations.Count - matching.Count;
        var found = _matchCount is { } count ? (count == 0 ? $"No entity matches \"{Term.Trim()}\"" : $"{count} element(s) match \"{Term.Trim()}\"") + " · " : string.Empty;
        SetStatus(_found.Count == 0 && Nodes.Count == 0
            ? found + "No relation found" + (_addedNodes.Count > 0 ? $" · {_addedNodes.Count} entity(ies) added: draw a relation from their link icon" : string.Empty)
            : found + $"{Nodes.Count} element(s) shown (at most {MaxNodes}) · {relations.Count} relation(s) · {Edges.Count} link(s)"
              + (faded > 0 ? $" · {faded} transparent (not meeting the filters)" : string.Empty), false);
        return true;
    }

    /// <summary>
    /// Applies the filters (dates, confidence) to the relations of the last search, then reads the titles of the
    /// elements again, as Refresh in the relation window: for an entity of the ontology its name and for an image
    /// analysis its title (from the server), else the title in the data of its window. An element then shows its
    /// current title only. The relations are not changed (Save in their window keeps the titles).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task RefreshTitlesAsync()
    {
        if (!ApplyFilters())
            return;
        var filterStatus = Status;
        if (Nodes.Count == 0)
            return;
        IsBusy = true;
        SetStatus("Reading the titles…", false);
        var changed = 0;
        foreach (var node in Nodes)
        {
            if (await RelationTitles.ResolveAsync(node.Type, node.ObjectId, node.Context, _server) is not { } title)
                continue;
            if (node.Titles.Count == 1 && node.Titles[0] == title)
                continue;
            node.Titles.Clear();
            node.Titles.Add(title);
            _refreshedTitles[node.Key] = title;
            changed++;
        }
        IsBusy = false;
        TitlesChanged?.Invoke();
        OnPropertyChanged(nameof(SelectedDetails));
        SetStatus(filterStatus + (changed == 0 ? " · titles up to date" : $" · {changed} title(s) updated"), false);
        ActivityLog.Info("Relation", $"Graph titles refreshed: {changed} updated");
    }

    /// <summary>The keys of the elements shown by the last search (null: before any search).</summary>
    private HashSet<string>? _shown;

    /// <summary>The entities found by the last search that no relation holds: shown on their own.</summary>
    private List<RelationGraphNode> _foundAlone = [];

    /// <summary>The number of elements matching the text of the last search (null: no text).</summary>
    private int? _matchCount;

    /// <summary>
    /// Searches the entities and builds their graph: the entities of the ontology matching <see cref="Term"/> (the search
    /// of the server: name, description, synonyms…) and the elements whose title holds it (accents and case ignored),
    /// with the relations that hold them and their direct neighbours; an empty text shows every element. At most
    /// <see cref="MaxNodes"/> elements: the ones found first, then their neighbours, the most linked first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        if (_server is null)
        {
            SetStatus("No Kaonomi server: the relations cannot be read", true);
            return;
        }
        IsBusy = true;
        var term = Term.Trim();
        SetStatus(term.Length == 0 ? "Reading the relations…" : $"Searching the entities matching \"{term}\"…", false);
        try
        {
            var relations = (await _server.SearchRelationsAsync("*", RelationPool)).Where(r => r.Versions.Count > 0).ToList();

            // How linked each element is, and its neighbours.
            var degree = new Dictionary<string, int>();
            var neighbours = new Dictionary<string, HashSet<string>>();
            foreach (var relation in relations)
            {
                var ends = EndsOf(relation);
                foreach (var (key, _) in ends.Skip(1))
                {
                    Link(ends[0].Key, key);
                    Link(key, ends[0].Key);
                }
            }
            void Link(string from, string to)
            {
                degree[from] = degree.GetValueOrDefault(from) + 1;
                if (!neighbours.TryGetValue(from, out var set))
                    neighbours[from] = set = [];
                set.Add(to);
            }

            // The elements found: each term of the expression (and, or, not, parentheses) matches the entities of the
            // ontology the server finds for it and the elements whose title holds it; the expression then decides.
            HashSet<string>? matched = null;
            var alone = new List<RelationGraphNode>();
            if (term.Length > 0)
            {
                if (BooleanQuery.Parse(term) is not { } query)
                {
                    SetStatus($"\"{term}\" holds no word to search (only operators or parentheses)", true);
                    return;
                }
                var entities = new Dictionary<string, OntologyEntry>();
                var entitiesOf = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var word in query.Terms)
                {
                    var found = await SearchEntitiesAsync(word);
                    entitiesOf[word] = [.. found.Keys];
                    foreach (var (id, entity) in found)
                        entities.TryAdd(id, entity);
                }
                // Every element: the ends of the relations, and the entities found (held by no relation or not).
                var elements = new Dictionary<string, (string? EntityId, string Title)>();
                foreach (var relation in relations)
                    foreach (var (key, end) in EndsOf(relation))
                        elements.TryAdd(key, (IsEntity(end) ? end.ObjectId?.Trim() : null, Fold(TitleOf(end) ?? string.Empty)));
                foreach (var (id, entity) in entities)
                    elements.TryAdd($"{EntityType.ToLowerInvariant()}|{id}", (id, Fold(entity.Name ?? string.Empty)));
                var folded = query.Terms.ToDictionary(t => t, Fold, StringComparer.OrdinalIgnoreCase);
                matched = elements.Where(e => query.Matches(word =>
                        (e.Value.EntityId is { } id && entitiesOf[word].Contains(id)) || e.Value.Title.Contains(folded[word], StringComparison.Ordinal)))
                    .Select(e => e.Key).ToHashSet();
                foreach (var key in matched.Where(k => !degree.ContainsKey(k)))
                    if (elements[key].EntityId is { } id && entities.TryGetValue(id, out var entity))
                        alone.Add(EntityNode(key, entity));
            }
            _matchCount = matched?.Count;

            // The elements shown: the ones found (else all), then their neighbours, the most linked first.
            IEnumerable<string> Ranked(IEnumerable<string> keys) => keys.OrderByDescending(k => degree.GetValueOrDefault(k)).ThenBy(k => k, StringComparer.Ordinal);
            var shown = Ranked(matched ?? (IEnumerable<string>)degree.Keys).Take(MaxNodes).ToList();
            if (matched is not null && shown.Count < MaxNodes)
                shown.AddRange(Ranked(matched.SelectMany(k => neighbours.GetValueOrDefault(k) ?? []).Where(k => !matched.Contains(k)).Distinct()).Take(MaxNodes - shown.Count));
            _shown = shown.ToHashSet();
            _foundAlone = alone.Where(n => _shown.Contains(n.Key)).ToList();
            // The relations drawn: those whose source and at least one destination are shown.
            _found = relations.Where(r => EndsOf(r) is var ends && _shown.Contains(ends[0].Key) && ends.Skip(1).Any(e => _shown.Contains(e.Key))).ToList();
            _refreshedTitles.Clear();
            ApplyFilters();
            await LoadPeriodsAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The relations could not be read: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The entities of the ontology the server finds for <paramref name="term"/>, by id (no class, no deleted one); empty when the server cannot search.</summary>
    private async Task<Dictionary<string, OntologyEntry>> SearchEntitiesAsync(string term)
    {
        try
        {
            return (await _server!.SearchOntologiesAsync(term, EntityPool))
                .Where(e => !string.IsNullOrWhiteSpace(e.Id) && !e.IsOntologyClass && !e.IsDeleted)
                .GroupBy(e => e.Id!.Trim())
                .ToDictionary(g => g.Key, g => g.First());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            ActivityLog.Error("Relation", $"Entity search \"{term}\" unavailable on the server: {ex.Message}");
            return [];
        }
    }

    /// <summary>An element for an entity found that no relation holds (as an entity added by +).</summary>
    private static RelationGraphNode EntityNode(string key, OntologyEntry entity)
    {
        var context = new JsonObject
        {
            ["window"] = EntityType,
            ["data"] = new JsonObject { ["id"] = entity.Id, ["name"] = entity.Name, ["entityType"] = entity.EntityType }
        };
        var node = new RelationGraphNode(key, EntityType, entity.Id) { Context = context.ToJsonString(), StartDate = entity.StartDate, EndDate = entity.EndDate,
            EntityType = string.IsNullOrWhiteSpace(entity.EntityType) ? null : entity.EntityType.Trim() };
        node.Titles.Add(string.IsNullOrWhiteSpace(entity.Name) ? entity.Id ?? EntityType : entity.Name.Trim());
        return node;
    }

    private static bool IsEntity(RelationEndEntry end) =>
        end.WindowName?.Trim() is { } window && (window.Equals(EntityType, StringComparison.OrdinalIgnoreCase) || window.Equals("Ontology", StringComparison.OrdinalIgnoreCase));

    /// <summary>The ends of the current version of <paramref name="relation"/> with the key of their element: the source first, then the destinations.</summary>
    private static List<(string Key, RelationEndEntry End)> EndsOf(RelationEntry relation)
    {
        var current = relation.Current;
        var ends = new List<(string, RelationEndEntry)> { (KeyOf(relation, current.Source, 0), current.Source) };
        for (var i = 0; i < current.Destinations.Count; i++)
            ends.Add((KeyOf(relation, current.Destinations[i], i + 1), current.Destinations[i]));
        return ends;
    }

    /// <summary>The key of the element of an end: its window and object id (an end without object id is an element of its own).</summary>
    private static string KeyOf(RelationEntry relation, RelationEndEntry end, int index)
    {
        var type = string.IsNullOrWhiteSpace(end.WindowName) ? "Window" : end.WindowName.Trim();
        var id = string.IsNullOrWhiteSpace(end.ObjectId) ? null : end.ObjectId.Trim();
        return id is null ? $"{relation.Id}|{index}" : $"{type.ToLowerInvariant()}|{id}";
    }

    /// <summary><paramref name="text"/> in lower case without accents, for the comparisons of the search.</summary>
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return new string(decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    }

    /// <summary>
    /// The graph of <paramref name="relations"/>: a node per end (the same object id of the same type is one node), an
    /// edge from the source of each relation to each of its destinations.
    /// </summary>
    public void Build(IReadOnlyList<RelationEntry> relations)
    {
        var nodes = new Dictionary<string, RelationGraphNode>();
        var edges = new List<RelationGraphEdge>();
        RelationGraphNode NodeOf(RelationEntry relation, RelationEndEntry end, int index)
        {
            var type = string.IsNullOrWhiteSpace(end.WindowName) ? "Window" : end.WindowName.Trim();
            var id = string.IsNullOrWhiteSpace(end.ObjectId) ? null : end.ObjectId.Trim();
            // Without an object id, an end is an element of its own.
            var key = KeyOf(relation, end, index);
            if (!nodes.TryGetValue(key, out var node))
                nodes[key] = node = new RelationGraphNode(key, type, id) { Context = end.Context };
            var title = TitleOf(end);
            if (title is not null && !node.Titles.Contains(title, StringComparer.OrdinalIgnoreCase))
                node.Titles.Add(title);
            if (!string.IsNullOrWhiteSpace(end.Information) && !node.Information.Contains(end.Information.Trim()))
                node.Information.Add(end.Information.Trim());
            if (!node.Relations.Contains(relation))
                node.Relations.Add(relation);
            return node;
        }
        // Each relation as its current version (its last one).
        // Only the elements chosen by the search (at most MaxNodes): a destination not shown is left out with its link.
        bool Shown(RelationEntry relation, RelationEndEntry end, int index) => _shown is null || _shown.Contains(KeyOf(relation, end, index));
        foreach (var relation in relations.Where(r => r.Versions.Count > 0))
        {
            var current = relation.Current;
            if (!Shown(relation, current.Source, 0))
                continue;
            var source = NodeOf(relation, current.Source, 0);
            for (var i = 0; i < current.Destinations.Count; i++)
                if (Shown(relation, current.Destinations[i], i + 1))
                    edges.Add(new RelationGraphEdge(source, NodeOf(relation, current.Destinations[i], i + 1), relation));
        }
        // The entities found that no relation holds, on their own.
        foreach (var alone in _foundAlone)
            nodes.TryAdd(alone.Key, alone);
        // The entities added by hand stay, even when the relations found do not hold them (or no more, once one is
        // deleted); one a relation holds is the element of that relation.
        foreach (var added in _addedNodes)
            nodes.TryAdd(added.Key, added);
        foreach (var node in nodes.Values)
            if (_refreshedTitles.TryGetValue(node.Key, out var refreshed))
            {
                node.Titles.Clear();
                node.Titles.Add(refreshed);
            }
        foreach (var node in nodes.Values)
            ApplyPeriod(node);
        Nodes = nodes.Values.ToList();
        Edges = edges;
        SelectedNode = null;
        GraphChanged?.Invoke();
    }

    /// <summary>The title of an end: the one saved with it (updated by Refresh in the relation window), else the one in its data.</summary>
    private static string? TitleOf(RelationEndEntry end) =>
        !string.IsNullOrWhiteSpace(end.Title) ? end.Title.Trim()
        : RelationTitles.FromContext(end.Context)
          ?? (end.Information?.Trim().Split('\n')[0] is { Length: > 0 } line ? (line.Length > 80 ? line[..80] + "…" : line) : null);

    /// <summary>Shows <paramref name="text"/> in the status bar of the window (in red when <paramref name="error"/>).</summary>
    public void ReportStatus(string text, bool error) => SetStatus(text, error);

    private void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }
}
