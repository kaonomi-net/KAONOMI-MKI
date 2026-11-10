using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The audio analysis of a recording of an interview: how the voice sounds, apart from what it says. The WAV file is
/// read piece by piece and brought to 8 kHz; every 20 ms a frame of 40 ms gives its level and, when it is voiced, its
/// pitch (the fundamental frequency, the strongest period of its autocorrelation between 70 and 400 Hz). From the
/// frames of a span of time (the whole recording, or a paragraph of its transcription) come the pitch and how much it
/// moves (in semitones), the level and how much it moves, the time of speech and the pauses; with the words of the
/// span, how fast they come.
/// </summary>
public static class VoiceAnalyzer
{
    private const int Rate = 8000;
    /// <summary>The frames: 40 ms long (two periods of the lowest pitch and more), one every 20 ms.</summary>
    private const int FrameLength = 320;
    private const int Hop = 160;
    private const double HopSeconds = Hop / (double)Rate;
    private const double LowestPitch = 70;
    private const double HighestPitch = 400;
    /// <summary>Under this share of its energy at the best period, a frame is not voiced (a consonant, a breath, noise).</summary>
    private const float Voicing = 0.5f;
    /// <summary>Under this level (RMS, full scale 1) a frame is never speech.</summary>
    private const float MinimumRms = 0.0015f;
    /// <summary>A silence this long is a pause.</summary>
    private const double PauseSeconds = 0.3;

    /// <summary>A frame: its level, whether it is speech, and its pitch in hertz (0: not voiced).</summary>
    private readonly record struct Frame(float Rms, bool Speech, float Pitch);

    /// <summary>
    /// Analyses the WAV file <paramref name="path"/>: the voice over the whole of it, and over each of
    /// <paramref name="spans"/> (start and end in seconds, with the text said in it: its words give the speed).
    /// <paramref name="progress"/> gets how far the reading is, from 0 to 1.
    /// </summary>
    public static (InterviewVoiceEntry Whole, List<InterviewVoiceEntry> Spans) Analyze(string path,
        IReadOnlyList<(double Start, double End, string Text)> spans, Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var frames = ReadFrames(path, progress, cancellationToken);
        var words = spans.Sum(s => Words(s.Text));
        var whole = Measure(frames, 0, frames.Count, words);
        var each = new List<InterviewVoiceEntry>(spans.Count);
        foreach (var (start, end, text) in spans)
        {
            var from = Math.Clamp((int)(start / HopSeconds), 0, frames.Count);
            var to = Math.Clamp((int)Math.Ceiling(end / HopSeconds), from, frames.Count);
            each.Add(Measure(frames, from, to, Words(text)));
        }
        return (whole, each);
    }

    /// <summary>How many words <paramref name="text"/> holds (runs of letters or digits; "l'équipe" is two).</summary>
    public static int Words(string? text)
    {
        var count = 0;
        var inside = false;
        foreach (var c in text ?? string.Empty)
        {
            var letter = char.IsLetterOrDigit(c);
            if (letter && !inside)
                count++;
            inside = letter;
        }
        return count;
    }

