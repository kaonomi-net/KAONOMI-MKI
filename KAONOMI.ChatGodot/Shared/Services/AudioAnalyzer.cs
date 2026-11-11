using System;
using System.Numerics;

namespace KAONOMI.Chat.Services;

/// <summary>
/// What <see cref="AudioAnalyzer"/> reads in the last instants of a sound: its level, its spectrum, and the pitch and
/// the formants of the voice in it. One instance is reused from one analysis to the next (its arrays are overwritten).
/// </summary>
public sealed class AudioFrame
{
    /// <summary>The peak and the RMS level of the last 50 ms, full scale 1.</summary>
    public float Peak;
    public float Rms;

    /// <summary>The spectrum, one value per band of <see cref="AudioAnalyzer.BandFrequencies"/>, in dB (0: a sine at full scale).</summary>
    public readonly float[] Spectrum = new float[AudioAnalyzer.Bands];

    /// <summary>
    /// The envelope of the spectrum given by the linear prediction (the resonances of the vocal tract), in dB on the
    /// scale of <see cref="Spectrum"/>; NaN above what the analysis of the voice reads, and when there is no voice.
    /// </summary>
    public readonly float[] Envelope = new float[AudioAnalyzer.Bands];

    /// <summary>True when a voice is heard: loud enough, and periodic.</summary>
    public bool Voiced;

    /// <summary>The pitch of the voice (F0), in Hz; 0 when none.</summary>
    public double Pitch;

    /// <summary>The formants found, in Hz, the lowest first (F1, F2, F3, F4); <see cref="FormantCount"/> of them are set.</summary>
    public readonly double[] Formants = new double[4];
    public int FormantCount;

    /// <summary>The French vowel whose F1 and F2 are the closest (an indication, not a recognition); empty when none.</summary>
    public string Vowel = string.Empty;

    public static float Decibels(float linear) => linear <= 1e-6f ? -120f : 20f * MathF.Log10(linear);
}

/// <summary>
/// The analysis of a sound as it is captured, for a level meter, a spectrum analyser and a real-time formant
/// analysis. The audio thread gives the samples (<see cref="Push"/>, no allocation); the interface asks for a frame
/// when it draws (<see cref="Analyze"/>, about a millisecond).
/// <list type="bullet">
/// <item><b>Level</b>: the peak and the RMS of the last 50 ms.</item>
/// <item><b>Spectrum</b>: an FFT of 2048 samples under a Hann window, shown on <see cref="Bands"/> bands spread
/// evenly on a logarithmic scale from 60 Hz to 16 kHz.</item>
/// <item><b>Formants</b>: the last 30 ms brought down to about 11 kHz (the formants of a voice are under 5 kHz),
/// pre-emphasised, under a Hamming window; a linear prediction (autocorrelation, Levinson-Durbin) models the vocal
/// tract; the roots of its polynomial give the resonances: those narrower than 500 Hz, above 90 Hz, are the
/// formants.</item>
/// <item><b>Pitch</b>: the strongest peak of the normalised autocorrelation between 60 and 400 Hz; under 0.4 the
/// sound is not a voice (a noise, a silence) and no formant is given.</item>
/// </list>
/// </summary>
public sealed class AudioAnalyzer
{
    public const int Bands = 72;
    private const int FftSize = 2048;
    private const int RingSize = 8192;
    private const double MinFrequency = 60;
    private const double MaxFrequency = 16000;
    /// <summary>Under this RMS (about -46 dB) nothing is said: no pitch, no formant.</summary>
    private const float VoiceGate = 0.005f;

    private readonly object _lock = new();
    private readonly float[] _ring = new float[RingSize];
    private int _head;
    private long _total;
    private int _rate = 44100;

    private readonly float[] _window = new float[FftSize];
    private readonly float[] _hann = new float[FftSize];
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly float[] _magnitude = new float[FftSize / 2];
    private readonly AudioFrame _frame = new();

    public AudioAnalyzer()
    {
        for (var i = 0; i < FftSize; i++)
            _hann[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (FftSize - 1));
        BandFrequencies = new double[Bands];
        for (var b = 0; b < Bands; b++)
            BandFrequencies[b] = MinFrequency * Math.Pow(MaxFrequency / MinFrequency, b / (double)(Bands - 1));
    }

    /// <summary>The frequency of each band of the spectrum, in Hz.</summary>
    public double[] BandFrequencies { get; }

