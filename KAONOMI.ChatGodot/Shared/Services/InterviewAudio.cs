using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using KAONOMI.Chat.Models;
using PortAudioSharp;

namespace KAONOMI.Chat.Services;

/// <summary>What was said between two silences: its samples (mono, at <paramref name="Rate"/> Hz) and where it starts and ends in the recording, in seconds.</summary>
public sealed record SpeechSegment(float[] Samples, int Rate, double Start, double End);

/// <summary>
/// Cuts a recording into what is said between two silences: the samples are given as they come (<see cref="Add"/>),
/// and a segment is returned each time a silence of <see cref="DefaultPause"/> (one second) or more follows speech.
/// The level of each 20 ms is compared with the noise of the room, followed as it changes, so that a quiet voice far
/// from the microphone still counts as speech and the hum of a room does not. The same cut serves the live
/// transcription and the one of a recorded file: a paragraph of the transcription is one segment.
/// </summary>
public sealed class SilenceSegmenter
{
    /// <summary>A silence this long ends a paragraph.</summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromSeconds(1);

    private const double FrameSeconds = 0.02;
    /// <summary>Under this level (RMS, full scale 1) a frame is never speech: a quiet room is around 0.0002.</summary>
    private const float MinimumRms = 0.0015f;
    /// <summary>The noise followed never goes over this: loud speech at the start is not taken for the room.</summary>
    private const float MaximumFloor = 0.01f;
    /// <summary>Kept before the first sound of a segment (the attack of the first word).</summary>
    private const double PreRollSeconds = 0.25;
    /// <summary>Kept of the silence that ends a segment.</summary>
    private const double TailSeconds = 0.3;
    /// <summary>Less speech than this is a click or a cough: no segment.</summary>
    private const double MinimumSpeechSeconds = 0.2;
    /// <summary>A segment is cut at this length even without a silence (memory).</summary>
    private const double MaximumSegmentSeconds = 600;

    private readonly int _rate;
    private readonly int _pauseSamples;
    private readonly int _preRollSamples;
    private readonly int _tailSamples;
    private readonly int _maximumSamples;
    private readonly float[] _frame;
    private int _filled;
    private readonly List<float> _preRoll = [];
    private List<float>? _segment;
    /// <summary>The samples of the frames done so far.</summary>
    private long _position;
    private long _segmentStart;
    private int _silentRun;
    private int _speechFrames;
    private float _floor = -1;

    public SilenceSegmenter(int rate, TimeSpan? pause = null)
    {
        _rate = Math.Max(8000, rate);
        _frame = new float[Math.Max(1, (int)(_rate * FrameSeconds))];
        _pauseSamples = (int)(_rate * (pause ?? DefaultPause).TotalSeconds);
        _preRollSamples = (int)(_rate * PreRollSeconds);
        _tailSamples = (int)(_rate * TailSeconds);
        _maximumSamples = (int)(_rate * MaximumSegmentSeconds);
    }

    /// <summary>Takes the next <paramref name="samples"/> of the recording; returns the segments they end (often none).</summary>
    public List<SpeechSegment> Add(ReadOnlySpan<float> samples)
    {
        var done = new List<SpeechSegment>();
        foreach (var sample in samples)
        {
            _frame[_filled++] = sample;
            if (_filled < _frame.Length)
                continue;
            Frame(done);
            _filled = 0;
        }
        return done;
    }

    /// <summary>The end of the recording: the segment being said, when there is one.</summary>
    public SpeechSegment? Flush()
    {
        if (_segment is null)
            return null;
        var done = new List<SpeechSegment>();
        Close(done);
        return done.Count > 0 ? done[0] : null;
    }

