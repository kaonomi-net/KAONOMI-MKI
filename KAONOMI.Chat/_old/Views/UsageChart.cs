using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace KAONOMI.Chat.Views;

/// <summary>
/// Activity graph in the Task Manager style: the last <see cref="Capacity"/> values as a filled line, newest on the
/// right, over a 25 % grid; the top of the graph is <see cref="Maximum"/> (100 for percentages). An optional second
/// series (<see cref="SecondaryValues"/>) is drawn as a line on the same scale. Redrawn whenever a bound collection
/// changes.
/// </summary>
public sealed class UsageChart : Control
{
    /// <summary>Values to draw (an observable collection is followed).</summary>
    public static readonly StyledProperty<IEnumerable<double>?> ValuesProperty =
        AvaloniaProperty.Register<UsageChart, IEnumerable<double>?>(nameof(Values));

    /// <summary>Second series, drawn as a line without fill (e.g. sent next to received).</summary>
    public static readonly StyledProperty<IEnumerable<double>?> SecondaryValuesProperty =
        AvaloniaProperty.Register<UsageChart, IEnumerable<double>?>(nameof(SecondaryValues));

    /// <summary>Line color (the fill uses the same color, transparent).</summary>
    public static readonly StyledProperty<Color> LineColorProperty =
        AvaloniaProperty.Register<UsageChart, Color>(nameof(LineColor), Colors.DodgerBlue);

    /// <summary>Line color of the second series.</summary>
    public static readonly StyledProperty<Color> SecondaryLineColorProperty =
        AvaloniaProperty.Register<UsageChart, Color>(nameof(SecondaryLineColor), Colors.Orange);

    /// <summary>Value shown at the top of the graph (100 for percentages).</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<UsageChart, double>(nameof(Maximum), 100);

    /// <summary>Number of points across the width.</summary>
    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<UsageChart, int>(nameof(Capacity), 60);

    static UsageChart()
    {
        AffectsRender<UsageChart>(ValuesProperty, SecondaryValuesProperty, LineColorProperty, SecondaryLineColorProperty,
            MaximumProperty, CapacityProperty);
    }

    /// <inheritdoc cref="ValuesProperty" />
    public IEnumerable<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <inheritdoc cref="SecondaryValuesProperty" />
    public IEnumerable<double>? SecondaryValues
    {
        get => GetValue(SecondaryValuesProperty);
        set => SetValue(SecondaryValuesProperty, value);
    }

    /// <inheritdoc cref="LineColorProperty" />
    public Color LineColor
    {
        get => GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    /// <inheritdoc cref="SecondaryLineColorProperty" />
    public Color SecondaryLineColor
    {
        get => GetValue(SecondaryLineColorProperty);
        set => SetValue(SecondaryLineColorProperty, value);
    }

    /// <inheritdoc cref="MaximumProperty" />
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <inheritdoc cref="CapacityProperty" />
    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ValuesProperty && change.Property != SecondaryValuesProperty)
            return;

        if (change.OldValue is INotifyCollectionChanged oldValues)
            oldValues.CollectionChanged -= OnValuesChanged;
        if (change.NewValue is INotifyCollectionChanged newValues)
            newValues.CollectionChanged += OnValuesChanged;
    }

    private void OnValuesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 2 || bounds.Height < 2)
            return;

        var color = LineColor;
        context.DrawRectangle(new SolidColorBrush(color, 0.06), new Pen(new SolidColorBrush(color, 0.6)), bounds);

        // 25 % grid.
        var gridPen = new Pen(new SolidColorBrush(color, 0.18));
        for (var i = 1; i < 4; i++)
        {
            var y = bounds.Height * i / 4;
            context.DrawLine(gridPen, new Point(0, y), new Point(bounds.Width, y));
        }
        for (var i = 1; i < 6; i++)
        {
            var x = bounds.Width * i / 6;
            context.DrawLine(gridPen, new Point(x, 0), new Point(x, bounds.Height));
        }

        var capacity = System.Math.Max(2, Capacity);
        var maximum = Maximum > 0 ? Maximum : 100;
        var step = bounds.Width / (capacity - 1);

        // Newest value on the right edge; older ones to the left.
        List<Point> Points(IEnumerable<double>? source)
        {
            var values = source?.TakeLast(capacity).ToList() ?? [];
            var startX = bounds.Width - step * (values.Count - 1);
            return values.Select((v, i) =>
                new Point(startX + step * i, bounds.Height - System.Math.Clamp(v / maximum, 0, 1) * bounds.Height)).ToList();
        }

        var points = Points(Values);
        if (points.Count >= 2)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(points[0].X, bounds.Height), isFilled: true);
                foreach (var point in points)
                    g.LineTo(point);
                g.LineTo(new Point(bounds.Width, bounds.Height));
                g.EndFigure(isClosed: true);
            }
            context.DrawGeometry(new SolidColorBrush(color, 0.25), null, area);
            DrawLine(context, points, new Pen(new SolidColorBrush(color), 1.5));
        }

        var secondary = Points(SecondaryValues);
        if (secondary.Count >= 2)
            DrawLine(context, secondary, new Pen(new SolidColorBrush(SecondaryLineColor), 1.5));
    }

    private static void DrawLine(DrawingContext context, List<Point> points, IPen pen)
    {
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], isFilled: false);
            for (var i = 1; i < points.Count; i++)
                g.LineTo(points[i]);
            g.EndFigure(isClosed: false);
        }
        context.DrawGeometry(null, pen, line);
    }
}
