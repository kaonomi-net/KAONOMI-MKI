using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The elements of the relation graph on a timeline (the TIMELINE button of /relation): a grid of time from the oldest
/// start date to the most recent end date (the day the timeline is built for an entity still going on), a row per
/// element with its name on the left, and across, a horizontal ribbon in the colour of the element from its start date
/// to its end date (an arrowhead at today when it has no end, a fade on the left when it has no start); an element
/// without a period (no date in the ontology) is a diamond at the date of its first relation. The double slider under
/// it chooses the dates shown (<see cref="SetRange"/>): the grid spans them, the rows outside them are left out. The
/// wheel scrolls the rows; a click selects an element (its details show on the right), a double-click opens it. The
/// ends of the ribbon of an entity can be dragged: the left end moves its start date, the right end its end date (a
/// day, written beside the end while it moves; Escape gives it up), <see cref="PeriodEdited"/> when it is let go; an
/// entity without a start or an end gets one that way. A start held against the left edge of the grid slides on into
/// the past (the grid going back with it), an end held against the right edge into the future.
/// With a cluster chosen (<see cref="SetCluster"/>), the rows are the cluster itself (first, in gold) and its members,
/// their ribbons the dates of the cluster (their probability beside their name): dragging an end changes those dates
/// in the cluster (<see cref="ClusterEdited"/>, to be saved by UPDATE), a double-click on the row of the cluster opens
/// its popup.
/// </summary>
public sealed class RelationTimeline : Control
{
    private const double NameWidth = 236;
    private const double HeaderHeight = 34;
    private const double RowHeight = 30;
    private const double RibbonHeight = 16;
    private const double RightMargin = 18;
    /// <summary>How near to the end of a ribbon the pointer must be to drag it.</summary>
    private const double Grip = 7;
    private static readonly Color ClusterColor = ClusterPen.Color;
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

    /// <summary>
    /// A row: an element of the graph (<paramref name="Node"/>), or with a cluster chosen, the cluster itself
    /// (<paramref name="Cluster"/>) or one of its members (<paramref name="Member"/>, with its element when it is in the graph).
    /// </summary>
    private sealed record Row(string Title, string? Kind, Color Color, RelationGraphNode? Node, ClusterMemberEntry? Member, ClusterEntry? Cluster,
        DateTimeOffset First, DateTimeOffset Last, bool Period, bool StartKnown, bool EndKnown, int? Probability)
    {
        /// <summary>Whether the ends of its ribbon can be dragged: an entity of the ontology, or any row of a cluster.</summary>
        public bool Editable => Period && (Member is not null || Cluster is not null || Node?.Type == RelationGraphViewModel.EntityType);
    }

    private readonly RelationGraphViewModel _vm;
    private readonly List<Row> _rows = [];
    private readonly List<Row> _shown = [];
    private ClusterEntry? _cluster;
    private DateTimeOffset _oldest = DateTimeOffset.Now, _newest = DateTimeOffset.Now, _hiddenStart = DateTimeOffset.Now;
    private DateTimeOffset _from = DateTimeOffset.Now, _to = DateTimeOffset.Now;
    private DateTimeOffset? _rangeFrom, _rangeTo;
    private double _scroll;
    private Row? _hovered;
    /// <summary>The end of a ribbon under the pointer, or being dragged: the row and which end (0: start, 1: end).</summary>
    private (Row Row, int End)? _grip, _dragging;
    /// <summary>The date of the end being dragged, as it moves.</summary>
    private DateTimeOffset _dragDate;
    /// <summary>Where the pointer is across while an end is dragged (for the slide at the edges of the grid).</summary>
    private double _dragPointerX;
    /// <summary>How far from the edge of the grid a held end starts sliding.</summary>
    private const double SlideEdge = 6;
    /// <summary>How fast the grid slides: the share of the dates shown gained each second.</summary>
    private const double SlideRate = 0.35;
    private readonly DispatcherTimer _slide = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private DateTime _lastSlide;
    /// <summary>True while a picture is taken (📷 PNG): the background is painted.</summary>
    private bool _capturing;