    private void Frame(List<SpeechSegment> done)
    {
        double sum = 0;
        foreach (var value in _frame)
            sum += value * value;
        var rms = (float)Math.Sqrt(sum / _frame.Length);
        if (_floor < 0)
            _floor = Math.Min(rms, MaximumFloor);
        var speech = rms > Math.Max(MinimumRms, _floor * 3f);
        // The noise of the room: followed down at once, up slowly, on the frames without speech only.
        if (!speech)
            _floor = Math.Min(MaximumFloor, rms < _floor ? (_floor + rms) / 2 : _floor * 0.98f + rms * 0.02f);

        if (_segment is null)
        {
            if (speech)
            {
                _segment = new List<float>(_rate * 8);
                _segmentStart = _position - _preRoll.Count;
                _segment.AddRange(_preRoll);
                _preRoll.Clear();
                _segment.AddRange(_frame);
                _silentRun = 0;
                _speechFrames = 1;
            }
            else
            {
                _preRoll.AddRange(_frame);
                if (_preRoll.Count > _preRollSamples)
                    _preRoll.RemoveRange(0, _preRoll.Count - _preRollSamples);
            }
        }
        else
        {
            _segment.AddRange(_frame);
            if (speech)
            {
                _silentRun = 0;
                _speechFrames++;
            }
            else
                _silentRun += _frame.Length;
            if (_silentRun >= _pauseSamples || _segment.Count >= _maximumSamples)
                Close(done);
        }
        _position += _frame.Length;
    }

    /// <summary>Ends the segment being said: without the silence that ended it (but its first 0.3 s), dropped when it holds too little speech.</summary>
    private void Close(List<SpeechSegment> done)
    {
        var segment = _segment!;
        _segment = null;
        var keep = segment.Count - Math.Max(0, _silentRun - _tailSamples);
        var spoken = _speechFrames * FrameSeconds;
        _silentRun = 0;
        _speechFrames = 0;
        if (spoken < MinimumSpeechSeconds || keep <= 0)
            return;
        var samples = CollectionsMarshal.AsSpan(segment)[..keep].ToArray();
        done.Add(new SpeechSegment(samples, _rate, Math.Max(0, _segmentStart) / (double)_rate, (_segmentStart + keep) / (double)_rate));
    }
}

/// <summary>What <see cref="InterviewRecorder.Stop"/> gives: the file written, its rate and length, and what went wrong while writing it (null: nothing).</summary>
public sealed record InterviewRecordingResult(string Path, int Rate, double Seconds, string? Error);

/// <summary>
/// Records the microphone (PortAudio; the one chosen with <c>/audiosettings</c>) into a WAV file (mono, 16 bits, at
/// the rate of the microphone), written as it is recorded: an interview of an hour is never held in memory, and the
/// header of the file is brought up to date every few seconds, so that a recording cut short is still read. While it
/// records, what is said is cut at each silence of a second (<see cref="SilenceSegmenter"/>) and given to
/// <see cref="SegmentReady"/>, for the live transcription.
/// </summary>
public sealed class InterviewRecorder : IDisposable
{
    /// <summary>The folder of the recordings: <c>~/kaonomi/audio/interview</c>.</summary>
    public static string Folder => Path.Combine(ChatSettings.KaonomiHome, "audio", "interview");

    private readonly object _lock = new();
    private PortAudioSharp.Stream? _stream;
    private PortAudioSharp.Stream.Callback? _callback;
    private BlockingCollection<float[]>? _queue;
    private Thread? _writer;
    /// <summary>A stop asked while the microphone was still opening: it is closed as soon as it is open.</summary>
    private bool _stopRequested;
    private string? _path;
    private int _rate;
    private long _captured;
    private long _written;
    private volatile float _level;
    private volatile string? _error;

    /// <summary>A segment ended by a silence (or by the end of the recording); raised from the thread that writes the file.</summary>
    public event Action<SpeechSegment>? SegmentReady;

    /// <summary>True while the microphone is recorded.</summary>
    public bool IsRecording
    {
        get
        {
            lock (_lock)
                return _stream is not null;
        }
    }

