using System;
using System.Collections.Generic;
using Godot;
using KAONOMI.Chat.ViewModels;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Immune;

/// <summary>
/// The animation of /immune while it scans: an immune cell (a wobbling membrane, its nucleus showing the files read, a
/// radar sweep) that the files flow into as particles, at the pace the scanner reads them. Each finding appears as a
/// pathogen around the cell (a violet hexagon for an AI image, a spiked virus coloured by severity for code in an
/// Office file, a green diamond for an AI service of the browsing history); antibodies fly from the nucleus to tag it, then it joins the outer orbit with the others.
/// Idle (no scan), the cell only breathes.
/// </summary>
public sealed partial class ImmuneScanCanvas : Control
{
    public static readonly Color Violet = Color.FromHtml("#B07CFF");
    public static readonly Color Crimson = Color.FromHtml("#FF4D5E");
    public static readonly Color Orange = Color.FromHtml("#FFA940");

    // The cell lives on a dark background whatever the theme: its colours are fixed.
    private static readonly Color Glow = Color.FromHtml("#3FD0FF");
    private static readonly Color Bright = new(0.93f, 0.97f, 1f);
    private static readonly Color Dim = new(0.55f, 0.65f, 0.75f);

    private sealed class Particle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Size;
        public float Life = 1;
        public bool Absorbed;
    }

    private sealed class Pathogen
    {
        public required ImmuneHit Hit;
        public float Angle;
        public float Born;
        public float Tagged = -1;
        public int Lane;
        public float Spin;
        public Color Color;
    }

    private sealed class Shot
    {
        public required Pathogen Target;
        public float Start;
        public float Offset;
    }

    private readonly ImmuneWindowViewModel _vm;
    private readonly List<Particle> _particles = [];
    private readonly List<Pathogen> _pathogens = [];
    private readonly List<Shot> _antibodies = [];
    private readonly RandomNumberGenerator _random = new();
    private float _time;
    private float _spawn;
    private long _lastFiles;
    private float _rate = 3;
    private int _count;

    public ImmuneScanCanvas(ImmuneWindowViewModel vm)
    {
        _vm = vm;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;
        _random.Randomize();
        Action<ImmuneHit> found = hit => Eve.Bind.OnMain(() => Add(hit));
        vm.Found += found;
        TreeExiting += () => vm.Found -= found;
    }

    private bool Running => _vm.IsRunning;

    private void Add(ImmuneHit hit)
    {
        if (!IsInstanceValid(this))
            return;
        var color = hit.Type == "image" ? Violet : hit.Type == "history" ? ImmuneCharts.Green : hit.Level switch
        {
            "high" => Crimson,
            "medium" => Orange,
            _ => Glow,
        };
        var pathogen = new Pathogen
        {
            Hit = hit,
            Angle = _random.RandfRange(0, Mathf.Tau),
            Born = _time,
            Lane = _count % 3,
            Spin = _random.RandfRange(-0.25f, 0.25f),
            Color = color,
        };
        _count++;
        _pathogens.Add(pathogen);
        if (_pathogens.Count > 60)
            _pathogens.RemoveAt(0);
        for (var i = 0; i < 3; i++)
            _antibodies.Add(new Shot { Target = pathogen, Start = _time + 0.25f + i * 0.12f, Offset = (i - 1) * 0.35f });
    }

    public override void _Process(double delta)
    {
        var dt = (float)Math.Min(delta, 0.1);
        _time += dt;

        // Particles: the files read per second, shown as a stream (a few when idle).
        var files = _vm.Files;
        var readNow = Math.Max(0, files - _lastFiles) / Math.Max(dt, 0.001f);
        _lastFiles = files;
        var target = Running ? Mathf.Clamp(6 + readNow / 40f, 6, 70) : 2.5f;
        _rate = Mathf.Lerp(_rate, target, dt * 2);
        _spawn += _rate * dt;
        var center = Size / 2;
        var radius = Radius;
        while (_spawn >= 1)
        {
            _spawn--;
            var angle = _random.RandfRange(0, Mathf.Tau);
            var from = center + Vector2.FromAngle(angle) * (Size.Length() / 2 + 10);
            var aim = center + Vector2.FromAngle(angle + _random.RandfRange(-0.5f, 0.5f)) * radius * 0.2f;
            _particles.Add(new Particle
            {
                Position = from,
                Velocity = (aim - from).Normalized() * _random.RandfRange(90, Running ? 260 : 120),
                Size = _random.RandfRange(1.2f, 2.6f),
            });
        }
        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Position += p.Velocity * dt;
            if (!p.Absorbed && p.Position.DistanceTo(center) < radius * Membrane(Mathf.Atan2(p.Position.Y - center.Y, p.Position.X - center.X)))
            {
                p.Absorbed = true;
                p.Velocity *= 0.35f;
            }
            if (p.Absorbed)
                p.Life -= dt * 1.6f;
            if (p.Life <= 0 || p.Position.DistanceTo(center) < radius * 0.42f)
                _particles.RemoveAt(i);
        }
        _antibodies.RemoveAll(a =>
        {
            if (_time - a.Start < 0.8f)
                return false;
            if (a.Target.Tagged < 0)
                a.Target.Tagged = _time;
            return true;
        });
        QueueRedraw();
    }

    private float Radius => Mathf.Min(Size.X, Size.Y) * 0.27f;

    private float Membrane(float angle)
    {
        var speed = Running ? 2.2f : 0.8f;
        return 1 + 0.025f * Mathf.Sin(6 * angle + _time * speed) + 0.018f * Mathf.Sin(11 * angle - _time * speed * 1.3f)
            + 0.01f * Mathf.Sin(_time * 1.7f);
    }

    public override void _Draw()
    {
        var center = Size / 2;
        var radius = Radius;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.015f, 0.025f, 0.04f));
        HexGrid(center);

        // The outer orbit, dashed, turning.
        var orbit = radius * 1.55f;
        var dashes = 72;
        var turn = _time * (Running ? 0.15f : 0.05f);
        for (var i = 0; i < dashes; i += 2)
        {
            var a0 = turn + i * Mathf.Tau / dashes;
            DrawArc(center, orbit, a0, a0 + Mathf.Tau / dashes * 0.8f, 4, Glow with { A = 0.18f }, 1, true);
        }
        DrawArc(center, radius * 1.22f, 0, Mathf.Tau, 128, Glow with { A = 0.07f }, 1, true);

        // The radar sweep.
        var sweep = _time * (Running ? 1.6f : 0.5f);
        const int Slices = 28;
        for (var i = 0; i < Slices; i++)
        {
            var a0 = sweep - i * 0.035f;
            var a1 = a0 - 0.035f;
            var alpha = (Running ? 0.22f : 0.08f) * (1 - i / (float)Slices);
            DrawColoredPolygon([center, center + Vector2.FromAngle(a0) * radius * 1.5f, center + Vector2.FromAngle(a1) * radius * 1.5f], Glow with { A = alpha });
        }

        // Particles: the files.
        foreach (var p in _particles)
            DrawCircle(p.Position, p.Size * (p.Absorbed ? Mathf.Max(p.Life, 0.2f) : 1), (p.Absorbed ? Glow : new Color(0.75f, 0.85f, 0.95f)) with { A = 0.25f + 0.6f * p.Life });

        // The membrane, glowing.
        var points = new Vector2[129];
        for (var i = 0; i <= 128; i++)
        {
            var a = i * Mathf.Tau / 128;
            points[i] = center + Vector2.FromAngle(a) * radius * Membrane(a);
        }
        DrawColoredPolygon(points[..128], Glow with { A = 0.05f });
        DrawPolyline(points, Glow with { A = 0.12f }, 9, true);
        DrawPolyline(points, Glow with { A = 0.25f }, 4, true);
        DrawPolyline(points, Glow with { A = 0.9f }, 1.6f, true);

        // Little receptors on the membrane.
        for (var i = 0; i < 24; i++)
        {
            var a = i * Mathf.Tau / 24 + _time * 0.1f;
            var at = center + Vector2.FromAngle(a) * radius * Membrane(a);
            DrawLine(at, at + Vector2.FromAngle(a) * 7, Glow with { A = 0.5f }, 1.2f, true);
            DrawCircle(at + Vector2.FromAngle(a) * 8, 1.8f, Glow with { A = 0.7f });
        }

        // The nucleus.
        var nucleus = radius * 0.42f * (1 + 0.03f * Mathf.Sin(_time * (Running ? 3 : 1.2f)));
        DrawCircle(center, nucleus, new Color(0.03f, 0.07f, 0.11f));
        DrawArc(center, nucleus, 0, Mathf.Tau, 96, Glow with { A = 0.6f }, 2, true);
        DrawArc(center, nucleus * 0.86f, _time, _time + Mathf.Pi * 1.2f, 48, Glow with { A = 0.35f }, 1, true);
        var font = EveStyle.Display;
        var big = Running || _vm.Files > 0 ? _vm.Files.ToString("N0") : "READY";
        var size = (int)Mathf.Clamp(nucleus * 0.36f, 14, 44);
        DrawString(font, center + new Vector2(-nucleus, size * 0.35f), big, HorizontalAlignment.Center, nucleus * 2, size, Bright);
        var caption = Running ? (_vm.Phase == "Scanning" ? "FILES INSPECTED" : _vm.Phase.ToUpperInvariant()) : "IMMUNE SYSTEM";
        DrawString(font, center + new Vector2(-nucleus, size * 0.35f + 18), caption, HorizontalAlignment.Center, nucleus * 2, 10, Glow);
        if (Running)
            DrawString(font, center + new Vector2(-nucleus, -size * 0.6f), TimeSpan.FromSeconds(_vm.Seconds).ToString(@"mm\:ss"), HorizontalAlignment.Center, nucleus * 2, 11, Dim);

        // Pathogens and antibodies.
        foreach (var pathogen in _pathogens)
            DrawPathogen(center, radius, pathogen);
        foreach (var a in _antibodies)
        {
            var k = Mathf.Clamp((_time - a.Start) / 0.8f, 0, 1);
            if (k <= 0)
                continue;
            var from = center + Vector2.FromAngle(a.Target.Angle + a.Offset) * nucleus;
            var to = PathogenAt(center, radius, a.Target);
            var ease = 1 - Mathf.Pow(1 - k, 3);
            var at = from.Lerp(to, ease);
            Antibody(at, (to - from).Angle(), a.Target.Color);
        }

        // The newest finding, named.
        if (_pathogens.Count > 0 && _time - _pathogens[^1].Born < 4)
        {
            var last = _pathogens[^1];
            var at = PathogenAt(center, radius, last);
            var text = $"{last.Hit.Type switch { "image" => "AI IMAGE", "history" => "AI SERVICE", _ => "OFFICE CODE" }} · {last.Hit.Label}";
            var alpha = Mathf.Clamp(4 - (_time - last.Born), 0, 1);
            DrawString(font, at + new Vector2(16, -12), text, HorizontalAlignment.Left, 320, 11, last.Color with { A = alpha });
            DrawString(EveStyle.Body, at + new Vector2(16, 3), last.Hit.Name, HorizontalAlignment.Left, 320, 11, Bright with { A = alpha * 0.8f });
        }
    }

    private Vector2 PathogenAt(Vector2 center, float radius, Pathogen p)
    {
        // Born next to the cell, it moves to its lane of the orbit once tagged.
        var near = radius * 1.28f;
        var lane = radius * (1.47f + p.Lane * 0.08f);
        var k = p.Tagged < 0 ? 0 : Mathf.Clamp((_time - p.Tagged - 0.6f) / 1.4f, 0, 1);
        var r = Mathf.Lerp(near, lane, k * k * (3 - 2 * k));
        var angle = p.Angle + (p.Tagged < 0 ? 0 : Mathf.Max(0, _time - p.Tagged - 0.6f) * 0.12f * (p.Lane % 2 == 0 ? 1 : -1));
        return center + Vector2.FromAngle(angle) * r;
    }

    private void DrawPathogen(Vector2 center, float radius, Pathogen p)
    {
        var at = PathogenAt(center, radius, p);
        var age = _time - p.Born;
        var grow = Mathf.Clamp(age / 0.4f, 0, 1);
        var size = 9 * grow * (1 + 0.08f * Mathf.Sin(_time * 4 + p.Angle * 7));
        var spin = _time * p.Spin * 4;
        if (p.Hit.Type == "history")
        {
            // An AI service of the browsing history: a diamond with a dot.
            var diamond = new Vector2[5];
            for (var i = 0; i <= 4; i++)
                diamond[i] = at + Vector2.FromAngle(spin + i * Mathf.Tau / 4) * size * 1.1f;
            DrawColoredPolygon(diamond[..4], p.Color with { A = 0.25f });
            DrawPolyline(diamond, p.Color, 1.6f, true);
            DrawCircle(at, size * 0.25f, p.Color);
        }
        else if (p.Hit.Type == "image")
        {
            var hex = new Vector2[7];
            for (var i = 0; i <= 6; i++)
                hex[i] = at + Vector2.FromAngle(spin + i * Mathf.Tau / 6) * size;
            DrawColoredPolygon(hex[..6], p.Color with { A = 0.28f });
            DrawPolyline(hex, p.Color, 1.6f, true);
            DrawCircle(at, size * 0.28f, p.Color);
        }
        else
        {
            for (var i = 0; i < 9; i++)
            {
                var dir = Vector2.FromAngle(spin + i * Mathf.Tau / 9);
                DrawLine(at + dir * size * 0.8f, at + dir * size * 1.45f, p.Color with { A = 0.85f }, 1.4f, true);
                DrawCircle(at + dir * size * 1.5f, 1.8f, p.Color);
            }
            DrawCircle(at, size * 0.85f, p.Color with { A = 0.3f });
            DrawArc(at, size * 0.85f, 0, Mathf.Tau, 24, p.Color, 1.5f, true);
        }
        if (p.Tagged >= 0)
        {
            // Tagged: a ring of antibodies around it.
            var ring = size * 2.1f;
            var flash = Mathf.Clamp(1 - (_time - p.Tagged) / 0.5f, 0, 1);
            if (flash > 0)
                DrawArc(at, ring * (1 + (1 - flash) * 1.5f), 0, Mathf.Tau, 32, Colors.White with { A = flash }, 2, true);
            for (var i = 0; i < 3; i++)
            {
                var a = _time * 1.2f + i * Mathf.Tau / 3;
                Antibody(at + Vector2.FromAngle(a) * ring, a + Mathf.Pi, p.Color, 0.75f);
            }
        }
    }

    /// <summary>A Y-shaped antibody at <paramref name="at"/>, its arms pointing along <paramref name="angle"/>.</summary>
    private void Antibody(Vector2 at, float angle, Color color, float scale = 1)
    {
        var dir = Vector2.FromAngle(angle) * 6 * scale;
        var tail = at - dir;
        DrawLine(tail, at, Colors.White with { A = 0.9f }, 1.6f, true);
        DrawLine(at, at + dir.Rotated(0.6f), color.Lightened(0.4f), 1.6f, true);
        DrawLine(at, at + dir.Rotated(-0.6f), color.Lightened(0.4f), 1.6f, true);
    }

    private void HexGrid(Vector2 center)
    {
        const float S = 26;
        var w = S * Mathf.Sqrt(3);
        var h = S * 1.5f;
        var line = Glow with { A = 0.035f };
        var hex = new Vector2[7];
        for (var row = -1; row * h < Size.Y + S; row++)
        {
            for (var col = -1; col * w < Size.X + w; col++)
            {
                var c = new Vector2(col * w + (row % 2 == 0 ? 0 : w / 2), row * h);
                var fade = Mathf.Clamp(1.4f - c.DistanceTo(center) / (Size.Length() / 2), 0.2f, 1);
                for (var i = 0; i <= 6; i++)
                    hex[i] = c + Vector2.FromAngle(Mathf.Pi / 6 + i * Mathf.Tau / 6) * S;
                DrawPolyline(hex, line with { A = line.A * fade }, 1);
            }
        }
    }
}
