using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// A canvas where clusters are drawn with the pen as in the graph of relations (/bpmnai, /infra, /mindmap): the pen
/// draws around some elements, the elements inside make a cluster (<see cref="ClusterDrawn"/>), the stored clusters of
/// its <see cref="ClusterCategory"/> are drawn around their elements, a double-click on the name of one opens it.
/// </summary>
public interface IClusterCanvas
{
    /// <summary>The category of the clusters drawn there (<see cref="ClusterCategories"/>).</summary>
    string ClusterCategory { get; }

    /// <summary>True while the pen is chosen: a left drag draws around the elements instead of moving them.</summary>
    bool PenMode { get; set; }

    /// <summary>True when the stored clusters are drawn around their elements.</summary>
    bool ShowClusters { get; set; }

    /// <summary>The stored clusters of <see cref="ClusterCategory"/> (only those with elements in the canvas are drawn).</summary>
    void SetClusters(IReadOnlyList<ClusterEntry> clusters);

    /// <summary>Removes the drawing of the pen (its cluster was saved or its popup closed).</summary>
    void ClearPen();

    /// <summary>Raised when a drawing closes around at least one element, with the drawing simplified, in the coordinates of the canvas.</summary>
    event Action<IReadOnlyList<ClusterMemberEntry>, IReadOnlyList<Vector2>>? ClusterDrawn;

    /// <summary>Raised by a double-click on the name of a cluster drawn by "Show clusters".</summary>
    event Action<ClusterEntry>? ClusterDoubleClicked;
}

/// <summary>
/// The pen of the clusters of a canvas (<see cref="IClusterCanvas"/>), with the behaviour of the one of the graph of
/// relations (<see cref="RelationGraphCanvas"/>): the drawing in gold while the button is held, closed and filled
/// faintly when it is released; the stored clusters drawn around their elements (the drawing of the pen smoothed, or,
/// when the elements have moved out of it, the hull of their boxes), their name at their top.
/// </summary>
internal sealed class ClusterPen
{
    public static readonly Color Color = new("#D4A017");

    private readonly List<Vector2> _path = [];
    private bool _closed;
    private readonly List<(ClusterEntry Cluster, Rect2 Label)> _labels = [];

    public bool Active { get; set; }
    public bool ShowClusters { get; set; }
    public IReadOnlyList<ClusterEntry> Clusters { get; set; } = [];

    /// <summary>True while the button is held down.</summary>
    public bool IsDown { get; private set; }

    public bool HasDrawing => _path.Count > 0;

    /// <summary>A new drawing from <paramref name="at"/> (screen).</summary>
    public void Begin(Vector2 at)
    {
        _path.Clear();
        _path.Add(at);
        IsDown = true;
        _closed = false;
    }

    /// <summary>The drawing goes on to <paramref name="at"/>; false when the point is too close to the last one (nothing to redraw).</summary>
    public bool Extend(Vector2 at)
    {
        if (!IsDown || (_path.Count > 0 && at.DistanceTo(_path[^1]) <= 2))
            return false;
        _path.Add(at);
        return true;
    }

    /// <summary>The button released: the drawing closes (false when it is too short to hold anything).</summary>
    public bool Lift()
    {
        IsDown = false;
        _closed = _path.Count >= 3;
        return _closed;
    }

    /// <summary>True when <paramref name="screen"/> is inside the drawing (a ray cast along x).</summary>
    public bool Contains(Vector2 screen) => _path.Count >= 3 && RelationGraphCanvas.Inside(_path, screen);

    /// <summary>The drawing kept in the cluster: simplified (3 px), in the coordinates of the canvas.</summary>
    public List<Vector2> Simplified(Func<Vector2, Vector2> toWorld) => RelationGraphCanvas.Simplify(_path, 3).Select(toWorld).ToList();

    public void Clear()
    {
        _path.Clear();
        IsDown = false;
        _closed = false;
    }

    /// <summary>The drawing of the pen: a line of gold, the shape filled faintly once it is closed.</summary>
    public void DrawPen(CanvasItem canvas)
    {
        if (_path.Count < 2)
            return;
        if (_closed && _path.Count >= 3)
            canvas.DrawColoredPolygon(_path.ToArray(), Color with { A = 0.1f });
        canvas.DrawPolyline(_path.ToArray(), Color with { A = _closed ? 0.9f : 0.75f }, 3, antialiased: true);
        if (_closed)
            canvas.DrawLine(_path[^1], _path[0], Color with { A = 0.9f }, 3, antialiased: true);
    }