    /// <summary>How long was recorded since <see cref="Start"/>.</summary>
    public TimeSpan Elapsed => _rate > 0 ? TimeSpan.FromSeconds(Interlocked.Read(ref _captured) / (double)_rate) : TimeSpan.Zero;

    /// <summary>The name of the audio source of the recording running (or of the last one).</summary>
    public string SourceName { get; private set; } = string.Empty;

    /// <summary>The peak level (full scale 1) of the last buffer of the microphone.</summary>
    public float Level => _level;

    /// <summary>
    /// Starts recording the microphone into the new file <paramref name="path"/>. A stop (<see cref="Cancel"/>) asked
    /// while it opens closes it as soon as it is open: it never records unseen. <paramref name="source"/> names the
    /// audio source to record (one of <see cref="AudioDevices.Inputs"/>); null: the microphone of <c>/audiosettings</c>.
    /// </summary>
    public void Start(string path, string? source = null)
    {
        lock (_lock)
        {
            if (_stream is not null)
                throw new InvalidOperationException("Already recording");
            _stopRequested = false;
        }
        var device = source is null ? AudioDevices.InputDevice() : AudioDevices.InputDevice(source);
        if (device == PortAudio.NoDevice)
            throw new InvalidOperationException("No audio source found");
        var info = PortAudio.GetDeviceInfo(device);
        var rate = (int)Math.Round(info.defaultSampleRate);
        SourceName = info.name;
        var parameters = new StreamParameters
        {
            device = device,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultHighInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        var queue = new BlockingCollection<float[]>();
        PortAudioSharp.Stream? stream = null;
        try
        {
            Wav.WriteHeader(file, rate, 0);
            _rate = rate;
            _path = path;
            _error = null;
            _level = 0;
            Interlocked.Exchange(ref _captured, 0);
            Interlocked.Exchange(ref _written, 0);
            _queue = queue;
            // Kept in a field: the native side calls it as long as the stream runs.
            _callback = OnAudio;
            stream = new PortAudioSharp.Stream(parameters, null, rate, 0, StreamFlags.ClipOff, _callback, IntPtr.Zero);
            stream.Start();
        }
        catch
        {
            stream?.Dispose();
            _queue = null;
            file.Dispose();
            TryDelete(path);
            throw;
        }
        var segmenter = new SilenceSegmenter(rate);
        var writer = new Thread(() => WriteLoop(queue, file, segmenter)) { IsBackground = true, Name = "Interview recorder" };
        lock (_lock)
        {
            if (!_stopRequested)
            {
                _stream = stream;
                _writer = writer;
                writer.Start();
                ActivityLog.Info("Interview", $"Recording {info.name} at {rate} Hz into {Path.GetFileName(path)}");
                return;
            }
        }
        // Given up while it was opening: closed at once, nothing kept.
        try
        {
            stream.Stop();
        }
        finally
        {
            stream.Dispose();
            _queue = null;
            file.Dispose();
            TryDelete(path);
        }
        throw new OperationCanceledException("The recording was given up while the microphone was opening");
    }

    private StreamCallbackResult OnAudio(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags, IntPtr userData)
    {
        if (input == IntPtr.Zero || frameCount == 0)
            return StreamCallbackResult.Continue;
        var buffer = new float[frameCount];
        Marshal.Copy(input, buffer, 0, (int)frameCount);
        var peak = 0f;
        foreach (var sample in buffer)
            peak = Math.Max(peak, Math.Abs(sample));
        _level = peak;
        Interlocked.Add(ref _captured, frameCount);
        try
        {
            _queue?.Add(buffer);
        }
        catch (InvalidOperationException)
        {
            // Stopped meanwhile.
        }
        return StreamCallbackResult.Continue;
    }

    /// <summary>The thread of the file: each buffer written as 16-bit samples, then cut into segments.</summary>
    private void WriteLoop(BlockingCollection<float[]> queue, FileStream file, SilenceSegmenter segmenter)
    {
        var bytes = Array.Empty<byte>();
        long sinceHeader = 0;
        foreach (var chunk in queue.GetConsumingEnumerable())
        {
            if (_error is null)
            {
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
                    if (sinceHeader >= _rate * 5L)
                    {
                        Wav.WriteHeader(file, _rate, Interlocked.Read(ref _written));
                        file.Flush();
                        sinceHeader = 0;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The disk is full or gone: what is written stays, the rest is lost.
                    _error = ex.Message;
                    ActivityLog.Error("Interview", "Recording: " + ex.Message);
                }
            }
            foreach (var segment in segmenter.Add(chunk))
                Raise(segment);
        }
        if (segmenter.Flush() is { } last)
            Raise(last);
        try
        {
            Wav.WriteHeader(file, _rate, Interlocked.Read(ref _written));
            file.Flush(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error ??= ex.Message;
        }
        finally
        {
            file.Dispose();
        }
    }

    private void Raise(SpeechSegment segment)
    {
        try
        {
            SegmentReady?.Invoke(segment);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Interview", "Segment: " + ex.Message);
        }
    }

    /// <summary>
    /// Stops recording and closes the file (its last segment is given to <see cref="SegmentReady"/> first); null when
    /// nothing was being recorded.
    /// </summary>
    public InterviewRecordingResult? Stop()
    {
        PortAudioSharp.Stream? stream;
        Thread? writer;
        lock (_lock)
        {
            // Also a stop for a microphone still opening (see Start).
            _stopRequested = true;
            stream = _stream;
            writer = _writer;
            _stream = null;
            _writer = null;
        }
        if (stream is null)
            return null;
        try
        {
            stream.Stop();
        }
        finally
        {
            stream.Dispose();
        }
        _queue?.CompleteAdding();
        writer?.Join();
        _queue?.Dispose();
        _queue = null;
        _level = 0;
        var written = Interlocked.Read(ref _written);
        ActivityLog.Info("Interview", $"Recorded {written / (double)_rate:0.0} s into {Path.GetFileName(_path)}");
        return new InterviewRecordingResult(_path!, _rate, written / (double)_rate, _error);
    }

    /// <summary>Gives up a recording still opening (or stops the one running).</summary>
    public void Cancel() => Stop();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty file is left.
        }
    }

