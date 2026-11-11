using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Reads the answers aloud with Piper (local, open source text to speech; <c>text2speech</c> and <c>tts</c> in config.yml). The text is cut
/// into sentences: Piper writes one WAV file per sentence, and each one is played as soon as it is ready while the next
/// ones are generated. A new answer (or <see cref="Stop"/>) interrupts the one being read.
/// </summary>
public sealed partial class SpeechService : IDisposable
{
    /// <summary>Longest sentence sent to Piper at once (longer ones are cut at a comma or a space).</summary>
    private const int MaxSentenceLength = 300;

    private readonly string? _piperPath;
    private readonly string? _voicePath;
    private readonly double _lengthScale;
    private readonly double _pitch;
    private readonly double _robot;
    private readonly double _vibrato;
    private readonly double _assistant;
    private readonly double _horror;
    private readonly int? _speakerId;
    private readonly object _gate = new();
    private CancellationTokenSource? _current;

    /// <summary>Length of one step of the loudness envelope, in milliseconds.</summary>
    private const int EnvelopeStepMs = 20;

    /// <summary>Time between the start of the player and the first sound it outputs (about the same everywhere).</summary>
    private const int PlayerLatencyMs = 60;

    /// <summary>Loudness envelope of the sentence being played, and when it started (Stopwatch ticks).</summary>
    private sealed record PlayingSound(float[] Levels, long StartTimestamp);

    private volatile PlayingSound? _playing;

    /// <summary>
    /// Speaks with the Piper executable <paramref name="piperPath"/> and the voice model <paramref name="voicePath"/>
    /// (.onnx); <paramref name="speaker"/> (name or number) picks one of the speakers of a multi-speaker voice;
    /// <paramref name="speed"/> 1 = normal, 1.2 = 20 % faster; <paramref name="pitch"/> 1 = normal, 1.15 = 15 % higher;
    /// <paramref name="robot"/> 0 = natural voice, 1 = plainly robotic (see <see cref="Robotize"/>);
    /// <paramref name="vibrato"/> 0 = steady voice, 1 = a singing vibrato (see <see cref="Vibrate"/>);
    /// <paramref name="assistant"/> 0 = dry voice, 1 = the sheen of a film's AI (see <see cref="AssistantSheen"/>);
    /// <paramref name="horror"/> 0 = none, 1 = the full horror-film treatment (see <see cref="Horrify"/>).
    /// </summary>
    public SpeechService(string? piperPath, string? voicePath, string? speaker, double speed, double pitch = 1, double robot = 0,
        double vibrato = 0, double assistant = 0, double horror = 0)
    {
        _piperPath = piperPath;
        _voicePath = voicePath;
        _pitch = Math.Clamp(pitch, 0.5, 2);
        _robot = Math.Clamp(robot, 0, 1);
        _vibrato = Math.Clamp(vibrato, 0, 1);
        _assistant = Math.Clamp(assistant, 0, 1);
        _horror = Math.Clamp(horror, 0, 1);
        // Piper has no pitch setting: the voice is generated slower by the pitch factor, then played faster by the same
        // factor (sample rate of the WAV raised, see RaisePitch), which raises the pitch and keeps the speed.
        _lengthScale = _pitch / Math.Clamp(speed, 0.25, 4);
        _speakerId = SpeakerId(voicePath, speaker);
    }

