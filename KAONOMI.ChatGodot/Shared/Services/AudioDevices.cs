using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PortAudioSharp;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The microphone and the loudspeakers of the chat (PortAudio): the devices of the system, the ones chosen with
/// <c>/audiosettings</c> (<see cref="Input"/> and <see cref="Output"/>, null for the default of the system), and a WAV
/// player on the chosen output. Only the devices of the host API of the default device are listed (on Windows the same
/// device is also seen through MME, DirectSound, WASAPI and WDM-KS).
/// </summary>
public static class AudioDevices
{
    private static readonly object InitLock = new();
    private static bool _ready;

    /// <summary>Name of the microphone chosen (null: the default microphone of the system).</summary>
    public static string? Input { get; set; }

    /// <summary>Name of the output chosen for the voice (null: the default player of the system).</summary>
    public static string? Output { get; set; }

    /// <summary>Starts PortAudio once for the whole application.</summary>
    public static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_ready)
                return;
            PortAudio.Initialize();
            _ready = true;
        }
    }

    /// <summary>The names of the microphones.</summary>
    public static IReadOnlyList<string> Inputs() => List(input: true);

    /// <summary>The names of the outputs (loudspeakers, headphones…).</summary>
    public static IReadOnlyList<string> Outputs() => List(input: false);

    /// <summary>The microphone chosen, or the default one when it is not there any more (<see cref="PortAudio.NoDevice"/>: none).</summary>
    public static int InputDevice() => Find(Input, input: true);

    /// <summary>The microphone named <paramref name="name"/> (null or empty: the default one of the system), or the default one when it is not there.</summary>
    public static int InputDevice(string? name) => Find(name, input: true);

    /// <summary>The output chosen, or the default one when it is not there any more (<see cref="PortAudio.NoDevice"/>: none).</summary>
    public static int OutputDevice() => Find(Output, input: false);

    private static IReadOnlyList<string> List(bool input)
    {
        EnsureInitialized();
        var names = new List<string>();
        var fallback = input ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;
        if (fallback == PortAudio.NoDevice)
            return names;
        var hostApi = PortAudio.GetDeviceInfo(fallback).hostApi;
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.hostApi == hostApi && (input ? info.maxInputChannels : info.maxOutputChannels) > 0 && !names.Contains(info.name))
                names.Add(info.name);
        }
        return names;
    }

    private static int Find(string? name, bool input)
    {
        EnsureInitialized();
        var fallback = input ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;
        if (string.IsNullOrEmpty(name) || fallback == PortAudio.NoDevice)
            return fallback;
        var hostApi = PortAudio.GetDeviceInfo(fallback).hostApi;
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.hostApi == hostApi && info.name == name && (input ? info.maxInputChannels : info.maxOutputChannels) > 0)
                return i;
        }
        ActivityLog.Info("Audio", $"{(input ? "Microphone" : "Output")} \"{name}\" not found: the default one is used");
        return fallback;
    }

    /// <summary>
    /// Plays a 16-bit PCM WAV file on <see cref="Output"/> and waits for its end (stopped on cancellation). The sound is
    /// resampled to the rate of the device, so any device takes it.
    /// </summary>
    public static async Task PlayAsync(string file, CancellationToken cancellationToken)
    {
        var (samples, fileRate) = ReadMono(file);
        var device = OutputDevice();
        if (device == PortAudio.NoDevice)
            throw new InvalidOperationException("No audio output found");
        var info = PortAudio.GetDeviceInfo(device);
        var rate = (int)Math.Round(info.defaultSampleRate);
        var sound = Resample(samples, fileRate, rate);
        var channels = Math.Min(2, info.maxOutputChannels);
        var parameters = new StreamParameters
        {
            device = device,
            channelCount = channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        var position = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // One buffer for the whole sound (grown when the callback asks more frames): nothing is allocated on the audio thread.
        var buffer = Array.Empty<float>();
        PortAudioSharp.Stream.Callback callback = (IntPtr _, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo _, StreamCallbackFlags _, IntPtr _) =>
        {
            var length = (int)frameCount * channels;
            if (buffer.Length < length)
                buffer = new float[length];
            var frames = (int)Math.Min(frameCount, sound.Length - position);
            for (var i = 0; i < frames; i++)
                for (var c = 0; c < channels; c++)
                    buffer[i * channels + c] = sound[position + i];
            // The end of the sound: silence after its last frame.
            if (frames * channels < length)
                Array.Clear(buffer, frames * channels, length - frames * channels);
            position += frames;
            Marshal.Copy(buffer, 0, output, length);
            if (position < sound.Length)
                return StreamCallbackResult.Continue;
            done.TrySetResult();
            return StreamCallbackResult.Complete;
        };

        using var stream = new PortAudioSharp.Stream(null, parameters, rate, 0, StreamFlags.ClipOff, callback, IntPtr.Zero);
        stream.Start();
        try
        {
            using (cancellationToken.Register(() => done.TrySetCanceled(cancellationToken)))
                await done.Task;
            // The last buffer is still in the device.
            await Task.Delay(TimeSpan.FromSeconds(info.defaultLowOutputLatency + 0.05), cancellationToken);
        }
        finally
        {
            stream.Stop();
            GC.KeepAlive(callback);
        }
    }

    /// <summary>The samples of a 16-bit PCM WAV file, mixed to mono (full scale 1), and its rate.</summary>
    private static (float[] Samples, int Rate) ReadMono(string file)
    {
        using var reader = new BinaryReader(File.OpenRead(file));
        if (new string(reader.ReadChars(4)) != "RIFF")
            throw new InvalidDataException("Not a WAV file: " + file);
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidDataException("Not a WAV file: " + file);
        int channels = 0, rate = 0, bits = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                var format = reader.ReadInt16();
                channels = reader.ReadInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bits = reader.ReadInt16();
                if (format != 1 || bits != 16)
                    throw new InvalidDataException("Only 16-bit PCM WAV files are played: " + file);
                reader.BaseStream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data" && channels > 0)
            {
                var length = (int)Math.Min(size, reader.BaseStream.Length - reader.BaseStream.Position);
                var frames = length / 2 / channels;
                var samples = new float[frames];
                for (var i = 0; i < frames; i++)
                {
                    var sum = 0f;
                    for (var c = 0; c < channels; c++)
                        sum += reader.ReadInt16() / 32768f;
                    samples[i] = sum / channels;
                }
                return (samples, rate);
            }
            else
            {
                reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }
        throw new InvalidDataException("No sound in the WAV file: " + file);
    }

    /// <summary>Linear resampling from <paramref name="from"/> Hz to <paramref name="to"/> Hz.</summary>
    private static float[] Resample(float[] samples, int from, int to)
    {
        if (from == to || samples.Length == 0)
            return samples;
        var length = (int)((long)samples.Length * to / from);
        var result = new float[length];
        var step = (double)from / to;
        for (var i = 0; i < length; i++)
        {
            var x = i * step;
            var j = (int)x;
            var next = Math.Min(j + 1, samples.Length - 1);
            result[i] = (float)(samples[j] + (samples[next] - samples[j]) * (x - j));
        }
        return result;
    }
}