    /// <summary>Stops the recording running: its file is closed and kept.</summary>
    public void Dispose() => Stop();
}

/// <summary>The WAV files of the interviews: their header, and what a segment needs before Whisper.</summary>
public static class Wav
{
    /// <summary>Whisper works on 16 kHz mono samples.</summary>
    public const int WhisperRate = 16000;

    /// <summary>
    /// Writes (or writes again) the 44 bytes of the header of a mono 16-bit PCM file of <paramref name="samples"/>
    /// samples at the start of <paramref name="file"/>, which is left where it was (at its end for a new file).
    /// </summary>
    public static void WriteHeader(System.IO.Stream file, int rate, long samples)
    {
        var data = (uint)Math.Min(uint.MaxValue - 36, samples * 2);
        Span<byte> header = stackalloc byte[44];
        Encoding.ASCII.GetBytes("RIFF", header);
        BitConverter.TryWriteBytes(header[4..], 36 + data);
        Encoding.ASCII.GetBytes("WAVE", header[8..]);
        Encoding.ASCII.GetBytes("fmt ", header[12..]);
        BitConverter.TryWriteBytes(header[16..], 16u);
        BitConverter.TryWriteBytes(header[20..], (ushort)1);
        BitConverter.TryWriteBytes(header[22..], (ushort)1);
        BitConverter.TryWriteBytes(header[24..], (uint)rate);
        BitConverter.TryWriteBytes(header[28..], (uint)rate * 2);
        BitConverter.TryWriteBytes(header[32..], (ushort)2);
        BitConverter.TryWriteBytes(header[34..], (ushort)16);
        Encoding.ASCII.GetBytes("data", header[36..]);
        BitConverter.TryWriteBytes(header[40..], data);
        var position = file.Position;
        file.Position = 0;
        file.Write(header);
        file.Position = Math.Max(position, 44);
    }

