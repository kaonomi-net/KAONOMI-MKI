using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>A value of a chart: its label, its count and its colour.</summary>
internal readonly record struct Slice(string Label, int Count, Color Color);

/// <summary>
/// The charts of the /immune report, drawn by hand in flat colours: a donut with its total in the middle, a list of
/// horizontal bars, columns (a month each) and stacked columns.
/// </summary>
internal static class ImmuneCharts
{
    /// <summary>The series colours: distinct on the dark background, the violet of the AI images first.</summary>
    public static readonly Color[] Palette =
    [
        Color.Parse("#B07CFF"), Color.Parse("#2B9BF4"), Color.Parse("#3DDC97"), Color.Parse("#FFA940"),
        Color.Parse("#FF6B9A"), Color.Parse("#5CE1E6"), Color.Parse("#E8E36B"), Color.Parse("#8A94A6"),
    ];

    public static readonly Color Violet = Color.Parse("#B07CFF");
    public static readonly Color Crimson = Color.Parse("#FF4D5E");
    public static readonly Color Orange = Color.Parse("#FFA940");
    public static readonly Color Green = Color.Parse("#3DDC97");
    public static readonly Color Cyan = Color.Parse("#3FD0FF");

    public static Color Level(string level) => level switch
    {
        "high" => Crimson,
        "medium" => Orange,
        "low" => Color.Parse("#E8E36B"),
        _ => Color.Parse("#5CA8D8"),
    };

    /// <summary>The colour of a category of AI service.</summary>
    public static Color Category(string category) => category switch
    {
        "chat" => Color.Parse("#2B9BF4"),
        "image" => Color.Parse("#B07CFF"),
        "video" => Color.Parse("#FF6B9A"),
        "audio" => Color.Parse("#FFA940"),
        "code" => Green,
        "writing" => Color.Parse("#E8E36B"),
        _ => Color.Parse("#8A94A6"),
    };

    public static IBrush B(Color color) => new SolidColorBrush(color);

    private static FormattedText Text(string text, double size, IBrush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);

    /// <summary>Draws <paramref name="text"/> centred in the slot from <paramref name="left"/>, <paramref name="width"/> wide, its baseline about <paramref name="y"/>.</summary>
    private static void Centered(DrawingContext context, FormattedText text, double left, double width, double y) =>
        context.DrawText(text, new Point(left + (width - text.Width) / 2, y - text.Height * 0.8));

    /// <summary>A donut and its legend side by side.</summary>
    public static Control DonutWithLegend(IReadOnlyList<Slice> slices, string caption)
    {
        var donut = new Donut(slices, caption) { Width = 150, Height = 150, VerticalAlignment = VerticalAlignment.Center };
        var legend = Ui.Column(4);
        legend.VerticalAlignment = VerticalAlignment.Center;
        var total = Math.Max(1, slices.Sum(s => s.Count));
        foreach (var s in slices.Where(s => s.Count > 0))
        {
            var name = Ui.Label(s.Label, Ui.Text, 12);
            ToolTip.SetTip(name, s.Label);
            legend.Children.Add(Kit.FillRow(6, [Ui.Label("●", B(s.Color), 12)], name, Ui.Label($"{s.Count}  ·  {100.0 * s.Count / total:0}%", Ui.Muted, 11)));
        }
        return Kit.FillRow(14, [donut], legend);
    }

    public sealed class Donut(IReadOnlyList<Slice> slices, string caption) : Control
    {
        public override void Render(DrawingContext context)
        {
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 10;
            var width = Math.Max(10, radius * 0.28);
            var total = slices.Sum(s => s.Count);
            context.DrawEllipse(null, new Pen(Ui.Edge, width), center, radius, radius);
            var angle = -Math.PI / 2;
            var shown = slices.Count(x => x.Count > 0);
            if (total > 0)
                foreach (var s in slices.Where(s => s.Count > 0))
                {
                    var sweep = 2 * Math.PI * s.Count / total;
                    var gap = shown > 1 ? 0.025 : 0;
                    Arc(context, center, radius, angle + gap, angle + sweep - gap, new Pen(B(s.Color), width));
                    angle += sweep;
                }
            var size = Math.Clamp(radius * 0.42, 14, 30);
            Centered(context, Text(total.ToString("N0"), size, Ui.TextBright, true), center.X - radius, radius * 2, center.Y + size * 0.3);
            Centered(context, Text(caption.ToUpperInvariant(), 9, Ui.Muted), center.X - radius, radius * 2, center.Y + size * 0.3 + 15);
        }

