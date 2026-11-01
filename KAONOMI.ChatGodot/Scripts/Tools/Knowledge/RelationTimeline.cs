using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

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
/// their ribbons the dates of the cluster (<c>dateFrom</c>, <c>dateTo</c> of the cluster and of each member, with their
/// probability beside their name): dragging an end changes those dates in the cluster (<see cref="ClusterEdited"/>, to
/// be saved by UPDATE), a double-click on the row of the cluster opens its popup.
/// </summary>
public partial class RelationTimeline : Control
{
    private const float NameWidth = 236;
    private const float HeaderHeight = 34;
    private const float RowHeight = 30;
    private const float RibbonHeight = 16;
    private const float RightMargin = 18;
    /// <summary>How near to the end of a ribbon the pointer must be to drag it.</summary>
    private const float Grip = 7;
    private static readonly Color ClusterColor = new("#D4A017");

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
    private float _scroll;
    private Row? _hovered;
    /// <summary>The end of a ribbon under the pointer, or being dragged: the row and which end (0: start, 1: end).</summary>
    private (Row Row, int End)? _grip, _dragging;
    /// <summary>The date of the end being dragged, as it moves.</summary>
    private DateTimeOffset _dragDate;
    /// <summary>Where the pointer is across while an end is dragged (for the slide at the edges of the grid).</summary>
    private float _dragPointerX;
    /// <summary>How far from the edge of the grid a held end starts sliding.</summary>
    private const float SlideEdge = 6;
    /// <summary>How fast the grid slides: the share of the dates shown gained each second.</summary>
    private const float SlideRate = 0.35f;

    private static readonly SystemFont SemiBold = new() { FontNames = Eve.Eve.Body.FontNames, FontWeight = 600, Fallbacks = [Eve.Eve.Emoji] };
    /// <summary>A copy drawn for a picture (📷 PNG): it paints its background (nothing behind it in its viewport).</summary>
    private bool _snapshot;

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

    public RelationTimeline() : this(new RelationGraphViewModel())
    {
    }