    /// <summary>
    /// The samples of <paramref name="segment"/> made ready for Whisper: raised when quiet (a soft voice is
    /// recognised better), brought to 16 kHz, and followed by silence up to 1.2 s (Whisper refuses less than a second).
    /// </summary>
    public static float[] ForWhisper(SpeechSegment segment)
    {
        var samples = segment.Samples;
        var peak = 0f;
        foreach (var sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));
        if (peak > 0 && peak < 0.5f)
        {
            var gain = 0.5f / peak;
            var louder = new float[samples.Length];
            for (var i = 0; i < samples.Length; i++)
                louder[i] = samples[i] * gain;
            samples = louder;
        }
        samples = SpeechRecognitionService.Resample(samples, segment.Rate, WhisperRate);
        var minimum = (int)(WhisperRate * 1.2);
        if (samples.Length < minimum)
            Array.Resize(ref samples, minimum);
        return samples;
    }
}

/// <summary>
/// Reads a WAV file as mono samples, a piece at a time (a recording of an hour is never whole in memory): PCM of 8,
/// 16, 24 or 32 bits or 32-bit floats, any number of channels (mixed into one).
/// </summary>
public sealed class WavReader : IDisposable
{
    private readonly FileStream _file;
    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly bool _float;
    private long _remainingFrames;
    private byte[] _bytes = [];

