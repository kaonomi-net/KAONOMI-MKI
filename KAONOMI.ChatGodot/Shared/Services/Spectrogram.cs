using System;
using System.Threading;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The spectrogram of a recording, computed once from its file: a column every <see cref="ColumnSeconds"/>, each on
/// <see cref="Rows"/> rows from 0 Hz (row 0) to <see cref="MaxHz"/>, with the waveform (the lowest and the highest
/// sample of each column) and the formants of the voice along it.
/// </summary>
public sealed class Spectrogram
{
    /// <summary>The rows of a column: from 0 Hz to <see cref="MaxHz"/>, evenly.</summary>
    public const int Rows = 128;
    public const double MaxHz = 8000;

    /// <summary>
    /// A column every 2 ms (the pulses of a voice, 3 to 10 ms apart, are told from one another when it is zoomed);
    /// a recording of more than four minutes has wider ones (each the loudest of what it covers), so that at most
    /// <see cref="MaxColumns"/> are kept (about 15 MB).
    /// </summary>
    private const int MaxColumns = 120_000;
    private const double FinestColumn = 0.002;
    private const int FftSize = 1024;
    private const float FloorDb = -120;

    /// <summary>The loudness of each cell: <c>Cells[column * Rows + row]</c>, 0 for -120 dB to 255 for 0 dB.</summary>
    public required byte[] Cells { get; init; }

    public required int Columns { get; init; }

    /// <summary>The time a column covers, in seconds.</summary>
    public required double ColumnSeconds { get; init; }

    /// <summary>The length of the recording, in seconds.</summary>
    public required double Seconds { get; init; }

    /// <summary>The lowest and the highest sample of each column (-1 to 1): the waveform of the recording.</summary>
    public required float[] Low { get; init; }

    public required float[] High { get; init; }

    /// <summary>The loudest cell, in dB: what the ink is set on.</summary>
    public required float LoudestDb { get; init; }

    /// <summary>The formants found every <see cref="FormantSeconds"/>: <c>Formants[index * 4 + n]</c> is F(n+1) in Hz, 0 when none.</summary>
    public required float[] Formants { get; init; }

    public required double FormantSeconds { get; init; }

    /// <summary>The loudness of a cell, in dB.</summary>
    public static float Decibels(byte cell) => FloorDb + cell * (-FloorDb / 255f);

