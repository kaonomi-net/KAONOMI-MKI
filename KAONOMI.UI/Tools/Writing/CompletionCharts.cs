using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;
using KAONOMI.Chat.Views;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>The drawing helpers of the charts of /completion.</summary>
internal static class ChartDraw
{
    public static IBrush Fade(IBrush brush, double opacity) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, opacity) : brush;

    public static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    public static void DrawText(DrawingContext context, string text, Point at, double size, IBrush brush, double maxWidth = 0)
    {
        var formatted = Text(text, size, brush);
        if (maxWidth > 0)
        {
            formatted.MaxTextWidth = Math.Max(1, maxWidth);
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }
        context.DrawText(formatted, at);
    }

    /// <summary>Sets the tooltip of <paramref name="control"/> to <paramref name="tip"/> (none when empty).</summary>
    public static void Tip(Control control, string? tip)
    {
        var current = ToolTip.GetTip(control) as string;
        if (current == tip)
            return;
        ToolTip.SetTip(control, string.IsNullOrEmpty(tip) ? null : tip);
        ToolTip.SetIsOpen(control, false);
        if (!string.IsNullOrEmpty(tip))
            ToolTip.SetIsOpen(control, true);
    }
}

/// <summary>
/// A bar chart of the statistics of /completion: one bar per value, from left to right (or one per line, the label on the
/// left, with <see cref="Horizontal"/>), the highest one filling the height, a grid at a quarter, half and three quarters,
/// the labels under the bars (one in a few when they are many). The bar under the pointer is lit and shows its tooltip; a
/// click calls <see cref="Clicked"/>.
/// </summary>
public sealed class BarChart : Control
{
    private IReadOnlyList<ChartBar> _bars = [];
    private int _hovered = -1;
    private const double LabelHeight = 16;
    private const double HorizontalLabelWidth = 150;

    public BarChart()
    {
        MinHeight = 120;
        ClipToBounds = true;
    }

    public IReadOnlyList<ChartBar> Bars
    {
        get => _bars;
        set
        {
            _bars = value ?? [];
            _hovered = -1;
            if (Horizontal)
                Height = Math.Max(40, _bars.Count * 22 + 4);
            InvalidateVisual();
        }
    }

    /// <summary>One bar per line (for labels too long to stand under a bar: topics, models).</summary>
    public bool Horizontal { get; set; }

    public IBrush BarColor { get; set; } = Ui.Accent;

    /// <summary>A bar clicked.</summary>
    public event Action<ChartBar>? Clicked;

