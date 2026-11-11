using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace KAONOMI.Chat.Views;

/// <summary>A slice of a <see cref="PieChart"/>: its label, its value and its color.</summary>
public sealed record PieSlice(string Label, int Value, IBrush Brush)
{
    /// <summary>Share of the whole, set by the view model (0 to 1).</summary>
    public double Share { get; init; }

    /// <summary>Text of the legend: the label, the value and its percentage.</summary>
    public string Legend => $"{Label}  {Value} ({Share:P0})";
}

/// <summary>
/// Pie chart of <see cref="Slices"/>: one slice per value, from the top clockwise, each with a thin gap in the color
/// of the background; a single slice is a full disc. Nothing is drawn without a value.
/// </summary>
public sealed class PieChart : Control
{
    /// <summary>The slices to draw.</summary>
    public static readonly StyledProperty<IReadOnlyList<PieSlice>?> SlicesProperty =
        AvaloniaProperty.Register<PieChart, IReadOnlyList<PieSlice>?>(nameof(Slices));

    /// <summary>Color of the gaps between the slices.</summary>
    public static readonly StyledProperty<IBrush?> GapBrushProperty =
        AvaloniaProperty.Register<PieChart, IBrush?>(nameof(GapBrush), Brushes.White);

    static PieChart()
    {
        AffectsRender<PieChart>(SlicesProperty, GapBrushProperty);
    }

    /// <inheritdoc cref="SlicesProperty" />
    public IReadOnlyList<PieSlice>? Slices
    {
        get => GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    /// <inheritdoc cref="GapBrushProperty" />
    public IBrush? GapBrush
    {
        get => GetValue(GapBrushProperty);
        set => SetValue(GapBrushProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var slices = Slices?.Where(s => s.Value > 0).ToList();
        var total = slices?.Sum(s => s.Value) ?? 0;
        if (slices is null || total <= 0)
            return;

        var radius = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) / 2 - 2);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var gap = new Pen(GapBrush, 2);
        if (slices.Count == 1)
        {
            context.DrawEllipse(slices[0].Brush, null, center, radius, radius);
            return;
        }

        var start = -Math.PI / 2;
        foreach (var slice in slices)
        {
            var sweep = 2 * Math.PI * slice.Value / total;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(center, true);
                g.LineTo(PointAt(center, radius, start));
                g.ArcTo(PointAt(center, radius, start + sweep), new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise);
                g.EndFigure(true);
            }
            context.DrawGeometry(slice.Brush, gap, geometry);
            start += sweep;
        }
    }

    private static Point PointAt(Point center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
}
