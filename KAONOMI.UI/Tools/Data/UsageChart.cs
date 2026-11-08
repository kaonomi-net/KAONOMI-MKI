using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// An activity graph in the Task Manager style (port of the <c>UsageChart</c> of the chat): the last
/// <see cref="Capacity"/> values as a filled line (newest on the right) over a 25 % grid, the top being
/// <see cref="Maximum"/>; an optional second series drawn as a line on the same scale. Redrawn whenever a followed
/// collection changes. Under the pointer: a vertical cursor and a tooltip with the values (through <see cref="Format"/>).
/// </summary>
public sealed class UsageChart : Control
{
    private IEnumerable<double>? _values;
    private IEnumerable<double>? _secondary;
    private Action? _unsubscribe;
    private double _hover = -1;
    private int _tipIndex = -1;

    public UsageChart()
    {
        MinHeight = 56;
        ClipToBounds = true;
        AttachedToVisualTree += (_, _) =>
        {
            if (_unsubscribe is null && (_values is not null || _secondary is not null))
                Follow(_values, _secondary);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _unsubscribe?.Invoke();
            _unsubscribe = null;
        };
    }

    public Color LineColor { get; set; } = Color.Parse("#2B9BF4");
    public Color SecondaryLineColor { get; set; } = Color.Parse("#F28C38");

    /// <summary>Value at the top of the graph (100 for percentages).</summary>
    public double Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            InvalidateVisual();
        }
    }

    private double _maximum = 100;

    /// <summary>Number of points across the width.</summary>
    public int Capacity { get; set; } = 60;

    /// <summary>Text of a value in the tooltip.</summary>
    public Func<double, string> Format { get; set; } = v => v.ToString("0.#");

    /// <summary>Names of the series in the tooltip.</summary>
    public string SeriesName { get; set; } = string.Empty;
    public string SecondaryName { get; set; } = string.Empty;

    /// <summary>Follows <paramref name="values"/> (and <paramref name="secondary"/>): redrawn at each change.</summary>
    public UsageChart Follow(IEnumerable<double>? values, IEnumerable<double>? secondary = null)
    {
        _unsubscribe?.Invoke();
        _values = values;
        _secondary = secondary;
        NotifyCollectionChangedEventHandler handler = (_, _) => Ui.OnMain(InvalidateVisual);
        var sources = new object?[] { values, secondary }.OfType<INotifyCollectionChanged>().Distinct().ToList();
        foreach (var source in sources)
            source.CollectionChanged += handler;
        _unsubscribe = () =>
        {
            foreach (var source in sources)
                source.CollectionChanged -= handler;
        };
        InvalidateVisual();
        return this;
    }

    /// <summary>A chart of <paramref name="values"/> whose top follows the number property <paramref name="maximum"/> of <paramref name="source"/>.</summary>
    public static UsageChart Of(IEnumerable<double> values, string color, int capacity, IEnumerable<double>? secondary = null, string? secondaryColor = null,
        INotifyPropertyChanged? source = null, string? maximum = null, double height = 56)
    {
        var chart = new UsageChart { LineColor = Color.Parse(color), Capacity = capacity, MinHeight = height };
        if (secondaryColor is not null)
            chart.SecondaryLineColor = Color.Parse(secondaryColor);
        chart.Follow(values, secondary);
        if (source is not null && maximum is not null)
            Ui.When(chart, source, () =>
            {
                if (DataUi.Get(source, maximum) is IConvertible value)
                    chart.Maximum = value.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
            }, maximum);
        return chart;
    }

    private static double[] Snapshot(IEnumerable<double>? source)
    {
        try
        {
            return source?.ToArray() ?? [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    /// <summary>The index (from the start of the points shown) of the point under <paramref name="x"/>, or -1.</summary>
    private int IndexAt(double x, int count)
    {
        var capacity = Math.Max(2, Capacity);
        var step = Bounds.Width / (capacity - 1);
        if (step <= 0)
            return -1;
        var fromEnd = (int)Math.Round((Bounds.Width - x) / step);
        return fromEnd >= 0 && fromEnd < count ? count - 1 - fromEnd : -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this).X;
        InvalidateVisual();
        var capacity = Math.Max(2, Capacity);
        var values = Snapshot(_values).TakeLast(capacity).ToArray();
        var secondary = Snapshot(_secondary).TakeLast(capacity).ToArray();
        var index = IndexAt(_hover, values.Length);
        if (index == _tipIndex)
            return;
        _tipIndex = index;
        if (index < 0)
        {
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetTip(this, null);
            return;
        }
        var text = (SeriesName.Length > 0 ? SeriesName + ": " : string.Empty) + Format(values[index]);
        var second = IndexAt(_hover, secondary.Length);
        if (second >= 0)
            text += "\n" + (SecondaryName.Length > 0 ? SecondaryName + ": " : string.Empty) + Format(secondary[second]);
        ToolTip.SetTip(this, text);
        ToolTip.SetIsOpen(this, true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        _tipIndex = -1;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }

    private static IBrush Brush(Color color, double opacity) => new SolidColorBrush(color, opacity);

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2)
            return;
        var color = LineColor;
        var whole = new Rect(size);
        context.FillRectangle(Brush(color, 0.06), whole);
        context.DrawRectangle(new Pen(Brush(color, 0.6), 1), whole.Deflate(0.5));

        // 25 % grid.
        var grid = new Pen(Brush(color, 0.18), 1);
        for (var i = 1; i < 4; i++)
            context.DrawLine(grid, new Point(0, size.Height * i / 4), new Point(size.Width, size.Height * i / 4));
        for (var i = 1; i < 6; i++)
            context.DrawLine(grid, new Point(size.Width * i / 6, 0), new Point(size.Width * i / 6, size.Height));

        var capacity = Math.Max(2, Capacity);
        var maximum = Maximum > 0 ? Maximum : 100;
        var step = size.Width / (capacity - 1);

        Point[] Points(IEnumerable<double>? source)
        {
            var values = Snapshot(source).TakeLast(capacity).ToArray();
            var startX = size.Width - step * (values.Length - 1);
            return values.Select((v, i) => new Point(startX + step * i, size.Height - Math.Clamp(double.IsFinite(v) ? v / maximum : 0, 0, 1) * size.Height)).ToArray();
        }

        var points = Points(_values);
        if (points.Length >= 2)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(points[0].X, size.Height), true);
                foreach (var point in points)
                    g.LineTo(point);
                g.LineTo(new Point(points[^1].X, size.Height));
                g.EndFigure(true);
            }
            context.DrawGeometry(Brush(color, 0.25), null, area);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.5), Line(points));
        }

        var secondary = Points(_secondary);
        if (secondary.Length >= 2)
            context.DrawGeometry(null, new Pen(new SolidColorBrush(SecondaryLineColor), 1.5), Line(secondary));

        if (_hover >= 0 && _hover <= size.Width && points.Length > 0)
        {
            var index = IndexAt(_hover, points.Length);
            if (index >= 0)
            {
                context.DrawLine(new Pen(Brush(Colors.White, 0.35), 1), new Point(points[index].X, 0), new Point(points[index].X, size.Height));
                context.DrawEllipse(new SolidColorBrush(color), null, points[index], 3, 3);
            }
        }
    }

    private static StreamGeometry Line(Point[] points)
    {
        var line = new StreamGeometry();
        using var g = line.Open();
        g.BeginFigure(points[0], false);
        for (var i = 1; i < points.Length; i++)
            g.LineTo(points[i]);
        g.EndFigure(false);
        return line;
    }
}