    /// <summary>
    /// Number of <paramref name="speaker"/>: the number itself, or the one of its name in the <c>speaker_id_map</c> of
    /// the voice (.onnx.json); null (first speaker) when there is none or it is unknown.
    /// </summary>
    private static int? SpeakerId(string? voicePath, string? speaker)
    {
        if (string.IsNullOrWhiteSpace(speaker))
            return null;
        if (int.TryParse(speaker, out var number))
            return number;

        try
        {
            var config = voicePath + ".json";
            if (File.Exists(config)
                && System.Text.Json.JsonDocument.Parse(File.ReadAllText(config)).RootElement
                    .TryGetProperty("speaker_id_map", out var map)
                && map.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var entry in map.EnumerateObject())
                    if (string.Equals(entry.Name, speaker, StringComparison.OrdinalIgnoreCase) && entry.Value.TryGetInt32(out var id))
                        return id;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            // Unreadable voice config: first speaker.
        }

        ActivityLog.Error("Speech", $"Unknown speaker \"{speaker}\" for {Path.GetFileName(voicePath)}: first speaker used", new ArgumentException(speaker));
        return null;
    }

    /// <summary>Raised (on a background thread) when the reading of an answer starts (true) or ends (false).</summary>
    public event Action<bool>? SpeakingChanged;

    /// <summary>
    /// Loudness of the voice right now, from 0 (silence) to 1 (loudest part of the sentence), read from the envelope of
    /// the WAV file being played; 0 when nothing is read aloud. Safe to call from any thread (the render loop).
    /// </summary>
    public float Level
    {
        get
        {
            if (_playing is not { } sound)
                return 0;
            var elapsedMs = Stopwatch.GetElapsedTime(sound.StartTimestamp).TotalMilliseconds - PlayerLatencyMs;
            var step = (int)(elapsedMs / EnvelopeStepMs);
            return step >= 0 && step < sound.Levels.Length ? sound.Levels[step] : 0;
        }
    }

    /// <summary>True when Piper and the voice are found.</summary>
    public bool IsAvailable => File.Exists(_piperPath) && File.Exists(_voicePath);