        private static void Arc(DrawingContext context, Point center, double radius, double from, double to, Pen pen)
        {
            if (to - from >= 2 * Math.PI - 0.001)
            {
                context.DrawEllipse(null, pen, center, radius, radius);
                return;
            }
            if (to <= from)
                return;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(center.X + radius * Math.Cos(from), center.Y + radius * Math.Sin(from)), false);
                g.ArcTo(new Point(center.X + radius * Math.Cos(to), center.Y + radius * Math.Sin(to)), new Size(radius, radius), 0, to - from > Math.PI, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>Horizontal bars, one row per value, the longest at the largest count.</summary>
    public sealed class Bars : Control
    {
        private const double Row = 24;
        private readonly IReadOnlyList<Slice> _slices;
        private readonly double _labelWidth;

        public Bars(IReadOnlyList<Slice> slices, double labelWidth = 150)
        {
            _slices = slices;
            _labelWidth = labelWidth;
            MinWidth = 220;
            Height = Math.Max(1, slices.Count) * Row + 4;
        }

        public override void Render(DrawingContext context)
        {
            var max = Math.Max(1, _slices.Count == 0 ? 1 : _slices.Max(s => s.Count));
            var barStart = _labelWidth + 8;
            var barRoom = Math.Max(20, Bounds.Width - barStart - 46);
            for (var i = 0; i < _slices.Count; i++)
            {
                var s = _slices[i];
                var y = i * Row;
                var label = Text(s.Label, 12, Ui.Text);
                label.MaxTextWidth = _labelWidth;
                label.MaxLineCount = 1;
                label.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(label, new Point(_labelWidth - label.Width, y + 4));
                context.FillRectangle(Ui.Field, new Rect(barStart, y + 6, barRoom, 12));
                var w = barRoom * s.Count / max;
                if (s.Count > 0)
                    context.FillRectangle(B(s.Color), new Rect(barStart, y + 6, Math.Max(2, w), 12));
                context.DrawText(Text(s.Count.ToString("N0"), 12, Ui.TextBright), new Point(barStart + w + 6, y + 4));
            }
        }
    }

    /// <summary>Columns with their label below (the AI images per month).</summary>
    public sealed class Columns : Control
    {
        private readonly IReadOnlyList<Slice> _slices;

        public Columns(IReadOnlyList<Slice> slices)
        {
            _slices = slices;
            MinWidth = 220;
            MinHeight = 150;
        }

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            if (_slices.Count == 0)
            {
                Centered(context, Text("No AI image", 12, Ui.Muted), 0, size.Width, size.Height / 2);
                return;
            }
            var max = Math.Max(1, _slices.Max(s => s.Count));
            const double top = 18;
            var bottom = size.Height - 20;
            var slot = size.Width / _slices.Count;
            var width = Math.Min(38, slot * 0.62);
            context.DrawLine(new Pen(Ui.Edge, 1), new Point(0, bottom), new Point(size.Width, bottom));
            for (var i = 0; i < _slices.Count; i++)
            {
                var s = _slices[i];
                var h = (bottom - top) * s.Count / max;
                var x = i * slot + (slot - width) / 2;
                context.FillRectangle(B(s.Color), new Rect(x, bottom - h, width, h));
                Centered(context, Text(s.Count.ToString("N0"), 11, Ui.TextBright), i * slot, slot, bottom - h - 5);
                Centered(context, Text(s.Label, 10, Ui.Muted), i * slot, slot, size.Height - 4);
            }
        }
    }

    /// <summary>Stacked columns: one per label (a month), its parts on top of each other, the total above.</summary>
    public sealed class StackedColumns : Control
    {
        private readonly IReadOnlyList<(string Label, IReadOnlyList<Slice> Parts)> _columns;

        public StackedColumns(IReadOnlyList<(string Label, IReadOnlyList<Slice> Parts)> columns)
        {
            _columns = columns;
            MinWidth = 260;
            MinHeight = 170;
        }

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            if (_columns.Count == 0)
            {
                Centered(context, Text("No visit", 12, Ui.Muted), 0, size.Width, size.Height / 2);
                return;
            }
            var max = Math.Max(1, _columns.Max(c => c.Parts.Sum(p => p.Count)));
            const double top = 18;
            var bottom = size.Height - 20;
            var slot = size.Width / _columns.Count;
            var width = Math.Min(38, slot * 0.62);
            var separator = new Pen(Ui.Panel, 1);
            context.DrawLine(new Pen(Ui.Edge, 1), new Point(0, bottom), new Point(size.Width, bottom));
            for (var i = 0; i < _columns.Count; i++)
            {
                var (label, parts) = _columns[i];
                var x = i * slot + (slot - width) / 2;
                var y = bottom;
                foreach (var part in parts.Where(p => p.Count > 0))
                {
                    var h = (bottom - top) * part.Count / max;
                    context.FillRectangle(B(part.Color), new Rect(x, y - h, width, h));
                    context.DrawLine(separator, new Point(x, y - h), new Point(x + width, y - h));
                    y -= h;
                }
                Centered(context, Text(parts.Sum(p => p.Count).ToString("N0"), 11, Ui.TextBright), i * slot, slot, y - 5);
                Centered(context, Text(label, 10, Ui.Muted), i * slot, slot, size.Height - 4);
            }
        }
    }
}