    /// <summary>The cursor is a hand over the bars when a click does something.</summary>
    public bool Clickable
    {
        set => Cursor = value ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    private Rect BarRect(int i)
    {
        var max = Math.Max(1, _bars.Max(b => b.Value));
        var size = Bounds.Size;
        if (Horizontal)
        {
            var width = Math.Max(1, size.Width - HorizontalLabelWidth - 40);
            return new Rect(HorizontalLabelWidth, i * 22 + 3, width * _bars[i].Value / max, 16);
        }
        var areaHeight = Math.Max(1, size.Height - LabelHeight - 6);
        var slot = size.Width / _bars.Count;
        var gap = Math.Min(4, slot * 0.2);
        var height = areaHeight * _bars[i].Value / max;
        return new Rect(slot * i + gap / 2, 4 + areaHeight - height, Math.Max(1, slot - gap), height);
    }

    private int BarAt(Point at)
    {
        if (_bars.Count == 0)
            return -1;
        if (Horizontal)
        {
            var line = (int)(at.Y / 22);
            return line >= 0 && line < _bars.Count ? line : -1;
        }
        var index = (int)(at.X / (Bounds.Width / _bars.Count));
        return index >= 0 && index < _bars.Count ? index : -1;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Brushes.Transparent, new Rect(size));
        if (_bars.Count == 0 || _bars.All(b => b.Value <= 0))
        {
            ChartDraw.DrawText(context, "No data", new Point(8, size.Height / 2 - 8), 12, Ui.Muted);
            return;
        }
        var max = _bars.Max(b => b.Value);
        if (!Horizontal)
        {
            var bottom = size.Height - LabelHeight - 2;
            var pen = new Pen(ChartDraw.Fade(Ui.Edge, 0.6), 1);
            foreach (var share in new[] { 0.25, 0.5, 0.75 })
            {
                var y = 4 + (bottom - 4) * (1 - share);
                context.DrawLine(pen, new Point(0, y), new Point(size.Width, y));
            }
            ChartDraw.DrawText(context, max.ToString("0", CultureInfo.InvariantCulture), new Point(2, 2), 10, Ui.Muted);
        }

        // One label in a few when the bars are many (about one every 60 pixels).
        var every = Horizontal ? 1 : Math.Max(1, (int)Math.Ceiling(_bars.Count * 60 / Math.Max(60, size.Width)));
        for (var i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            var rect = BarRect(i);
            var color = i == _hovered ? BarColor : ChartDraw.Fade(BarColor, 0.75);
            if (bar.Value > 0)
                context.FillRectangle(color, rect);
            if (Horizontal)
            {
                ChartDraw.DrawText(context, bar.Label, new Point(4, rect.Y), 11, Ui.Text, HorizontalLabelWidth - 8);
                ChartDraw.DrawText(context, bar.Value.ToString("0", CultureInfo.InvariantCulture), new Point(rect.Right + 6, rect.Y), 11, Ui.Muted);
            }
            else if (i % every == 0)
            {
                var slot = size.Width / _bars.Count;
                ChartDraw.DrawText(context, bar.Label, new Point(slot * i, size.Height - LabelHeight), 10, Ui.Muted, Math.Max(slot * every, 30));
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hovered = BarAt(e.GetPosition(this));
        if (hovered == _hovered)
            return;
        _hovered = hovered;
        ChartDraw.Tip(this, hovered >= 0 ? _bars[hovered].Tip : null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = -1;
        ChartDraw.Tip(this, null);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || BarAt(e.GetPosition(this)) is not (var index and >= 0))
            return;
        Clicked?.Invoke(_bars[index]);
        e.Handled = true;
    }
}

/// <summary>
/// The completions by weekday (lines, Monday first) and hour (columns, 0 to 23): each cell the brighter the more
/// completions; the count of a cell in its tooltip.
/// </summary>
public sealed class WeekHourHeatmap : Control
{
    private static readonly string[] Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private IReadOnlyList<int> _counts = new int[7 * 24];
    private const double Left = 34;
    private const double Top = 16;

    public WeekHourHeatmap()
    {
        Height = 7 * 16 + 18;
    }

    public IReadOnlyList<int> Counts
    {
        get => _counts;
        set
        {
            _counts = value is { Count: 7 * 24 } ? value : new int[7 * 24];
            InvalidateVisual();
        }
    }

    private Size Cell => new(Math.Max(1, (Bounds.Width - Left) / 24), Math.Max(1, (Bounds.Height - Top) / 7));

    public override void Render(DrawingContext context)
    {
        var cell = Cell;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var max = Math.Max(1, _counts.Max());
        for (var h = 0; h < 24; h += 3)
            ChartDraw.DrawText(context, h.ToString("00", CultureInfo.InvariantCulture), new Point(Left + h * cell.Width, 0), 10, Ui.Muted);
        for (var d = 0; d < 7; d++)
        {
            ChartDraw.DrawText(context, Days[d], new Point(2, Top + d * cell.Height + cell.Height / 2 - 7), 10, Ui.Muted);
            for (var h = 0; h < 24; h++)
            {
                var count = _counts[d * 24 + h];
                var rect = new Rect(Left + h * cell.Width + 1, Top + d * cell.Height + 1, Math.Max(1, cell.Width - 2), Math.Max(1, cell.Height - 2));
                var color = count == 0 ? ChartDraw.Fade(Ui.Edge, 0.35) : ChartDraw.Fade(Ui.Accent, 0.15 + 0.85 * Math.Sqrt(count / (double)max));
                context.FillRectangle(color, rect);
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        var cell = Cell;
        var h = (int)Math.Floor((at.X - Left) / cell.Width);
        var d = (int)Math.Floor((at.Y - Top) / cell.Height);
        ChartDraw.Tip(this, h < 0 || h > 23 || d < 0 || d > 6 ? null : $"{Days[d]} {h:00}:00–{h:00}:59: {_counts[d * 24 + h]} completion(s)");
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ChartDraw.Tip(this, null);
    }
}

/// <summary>
/// The pie chart of the languages: one slice per value, from the top clockwise, each with a thin gap; a single slice is a
/// full disc; nothing without a value. The slice under the pointer shows its legend as tooltip.
/// </summary>
public sealed class PieChart : Control
{
    private IReadOnlyList<PieSlice> _slices = [];

    public IReadOnlyList<PieSlice> Slices
    {
        get => _slices;
        set
        {
            _slices = value ?? [];
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Brushes.Transparent, new Rect(size));
        var slices = _slices.Where(s => s.Value > 0).ToList();
        var total = slices.Sum(s => s.Value);
        if (total <= 0)
            return;
        var radius = Math.Max(1, Math.Min(size.Width, size.Height) / 2 - 2);
        var center = new Point(size.Width / 2, size.Height / 2);
        if (slices.Count == 1)
        {
            context.DrawEllipse(slices[0].Brush, null, center, radius, radius);
            return;
        }
        Point At(double angle) => new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
        var start = -Math.PI / 2;
        var gap = new Pen(Ui.Panel, 2);
        foreach (var slice in slices)
        {
            var sweep = 2 * Math.PI * slice.Value / total;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(center, true);
                g.LineTo(At(start));
                g.ArcTo(At(start + sweep), new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise);
                g.EndFigure(true);
            }
            context.DrawGeometry(slice.Brush, gap, geometry);
            start += sweep;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var slices = _slices.Where(s => s.Value > 0).ToList();
        var total = slices.Sum(s => s.Value);
        var at = e.GetPosition(this);
        var offset = at - new Point(Bounds.Width / 2, Bounds.Height / 2);
        var length = Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
        string? tip = null;
        if (total > 0 && length <= Math.Min(Bounds.Width, Bounds.Height) / 2)
        {
            var angle = Math.Atan2(offset.Y, offset.X) + Math.PI / 2;
            angle = ((angle % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
            var start = 0.0;
            foreach (var slice in slices)
            {
                var sweep = 2 * Math.PI * slice.Value / total;
                if (angle >= start && angle < start + sweep)
                {
                    tip = $"{slice.Label}  {slice.Value} ({slice.Share:P0})";
                    break;
                }
                start += sweep;
            }
        }
        ChartDraw.Tip(this, tip);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ChartDraw.Tip(this, null);
    }
}