    /// <summary>The frames of the whole file, at 8 kHz.</summary>
    private static List<Frame> ReadFrames(string path, Action<double>? progress, CancellationToken cancellationToken)
    {
        using var reader = new WavReader(path);
        var frames = new List<Frame>((int)(reader.TotalFrames / Math.Max(1, reader.Rate) / HopSeconds) + 16);
        var input = new float[Math.Max(4096, reader.Rate * 2)];
        // Brought to 8 kHz by the mean of the samples each new one covers (which also takes away what is above 4 kHz).
        var step = reader.Rate / (double)Rate;
        var window = new float[FrameLength];
        var filled = 0;
        double position = 0, sum = 0;
        var taken = 0;
        var floor = -1f;
        var lowest = (int)(Rate / HighestPitch);
        var highest = (int)(Rate / LowestPitch);
        var centred = new float[FrameLength];
        int read;
        while ((read = reader.Read(input)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
            {
                sum += input[i];
                taken++;
                position += 1;
                if (position < step)
                    continue;
                position -= step;
                window[filled++] = (float)(sum / taken);
                sum = 0;
                taken = 0;
                if (filled < FrameLength)
                    continue;
                frames.Add(FrameOf(window, centred, lowest, highest, ref floor));
                // The next frame starts 20 ms later: the second half is kept.
                Array.Copy(window, Hop, window, 0, FrameLength - Hop);
                filled = FrameLength - Hop;
            }
            if (reader.TotalFrames > 0)
                progress?.Invoke(reader.Position / (double)reader.TotalFrames);
        }
        return frames;
    }

    private static Frame FrameOf(float[] window, float[] centred, int lowest, int highest, ref float floor)
    {
        double mean = 0;
        foreach (var sample in window)
            mean += sample;
        mean /= window.Length;
        double energy = 0;
        for (var i = 0; i < window.Length; i++)
        {
            centred[i] = (float)(window[i] - mean);
            energy += centred[i] * centred[i];
        }
        var rms = (float)Math.Sqrt(energy / window.Length);
        // The noise of the room, followed as SilenceSegmenter does: down at once, up slowly, on the frames without speech.
        if (floor < 0)
            floor = Math.Min(rms, 0.01f);
        var speech = rms > Math.Max(MinimumRms, floor * 3f);
        if (!speech)
        {
            floor = Math.Min(0.01f, rms < floor ? (floor + rms) / 2 : floor * 0.98f + rms * 0.02f);
            return new Frame(rms, false, 0);
        }
        // The period: the lag where the frame looks most like itself, each lag weighed by the energy of its two parts.
        var best = 0;
        var bestValue = 0.0;
        double before = 0, value = 0, after = 0, previous = 0;
        for (var lag = lowest - 1; lag <= highest + 1; lag++)
        {
            double product = 0, left = 0, right = 0;
            for (var i = 0; i + lag < centred.Length; i++)
            {
                product += centred[i] * centred[i + lag];
                left += centred[i] * centred[i];
                right += centred[i + lag] * centred[i + lag];
            }
            var norm = Math.Sqrt(left * right);
            var correlation = norm > 0 ? product / norm : 0;
            // The first clear peak is kept: a later lag must be clearly better (twice the period looks as good).
            if (lag == best + 1 && best > 0)
                after = correlation;
            if (lag >= lowest && lag <= highest && correlation > bestValue * 1.08)
            {
                bestValue = correlation;
                best = lag;
                before = previous;
                value = correlation;
                after = 0;
            }
            previous = correlation;
        }
        if (best == 0 || bestValue < Voicing)
            return new Frame(rms, true, 0);
        // Between two lags: the top of the parabola through the peak and its two neighbours.
        var bend = before - 2 * value + after;
        var shift = Math.Abs(bend) > 1e-9 && before > 0 && after > 0 ? Math.Clamp(0.5 * (before - after) / bend, -0.5, 0.5) : 0;
        return new Frame(rms, true, (float)(Rate / (best + shift)));
    }

    /// <summary>The voice over the frames <paramref name="from"/> to <paramref name="to"/> (excluded), in which <paramref name="words"/> words are said.</summary>
    private static InterviewVoiceEntry Measure(List<Frame> frames, int from, int to, int words)
    {
        var voice = new InterviewVoiceEntry();
        var count = to - from;
        if (count <= 0)
            return voice;
        var pitches = new List<double>();
        var levels = new List<double>();
        var speech = 0;
        var silent = 0;
        // The silence before the first word and after the last one of the span is not a pause.
        var started = false;
        for (var i = from; i < to; i++)
        {
            var frame = frames[i];
            if (frame.Speech)
            {
                if (started && silent * HopSeconds >= PauseSeconds)
                {
                    voice.PauseCount++;
                    voice.PauseSeconds += silent * HopSeconds;
                }
                started = true;
                silent = 0;
                speech++;
                levels.Add(20 * Math.Log10(Math.Max(frame.Rms, 1e-6f)));
                if (frame.Pitch > 0)
                    pitches.Add(frame.Pitch);
            }
            else
                silent++;
        }
        voice.SpeechSeconds = Math.Round(speech * HopSeconds, 2);
        voice.PauseSeconds = Math.Round(voice.PauseSeconds, 2);
        if (levels.Count > 0)
        {
            var mean = levels.Average();
            voice.LoudnessDb = Math.Round(mean, 1);
            voice.LoudnessVariation = Math.Round(Math.Sqrt(levels.Sum(l => (l - mean) * (l - mean)) / levels.Count), 1);
        }
        if (pitches.Count >= 5)
        {
            pitches.Sort();
            // Without the frames far from the others (a period taken for its double or its half): the middle 90 %.
            var low = pitches[(int)(pitches.Count * 0.05)];
            var high = pitches[Math.Min(pitches.Count - 1, (int)(pitches.Count * 0.95))];
            var kept = pitches.Where(p => p >= low && p <= high).ToList();
            // In semitones (the ear hears the ratios of frequencies): the mean is the one of the logarithms.
            var logs = kept.Select(p => 12 * Math.Log2(p)).ToList();
            var meanLog = logs.Average();
            voice.PitchHz = Math.Round(Math.Pow(2, meanLog / 12), 1);
            voice.PitchMinHz = Math.Round(low, 1);
            voice.PitchMaxHz = Math.Round(high, 1);
            voice.PitchVariation = Math.Round(Math.Sqrt(logs.Sum(l => (l - meanLog) * (l - meanLog)) / logs.Count), 2);
        }
        var seconds = count * HopSeconds;
        if (words > 0 && seconds > 0.5)
        {
            voice.WordsPerMinute = Math.Round(words / seconds * 60, 0);
            if (voice.SpeechSeconds > 0.5)
                voice.ArticulationRate = Math.Round(words / voice.SpeechSeconds * 60, 0);
        }
        return voice;
    }
}
