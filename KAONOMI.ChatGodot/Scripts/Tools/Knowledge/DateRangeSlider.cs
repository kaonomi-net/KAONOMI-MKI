using System;
using Godot;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// A double slider of dates (under the 3D relation graph): a track from the oldest date to the newest, a handle for the
/// first date shown (it starts at the oldest) and one for the last (it starts at the newest), the dates chosen written
/// above them. Dragging a handle (or clicking the track, the nearest handle going there) moves it and its date; the graph is
/// updated once, when the handle is let go (<see cref="RangeChanged"/>); a double-click shows all the dates again.
/// </summary>
public partial class DateRangeSlider : Control
{
    private const float Side = 16;   // margin of the track at each end, for the handles
    private const float TrackY = 34;
    private const float HandleRadius = 8;

    private DateTimeOffset _oldest = DateTimeOffset.Now, _newest = DateTimeOffset.Now;
    private float _from, _to = 1;    // the handles, 0 (oldest) to 1 (newest)
    private int _dragged = -1;       // 0: from, 1: to, -1: none

    public DateRangeSlider()
    {
        CustomMinimumSize = new Vector2(240, 64);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        TooltipText = "The dates shown in the 3D graph: drag the left handle from the oldest date, the right one from the newest · double-click: all the dates";
    }

    /// <summary>Raised with the dates chosen and whether they are all of them (from the oldest to the newest).</summary>
    public event Action<DateTimeOffset, DateTimeOffset, bool>? RangeChanged;

    /// <summary>The dates of the graph and the ones shown, without raising <see cref="RangeChanged"/> (while no handle is dragged).</summary>
    public void SetBounds(DateTimeOffset oldest, DateTimeOffset newest, DateTimeOffset from, DateTimeOffset to)
    {
        if (_dragged >= 0)
            return;
        (_oldest, _newest) = (oldest, newest > oldest ? newest : oldest);
        _from = ValueOf(from);
        _to = ValueOf(to);
        QueueRedraw();
    }

    private double Span => (_newest - _oldest).TotalSeconds;

    private float ValueOf(DateTimeOffset date) => Span <= 0 ? 0 : (float)Math.Clamp((date - _oldest).TotalSeconds / Span, 0, 1);

    private DateTimeOffset DateOf(float value) => _oldest + TimeSpan.FromSeconds(Span * value);

    private float XOf(float value) => Side + value * Math.Max(1, Size.X - Side * 2);

    private float ValueAt(float x) => Math.Clamp((x - Side) / Math.Max(1, Size.X - Side * 2), 0, 1);

    private string Format(DateTimeOffset date) =>
        date.ToLocalTime().ToString(Span >= 2 * 86400 ? "yyyy-MM-dd" : Span >= 2 * 3600 ? "dd/MM HH:mm" : "HH:mm:ss");

    private bool Whole => _from <= 0.0001f && _to >= 0.9999f;

    private void Raise() => RangeChanged?.Invoke(DateOf(_from), DateOf(_to), Whole);

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true }:
                _dragged = -1;
                (_from, _to) = (0, 1);
                QueueRedraw();
                Raise();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
            {
                var value = ValueAt(press.Position.X);
                // The nearest handle (the right one when they meet and the click is after them).
                _dragged = Math.Abs(value - _from) < Math.Abs(value - _to) || (Math.Abs(_from - _to) < 0.001f && value < _from) ? 0 : 1;
                Move(value);
                AcceptEvent();
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _dragged >= 0:
                _dragged = -1;
                Raise();
                QueueRedraw();
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _dragged >= 0:
                Move(ValueAt(motion.Position.X));
                AcceptEvent();
                break;
        }
    }

    private void Move(float value)
    {
        // The handles do not cross: at least 1% of the track between them.
        if (_dragged == 0)
            _from = Math.Min(value, _to - 0.01f);
        else
            _to = Math.Max(value, _from + 0.01f);
        _from = Math.Clamp(_from, 0, 1);
        _to = Math.Clamp(_to, 0, 1);
        // Only the handle and its date move: the graph waits for the handle to be let go.
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = Eve.Eve.Body;
        var left = XOf(0);
        var right = XOf(1);
        var a = XOf(_from);
        var b = XOf(_to);
        // The track, the dates shown in the accent colour, a tick every tenth.
        DrawLine(new Vector2(left, TrackY), new Vector2(right, TrackY), Eve.Eve.Edge, 4, true);
        DrawLine(new Vector2(a, TrackY), new Vector2(b, TrackY), Eve.Eve.Cyan, 4, true);
        for (var i = 0; i <= 10; i++)
        {
            var x = XOf(i / 10f);
            DrawLine(new Vector2(x, TrackY + 6), new Vector2(x, TrackY + (i % 5 == 0 ? 12 : 9)), Eve.Eve.Muted, 1);
        }
        foreach (var (x, value, index) in new[] { (a, _from, 0), (b, _to, 1) })
        {
            var color = _dragged == index ? Eve.Eve.Amber : Eve.Eve.Cyan;
            DrawCircle(new Vector2(x, TrackY), HandleRadius, Eve.Eve.Field);
            DrawArc(new Vector2(x, TrackY), HandleRadius, 0, Mathf.Tau, 24, color, 2.5f, true);
            DrawCircle(new Vector2(x, TrackY), 3, color);
            var text = (index == 0 ? "FROM " : "TO ") + Format(DateOf(value));
            var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, 12).X;
            // Above its handle, kept inside the control and away from the other one.
            var textX = index == 0 ? Math.Clamp(x - width / 2, 0, Math.Max(0, b - width - 12)) : Math.Clamp(x - width / 2, a + 12, Math.Max(a + 12, Size.X - width));
            textX = Math.Clamp(textX, 0, Math.Max(0, Size.X - width));
            DrawString(font, new Vector2(textX, TrackY - 14), text, HorizontalAlignment.Left, -1, 12, color);
        }
        // The ends of the graph.
        DrawString(font, new Vector2(left - 4, TrackY + 24), Format(_oldest), HorizontalAlignment.Left, -1, 10, Eve.Eve.Muted);
        var newest = Format(_newest);
        var newestWidth = font.GetStringSize(newest, HorizontalAlignment.Left, -1, 10).X;
        DrawString(font, new Vector2(right - newestWidth + 4, TrackY + 24), newest, HorizontalAlignment.Left, -1, 10, Eve.Eve.Muted);
    }
}
