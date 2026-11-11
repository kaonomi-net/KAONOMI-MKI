using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PortAudioSharp;

namespace KAONOMI.Chat.Services;

/// <summary>What a recording gave: its file, its rate, its length, and what went wrong while writing it (null: nothing).</summary>
public sealed record AudioRecording(string Path, int Rate, double Seconds, string? Error);

/// <summary>
/// The microphone listened to and, on demand, recorded (PortAudio; the microphone chosen with <c>/audiosettings</c>).
/// <see cref="Listen"/> opens it: from then on <see cref="Analyzer"/> receives what it hears (for the meters), whether
/// it is recorded or not. <see cref="StartRecording"/> also writes it into a WAV file (mono, 16 bits, at the rate of
/// the microphone), as it comes: a long recording is never held in memory, and the header of the file is brought up to
/// date every few seconds, so that a recording cut short is still read.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private readonly object _lock = new();
    private PortAudioSharp.Stream? _stream;
    private PortAudioSharp.Stream.Callback? _callback;
    private float[] _buffer = [];
    private BlockingCollection<float[]>? _queue;
    private Thread? _writer;
    private string? _path;
    private long _captured;
    private long _written;
    private volatile string? _error;

    /// <summary>What the microphone hears, analysed for the meters.</summary>
    public AudioAnalyzer Analyzer { get; } = new();

    /// <summary>The rate of the microphone, in Hz (0 before <see cref="Listen"/>).</summary>
    public int Rate { get; private set; }

    /// <summary>The name of the microphone listened to.</summary>
    public string SourceName { get; private set; } = string.Empty;

    public bool IsListening
    {
        get
        {
            lock (_lock)
                return _stream is not null;
        }
    }

    public bool IsRecording
    {
        get
        {
            lock (_lock)
                return _queue is not null;
        }
    }

    /// <summary>How long was recorded since <see cref="StartRecording"/>.</summary>
    public TimeSpan Elapsed => Rate > 0 ? TimeSpan.FromSeconds(Interlocked.Read(ref _captured) / (double)Rate) : TimeSpan.Zero;

    /// <summary>Opens the microphone (nothing when it already is); throws when there is none or it cannot be opened.</summary>
    public void Listen()
    {
        lock (_lock)
        {
            if (_stream is not null)
                return;
            AudioDevices.EnsureInitialized();
            var device = AudioDevices.InputDevice();
            if (device == PortAudio.NoDevice)
                throw new InvalidOperationException("No microphone found");
            var info = PortAudio.GetDeviceInfo(device);
            Rate = (int)Math.Round(info.defaultSampleRate);
            SourceName = info.name;
            Analyzer.Rate = Rate;
            Analyzer.Clear();
            var parameters = new StreamParameters
            {
                device = device,
                channelCount = 1,
                sampleFormat = SampleFormat.Float32,
                suggestedLatency = info.defaultLowInputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };
            // Kept in a field: the native side calls it as long as the stream runs.
            _callback = OnAudio;
            var stream = new PortAudioSharp.Stream(parameters, null, Rate, 0, StreamFlags.ClipOff, _callback, IntPtr.Zero);
            try
            {
                stream.Start();
            }
            catch
            {
                stream.Dispose();
                throw;
            }
            _stream = stream;
            ActivityLog.Info("Recorder", $"Listening to {info.name} at {Rate} Hz");
        }
    }

    private StreamCallbackResult OnAudio(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags, IntPtr userData)
    {
        if (input == IntPtr.Zero || frameCount == 0)
            return StreamCallbackResult.Continue;
        var count = (int)frameCount;
        if (_buffer.Length < count)
            _buffer = new float[count];
        Marshal.Copy(input, _buffer, 0, count);
        Analyzer.Push(_buffer.AsSpan(0, count));
        // Recorded: a copy goes to the thread of the file (the buffer is reused by the next callback).
        if (_queue is { } queue)
        {
            Interlocked.Add(ref _captured, count);
            try
            {
                queue.Add(_buffer.AsSpan(0, count).ToArray());
            }
            catch (InvalidOperationException)
            {
                // Stopped meanwhile.
            }
        }
        return StreamCallbackResult.Continue;
    }

    /// <summary>Starts recording what is heard into the new file <paramref name="path"/> (the microphone is opened when it is not).</summary>
    public void StartRecording(string path)
    {
        Listen();
        lock (_lock)
        {
            if (_queue is not null)
                throw new InvalidOperationException("Already recording");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            try
            {
                Wav.WriteHeader(file, Rate, 0);
            }
            catch
            {
                file.Dispose();
                throw;
            }
            _path = path;
            _error = null;
            Interlocked.Exchange(ref _captured, 0);
            Interlocked.Exchange(ref _written, 0);
            var queue = new BlockingCollection<float[]>();
            _writer = new Thread(() => WriteLoop(queue, file)) { IsBackground = true, Name = "Audio recorder" };
            _writer.Start();
            _queue = queue;
            ActivityLog.Info("Recorder", $"Recording {SourceName} into {path}");
        }
    }

    /// <summary>The thread of the file: each buffer written as 16-bit samples.</summary>
    private void WriteLoop(BlockingCollection<float[]> queue, FileStream file)
    {
        var bytes = Array.Empty<byte>();
        long sinceHeader = 0;
        using (file)
        {
            foreach (var chunk in queue.GetConsumingEnumerable())
            {
                if (_error is not null)
                    continue;
                try
                {
                    if (bytes.Length < chunk.Length * 2)
                        bytes = new byte[chunk.Length * 2];
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        var value = (short)Math.Round(Math.Clamp(chunk[i], -1f, 1f) * 32767f);
                        bytes[2 * i] = (byte)value;
                        bytes[2 * i + 1] = (byte)(value >> 8);
                    }
                    file.Write(bytes, 0, chunk.Length * 2);
                    Interlocked.Add(ref _written, chunk.Length);
                    sinceHeader += chunk.Length;
                    if (sinceHeader >= Rate * 5L)
                    {
                        Wav.WriteHeader(file, Rate, Interlocked.Read(ref _written));
                        file.Flush();
                        sinceHeader = 0;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The disk is full or gone: what is written stays, the rest is lost.
                    _error = ex.Message;
                    ActivityLog.Error("Recorder", "Recording: " + ex.Message);
                }
            }
            try
            {
                Wav.WriteHeader(file, Rate, Interlocked.Read(ref _written));
                file.Flush(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _error ??= ex.Message;
            }
        }
    }

    /// <summary>Stops recording and closes the file (the microphone stays listened to); null when nothing was recorded.</summary>
    public AudioRecording? StopRecording()
    {
        BlockingCollection<float[]>? queue;
        Thread? writer;
        lock (_lock)
        {
            queue = _queue;
            writer = _writer;
            _queue = null;
            _writer = null;
        }
        if (queue is null)
            return null;
        queue.CompleteAdding();
        writer?.Join();
        queue.Dispose();
        var written = Interlocked.Read(ref _written);
        ActivityLog.Info("Recorder", $"Recorded {written / (double)Rate:0.0} s into {_path}");
        return new AudioRecording(_path!, Rate, written / (double)Rate, _error);
    }

    /// <summary>Stops the recording running (its file is closed and kept) and closes the microphone.</summary>
    public void Dispose()
    {
        StopRecording();
        PortAudioSharp.Stream? stream;
        lock (_lock)
        {
            stream = _stream;
            _stream = null;
        }
        if (stream is null)
            return;
        try
        {
            stream.Stop();
        }
        catch (PortAudioException)
        {
            // Already stopped.
        }
        finally
        {
            stream.Dispose();
        }
    }
}