    /// <summary>The rate of the samples given to <see cref="Push"/>, in Hz.</summary>
    public int Rate
    {
        get => _rate;
        set => _rate = Math.Max(8000, value);
    }

    /// <summary>The samples captured, from the audio thread.</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            foreach (var sample in samples)
            {
                _ring[_head] = sample;
                _head = (_head + 1) % RingSize;
            }
            _total += samples.Length;
        }
    }

    /// <summary>Forgets what was captured (the source changed, or stopped).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_ring);
            _total = 0;
        }
    }

    /// <summary>The last <paramref name="count"/> samples, the oldest first (zeros before the first sample captured).</summary>
    private void Latest(Span<float> target)
    {
        lock (_lock)
        {
            var start = (_head - target.Length + RingSize * 2) % RingSize;
            for (var i = 0; i < target.Length; i++)
                target[i] = _ring[(start + i) % RingSize];
        }
    }

    /// <summary>The analysis of the last instants. The frame returned is reused by the next call.</summary>
    public AudioFrame Analyze()
    {
        var frame = _frame;
        Latest(_window);

        // The level, on the last 50 ms.
        var recent = Math.Min(FftSize, Math.Max(256, _rate / 20));
        float peak = 0, sum = 0;
        for (var i = FftSize - recent; i < FftSize; i++)
        {
            var sample = _window[i];
            peak = MathF.Max(peak, MathF.Abs(sample));
            sum += sample * sample;
        }
        frame.Peak = peak;
        frame.Rms = MathF.Sqrt(sum / recent);

        Spectrum(frame);
        Voice(frame);
        return frame;
    }

    // ── The spectrum ──

    private void Spectrum(AudioFrame frame)
    {
        for (var i = 0; i < FftSize; i++)
        {
            _re[i] = _window[i] * _hann[i];
            _im[i] = 0;
        }
        Fft(_re, _im);
        // A sine at full scale under a Hann window gives N / 4 in its bin: 0 dB.
        const float scale = 4f / FftSize;
        for (var i = 0; i < FftSize / 2; i++)
            _magnitude[i] = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]) * scale;

        var binWidth = _rate / (double)FftSize;
        var ratio = Math.Pow(MaxFrequency / MinFrequency, 0.5 / (Bands - 1));
        for (var b = 0; b < Bands; b++)
        {
            var center = BandFrequencies[b];
            if (center >= _rate / 2.0)
            {
                frame.Spectrum[b] = -120;
                continue;
            }
            // The loudest bin of the band (one bin at least: the low bands are narrower than a bin).
            var from = (int)Math.Floor(center / ratio / binWidth);
            var to = (int)Math.Ceiling(center * ratio / binWidth);
            from = Math.Clamp(from, 1, FftSize / 2 - 1);
            to = Math.Clamp(Math.Max(to, from + 1), from + 1, FftSize / 2);
            var loudest = 0f;
            for (var i = from; i < to; i++)
                loudest = MathF.Max(loudest, _magnitude[i]);
            frame.Spectrum[b] = AudioFrame.Decibels(loudest);
        }
    }

    /// <summary>An in-place radix-2 FFT (the length is a power of two).</summary>
    internal static void Fft(float[] re, float[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var wRe = (float)Math.Cos(angle);
            var wIm = (float)Math.Sin(angle);
            for (var i = 0; i < n; i += length)
            {
                float curRe = 1, curIm = 0;
                for (var k = 0; k < length / 2; k++)
                {
                    var a = i + k;
                    var b = a + length / 2;
                    var tRe = re[b] * curRe - im[b] * curIm;
                    var tIm = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }

    // ── The voice: its pitch and its formants ──

    private const int MaxOrder = 20;
    private double[] _speech = [];
    private double[] _emphasised = [];
    private readonly double[] _autocorrelation = new double[MaxOrder + 1];
    private readonly double[] _lpc = new double[MaxOrder + 1];
    private readonly double[] _previous = new double[MaxOrder + 1];
    private readonly Complex[] _roots = new Complex[MaxOrder];

    private void Voice(AudioFrame frame)
    {
        frame.Voiced = false;
        frame.Pitch = 0;
        frame.FormantCount = 0;
        frame.Vowel = string.Empty;
        Array.Fill(frame.Envelope, float.NaN);
        if (frame.Rms < VoiceGate)
            return;

        // The last 30 ms, brought down to about 11 kHz (each sample the mean of `step` samples: a low-pass, then one in `step`).
        var step = Math.Max(1, (int)Math.Round(_rate / 11025.0));
        var rate = _rate / (double)step;
        var length = Math.Min(FftSize / step, (int)(0.03 * rate));
        if (length < 128)
            return;
        if (_speech.Length < length)
        {
            _speech = new double[length];
            _emphasised = new double[length];
        }
        var first = FftSize - length * step;
        for (var i = 0; i < length; i++)
        {
            double sum = 0;
            for (var k = 0; k < step; k++)
                sum += _window[first + i * step + k];
            _speech[i] = sum / step;
        }

        frame.Pitch = Pitch(_speech, length, rate);
        frame.Voiced = frame.Pitch > 0;
        if (!frame.Voiced)
            return;

        // Pre-emphasis (the voice loses 6 dB per octave: the high formants would be missed), then a Hamming window.
        _emphasised[0] = _speech[0] * 0.08;
        for (var i = 1; i < length; i++)
            _emphasised[i] = (_speech[i] - 0.97 * _speech[i - 1]) * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (length - 1)));

        // The linear prediction: two coefficients per formant expected (one per kHz), and two for the slope.
        var order = Math.Clamp(2 + (int)Math.Round(rate / 1000), 8, MaxOrder);
        for (var lag = 0; lag <= order; lag++)
        {
            double sum = 0;
            for (var i = lag; i < length; i++)
                sum += _emphasised[i] * _emphasised[i - lag];
            _autocorrelation[lag] = sum;
        }
        if (_autocorrelation[0] < 1e-12)
            return;
        var error = LevinsonDurbin(order);
        if (error <= 0)
            return;

        Formants(frame, order, rate);
        Envelope(frame, order, rate, Math.Sqrt(error));
        frame.Vowel = frame.FormantCount >= 2 ? ClosestVowel(frame.Formants[0], frame.Formants[1]) : string.Empty;
    }

    /// <summary>The pitch by the autocorrelation: its strongest peak between 60 and 400 Hz, when it is a real periodicity.</summary>
    private static double Pitch(double[] samples, int length, double rate)
    {
        double energy = 0;
        for (var i = 0; i < length; i++)
            energy += samples[i] * samples[i];
        if (energy < 1e-9)
            return 0;
        var minLag = Math.Max(2, (int)(rate / 400));
        var maxLag = Math.Min(length - 2, (int)(rate / 60));
        if (maxLag <= minLag)
            return 0;
        // Normalised by the energy of what overlaps: a perfect period gives 1 whatever the lag.
        Span<double> values = stackalloc double[maxLag + 2];
        for (var lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            double sum = 0, tail = 0;
            for (var i = lag; i < length; i++)
            {
                sum += samples[i] * samples[i - lag];
                tail += samples[i] * samples[i];
            }
            values[lag] = tail > 0 ? sum / Math.Sqrt(tail * energy) : 0;
        }
        var bestLag = minLag;
        for (var lag = minLag; lag <= maxLag; lag++)
            if (values[lag] > values[bestLag])
                bestLag = lag;
        var best = values[bestLag];
        // A real periodicity: strong, and a peak (not the slope of a slow wave at the edge of the range).
        if (best < 0.4 || values[bestLag - 1] > best || values[bestLag + 1] > best)
            return 0;
        // The top of the peak, between the lags.
        var denominator = values[bestLag - 1] - 2 * best + values[bestLag + 1];
        var shift = Math.Abs(denominator) > 1e-9 ? 0.5 * (values[bestLag - 1] - values[bestLag + 1]) / denominator : 0;
        return rate / (bestLag + Math.Clamp(shift, -0.5, 0.5));
    }

    /// <summary>The coefficients of the prediction (<c>_lpc[0]</c> = 1) from the autocorrelation; returns the error left.</summary>
    private double LevinsonDurbin(int order)
    {
        Array.Clear(_lpc);
        _lpc[0] = 1;
        var error = _autocorrelation[0];
        for (var i = 1; i <= order; i++)
        {
            var reflection = _autocorrelation[i];
            for (var j = 1; j < i; j++)
                reflection += _lpc[j] * _autocorrelation[i - j];
            reflection = -reflection / error;
            Array.Copy(_lpc, _previous, order + 1);
            for (var j = 1; j < i; j++)
                _lpc[j] = _previous[j] + reflection * _previous[i - j];
            _lpc[i] = reflection;
            error *= 1 - reflection * reflection;
            if (error <= 0)
                return 0;
        }
        return error;
    }

    /// <summary>The roots of the prediction polynomial (Durand-Kerner): each pair of them is a resonance.</summary>
    private void Formants(AudioFrame frame, int order, double rate)
    {
        var seed = new Complex(0.4, 0.9);
        var point = Complex.One;
        for (var i = 0; i < order; i++)
        {
            _roots[i] = point;
            point *= seed;
        }
        for (var iteration = 0; iteration < 60; iteration++)
        {
            var moved = 0.0;
            for (var i = 0; i < order; i++)
            {
                // z^order + a1 z^(order-1) + … + a_order, at the root.
                var value = Complex.One;
                for (var k = 1; k <= order; k++)
                    value = value * _roots[i] + _lpc[k];
                var divisor = Complex.One;
                for (var j = 0; j < order; j++)
                    if (j != i)
                        divisor *= _roots[i] - _roots[j];
                if (divisor == Complex.Zero)
                    continue;
                var delta = value / divisor;
                _roots[i] -= delta;
                moved = Math.Max(moved, delta.Magnitude);
            }
            if (moved < 1e-10)
                break;
        }

        Span<double> found = stackalloc double[MaxOrder];
        var count = 0;
        for (var i = 0; i < order; i++)
        {
            var root = _roots[i];
            if (root.Imaginary <= 0.01 || double.IsNaN(root.Real))
                continue;
            var frequency = Math.Atan2(root.Imaginary, root.Real) * rate / (2 * Math.PI);
            var bandwidth = -Math.Log(Math.Max(root.Magnitude, 1e-9)) * rate / Math.PI;
            // A resonance of the voice: above the pitch, narrow enough.
            if (frequency > 90 && frequency < rate / 2 - 50 && bandwidth is > 0 and < 500)
                found[count++] = frequency;
        }
        var formants = found[..count];
        formants.Sort();
        frame.FormantCount = Math.Min(4, count);
        for (var i = 0; i < frame.FormantCount; i++)
            frame.Formants[i] = formants[i];
    }

    /// <summary>The envelope of the prediction on the bands of the spectrum, brought to the level of the spectrum.</summary>
    private void Envelope(AudioFrame frame, int order, double rate, double gain)
    {
        var top = float.NegativeInfinity;
        var spectrumTop = float.NegativeInfinity;
        for (var b = 0; b < Bands; b++)
        {
            var frequency = BandFrequencies[b];
            if (frequency >= rate / 2)
                break;
            var omega = 2 * Math.PI * frequency / rate;
            double re = 1, im = 0;
            for (var k = 1; k <= order; k++)
            {
                re += _lpc[k] * Math.Cos(k * omega);
                im -= _lpc[k] * Math.Sin(k * omega);
            }
            var value = (float)(20 * Math.Log10(gain / Math.Max(1e-9, Math.Sqrt(re * re + im * im))));
            frame.Envelope[b] = value;
            top = MathF.Max(top, value);
            spectrumTop = MathF.Max(spectrumTop, frame.Spectrum[b]);
        }
        // The envelope is the one of the pre-emphasised voice, on another scale: its top put on the top of the spectrum.
        var offset = spectrumTop - top;
        for (var b = 0; b < Bands; b++)
            if (!float.IsNaN(frame.Envelope[b]))
                frame.Envelope[b] += offset;
    }

    /// <summary>The oral vowels of French and their usual first two formants (Hz, an adult voice).</summary>
    private static readonly (string Vowel, double F1, double F2)[] Vowels =
    [
        ("i", 280, 2250), ("e", 370, 2100), ("ɛ", 530, 1850), ("a", 720, 1300), ("ɔ", 550, 950), ("o", 380, 800),
        ("u", 300, 750), ("y", 280, 1800), ("ø", 380, 1450), ("œ", 520, 1400)
    ];

    private static string ClosestVowel(double f1, double f2)
    {
        var best = string.Empty;
        var bestDistance = double.MaxValue;
        foreach (var (vowel, v1, v2) in Vowels)
        {
            // On a logarithmic scale, as the ear hears the frequencies.
            var d1 = Math.Log(f1 / v1);
            var d2 = Math.Log(f2 / v2);
            var distance = d1 * d1 + d2 * d2;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = vowel;
            }
        }
        // Too far from every vowel: no indication.
        return bestDistance < 0.12 ? best : string.Empty;
    }
}
