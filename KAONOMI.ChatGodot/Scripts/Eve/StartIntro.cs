using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The intro at the start of the chat: the 3D animation of Kaonomi, already close to the camera, comes a little closer
/// and says "Let's go to think" in English with a deep robot voice (Piper, its English voice of a man, lowered and
/// robotised; the words written under it), after the sound of a sliding door that opens by itself, as in the films and
/// games of science fiction (a latch, a pneumatic hiss, the slide, the stop), over the ambient sound of a lab of AI (the
/// hum of the servers, the air of their fans, a soft drone, the chirps and relays of the machines at work). The application goes to full screen with
/// it. A key or a click skips it;
/// <c>--no-intro</c> on the command line too.
/// </summary>
public partial class StartIntro : Control
{
    /// <summary>The sound of the door, in seconds; the intro fades from <see cref="FadeAt"/> once the voice is done (<see cref="SpeechWait"/> at most).</summary>
    private const double Length = 1.8;
    private const double FadeAt = 5.5;
    private const double SpeechWait = 4;

    /// <summary>The loop of the ambient sound of the lab, in seconds.</summary>
    private const double AmbienceLength = 8;

    private const int Rate = 44100;

    /// <summary>The words of the animation, said with <see cref="_voice"/>.</summary>
    private const string Words = "Let's go to think";

    /// <summary>The animation appears, comes closer until <see cref="CloseAt"/>, and speaks at <see cref="SayAt"/> (seconds).</summary>
    private const float CloseAt = 4.5f, SayAt = 1.2f;

    /// <summary>The camera: close at the start (the head and the shoulders), a little closer at the end.</summary>
    private const float StartDistance = 1.5f, EndDistance = 1.05f;

    /// <summary>Where the camera aims: the head of the robot, near its eye (at 0.11, 0.34).</summary>
    private static readonly Vector2 HeadAim = new(0.06f, 0.3f);

    private static readonly Color Paper = new(0.975f, 0.972f, 0.965f);
    private static readonly Color Gold = new(196 / 255f, 154 / 255f, 60 / 255f);

    /// <summary>Raised once, when the intro is over (ended or skipped), just before it is freed.</summary>
    public event Action? Finished;

    /// <summary>
    /// The deep robot voice: Piper with its English voice of a man (the voices of a single speaker first), a third lower
    /// and robotised, with the sheen of a film's AI; null when Piper or no English voice is installed.
    /// </summary>
    private readonly SpeechService? _voice;
    private static readonly string[] DeepVoices = ["en_GB-alan-medium", "en_US-ryan-high", "en_US-john-medium", "en_GB-alan-low", "en_US-danny-low"];

    private readonly FaceAvatar _face;
    private readonly Label _words;
    private AudioStreamPlayer? _player, _ambience;
    private Task<AudioStreamWav>? _sound, _lab;
    private double _time = -1;
    private bool _leaving, _said;

