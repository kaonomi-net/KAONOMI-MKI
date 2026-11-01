using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PortAudioSharp;
using Whisper.net;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Voice input of the chat (push-to-talk): <see cref="Start"/> records the default microphone (PortAudio), then
/// <see cref="StopAndTranscribeAsync"/> stops and turns the speech into text with Whisper (whisper.cpp through
/// Whisper.net, open source, run on this machine; the multilingual model understands French, German and English).
/// </summary>
public sealed class SpeechRecognitionService : IDisposable
{
    /// <summary>Whisper works on 16 kHz mono samples.</summary>
    private const int WhisperRate = 16000;

    /// <summary>Under this length the recording is taken as a click, not speech.</summary>
    private static readonly TimeSpan MinimumSpeech = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Under this peak level (full scale 1) the recording is silence. A quiet room peaks around 0.0005 with a USB
    /// microphone, a soft voice a little away from it around 0.01: 0.015 threw that voice away.
    /// </summary>
    private const float SilencePeak = 0.003f;

    /// <summary>A quiet recording is raised to this peak before Whisper (a soft voice is recognised better).</summary>
    private const float TargetPeak = 0.5f;

    private static readonly object PortAudioLock = new();
    private static bool _portAudioReady;

    private readonly string _modelPath;
    private readonly object _samplesLock = new();
    private readonly List<float> _samples = [];
    /// <summary>Guards <see cref="_stream"/> and <see cref="_stopRequested"/>: started and stopped from different threads.</summary>
    private readonly object _streamLock = new();
    private PortAudioSharp.Stream? _stream;
    private PortAudioSharp.Stream.Callback? _callback;
    /// <summary>A stop asked while the microphone was still opening: it is closed as soon as it is open.</summary>
    private bool _stopRequested;
    private int _rate;
    private WhisperFactory? _factory;
    /// <summary>One transcription at a time with the model (the warm-up, the words so far, the final one).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>Cancelled by <see cref="Dispose"/>: the transcriptions waiting stop, the one running ends first.</summary>
    private readonly CancellationTokenSource _disposing = new();
    private volatile bool _disposed;

    /// <summary>Recognises speech with the ggml Whisper model at <paramref name="modelPath"/>.</summary>
    public SpeechRecognitionService(string modelPath) => _modelPath = modelPath;

    /// <summary>True while the microphone is recorded.</summary>
    public bool IsRecording
    {
        get
        {
            lock (_streamLock)
                return _stream is not null;
        }
    }

    /// <summary>True when the Whisper model file is there.</summary>
    public bool HasModel => File.Exists(_modelPath);

    public string ModelPath => _modelPath;

    /// <summary>
    /// Starts recording the default microphone (at its own rate, mono). A stop (<see cref="Cancel"/>) asked while it
    /// opens (the caller gave up waiting) closes it as soon as it is open: it never stays on unseen.
    /// </summary>
    public void Start()
    {
        lock (_streamLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream is not null)
                return;
            _stopRequested = false;
        }
        lock (PortAudioLock)
        {
            if (!_portAudioReady)
            {
                PortAudio.Initialize();
                _portAudioReady = true;
            }
        }
        var device = PortAudio.DefaultInputDevice;
        if (device == PortAudio.NoDevice)
            throw new InvalidOperationException("No microphone found");
        var info = PortAudio.GetDeviceInfo(device);
        _rate = (int)Math.Round(info.defaultSampleRate);
        lock (_samplesLock)
            _samples.Clear();
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
        var stream = new PortAudioSharp.Stream(parameters, null, _rate, 0, StreamFlags.ClipOff, _callback, IntPtr.Zero);
        try
        {
            stream.Start();
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        lock (_streamLock)
        {
            if (!_stopRequested && !_disposed)
            {
                _stream = stream;
                ActivityLog.Info("Speech", $"Recording {info.name} at {_rate} Hz");
                return;
            }
        }
        // Stopped while it was opening: closed at once.
        try
        {
            stream.Stop();
        }
        finally
        {
            stream.Dispose();
        }
        ActivityLog.Info("Speech", "The microphone opened after it was given up: closed at once");
    }

    private StreamCallbackResult OnAudio(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags, IntPtr userData)
    {
        if (input == IntPtr.Zero || frameCount == 0)
            return StreamCallbackResult.Continue;
        var buffer = new float[frameCount];
        Marshal.Copy(input, buffer, 0, (int)frameCount);
        lock (_samplesLock)
            _samples.AddRange(buffer);
        return StreamCallbackResult.Continue;
    }

    /// <summary>Stops recording without transcribing.</summary>
    public void Cancel() => StopRecording();

    private float[] StopRecording()
    {
        PortAudioSharp.Stream? stream;
        lock (_streamLock)
        {
            // Also a stop for a microphone still opening (see Start).
            _stopRequested = true;
            stream = _stream;
            _stream = null;
        }
        if (stream is not null)
        {
            try
            {
                stream.Stop();
            }
            finally
            {
                stream.Dispose();
            }
        }
        lock (_samplesLock)
        {
            var samples = _samples.ToArray();
            _samples.Clear();
            return samples;
        }
    }

    /// <summary>
    /// Stops recording and returns the text spoken, in <paramref name="language"/> (fr, de, en); empty when nothing
    /// was said.
    /// </summary>
    public async Task<string> StopAndTranscribeAsync(string language, CancellationToken cancellationToken = default)
    {
        var recorded = StopRecording();
        var rate = _rate;
        if (rate <= 0 || recorded.Length < rate * MinimumSpeech.TotalSeconds)
        {
            ActivityLog.Info("Speech", $"Too short to be speech: {recorded.Length} samples at {rate} Hz");
            return string.Empty;
        }
        // Silence (or a muted microphone): Whisper would make up words, so nothing is recognised.
        var peak = 0f;
        foreach (var sample in recorded)
            peak = Math.Max(peak, Math.Abs(sample));
        if (peak < SilencePeak)
        {
            ActivityLog.Info("Speech", $"Only silence recorded ({recorded.Length / (double)rate:0.0} s, peak {peak:0.0000})");
            return string.Empty;
        }
        return await TranscribeAsync(Resample(Louder(recorded, peak), rate, WhisperRate), language, cancellationToken);
    }

