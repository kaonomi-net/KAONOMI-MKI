using System;
using Godot;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The target of Ctrl+M: a reticle drawn around the pointer for two seconds, following it, above everything (its own
/// canvas layer, no mouse filter). The outer ring closes in on the pointer with four turning ticks, two sonar rings
/// spread from the centre, a thin crosshair fades in, a dot marks the pointer; the whole fades out at the end.
/// Ctrl+M again starts the two seconds over.
/// </summary>
public partial class PointerTarget : Control
{
    private const double Duration = 2.0;
    private double _elapsed;

    public PointerTarget()
    {
        Name = nameof(PointerTarget);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready() => SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

    /// <summary>Starts the two seconds again (Ctrl+M while the target is shown).</summary>
    public void Restart() => _elapsed = 0;

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_elapsed >= Duration)
        {
            QueueFree();
            return;
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        var t = (float)Math.Clamp(_elapsed / Duration, 0, 1);
        var center = GetViewport().GetMousePosition();
        var color = Eve.Cyan;
        // In quickly, out during the last third.
        var alpha = Math.Min(1, t * 8) * (t > 0.66f ? 1 - (t - 0.66f) / 0.34f : 1);
        var main = color with { A = alpha };
        var glow = color with { A = alpha * 0.25f };

        // The outer ring closes in on the pointer (eased), its four ticks turning.
        var ease = 1 - MathF.Pow(1 - Math.Min(1, t / 0.7f), 3);
        var radius = 170 - 125 * ease;
        DrawArc(center, radius, 0, Mathf.Tau, 96, glow, 7, true);
        DrawArc(center, radius, 0, Mathf.Tau, 96, main, 2, true);
        var angle = t * Mathf.Pi / 2;
        for (var i = 0; i < 4; i++)
        {
            var a = angle + i * Mathf.Pi / 2;
            var direction = new Vector2(MathF.Cos(a), MathF.Sin(a));
            DrawLine(center + direction * (radius - 12), center + direction * (radius + 12), glow, 6, true);
            DrawLine(center + direction * (radius - 12), center + direction * (radius + 12), main, 2.5f, true);
        }

        // Two sonar rings spreading from the centre, fading as they grow.
        foreach (var offset in new[] { 0f, 0.5f })
        {
            var phase = (t * 1.6f + offset) % 1f;
            var ring = 6 + phase * 150;
            DrawArc(center, ring, 0, Mathf.Tau, 64, color with { A = alpha * (1 - phase) * 0.6f }, 1.5f, true);
        }

        // The crosshair: thin lines with a gap around the pointer, fading in after the ring arrives.
        var cross = Math.Clamp((t - 0.35f) / 0.3f, 0, 1) * alpha;
        var line = color with { A = cross * 0.8f };
        const float gap = 10;
        const float length = 60;
        DrawLine(center + new Vector2(gap, 0), center + new Vector2(length, 0), line, 1.5f, true);
        DrawLine(center - new Vector2(gap, 0), center - new Vector2(length, 0), line, 1.5f, true);
        DrawLine(center + new Vector2(0, gap), center + new Vector2(0, length), line, 1.5f, true);
        DrawLine(center - new Vector2(0, gap), center - new Vector2(0, length), line, 1.5f, true);

        // The pointer itself: a small ring and a dot.
        DrawArc(center, 5, 0, Mathf.Tau, 24, main, 1.5f, true);
        DrawCircle(center, 2, main);
    }
}