    public RelationTimeline(RelationGraphViewModel vm)
    {
        _vm = vm;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.Click;
        ClipContents = true;
        Action changed = () => Bind.OnMain(Rebuild);
        Action<RelationGraphNode> added = _ => Bind.OnMain(Rebuild);
        vm.GraphChanged += changed;
        vm.TitlesChanged += changed;
        vm.PeriodsChanged += changed;
        vm.NodeAdded += added;
        Bind.OnGone(this, () =>
        {
            vm.GraphChanged -= changed;
            vm.TitlesChanged -= changed;
            vm.PeriodsChanged -= changed;
            vm.NodeAdded -= added;
        });
        Bind.When(this, vm, QueueRedraw, nameof(RelationGraphViewModel.SelectedNode));
        Resized += QueueRedraw;
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
                var color = node is not null ? RelationGraph3D.ColorOf(node) : RelationGraphCanvas.ColorOf(member.WindowName);
                var kind = node is not null ? KindOf(node) : member.WindowName.ToUpperInvariant();
                var title = string.IsNullOrWhiteSpace(member.Title) ? member.ObjectId : member.Title;
                _rows.Add(new Row(title, kind, color, node, member, null,
                    member.DateFrom ?? DateTimeOffset.MinValue, member.DateTo ?? today, true, member.DateFrom is not null, member.DateTo is not null, member.Probability));
            }
        }
        else
            foreach (var node in _vm.Nodes)
            {
                var period = node.StartDate is not null || node.EndDate is not null;
                var color = RelationGraph3D.ColorOf(node);
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
        QueueRedraw();
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

    private float GridLeft => NameWidth;
    private float GridRight => Math.Max(NameWidth + 40, Size.X - RightMargin);

    private float XOf(DateTimeOffset date)
    {
        var span = (_to - _from).TotalSeconds;
        var t = span <= 0 ? 0 : (date - _from).TotalSeconds / span;
        return GridLeft + (float)t * (GridRight - GridLeft);
    }

    private float MaxScroll => Math.Max(0, _shown.Count * RowHeight - (Size.Y - HeaderHeight));

    private float RowTop(int index) => HeaderHeight + index * RowHeight - _scroll;

    private Row? RowAt(Vector2 point)
    {
        if (point.Y < HeaderHeight)
            return null;
        var index = (int)MathF.Floor((point.Y - HeaderHeight + _scroll) / RowHeight);
        return index >= 0 && index < _shown.Count ? _shown[index] : null;
    }

    /// <summary>The ends of the ribbon of <paramref name="row"/> on the screen (its start, its end), as drawn.</summary>
    private (float X1, float X2) EndsOf(Row row)
    {
        var x1 = Math.Max(GridLeft, XOf(row.First));
        var x2 = Math.Min(GridRight, XOf(row.Last));
        return x2 < x1 ? (x2, x1) : (x1, x2);
    }

    /// <summary>The end of a ribbon that can be dragged under <paramref name="point"/>: the start (0) or the end (1), within <see cref="Grip"/>.</summary>
    private (Row Row, int End)? GripAt(Vector2 point)
    {
        if (RowAt(point) is not { Editable: true } row)
            return null;
        var (x1, x2) = EndsOf(row);
        var nearStart = MathF.Abs(point.X - x1) <= Grip;
        var nearEnd = MathF.Abs(point.X - x2) <= Grip;
        if (nearStart && nearEnd)
            return (row, point.X < (x1 + x2) / 2 ? 0 : 1);
        return nearStart ? (row, 0) : nearEnd ? (row, 1) : null;
    }

    /// <summary>The date at <paramref name="x"/> on the grid, as a day (the time dropped), within the dates shown.</summary>
    private DateTimeOffset DayAt(float x)
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

    public override void _Draw()
    {
        var font = Eve.Eve.Body;
        var light = Eve.Eve.IsLight;
        if (_snapshot)
            DrawRect(new Rect2(Vector2.Zero, Size), light ? new Color(0.97f, 0.98f, 0.99f) : new Color(0.015f, 0.022f, 0.03f));
        var grid = Eve.Eve.Edge with { A = light ? 0.5f : 0.35f };
        var text = Eve.Eve.TextBright;
        var muted = Eve.Eve.Muted;
        // The rows: a band one in two, the name on the left, the ribbon across.
        var first = Math.Max(0, (int)MathF.Floor(_scroll / RowHeight));
        var last = Math.Min(_shown.Count - 1, (int)MathF.Ceiling((_scroll + Size.Y) / RowHeight));
        for (var i = first; i <= last; i++)
        {
            var row = _shown[i];
            var top = RowTop(i);
            if (row.Cluster is not null)
                DrawRect(new Rect2(0, top, Size.X, RowHeight), ClusterColor with { A = 0.08f });
            else if (i % 2 == 1)
                DrawRect(new Rect2(0, top, Size.X, RowHeight), Eve.Eve.Field with { A = light ? 0.35f : 0.25f });
            var selected = IsSelected(row);
            var hovered = ReferenceEquals(row, _hovered);
            var color = row.Color;
            // The name, its kind under it in the colour of the element (the probability of a member of the cluster beside it).
            var title = row.Title.Length > 34 ? row.Title[..34] + "…" : row.Title;
            DrawString(selected || row.Cluster is not null ? SemiBold : font, new Vector2(10, top + 13), title, HorizontalAlignment.Left, NameWidth - 20, 12, selected ? Eve.Eve.Amber : row.Cluster is not null ? ClusterColor : text);
            var kind = row.Kind ?? string.Empty;
            if (row.Probability is { } probability)
                kind = (kind.Length > 0 ? kind + " · " : string.Empty) + $"{probability} %";
            if (kind.Length > 0)
                DrawString(font, new Vector2(10, top + 25), kind, HorizontalAlignment.Left, NameWidth - 20, 9, color.Lightened(light ? -0.1f : 0.2f));
            var y = top + RowHeight / 2;
            if (row.Period)
            {
                var (x1, x2) = EndsOf(row);
                var dragged = _dragging is { } d && ReferenceEquals(d.Row, row) ? d.End : -1;
                if (dragged == 0)
                    x1 = Math.Clamp(XOf(_dragDate), GridLeft, x2 - 2);
                else if (dragged == 1)
                    x2 = Math.Clamp(XOf(_dragDate), x1 + 2, GridRight);
                var fill = color with { A = selected ? 1f : hovered ? 0.95f : 0.8f };
                var rect = new Rect2(x1, y - RibbonHeight / 2, Math.Max(3, x2 - x1), RibbonHeight);
                if (!row.StartKnown && dragged != 0 && row.First >= _from)
                {
                    // No start: the first 40% of the ribbon fade in from the left.
                    const int fades = 12;
                    var fadeWidth = rect.Size.X * 0.4f;
                    for (var f = 0; f < fades; f++)
                        DrawRect(new Rect2(rect.Position.X + fadeWidth * f / fades, rect.Position.Y, fadeWidth / fades + 0.5f, rect.Size.Y), fill with { A = fill.A * (f + 1) / (fades + 1) });
                    DrawRect(new Rect2(rect.Position.X + fadeWidth, rect.Position.Y, rect.Size.X - fadeWidth, rect.Size.Y), fill);
                }
                else
                    DrawRect(rect, fill);
                if ((row.StartKnown || dragged == 0) && row.First >= _from)
                    DrawRect(new Rect2(x1 - 1.5f, y - RibbonHeight / 2 - 3, 3, RibbonHeight + 6), fill);
                if (!row.EndKnown && dragged != 1 && row.Last <= _to)
                {
                    // No end: it goes on, an arrowhead at today.
                    DrawColoredPolygon([new Vector2(x2, y - RibbonHeight / 2 - 4), new Vector2(x2 + 10, y), new Vector2(x2, y + RibbonHeight / 2 + 4)], fill);
                }
                if (selected || hovered)
                    DrawRect(new Rect2(rect.Position - new Vector2(1, 1), rect.Size + new Vector2(2, 2)), selected ? Eve.Eve.Amber : Eve.Eve.TextBright, false, 1.5f);
                // The grips at the ends of a ribbon that can be dragged (the one under the pointer, or dragged, in amber).
                if (row.Editable && (hovered || dragged >= 0))
                {
                    var gripped = _dragging is { } g && ReferenceEquals(g.Row, row) ? g.End : _grip is { } h && ReferenceEquals(h.Row, row) ? h.End : -1;
                    foreach (var (gx, end) in new[] { (x1, 0), (x2, 1) })
                    {
                        DrawRect(new Rect2(gx - 3, y - RibbonHeight / 2 - 5, 6, RibbonHeight + 10), gripped == end ? Eve.Eve.Amber : Eve.Eve.TextBright);
                        DrawRect(new Rect2(gx - 3, y - RibbonHeight / 2 - 5, 6, RibbonHeight + 10), Eve.Eve.Field, false, 1);
                    }
                }
                if (dragged >= 0)
                {
                    // The day the end is dragged to, beside it.
                    var day = _dragDate.ToString("yyyy-MM-dd");
                    var dayWidth = font.GetStringSize(day, HorizontalAlignment.Left, -1, 11).X;
                    var dayX = dragged == 0 ? Math.Max(GridLeft, x1 - dayWidth - 8) : Math.Min(GridRight - dayWidth, x2 + 8);
                    DrawRect(new Rect2(dayX - 3, y - RibbonHeight / 2 - 14, dayWidth + 6, 15), Eve.Eve.Field);
                    DrawString(SemiBold, new Vector2(dayX, y - RibbonHeight / 2 - 3), day, HorizontalAlignment.Left, -1, 11, Eve.Eve.Amber);
                }
                // The dates at the ends of the ribbon, when there is room between them.
                var dates = (row.StartKnown ? row.First.ToLocalTime().ToString("yyyy-MM-dd") : "?") + " → " + (row.EndKnown ? row.Last.ToLocalTime().ToString("yyyy-MM-dd") : "today");
                var width = font.GetStringSize(dates, HorizontalAlignment.Left, -1, 10).X;
                if (rect.Size.X > width + 12)
                    DrawString(font, new Vector2(rect.Position.X + 6 + (row.StartKnown ? 0 : rect.Size.X * 0.4f), y + 4), dates, HorizontalAlignment.Left, -1, 10, light ? new Color(0.1f, 0.1f, 0.12f, 0.9f) : new Color(0, 0, 0, 0.75f));
                else if (x2 + 14 + width < GridRight)
                    DrawString(font, new Vector2(x2 + 14, y + 4), dates, HorizontalAlignment.Left, -1, 10, muted);
            }
            else
            {
                var x = XOf(row.First);
                var r = selected ? 8f : hovered ? 7f : 6f;
                DrawColoredPolygon([new Vector2(x, y - r), new Vector2(x + r, y), new Vector2(x, y + r), new Vector2(x - r, y)], color with { A = selected ? 1 : 0.85f });
                if (selected || hovered)
                    DrawPolyline([new Vector2(x, y - r - 1), new Vector2(x + r + 1, y), new Vector2(x, y + r + 1), new Vector2(x - r - 1, y), new Vector2(x, y - r - 1)], selected ? Eve.Eve.Amber : Eve.Eve.TextBright, 1.5f, true);
                var date = row.First.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                DrawString(font, new Vector2(x + 12, y + 4), date, HorizontalAlignment.Left, -1, 10, muted);
            }
        }
        // The grid: a line and its date at each tick, over the rows (the header hides the rows scrolled under it).
        DrawRect(new Rect2(0, 0, Size.X, HeaderHeight), Eve.Eve.Field);
        DrawLine(new Vector2(0, HeaderHeight), new Vector2(Size.X, HeaderHeight), Eve.Eve.Edge, 1);
        DrawLine(new Vector2(NameWidth - 4, 0), new Vector2(NameWidth - 4, Size.Y), Eve.Eve.Edge, 1);
        DrawString(SemiBold, new Vector2(10, 21), _cluster is null ? "ELEMENT" : "CLUSTER · MEMBERS", HorizontalAlignment.Left, -1, 11, Eve.Eve.Amber);
        foreach (var (date, caption) in Ticks())
        {
            var x = XOf(date);
            DrawLine(new Vector2(x, HeaderHeight), new Vector2(x, Size.Y), grid, 1);
            DrawString(font, new Vector2(x + 4, 21), caption, HorizontalAlignment.Left, -1, 11, muted);
        }
        // Today, when it is within the dates shown.
        var today = DateTimeOffset.Now;
        if (today >= _from && today <= _to)
        {
            var x = XOf(today);
            DrawDashedLine(new Vector2(x, HeaderHeight), new Vector2(x, Size.Y), Eve.Eve.Amber with { A = 0.8f }, 1, 6);
            DrawString(SemiBold, new Vector2(x + 4, 10), "today", HorizontalAlignment.Left, -1, 10, Eve.Eve.Amber);
        }
        // The ends of the dates shown.
        var fromText = _from.ToLocalTime().ToString("yyyy-MM-dd");
        var toText = _to.ToLocalTime().ToString("yyyy-MM-dd");
        DrawString(SemiBold, new Vector2(GridLeft + 2, 10), fromText, HorizontalAlignment.Left, -1, 10, Eve.Eve.Cyan);
        DrawString(SemiBold, new Vector2(GridRight - font.GetStringSize(toText, HorizontalAlignment.Left, -1, 10).X - 2, 10), toText, HorizontalAlignment.Left, -1, 10, Eve.Eve.Cyan);
        if (_shown.Count == 0)
            DrawString(font, new Vector2(GridLeft + 12, HeaderHeight + 28),
                _rows.Count == 0 ? (_cluster is null ? "No element in the graph: search some relations, or add an entity with +" : "This cluster has no member")
                    : "No element in these dates: move the handles of the slider under the timeline", HorizontalAlignment.Left, -1, 12, muted);
        // The scrollbar, when the rows do not all fit.
        if (MaxScroll > 0)
        {
            var track = Size.Y - HeaderHeight;
            var thumb = Math.Max(24, track * track / (_shown.Count * RowHeight));
            var thumbY = HeaderHeight + (track - thumb) * (_scroll / MaxScroll);
            DrawRect(new Rect2(Size.X - 6, thumbY, 4, thumb), Eve.Eve.Muted with { A = 0.6f });
        }
    }

    /// <summary>
    /// The picture of the timeline as it is shown (📷 PNG): a copy with the same cluster, dates and scrolling, drawn
    /// in a viewport of its own, its background painted.
    /// </summary>
    public async System.Threading.Tasks.Task<Image?> CaptureAsync()
    {
        if (Size.X < 1 || Size.Y < 1)
            return null;
        var viewport = new SubViewport
        {
            Size = (Vector2I)Size.Ceil(),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            TransparentBg = false,
            Disable3D = true
        };
        var copy = new RelationTimeline(_vm) { _snapshot = true, _cluster = _cluster, _rangeFrom = _rangeFrom, _rangeTo = _rangeTo, _scroll = _scroll };
        copy.Size = Size;
        viewport.AddChild(copy);
        AddChild(viewport);
        copy.Rebuild();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GodotObject.IsInstanceValid(viewport) ? viewport.GetTexture().GetImage() : null;
        if (GodotObject.IsInstanceValid(viewport))
            viewport.QueueFree();
        return image;
    }

    // ── The pointer ──

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel:
                _scroll = Math.Clamp(_scroll + (wheel.ButtonIndex == MouseButton.WheelDown ? 1 : -1) * RowHeight * 3 * Math.Max(wheel.Factor, 1), 0, MaxScroll);
                QueueRedraw();
                AcceptEvent();
                break;
            case InputEventPanGesture pan:
                _scroll = Math.Clamp(_scroll + pan.Delta.Y * RowHeight, 0, MaxScroll);
                QueueRedraw();
                AcceptEvent();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Escape } when _dragging is not null:
                // The drag given up: the ribbon goes back where it was (and the grid, when it slid).
                _dragging = null;
                Rebuild();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true, Pressed: true } click:
                if (GripAt(click.Position) is null && RowAt(click.Position) is { } opened)
                {
                    if (opened.Cluster is { } cluster)
                        ClusterDoubleClicked?.Invoke(cluster);
                    else if (opened.Node is { } node)
                        NodeDoubleClicked?.Invoke(node);
                }
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _vm.SelectedNode = RowAt(press.Position)?.Node;
                if (GripAt(press.Position) is { } taken)
                {
                    // An end of a ribbon taken: it follows the pointer until it is let go.
                    _dragging = taken;
                    _dragDate = taken.End == 0 ? taken.Row.First : taken.Row.Last;
                    _dragPointerX = press.Position.X;
                    GrabFocus();
                }
                QueueRedraw();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _dragging is { } done:
            {
                _dragging = null;
                EndDrag(done.Row, done.End, Day(_dragDate));
                QueueRedraw();
                AcceptEvent();
                break;
            }
            case InputEventMouseMotion motion when _dragging is { } drag:
            {
                // The end being dragged: a day of the grid, the start kept before the end and the end after the start.
                var day = DayAt(motion.Position.X);
                if (drag.End == 0 && drag.Row.EndKnown && day > drag.Row.Last)
                    day = Day(drag.Row.Last);
                if (drag.End == 1 && drag.Row.StartKnown && day < drag.Row.First)
                    day = Day(drag.Row.First);
                _dragDate = day;
                _dragPointerX = motion.Position.X;
                QueueRedraw();
                AcceptEvent();
                break;
            }
            case InputEventMouseMotion motion:
            {
                var hovered = RowAt(motion.Position);
                var grip = GripAt(motion.Position);
                var gripChanged = (grip?.Row, grip?.End) != (_grip?.Row, _grip?.End);
                if (!ReferenceEquals(hovered, _hovered) || gripChanged)
                {
                    _hovered = hovered;
                    _grip = grip;
                    MouseDefaultCursorShape = grip is not null ? CursorShape.Hsize : hovered is null ? CursorShape.Arrow : CursorShape.PointingHand;
                    TooltipText = grip is { } g ? $"{g.Row.Title} · drag: {(g.End == 0 ? "the start date" : "the end date")}{(_cluster is null ? " (saved at once)" : " (then UPDATE to save the cluster)")}"
                        : hovered is null ? string.Empty
                        : hovered.Cluster is not null ? $"Cluster · {hovered.Title[2..]} · double-click: edit · drag an end of the ribbon: its date"
                        : $"{hovered.Kind ?? hovered.Title} · {hovered.Title} · click: details · double-click: open" + (hovered.Editable ? " · drag an end of the ribbon: its date" : string.Empty);
                    QueueRedraw();
                }
                break;
            }
        }
    }

    /// <summary>
    /// While an end is held against an edge of the grid, the grid slides: the start held at the left goes on into the
    /// past, the end held at the right into the future, the dates shown growing that way (the slider under the
    /// timeline follows when the end is let go).
    /// </summary>
    public override void _Process(double delta)
    {
        if (_dragging is not { } drag)
            return;
        var span = (_to - _from).TotalSeconds;
        var shift = TimeSpan.FromSeconds(span * SlideRate * delta);
        if (drag.End == 0 && _dragPointerX <= GridLeft + SlideEdge)
        {
            _from -= shift;
            (_rangeFrom, _rangeTo) = (_from, _to);
            _dragDate = DayAt(GridLeft);
            if (drag.Row.EndKnown && _dragDate > drag.Row.Last)
                _dragDate = Day(drag.Row.Last);
            QueueRedraw();
        }
        else if (drag.End == 1 && _dragPointerX >= GridRight - SlideEdge)
        {
            _to += shift;
            (_rangeFrom, _rangeTo) = (_from, _to);
            _dragDate = DayAt(GridRight);
            if (drag.Row.StartKnown && _dragDate < drag.Row.First)
                _dragDate = Day(drag.Row.First);
            QueueRedraw();
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
            QueueRedraw();
        }
    }
}