    public WavReader(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            Span<byte> head = stackalloc byte[12];
            if (_file.Read(head) < 12 || Encoding.ASCII.GetString(head[..4]) != "RIFF" || Encoding.ASCII.GetString(head[8..12]) != "WAVE")
                throw new InvalidDataException("Not a WAV file");
            var format = 0;
            var bits = 0;
            Span<byte> chunk = stackalloc byte[8];
            while (true)
            {
                if (_file.Read(chunk) < 8)
                    throw new InvalidDataException("The WAV file has no samples");
                var id = Encoding.ASCII.GetString(chunk[..4]);
                long size = BitConverter.ToUInt32(chunk[4..]);
                if (id == "fmt ")
                {
                    var fmt = new byte[Math.Max(16, (int)Math.Min(size, 64))];
                    if (_file.Read(fmt, 0, (int)Math.Min(size, fmt.Length)) < 16)
                        throw new InvalidDataException("The WAV file has a short format");
                    format = BitConverter.ToUInt16(fmt, 0);
                    _channels = BitConverter.ToUInt16(fmt, 2);
                    Rate = BitConverter.ToInt32(fmt, 4);
                    bits = BitConverter.ToUInt16(fmt, 14);
                    // WAVE_FORMAT_EXTENSIBLE: the real format is the start of its sub-format.
                    if (format == 0xFFFE && size >= 26)
                        format = BitConverter.ToUInt16(fmt, 24);
                    _file.Position += Math.Max(0, size - fmt.Length) + (size & 1);
                }
                else if (id == "data")
                {
                    if (_channels <= 0 || Rate <= 0 || bits is not (8 or 16 or 24 or 32) || format is not (1 or 3))
                        throw new InvalidDataException($"WAV format not read (format {format}, {bits} bits, {_channels} channel(s))");
                    _float = format == 3;
                    _bytesPerSample = bits / 8;
                    // A recording cut short says 0 (or too much): what is in the file is read.
                    var left = _file.Length - _file.Position;
                    var bytes = size == 0 || size > left ? left : size;
                    TotalFrames = _remainingFrames = bytes / (_bytesPerSample * _channels);
                    _dataStart = _file.Position;
                    break;
                }
                else
                    _file.Position += size + (size & 1);
            }
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    /// <summary>The samples per second.</summary>
    public int Rate { get; }

    /// <summary>How many samples (of one channel) the file holds.</summary>
    public long TotalFrames { get; }

    /// <summary>How many samples were read so far.</summary>
    public long Position => TotalFrames - _remainingFrames;

    private readonly long _dataStart;

    /// <summary>Goes to the sample <paramref name="frame"/> (kept between the start and the end): the next <see cref="Read"/> starts there.</summary>
    public void Seek(long frame)
    {
        frame = Math.Clamp(frame, 0, TotalFrames);
        _file.Position = _dataStart + frame * _bytesPerSample * _channels;
        _remainingFrames = TotalFrames - frame;
    }

    /// <summary>Reads the next samples into <paramref name="buffer"/> (the channels mixed into one); returns how many, 0 at the end.</summary>
    public int Read(float[] buffer)
    {
        var frames = (int)Math.Min(buffer.Length, _remainingFrames);
        if (frames <= 0)
            return 0;
        var frameBytes = _bytesPerSample * _channels;
        if (_bytes.Length < frames * frameBytes)
            _bytes = new byte[frames * frameBytes];
        var read = 0;
        while (read < frames * frameBytes)
        {
            var n = _file.Read(_bytes, read, frames * frameBytes - read);
            if (n <= 0)
                break;
            read += n;
        }
        frames = read / frameBytes;
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < _channels; c++)
            {
                var at = (f * _channels + c) * _bytesPerSample;
                sum += _bytesPerSample switch
                {
                    1 => (_bytes[at] - 128) / 128f,
                    2 => BitConverter.ToInt16(_bytes, at) / 32768f,
                    3 => ((_bytes[at] | _bytes[at + 1] << 8 | (sbyte)_bytes[at + 2] << 16)) / 8388608f,
                    _ => _float ? BitConverter.ToSingle(_bytes, at) : BitConverter.ToInt32(_bytes, at) / 2147483648f
                };
            }
            buffer[f] = sum / _channels;
        }
        _remainingFrames = frames == 0 ? 0 : _remainingFrames - frames;
        return frames;
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>
/// Plays a WAV file inside the chat (the player of /interview), on the output chosen with <c>/audiosettings</c>
/// (PortAudio): play, pause, and a position that can be moved. The file is read as it is played (a recording of an
/// hour is never whole in memory) and brought to the rate of the output on the way.
/// </summary>
public sealed class WavPlayer : IDisposable
{
    private readonly object _lock = new();
    private readonly WavReader _reader;
    private readonly PortAudioSharp.Stream _stream;
    private readonly PortAudioSharp.Stream.Callback _callback;
    private readonly int _channels;
    /// <summary>Samples of the file per sample of the output.</summary>
    private readonly double _step;
    private readonly float[] _chunk;
    private int _chunkCount;
    private int _chunkAt;
    /// <summary>The two samples of the file the output is between, and how far between them (0 to 1).</summary>
    private float _previous;
    private float _next;
    private double _fraction;
    /// <summary>The sample of the file being played.</summary>
    private long _frame;
    private bool _running;
    private volatile bool _ended;
    private bool _disposed;