    /// <summary>Raised after each build with the dates of the whole timeline (oldest, newest) and the ones shown (from, to).</summary>
    public event Action<DateTimeOffset, DateTimeOffset, DateTimeOffset, DateTimeOffset>? TimeBoundsChanged;

    /// <summary>Raised by a double-click on a ribbon (or a diamond) of an element of the graph.</summary>
    public event Action<RelationGraphNode>? NodeDoubleClicked;

    /// <summary>Raised by a double-click on the row of the cluster chosen.</summary>
    public event Action<ClusterEntry>? ClusterDoubleClicked;

    /// <summary>Raised when an end of the ribbon of an entity has been dragged: the element and its new start and end dates (days).</summary>
    public event Action<RelationGraphNode, DateTime?, DateTime?>? PeriodEdited;

    /// <summary>Raised when an end of a ribbon of the cluster chosen has been dragged: its dates (or those of a member) changed, to be saved.</summary>
    public event Action<ClusterEntry>? ClusterEdited;

    public RelationTimeline(RelationGraphViewModel vm)
    {
        _vm = vm;
        Focusable = true;
        ClipToBounds = true;
        Action changed = () => Ui.OnMain(Rebuild);
        Action<RelationGraphNode> added = _ => Ui.OnMain(Rebuild);
        vm.GraphChanged += changed;
        vm.TitlesChanged += changed;
        vm.PeriodsChanged += changed;
        vm.NodeAdded += added;
        KnowledgeUi.OnGone(this, () =>
        {
            vm.GraphChanged -= changed;
            vm.TitlesChanged -= changed;
            vm.PeriodsChanged -= changed;
            vm.NodeAdded -= added;
            _slide.Stop();
        });
        Ui.When(this, vm, InvalidateVisual, nameof(RelationGraphViewModel.SelectedNode));
        SizeChanged += (_, _) =>
        {
            _scroll = Math.Clamp(_scroll, 0, MaxScroll);
            InvalidateVisual();
        };
        _slide.Tick += (_, _) => Slide();
        Rebuild();
    }

    /// <summary>Shows the dates from <paramref name="from"/> to <paramref name="to"/> only; all of them when it is the whole timeline.</summary>
    public void SetRange(DateTimeOffset from, DateTimeOffset to, bool whole)
    {
        (_rangeFrom, _rangeTo) = whole ? ((DateTimeOffset?)null, (DateTimeOffset?)null) : (from, to);
        Rebuild();
    }

    /// <summary>The cluster whose dates the timeline shows and edits; null: the elements of the graph with the dates of the ontology.</summary>
    public ClusterEntry? Cluster => _cluster;

    /// <summary>Shows the cluster <paramref name="cluster"/> (its row first, then its members) with its dates; null: back to the elements of the graph.</summary>
    public void SetCluster(ClusterEntry? cluster)
    {
        _cluster = cluster;
        _dragging = null;
        (_rangeFrom, _rangeTo) = (null, null);
        Rebuild();
    }

    // ── Building ──

