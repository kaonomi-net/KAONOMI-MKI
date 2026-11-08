using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// A double slider of dates (under the timeline of the relations): a track from the oldest date to the newest, a handle
/// for the first date shown (it starts at the oldest) and one for the last (it starts at the newest), the dates chosen
/// written above them. Dragging a handle (or clicking the track, the nearest handle going there) moves it and its date;
/// the timeline is updated once, when the handle is let go (<see cref="RangeChanged"/>); a double-click shows all the
/// dates again.
/// </summary>
public sealed class DateRangeSlider : Control
{
    private const double Side = 16;   // margin of the track at each end, for the handles
    private const double TrackY = 34;
    private const double HandleRadius = 8;

    private DateTimeOffset _oldest = DateTimeOffset.Now, _newest = DateTimeOffset.Now;
    private double _from, _to = 1;   // the handles, 0 (oldest) to 1 (newest)
    private int _dragged = -1;       // 0: from, 1: to, -1: none

    public DateRangeSlider()
    {
        Height = 64;
        MinWidth = 240;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "The dates shown: drag the left handle from the oldest date, the right one from the newest · double-click: all the dates");
    }

    /// <summary>Raised with the dates chosen and whether they are all of them (from the oldest to the newest).</summary>
    public event Action<DateTimeOffset, DateTimeOffset, bool>? RangeChanged;

    /// <summary>The dates of the timeline and the ones shown, without raising <see cref="RangeChanged"/> (while no handle is dragged).</summary>
    public void SetBounds(DateTimeOffset oldest, DateTimeOffset newest, DateTimeOffset from, DateTimeOffset to)
    {
        if (_dragged >= 0)
            return;
        (_oldest, _newest) = (oldest, newest > oldest ? newest : oldest);
        _from = ValueOf(from);
        _to = ValueOf(to);
        InvalidateVisual();
    }

    private double Span => (_newest - _oldest).TotalSeconds;

    private double ValueOf(DateTimeOffset date) => Span <= 0 ? 0 : Math.Clamp((date - _oldest).TotalSeconds / Span, 0, 1);

    private DateTimeOffset DateOf(double value) => _oldest + TimeSpan.FromSeconds(Span * value);

    private double XOf(double value) => Side + value * Math.Max(1, Bounds.Width - Side * 2);

    private double ValueAt(double x) => Math.Clamp((x - Side) / Math.Max(1, Bounds.Width - Side * 2), 0, 1);

    private string Format(DateTimeOffset date) =>
        date.ToLocalTime().ToString(Span >= 2 * 86400 ? "yyyy-MM-dd" : Span >= 2 * 3600 ? "dd/MM HH:mm" : "HH:mm:ss");

    private bool Whole => _from <= 0.0001 && _to >= 0.9999;

    private void Raise() => RangeChanged?.Invoke(DateOf(_from), DateOf(_to), Whole);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            _dragged = -1;
            (_from, _to) = (0, 1);
            InvalidateVisual();
            Raise();
            return;
        }
        var value = ValueAt(e.GetPosition(this).X);
        // The nearest handle (the right one when they meet and the click is after them).
        _dragged = Math.Abs(value - _from) < Math.Abs(value - _to) || (Math.Abs(_from - _to) < 0.001 && value < _from) ? 0 : 1;
        e.Pointer.Capture(this);
        Move(value);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragged < 0)
            return;
        Move(ValueAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (_dragged < 0)
            return;
        _dragged = -1;
        Raise();
        InvalidateVisual();
        e.Handled = true;
    }

    private void Move(double value)
    {
        // The handles do not cross: at least 1% of the track between them.
        if (_dragged == 0)
            _from = Math.Min(value, _to - 0.01);
        else
            _to = Math.Max(value, _from + 0.01);
        _from = Math.Clamp(_from, 0, 1);
        _to = Math.Clamp(_to, 0, 1);
        // Only the handle and its date move: the timeline waits for the handle to be let go.
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        Paint.Rect(context, new Rect(Bounds.Size), Colors.Transparent);
        var left = XOf(0);
        var right = XOf(1);
        var a = XOf(_from);
        var b = XOf(_to);
        // The track, the dates shown in the accent colour, a tick every tenth.
        Paint.Line(context, new Point(left, TrackY), new Point(right, TrackY), Paint.Edge, 4);
        Paint.Line(context, new Point(a, TrackY), new Point(b, TrackY), Paint.Cyan, 4);
        for (var i = 0; i <= 10; i++)
        {
            var x = XOf(i / 10.0);
            Paint.Line(context, new Point(x, TrackY + 6), new Point(x, TrackY + (i % 5 == 0 ? 12 : 9)), Paint.Muted);
        }
        foreach (var (x, value, index) in new[] { (a, _from, 0), (b, _to, 1) })
        {
            var color = _dragged == index ? Paint.Amber : Paint.Cyan;
            Paint.Circle(context, new Point(x, TrackY), HandleRadius, Paint.Field);
            Paint.Ring(context, new Point(x, TrackY), HandleRadius, color, 2.5);
            Paint.Circle(context, new Point(x, TrackY), 3, color);
            var text = (index == 0 ? "FROM " : "TO ") + Format(DateOf(value));
            var textWidth = Paint.Measure(text, 12).Width;
            // Above its handle, kept inside the control and away from the other one.
            var textX = index == 0 ? Math.Clamp(x - textWidth / 2, 0, Math.Max(0, b - textWidth - 12)) : Math.Clamp(x - textWidth / 2, a + 12, Math.Max(a + 12, width - textWidth));
            textX = Math.Clamp(textX, 0, Math.Max(0, width - textWidth));
            Paint.Write(context, text, new Point(textX, TrackY - 30), 12, color);
        }
        // The ends of the timeline.
        Paint.Write(context, Format(_oldest), new Point(Math.Max(0, left - 4), TrackY + 12), 10, Paint.Muted);
        var newest = Format(_newest);
        var newestWidth = Paint.Measure(newest, 10).Width;
        Paint.Write(context, newest, new Point(Math.Max(0, right - newestWidth + 4), TrackY + 12), 10, Paint.Muted);
    }
}