    /// <summary>
    /// The stored clusters, when "Show clusters" is ticked, around their elements present in the canvas
    /// (<paramref name="boxOf"/>: the box of a member in the coordinates of the canvas, null when it is not there).
    /// </summary>
    public void DrawClusters(CanvasItem canvas, Func<ClusterMemberEntry, Rect2?> boxOf, Func<Vector2, Vector2> toScreen, float zoom)
    {
        _labels.Clear();
        if (!ShowClusters)
            return;
        foreach (var cluster in Clusters)
        {
            var boxes = cluster.Members.Select(boxOf).OfType<Rect2>().ToList();
            if (boxes.Count == 0)
                continue;
            var drawn = cluster.Path.Select(p => new Vector2((float)p.X, (float)p.Y)).ToList();
            List<Vector2> shape;
            if (drawn.Count >= 3 && boxes.All(b => RelationGraphCanvas.Inside(drawn, b.GetCenter())))
                shape = drawn;
            else
            {
                // The elements moved out of the drawing: the hull of their boxes, with some room around.
                var corners = new List<Vector2>();
                foreach (var b in boxes.Select(b => b.Grow(28)))
                    corners.AddRange([b.Position, new Vector2(b.End.X, b.Position.Y), b.End, new Vector2(b.Position.X, b.End.Y)]);
                shape = RelationGraphCanvas.Hull(corners);
            }
            var smooth = RelationGraphCanvas.Smooth(shape, 3).Select(toScreen).ToArray();
            if (smooth.Length < 3)
                continue;
            canvas.DrawColoredPolygon(smooth, Color with { A = 0.07f });
            canvas.DrawPolyline([.. smooth, smooth[0]], Color with { A = 0.85f }, 2.5f, antialiased: true);
            var top = smooth.OrderBy(p => p.Y).First();
            var font = Eve.Eve.Display;
            var size = (int)Math.Clamp(13 * zoom, 9, 18);
            var width = font.GetStringSize(cluster.Name, fontSize: size).X;
            var height = font.GetHeight(size);
            canvas.DrawString(font, new Vector2(top.X - width / 2, top.Y - 6), cluster.Name, HorizontalAlignment.Left, -1, size, Color);
            _labels.Add((cluster, new Rect2(top.X - width / 2 - 4, top.Y - 6 - height, width + 8, height + 6)));
        }
    }

    /// <summary>The cluster whose name is under <paramref name="screen"/>, or null.</summary>
    public ClusterEntry? LabelAt(Vector2 screen) => _labels.LastOrDefault(l => l.Label.HasPoint(screen)).Cluster;

    /// <summary>
    /// The buttons of the toolbar of <paramref name="canvas"/>: ✎ PEN (draw around some elements, then name their
    /// cluster in the popup of <see cref="ClusterDialog"/>) and "Show clusters" (the stored clusters of its category).
    /// </summary>
    public static Control[] Toolbar(Shell shell, IClusterCanvas canvas)
    {
        var pen = new Button
        {
            Text = "✎ PEN",
            ToggleMode = true,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Draw around some elements with the mouse: the elements inside the drawing make a cluster, named in the popup that opens · right click or Escape: remove the drawing · click again: back to the pointer"
        };
        var show = new CheckBox
        {
            Text = "Show clusters",
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "The clusters stored on the server drawn around their elements (the drawing of the pen, smoothed, or the hull of the elements when they moved out of it) · double-click a name: open the cluster"
        };
        void Reload() => _ = LoadAsync(canvas);
        pen.Toggled += on =>
        {
            canvas.PenMode = on;
            if (!on)
                canvas.ClearPen();
        };
        show.Toggled += on =>
        {
            canvas.ShowClusters = on;
            if (on)
                Reload();
        };
        canvas.ClusterDrawn += (members, path) =>
            ClusterDialog.Open(shell, members, path, canvas.ClusterCategory, saved: () =>
            {
                canvas.ClearPen();
                pen.ButtonPressed = false;
                if (show.ButtonPressed)
                    Reload();
                else
                    show.ButtonPressed = true;
            }, closed: canvas.ClearPen);
        canvas.ClusterDoubleClicked += cluster => ClusterDialog.Open(shell, cluster, saved: Reload);
        return [pen, show];
    }

    /// <summary>Reads the clusters of the category of <paramref name="canvas"/> (the newest 300, the deleted ones left out).</summary>
    private static async Task LoadAsync(IClusterCanvas canvas)
    {
        if (KaonomiAssistantService.Shared is not { } server)
            return;
        try
        {
            var clusters = (await server.GetLastClustersAsync(300)).Where(c => c.Category == canvas.ClusterCategory).ToList();
            Bind.OnMain(() =>
            {
                if (canvas is not GodotObject node || GodotObject.IsInstanceValid(node))
                    canvas.SetClusters(clusters);
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            ActivityLog.Error("Cluster", "The clusters could not be read", ex);
        }
    }
}