    /// <summary>The rows and the dates of the timeline from the elements of the graph now (or from the cluster chosen).</summary>
    public void Rebuild()
    {
        _rows.Clear();
        var today = DateTimeOffset.Now;
        if (_cluster is { } cluster)
        {
            _rows.Add(new Row("◎ " + cluster.Name, "CLUSTER", ClusterColor, null, null, cluster,
                cluster.DateFrom ?? DateTimeOffset.MinValue, cluster.DateTo ?? today, true, cluster.DateFrom is not null, cluster.DateTo is not null, cluster.Probability));
            foreach (var member in cluster.Members)
            {
                var node = _vm.Nodes.FirstOrDefault(n => n.Type == member.WindowName && (n.ObjectId ?? n.Key) == member.ObjectId);
                var color = node is not null ? RelationGraphCanvas.ColorOf(node) : RelationGraphCanvas.ColorOf(member.WindowName);
                var kind = node is not null ? KindOf(node) : member.WindowName.ToUpperInvariant();
                var title = string.IsNullOrWhiteSpace(member.Title) ? member.ObjectId ?? string.Empty : member.Title;
                _rows.Add(new Row(title, kind, color, node, member, null,
                    member.DateFrom ?? DateTimeOffset.MinValue, member.DateTo ?? today, true, member.DateFrom is not null, member.DateTo is not null, member.Probability));
            }
        }
        else
            foreach (var node in _vm.Nodes)
            {
                var period = node.StartDate is not null || node.EndDate is not null;
                var color = RelationGraphCanvas.ColorOf(node);
                if (period)
                    _rows.Add(new Row(node.Title, KindOf(node), color, node, null, null,
                        Local(node.StartDate) ?? DateTimeOffset.MinValue, Local(node.EndDate) ?? today, true, node.StartDate is not null, node.EndDate is not null, null));
                else
                {
                    var date = DateOf(node);
                    _rows.Add(new Row(node.Title, KindOf(node), color, node, null, null, date, date, false, true, true, null));
                }
            }
        // The known dates: the oldest start date (or date) to the most recent end date (today for an open end).
        var known = _rows.Where(r => r.StartKnown).Select(r => r.First).Concat(_rows.Select(r => r.Last)).ToList();
        _oldest = known.Count == 0 ? today : known.Min();
        _newest = known.Count == 0 ? today : known.Max();
        // Nothing dated yet (a cluster without any date): the last year, so that the ends can be dragged somewhere.
        if (_rows.Count > 0 && !_rows.Any(r => r.StartKnown))
            _oldest = _newest.AddYears(-1);
        if (_newest <= _oldest)
            _newest = _oldest + TimeSpan.FromDays(1);
        // An end date without a start: the ribbon comes out of the past, from 10% before the oldest date.
        _hiddenStart = _oldest - TimeSpan.FromSeconds((_newest - _oldest).TotalSeconds * 0.1);
        for (var i = 0; i < _rows.Count; i++)
            if (!_rows[i].StartKnown)
                _rows[i] = _rows[i] with { First = _hiddenStart };
        var fullOldest = _rows.Any(r => !r.StartKnown) ? _hiddenStart : _oldest;
        (_from, _to) = (fullOldest, _newest);
        if (_rangeFrom is { } from && _rangeTo is { } to)
        {
            from = from < fullOldest ? fullOldest : from;
            to = to > _newest ? _newest : to;
            if (from < to)
                (_from, _to) = (from, to);
            else
                (_rangeFrom, _rangeTo) = (null, null);
        }
        // The rows: the cluster first, then the periods by their start, then the single dates; only the ones meeting the dates shown.
        _shown.Clear();
        _shown.AddRange(_rows
            .Where(r => r.First <= _to && r.Last >= _from)
            .OrderByDescending(r => r.Cluster is not null)
            .ThenByDescending(r => r.Period)
            .ThenBy(r => r.First)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase));
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        InvalidateVisual();
        TimeBoundsChanged?.Invoke(fullOldest, _newest, _from, _to);
    }

    /// <summary>What an element is, under its name: the entity type of an entity of the ontology, else its window.</summary>
    private static string? KindOf(RelationGraphNode node) =>
        node.Type == RelationGraphViewModel.EntityType ? node.EntityType?.ToUpperInvariant() : node.Type.ToUpperInvariant();

    /// <summary>The date of an element without a period: the date of its oldest relation (now without any).</summary>
    private static DateTimeOffset DateOf(RelationGraphNode node) =>
        node.Relations
            .Select(r => r.Versions.FirstOrDefault()?.Date ?? r.Date)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .DefaultIfEmpty(DateTimeOffset.Now)
            .Min();

    /// <summary>A date of the ontology (no time zone: a day of the calendar here) as a local date.</summary>
    private static DateTimeOffset? Local(DateTime? date) =>
        date is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d.Date, DateTimeKind.Local)) : null;

    // ── Geometry ──

    private double GridLeft => NameWidth;
    private double GridRight => Math.Max(NameWidth + 40, Bounds.Width - RightMargin);

    private double XOf(DateTimeOffset date)
    {
        var span = (_to - _from).TotalSeconds;
        var t = span <= 0 ? 0 : (date - _from).TotalSeconds / span;
        return GridLeft + t * (GridRight - GridLeft);
    }

    private double MaxScroll => Math.Max(0, _shown.Count * RowHeight - (Bounds.Height - HeaderHeight));

    private double RowTop(int index) => HeaderHeight + index * RowHeight - _scroll;

    private Row? RowAt(Point point)
    {
        if (point.Y < HeaderHeight)
            return null;
        var index = (int)Math.Floor((point.Y - HeaderHeight + _scroll) / RowHeight);
        return index >= 0 && index < _shown.Count ? _shown[index] : null;
    }

    /// <summary>The ends of the ribbon of <paramref name="row"/> on the screen (its start, its end), as drawn.</summary>
    private (double X1, double X2) EndsOf(Row row)
    {
        var x1 = Math.Max(GridLeft, XOf(row.First));
        var x2 = Math.Min(GridRight, XOf(row.Last));
        return x2 < x1 ? (x2, x1) : (x1, x2);
    }

    /// <summary>The end of a ribbon that can be dragged under <paramref name="point"/>: the start (0) or the end (1), within <see cref="Grip"/>.</summary>
    private (Row Row, int End)? GripAt(Point point)
    {
        if (RowAt(point) is not { Editable: true } row)
            return null;
        var (x1, x2) = EndsOf(row);
        var nearStart = Math.Abs(point.X - x1) <= Grip;
        var nearEnd = Math.Abs(point.X - x2) <= Grip;
        if (nearStart && nearEnd)
            return (row, point.X < (x1 + x2) / 2 ? 0 : 1);
        return nearStart ? (row, 0) : nearEnd ? (row, 1) : null;
    }

    /// <summary>The date at <paramref name="x"/> on the grid, as a day (the time dropped), within the dates shown.</summary>
    private DateTimeOffset DayAt(double x)
    {
        var t = Math.Clamp((x - GridLeft) / Math.Max(1, GridRight - GridLeft), 0, 1);
        var date = (_from + TimeSpan.FromSeconds((_to - _from).TotalSeconds * t)).ToLocalTime();
        return new DateTimeOffset(date.Date, date.Offset);
    }

    private static DateTimeOffset Day(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return new DateTimeOffset(local.Date, local.Offset);
    }

    // ── The ticks of the grid ──

    /// <summary>The ticks of the grid: a date and its caption each, about ten across whatever the span.</summary>
    private List<(DateTimeOffset Date, string Text)> Ticks()
    {
        var ticks = new List<(DateTimeOffset, string)>();
        var span = _to - _from;
        var local = _from.ToLocalTime();
        if (span.TotalDays >= 365 * 4)
        {
            var years = (int)Math.Max(1, Math.Round(span.TotalDays / 365 / 8));
            years = years <= 1 ? 1 : years <= 2 ? 2 : years <= 5 ? 5 : years <= 10 ? 10 : years <= 25 ? 25 : years <= 50 ? 50 : 100;
            var year = local.Year / years * years;
            for (var y = year; ; y += years)
            {
                var date = new DateTimeOffset(new DateTime(Math.Clamp(y, 1, 9999), 1, 1, 0, 0, 0, DateTimeKind.Local));
                if (date > _to || y >= 9999) break;
                if (date >= _from) ticks.Add((date, date.ToString("yyyy")));
            }
        }
        else if (span.TotalDays >= 60)
        {
            var months = span.TotalDays >= 365 * 2 ? 3 : span.TotalDays >= 365 ? 2 : 1;
            var date = new DateTimeOffset(new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Local));
            for (; date <= _to; date = date.AddMonths(months))
                if (date >= _from) ticks.Add((date, date.ToString("MMM yyyy")));
        }
        else if (span.TotalDays >= 2)
        {
            var days = span.TotalDays >= 30 ? 7 : span.TotalDays >= 10 ? 2 : 1;
            var date = new DateTimeOffset(local.Date, local.Offset);
            for (; date <= _to; date = date.AddDays(days))
                if (date >= _from) ticks.Add((date, date.ToString("dd MMM")));
        }
        else
        {
            var hours = span.TotalHours >= 24 ? 4 : span.TotalHours >= 8 ? 2 : 1;
            var date = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset);
            for (; date <= _to; date = date.AddHours(hours))
                if (date >= _from) ticks.Add((date, date.ToString("HH:mm")));
        }
        return ticks;
    }

    // ── Drawing ──

    private bool IsSelected(Row row) => row.Node is not null && ReferenceEquals(row.Node, _vm.SelectedNode) && row.Cluster is null;

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var light = Paint.IsLight;
        Paint.Rect(context, new Rect(size), _capturing ? (light ? Paint.Rgb(0.97, 0.98, 0.99) : Paint.Rgb(0.015, 0.022, 0.03)) : Colors.Transparent);
        var grid = Paint.Alpha(Paint.Edge, light ? 0.5 : 0.35);
        var text = Paint.TextBright;
        var muted = Paint.Muted;
        // The rows: a band one in two, the name on the left, the ribbon across.
        var first = Math.Max(0, (int)Math.Floor(_scroll / RowHeight));
        var last = Math.Min(_shown.Count - 1, (int)Math.Ceiling((_scroll + size.Height) / RowHeight));
        for (var i = first; i <= last; i++)
        {
            var row = _shown[i];
            var top = RowTop(i);
            if (row.Cluster is not null)
                Paint.Rect(context, new Rect(0, top, size.Width, RowHeight), Paint.Alpha(ClusterColor, 0.08));
            else if (i % 2 == 1)
                Paint.Rect(context, new Rect(0, top, size.Width, RowHeight), Paint.Alpha(Paint.Field, light ? 0.35 : 0.5));
            var selected = IsSelected(row);
            var hovered = ReferenceEquals(row, _hovered);
            var color = row.Color;
            // The name, its kind under it in the colour of the element (the probability of a member of the cluster beside it).
            var title = row.Title.Length > 34 ? row.Title[..34] + "…" : row.Title;
            Paint.Write(context, title, new Point(10, top + 1), 12, selected ? Paint.Amber : row.Cluster is not null ? ClusterColor : text, NameWidth - 20, 1,
                selected || row.Cluster is not null ? Paint.SemiBold : Paint.Body);
            var kind = row.Kind ?? string.Empty;
            if (row.Probability is { } probability)
                kind = (kind.Length > 0 ? kind + " · " : string.Empty) + $"{probability} %";
            if (kind.Length > 0)
                Paint.Write(context, kind, new Point(10, top + 17), 9, Paint.Lighten(color, light ? -0.1 : 0.2), NameWidth - 20);
            var y = top + RowHeight / 2;
            if (row.Period)
            {
                var (x1, x2) = EndsOf(row);
                var dragged = _dragging is { } d && ReferenceEquals(d.Row, row) ? d.End : -1;
                if (dragged == 0)
                    x1 = Math.Clamp(XOf(_dragDate), GridLeft, Math.Max(GridLeft, x2 - 2));
                else if (dragged == 1)
                    x2 = Math.Clamp(XOf(_dragDate), Math.Min(GridRight, x1 + 2), GridRight);
                var fill = Paint.Alpha(color, selected ? 1 : hovered ? 0.95 : 0.8);
                var rect = new Rect(x1, y - RibbonHeight / 2, Math.Max(3, x2 - x1), RibbonHeight);
                if (!row.StartKnown && dragged != 0 && row.First >= _from)
                {
                    // No start: the first 40% of the ribbon fade in from the left.
                    const int fades = 12;
                    var fadeWidth = rect.Width * 0.4;
                    for (var f = 0; f < fades; f++)
                        Paint.Rect(context, new Rect(rect.X + fadeWidth * f / fades, rect.Y, fadeWidth / fades + 0.5, rect.Height), Paint.Fade(fill, (f + 1.0) / (fades + 1)));
                    Paint.Rect(context, new Rect(rect.X + fadeWidth, rect.Y, rect.Width - fadeWidth, rect.Height), fill);
                }
                else
                    Paint.Rect(context, rect, fill);
                if ((row.StartKnown || dragged == 0) && row.First >= _from)
                    Paint.Rect(context, new Rect(x1 - 1.5, y - RibbonHeight / 2 - 3, 3, RibbonHeight + 6), fill);
                if (!row.EndKnown && dragged != 1 && row.Last <= _to)
                {
                    // No end: it goes on, an arrowhead at today.
                    Paint.Polygon(context, [new Point(x2, y - RibbonHeight / 2 - 4), new Point(x2 + 10, y), new Point(x2, y + RibbonHeight / 2 + 4)], fill);
                }
                if (selected || hovered)
                    Paint.Outline(context, Paint.Grow(rect, 1), selected ? Paint.Amber : Paint.TextBright, 1.5);
                // The grips at the ends of a ribbon that can be dragged (the one under the pointer, or dragged, in amber).
                if (row.Editable && (hovered || dragged >= 0))
                {
                    var gripped = _dragging is { } g && ReferenceEquals(g.Row, row) ? g.End : _grip is { } h && ReferenceEquals(h.Row, row) ? h.End : -1;
                    foreach (var (gx, end) in new[] { (x1, 0), (x2, 1) })
                    {
                        var box = new Rect(gx - 3, y - RibbonHeight / 2 - 5, 6, RibbonHeight + 10);
                        Paint.Rect(context, box, gripped == end ? Paint.Amber : Paint.TextBright);
                        Paint.Outline(context, box, Paint.Field);
                    }
                }
                if (dragged >= 0)
                {
                    // The day the end is dragged to, beside it.
                    var day = _dragDate.ToString("yyyy-MM-dd");
                    var dayWidth = Paint.Measure(day, 11, typeface: Paint.SemiBold).Width;
                    var dayX = dragged == 0 ? Math.Max(GridLeft, x1 - dayWidth - 8) : Math.Min(GridRight - dayWidth, x2 + 8);
                    Paint.Rect(context, new Rect(dayX - 3, y - RibbonHeight / 2 - 15, dayWidth + 6, 15), Paint.Field);
                    Paint.Write(context, day, new Point(dayX, y - RibbonHeight / 2 - 15), 11, Paint.Amber, typeface: Paint.SemiBold);
                }
                // The dates at the ends of the ribbon, when there is room between them.
                var dates = (row.StartKnown ? row.First.ToLocalTime().ToString("yyyy-MM-dd") : "?") + " → " + (row.EndKnown ? row.Last.ToLocalTime().ToString("yyyy-MM-dd") : "today");
                var width = Paint.Measure(dates, 10).Width;
                var datesSize = Paint.Measure(dates, 10);
                if (rect.Width > width + 12)
                    Paint.Write(context, dates, new Point(rect.X + 6 + (row.StartKnown ? 0 : rect.Width * 0.4), y - datesSize.Height / 2), 10,
                        light ? Paint.Rgb(0.1, 0.1, 0.12, 0.9) : Paint.Rgb(0, 0, 0, 0.75));
                else if (x2 + 14 + width < GridRight)
                    Paint.Write(context, dates, new Point(x2 + 14, y - datesSize.Height / 2), 10, muted);
            }
            else
            {
                var x = XOf(row.First);
                var r = selected ? 8.0 : hovered ? 7.0 : 6.0;
                Paint.Polygon(context, [new Point(x, y - r), new Point(x + r, y), new Point(x, y + r), new Point(x - r, y)], Paint.Alpha(color, selected ? 1 : 0.85));
                if (selected || hovered)
                    Paint.Polyline(context, [new Point(x, y - r - 1), new Point(x + r + 1, y), new Point(x, y + r + 1), new Point(x - r - 1, y)], selected ? Paint.Amber : Paint.TextBright, 1.5, closed: true);
                var date = row.First.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                Paint.Write(context, date, new Point(x + 12, y - 7), 10, muted);
            }
        }
        // The grid: a line and its date at each tick, over the rows (the header hides the rows scrolled under it).
        Paint.Rect(context, new Rect(0, 0, size.Width, HeaderHeight), Paint.Field);
        Paint.Line(context, new Point(0, HeaderHeight), new Point(size.Width, HeaderHeight), Paint.Edge);
        Paint.Line(context, new Point(NameWidth - 4, 0), new Point(NameWidth - 4, size.Height), Paint.Edge);
        Paint.Write(context, _cluster is null ? "ELEMENT" : "CLUSTER · MEMBERS", new Point(10, 12), 11, Paint.Amber, typeface: Paint.SemiBold);
        foreach (var (date, caption) in Ticks())
        {
            var x = XOf(date);
            Paint.Line(context, new Point(x, HeaderHeight), new Point(x, size.Height), grid);
            Paint.Write(context, caption, new Point(x + 4, 13), 11, muted);
        }
        // Today, when it is within the dates shown.
        var today = DateTimeOffset.Now;
        if (today >= _from && today <= _to)
        {
            var x = XOf(today);
            Paint.Dashed(context, new Point(x, HeaderHeight), new Point(x, size.Height), Paint.Alpha(Paint.Amber, 0.8), 1, 6);
            Paint.Write(context, "today", new Point(x + 4, 0), 10, Paint.Amber, typeface: Paint.SemiBold);
        }
        // The ends of the dates shown.
        var fromText = _from.ToLocalTime().ToString("yyyy-MM-dd");
        var toText = _to.ToLocalTime().ToString("yyyy-MM-dd");
        Paint.Write(context, fromText, new Point(GridLeft + 2, 0), 10, Paint.Cyan, typeface: Paint.SemiBold);
        Paint.Write(context, toText, new Point(GridRight - Paint.Measure(toText, 10, typeface: Paint.SemiBold).Width - 2, 0), 10, Paint.Cyan, typeface: Paint.SemiBold);
        if (_shown.Count == 0)
            Paint.Write(context,
                _rows.Count == 0 ? (_cluster is null ? "No element in the graph: search some relations, or add an entity with +" : "This cluster has no member")
                    : "No element in these dates: move the handles of the slider under the timeline", new Point(GridLeft + 12, HeaderHeight + 18), 12, muted);
        // The scrollbar, when the rows do not all fit.
        if (MaxScroll > 0)
        {
            var track = size.Height - HeaderHeight;
            var thumb = Math.Max(24, track * track / (_shown.Count * RowHeight));
            var thumbY = HeaderHeight + (track - thumb) * (_scroll / MaxScroll);
            Paint.Rect(context, new Rect(size.Width - 6, thumbY, 4, thumb), Paint.Alpha(Paint.Muted, 0.6));
        }
    }

    /// <summary>Saves the timeline as it is shown (its cluster, dates and scrolling), its background painted, to <paramref name="path"/>.</summary>
    public bool SaveCapture(string path)
    {
        _capturing = true;
        try
        {
            return KnowledgeUi.SavePng(this, path, 1);
        }
        finally
        {
            _capturing = false;
            InvalidateVisual();
        }
    }

    // ── The pointer ──

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _scroll = Math.Clamp(_scroll - e.Delta.Y * RowHeight * 3, 0, MaxScroll);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _dragging is not null)
        {
            // The drag given up: the ribbon goes back where it was (and the grid, when it slid).
            _dragging = null;
            _slide.Stop();
            Rebuild();
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        var position = e.GetPosition(this);
        if (e.ClickCount >= 2)
        {
            if (GripAt(position) is null && RowAt(position) is { } opened)
            {
                if (opened.Cluster is { } cluster)
                    ClusterDoubleClicked?.Invoke(cluster);
                else if (opened.Node is { } node)
                    NodeDoubleClicked?.Invoke(node);
            }
            return;
        }
        _vm.SelectedNode = RowAt(position)?.Node;
        if (GripAt(position) is { } taken)
        {
            // An end of a ribbon taken: it follows the pointer until it is let go.
            _dragging = taken;
            _dragDate = taken.End == 0 ? taken.Row.First : taken.Row.Last;
            _dragPointerX = position.X;
            Focus();
            e.Pointer.Capture(this);
            _lastSlide = DateTime.UtcNow;
            _slide.Start();
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (e.InitialPressMouseButton != MouseButton.Left || _dragging is not { } done)
            return;
        _dragging = null;
        _slide.Stop();
        EndDrag(done.Row, done.End, Day(_dragDate));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_dragging is { } drag)
        {
            // The end being dragged: a day of the grid, the start kept before the end and the end after the start.
            var day = DayAt(position.X);
            if (drag.End == 0 && drag.Row.EndKnown && day > drag.Row.Last)
                day = Day(drag.Row.Last);
            if (drag.End == 1 && drag.Row.StartKnown && day < drag.Row.First)
                day = Day(drag.Row.First);
            _dragDate = day;
            _dragPointerX = position.X;
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        var hovered = RowAt(position);
        var grip = GripAt(position);
        var gripChanged = (grip?.Row, grip?.End) != (_grip?.Row, _grip?.End);
        if (!ReferenceEquals(hovered, _hovered) || gripChanged)
        {
            _hovered = hovered;
            _grip = grip;
            Cursor = grip is not null ? ResizeCursor : hovered is null ? ArrowCursor : HandCursor;
            ToolTip.SetTip(this, grip is { } g ? $"{g.Row.Title} · drag: {(g.End == 0 ? "the start date" : "the end date")}{(_cluster is null ? " (saved at once)" : " (then UPDATE to save the cluster)")}"
                : hovered is null ? null
                : hovered.Cluster is not null ? $"Cluster · {hovered.Title[2..]} · double-click: edit · drag an end of the ribbon: its date"
                : $"{hovered.Kind ?? hovered.Title} · {hovered.Title} · click: details · double-click: open" + (hovered.Editable ? " · drag an end of the ribbon: its date" : string.Empty));
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hovered is not null && _dragging is null)
        {
            _hovered = null;
            _grip = null;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// While an end is held against an edge of the grid, the grid slides: the start held at the left goes on into the
    /// past, the end held at the right into the future, the dates shown growing that way (the slider under the
    /// timeline follows when the end is let go).
    /// </summary>
    private void Slide()
    {
        var now = DateTime.UtcNow;
        var delta = (now - _lastSlide).TotalSeconds;
        _lastSlide = now;
        if (_dragging is not { } drag)
        {
            _slide.Stop();
            return;
        }
        var span = (_to - _from).TotalSeconds;
        var shift = TimeSpan.FromSeconds(span * SlideRate * delta);
        if (drag.End == 0 && _dragPointerX <= GridLeft + SlideEdge)
        {
            _from -= shift;
            (_rangeFrom, _rangeTo) = (_from, _to);
            _dragDate = DayAt(GridLeft);
            if (drag.Row.EndKnown && _dragDate > drag.Row.Last)
                _dragDate = Day(drag.Row.Last);
            InvalidateVisual();
        }
        else if (drag.End == 1 && _dragPointerX >= GridRight - SlideEdge)
        {
            _to += shift;
            (_rangeFrom, _rangeTo) = (_from, _to);
            _dragDate = DayAt(GridRight);
            if (drag.Row.StartKnown && _dragDate < drag.Row.First)
                _dragDate = Day(drag.Row.First);
            InvalidateVisual();
        }
    }

    /// <summary>
    /// An end of the ribbon of <paramref name="row"/> let go at <paramref name="day"/>: with a cluster chosen, its
    /// dates (or those of the member) change in the cluster (<see cref="ClusterEdited"/>); else the dates of the
    /// entity are reported (<see cref="PeriodEdited"/>). Nothing when the day is the one it had.
    /// </summary>
    private void EndDrag(Row row, int end, DateTimeOffset day)
    {
        var before = (row.StartKnown ? Day(row.First) : (DateTimeOffset?)null, row.EndKnown ? Day(row.Last) : (DateTimeOffset?)null);
        var start = end == 0 ? day : before.Item1;
        var finish = end == 1 ? day : before.Item2;
        if ((start, finish) == before)
        {
            Rebuild();
            return;
        }
        if (row.Cluster is { } cluster)
        {
            cluster.DateFrom = start;
            cluster.DateTo = finish;
            ClusterEdited?.Invoke(cluster);
            Rebuild();
        }
        else if (row.Member is { } member && _cluster is { } owner)
        {
            member.DateFrom = start;
            member.DateTo = finish;
            ClusterEdited?.Invoke(owner);
            Rebuild();
        }
        else if (row.Node is { } node)
        {
            PeriodEdited?.Invoke(node, start?.DateTime.Date, finish?.DateTime.Date);
            InvalidateVisual();
        }
    }
}
