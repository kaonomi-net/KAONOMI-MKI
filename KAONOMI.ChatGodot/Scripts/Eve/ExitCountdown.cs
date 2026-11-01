using System;
using Godot;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The countdown of <c>/bye</c> over the whole screen: a veil, the seconds left from 10 to 1 in large digits that pop in
/// with each second, a ring that empties during the second, and a tick of a clock each second (a deeper one at 1). Escape
/// or a click cancels the exit.
/// </summary>
public partial class ExitCountdown : Control
{
    private const int Rate = 44100;

    private readonly Action _cancel;
    private readonly AudioStreamPlayer _tick = new() { VolumeDb = -4 };
    private readonly AudioStreamWav _tickSound = MakeTick(false);
    private readonly AudioStreamWav _lastTickSound = MakeTick(true);
    private int _remaining = -1;
    private double _since;

    /// <summary>A countdown whose cancel (Escape, a click) runs <paramref name="cancel"/>.</summary>
    public ExitCountdown(Action cancel)
    {
        _cancel = cancel;
        Name = nameof(ExitCountdown);
        MouseFilter = MouseFilterEnum.Stop;
        Modulate = Colors.Transparent;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_tick);
        CreateTween().TweenProperty(this, "modulate:a", 1f, 0.25);
    }

    /// <summary>Shows <paramref name="remaining"/> seconds left, with its tick.</summary>
    public void SetRemaining(int remaining)
    {
        if (remaining == _remaining)
            return;
        _remaining = remaining;
        _since = 0;
        _tick.Stream = remaining == 1 ? _lastTickSound : _tickSound;
        _tick.Play();
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _since += delta;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true })
        {
            AcceptEvent();
            _cancel();
        }
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            GetViewport().SetInputAsHandled();
            _cancel();
        }
    }

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.02f, 0.03f, 0.05f, 0.82f));
        if (_remaining < 0)
            return;
        var m = Math.Min(size.X, size.Y);
        var centre = size / 2;
        var second = (float)Math.Min(1, _since);
        var colour = _remaining == 1 ? Eve.Amber : Eve.Cyan;

        // The ring: the whole circle faint, and the part of the second left bright, emptying clockwise from the top.
        var radius = m * 0.27f;
        var width = Math.Max(3, m * 0.012f);
        DrawArc(centre, radius, 0, MathF.Tau, 128, colour with { A = 0.15f }, width, true);
        var left = 1 - second;
        if (left > 0)
            DrawArc(centre, radius, -MathF.PI / 2, -MathF.PI / 2 + MathF.Tau * left, 128, colour with { A = 0.9f }, width, true);
        // Ten marks around it, those of the seconds gone dimmed.
        for (var k = 0; k < 10; k++)
        {
            var a = -MathF.PI / 2 + k * MathF.Tau / 10;
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            var lit = k < _remaining;
            DrawLine(centre + dir * (radius + width * 2), centre + dir * (radius + width * 4.5f), colour with { A = lit ? 0.85f : 0.2f }, width * 0.6f, true);
        }

        // The digit pops in: larger and bright at the tick, then settles.
        var pop = 1 + 0.25f * MathF.Exp(-second * 9);
        var fontSize = (int)(m * 0.3f * pop);
        var font = Eve.Display;
        var text = _remaining.ToString();
        var textSize = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize);
        var at = centre + new Vector2(-textSize.X / 2, font.GetAscent(fontSize) / 2 - font.GetDescent(fontSize) / 2 - fontSize * 0.08f);
        DrawString(font, at, text, HorizontalAlignment.Left, -1, fontSize, Colors.White with { A = 0.6f + 0.4f * MathF.Exp(-second * 4) });

        var caption = (int)(m * 0.028f);
        DrawString(font, new Vector2(0, centre.Y + radius + m * 0.12f), "CLOSING  ·  ESCAPE OR CLICK TO STAY",
            HorizontalAlignment.Center, size.X, caption, colour with { A = 0.85f });
    }

    /// <summary>
    /// The tick of a clock: a click of noise rung through a few resonances of wood and metal, and a little knock under
    /// it; <paramref name="last"/>, a deeper and longer one (the end of the countdown).
    /// </summary>
    private static AudioStreamWav MakeTick(bool last)
    {
        var random = new Random(last ? 3 : 2);
        var frames = (int)((last ? 0.5 : 0.18) * Rate);
        var samples = new float[frames];
        double[] tones = last ? new double[] { 620, 1240, 1710 } : new double[] { 2200, 3400, 5100 };
        var decay = last ? 0.09 : 0.018;
        for (var n = 0; n < frames; n++)
        {
            var t = n / (double)Rate;
            var click = (random.NextDouble() * 2 - 1) * Math.Exp(-t / 0.0012);
            var ring = 0.0;
            for (var k = 0; k < tones.Length; k++)
                ring += Math.Sin(Math.Tau * tones[k] * t) * Math.Exp(-t / (decay / (k + 1))) / (k + 1);
            var knock = Math.Sin(Math.Tau * (last ? 110 : 180) * t) * Math.Exp(-t / (last ? 0.06 : 0.012));
            samples[n] = (float)(click * 0.5 + ring * 0.6 + knock * 0.5);
        }
        var peak = 0f;
        foreach (var v in samples)
            peak = Math.Max(peak, Math.Abs(v));
        var gain = peak > 0 ? 0.9f / peak : 1;
        var data = new byte[frames * 2];
        for (var n = 0; n < frames; n++)
        {
            var v = (short)(samples[n] * gain * 32000);
            data[n * 2] = (byte)v;
            data[n * 2 + 1] = (byte)(v >> 8);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Data = data };
    }
}
