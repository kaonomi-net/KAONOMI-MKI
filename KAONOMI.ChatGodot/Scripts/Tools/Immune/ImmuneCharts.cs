using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Immune;

/// <summary>A value of a chart: its label, its count and its colour.</summary>
internal readonly record struct Slice(string Label, int Count, Color Color);

/// <summary>
/// The charts of the /immune report, drawn by hand in the colours of the chat: a donut with its total in the middle, a
/// list of horizontal bars, and columns (a month each). They grow into place when they appear.
/// </summary>
internal static partial class ImmuneCharts
{
    /// <summary>The series colours: distinct on the dark background, the violet of the AI images first.</summary>
    public static readonly Color[] Palette =
    [
        Color.FromHtml("#B07CFF"), Color.FromHtml("#2B9BF4"), Color.FromHtml("#3DDC97"), Color.FromHtml("#FFA940"),
        Color.FromHtml("#FF6B9A"), Color.FromHtml("#5CE1E6"), Color.FromHtml("#E8E36B"), Color.FromHtml("#8A94A6"),
    ];

    public static Color Level(string level) => level switch
    {
        "high" => ImmuneScanCanvas.Crimson,
        "medium" => ImmuneScanCanvas.Orange,
        "low" => Color.FromHtml("#E8E36B"),
        _ => Color.FromHtml("#5CA8D8"),
    };

    public static readonly Color Green = Color.FromHtml("#3DDC97");

    /// <summary>The colour of a category of AI service.</summary>
    public static Color Category(string category) => category switch
    {
        "chat" => Color.FromHtml("#2B9BF4"),
        "image" => Color.FromHtml("#B07CFF"),
        "video" => Color.FromHtml("#FF6B9A"),
        "audio" => Color.FromHtml("#FFA940"),
        "code" => Green,
        "writing" => Color.FromHtml("#E8E36B"),
        _ => Color.FromHtml("#8A94A6"),
    };

    /// <summary>A donut and its legend side by side.</summary>
    public static Control DonutWithLegend(IReadOnlyList<Slice> slices, string caption)
    {
        var donut = new Donut(slices, caption) { CustomMinimumSize = new Vector2(150, 150) };
        var legend = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        legend.AddThemeConstantOverride("separation", 4);
        var total = Math.Max(1, slices.Sum(s => s.Count));
        foreach (var s in slices.Where(s => s.Count > 0))
        {
            var dot = EveStyle.Text_("●", s.Color, 12);
            var name = EveStyle.Text_(s.Label, EveStyle.Text, 12);
            name.ClipText = true;
            name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            name.TooltipText = s.Label;
            name.MouseFilter = Control.MouseFilterEnum.Pass;
            var value = EveStyle.Text_($"{s.Count}  ·  {100.0 * s.Count / total:0}%", EveStyle.Muted, 11);
            legend.AddChild(Eve.Ui.Row(6, dot, name, value));
        }
        var row = Eve.Ui.Row(14, donut, legend);
        row.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return row;
    }

    public sealed partial class Donut(IReadOnlyList<Slice> slices, string caption) : Control
    {
        private float _grow;

        public override void _Process(double delta)
        {
            if (_grow >= 1)
                return;
            _grow = Mathf.Min(1, _grow + (float)delta * 1.8f);
            QueueRedraw();
        }