    public StartIntro(ChatSettings settings)
    {
        var voices = ChatSettings.ResolveDirectory(settings.TtsVoicesDirectory);
        var voice = DeepVoices.Select(name => Path.Combine(voices, name + ".onnx")).FirstOrDefault(File.Exists)
                    ?? settings.InstalledTtsVoice("EN");
        if (settings.TtsEnabled && voice is not null)
        {
            var speech = new SpeechService(settings.ResolvedTtsPiperPath, voice, null, 0.9, pitch: 0.68, robot: 0.85, assistant: 0.4);
            _voice = speech.IsAvailable ? speech : null;
        }
        Name = nameof(StartIntro);
        MouseFilter = MouseFilterEnum.Stop;
        _face = new FaceAvatar(SceneAnimation.Kaonomi) { Distance = StartDistance, Aim = HeadAim, Modulate = Colors.Transparent, VoiceLevel = () => _voice?.Level ?? 0 };
        _words = new Label
        {
            Text = Words.ToUpperInvariant(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            VisibleRatio = 0,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _words.AddThemeFontOverride("font", Eve.Display);
        _words.AddThemeColorOverride("font_color", Gold);
        _words.AddThemeColorOverride("font_outline_color", new Color(1, 1, 1, 0.9f));
        _sound = Task.Run(MakeSound);
        _lab = Task.Run(MakeAmbience);
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // The application in full screen with the intro (and after it).
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
        AddChild(_face);
        _face.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_words);
    }

    public override void _Process(double delta)
    {
        if (_time < 0)
        {
            // The sound is made in the background (a fraction of a second): the intro starts with it.
            if (_sound is not { IsCompleted: true } || _lab is not { IsCompleted: true })
                return;
            _time = 0;
            if (_sound.IsCompletedSuccessfully)
            {
                _player = new AudioStreamPlayer { Stream = _sound.Result, VolumeDb = -3 };
                AddChild(_player);
                _player.Play();
            }
            // The lab, in a loop under the door and the voice, rising in half a second.
            if (_lab.IsCompletedSuccessfully)
            {
                _ambience = new AudioStreamPlayer { Stream = _lab.Result, VolumeDb = -40 };
                AddChild(_ambience);
                _ambience.Play();
                CreateTween().TweenProperty(_ambience, "volume_db", -9f, 0.5);
            }
        }
        else
            _time += delta;
        var t = (float)_time;
        // The animation appears close, comes a little closer, then stays; its eye follows the voice.
        _face.Modulate = Colors.White with { A = Smooth(t / 0.6f) };
        _face.Distance = Mathf.Lerp(StartDistance, EndDistance, Smooth(t / CloseAt));
        _face.Speaking = _voice is { IsActive: true };
        if (!_said && t >= SayAt)
        {
            _said = true;
            _voice?.Speak(Words);
        }
        var m = Math.Min(Size.X, Size.Y);
        _words.AddThemeFontSizeOverride("font_size", (int)(m * 0.055f));
        _words.AddThemeConstantOverride("outline_size", (int)(m * 0.012f));
        _words.Position = new Vector2(0, Size.Y * 0.8f);
        _words.Size = new Vector2(Size.X, Size.Y * 0.12f);
        _words.VisibleRatio = Smooth((t - SayAt - 0.2f) / 0.9f);
        if (!_leaving && t >= FadeAt && (_voice is not { IsActive: true } || t >= FadeAt + SpeechWait))
            Leave(0.8);
    }

    public override void _Input(InputEvent e)
    {
        // A key or a click skips the intro (and is not given to the chat).
        if (e is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true })
        {
            GetViewport().SetInputAsHandled();
            if (!_leaving)
                Leave(0.5);
        }
    }