    /// <summary>Reads <paramref name="markdown"/> aloud (code blocks are skipped), interrupting the current reading.</summary>
    public void Speak(string markdown)
    {
        if (!IsAvailable)
            return;

        var sentences = Sentences(markdown);
        if (sentences.Count == 0)
            return;

        var cancellation = new CancellationTokenSource();
        lock (_gate)
        {
            _current?.Cancel();
            _current = cancellation;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await SpeakAsync(sentences, cancellation.Token);
            }
            finally
            {
                lock (_gate)
                    if (ReferenceEquals(_current, cancellation))
                        _current = null;
            }
        });
    }

    /// <summary>True from <see cref="Speak"/> until the answer has been read to the end or <see cref="Stop"/> is called.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _current is not null;
        }
    }

    /// <summary>Stops the current reading at once.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current = null;
        }
        _playing = null;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private async Task SpeakAsync(IReadOnlyList<string> sentences, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "kaonomi-tts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var files = Channel.CreateUnbounded<string>();
        Process? piper = null;
        try
        {
            var start = new ProcessStartInfo(_piperPath!)
            {
                ArgumentList = { "-m", _voicePath!, "-d", directory, "--length-scale", _lengthScale.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (_speakerId is { } speakerId)
            {
                start.ArgumentList.Add("-s");
                start.ArgumentList.Add(speakerId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            piper = Process.Start(start);
            if (piper is null)
                return;
            using var killPiper = cancellationToken.Register(() => Kill(piper));

            // Piper logs "Wrote <file>.wav" on stderr for each line, in order; the end of stderr ends the list.
            piper.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    files.Writer.TryComplete();
                else if (WroteLine().Match(e.Data) is { Success: true } match)
                    files.Writer.TryWrite(match.Groups[1].Value.Trim());
            };
            piper.BeginErrorReadLine();
            piper.BeginOutputReadLine();

            foreach (var sentence in sentences)
                await piper.StandardInput.WriteLineAsync(sentence.AsMemory(), cancellationToken);
            piper.StandardInput.Close();

            var speaking = false;
            try
            {
                await foreach (var file in files.Reader.ReadAllAsync(cancellationToken))
                {
                    if (!speaking)
                        SpeakingChanged?.Invoke(speaking = true);
                    await PlayAsync(file, cancellationToken);
                }
            }
            finally
            {
                if (speaking)
                    SpeakingChanged?.Invoke(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Interrupted by a new answer or Stop().
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ActivityLog.Error("Speech", "Text to speech failed", ex);
        }
        finally
        {
            if (piper is not null)
            {
                Kill(piper);
                piper.Dispose();
            }
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A file still being played: left in the temp folder.
            }
        }
    }

    /// <summary>
    /// Plays a WAV file with the player of the system and waits for its end (killed on cancellation); its loudness
    /// envelope is published through <see cref="Level"/> meanwhile.
    /// </summary>
    private async Task PlayAsync(string file, CancellationToken cancellationToken)
    {
        Robotize(file, _robot);
        Vibrate(file, _vibrato, _pitch);
        AssistantSheen(file, _assistant, _pitch);
        Horrify(file, _horror, _pitch);
        RaisePitch(file, _pitch);
        var levels = LoudnessEnvelope(file);
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell", ["-NoProfile", "-Command", $"(New-Object Media.SoundPlayer '{file.Replace("'", "''")}').PlaySync()"])
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("afplay", [file])
                : new ProcessStartInfo("aplay", ["-q", file]);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        using var player = Process.Start(start);
        if (player is null)
            return;
        var playing = new PlayingSound(levels, Stopwatch.GetTimestamp());
        _playing = playing;
        try
        {
            using var killPlayer = cancellationToken.Register(() => Kill(player));
            await player.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (ReferenceEquals(_playing, playing))
                _playing = null;
        }
    }

    /// <summary>Frequency of the carrier of the ring modulation, in Hz: a low buzz under the voice, like a robot.</summary>
    private const double RobotCarrierHz = 55;

    /// <summary>Delay of the comb filter, in milliseconds: a metallic resonance at about 140 Hz and its harmonics.</summary>
    private const double RobotCombMs = 7;

    /// <summary>
    /// Makes the voice of a 16-bit PCM WAV file robotic, in place: a ring modulation (the voice multiplied by a
    /// <see cref="RobotCarrierHz"/> sine, mixed with the voice itself) for the buzzing timbre, then a comb filter (the
    /// sound added to itself <see cref="RobotCombMs"/> later, with feedback) for the metallic resonance. Both grow with
    /// <paramref name="amount"/> (0 = unchanged, 1 = plainly robotic, still easy to understand); the result keeps the
    /// loudest peak of the original. Unchanged when the file cannot be read or is in another format.
    /// </summary>
    private static void Robotize(string file, double amount)
    {
        if (amount < 0.001)
            return;
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (!TryFindPcm16(bytes, out var channels, out var sampleRate, out var dataStart, out var dataLength))
                return;

            var frames = dataLength / (2 * channels);
            var ring = 0.75 * amount;                     // share of the ring-modulated voice in the mix
            var feedback = 0.6 * amount;                  // strength of the metallic resonance
            var delay = Math.Max(1, (int)(sampleRate * RobotCombMs / 1000));
            for (var channel = 0; channel < channels; channel++)
            {
                var input = new double[frames];
                double inputPeak = 0;
                for (var frame = 0; frame < frames; frame++)
                {
                    input[frame] = BitConverter.ToInt16(bytes, dataStart + (frame * channels + channel) * 2);
                    inputPeak = Math.Max(inputPeak, Math.Abs(input[frame]));
                }
                if (inputPeak < 1)
                    continue;

                var output = new double[frames];
                double outputPeak = 0;
                for (var frame = 0; frame < frames; frame++)
                {
                    var carrier = Math.Sin(2 * Math.PI * RobotCarrierHz * frame / sampleRate);
                    var modulated = (1 - ring) * input[frame] + ring * input[frame] * carrier;
                    output[frame] = modulated + (frame >= delay ? feedback * output[frame - delay] : 0);
                    outputPeak = Math.Max(outputPeak, Math.Abs(output[frame]));
                }

                var gain = outputPeak < 1 ? 1 : inputPeak / outputPeak;
                for (var frame = 0; frame < frames; frame++)
                {
                    var sample = (short)Math.Clamp(Math.Round(output[frame] * gain), short.MinValue, short.MaxValue);
                    BitConverter.GetBytes(sample).CopyTo(bytes, dataStart + (frame * channels + channel) * 2);
                }
            }
            File.WriteAllBytes(file, bytes);
        }
        catch (IOException)
        {
            // Unchanged: the voice keeps its own timbre.
        }
    }

    /// <summary>Rate of the vibrato heard, in Hz (a singer's vibrato is 5 to 7 Hz).</summary>
    private const double VibratoHz = 5.5;

    /// <summary>Largest swing of the pitch of the vibrato (amount 1), as a share of the frequency: about ±0.8 semitone.</summary>
    private const double VibratoSwing = 0.045;

    /// <summary>
    /// Adds a vibrato to the voice of a 16-bit PCM WAV file, in place: the sound is read through a delay that swings
    /// back and forth (<see cref="VibratoHz"/>), which makes its pitch waver up and down by up to
    /// <see cref="VibratoSwing"/>, with a slight tremolo of the loudness at the same rate. <paramref name="amount"/>
    /// 0 = unchanged, 1 = a full singing vibrato; <paramref name="pitch"/> is the factor the WAV will be played faster
    /// by (<see cref="RaisePitch"/>), so that the vibrato heard keeps its rate. Unchanged when the file cannot be read.
    /// </summary>
    private static void Vibrate(string file, double amount, double pitch)
    {
        if (amount < 0.001)
            return;
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (!TryFindPcm16(bytes, out var channels, out var sampleRate, out var dataStart, out var dataLength))
                return;

            var frames = dataLength / (2 * channels);
            var rate = VibratoHz / pitch;
            // A delay swinging by ±depth at the rate f changes the pitch by up to 2π·f·depth.
            var depth = VibratoSwing * amount / (2 * Math.PI * rate) * sampleRate;
            var tremolo = 0.12 * amount;
            for (var channel = 0; channel < channels; channel++)
            {
                var input = new double[frames];
                for (var frame = 0; frame < frames; frame++)
                    input[frame] = BitConverter.ToInt16(bytes, dataStart + (frame * channels + channel) * 2);

                for (var frame = 0; frame < frames; frame++)
                {
                    var phase = 2 * Math.PI * rate * frame / sampleRate;
                    var position = frame - depth * (1 + Math.Sin(phase));
                    var sample = 0.0;
                    if (position >= 0)
                    {
                        var index = (int)position;
                        var fraction = position - index;
                        var next = Math.Min(index + 1, frames - 1);
                        sample = input[index] * (1 - fraction) + input[next] * fraction;
                    }
                    sample *= 1 - tremolo * (0.5 + 0.5 * Math.Sin(phase));
                    var value = (short)Math.Clamp(Math.Round(sample), short.MinValue, short.MaxValue);
                    BitConverter.GetBytes(value).CopyTo(bytes, dataStart + (frame * channels + channel) * 2);
                }
            }
            File.WriteAllBytes(file, bytes);
        }
        catch (IOException)
        {
            // Unchanged: the voice stays steady.
        }
    }

    /// <summary>Delays (ms) and gains of the early reflections of the small room of <see cref="AssistantSheen"/>.</summary>
    private static readonly (double Ms, double Gain)[] RoomReflections = [(17, 0.22), (29, 0.16), (43, 0.12), (61, 0.08)];

    /// <summary>
    /// Gives the voice of a 16-bit PCM WAV file, in place, the sheen of the AI assistant of a film, heard through the
    /// speakers of a high-tech room: a soft doubling (the voice mixed with itself 14 ms later, the delay swinging slowly,
    /// a chorus that makes it smooth and synthetic), a little presence (the highs brought forward), the early reflections
    /// of a small room and a short reverb tail. <paramref name="amount"/> 0 = unchanged, 1 = full; <paramref name="pitch"/>
    /// is the factor the WAV will be played faster by (<see cref="RaisePitch"/>), so that the delays heard keep their
    /// length. The result keeps the loudest peak of the original; unchanged when the file cannot be read.
    /// </summary>
    private static void AssistantSheen(string file, double amount, double pitch)
    {
        if (amount < 0.001)
            return;
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (!TryFindPcm16(bytes, out var channels, out var sampleRate, out var dataStart, out var dataLength))
                return;

            var frames = dataLength / (2 * channels);
            double Samples(double ms) => ms / 1000 * sampleRate * pitch;
            var chorusDelay = Samples(14);
            var chorusSwing = Samples(2.5);
            var chorusRate = 0.7 / pitch;
            var tailDelay = (int)Math.Max(1, Samples(79));
            var tailFeedback = 0.32 * amount;
            for (var channel = 0; channel < channels; channel++)
            {
                var input = new double[frames];
                double inputPeak = 0;
                for (var frame = 0; frame < frames; frame++)
                {
                    input[frame] = BitConverter.ToInt16(bytes, dataStart + (frame * channels + channel) * 2);
                    inputPeak = Math.Max(inputPeak, Math.Abs(input[frame]));
                }
                if (inputPeak < 1)
                    continue;

                double At(double position)
                {
                    if (position < 0)
                        return 0;
                    var index = (int)position;
                    var fraction = position - index;
                    return input[index] * (1 - fraction) + input[Math.Min(index + 1, frames - 1)] * fraction;
                }

                var output = new double[frames];
                double previous = 0, outputPeak = 0;
                for (var frame = 0; frame < frames; frame++)
                {
                    // Presence: the difference with the previous sample (the highs) added a little.
                    var dry = input[frame] + 0.25 * amount * (input[frame] - previous);
                    previous = input[frame];

                    var swing = chorusDelay + chorusSwing * Math.Sin(2 * Math.PI * chorusRate * frame / sampleRate);
                    var wet = 0.4 * amount * At(frame - swing);
                    foreach (var (ms, gain) in RoomReflections)
                        wet += gain * amount * At(frame - Samples(ms));

                    output[frame] = dry + wet + (frame >= tailDelay ? tailFeedback * 0.5 * (output[frame - tailDelay] + output[Math.Max(0, frame - tailDelay - 1)]) : 0);
                    outputPeak = Math.Max(outputPeak, Math.Abs(output[frame]));
                }

                var gainBack = outputPeak < 1 ? 1 : inputPeak / outputPeak;
                for (var frame = 0; frame < frames; frame++)
                {
                    var sample = (short)Math.Clamp(Math.Round(output[frame] * gainBack), short.MinValue, short.MaxValue);
                    BitConverter.GetBytes(sample).CopyTo(bytes, dataStart + (frame * channels + channel) * 2);
                }
            }
            File.WriteAllBytes(file, bytes);
        }
        catch (IOException)
        {
            // Unchanged: the voice stays dry.
        }
    }

    /// <summary>
    /// Horror-film treatment of the voice of a 16-bit PCM WAV file, in place: a demonic shadow (the voice an octave lower,
    /// through a pitch shifter of two crossfaded delay taps) mixed under it, a breath (noise following the loudness of
    /// the voice), the stutters of a broken doll (a syllable repeated now and then) and a dark hall (two long feedback
    /// delays, dull). <paramref name="amount"/> 0 = unchanged, 1 = full; <paramref name="pitch"/> is the factor the WAV
    /// will be played faster by (<see cref="RaisePitch"/>), so that the times heard keep their length. The result keeps
    /// the loudest peak of the original; unchanged when the file cannot be read. The same sentence always sounds the same.
    /// </summary>
    private static void Horrify(string file, double amount, double pitch)
    {
        if (amount < 0.001)
            return;
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (!TryFindPcm16(bytes, out var channels, out var sampleRate, out var dataStart, out var dataLength))
                return;

            var frames = dataLength / (2 * channels);
            double Samples(double ms) => ms / 1000 * sampleRate * pitch;
            var window = Samples(60);                 // length of the grains of the pitch shifter
            const double shadowRatio = 0.5;           // an octave lower
            var stutter = (int)Samples(55);           // length of the syllable repeated by a stutter
            var stutterEvery = (int)Samples(2600);    // about one stutter every 2.6 seconds of speech
            int hallA = (int)Samples(97), hallB = (int)Samples(131);
            var hallFeedback = 0.45 * amount;
            for (var channel = 0; channel < channels; channel++)
            {
                var input = new double[frames];
                double inputPeak = 0;
                for (var frame = 0; frame < frames; frame++)
                {
                    input[frame] = BitConverter.ToInt16(bytes, dataStart + (frame * channels + channel) * 2);
                    inputPeak = Math.Max(inputPeak, Math.Abs(input[frame]));
                }
                if (inputPeak < 1)
                    continue;

                double At(double position)
                {
                    if (position < 0)
                        return 0;
                    var index = (int)position;
                    var fraction = position - index;
                    return input[index] * (1 - fraction) + input[Math.Min(index + 1, frames - 1)] * fraction;
                }

                var random = new Random(frames);
                var output = new double[frames];
                double envelope = 0, previousNoise = 0, dullA = 0, dullB = 0;
                var envelopeKeep = Math.Exp(-1 / Samples(10));
                for (var frame = 0; frame < frames; frame++)
                {
                    // Shadow: two taps whose delay grows (the sound read slower: lower), each faded in and out while
                    // the other takes over when its delay jumps back.
                    var phase = frame * (1 - shadowRatio) / window % 1;
                    var other = (phase + 0.5) % 1;
                    var shadow = (1 - Math.Abs(2 * phase - 1)) * At(frame - phase * window)
                                 + (1 - Math.Abs(2 * other - 1)) * At(frame - other * window);

                    // Breath: bright noise as loud as the voice.
                    envelope = Math.Max(Math.Abs(input[frame]), envelope * envelopeKeep);
                    var noise = random.NextDouble() * 2 - 1;
                    var breath = (noise - previousNoise) * 0.5 * envelope;
                    previousNoise = noise;

                    var voice = input[frame] + 0.5 * amount * shadow + 0.12 * amount * breath;

                    // Dark hall: two long feedback delays, dulled at each pass.
                    if (frame >= hallA)
                        dullA += 0.35 * (output[frame - hallA] - dullA);
                    if (frame >= hallB)
                        dullB += 0.35 * (output[frame - hallB] - dullB);
                    output[frame] = voice + hallFeedback * 0.5 * (dullA + dullB);
                }

                // Stutters of a broken doll: now and then, while she speaks, a syllable is repeated twice over what follows.
                for (var start = stutterEvery / 2; start + 3 * stutter < frames; start += stutterEvery / 2 + random.Next(stutterEvery))
                {
                    if (amount < 0.3 || Math.Abs(input[start]) < inputPeak * 0.08)
                        continue;
                    for (var copy = 1; copy <= 2; copy++)
                        Array.Copy(output, start, output, start + copy * stutter, stutter);
                }

                double outputPeak = 0;
                foreach (var sample in output)
                    outputPeak = Math.Max(outputPeak, Math.Abs(sample));
                var gain = outputPeak < 1 ? 1 : inputPeak / outputPeak;
                for (var frame = 0; frame < frames; frame++)
                {
                    var sample = (short)Math.Clamp(Math.Round(output[frame] * gain), short.MinValue, short.MaxValue);
                    BitConverter.GetBytes(sample).CopyTo(bytes, dataStart + (frame * channels + channel) * 2);
                }
            }
            File.WriteAllBytes(file, bytes);
        }
        catch (IOException)
        {
            // Unchanged: the voice keeps its own color.
        }
    }

    /// <summary>Format and position of the samples of a 16-bit PCM WAV file; false for any other content.</summary>
    private static bool TryFindPcm16(byte[] bytes, out int channels, out int sampleRate, out int dataStart, out int dataLength)
    {
        channels = sampleRate = dataLength = 0;
        dataStart = -1;
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            return false;

        var bits = 0;
        for (var offset = 12; offset + 8 <= bytes.Length;)
        {
            var id = Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "fmt " && size >= 16)
            {
                if (BitConverter.ToInt16(bytes, offset + 8) != 1)   // PCM only
                    return false;
                channels = BitConverter.ToInt16(bytes, offset + 10);
                sampleRate = BitConverter.ToInt32(bytes, offset + 12);
                bits = BitConverter.ToInt16(bytes, offset + 22);
            }
            else if (id == "data")
            {
                dataStart = offset + 8;
                dataLength = Math.Min(size, bytes.Length - dataStart);
                break;
            }
            offset += 8 + size + (size & 1);
        }
        return dataStart >= 0 && bits == 16 && channels >= 1 && sampleRate >= 1;
    }

    /// <summary>
    /// Multiplies the sample rate written in the header of a WAV file by <paramref name="pitch"/>: the samples are
    /// played that much faster, so the voice is that much higher (and shorter, which the slower generation makes up for).
    /// </summary>
    private static void RaisePitch(string file, double pitch)
    {
        if (Math.Abs(pitch - 1) < 0.001)
            return;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite);
            var header = new byte[12];
            if (stream.Read(header, 0, 12) < 12 || Encoding.ASCII.GetString(header, 0, 4) != "RIFF" || Encoding.ASCII.GetString(header, 8, 4) != "WAVE")
                return;

            var chunk = new byte[8];
            while (stream.Read(chunk, 0, 8) == 8)
            {
                var size = BitConverter.ToInt32(chunk, 4);
                if (Encoding.ASCII.GetString(chunk, 0, 4) == "fmt " && size >= 16)
                {
                    var format = new byte[16];
                    var start = stream.Position;
                    if (stream.Read(format, 0, 16) < 16)
                        return;
                    var sampleRate = (int)Math.Round(BitConverter.ToInt32(format, 4) * pitch);
                    var blockAlign = BitConverter.ToInt16(format, 12);
                    BitConverter.GetBytes(sampleRate).CopyTo(format, 4);
                    BitConverter.GetBytes(sampleRate * blockAlign).CopyTo(format, 8);   // byte rate
                    stream.Position = start;
                    stream.Write(format, 0, 16);
                    return;
                }
                stream.Position += size + (size & 1);
            }
        }
        catch (IOException)
        {
            // Unchanged: the voice keeps its own pitch.
        }
    }

    /// <summary>
    /// Loudness of a 16-bit PCM WAV file (Piper writes mono 22,050 Hz), one value every <see cref="EnvelopeStepMs"/>:
    /// the RMS of the step relative to the loudest step, slightly compressed so that quiet syllables still show, and
    /// silence gated to 0. Empty when the file cannot be read or is in another format.
    /// </summary>
    private static float[] LoudnessEnvelope(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                return [];

            int channels = 0, sampleRate = 0, bits = 0, dataStart = -1, dataLength = 0;
            for (var offset = 12; offset + 8 <= bytes.Length;)
            {
                var id = Encoding.ASCII.GetString(bytes, offset, 4);
                var size = BitConverter.ToInt32(bytes, offset + 4);
                if (id == "fmt " && size >= 16)
                {
                    if (BitConverter.ToInt16(bytes, offset + 8) != 1)   // PCM only
                        return [];
                    channels = BitConverter.ToInt16(bytes, offset + 10);
                    sampleRate = BitConverter.ToInt32(bytes, offset + 12);
                    bits = BitConverter.ToInt16(bytes, offset + 22);
                }
                else if (id == "data")
                {
                    dataStart = offset + 8;
                    dataLength = Math.Min(size, bytes.Length - dataStart);
                    break;
                }
                offset += 8 + size + (size & 1);
            }
            if (dataStart < 0 || bits != 16 || channels < 1 || sampleRate < 1)
                return [];

            var frameBytes = 2 * channels;
            var frames = dataLength / frameBytes;
            var framesPerStep = Math.Max(1, sampleRate * EnvelopeStepMs / 1000);
            var levels = new float[(frames + framesPerStep - 1) / framesPerStep];
            for (var step = 0; step < levels.Length; step++)
            {
                double sum = 0;
                var first = step * framesPerStep;
                var last = Math.Min(frames, first + framesPerStep);
                for (var frame = first; frame < last; frame++)
                    for (var channel = 0; channel < channels; channel++)
                    {
                        double sample = BitConverter.ToInt16(bytes, dataStart + frame * frameBytes + channel * 2) / 32768.0;
                        sum += sample * sample;
                    }
                levels[step] = (float)Math.Sqrt(sum / Math.Max(1, (last - first) * channels));
            }

            var loudest = levels.Length == 0 ? 0 : levels.Max();
            if (loudest < 1e-4)
                return new float[levels.Length];
            for (var step = 0; step < levels.Length; step++)
            {
                var level = MathF.Pow(levels[step] / loudest, 0.7f);
                levels[step] = level < 0.12f ? 0 : level;
            }
            return levels;
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>Sentences of <paramref name="markdown"/> to read: no code blocks, formatting or emojis, one per line.</summary>
    private static List<string> Sentences(string markdown)
    {
        var text = WithoutEmojis(PlainText.FromMarkdown(CodeBlock().Replace(markdown, " ")));
        var sentences = new List<string>();
        foreach (var part in SentenceEnd().Split(text))
        {
            var sentence = Whitespace().Replace(part, " ").Trim();
            while (sentence.Length > MaxSentenceLength)
            {
                var cut = sentence.LastIndexOfAny([',', ';', ' '], MaxSentenceLength);
                if (cut <= 0)
                    cut = MaxSentenceLength;
                sentences.Add(sentence[..cut].Trim());
                sentence = sentence[(cut + 1)..].Trim();
            }
            if (sentence.Any(char.IsLetterOrDigit))
                sentences.Add(sentence);
        }
        return sentences;
    }

    /// <summary>
    /// <paramref name="text"/> without emojis (pictographs, flags, skin tones, joiners, variation selectors) nor text
    /// emoticons such as <c>:)</c> or <c>&lt;3</c>, which Piper would spell out. Symbols below U+2000 (°, ©…) stay.
    /// </summary>
    private static string WithoutEmojis(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in TextEmoticon().Replace(text, " ").EnumerateRunes())
        {
            var emoji = rune.Value is 0x200D or 0x20E3 or (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0020 and <= 0xE007F)
                        || (rune.Value >= 0x2000 && Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.OtherSymbol
                                or System.Globalization.UnicodeCategory.ModifierSymbol);
            builder.Append(emoji ? " " : rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>Text emoticons: <c>:) ;-) :D :P =( :'( :| :* &lt;3 ^_^ xD</c>…</summary>
    [GeneratedRegex(@"(?<![\w:])(?:[:;=][-'^o]?[)(\]\[DPpO3|*]|<3|\^_?\^|[xX]D)(?![\w/])")]
    private static partial Regex TextEmoticon();

    [GeneratedRegex(@"^(```|~~~).*?^(```|~~~)[^\n]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CodeBlock();

    [GeneratedRegex(@"(?<=[.!?…:;])\s+|\n+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"Wrote\s+(.+\.wav)\s*$")]
    private static partial Regex WroteLine();
}