        public override void _Draw()
        {
            var center = Size / 2;
            var radius = Mathf.Min(Size.X, Size.Y) / 2 - 10;
            var width = Mathf.Max(10, radius * 0.28f);
            var total = slices.Sum(s => s.Count);
            DrawArc(center, radius, 0, Mathf.Tau, 96, EveStyle.Raised(0.05f), width, true);
            var ease = 1 - Mathf.Pow(1 - _grow, 3);
            var angle = -Mathf.Pi / 2;
            if (total > 0)
                foreach (var s in slices.Where(s => s.Count > 0))
                {
                    var sweep = Mathf.Tau * s.Count / total * ease;
                    var gap = slices.Count(x => x.Count > 0) > 1 ? 0.025f : 0;
                    DrawArc(center, radius, angle + gap, angle + sweep - gap, Math.Max(8, (int)(sweep * 30)), s.Color, width, true);
                    angle += sweep;
                }
            var font = EveStyle.Display;
            var size = (int)Mathf.Clamp(radius * 0.42f, 14, 30);
            DrawString(font, center + new Vector2(-radius, size * 0.3f), Mathf.RoundToInt(total * ease).ToString("N0"), HorizontalAlignment.Center, radius * 2, size, EveStyle.TextBright);
            DrawString(font, center + new Vector2(-radius, size * 0.3f + 15), caption.ToUpperInvariant(), HorizontalAlignment.Center, radius * 2, 9, EveStyle.Muted);
        }
    }

    /// <summary>Horizontal bars, one row per value, the longest at the largest count.</summary>
    public sealed partial class Bars : Control
    {
        private const float Row = 24;
        private readonly IReadOnlyList<Slice> _slices;
        private readonly float _labelWidth;
        private float _grow;

        public Bars(IReadOnlyList<Slice> slices, float labelWidth = 150)
        {
            _slices = slices;
            _labelWidth = labelWidth;
            CustomMinimumSize = new Vector2(220, Math.Max(1, slices.Count) * Row + 4);
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
        }

        public override void _Process(double delta)
        {
            if (_grow >= 1)
                return;
            _grow = Mathf.Min(1, _grow + (float)delta * 1.6f);
            QueueRedraw();
        }

        public override void _Draw()
        {
            var max = Math.Max(1, _slices.Count == 0 ? 1 : _slices.Max(s => s.Count));
            var font = EveStyle.Body;
            var barStart = _labelWidth + 8;
            var barRoom = Mathf.Max(20, Size.X - barStart - 46);
            for (var i = 0; i < _slices.Count; i++)
            {
                var s = _slices[i];
                var y = i * Row;
                // Each row starts a little after the one above.
                var k = Mathf.Clamp(_grow * 1.4f - i * 0.08f, 0, 1);
                var ease = 1 - Mathf.Pow(1 - k, 3);
                DrawString(font, new Vector2(0, y + 16), s.Label, HorizontalAlignment.Right, _labelWidth, 12, EveStyle.Text);
                DrawRect(new Rect2(barStart, y + 6, barRoom, 12), EveStyle.Raised(0.04f));
                var w = barRoom * s.Count / max * ease;
                if (s.Count > 0)
                {
                    DrawRect(new Rect2(barStart, y + 6, Mathf.Max(2, w), 12), s.Color with { A = 0.85f });
                    DrawRect(new Rect2(barStart, y + 6, Mathf.Max(2, w), 2), s.Color.Lightened(0.35f));
                }
                DrawString(font, new Vector2(barStart + w + 6, y + 16), Mathf.RoundToInt(s.Count * ease).ToString("N0"), HorizontalAlignment.Left, 60, 12, EveStyle.TextBright);
            }
        }
    }

    /// <summary>Columns with their label below (the AI images per month).</summary>
    public sealed partial class Columns : Control
    {
        private readonly IReadOnlyList<Slice> _slices;
        private float _grow;

        public Columns(IReadOnlyList<Slice> slices)
        {
            _slices = slices;
            CustomMinimumSize = new Vector2(220, 150);
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
        }

        public override void _Process(double delta)
        {
            if (_grow >= 1)
                return;
            _grow = Mathf.Min(1, _grow + (float)delta * 1.6f);
            QueueRedraw();
        }

