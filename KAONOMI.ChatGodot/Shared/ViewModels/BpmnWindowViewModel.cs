using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// AIMN (from BPMN), the editor of diagrams of AI processes in the BPMN 2.0 notation (<c>/bpmnai</c>): one process with the basic elements of the notation (start
/// events, plain, message, timer or event collector; end events; task, user task, agent, virtual agent, collapsed sub-process; exclusive, parallel and
/// inclusive gateways; data object, the search engine, the graph base and text annotation), joined by sequence flows (an arrow between two flow nodes)
/// and associations (a dotted line to a data object or an annotation). The palette gives the kind to put on the
/// canvas (<see cref="Placing"/>) or the connect tool (<see cref="Connecting"/>); the canvas moves, joins, renames and
/// deletes. Every change can be undone (<see cref="UndoCommand"/>: a copy of the diagram before each change). The
/// diagram is saved as BPMN 2.0 XML with its diagram interchange (<see cref="BpmnXml"/>), in <c>~/kaonomi/bpmn</c>
/// by default, and read back from any BPMN 2.0 file. It is also saved in the search engine through the Kaonomi server
/// (the <c>bpmn</c> index: its XML, a comment, its version; its id the slug of the name of the process), where the
/// diagrams are searched (<see cref="SearchCommand"/>) and opened again.
/// The same editor draws the infrastructures (<see cref="BpmnNotation.Infrastructure"/>, <c>/infra</c>,
/// <see cref="InfraWindowViewModel"/>): its palette then holds the machines, devices and services of an installation,
/// joined by links; the files go to <c>~/kaonomi/infra</c> and the search engine keeps them in the <c>infra</c> index.
/// </summary>
public partial class BpmnWindowViewModel : ViewModelBase, IUnsavedChanges, IProjectDocument
{
    /// <summary>The project the diagram is attached to (the id of a project, chosen by its name in the header of the window), or null; saved with it in the search engine.</summary>
    [ObservableProperty]
    private string? _projectId;

    /// <summary>The editor always holds a diagram.</summary>
    public bool HasProjectDocument => true;

    partial void OnProjectIdChanged(string? value)
    {
        if (!_loading)
            IsDirty = true;
    }

    private const int UndoLimit = 100;
    private Stack<BpmnDiagram> _undo = new();
    private readonly Stack<BpmnDiagram> _redo = new();
    private BpmnDiagram? _moveStart;
    private readonly KaonomiAssistantService _server;
    /// <summary>True while a diagram is being loaded: the comment set then is not a change.</summary>
    private bool _loading;
    /// <summary>The element whose name is being typed: one undo step for the whole name, not one per letter.</summary>
    private object? _renaming;

    /// <summary>For the designer.</summary>
    public BpmnWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    public BpmnWindowViewModel(KaonomiAssistantService server, BpmnNotation notation = BpmnNotation.Process)
    {
        _server = server;
        Notation = notation;
        Diagram = Sample();
        ProcessName = Diagram.Name;
        Status = "A new diagram: drag an element of the palette onto the canvas, drag the arrow of an element onto another to join them";
        _ = SearchAsync();
    }

    /// <summary>What the diagrams of this window draw: AI processes (/bpmnai) or infrastructures (/infra).</summary>
    public BpmnNotation Notation { get; }

    public bool IsInfrastructure => Notation == BpmnNotation.Infrastructure;

    /// <summary>The name of the editor (its header, the title of its window).</summary>
    public string EditorTitle => IsInfrastructure ? "Infrastructure" : "AIMN (from BPMN)";

    /// <summary>What a diagram is the drawing of, in the texts: a process, or an infrastructure.</summary>
    public string Subject => IsInfrastructure ? "infrastructure" : "process";

    /// <summary>The kinds of the palette.</summary>
    public BpmnElementKind[] Kinds => BpmnElementKinds.Of(Notation);

    /// <summary>Where the diagrams of this window are saved by default.</summary>
    public string FilesDirectory => IsInfrastructure ? Path.Combine(ChatSettings.KaonomiHome, "infra") : DefaultDirectory;

