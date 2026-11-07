using System;
using System.Collections.Generic;
using Godot;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The ringtones of /alarm, made by the code (as the tick of /bye), each a pattern of a few seconds that loops:
/// <list type="bullet">
/// <item><b>Bell</b>: the double strike of the bells of an old alarm clock, rung fast;</item>
/// <item><b>Digital</b>: four beeps and a silence, the alarm of a bedside clock;</item>
/// <item><b>Chime</b>: a soft arpeggio C E G C of struck metal;</item>
/// <item><b>Rising</b>: a tone pulsing faster and higher along the pattern.</item>
/// </list>
/// </summary>
public static class Ringtones
{
    private const int Rate = 44100;

    private static readonly Dictionary<string, AudioStreamWav> Made = [];

    /// <summary>The ringtone <paramref name="name"/> (Bell when unknown), looping.</summary>
    public static AudioStreamWav Get(string name)
    {
        if (Made.TryGetValue(name, out var made))
            return made;
        var samples = name switch
        {
            "Digital" => Digital(),
            "Chime" => Chime(),
            "Rising" => Rising(),
            _ => Bell()
        };
        return Made[name] = Wav(samples);
    }

    /// <summary>A hammer between two bells, 18 strikes a second for 1.2 s, then 0.6 s of silence.</summary>
    private static float[] Bell()
    {
        var samples = new float[(int)(1.8 * Rate)];
        double[] partials = [1, 2.76, 5.4, 8.93];
        for (var strike = 0; strike < 22; strike++)
        {
            var start = (int)(strike / 18.0 * Rate);
            var pitch = strike % 2 == 0 ? 1850.0 : 2080.0;
            for (var n = start; n < samples.Length; n++)
            {
                var t = (n - start) / (double)Rate;
                if (t > 0.5)
                    break;
                var v = 0.0;
                for (var k = 0; k < partials.Length; k++)
                    v += Math.Sin(Math.Tau * pitch * partials[k] / 2.2 * t) * Math.Exp(-t * (14 + k * 10)) / (k + 1);
                samples[n] += (float)v;
            }
        }
        return samples;
    }

    /// <summary>Four square beeps at 2 kHz, 0.09 s each, then a silence: 1.2 s.</summary>
    private static float[] Digital()
    {
        var samples = new float[(int)(1.2 * Rate)];
        for (var beep = 0; beep < 4; beep++)
        {
            var start = (int)(beep * 0.16 * Rate);
            var length = (int)(0.09 * Rate);
            for (var n = 0; n < length; n++)
            {
                var t = n / (double)Rate;
                var square = Math.Sign(Math.Sin(Math.Tau * 2000 * t)) * 0.6 + Math.Sin(Math.Tau * 2000 * t) * 0.4;
                var edge = Math.Min(1, Math.Min(n, length - n) / (0.004 * Rate));
                samples[start + n] = (float)(square * edge * 0.7);
            }
        }
        return samples;
    }

    /// <summary>C5 E5 G5 C6 struck one after the other, each ringing on: 2.4 s.</summary>
    private static float[] Chime()
    {
        var samples = new float[(int)(2.4 * Rate)];
        double[] notes = [523.25, 659.25, 783.99, 1046.5];
        for (var i = 0; i < notes.Length; i++)
        {
            var start = (int)(i * 0.28 * Rate);
            for (var n = start; n < samples.Length; n++)
            {
                var t = (n - start) / (double)Rate;
                var v = Math.Sin(Math.Tau * notes[i] * t) + 0.35 * Math.Sin(Math.Tau * notes[i] * 2.01 * t) * Math.Exp(-t * 3) + 0.15 * Math.Sin(Math.Tau * notes[i] * 3.98 * t) * Math.Exp(-t * 6);
                samples[n] += (float)(v * Math.Exp(-t * 1.6) * Math.Min(1, t / 0.003));
            }
        }
        return samples;
    }

    /// <summary>Pulses of a tone going from 600 to 1500 Hz, closer and closer: 2 s.</summary>
    private static float[] Rising()
    {
        var samples = new float[2 * Rate];
        var phase = 0.0;
        for (var n = 0; n < samples.Length; n++)
        {
            var t = n / (double)Rate;
            var progress = t / 2;
            var frequency = 600 + 900 * progress;
            phase += Math.Tau * frequency / Rate;
            var pulseRate = 3 + 9 * progress;
            var gate = Math.Pow(Math.Max(0, Math.Sin(Math.Tau * pulseRate * t)), 0.6);
            samples[n] = (float)((Math.Sin(phase) + 0.3 * Math.Sin(phase * 2)) * gate * (0.5 + 0.5 * progress));
        }
        return samples;
    }

    /// <summary>16-bit mono at 44.1 kHz, normalised, looping over the whole pattern.</summary>
    private static AudioStreamWav Wav(float[] samples)
    {
        var peak = 0f;
        foreach (var v in samples)
            peak = Math.Max(peak, Math.Abs(v));
        var gain = peak > 0 ? 0.85f / peak : 1;
        var data = new byte[samples.Length * 2];
        for (var n = 0; n < samples.Length; n++)
        {
            var v = (short)(samples[n] * gain * 32000);
            data[n * 2] = (byte)v;
            data[n * 2 + 1] = (byte)(v >> 8);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Rate,
            Data = data,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward,
            LoopBegin = 0,
            LoopEnd = samples.Length
        };
    }
}