        public override void _Draw()
        {
            var font = EveStyle.Body;
            if (_slices.Count == 0)
            {
                DrawString(font, new Vector2(0, Size.Y / 2), "No AI image", HorizontalAlignment.Center, Size.X, 12, EveStyle.Muted);
                return;
            }
            var max = Math.Max(1, _slices.Max(s => s.Count));
            var top = 18f;
            var bottom = Size.Y - 20;
            var slot = Size.X / _slices.Count;
            var width = Mathf.Min(38, slot * 0.62f);
            DrawLine(new Vector2(0, bottom), new Vector2(Size.X, bottom), EveStyle.Edge with { A = 0.6f }, 1);
            for (var i = 0; i < _slices.Count; i++)
            {
                var s = _slices[i];
                var k = Mathf.Clamp(_grow * 1.5f - i * 0.05f, 0, 1);
                var ease = 1 - Mathf.Pow(1 - k, 3);
                var h = (bottom - top) * s.Count / max * ease;
                var x = i * slot + (slot - width) / 2;
                DrawRect(new Rect2(x, bottom - h, width, h), s.Color with { A = 0.8f });
                DrawRect(new Rect2(x, bottom - h, width, 2), s.Color.Lightened(0.4f));
                DrawString(font, new Vector2(i * slot, bottom - h - 5), s.Count.ToString("N0"), HorizontalAlignment.Center, slot, 11, EveStyle.TextBright with { A = ease });
                DrawString(font, new Vector2(i * slot, Size.Y - 4), s.Label, HorizontalAlignment.Center, slot, 10, EveStyle.Muted);
            }
        }
    }

    /// <summary>Stacked columns: one per label (a month), its parts on top of each other, the total above.</summary>
    public sealed partial class StackedColumns : Control
    {
        private readonly IReadOnlyList<(string Label, IReadOnlyList<Slice> Parts)> _columns;
        private float _grow;

        public StackedColumns(IReadOnlyList<(string Label, IReadOnlyList<Slice> Parts)> columns)
        {
            _columns = columns;
            CustomMinimumSize = new Vector2(260, 170);
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            SizeFlagsVertical = SizeFlags.ExpandFill;
        }

        public override void _Process(double delta)
        {
            if (_grow >= 1)
                return;
            _grow = Mathf.Min(1, _grow + (float)delta * 1.6f);
            QueueRedraw();
        }

        public override void _Draw()
        {
            var font = EveStyle.Body;
            if (_columns.Count == 0)
            {
                DrawString(font, new Vector2(0, Size.Y / 2), "No visit", HorizontalAlignment.Center, Size.X, 12, EveStyle.Muted);
                return;
            }
            var max = Math.Max(1, _columns.Max(c => c.Parts.Sum(p => p.Count)));
            var top = 18f;
            var bottom = Size.Y - 20;
            var slot = Size.X / _columns.Count;
            var width = Mathf.Min(38, slot * 0.62f);
            DrawLine(new Vector2(0, bottom), new Vector2(Size.X, bottom), EveStyle.Edge with { A = 0.6f }, 1);
            for (var i = 0; i < _columns.Count; i++)
            {
                var (label, parts) = _columns[i];
                var k = Mathf.Clamp(_grow * 1.5f - i * 0.05f, 0, 1);
                var ease = 1 - Mathf.Pow(1 - k, 3);
                var x = i * slot + (slot - width) / 2;
                var y = bottom;
                foreach (var part in parts.Where(p => p.Count > 0))
                {
                    var h = (bottom - top) * part.Count / max * ease;
                    DrawRect(new Rect2(x, y - h, width, h), part.Color with { A = 0.85f });
                    DrawLine(new Vector2(x, y - h), new Vector2(x + width, y - h), EveStyle.Space with { A = 0.6f }, 1);
                    y -= h;
                }
                var total = parts.Sum(p => p.Count);
                DrawString(font, new Vector2(i * slot, y - 5), total.ToString("N0"), HorizontalAlignment.Center, slot, 11, EveStyle.TextBright with { A = ease });
                DrawString(font, new Vector2(i * slot, Size.Y - 4), label, HorizontalAlignment.Center, slot, 10, EveStyle.Muted);
            }
        }
    }
}