    public WavPlayer(string path)
    {
        _reader = new WavReader(path);
        try
        {
            var device = AudioDevices.OutputDevice();
            if (device == PortAudio.NoDevice)
                throw new InvalidOperationException("No audio output found");
            var info = PortAudio.GetDeviceInfo(device);
            var rate = (int)Math.Round(info.defaultSampleRate);
            _channels = Math.Clamp(info.maxOutputChannels, 1, 2);
            _step = _reader.Rate / (double)rate;
            _chunk = new float[Math.Max(1024, _reader.Rate / 4)];
            var parameters = new StreamParameters
            {
                device = device,
                channelCount = _channels,
                sampleFormat = SampleFormat.Float32,
                suggestedLatency = info.defaultHighOutputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero
            };
            // Kept in a field: the native side calls it as long as the stream runs.
            _callback = OnAudio;
            _stream = new PortAudioSharp.Stream(null, parameters, rate, 0, StreamFlags.ClipOff, _callback, IntPtr.Zero);
            Prime();
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    /// <summary>The length of the file, in seconds.</summary>
    public double Duration => _reader.Rate > 0 ? _reader.TotalFrames / (double)_reader.Rate : 0;

    /// <summary>Where the playing is in the file, in seconds.</summary>
    public double Position
    {
        get
        {
            lock (_lock)
                return _reader.Rate > 0 ? Math.Min(_frame, _reader.TotalFrames) / (double)_reader.Rate : 0;
        }
    }

    /// <summary>True while it plays (false when paused or at the end).</summary>
    public bool IsPlaying
    {
        get
        {
            lock (_lock)
                return _running && !_ended;
        }
    }

    /// <summary>True once the end of the file was played.</summary>
    public bool Ended => _ended;

    /// <summary>The next sample of the file (0 after its end).</summary>
    private float Pull()
    {
        if (_chunkAt >= _chunkCount)
        {
            _chunkCount = _reader.Read(_chunk);
            _chunkAt = 0;
            if (_chunkCount == 0)
                return 0;
        }
        return _chunk[_chunkAt++];
    }

    /// <summary>The first two samples from where the file stands.</summary>
    private void Prime()
    {
        _chunkCount = _chunkAt = 0;
        _previous = Pull();
        _next = Pull();
        _fraction = 0;
    }

    private StreamCallbackResult OnAudio(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags, IntPtr userData)
    {
        var buffer = new float[frameCount * _channels];
        var done = false;
        lock (_lock)
        {
            if (!_disposed)
            {
                for (var i = 0; i < frameCount; i++)
                {
                    if (_frame >= _reader.TotalFrames)
                    {
                        done = true;
                        break;
                    }
                    var value = _previous + (_next - _previous) * (float)_fraction;
                    for (var c = 0; c < _channels; c++)
                        buffer[i * _channels + c] = value;
                    _fraction += _step;
                    while (_fraction >= 1)
                    {
                        _previous = _next;
                        _next = Pull();
                        _fraction -= 1;
                        _frame++;
                    }
                }
            }
            else
                done = true;
        }
        Marshal.Copy(buffer, 0, output, buffer.Length);
        if (!done)
            return StreamCallbackResult.Continue;
        _ended = true;
        return StreamCallbackResult.Complete;
    }

    /// <summary>Plays from where it stands (from the start again after the end).</summary>
    public void Play()
    {
        bool restart;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running && !_ended)
                return;
            restart = _running;
        }
        // After the end the stream is done, not stopped: it is stopped before it starts again.
        if (restart)
            _stream.Stop();
        lock (_lock)
        {
            if (_ended || _frame >= _reader.TotalFrames)
                SeekLocked(0);
            _ended = false;
            _running = true;
        }
        _stream.Start();
    }

    /// <summary>Stops playing, the position kept.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (_disposed || !_running)
                return;
            _running = false;
        }
        _stream.Stop();
    }

    /// <summary>Goes to <paramref name="seconds"/> in the file (while it plays or not).</summary>
    public void Seek(double seconds)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            SeekLocked((long)(Math.Max(0, seconds) * _reader.Rate));
            if (!_running)
                _ended = false;
        }
    }

    private void SeekLocked(long frame)
    {
        _frame = Math.Clamp(frame, 0, _reader.TotalFrames);
        _reader.Seek(_frame);
        Prime();
    }

    public void Dispose()
    {
        bool running;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            running = _running;
            _running = false;
        }
        try
        {
            if (running)
                _stream.Stop();
        }
        finally
        {
            _stream.Dispose();
            _reader.Dispose();
        }
    }
}
