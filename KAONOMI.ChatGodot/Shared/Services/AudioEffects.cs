using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The filters of the recorder, applied to a part of a recording when it is listened to or saved as a sample: a
/// gain, a compressor, a band of frequencies kept (or, inverted, taken out), and an equalizer.
/// </summary>
public sealed class AudioFilterSettings
{
    /// <summary>The bands of the equalizer, in Hz.</summary>
    public static readonly double[] EqualizerHz = [60, 150, 400, 1000, 2400, 6000, 12000];

    public const double MinHz = 20;
    public const double MaxHz = 20000;
    public const double MaxEqualizerDb = 18;

    /// <summary>The gain, in dB, applied last.</summary>
    public double GainDb { get; set; }

    /// <summary>The compressor: above <see cref="ThresholdDb"/>, the level rises <see cref="Ratio"/> times less.</summary>
    public bool Compressor { get; set; }

    public double ThresholdDb { get; set; } = -20;

    public double Ratio { get; set; } = 4;

    /// <summary>The band kept: what is under <see cref="LowHz"/> and above <see cref="HighHz"/> is filtered out.</summary>
    public double LowHz { get; set; } = MinHz;

    public double HighHz { get; set; } = MaxHz;

    /// <summary>The band inverted: what is between <see cref="LowHz"/> and <see cref="HighHz"/> is taken out, the rest kept.</summary>
    public bool InvertBand { get; set; }

    /// <summary>The gain of each band of the equalizer (<see cref="EqualizerHz"/>), in dB.</summary>
    public double[] EqualizerDb { get; set; } = new double[EqualizerHz.Length];

    /// <summary>True when the low edge, the high edge of the band filters something.</summary>
    public bool CutsLow => LowHz > MinHz + 1;

    public bool CutsHigh => HighHz < MaxHz - 1;

    /// <summary>True when nothing is changed: the sound comes out as it went in.</summary>
    public bool IsNeutral => Math.Abs(GainDb) < 0.05 && !Compressor && !CutsLow && !CutsHigh && EqualizerDb.All(g => Math.Abs(g) < 0.05);

    public AudioFilterSettings Clone() => new()
    {
        GainDb = GainDb, Compressor = Compressor, ThresholdDb = ThresholdDb, Ratio = Ratio,
        LowHz = LowHz, HighHz = HighHz, InvertBand = InvertBand, EqualizerDb = (double[])EqualizerDb.Clone()
    };

