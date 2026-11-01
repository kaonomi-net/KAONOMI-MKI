using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Godot;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// Port of the Avalonia <c>UsageChart</c>: an activity graph in the Task Manager style, the last <see cref="Capacity"/>
/// values as a filled line (newest on the right) over a 25 % grid, the top being <see cref="Maximum"/>; an optional
/// second series drawn as a line on the same scale. Redrawn whenever a followed collection changes. Under the pointer:
/// a vertical cursor and a tooltip with the values (through <see cref="Format"/>).
/// </summary>
public partial class UsageChart : Control
{
    private IEnumerable<double>? _values;
    private IEnumerable<double>? _secondary;
    private Action? _unsubscribe;
    private float _hover = -1;

    public UsageChart()
    {
        CustomMinimumSize = new Vector2(0, 56);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText = " ";
        MouseExited += () =>
        {
            _hover = -1;
            QueueRedraw();
        };
    }

    public Color LineColor { get; set; } = DashUi.Blue;
    public Color SecondaryLineColor { get; set; } = DashUi.Orange;

    /// <summary>Value at the top of the graph (100 for percentages).</summary>
    public double Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            QueueRedraw();
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
        NotifyCollectionChangedEventHandler handler = (_, _) => Bind.OnMain(() =>
        {
            if (IsInstanceValid(this))
                QueueRedraw();
        });
        var sources = new[] { values, secondary }.OfType<INotifyCollectionChanged>().ToList();
        foreach (var source in sources)
            source.CollectionChanged += handler;
        _unsubscribe = () =>
        {
            foreach (var source in sources)
                source.CollectionChanged -= handler;
        };
        QueueRedraw();
        return this;
    }

    /// <summary>A chart of <paramref name="values"/> whose top follows the number property <paramref name="maximum"/> of <paramref name="source"/>.</summary>
    public static UsageChart Of(IEnumerable<double> values, Color color, int capacity, IEnumerable<double>? secondary = null, Color? secondaryColor = null,
        INotifyPropertyChanged? source = null, string? maximum = null, int height = 56)
    {
        var chart = new UsageChart { LineColor = color, Capacity = capacity, CustomMinimumSize = new Vector2(0, height) };
        if (secondaryColor is { } c)
            chart.SecondaryLineColor = c;
        chart.Follow(values, secondary);
        if (source is not null && maximum is not null)
            Bind.When(chart, source, () =>
            {
                if (Bind.Get(source, maximum) is IConvertible value)
                    chart.Maximum = value.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
            }, maximum);
        return chart;
    }

    public override void _ExitTree()
    {
        _unsubscribe?.Invoke();
        _unsubscribe = null;
    }

    public override void _EnterTree()
    {
        if (_unsubscribe is null && (_values is not null || _secondary is not null))
            Follow(_values, _secondary);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion motion)
        {
            _hover = motion.Position.X;
            QueueRedraw();
        }
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
    }

    /// <summary>The index (from the end) of the point under <paramref name="x"/>, or -1.</summary>
    private int IndexAt(float x, int count)
    {
        var capacity = Math.Max(2, Capacity);
        var step = Size.X / (capacity - 1);
        var fromEnd = (int)Math.Round((Size.X - x) / step);
        return fromEnd >= 0 && fromEnd < count ? count - 1 - fromEnd : -1;
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        var values = Snapshot(_values).TakeLast(Math.Max(2, Capacity)).ToArray();
        var secondary = Snapshot(_secondary).TakeLast(Math.Max(2, Capacity)).ToArray();
        var index = IndexAt(atPosition.X, values.Length);
        if (index < 0)
            return string.Empty;
        var text = (SeriesName.Length > 0 ? SeriesName + ": " : string.Empty) + Format(values[index]);
        var second = IndexAt(atPosition.X, secondary.Length);
        if (second >= 0)
            text += "\n" + (SecondaryName.Length > 0 ? SecondaryName + ": " : string.Empty) + Format(secondary[second]);
        return text;
    }

    public override void _Draw()
    {
        var size = Size;
        if (size.X < 2 || size.Y < 2)
            return;
        var color = LineColor;
        DrawRect(new Rect2(Vector2.Zero, size), color with { A = 0.06f });
        DrawRect(new Rect2(Vector2.Zero, size), color with { A = 0.6f }, false, 1);

        // 25 % grid.
        var grid = color with { A = 0.18f };
        for (var i = 1; i < 4; i++)
            DrawLine(new Vector2(0, size.Y * i / 4), new Vector2(size.X, size.Y * i / 4), grid);
        for (var i = 1; i < 6; i++)
            DrawLine(new Vector2(size.X * i / 6, 0), new Vector2(size.X * i / 6, size.Y), grid);

        var capacity = Math.Max(2, Capacity);
        var maximum = Maximum > 0 ? Maximum : 100;
        var step = size.X / (capacity - 1);

        Vector2[] Points(IEnumerable<double>? source)
        {
            var values = Snapshot(source).TakeLast(capacity).ToArray();
            var startX = size.X - step * (values.Length - 1);
            return values.Select((v, i) => new Vector2(startX + step * i, size.Y - (float)Math.Clamp(v / maximum, 0, 1) * size.Y)).ToArray();
        }

        var points = Points(_values);
        if (points.Length >= 2)
        {
            var area = new Vector2[points.Length + 2];
            area[0] = new Vector2(points[0].X, size.Y);
            points.CopyTo(area, 1);
            area[^1] = new Vector2(size.X, size.Y);
            var fill = color with { A = 0.25f };
            // Drawn as strips (a polygon of a jagged line may not triangulate).
            for (var i = 0; i < points.Length - 1; i++)
                DrawPrimitive([points[i], points[i + 1], new Vector2(points[i + 1].X, size.Y), new Vector2(points[i].X, size.Y)], [fill, fill, fill, fill], []);
            DrawPolyline(points, color, 1.5f, true);
        }

        var secondary = Points(_secondary);
        if (secondary.Length >= 2)
            DrawPolyline(secondary, SecondaryLineColor, 1.5f, true);

        if (_hover >= 0 && _hover <= size.X && points.Length > 0)
        {
            var index = IndexAt(_hover, points.Length);
            if (index >= 0)
            {
                DrawLine(new Vector2(points[index].X, 0), new Vector2(points[index].X, size.Y), Godot.Colors.White with { A = 0.35f });
                DrawCircle(points[index], 3, color);
            }
        }
    }
}