    /// <summary>
    /// The text spoken since <see cref="Start"/>, while the microphone goes on recording (the words shown as they are
    /// said); empty while too little or only silence was recorded.
    /// </summary>
    public async Task<string> TranscribeSoFarAsync(string language, CancellationToken cancellationToken = default)
    {
        float[] recorded;
        lock (_samplesLock)
            recorded = _samples.ToArray();
        var rate = _rate;
        if (rate <= 0 || recorded.Length < rate * MinimumSpeech.TotalSeconds)
            return string.Empty;
        var peak = 0f;
        foreach (var sample in recorded)
            peak = Math.Max(peak, Math.Abs(sample));
        if (peak < SilencePeak)
            return string.Empty;
        // Not logged: one pass every few tenths of a second while the button is held.
        return await TranscribeAsync(Resample(Louder(recorded, peak), rate, WhisperRate), language, cancellationToken, log: false);
    }

    /// <summary><paramref name="samples"/> raised to <see cref="TargetPeak"/> when they peak lower (a soft voice); else as they are.</summary>
    private static float[] Louder(float[] samples, float peak)
    {
        if (peak <= 0 || peak >= TargetPeak)
            return samples;
        var gain = TargetPeak / peak;
        var louder = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            louder[i] = samples[i] * gain;
        return louder;
    }

    /// <summary>
    /// Loads the model and runs it once on a second of silence, in the background: the first pass on the GPU prepares
    /// its shaders (seconds), better done before the first words are said.
    /// </summary>
    public Task WarmUpAsync(string language) => !HasModel ? Task.CompletedTask : Task.Run(async () =>
    {
        var started = DateTime.Now;
        try
        {
            await TranscribeAsync(new float[WhisperRate], language, log: false);
            ActivityLog.Info("Speech", $"Whisper ready in {(DateTime.Now - started).TotalSeconds:0.0} s");
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Closed while it was warming up.
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Speech", "Warm-up: " + ex.Message);
        }
    });

    /// <summary>
    /// The text spoken in <paramref name="samples"/> (16 kHz mono), in <paramref name="language"/> (fr, de, en): one
    /// transcription at a time; logged (its length and time, never the words, which are the user's) when
    /// <paramref name="log"/>.
    /// </summary>
    public async Task<string> TranscribeAsync(float[] samples, string language, CancellationToken cancellationToken = default, bool log = true)
    {
        if (!HasModel)
            throw new FileNotFoundException($"Whisper model not found: {_modelPath}", _modelPath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
        await _gate.WaitAsync(stop.Token);
        var started = DateTime.Now;
        string text;
        try
        {
            text = await Task.Run(async () =>
            {
                if (_factory is null)
                {
                    _factory = WhisperFactory.FromPath(_modelPath);
                    // The GPU (Vulkan) when it is there, else the processor.
                    ActivityLog.Info("Speech", $"Whisper runs on {Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown"}");
                }
                await using var processor = _factory.CreateBuilder().WithLanguage(language).Build();
                var result = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(samples, stop.Token))
                    result.Append(segment.Text);
                return result.ToString();
            }, stop.Token);
        }
        finally
        {
            _gate.Release();
            // Disposed while this pass ran: the model is freed now that nothing uses it any more.
            if (_disposed)
                FreeModel();
        }
        text = Clean(text);
        if (log)
            ActivityLog.Info("Speech", $"{samples.Length / (double)WhisperRate:0.0} s in {language} transcribed in {(DateTime.Now - started).TotalSeconds:0.0} s ({text.Length} characters)");
        return text;
    }

    /// <summary>Frees the model once no transcription runs (the gate taken for good: none can start after).</summary>
    private void FreeModel()
    {
        if (!_gate.Wait(0))
            return;
        _factory?.Dispose();
        _factory = null;
    }

    /// <summary>
    /// The text without spaces around it nor what Whisper writes for sounds that are not speech: [BLANK_AUDIO],
    /// *Bruit de téléphone*, (music), ♪…
    /// </summary>
    private static string Clean(string text)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text,
            @"\[[^\]]*\]|\*[^*]*\*|♪[^♪]*♪?|\((?:music|musique|Musik|silence|applause|applaudissements|Applaus|bruit|noise|Geräusch|rires|laughter|Lachen)[^)]*\)", " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    /// <summary>Linear resampling of mono samples from <paramref name="from"/> Hz to <paramref name="to"/> Hz.</summary>
    public static float[] Resample(float[] samples, int from, int to)
    {
        if (from == to || samples.Length == 0)
            return samples;
        var length = (int)((long)samples.Length * to / from);
        var result = new float[length];
        var step = (double)from / to;
        for (var i = 0; i < length; i++)
        {
            var position = i * step;
            var index = (int)position;
            var fraction = (float)(position - index);
            var next = index + 1 < samples.Length ? samples[index + 1] : samples[index];
            result[i] = samples[index] + (next - samples[index]) * fraction;
        }
        return result;
    }

    /// <summary>
    /// Closes the microphone and frees the model: at once when no transcription runs, else when the running one ends
    /// (the native model must not be freed while it computes: that would end the process).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _disposing.Cancel();
        StopRecording();
        FreeModel();
    }
}