    /// <summary>The intro fades out (and its sound) in <paramref name="seconds"/>, then it is freed.</summary>
    private void Leave(double seconds)
    {
        _leaving = true;
        var tween = CreateTween().SetParallel();
        tween.TweenProperty(this, "modulate:a", 0f, seconds);
        if (_player is not null)
            tween.TweenProperty(_player, "volume_db", -60f, seconds);
        if (_ambience is not null)
            tween.TweenProperty(_ambience, "volume_db", -60f, seconds);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            _voice?.Dispose();
            Finished?.Invoke();
            QueueFree();
        }));
    }

    public override void _Draw() => DrawRect(new Rect2(Vector2.Zero, Size), Paper);

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    // ---- The sound ----

    /// <summary>
    /// The sound of the intro, in stereo: a sliding door of science fiction that opens by itself. The latch gives (a
    /// click and a low thump), the air escapes (a pneumatic hiss that falls from high to lower), the door slides (the
    /// whir of a motor that rises, the grind of a heavy panel), then it stops against its frame (a thud and a metallic
    /// clank); in a small room.
    /// </summary>
    private static AudioStreamWav MakeSound()
    {
        var random = new Random(5);
        var frames = (int)(Length * Rate);
        var left = new float[frames];
        var right = new float[frames];

        // The latch: a click of noise and a low thump.
        for (var n = 0; n < (int)(0.12 * Rate); n++)
        {
            var u = n / (double)Rate;
            var click = (random.NextDouble() * 2 - 1) * Math.Exp(-u / 0.004) * 0.5;
            var thump = Math.Sin(Math.Tau * (95 - 30 * u / 0.12) * u) * Math.Exp(-u / 0.05) * 0.8;
            Mix(left, right, n, click + thump, 0);
        }
        // The air: noise through a band-pass (two one-poles) whose centre falls from 6 kHz to 1.8 kHz, a different noise
        // in each ear; a fast attack, then a decay.
        for (var side = 0; side < 2; side++)
        {
            var samples = side == 0 ? left : right;
            double low = 0, lower = 0, lowest = 0;
            for (var n = (int)(0.02 * Rate); n < (int)(0.75 * Rate); n++)
            {
                var u = n / (double)Rate - 0.02;
                var centre = 1800 + 4200 * Math.Exp(-u / 0.2);
                var noise = random.NextDouble() * 2 - 1;
                var top = 1 - Math.Exp(-Math.Tau * centre * 1.4 / Rate);
                low += (noise - low) * top;
                lower += (low - lower) * top;
                lowest += (lower - lowest) * (1 - Math.Exp(-Math.Tau * centre / 1.6 / Rate));
                var envelope = Math.Min(1, u / 0.015) * Math.Exp(-u / 0.13);
                samples[n] += (float)((lower - lowest) * envelope * 1.3);
            }
        }
        // The slide: a motor whose whir rises (a buzz of a few harmonics) and the grind of the panel (low noise).
        double phase = 0, grind = 0;
        for (var n = (int)(0.08 * Rate); n < (int)(0.82 * Rate); n++)
        {
            var u = n / (double)Rate - 0.08;
            var span = 0.74;
            var envelope = Math.Min(1, u / 0.08) * Math.Min(1, (span - u) / 0.05);
            phase += Math.Tau * (85 + 70 * u / span) / Rate;
            var whir = Math.Sin(phase) + 0.5 * Math.Sin(2 * phase) + 0.3 * Math.Sin(3 * phase) + 0.15 * Math.Sin(5 * phase);
            grind += ((random.NextDouble() * 2 - 1) - grind) * 0.04;
            var rattle = 0.8 + 0.2 * Math.Sin(Math.Tau * 23 * u);
            Mix(left, right, n, (whir * 0.22 + grind * 2.2 * rattle) * envelope, -0.6 + 1.2 * u / span);
        }
        // The stop against the frame: a thud and a clank of metal (inharmonic partials), a little to the right.
        var stop = (int)(0.82 * Rate);
        double[] partials = { 420, 1130, 1870, 2650 };
        for (var n = stop; n < frames; n++)
        {
            var u = (n - stop) / (double)Rate;
            var thud = Math.Sin(Math.Tau * (70 - 20 * Math.Min(1, u / 0.2)) * u) * Math.Exp(-u / 0.09);
            var clank = 0.0;
            for (var k = 0; k < partials.Length; k++)
                clank += Math.Sin(Math.Tau * partials[k] * u) * Math.Exp(-u / (0.12 / (k + 1))) / (k + 1);
            Mix(left, right, n, thud * 0.9 + clank * 0.35, 0.6);
        }

        Reverb(left, new[] { 1557, 1617, 1491, 1422 }, 0.12f);
        Reverb(right, new[] { 1580, 1640, 1514, 1445 }, 0.12f);
        var peak = 0f;
        for (var n = 0; n < frames; n++)
            peak = Math.Max(peak, Math.Max(Math.Abs(left[n]), Math.Abs(right[n])));
        var gain = peak > 0 ? 0.9f / peak : 1;
        var data = new byte[frames * 4];
        for (var n = 0; n < frames; n++)
        {
            // The last 0.3 s fade out (the reverberation).
            var fade = Math.Clamp((frames - n) / (Rate * 0.3), 0, 1);
            var l = (short)(Math.Tanh(left[n] * gain * fade) * 32000);
            var r = (short)(Math.Tanh(right[n] * gain * fade) * 32000);
            data[n * 4] = (byte)l;
            data[n * 4 + 1] = (byte)(l >> 8);
            data[n * 4 + 2] = (byte)r;
            data[n * 4 + 3] = (byte)(r >> 8);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = true, Data = data };
    }

    /// <summary>
    /// The ambient sound of a lab of AI, a loop of <see cref="AmbienceLength"/> seconds: the hum of the servers (the mains
    /// and its harmonics, beating slowly), the air of their fans (a soft low noise, different in each ear), a drone of
    /// two detuned fifths that breathes, and now and then the chirps of the machines at work (short soft tones) and the
    /// clicks of relays, here and there in the room. Its end is crossfaded into its start, so that it loops without a seam.
    /// </summary>
    private static AudioStreamWav MakeAmbience()
    {
        var random = new Random(23);
        var frames = (int)(AmbienceLength * Rate);
        var overlap = Rate / 2;
        var left = new float[frames + overlap];
        var right = new float[frames + overlap];
        var total = left.Length;

        // The servers and the drone (periodic over the loop: whole numbers of cycles in its length).
        for (var n = 0; n < total; n++)
        {
            var t = (double)n / Rate;
            var hum = Math.Sin(Math.Tau * 50 * t) * 0.05 + Math.Sin(Math.Tau * 100.25 * t) * 0.035 + Math.Sin(Math.Tau * 150 * t) * 0.02
                      + Math.Sin(Math.Tau * 300.5 * t) * 0.006;
            var breath = 0.75 + 0.25 * Math.Sin(Math.Tau * t / AmbienceLength);
            var drone = (Math.Sin(Math.Tau * 110 * t) + Math.Sin(Math.Tau * 110.375 * t) + 0.7 * Math.Sin(Math.Tau * 165 * t)
                         + 0.5 * Math.Sin(Math.Tau * 220.25 * t)) * 0.018 * breath;
            left[n] += (float)(hum + drone);
            right[n] += (float)(hum * 0.95 + drone);
        }
        // The fans: low-passed noise (twice), a little louder and darker on one side.
        for (var side = 0; side < 2; side++)
        {
            var samples = side == 0 ? left : right;
            var cutoff = 1 - Math.Exp(-Math.Tau * (side == 0 ? 900 : 1200) / Rate);
            double a = 0, b = 0;
            for (var n = 0; n < total; n++)
            {
                a += ((random.NextDouble() * 2 - 1) - a) * cutoff;
                b += (a - b) * cutoff;
                samples[n] += (float)(b * 0.5);
            }
        }
        // The machines at work: soft chirps of one to three short tones, and clicks of relays.
        for (var at = 0.3; at < AmbienceLength - 0.3; at += 0.35 + random.NextDouble() * 0.9)
        {
            var pan = random.NextDouble() * 1.6 - 0.8;
            if (random.NextDouble() < 0.3)
            {
                var start = (int)(at * Rate);
                for (var n = start; n < start + Rate / 100; n++)
                    Mix(left, right, n, (random.NextDouble() * 2 - 1) * Math.Exp(-(n - start) / (Rate * 0.0015)) * 0.08, pan);
                continue;
            }
            var tones = random.Next(1, 4);
            for (var k = 0; k < tones; k++)
            {
                var frequency = 900 + random.Next(0, 9) * 220;
                var from = at + k * 0.07;
                const double length = 0.05;
                for (var n = (int)(from * Rate); n < (int)((from + length) * Rate); n++)
                {
                    var u = n / (double)Rate - from;
                    var gate = Math.Min(1, Math.Min(u, length - u) / 0.008);
                    Mix(left, right, n, Math.Sin(Math.Tau * frequency * u) * gate * 0.03, pan);
                }
            }
        }
        Reverb(left, new[] { 1757, 1817, 1691, 1622 }, 0.25f);
        Reverb(right, new[] { 1780, 1840, 1714, 1645 }, 0.25f);

        // The end crossfaded into the start: the loop has no seam.
        for (var n = 0; n < overlap; n++)
        {
            var share = (float)n / overlap;
            left[n] = left[n] * share + left[frames + n] * (1 - share);
            right[n] = right[n] * share + right[frames + n] * (1 - share);
        }
        var peak = 0f;
        for (var n = 0; n < frames; n++)
            peak = Math.Max(peak, Math.Max(Math.Abs(left[n]), Math.Abs(right[n])));
        var gain = peak > 0 ? 0.8f / peak : 1;
        var data = new byte[frames * 4];
        for (var n = 0; n < frames; n++)
        {
            var l = (short)(left[n] * gain * 32000);
            var r = (short)(right[n] * gain * 32000);
            data[n * 4] = (byte)l;
            data[n * 4 + 1] = (byte)(l >> 8);
            data[n * 4 + 2] = (byte)r;
            data[n * 4 + 3] = (byte)(r >> 8);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = true, Data = data,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = frames
        };
    }

    private static void Mix(float[] left, float[] right, int n, double v, double pan)
    {
        if (n >= left.Length)
            return;
        left[n] += (float)(v * Math.Sqrt((1 - pan) / 2));
        right[n] += (float)(v * Math.Sqrt((1 + pan) / 2));
    }

    /// <summary>A room: parallel comb filters (Schroeder) mixed with the dry sound.</summary>
    private static void Reverb(float[] samples, int[] delays, float mix)
    {
        var wet = new float[samples.Length];
        foreach (var delay in delays)
        {
            var line = new float[delay];
            var index = 0;
            float damp = 0;
            for (var n = 0; n < samples.Length; n++)
            {
                var output = line[index];
                damp += (output - damp) * 0.6f;
                line[index] = samples[n] + damp * 0.8f;
                wet[n] += output;
                index = (index + 1) % delay;
            }
        }
        for (var n = 0; n < samples.Length; n++)
            samples[n] += wet[n] * mix;
    }
}
