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
/// The mind mapping window (<c>/mindmap</c>): a central topic (the root, whose text is the title of the map) and
/// its branches, each node with a text, a note and, on the children of the root, the colour of its branch (inherited
/// below). A child is added under the selection (Tab, the + handle, the toolbar), a sibling next to it (Enter); a
/// node dragged onto another one moves under it with its branch; LAYOUT lays the tree out again. Every change can
/// be undone (<see cref="UndoCommand"/>: a copy of the map before each change). The map is saved as FreeMind XML
/// (<see cref="MindmapXml"/>, <c>.mm</c>, read by FreeMind, Freeplane, XMind…), in <c>~/kaonomi/mindmap</c> by
/// default, and read back from any <c>.mm</c> file. It is also saved in the search engine through the Kaonomi server
/// (the <c>mindmap</c> index: its XML, a comment, its version; its id the slug of its title), where the maps are
/// searched (<see cref="SearchCommand"/>) and opened again.
/// </summary>
public sealed partial class MindmapWindowViewModel : ViewModelBase, IUnsavedChanges, IProjectDocument
{
    /// <summary>The project the map is attached to (the id of a project, chosen by its name in the header of the window), or null; saved with it in the search engine.</summary>
    [ObservableProperty]
    private string? _projectId;

    /// <summary>The editor always holds a map.</summary>
    public bool HasProjectDocument => true;

    partial void OnProjectIdChanged(string? value)
    {
        if (!_loading)
            IsDirty = true;
    }

    private const int UndoLimit = 100;
    private const float LevelGap = 200;
    private const float SiblingGap = 48;

    private Stack<MindmapDiagram> _undo = new();
    private readonly Stack<MindmapDiagram> _redo = new();
    private MindmapDiagram? _moveStart;
    private readonly KaonomiAssistantService _server;
    /// <summary>True while a map is being loaded: the texts set then are not changes.</summary>
    private bool _loading;
    /// <summary>The node whose text or note is being typed: one undo step for the whole text, not one per letter.</summary>
    private object? _typing;

    /// <summary>For the designer.</summary>
    public MindmapWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    public MindmapWindowViewModel(KaonomiAssistantService server)
    {
        _server = server;
        Map = Sample();
        _loading = true;
        Title = Map.Title;
        _loading = false;
        Selected = Map.Root;
        Status = "A new map: Tab adds a child of the selection, Enter a sibling, double-click renames, drag a node onto another to move it there";
        _ = SearchAsync();
    }

    /// <summary>Where the maps are saved by default.</summary>
    public static string DefaultDirectory => Path.Combine(ChatSettings.KaonomiHome, "mindmap");

