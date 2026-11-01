using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using KAONOMI.Chat.ViewModels;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// A bar chart of the statistics of /completion: one bar per value, from left to right (or one per line, the label on
/// the left, with <see cref="Horizontal"/>), the highest one filling the height, a grid at a quarter, half and three
/// quarters, the labels under the bars (one in a few when they are many). The bar under the pointer is lit and shows its
/// tooltip; a click calls <see cref="Clicked"/>.
/// </summary>
public partial class BarChart : Control
{
    private IReadOnlyList<ChartBar> _bars = [];
    private int _hovered = -1;

    public BarChart()
    {
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(0, 120);
    }

    public IReadOnlyList<ChartBar> Bars
    {
        get => _bars;
        set
        {
            _bars = value ?? [];
            _hovered = -1;
            if (Horizontal)
                CustomMinimumSize = new Vector2(CustomMinimumSize.X, Math.Max(40, _bars.Count * 22 + 4));
            QueueRedraw();
        }
    }

    /// <summary>One bar per line (for labels too long to stand under a bar: topics, models).</summary>
    public bool Horizontal { get; set; }

    public Color BarColor { get; set; } = EveStyle.Cyan;

    /// <summary>A bar clicked.</summary>
    public event Action<ChartBar>? Clicked;

    /// <summary>The cursor is a hand over the bars when a click does something.</summary>
    public bool Clickable
    {
        set => MouseDefaultCursorShape = value ? CursorShape.PointingHand : CursorShape.Arrow;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
            QueueRedraw();
        else if (what == NotificationMouseExit && _hovered >= 0)
        {
            _hovered = -1;
            QueueRedraw();
        }
    }

    private const float LabelHeight = 16;
    private const float HorizontalLabelWidth = 150;

    private Rect2 BarRect(int i)
    {
        var max = Math.Max(1, _bars.Max(b => b.Value));
        if (Horizontal)
        {
            var width = Math.Max(1, Size.X - HorizontalLabelWidth - 40);
            return new Rect2(HorizontalLabelWidth, i * 22 + 3, (float)(width * _bars[i].Value / max), 16);
        }
        var area = new Rect2(0, 4, Size.X, Math.Max(1, Size.Y - LabelHeight - 6));
        var slot = area.Size.X / _bars.Count;
        var gap = Math.Min(4, slot * 0.2f);
        var height = (float)(area.Size.Y * _bars[i].Value / max);
        return new Rect2(area.Position.X + slot * i + gap / 2, area.End.Y - height, Math.Max(1, slot - gap), height);
    }

    private int BarAt(Vector2 at)
    {
        if (_bars.Count == 0)
            return -1;
        if (Horizontal)
        {
            var line = (int)(at.Y / 22);
            return line >= 0 && line < _bars.Count ? line : -1;
        }
        var index = (int)(at.X / (Size.X / _bars.Count));
        return index >= 0 && index < _bars.Count ? index : -1;
    }

    public override void _Draw()
    {
        var font = EveStyle.Body;
        var muted = EveStyle.Muted;
        if (_bars.Count == 0 || _bars.All(b => b.Value <= 0))
        {
            DrawString(font, new Vector2(8, Size.Y / 2), "No data", HorizontalAlignment.Left, -1, 12, muted);
            return;
        }
        var max = _bars.Max(b => b.Value);
        if (!Horizontal)
        {
            var bottom = Size.Y - LabelHeight - 2;
            foreach (var share in new[] { 0.25f, 0.5f, 0.75f })
            {
                var y = 4 + (bottom - 4) * (1 - share);
                DrawLine(new Vector2(0, y), new Vector2(Size.X, y), EveStyle.Edge with { A = 0.12f });
            }
            DrawString(font, new Vector2(2, 14), max.ToString("0"), HorizontalAlignment.Left, -1, 10, muted);
        }

        // One label in a few when the bars are many (about one every 60 pixels).
        var every = Horizontal ? 1 : Math.Max(1, (int)Math.Ceiling(_bars.Count * 60 / Math.Max(60, Size.X)));
        for (var i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            var rect = BarRect(i);
            var color = i == _hovered ? BarColor.Lightened(0.35f) : BarColor with { A = 0.8f };
            if (bar.Value > 0)
                DrawRect(rect, color);
            if (Horizontal)
            {
                DrawString(font, new Vector2(4, rect.Position.Y + 13), Clip(bar.Label, 22), HorizontalAlignment.Left, HorizontalLabelWidth - 8, 11, EveStyle.Text);
                DrawString(font, new Vector2(rect.End.X + 6, rect.Position.Y + 13), bar.Value.ToString("0"), HorizontalAlignment.Left, -1, 11, muted);
            }
            else if (i % every == 0)
            {
                var slot = Size.X / _bars.Count;
                DrawString(font, new Vector2(slot * i, Size.Y - 3), bar.Label, HorizontalAlignment.Left, Math.Max(slot * every, 30), 10, muted);
            }
        }
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion motion:
                var hovered = BarAt(motion.Position);
                if (hovered != _hovered)
                {
                    _hovered = hovered;
                    QueueRedraw();
                }
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click when BarAt(click.Position) is var index and >= 0:
                Clicked?.Invoke(_bars[index]);
                AcceptEvent();
                break;
        }
    }

    public override string _GetTooltip(Vector2 atPosition) => BarAt(atPosition) is var i and >= 0 ? _bars[i].Tip : string.Empty;
}

/// <summary>
/// The completions by weekday (lines, Monday first) and hour (columns, 0 to 23): each cell the brighter the more
/// completions; the count of a cell in its tooltip.
/// </summary>
public partial class WeekHourHeatmap : Control
{
    private static readonly string[] Days = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private IReadOnlyList<int> _counts = new int[7 * 24];

    public WeekHourHeatmap()
    {
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(0, 7 * 16 + 18);
    }

    public IReadOnlyList<int> Counts
    {
        get => _counts;
        set
        {
            _counts = value is { Count: 7 * 24 } ? value : new int[7 * 24];
            QueueRedraw();
        }
    }

    private const float Left = 34;
    private const float Top = 16;

    private Vector2 Cell => new((Size.X - Left) / 24, (Size.Y - Top) / 7);

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
            QueueRedraw();
    }

    public override void _Draw()
    {
        var font = EveStyle.Body;
        var cell = Cell;
        var max = Math.Max(1, _counts.Max());
        for (var h = 0; h < 24; h += 3)
            DrawString(font, new Vector2(Left + h * cell.X, 11), h.ToString("00"), HorizontalAlignment.Left, -1, 10, EveStyle.Muted);
        for (var d = 0; d < 7; d++)
        {
            DrawString(font, new Vector2(2, Top + d * cell.Y + cell.Y * 0.7f), Days[d], HorizontalAlignment.Left, -1, 10, EveStyle.Muted);
            for (var h = 0; h < 24; h++)
            {
                var count = _counts[d * 24 + h];
                var rect = new Rect2(Left + h * cell.X + 1, Top + d * cell.Y + 1, cell.X - 2, cell.Y - 2);
                var color = count == 0 ? EveStyle.Edge with { A = 0.08f } : EveStyle.Cyan with { A = 0.15f + 0.85f * MathF.Sqrt(count / (float)max) };
                DrawRect(rect, color);
            }
        }
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        var cell = Cell;
        var h = (int)((atPosition.X - Left) / cell.X);
        var d = (int)((atPosition.Y - Top) / cell.Y);
        if (h < 0 || h > 23 || d < 0 || d > 6)
            return string.Empty;
        return $"{Days[d]} {h:00}:00–{h:00}:59: {_counts[d * 24 + h]} completion(s)";
    }
}
