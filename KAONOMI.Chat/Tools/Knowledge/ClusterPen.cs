using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

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
    event Action<IReadOnlyList<ClusterMemberEntry>, IReadOnlyList<Point>>? ClusterDrawn;

    /// <summary>Raised by a double-click on the name of a cluster drawn by "Show clusters".</summary>
    event Action<ClusterEntry>? ClusterDoubleClicked;
}

/// <summary>
/// The pen of the clusters of a canvas (<see cref="IClusterCanvas"/>), with the behaviour of the one of the graph of
/// relations: the drawing in gold while the button is held, closed and filled faintly when it is released; the stored
/// clusters drawn around their elements (the drawing of the pen smoothed, or, when the elements have moved out of it,
/// the hull of their boxes), their name at their top. Also the geometry shared with the graph (simplify, smooth, hull,
/// inside).
/// </summary>
internal sealed class ClusterPen
{
    public static readonly Color Color = Paint.Hex("#D4A017");

    private readonly List<Point> _path = [];
    private bool _closed;
    private readonly List<(ClusterEntry Cluster, Rect Label)> _labels = [];

    public bool Active { get; set; }
    public bool ShowClusters { get; set; }
    public IReadOnlyList<ClusterEntry> Clusters { get; set; } = [];

    /// <summary>True while the button is held down.</summary>
    public bool IsDown { get; private set; }

    public bool HasDrawing => _path.Count > 0;

    public IReadOnlyList<Point> Path => _path;

    /// <summary>A new drawing from <paramref name="at"/> (screen).</summary>
    public void Begin(Point at)
    {
        _path.Clear();
        _path.Add(at);
        IsDown = true;
        _closed = false;
    }

    /// <summary>The drawing goes on to <paramref name="at"/>; false when the point is too close to the last one (nothing to redraw).</summary>
    public bool Extend(Point at)
    {
        if (!IsDown || (_path.Count > 0 && Paint.Distance(at, _path[^1]) <= 2))
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
    public bool Contains(Point screen) => _path.Count >= 3 && Inside(_path, screen);

    /// <summary>The drawing kept in the cluster: simplified (3 px), in the coordinates of the canvas.</summary>
    public List<Point> Simplified(Func<Point, Point> toWorld) => Simplify(_path, 3).Select(toWorld).ToList();

    public void Clear()
    {
        _path.Clear();
        IsDown = false;
        _closed = false;
    }

    /// <summary>The drawing of the pen: a line of gold, the shape filled faintly once it is closed.</summary>
    public void DrawPen(DrawingContext context) => DrawPath(context, _path, _closed);

    /// <summary>A drawing of the pen (<paramref name="path"/>, in screen coordinates), filled faintly when <paramref name="closed"/>.</summary>
    public static void DrawPath(DrawingContext context, IReadOnlyList<Point> path, bool closed)
    {
        if (path.Count < 2)
            return;
        if (closed && path.Count >= 3)
            Paint.Polygon(context, path, Paint.Alpha(Color, 0.1));
        Paint.Polyline(context, path, Paint.Alpha(Color, closed ? 0.9 : 0.75), 3, closed);
    }

    /// <summary>
    /// The stored clusters, when "Show clusters" is ticked, around their elements present in the canvas
    /// (<paramref name="boxOf"/>: the box of a member in the coordinates of the canvas, null when it is not there).
    /// </summary>
    public void DrawClusters(DrawingContext context, Func<ClusterMemberEntry, Rect?> boxOf, Func<Point, Point> toScreen, double zoom)
    {
        _labels.Clear();
        if (!ShowClusters)
            return;
        foreach (var cluster in Clusters)
        {
            var boxes = cluster.Members.Select(boxOf).Where(b => b is not null).Select(b => b!.Value).ToList();
            if (boxes.Count == 0)
                continue;
            var drawn = cluster.Path.Select(p => new Point(p.X, p.Y)).ToList();
            List<Point> shape;
            if (drawn.Count >= 3 && boxes.All(b => Inside(drawn, Paint.Center(b))))
                shape = drawn;
            else
            {
                // The elements moved out of the drawing: the hull of their boxes, with some room around.
                var corners = new List<Point>();
                foreach (var b in boxes.Select(b => Paint.Grow(b, 28)))
                    corners.AddRange([b.TopLeft, b.TopRight, b.BottomRight, b.BottomLeft]);
                shape = Hull(corners);
            }
            if (DrawShape(context, shape, cluster.Name, toScreen, zoom) is { } label)
                _labels.Add((cluster, label));
        }
    }

    /// <summary>A cluster drawn: its shape smoothed, filled faintly, its name at its top; the box of the name (null: not drawn).</summary>
    public static Rect? DrawShape(DrawingContext context, List<Point> shape, string name, Func<Point, Point> toScreen, double zoom)
    {
        var smooth = Smooth(shape, 3).Select(toScreen).ToList();
        if (smooth.Count < 3)
            return null;
        Paint.Polygon(context, smooth, Paint.Alpha(Color, 0.07));
        Paint.Polyline(context, smooth, Paint.Alpha(Color, 0.85), 2.5, closed: true);
        var top = smooth.OrderBy(p => p.Y).First();
        var size = Math.Clamp(13 * zoom, 9, 18);
        var measured = Paint.Measure(name, size, typeface: Paint.SemiBold);
        var at = new Point(top.X - measured.Width / 2, top.Y - 6 - measured.Height);
        Paint.Write(context, name, at, size, Color, typeface: Paint.SemiBold);
        return new Rect(at.X - 4, at.Y - 3, measured.Width + 8, measured.Height + 6);
    }

    /// <summary>The cluster whose name is under <paramref name="screen"/>, or null.</summary>
    public ClusterEntry? LabelAt(Point screen) => _labels.LastOrDefault(l => l.Label.Contains(screen)).Cluster;

    // ── Geometry ──

    /// <summary>The drawing with its useless points left out (Ramer–Douglas–Peucker, <paramref name="tolerance"/> pixels).</summary>
    public static List<Point> Simplify(IReadOnlyList<Point> points, double tolerance)
    {
        if (points.Count < 3)
            return [.. points];
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            var farthest = -1;
            var distance = 0.0;
            for (var i = first + 1; i < last; i++)
            {
                var d = Paint.DistanceToSegment(points[i], points[first], points[last]);
                if (d > distance)
                {
                    distance = d;
                    farthest = i;
                }
            }
            if (farthest >= 0 && distance > tolerance)
            {
                keep[farthest] = true;
                stack.Push((first, farthest));
                stack.Push((farthest, last));
            }
        }
        var result = new List<Point>();
        for (var i = 0; i < points.Count; i++)
            if (keep[i])
                result.Add(points[i]);
        return result;
    }

