using System;
using Godot;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The eyes of the dock of the NeXT stations of the 1990s (the Eyes tile, after xeyes): two white eyes with black
/// pupils that follow the pointer over the whole screen, drawn straight on the Neocom (no tile under them: the bar
/// shows through). At the top of the Neocom, under the K.
/// </summary>
public partial class NextEyes : Control
{
    private const float TileWidth = 44;
    private const float TileHeight = 30;
    private const float EyeRadiusX = 8;
    private const float EyeRadiusY = 11;
    private const float PupilRadius = 3.2f;
    /// <summary>Distance of the pointer (pixels) at which the pupil reaches the edge of the eye.</summary>
    private const float Reach = 40;

    private static readonly Color White = new(1, 1, 1);
    private static readonly Color Black = new(0.05f, 0.05f, 0.05f);

    private Vector2 _lastPointer = new(float.NaN, float.NaN);

    public NextEyes()
    {
        CustomMinimumSize = new Vector2(TileWidth, TileHeight);
        SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText = "Eyes · as on the dock of the NeXT stations (1990): they follow the pointer";
    }

    public override void _Process(double delta)
    {
        // Redrawn only when the pointer moved (the position is read in the coordinates of the canvas, where the
        // control is too, so the eyes follow the pointer over the whole window).
        var pointer = GetGlobalMousePosition();
        if (pointer == _lastPointer)
            return;
        _lastPointer = pointer;
        QueueRedraw();
    }

    public override void _Draw()
    {
        // No tile: the eyes alone, the bar showing between and around them.
        var middle = Size / 2;
        DrawEye(middle + new Vector2(-EyeRadiusX - 1.5f, 0));
        DrawEye(middle + new Vector2(EyeRadiusX + 1.5f, 0));
    }

    /// <summary>One eye: a white ellipse with a black outline, and its pupil towards the pointer.</summary>
    private void DrawEye(Vector2 center)
    {
        var outline = Ellipse(center, EyeRadiusX, EyeRadiusY);
        DrawPolygon(outline, [White]);
        DrawPolyline(outline, Black, 1.5f);

        // As xeyes: the pupil goes towards the pointer, and sits on the edge of the eye when the pointer is away.
        var toPointer = _lastPointer - (GlobalPosition + center);
        var offset = Vector2.Zero;
        if (float.IsFinite(toPointer.X) && toPointer.Length() > 0.5f)
        {
            var angle = MathF.Atan2(toPointer.Y, toPointer.X);
            var edge = new Vector2((EyeRadiusX - PupilRadius - 1.2f) * MathF.Cos(angle), (EyeRadiusY - PupilRadius - 1.2f) * MathF.Sin(angle));
            offset = edge * MathF.Min(1, toPointer.Length() / Reach);
        }
        DrawCircle(center + offset, PupilRadius, Black);
    }

    private static Vector2[] Ellipse(Vector2 center, float radiusX, float radiusY)
    {
        const int Points = 36;
        var points = new Vector2[Points + 1];
        for (var i = 0; i <= Points; i++)
        {
            var a = i * MathF.Tau / Points;
            points[i] = center + new Vector2(radiusX * MathF.Cos(a), radiusY * MathF.Sin(a));
        }
        return points;
    }
}