    /// <summary>Where the pictures (PNG) of the diagrams of this window are saved.</summary>
    public string PicturesFolder => IsInfrastructure ? Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "infra") : PicturesDirectory;

    /// <summary>The routes of the Kaonomi server of the diagrams of this window (and their index).</summary>
    private string Route => IsInfrastructure ? "infra" : "bpmn";

    /// <summary>Where the diagrams are saved by default.</summary>
    public static string DefaultDirectory => Path.Combine(ChatSettings.KaonomiHome, "bpmn");

    /// <summary>Where the pictures of the diagrams (PNG) are saved: the Pictures folder of the system, kaonomi/bpmn.</summary>
    public static string PicturesDirectory => Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "bpmn");

    /// <summary>The diagram edited.</summary>
    public BpmnDiagram Diagram { get; private set; }

    /// <summary>Raised after any change of the diagram (the canvas draws it again).</summary>
    public event Action? Changed;

    /// <summary>The file of the diagram (null until it is saved).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName))]
    private string? _filePath;

    /// <summary>True when the diagram has changes not saved.</summary>
    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>The kind the next click on the canvas places (null: the pointer selects and moves).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolName))]
    private BpmnElementKind? _placing;

    /// <summary>True while the connect tool is chosen: a click on an element then on another joins them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolName))]
    private bool _connecting;

    /// <summary>The element or the flow selected (a <see cref="BpmnShape"/> or a <see cref="BpmnFlow"/>), null when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedKind), nameof(SelectedId))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private object? _selected;

    /// <summary>The name of the selected element or flow, edited in the properties panel.</summary>
    [ObservableProperty]
    private string _selectedName = string.Empty;

    /// <summary>The name of the process.</summary>
    [ObservableProperty]
    private string _processName = string.Empty;

    /// <summary>The comment written with the diagram (saved with it in the search engine).</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>The id of the diagram in the search engine (the slug of its title), null until it is saved there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiagramInfo), nameof(IsInEngine))]
    [NotifyCanExecuteChangedFor(nameof(DeleteFromServerCommand))]
    private string? _diagramId;

    /// <summary>Its version in the search engine (1 at its creation, one more at each save).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiagramInfo))]
    private int _version;

    /// <summary>The search typed for the diagrams of the search engine (empty: the ones changed last).</summary>
    [ObservableProperty]
    private string _search = string.Empty;

    /// <summary>True while the server is called.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The diagrams of the search engine found by the search (or changed last).</summary>
    public ObservableCollection<BpmnEntry> Diagrams { get; } = [];

    public bool IsInEngine => DiagramId is not null;

    public string DiagramInfo => DiagramId is null ? "Not in the search engine yet" : $"{DiagramId} · version {Version}";

    public string FileName => FilePath is null ? "Untitled diagram" : Path.GetFileName(FilePath);

    public string ToolName => Placing is { } kind ? $"Place: {kind.Title()}" : Connecting ? "Connect: click the source, then the target" : "Select";

    public bool HasSelection => Selected is not null;

    public string SelectedKind => Selected switch
    {
        BpmnShape shape => shape.Kind.Title(),
        BpmnFlow { Kind: BpmnFlowKind.SequenceFlow } => IsInfrastructure ? "Link" : "Sequence flow",
        BpmnFlow => "Association",
        _ => "Nothing selected"
    };

    public string SelectedId => Selected switch
    {
        BpmnShape shape => shape.Id,
        BpmnFlow flow => flow.Id,
        _ => string.Empty
    };

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>What the diagram holds, for the status bar.</summary>
    public string Summary => $"{Diagram.Shapes.Count} element(s), {Diagram.Flows.Count(f => f.Kind == BpmnFlowKind.SequenceFlow)} flow(s), {Diagram.Flows.Count(f => f.Kind == BpmnFlowKind.Association)} association(s)";

    // ── The tools ──

    /// <summary>Chooses the kind the next click places (the same kind again, or null: back to the pointer).</summary>
    public void Place(BpmnElementKind? kind)
    {
        Connecting = false;
        Placing = Placing == kind ? null : kind;
    }

    /// <summary>Chooses the connect tool (again: back to the pointer).</summary>
    public void ToggleConnect()
    {
        Placing = null;
        Connecting = !Connecting;
    }

    [RelayCommand]
    private void SelectTool()
    {
        Placing = null;
        Connecting = false;
    }

    // ── The changes ──

    private void Record()
    {
        _renaming = null;
        _undo.Push(Diagram.Clone());
        while (_undo.Count > UndoLimit)
            _undo = Trim(_undo);
        _redo.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The newest <see cref="UndoLimit"/> states of the stack (the oldest forgotten).</summary>
    private static Stack<BpmnDiagram> Trim(Stack<BpmnDiagram> stack) => new(stack.Take(UndoLimit).Reverse());

    private void Touched(string? status = null)
    {
        IsDirty = true;
        if (status is not null)
            SetStatus(status, false);
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke();
    }

    /// <summary>Adds an element of <paramref name="kind"/> centred on (<paramref name="x"/>, <paramref name="y"/>) and selects it.</summary>
    public BpmnShape AddShape(BpmnElementKind kind, float x, float y)
    {
        Record();
        var (width, height) = kind.DefaultSize();
        var shape = new BpmnShape
        {
            Id = Diagram.NewId(Prefix(kind)),
            Kind = kind,
            Name = DefaultName(kind),
            X = Snap(x - width / 2),
            Y = Snap(y - height / 2),
            Width = width,
            Height = height
        };
        Diagram.Shapes.Add(shape);
        Selected = shape;
        Touched($"{kind.Title()} added ({shape.Id})");
        return shape;
    }

    /// <summary>
    /// Joins <paramref name="source"/> to <paramref name="target"/>: a sequence flow between two flow nodes, an
    /// association when one is a data object or an annotation. Null, with the reason, when the notation forbids it.
    /// </summary>
    public BpmnFlow? Connect(BpmnShape source, BpmnShape target, out string reason)
    {
        reason = string.Empty;
        if (ReferenceEquals(source, target))
        {
            reason = "An element cannot be joined to itself";
            return null;
        }
        var kind = source.Kind.IsArtifact() || target.Kind.IsArtifact() ? BpmnFlowKind.Association : BpmnFlowKind.SequenceFlow;
        if (kind == BpmnFlowKind.SequenceFlow && source.Kind == BpmnElementKind.EndEvent)
        {
            reason = "An end event has no outgoing flow";
            return null;
        }
        if (kind == BpmnFlowKind.SequenceFlow && target.Kind.IsStartEvent())
        {
            reason = "A start event has no incoming flow";
            return null;
        }
        if (Diagram.Flows.Any(f => f.SourceId == source.Id && f.TargetId == target.Id || kind == BpmnFlowKind.Association && f.SourceId == target.Id && f.TargetId == source.Id))
        {
            reason = $"{source.Id} and {target.Id} are already joined";
            return null;
        }
        Record();
        var flow = new BpmnFlow
        {
            Id = Diagram.NewId(kind == BpmnFlowKind.SequenceFlow ? "Flow" : "Association"),
            SourceId = source.Id,
            TargetId = target.Id,
            Kind = kind
        };
        Diagram.Flows.Add(flow);
        Selected = flow;
        Touched($"{(kind == BpmnFlowKind.SequenceFlow ? "Sequence flow" : "Association")} {flow.Id}: {source.Id} → {target.Id}");
        return flow;
    }

    /// <summary>Before a drag of elements: the diagram as it is, recorded for undo when the drag moved something.</summary>
    public void BeginMove() => _moveStart = Diagram.Clone();

    /// <summary>Moves <paramref name="shape"/> by (<paramref name="dx"/>, <paramref name="dy"/>) while it is dragged (not recorded: see <see cref="EndMove"/>).</summary>
    public void Move(BpmnShape shape, float dx, float dy)
    {
        shape.X += dx;
        shape.Y += dy;
        Changed?.Invoke();
    }

    /// <summary>After a drag: the elements snapped to the grid, the drag recorded for undo when something moved.</summary>
    public void EndMove()
    {
        if (_moveStart is null)
            return;
        var before = _moveStart;
        _moveStart = null;
        foreach (var shape in Diagram.Shapes)
        {
            shape.X = Snap(shape.X);
            shape.Y = Snap(shape.Y);
        }
        var moved = Diagram.Shapes.Any(s => before.Shape(s.Id) is { } old && (Math.Abs(old.X - s.X) > 0.5f || Math.Abs(old.Y - s.Y) > 0.5f));
        if (!moved)
        {
            Changed?.Invoke();
            return;
        }
        _renaming = null;
        _undo.Push(before);
        _redo.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        Touched();
    }

    /// <summary>Resizes <paramref name="shape"/> (a task, a sub-process, an annotation), recorded for undo.</summary>
    public void Resize(BpmnShape shape, float width, float height)
    {
        Record();
        shape.Width = Math.Max(40, Snap(width));
        shape.Height = Math.Max(30, Snap(height));
        Touched();
    }

    /// <summary>
    /// Names the element or the flow (the properties panel, a double-click on the canvas). The letters typed one after
    /// the other in the same name make one undo step.
    /// </summary>
    public void Rename(object element, string name)
    {
        var current = element switch { BpmnShape shape => shape.Name, BpmnFlow flow => flow.Name, _ => null };
        if (current is null || current == name)
            return;
        if (!ReferenceEquals(_renaming, element))
        {
            Record();
            _renaming = element;
        }
        switch (element)
        {
            case BpmnShape shape:
                shape.Name = name;
                break;
            case BpmnFlow flow:
                flow.Name = name;
                break;
        }
        Touched();
        if (ReferenceEquals(element, Selected))
            SelectedName = name;
    }

    /// <summary>Removes an element (with its flows) or a flow.</summary>
    public void Remove(object element)
    {
        switch (element)
        {
            case BpmnShape shape:
                Record();
                Diagram.Flows.RemoveAll(f => f.SourceId == shape.Id || f.TargetId == shape.Id);
                Diagram.Shapes.Remove(shape);
                if (ReferenceEquals(Selected, shape))
                    Selected = null;
                Touched($"{shape.Kind.Title()} {shape.Id} removed");
                break;
            case BpmnFlow flow:
                Record();
                Diagram.Flows.Remove(flow);
                if (ReferenceEquals(Selected, flow))
                    Selected = null;
                Touched($"{flow.Id} removed");
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        if (Selected is { } selected)
            Remove(selected);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0)
            return;
        _redo.Push(Diagram.Clone());
        Restore(_undo.Pop());
        SetStatus("Undone", false);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0)
            return;
        _undo.Push(Diagram.Clone());
        Restore(_redo.Pop());
        SetStatus("Redone", false);
    }

    private void Restore(BpmnDiagram diagram)
    {
        Diagram = diagram;
        ProcessName = diagram.Name;
        Selected = Selected switch
        {
            BpmnShape shape => diagram.Shape(shape.Id),
            BpmnFlow flow => diagram.Flows.FirstOrDefault(f => f.Id == flow.Id),
            _ => null
        };
        IsDirty = true;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke();
    }

    partial void OnSelectedChanged(object? value) =>
        SelectedName = value switch
        {
            BpmnShape shape => shape.Name,
            BpmnFlow flow => flow.Name,
            _ => string.Empty
        };

    partial void OnSelectedNameChanged(string value)
    {
        if (Selected is { } selected)
            Rename(selected, value);
    }

    partial void OnProcessNameChanged(string value)
    {
        var name = value.Trim();
        if (Diagram.Name == name)
            return;
        Diagram.Name = name;
        IsDirty = true;
    }

    partial void OnDescriptionChanged(string value)
    {
        if (!_loading)
            IsDirty = true;
    }

    // ── New, open, save ──

    /// <summary>Starts an empty diagram (the caller asks about the changes not saved first).</summary>
    [RelayCommand]
    private void New()
    {
        Diagram = IsInfrastructure ? new BpmnDiagram { Id = "Infrastructure_1", Name = "Infrastructure" } : new BpmnDiagram();
        ProcessName = Diagram.Name;
        FilePath = null;
        DiagramId = null;
        Version = 0;
        _loading = true;
        Description = string.Empty;
        ProjectId = null;
        _loading = false;
        Selected = null;
        _undo.Clear();
        _redo.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        IsDirty = false;
        OnPropertyChanged(nameof(Summary));
        SetStatus("New diagram", false);
        Changed?.Invoke();
    }

    /// <summary>The diagram as BPMN 2.0 XML.</summary>
    public string ToXml() => BpmnXml.Write(Diagram);

    /// <summary>Saves the diagram as BPMN 2.0 XML in <paramref name="path"/>.</summary>
    public async Task<bool> SaveAsync(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? DefaultDirectory);
            await File.WriteAllTextAsync(path, ToXml());
            FilePath = path;
            IsDirty = false;
            SetStatus($"Saved: {path}", false);
            ActivityLog.Info("Bpmn", $"Diagram saved: {path} ({Summary})");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Not saved: {ex.Message}", true);
            ActivityLog.Error("Bpmn", $"Diagram not saved: {path}", ex);
            return false;
        }
    }

    /// <summary>Reads a BPMN 2.0 file.</summary>
    public async Task<bool> LoadAsync(string path)
    {
        try
        {
            var xml = await File.ReadAllTextAsync(path);
            var diagram = BpmnXml.Read(xml);
            Diagram = diagram;
            ProcessName = diagram.Name;
            FilePath = path;
            DiagramId = null;
            Version = 0;
            // A file holds no project: the diagram read from it has none.
            _loading = true;
            ProjectId = null;
            _loading = false;
            Selected = null;
            _undo.Clear();
            _redo.Clear();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            IsDirty = false;
            OnPropertyChanged(nameof(Summary));
            SetStatus($"Opened: {path} ({Summary})", false);
            ActivityLog.Info("Bpmn", $"Diagram opened: {path} ({Summary})");
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            SetStatus($"Not opened: {ex.Message}", true);
            ActivityLog.Error("Bpmn", $"Diagram not opened: {path}", ex);
            return false;
        }
    }

    public void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }

    // ── The search engine (through the Kaonomi server) ──

    /// <summary>The diagrams changed last (empty search), or the diagrams found.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        try
        {
            var term = Search.Trim();
            var found = term.Length == 0 ? await _server.GetLastBpmnAsync(200, route: Route) : await _server.SearchBpmnAsync(term, 100, route: Route);
            Diagrams.Clear();
            foreach (var entry in term.Length == 0 ? found.OrderBy(d => d.Title, StringComparer.CurrentCultureIgnoreCase) : found.AsEnumerable())
                Diagrams.Add(entry);
            if (term.Length > 0)
                SetStatus($"{Diagrams.Count} diagram(s) found for \"{term}\"", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The diagrams of the search engine could not be read: " + ex.Message, true);
        }
    }

    /// <summary>
    /// Saves the diagram (its XML, the comment) in the search engine: a new document the first time (its id the slug of
    /// the name of the process; refused when a diagram of that name exists), a new version after.
    /// </summary>
    [RelayCommand]
    private async Task SaveToServerAsync()
    {
        var title = ProcessName.Trim();
        if (title.Length == 0)
        {
            SetStatus($"Give the {Subject} a name: it is the title of the diagram in the search engine", true);
            return;
        }
        var entry = new BpmnEntry
        {
            Id = DiagramId ?? BpmnEntry.Slug(title),
            AppName = "KAONOMI.Chat",
            ProjectId = ProjectId,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            Author = Environment.UserName,
            Title = title,
            Description = Description.Trim(),
            Xml = ToXml()
        };
        IsBusy = true;
        try
        {
            var result = DiagramId is null ? await _server.CreateBpmnAsync(entry, route: Route) : await _server.UpdateBpmnAsync(entry, route: Route);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("Not saved in the search engine: " + (result.Message ?? "no answer") + (DiagramId is null ? " (open it from the list to save a new version of it, or rename the process)" : ""), true);
                return;
            }
            DiagramId = result.ReturnedId;
            var saved = await _server.GetBpmnAsync(result.ReturnedId, route: Route);
            Version = saved?.Version ?? Version + 1;
            IsDirty = false;
            SetStatus($"Diagram \"{title}\" saved in the search engine (version {Version})", false);
            ActivityLog.Info("Bpmn", $"Diagram {DiagramId} saved in the search engine (version {Version}, {Summary})");
            await SearchAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("Not saved in the search engine: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens a diagram of the list.</summary>
    [RelayCommand]
    private Task OpenEntryAsync(BpmnEntry? entry) => entry?.Id is { } id ? OpenFromServerAsync(id) : Task.CompletedTask;

    /// <summary>Opens the diagram <paramref name="id"/> of the search engine (its XML read, its comment, its version).</summary>
    public async Task<bool> OpenFromServerAsync(string id)
    {
        IsBusy = true;
        try
        {
            var entry = await _server.GetBpmnAsync(id, route: Route);
            if (entry is null)
            {
                SetStatus($"No diagram {id} in the search engine", true);
                return false;
            }
            var diagram = BpmnXml.Read(entry.Xml);
            if (string.IsNullOrWhiteSpace(diagram.Name))
                diagram.Name = entry.Title;
            Diagram = diagram;
            ProcessName = diagram.Name;
            _loading = true;
            Description = entry.Description;
            ProjectId = entry.ProjectId;
            _loading = false;
            FilePath = null;
            DiagramId = entry.Id;
            Version = entry.Version;
            Selected = null;
            _undo.Clear();
            _redo.Clear();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            IsDirty = false;
            OnPropertyChanged(nameof(Summary));
            SetStatus($"Opened from the search engine: {entry.Title} (version {entry.Version}, {Summary})", false);
            ActivityLog.Info("Bpmn", $"Diagram {entry.Id} opened from the search engine (version {entry.Version})");
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or System.Xml.XmlException)
        {
            SetStatus($"The diagram {id} could not be opened: {ex.Message}", true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Marks the diagram deleted in the search engine (it is never erased); the diagram stays on the canvas.</summary>
    [RelayCommand(CanExecute = nameof(IsInEngine))]
    private async Task DeleteFromServerAsync()
    {
        if (DiagramId is not { } id)
            return;
        IsBusy = true;
        try
        {
            var result = await _server.DeleteBpmnAsync(id, route: Route);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("Not deleted: " + (result.Message ?? "no answer"), true);
                return;
            }
            SetStatus($"Diagram {id} deleted from the search engine (the canvas keeps it: save it again to put it back)", false);
            ActivityLog.Info("Bpmn", $"Diagram {id} deleted from the search engine");
            DiagramId = null;
            Version = 0;
            IsDirty = true;
            await SearchAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("Not deleted: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Helpers ──

    /// <summary>The grid the elements snap to.</summary>
    public const float Grid = 10;

    public static float Snap(float value) => MathF.Round(value / Grid) * Grid;

    private static string Prefix(BpmnElementKind kind) => kind switch
    {
        BpmnElementKind.StartEvent => "StartEvent",
        BpmnElementKind.MessageStartEvent => "MessageStartEvent",
        BpmnElementKind.TimerStartEvent => "TimerStartEvent",
        BpmnElementKind.EventCollector => "EventCollector",
        BpmnElementKind.EndEvent => "EndEvent",
        BpmnElementKind.Task => "Task",
        BpmnElementKind.UserTask => "UserTask",
        BpmnElementKind.AgentTask => "Agent",
        BpmnElementKind.VirtualAgentTask => "VirtualAgent",
        BpmnElementKind.SubProcess => "SubProcess",
        BpmnElementKind.ExclusiveGateway => "Gateway",
        BpmnElementKind.ParallelGateway => "ParallelGateway",
        BpmnElementKind.InclusiveGateway => "InclusiveGateway",
        BpmnElementKind.DataObject => "DataObject",
        BpmnElementKind.SearchEngine => "SearchEngine",
        BpmnElementKind.Graphbase => "Graphbase",
        BpmnElementKind.Database => "Database",
        BpmnElementKind.VectorDatabase => "VectorDatabase",
        BpmnElementKind.Rag => "Rag",
        BpmnElementKind.Llm => "Llm",
        BpmnElementKind.Model => "Model",
        BpmnElementKind.RestApi => "RestApi",
        BpmnElementKind.Mcp => "Mcp",
        BpmnElementKind.TextAnnotation => "TextAnnotation",
        _ => kind.ToString()
    };

    private static string DefaultName(BpmnElementKind kind) => kind switch
    {
        BpmnElementKind.Task => "Task",
        BpmnElementKind.UserTask => "User task",
        BpmnElementKind.AgentTask => "Agent",
        BpmnElementKind.VirtualAgentTask => "Virtual agent",
        BpmnElementKind.SearchEngine => "Search engine",
        BpmnElementKind.Graphbase => "Graphbase",
        BpmnElementKind.Database => "Database",
        BpmnElementKind.VectorDatabase => "Vector DB",
        BpmnElementKind.Rag => "RAG",
        BpmnElementKind.Llm => "LLM",
        BpmnElementKind.Model => "Model",
        BpmnElementKind.RestApi => "REST API",
        BpmnElementKind.Mcp => "MCP",
        BpmnElementKind.SubProcess => "Sub-process",
        BpmnElementKind.TextAnnotation => "Annotation",
        _ when kind.IsInfra() => kind.Title(),
        _ => string.Empty
    };

    /// <summary>
    /// The diagram a new window opens with: a start, a task and an end, to show the notation; for an infrastructure, a
    /// PC, the LLM gateway, an LLM in the cloud and a DGX Spark.
    /// </summary>
    private BpmnDiagram Sample()
    {
        if (IsInfrastructure)
        {
            var infra = new BpmnDiagram { Id = "Infrastructure_1", Name = "Infrastructure" };
            void Device(string id, BpmnElementKind kind, float x, float y)
            {
                var (width, height) = kind.DefaultSize();
                infra.Shapes.Add(new BpmnShape { Id = id, Kind = kind, Name = kind.Title(), X = x, Y = y, Width = width, Height = height });
            }
            Device("Pc_1", BpmnElementKind.Pc, 100, 140);
            Device("LlmGateway_1", BpmnElementKind.LlmGateway, 290, 140);
            Device("LlmCloud_1", BpmnElementKind.LlmCloud, 480, 60);
            Device("DgxSpark_1", BpmnElementKind.DgxSpark, 480, 220);
            infra.Flows.Add(new BpmnFlow { Id = "Flow_1", SourceId = "Pc_1", TargetId = "LlmGateway_1" });
            infra.Flows.Add(new BpmnFlow { Id = "Flow_2", SourceId = "LlmGateway_1", TargetId = "LlmCloud_1" });
            infra.Flows.Add(new BpmnFlow { Id = "Flow_3", SourceId = "LlmGateway_1", TargetId = "DgxSpark_1" });
            return infra;
        }

        var diagram = new BpmnDiagram { Id = "Process_1", Name = "Process" };
        diagram.Shapes.Add(new BpmnShape { Id = "StartEvent_1", Kind = BpmnElementKind.StartEvent, Name = "Start", X = 120, Y = 162, Width = 36, Height = 36 });
        diagram.Shapes.Add(new BpmnShape { Id = "Task_1", Kind = BpmnElementKind.Task, Name = "Do something", X = 220, Y = 140, Width = 100, Height = 80 });
        diagram.Shapes.Add(new BpmnShape { Id = "EndEvent_1", Kind = BpmnElementKind.EndEvent, Name = "End", X = 390, Y = 162, Width = 36, Height = 36 });
        diagram.Flows.Add(new BpmnFlow { Id = "Flow_1", SourceId = "StartEvent_1", TargetId = "Task_1" });
        diagram.Flows.Add(new BpmnFlow { Id = "Flow_2", SourceId = "Task_1", TargetId = "EndEvent_1" });
        return diagram;
    }
}

/// <summary>
/// The editor of infrastructure diagrams (<c>/infra</c>): the editor of <c>/bpmnai</c> with the machines, devices and
/// services of an installation (<see cref="BpmnNotation.Infrastructure"/>), in a window of its own.
/// </summary>
public sealed class InfraWindowViewModel : BpmnWindowViewModel
{
    /// <summary>For the designer.</summary>
    public InfraWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    public InfraWindowViewModel(KaonomiAssistantService server) : base(server, BpmnNotation.Infrastructure)
    {
    }
}