    /// <summary>A closed shape smoothed (Chaikin, <paramref name="passes"/> times: the corners cut in two).</summary>
    public static List<Point> Smooth(IReadOnlyList<Point> shape, int passes)
    {
        var points = new List<Point>(shape);
        for (var pass = 0; pass < passes && points.Count >= 3; pass++)
        {
            var next = new List<Point>(points.Count * 2);
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                next.Add(Paint.Lerp(a, b, 0.25));
                next.Add(Paint.Lerp(a, b, 0.75));
            }
            points = next;
        }
        return points;
    }

    /// <summary>The convex hull of <paramref name="points"/> (monotone chain), counter-clockwise.</summary>
    public static List<Point> Hull(List<Point> points)
    {
        points = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (points.Count < 3)
            return points;
        static double Cross(Point o, Point a, Point b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var lower = new List<Point>();
        foreach (var p in points)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }
        var upper = new List<Point>();
        for (var i = points.Count - 1; i >= 0; i--)
        {
            var p = points[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        return [.. lower, .. upper];
    }

    public static bool Inside(IReadOnlyList<Point> shape, Point p)
    {
        var inside = false;
        for (int i = 0, j = shape.Count - 1; i < shape.Count; j = i++)
        {
            var a = shape[i];
            var b = shape[j];
            if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    // ── The toolbar ──

    /// <summary>
    /// The buttons of the toolbar of <paramref name="canvas"/>: ✎ PEN (draw around some elements, then name their
    /// cluster in the popup of <see cref="ClusterDialog"/>) and "Show clusters" (the stored clusters of its category).
    /// </summary>
    public static Control[] Toolbar(Shell shell, IClusterCanvas canvas)
    {
        var pen = new ToggleButton { Content = "✎ PEN", Focusable = false };
        ToolTip.SetTip(pen, "Draw around some elements with the mouse: the elements inside the drawing make a cluster, named in the popup that opens · right click or Escape: remove the drawing · click again: back to the pointer");
        var show = new CheckBox { Content = "Show clusters", Focusable = false };
        ToolTip.SetTip(show, "The clusters stored on the server drawn around their elements (the drawing of the pen, smoothed, or the hull of the elements when they moved out of it) · double-click a name: open the cluster");
        void Reload() => _ = LoadAsync(canvas);
        pen.IsCheckedChanged += (_, _) =>
        {
            var on = pen.IsChecked == true;
            canvas.PenMode = on;
            if (!on)
                canvas.ClearPen();
        };
        show.IsCheckedChanged += (_, _) =>
        {
            var on = show.IsChecked == true;
            canvas.ShowClusters = on;
            if (on)
                Reload();
        };
        canvas.ClusterDrawn += (members, path) =>
            ClusterDialog.Open(shell, members, path, canvas.ClusterCategory, saved: () =>
            {
                canvas.ClearPen();
                pen.IsChecked = false;
                if (show.IsChecked == true)
                    Reload();
                else
                    show.IsChecked = true;
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
            Ui.OnMain(() => canvas.SetClusters(clusters));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            ActivityLog.Error("Cluster", "The clusters could not be read", ex);
        }
    }
}