    /// <summary>Where the pictures of the maps (PNG) are saved: the Pictures folder of the system, kaonomi/mindmap.</summary>
    public static string PicturesDirectory => Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "mindmap");

    /// <summary>The map edited.</summary>
    public MindmapDiagram Map { get; private set; }

    /// <summary>Raised after any change of the map (the canvas draws it again).</summary>
    public event Action? Changed;

    /// <summary>The file of the map (null until it is saved).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName))]
    private string? _filePath;

    /// <summary>True when the map has changes not saved.</summary>
    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>True while the server is called.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The node selected, null when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedInfo), nameof(CanDeleteSelected), nameof(IsBranchSelected))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand), nameof(AddChildOfSelectionCommand), nameof(AddSiblingOfSelectionCommand))]
    private MindmapNode? _selected;

    /// <summary>The text of the node selected, edited in the properties panel.</summary>
    [ObservableProperty]
    private string _selectedText = string.Empty;

    /// <summary>The note of the node selected, edited in the properties panel.</summary>
    [ObservableProperty]
    private string _selectedNote = string.Empty;

    /// <summary>The title of the map: the text of its root.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>The comment written with the map (saved with it in the search engine).</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>The id of the map in the search engine (the slug of its title), null until it is saved there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapInfo), nameof(IsInEngine))]
    [NotifyCanExecuteChangedFor(nameof(DeleteFromServerCommand))]
    private string? _mapId;

    /// <summary>Its version in the search engine (1 at its creation, one more at each save).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapInfo))]
    private int _version;

    /// <summary>The search typed for the maps of the search engine (empty: the ones changed last).</summary>
    [ObservableProperty]
    private string _search = string.Empty;

    /// <summary>The maps of the search engine found by the search (or changed last).</summary>
    public ObservableCollection<MindmapEntry> Maps { get; } = [];

    public string FileName => FilePath is null ? "Untitled map" : Path.GetFileName(FilePath);

    public bool HasSelection => Selected is not null;

    /// <summary>True when a child of the root is selected: its branch has a colour of its own.</summary>
    public bool IsBranchSelected => Selected is { } node && Map.Depth(node) == 1;

    public bool CanDeleteSelected => Selected is { ParentId: not null };

    public string SelectedInfo => Selected switch
    {
        null => "Nothing selected",
        { ParentId: null } => "Central topic · " + Selected.Id,
        _ => $"Level {Map.Depth(Selected)} · {Selected.Id} · {Map.Children(Selected.Id).Count} child(ren)"
    };

    public bool IsInEngine => MapId is not null;

    public string MapInfo => MapId is null ? "Not in the search engine yet" : $"{MapId} · version {Version}";

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>What the map holds, for the status bar.</summary>
    public string Summary
    {
        get
        {
            var depth = Map.Nodes.Count == 0 ? 0 : Map.Nodes.Max(Map.Depth);
            var branches = Map.Root is { } root ? Map.Children(root.Id).Count : 0;
            return $"{Map.Nodes.Count} node(s), {branches} branch(es), depth {depth}";
        }
    }

    // ── The changes ──

    private void Record()
    {
        _typing = null;
        _undo.Push(Map.Clone());
        while (_undo.Count > UndoLimit)
            _undo = new Stack<MindmapDiagram>(_undo.Take(UndoLimit).Reverse());
        _redo.Clear();
        NotifyUndo();
    }

    private void NotifyUndo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void Touched(string? status = null)
    {
        IsDirty = true;
        if (status is not null)
            SetStatus(status, false);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedInfo));
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds a child under <paramref name="parent"/> (the selection when null), placed beside it on its side (for the
    /// root: right and left in turn, or the <paramref name="side"/> asked, +1 right, -1 left), and selects it.
    /// </summary>
    public MindmapNode? AddChild(MindmapNode? parent = null, string text = "New topic", int side = 0)
    {
        parent ??= Selected ?? Map.Root;
        if (parent is null)
            return null;
        Record();
        var siblings = Map.Children(parent.Id);
        if (parent.ParentId is null)
        {
            if (side == 0)
                side = siblings.Count % 2 == 0 ? 1 : -1;
        }
        else
        {
            var grandParent = Map.Parent(parent);
            side = grandParent is null || parent.X >= grandParent.X ? 1 : -1;
        }
        // Under the last child of that side (the root has children on both sides).
        var sameSide = siblings.Where(s => parent.ParentId is null ? Math.Sign(s.X - parent.X) == side : true).ToList();
        var node = new MindmapNode
        {
            Id = Map.NewId(),
            Text = text,
            ParentId = parent.Id,
            X = parent.X + side * LevelGap,
            Y = sameSide.Count == 0 ? parent.Y : sameSide.Max(s => s.Y) + SiblingGap,
            Color = parent.ParentId is null ? MindmapDiagram.Palette[siblings.Count % MindmapDiagram.Palette.Length] : null
        };
        Map.Nodes.Add(node);
        Selected = node;
        Touched($"Added under \"{parent.Text}\": {node.Id}");
        return node;
    }

    /// <summary>Adds a sibling after <paramref name="node"/> (the selection when null); a child of the root when the root is selected.</summary>
    public MindmapNode? AddSibling(MindmapNode? node = null, string text = "New topic")
    {
        node ??= Selected;
        if (node is null)
            return null;
        if (node.ParentId is null || Map.Parent(node) is not { } parent)
            return AddChild(node, text);
        Record();
        var siblings = Map.Children(parent.Id);
        var index = siblings.IndexOf(node);
        var created = new MindmapNode
        {
            Id = Map.NewId(),
            Text = text,
            ParentId = parent.Id,
            X = node.X,
            Y = node.Y + SiblingGap,
            Color = parent.ParentId is null ? MindmapDiagram.Palette[siblings.Count % MindmapDiagram.Palette.Length] : null
        };
        // Kept right after its sibling in the order of the map (the file, the layout).
        var position = Map.Nodes.IndexOf(node) + 1;
        Map.Nodes.Insert(Math.Min(position, Map.Nodes.Count), created);
        // The siblings below are pushed down, so that the new one has its place.
        foreach (var below in siblings.Skip(index + 1).SelectMany(Map.Subtree))
            below.Y += SiblingGap;
        Selected = created;
        Touched($"Added beside \"{node.Text}\": {created.Id}");
        return created;
    }

    /// <summary>Moves <paramref name="node"/> (with its branch) under <paramref name="parent"/>; refused for the root, or under its own branch.</summary>
    public bool Reparent(MindmapNode node, MindmapNode parent, out string reason)
    {
        reason = string.Empty;
        if (node.ParentId is null)
        {
            reason = "The central topic stays where it is";
            return false;
        }
        if (ReferenceEquals(node, parent) || Map.Subtree(node).Contains(parent))
        {
            reason = "A topic cannot move under its own branch";
            return false;
        }
        if (node.ParentId == parent.Id)
            return false;
        Record();
        node.ParentId = parent.Id;
        node.Color = parent.ParentId is null ? node.Color ?? MindmapDiagram.Palette[Map.Children(parent.Id).Count % MindmapDiagram.Palette.Length] : null;
        Touched($"\"{node.Text}\" moved under \"{parent.Text}\"");
        return true;
    }

    /// <summary>Before a drag: the map as it is, recorded for undo when the drag moved something.</summary>
    public void BeginMove() => _moveStart = Map.Clone();

    /// <summary>Moves <paramref name="node"/> and its branch by (<paramref name="dx"/>, <paramref name="dy"/>) while dragged.</summary>
    public void Move(MindmapNode node, float dx, float dy)
    {
        foreach (var moved in Map.Subtree(node))
        {
            moved.X += dx;
            moved.Y += dy;
        }
        Changed?.Invoke();
    }

    /// <summary>After a drag: recorded for undo when something moved.</summary>
    public void EndMove()
    {
        if (_moveStart is null)
            return;
        var before = _moveStart;
        _moveStart = null;
        var moved = Map.Nodes.Any(n => before.Node(n.Id) is { } old && (Math.Abs(old.X - n.X) > 0.5f || Math.Abs(old.Y - n.Y) > 0.5f));
        if (!moved)
        {
            Changed?.Invoke();
            return;
        }
        _typing = null;
        _undo.Push(before);
        _redo.Clear();
        NotifyUndo();
        Touched();
    }

    /// <summary>Names the node (the properties panel, a double-click). The letters typed one after the other make one undo step.</summary>
    public void Rename(MindmapNode node, string text)
    {
        if (node.Text == text)
            return;
        if (!ReferenceEquals(_typing, node))
        {
            Record();
            _typing = node;
        }
        node.Text = text;
        if (node.ParentId is null)
        {
            _loading = true;
            Title = text;
            _loading = false;
        }
        Touched();
        if (ReferenceEquals(node, Selected))
            SelectedText = text;
    }

    /// <summary>Writes the note of the node.</summary>
    public void SetNote(MindmapNode node, string note)
    {
        if (node.Note == note)
            return;
        if (!(_typing is string typing && typing == node.Id + "/note"))
        {
            Record();
            _typing = node.Id + "/note";
        }
        node.Note = note;
        IsDirty = true;
        if (ReferenceEquals(node, Selected))
            SelectedNote = note;
    }

    /// <summary>Gives the branch of a child of the root its colour (#rrggbb).</summary>
    public void SetColor(MindmapNode node, string color)
    {
        if (node.Color == color)
            return;
        Record();
        node.Color = color;
        Touched($"Branch \"{node.Text}\": {color}");
    }

    /// <summary>Removes a node and its branch (never the root).</summary>
    public void Remove(MindmapNode node)
    {
        if (node.ParentId is null)
        {
            SetStatus("The central topic cannot be removed: rename it, or start a new map", true);
            return;
        }
        Record();
        var parent = Map.Parent(node);
        var gone = Map.Subtree(node).ToList();
        foreach (var n in gone)
            Map.Nodes.Remove(n);
        if (Selected is { } selected && gone.Contains(selected))
            Selected = parent;
        Touched($"\"{node.Text}\" removed ({gone.Count} node(s))");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddChildOfSelection() => AddChild();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void AddSiblingOfSelection() => AddSibling();

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (Selected is { } selected)
            Remove(selected);
    }

    /// <summary>Lays the tree out again around the root.</summary>
    [RelayCommand]
    private void Layout()
    {
        Record();
        Map.Layout();
        Touched("Map laid out again");
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undo.Count == 0)
            return;
        _redo.Push(Map.Clone());
        Restore(_undo.Pop());
        SetStatus("Undone", false);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redo.Count == 0)
            return;
        _undo.Push(Map.Clone());
        Restore(_redo.Pop());
        SetStatus("Redone", false);
    }

    private void Restore(MindmapDiagram map)
    {
        Map = map;
        _typing = null;
        _loading = true;
        Title = map.Title;
        _loading = false;
        Selected = Selected is { } selected ? map.Node(selected.Id) ?? map.Root : map.Root;
        IsDirty = true;
        NotifyUndo();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedInfo));
        Changed?.Invoke();
    }

    partial void OnSelectedChanged(MindmapNode? value)
    {
        _loading = true;
        SelectedText = value?.Text ?? string.Empty;
        SelectedNote = value?.Note ?? string.Empty;
        _loading = false;
    }

    partial void OnSelectedTextChanged(string value)
    {
        if (!_loading && Selected is { } selected)
            Rename(selected, value);
    }

    partial void OnSelectedNoteChanged(string value)
    {
        if (!_loading && Selected is { } selected)
            SetNote(selected, value);
    }

    partial void OnTitleChanged(string value)
    {
        if (!_loading && Map.Root is { } root)
            Rename(root, value);
    }

    partial void OnDescriptionChanged(string value)
    {
        if (!_loading)
            IsDirty = true;
    }

    // ── New, open, save ──

    private void Reset(MindmapDiagram map, string? filePath, string? mapId, int version, string description, string? projectId = null)
    {
        Map = map;
        _loading = true;
        Title = map.Title;
        Description = description;
        ProjectId = projectId;
        _loading = false;
        FilePath = filePath;
        MapId = mapId;
        Version = version;
        Selected = map.Root;
        _undo.Clear();
        _redo.Clear();
        _typing = null;
        NotifyUndo();
        IsDirty = false;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedInfo));
        Changed?.Invoke();
    }

    /// <summary>Starts a map with its central topic alone (the caller asks about the changes not saved first).</summary>
    [RelayCommand]
    private void New()
    {
        Reset(MindmapDiagram.New("Central topic"), null, null, 0, string.Empty);
        SetStatus("New map: name the central topic, then Tab adds its first branch", false);
    }

    /// <summary>The map as FreeMind XML.</summary>
    public string ToXml() => MindmapXml.Write(Map);

    /// <summary>Saves the map as FreeMind XML in <paramref name="path"/>.</summary>
    public async Task<bool> SaveAsync(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? DefaultDirectory);
            await File.WriteAllTextAsync(path, ToXml());
            FilePath = path;
            IsDirty = false;
            SetStatus($"Saved: {path}", false);
            ActivityLog.Info("Mindmap", $"Map saved: {path} ({Summary})");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"Not saved: {ex.Message}", true);
            ActivityLog.Error("Mindmap", $"Map not saved: {path}", ex);
            return false;
        }
    }

    /// <summary>Reads a FreeMind file.</summary>
    public async Task<bool> LoadAsync(string path)
    {
        try
        {
            var xml = await File.ReadAllTextAsync(path);
            Reset(MindmapXml.Read(xml), path, null, 0, string.Empty);
            SetStatus($"Opened: {path} ({Summary})", false);
            ActivityLog.Info("Mindmap", $"Map opened: {path} ({Summary})");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            SetStatus($"Not opened: {ex.Message}", true);
            ActivityLog.Error("Mindmap", $"Map not opened: {path}", ex);
            return false;
        }
    }

    public void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }

    // ── The search engine (through the Kaonomi server) ──

    /// <summary>The maps changed last (empty search), or the maps found.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        try
        {
            var term = Search.Trim();
            var found = term.Length == 0 ? await _server.GetLastMindmapAsync(200) : await _server.SearchMindmapAsync(term, 100);
            Maps.Clear();
            foreach (var entry in term.Length == 0 ? found.OrderBy(d => d.Title, StringComparer.CurrentCultureIgnoreCase) : found.AsEnumerable())
                Maps.Add(entry);
            if (term.Length > 0)
                SetStatus($"{Maps.Count} map(s) found for \"{term}\"", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The maps of the search engine could not be read: " + ex.Message, true);
        }
    }

    /// <summary>
    /// Saves the map (its XML, the comment) in the search engine: a new document the first time (its id the slug of the
    /// title; refused when a map of that title exists), a new version after.
    /// </summary>
    [RelayCommand]
    private async Task SaveToServerAsync()
    {
        var title = Title.Trim();
        if (title.Length == 0)
        {
            SetStatus("Name the central topic: it is the title of the map in the search engine", true);
            return;
        }
        var entry = new MindmapEntry
        {
            Id = MapId ?? MindmapEntry.Slug(title),
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
            var result = MapId is null ? await _server.CreateMindmapAsync(entry) : await _server.UpdateMindmapAsync(entry);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("Not saved in the search engine: " + (result.Message ?? "no answer") + (MapId is null ? " (open it from the list to save a new version of it, or rename the central topic)" : ""), true);
                return;
            }
            MapId = result.ReturnedId;
            var saved = await _server.GetMindmapAsync(result.ReturnedId);
            Version = saved?.Version ?? Version + 1;
            IsDirty = false;
            SetStatus($"Map \"{title}\" saved in the search engine (version {Version})", false);
            ActivityLog.Info("Mindmap", $"Map {MapId} saved in the search engine (version {Version}, {Summary})");
            await SearchAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            SetStatus("Not saved in the search engine: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens a map of the list.</summary>
    [RelayCommand]
    private Task OpenEntryAsync(MindmapEntry? entry) => entry?.Id is { } id ? OpenFromServerAsync(id) : Task.CompletedTask;

    /// <summary>Opens the map <paramref name="id"/> of the search engine (its XML read, its comment, its version).</summary>
    public async Task<bool> OpenFromServerAsync(string id)
    {
        IsBusy = true;
        try
        {
            var entry = await _server.GetMindmapAsync(id);
            if (entry is null)
            {
                SetStatus($"No map {id} in the search engine", true);
                return false;
            }
            var map = MindmapXml.Read(entry.Xml);
            Reset(map, null, entry.Id, entry.Version, entry.Description, entry.ProjectId);
            SetStatus($"Opened from the search engine: {entry.Title} (version {entry.Version}, {Summary})", false);
            ActivityLog.Info("Mindmap", $"Map {entry.Id} opened from the search engine (version {entry.Version})");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or System.Xml.XmlException)
        {
            SetStatus($"The map {id} could not be opened: {ex.Message}", true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Marks the map deleted in the search engine (it is never erased); the map stays on the canvas.</summary>
    [RelayCommand(CanExecute = nameof(IsInEngine))]
    private async Task DeleteFromServerAsync()
    {
        if (MapId is not { } id)
            return;
        IsBusy = true;
        try
        {
            var result = await _server.DeleteMindmapAsync(id);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("Not deleted: " + (result.Message ?? "no answer"), true);
                return;
            }
            SetStatus($"Map {id} deleted from the search engine (the canvas keeps it: save it again to put it back)", false);
            ActivityLog.Info("Mindmap", $"Map {id} deleted from the search engine");
            MapId = null;
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

    /// <summary>The map a new window opens with: a topic and three branches, to show the look.</summary>
    private static MindmapDiagram Sample()
    {
        var map = MindmapDiagram.New("My idea");
        map.Nodes.Add(new MindmapNode { Id = "ID_2", Text = "Why", ParentId = "ID_1", Color = MindmapDiagram.Palette[0] });
        map.Nodes.Add(new MindmapNode { Id = "ID_3", Text = "How", ParentId = "ID_1", Color = MindmapDiagram.Palette[1] });
        map.Nodes.Add(new MindmapNode { Id = "ID_4", Text = "When", ParentId = "ID_1", Color = MindmapDiagram.Palette[2] });
        map.Nodes.Add(new MindmapNode { Id = "ID_5", Text = "First step", ParentId = "ID_3" });
        map.Layout();
        return map;
    }
}
