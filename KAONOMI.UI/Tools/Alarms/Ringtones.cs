using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Services;

namespace KAONOMI.UI.Tools.Alarms;

/// <summary>
/// The ringtones of /alarm, made by the code as in the Godot chat (Bell, Digital, Chime, Rising), written as WAV files
/// and played on the output of /audiosettings (<see cref="AudioDevices"/>): the first 30 s grow from soft to loud, then
/// the pattern loops at full volume until stopped.
/// </summary>
public static class Ringtones
{
    private const int Rate = 44100;

    /// <summary>Plays <paramref name="name"/> until <paramref name="stop"/> is cancelled (silent when there is no output).</summary>
    public static async Task RingAsync(string name, CancellationToken stop)
    {
        try
        {
            var pattern = Pattern(name);
            var rising = Path.Combine(Path.GetTempPath(), $"kaonomi-ring-{name}-rising.wav");
            var loud = Path.Combine(Path.GetTempPath(), $"kaonomi-ring-{name}.wav");
            // The pattern repeated over 30 s, from 6 % of the volume to all of it.
            var count = (int)Math.Ceiling(30.0 * Rate / pattern.Length);
            var ramp = new float[pattern.Length * count];
            for (var n = 0; n < ramp.Length; n++)
                ramp[n] = pattern[n % pattern.Length] * (float)(0.06 + 0.94 * n / ramp.Length);
            Write(rising, ramp);
            Write(loud, Repeat(pattern, 4));
            await AudioDevices.PlayAsync(rising, stop);
            while (!stop.IsCancellationRequested)
                await AudioDevices.PlayAsync(loud, stop);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Alarm", $"The ringtone {name} could not be played: {ex.Message}");
        }
    }

    private static float[] Repeat(float[] pattern, int times)
    {
        var samples = new float[pattern.Length * times];
        for (var i = 0; i < times; i++)
            Array.Copy(pattern, 0, samples, i * pattern.Length, pattern.Length);
        return samples;
    }

    private static float[] Pattern(string name) => name switch
    {
        "Digital" => Digital(),
        "Chime" => Chime(),
        "Rising" => Rising(),
        _ => Bell()
    };

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

    /// <summary>16-bit mono PCM at 44.1 kHz, normalised.</summary>
    private static void Write(string path, float[] samples)
    {
        var peak = 0f;
        foreach (var v in samples)
            peak = Math.Max(peak, Math.Abs(v));
        var gain = peak > 0 ? 0.85f / peak : 1;
        using var writer = new BinaryWriter(File.Create(path));
        var bytes = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + bytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(bytes);
        foreach (var v in samples)
            writer.Write((short)Math.Clamp(v * gain * 32000, short.MinValue, short.MaxValue));
    }
}