    /// <summary>The filters as a sentence, for the journal of a recording.</summary>
    public string Describe()
    {
        if (IsNeutral)
            return "no filter";
        var parts = new System.Collections.Generic.List<string>();
        if (CutsLow || CutsHigh)
            parts.Add($"{(InvertBand ? "band out" : "band kept")} {LowHz:0}–{HighHz:0} Hz");
        if (EqualizerDb.Any(g => Math.Abs(g) >= 0.05))
            parts.Add("equalizer " + string.Join(" ", EqualizerDb.Select((g, i) => $"{EqualizerHz[i]:0}:{g:+0.#;-0.#;0}")));
        if (Compressor)
            parts.Add($"compressor {ThresholdDb:0} dB {Ratio:0.#}:1");
        if (Math.Abs(GainDb) >= 0.05)
            parts.Add($"gain {GainDb:+0.#;-0.#} dB");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Applies <see cref="AudioFilterSettings"/> to samples (mono, -1 to 1), in this order: the band (two second-order
/// Butterworth filters at each of its edges, 24 dB per octave; inverted, what is under its low edge and above its
/// high edge is added back instead), the equalizer (one peaking filter per band), the compressor (the peak level
/// followed with a 5 ms attack and a 120 ms release), the gain, then the samples are kept within full scale.
/// </summary>
public static class AudioEffects
{
    /// <summary>A second-order filter (RBJ cookbook), with its two samples of memory.</summary>
    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _z1, _z2;

        public static Biquad LowPass(double hz, int rate) => Make(hz, rate, 0, (cos, alpha, _) => ((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha));

        public static Biquad HighPass(double hz, int rate) => Make(hz, rate, 0, (cos, alpha, _) => ((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha));

        public static Biquad Peaking(double hz, int rate, double gainDb) =>
            Make(hz, rate, gainDb, (cos, alpha, a) => (1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a), q: 1.1);

        private static Biquad Make(double hz, int rate, double gainDb, Func<double, double, double, (double B0, double B1, double B2, double A0, double A1, double A2)> shape, double q = 0.70710678)
        {
            var omega = 2 * Math.PI * Math.Clamp(hz, 10, rate * 0.49) / rate;
            var (b0, b1, b2, a0, a1, a2) = shape(Math.Cos(omega), Math.Sin(omega) / (2 * q), Math.Pow(10, gainDb / 40));
            return new Biquad { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };
        }

        public double Next(double x)
        {
            var y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }

        /// <summary>What the filter does to the frequency <paramref name="hz"/>: its gain and its phase.</summary>
        public readonly Complex Response(double hz, int rate)
        {
            var z1 = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz / rate);
            var z2 = z1 * z1;
            return (_b0 + _b1 * z1 + _b2 * z2) / (1 + _a1 * z1 + _a2 * z2);
        }
    }

    /// <summary>The filters of the band and of the equalizer for <paramref name="settings"/>.</summary>
    private sealed class Chain
    {
        public Biquad[] High = [], Low = [], InvertedLow = [], InvertedHigh = [], Equalizer = [];
        public bool Inverted;

        public Chain(AudioFilterSettings settings, int rate)
        {
            Inverted = settings.InvertBand && (settings.CutsLow || settings.CutsHigh);
            if (Inverted)
            {
                // What is under the low edge, and what is above the high edge.
                if (settings.CutsLow)
                    InvertedLow = [Biquad.LowPass(settings.LowHz, rate), Biquad.LowPass(settings.LowHz, rate)];
                if (settings.CutsHigh && settings.HighHz < rate * 0.49)
                    InvertedHigh = [Biquad.HighPass(settings.HighHz, rate), Biquad.HighPass(settings.HighHz, rate)];
            }
            else
            {
                if (settings.CutsLow)
                    High = [Biquad.HighPass(settings.LowHz, rate), Biquad.HighPass(settings.LowHz, rate)];
                if (settings.CutsHigh && settings.HighHz < rate * 0.49)
                    Low = [Biquad.LowPass(settings.HighHz, rate), Biquad.LowPass(settings.HighHz, rate)];
            }
            Equalizer = AudioFilterSettings.EqualizerHz
                .Select((hz, i) => (hz, Gain: settings.EqualizerDb.ElementAtOrDefault(i)))
                .Where(band => Math.Abs(band.Gain) >= 0.05 && band.hz < rate * 0.49)
                .Select(band => Biquad.Peaking(band.hz, rate, band.Gain))
                .ToArray();
        }
    }

    /// <summary>
    /// The filters applied one sample after the other, as a sound is played or written: the band, the equalizer, the
    /// compressor, the gain. <see cref="Update"/> changes the filters while it runs (from another thread): the next
    /// samples are filtered with the new ones.
    /// </summary>
    public sealed class Processor
    {
        private sealed class State
        {
            public Chain? Chain;
            public double Gain = 1, Slope, ThresholdDb;
            public bool Compressor;
        }

        private readonly int _rate;
        private readonly double _attack, _release;
        private volatile State _state;
        private double _envelope;

        public Processor(int rate, AudioFilterSettings settings)
        {
            _rate = Math.Max(1, rate);
            _attack = Math.Exp(-1 / (0.005 * _rate));
            _release = Math.Exp(-1 / (0.120 * _rate));
            _state = Make(settings);
        }

        private State Make(AudioFilterSettings settings) => new()
        {
            // No chain when nothing is filtered: the samples go through as they are.
            Chain = settings.IsNeutral ? null : new Chain(settings, _rate),
            Gain = Math.Pow(10, settings.GainDb / 20),
            Slope = 1 - 1 / Math.Max(1, settings.Ratio),
            ThresholdDb = settings.ThresholdDb,
            Compressor = settings.Compressor
        };

        /// <summary>Changes the filters: heard from the next sample.</summary>
        public void Update(AudioFilterSettings settings) => _state = Make(settings);

        /// <summary>The sample with the filters applied.</summary>
        public float Next(float sample)
        {
            var state = _state;
            if (state.Chain is not { } chain)
                return sample;
            double x = sample;
            if (chain.Inverted)
            {
                double under = chain.InvertedLow.Length > 0 ? x : 0, above = chain.InvertedHigh.Length > 0 ? x : 0;
                for (var k = 0; k < chain.InvertedLow.Length; k++)
                    under = chain.InvertedLow[k].Next(under);
                for (var k = 0; k < chain.InvertedHigh.Length; k++)
                    above = chain.InvertedHigh[k].Next(above);
                x = under + above;
            }
            else
            {
                for (var k = 0; k < chain.High.Length; k++)
                    x = chain.High[k].Next(x);
                for (var k = 0; k < chain.Low.Length; k++)
                    x = chain.Low[k].Next(x);
            }
            for (var k = 0; k < chain.Equalizer.Length; k++)
                x = chain.Equalizer[k].Next(x);

            if (state.Compressor)
            {
                var level = Math.Abs(x);
                _envelope = level > _envelope ? _attack * _envelope + (1 - _attack) * level : _release * _envelope + (1 - _release) * level;
                var levelDb = 20 * Math.Log10(Math.Max(_envelope, 1e-9));
                if (levelDb > state.ThresholdDb)
                    x *= Math.Pow(10, -(levelDb - state.ThresholdDb) * state.Slope / 20);
            }
            return (float)Math.Clamp(x * state.Gain, -1, 1);
        }
    }

    /// <summary>The samples with the filters applied (a new array; <paramref name="samples"/> is not changed).</summary>
    public static float[] Process(float[] samples, int rate, AudioFilterSettings settings)
    {
        var result = new float[samples.Length];
        var processor = new Processor(rate, settings);
        for (var i = 0; i < samples.Length; i++)
            result[i] = processor.Next(samples[i]);
        return result;
    }

    /// <summary>
    /// What the band, the equalizer and the gain do to the frequency <paramref name="hz"/>, in dB: the curve drawn
    /// by the equalizer (the compressor depends on the level, not on the frequency: it is not in it).
    /// </summary>
    public static double ResponseDb(double hz, int rate, AudioFilterSettings settings)
    {
        var chain = new Chain(settings, rate);
        Complex response;
        if (chain.Inverted)
        {
            Complex under = chain.InvertedLow.Length > 0 ? Complex.One : Complex.Zero, above = chain.InvertedHigh.Length > 0 ? Complex.One : Complex.Zero;
            foreach (var filter in chain.InvertedLow)
                under *= filter.Response(hz, rate);
            foreach (var filter in chain.InvertedHigh)
                above *= filter.Response(hz, rate);
            response = under + above;
        }
        else
        {
            response = Complex.One;
            foreach (var filter in chain.High)
                response *= filter.Response(hz, rate);
            foreach (var filter in chain.Low)
                response *= filter.Response(hz, rate);
        }
        foreach (var filter in chain.Equalizer)
            response *= filter.Response(hz, rate);
        return 20 * Math.Log10(Math.Max(response.Magnitude, 1e-6)) + settings.GainDb;
    }

    /// <summary>
    /// Writes the part of <paramref name="source"/> between <paramref name="start"/> and <paramref name="end"/>
    /// (seconds), with the filters applied, into the WAV file <paramref name="target"/> (mono, 16 bits, at the rate of
    /// the source; written over when it exists). Returns its length, in seconds.
    /// </summary>
    public static double Render(string source, double start, double end, AudioFilterSettings settings, string target)
    {
        float[] samples;
        int rate;
        using (var reader = new WavReader(source))
        {
            rate = reader.Rate;
            var first = Math.Clamp((long)(start * rate), 0, reader.TotalFrames);
            var last = Math.Clamp((long)(end * rate), first, reader.TotalFrames);
            samples = new float[last - first];
            reader.Seek(first);
            var block = new float[8192];
            var at = 0;
            while (at < samples.Length)
            {
                var read = reader.Read(block);
                if (read <= 0)
                    break;
                var count = Math.Min(read, samples.Length - at);
                Array.Copy(block, 0, samples, at, count);
                at += count;
            }
        }
        var processed = Process(samples, rate, settings);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var file = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        Wav.WriteHeader(file, rate, processed.Length);
        var bytes = new byte[processed.Length * 2];
        for (var i = 0; i < processed.Length; i++)
        {
            var value = (short)Math.Round(processed[i] * 32767f);
            bytes[2 * i] = (byte)value;
            bytes[2 * i + 1] = (byte)(value >> 8);
        }
        file.Write(bytes);
        file.Flush(true);
        return rate > 0 ? processed.Length / (double)rate : 0;
    }
}
