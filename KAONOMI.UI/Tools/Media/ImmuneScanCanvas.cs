using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// The picture of /immune while it scans, drawn flat (no animation): an immune cell (its membrane, its nucleus showing
/// the files read, the phase and the time) with each finding placed around it as a pathogen, on three orbits (a violet
/// hexagon for an AI image, a spiked virus coloured by severity for code in an Office file, a green diamond for an AI
/// service of the browsing history); the newest finding is named. It is drawn again when the scan reports progress or
/// a finding. Idle (no scan), the cell is shown alone with READY.
/// </summary>
public sealed class ImmuneScanCanvas : Control
{
    // The cell lives on a dark background whatever the theme: its colours are fixed.
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(4, 6, 10));
    private static readonly IBrush NucleusFill = new SolidColorBrush(Color.FromRgb(8, 18, 28));
    private static readonly IBrush CellFill = new SolidColorBrush(Color.FromRgb(8, 24, 34));
    private static readonly IBrush Glow = new SolidColorBrush(ImmuneCharts.Cyan);
    private static readonly IBrush GlowDim = new SolidColorBrush(Color.FromRgb(20, 70, 90));
    private static readonly IBrush Bright = new SolidColorBrush(Color.FromRgb(237, 247, 255));
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromRgb(140, 166, 191));

    private sealed record Pathogen(ImmuneHit Hit, double Angle, int Lane, Color Color);

    private readonly ImmuneWindowViewModel _vm;
    private readonly List<Pathogen> _pathogens = [];
    private readonly Random _random = new();
    private int _count;

    public ImmuneScanCanvas(ImmuneWindowViewModel vm)
    {
        _vm = vm;
        IsHitTestVisible = false;
        ClipToBounds = true;
        Action<ImmuneHit> found = hit => Ui.OnMain(() => Add(hit));
        PropertyChangedEventHandler changed = (_, e) =>
        {
            if (e.PropertyName is nameof(vm.Files) or nameof(vm.Phase) or nameof(vm.Seconds) or nameof(vm.IsRunning) or nameof(vm.Stage))
                Ui.OnMain(InvalidateVisual);
        };
        AttachedToVisualTree += (_, _) =>
        {
            vm.Found += found;
            vm.PropertyChanged += changed;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            vm.Found -= found;
            vm.PropertyChanged -= changed;
        };
        // The findings already there (the window opened again while it scans).
        foreach (var hit in vm.Feed)
            Add(hit);
    }

    private bool Running => _vm.IsRunning;

    private void Add(ImmuneHit hit)
    {
        var color = hit.Type == "image" ? ImmuneCharts.Violet : hit.Type == "history" ? ImmuneCharts.Green : hit.Level switch
        {
            "high" => ImmuneCharts.Crimson,
            "medium" => ImmuneCharts.Orange,
            _ => ImmuneCharts.Cyan,
        };
        _pathogens.Add(new Pathogen(hit, _random.NextDouble() * 2 * Math.PI, _count++ % 3, color));
        if (_pathogens.Count > 60)
            _pathogens.RemoveAt(0);
        InvalidateVisual();
    }

    private double Radius => Math.Min(Bounds.Width, Bounds.Height) * 0.27;

    private static Point At(Point center, double angle, double radius) => new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);

    private static FormattedText Text(string text, double size, IBrush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var center = new Point(size.Width / 2, size.Height / 2);
        var radius = Radius;
        context.FillRectangle(Background, new Rect(size));
        if (radius <= 4)
            return;

        // The orbits of the findings.
        var thin = new Pen(GlowDim, 1);
        for (var lane = 0; lane < 3; lane++)
        {
            var r = radius * (1.47 + lane * 0.08);
            context.DrawEllipse(null, thin, center, r, r);
        }

        // The cell: its membrane and receptors.
        context.DrawEllipse(CellFill, new Pen(Glow, 1.6), center, radius, radius);
        var receptor = new Pen(Glow, 1.2);
        for (var i = 0; i < 24; i++)
        {
            var a = i * 2 * Math.PI / 24;
            var from = At(center, a, radius);
            context.DrawLine(receptor, from, At(center, a, radius + 7));
            context.DrawEllipse(Glow, null, At(center, a, radius + 8), 1.8, 1.8);
        }

        // The nucleus: the files read, the phase, the time.
        var nucleus = radius * 0.42;
        context.DrawEllipse(NucleusFill, new Pen(Glow, 2), center, nucleus, nucleus);
        var big = Running || _vm.Files > 0 ? _vm.Files.ToString("N0", CultureInfo.CurrentCulture) : "READY";
        var bigSize = Math.Clamp(nucleus * 0.36, 14, 44);
        var bigText = Text(big, bigSize, Bright, true);
        context.DrawText(bigText, new Point(center.X - bigText.Width / 2, center.Y - bigText.Height / 2));
        var caption = Running ? (_vm.Phase == "Scanning" ? "FILES INSPECTED" : (_vm.Phase ?? string.Empty).ToUpperInvariant()) : "IMMUNE SYSTEM";
        var captionText = Text(caption, 10, Glow);
        context.DrawText(captionText, new Point(center.X - captionText.Width / 2, center.Y + bigText.Height / 2 + 2));
        if (Running)
        {
            var time = Text(TimeSpan.FromSeconds(_vm.Seconds).ToString(@"mm\:ss"), 11, Dim);
            context.DrawText(time, new Point(center.X - time.Width / 2, center.Y - bigText.Height / 2 - time.Height - 2));
        }

        // The findings, on their orbits.
        foreach (var pathogen in _pathogens)
            DrawPathogen(context, At(center, pathogen.Angle, radius * (1.47 + pathogen.Lane * 0.08)), pathogen);

        // The newest finding, named.
        if (_pathogens.Count > 0)
        {
            var last = _pathogens[^1];
            var at = At(center, last.Angle, radius * (1.47 + last.Lane * 0.08));
            var kind = last.Hit.Type switch { "image" => "AI IMAGE", "history" => "AI SERVICE", _ => "OFFICE CODE" };
            var title = Text($"{kind} · {last.Hit.Label}", 11, new SolidColorBrush(last.Color), true);
            title.MaxTextWidth = 320;
            title.MaxLineCount = 1;
            title.Trimming = TextTrimming.CharacterEllipsis;
            var name = Text(last.Hit.Name, 11, Bright);
            name.MaxTextWidth = 320;
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            var x = Math.Min(at.X + 16, Math.Max(0, size.Width - Math.Max(title.Width, name.Width) - 4));
            context.DrawText(title, new Point(x, at.Y - 24));
            context.DrawText(name, new Point(x, at.Y - 9));
        }
    }

    private static void DrawPathogen(DrawingContext context, Point at, Pathogen p)
    {
        const double size = 9;
        var brush = new SolidColorBrush(p.Color);
        var pen = new Pen(brush, 1.6);
        Geometry Polygon(int sides, double radius, double start)
        {
            var geometry = new StreamGeometry();
            using var g = geometry.Open();
            g.BeginFigure(At(at, start, radius), true);
            for (var i = 1; i < sides; i++)
                g.LineTo(At(at, start + i * 2 * Math.PI / sides, radius));
            g.EndFigure(true);
            return geometry;
        }
        if (p.Hit.Type == "history")
        {
            // An AI service of the browsing history: a diamond with a dot.
            context.DrawGeometry(null, pen, Polygon(4, size * 1.1, 0));
            context.DrawEllipse(brush, null, at, size * 0.25, size * 0.25);
        }
        else if (p.Hit.Type == "image")
        {
            context.DrawGeometry(null, pen, Polygon(6, size, 0));
            context.DrawEllipse(brush, null, at, size * 0.28, size * 0.28);
        }
        else
        {
            var spike = new Pen(brush, 1.4);
            for (var i = 0; i < 9; i++)
            {
                var a = i * 2 * Math.PI / 9;
                context.DrawLine(spike, At(at, a, size * 0.8), At(at, a, size * 1.45));
                context.DrawEllipse(brush, null, At(at, a, size * 1.5), 1.8, 1.8);
            }
            context.DrawEllipse(null, new Pen(brush, 1.5), at, size * 0.85, size * 0.85);
        }
    }
}