    /// <summary>
    /// Reads <paramref name="path"/> (a WAV file) and computes its spectrogram: wide-band (each column the spectrum of
    /// 6 ms under a Hann window: the pulses of the voice show as vertical lines, its formants as dark bands),
    /// pre-emphasised as the spectrograms of speech are; and the formants of the voice every 10 ms. Meant for the
    /// thread pool: a minute of sound takes a fraction of a second.
    /// </summary>
    public static Spectrogram Compute(string path, CancellationToken cancellationToken = default)
    {
        using var reader = new WavReader(path);
        var rate = Math.Max(8000, reader.Rate);
        var seconds = reader.TotalFrames / (double)rate;
        var columnSeconds = Math.Max(FinestColumn, seconds / MaxColumns);
        var columns = Math.Max(1, (int)Math.Ceiling(seconds / columnSeconds));
        var cells = new float[columns * Rows];
        Array.Fill(cells, FloorDb);
        var lowest = new float[columns];
        var highest = new float[columns];
        var samplesPerColumn = rate * columnSeconds;

        // A spectrum every 2 ms at most (several per column when the columns are wider), of the 6 ms before it.
        var hop = Math.Max(1, (int)Math.Round(rate * Math.Min(columnSeconds, FinestColumn)));
        var window = Math.Clamp((int)(0.006 * rate), 64, 512);
        var hann = new float[window];
        for (var i = 0; i < window; i++)
            hann[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (window - 1));
        var re = new float[FftSize];
        var im = new float[FftSize];
        var binWidth = rate / (double)FftSize;
        var scale = 4f / window;

        // The formants, by the analyser of the live meters, given the sound as the microphone would.
        const double formantSeconds = 0.01;
        var formantHop = Math.Max(1, (int)Math.Round(rate * formantSeconds));
        var formants = new float[(int)(reader.TotalFrames / formantHop + 1) * 4];
        var analyzer = new AudioAnalyzer { Rate = rate };

        // The last samples read, kept so that a window can reach back over the edge of a block.
        var block = new float[Math.Max(hop, formantHop) * 64];
        var tail = new float[window + 1];
        var joined = new float[tail.Length + block.Length];
        long position = 0;          // the samples read so far
        long nextSpectrum = hop;    // the sample each next spectrum ends at
        long nextFormant = formantHop;
        var loudest = FloorDb;
        int read;
        while ((read = reader.Read(block)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Copy(tail, joined, tail.Length);
            Array.Copy(block, 0, joined, tail.Length, read);
            // The waveform: the lowest and the highest sample of each column.
            for (var k = 0; k < read; k++)
            {
                var at = Math.Min(columns - 1, (int)((position + k) / samplesPerColumn));
                var sample = block[k];
                if (sample < lowest[at])
                    lowest[at] = sample;
                if (sample > highest[at])
                    highest[at] = sample;
            }
            // joined[tail.Length + k] is the sample position + k.
            while (nextSpectrum <= position + read)
            {
                var end = tail.Length + (int)(nextSpectrum - position);
                Array.Clear(re);
                Array.Clear(im);
                for (var i = 0; i < window; i++)
                {
                    var at = end - window + i;
                    re[i] = (joined[at] - 0.95f * joined[at - 1]) * hann[i];
                }
                AudioAnalyzer.Fft(re, im);
                var column = Math.Min(columns - 1, (int)((nextSpectrum - 1) / (double)rate / columnSeconds));
                for (var row = 0; row < Rows; row++)
                {
                    var bin = row / (double)(Rows - 1) * MaxHz / binWidth;
                    var low = (int)bin;
                    if (low + 1 >= FftSize / 2)
                        continue;
                    var mix = (float)(bin - low);
                    var a = MathF.Sqrt(re[low] * re[low] + im[low] * im[low]);
                    var b = MathF.Sqrt(re[low + 1] * re[low + 1] + im[low + 1] * im[low + 1]);
                    var value = AudioFrame.Decibels((a + (b - a) * mix) * scale);
                    if (value > cells[column * Rows + row])
                        cells[column * Rows + row] = value;
                    if (value > loudest)
                        loudest = value;
                }
                nextSpectrum += hop;
            }

            // The formants: the sound pushed up to each analysis, then read.
            var pushed = 0;
            while (nextFormant <= position + read)
            {
                var upTo = (int)(nextFormant - position);
                analyzer.Push(block.AsSpan(pushed, upTo - pushed));
                pushed = upTo;
                var frame = analyzer.Analyze();
                var index = (int)(nextFormant / formantHop) - 1;
                if (frame.Voiced && index >= 0 && index * 4 + 3 < formants.Length)
                    for (var n = 0; n < frame.FormantCount; n++)
                        formants[index * 4 + n] = (float)frame.Formants[n];
                nextFormant += formantHop;
            }
            if (pushed < read)
                analyzer.Push(block.AsSpan(pushed, read - pushed));

            // The end of this block is the start of the next windows.
            if (read >= tail.Length)
                Array.Copy(block, read - tail.Length, tail, 0, tail.Length);
            else
            {
                Array.Copy(tail, read, tail, 0, tail.Length - read);
                Array.Copy(block, 0, tail, tail.Length - read, read);
            }
            position += read;
        }

        var bytes = new byte[cells.Length];
        for (var i = 0; i < cells.Length; i++)
            bytes[i] = (byte)Math.Clamp((int)MathF.Round((cells[i] - FloorDb) * (255f / -FloorDb)), 0, 255);
        return new Spectrogram
        {
            Cells = bytes,
            Columns = columns,
            ColumnSeconds = columnSeconds,
            Seconds = seconds,
            Low = lowest,
            High = highest,
            LoudestDb = loudest,
            Formants = formants,
            FormantSeconds = formantSeconds
        };
    }
}
