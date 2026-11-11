using System;
using Godot;
using KAONOMI.Chat.Views;

namespace KAONOMI.ChatGodot;

/// <summary>
/// JARVIS, the holographic display of the Avalonia chat (reticle, gyroscope rings, wireframe sphere, glowing core and
/// voice spectrum): the same fragment shader (<see cref="JarvisShaders"/>, translated by
/// <see cref="SceneShaderTranslator"/>) driven as in JarvisScene, by the loudness of the voice while the answer is read
/// aloud and by the words shown otherwise, with a glitch now and then.
/// </summary>
public partial class JarvisAvatar : ColorRect, IAvatar
{
    private const float Decay = 0.92f;          // word energy kept from one frame to the next (at 60 fps)
    private const float LevelAttack = 0.55f;    // share of the way to a louder level made in one frame
    private const float LevelRelease = 0.12f;   // share of the way to a quieter level made in one frame

    private readonly Random _random = new();
    private ShaderMaterial _material = null!;
    private double _time;
    private float _energy, _level;
    private float _glitchRow = 99, _glitchShift;
    private double _glitchEnd, _nextGlitch = 3;

    public JarvisAvatar() => MouseFilter = MouseFilterEnum.Ignore;

    public Func<float>? VoiceLevel { get; set; }
    public bool Speaking { get; set; }
    public bool Thinking { get; set; }

    /// <summary>A word shown: the voice level rises, more for longer words.</summary>
    public void Pulse(int length) => _energy = Math.Min(1, _energy + Math.Clamp(length / 7f, 0.3f, 1f) * 0.6f);

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = new Shader { Code = SceneShaderTranslator.Jarvis() } };
        Material = _material;
    }

    // The names of the shader parameters, made once: a string given at each frame is a new StringName each time.
    private static readonly StringName UFrame = "uFrame", UGlitch = "uGlitch", ULevel = "uLevel", ULight = "uLight";

    public override void _Process(double delta)
    {
        // Nothing to compute for an animation that is not shown.
        if (!IsVisibleInTree())
            return;
        _time += delta;
        var frames = (float)(delta * 60);
        var scaling = GetWindow().ContentScaleFactor;

        // Voice level: the loudness while the answer is read aloud, the streamed words otherwise.
        _energy *= MathF.Pow(Decay, frames);
        var target = Speaking ? Math.Clamp(VoiceLevel?.Invoke() ?? 0, 0, 1) : _energy;
        var share = target > _level ? LevelAttack : LevelRelease;
        _level += (target - _level) * (1 - MathF.Pow(1 - share, frames));

        // A thin band of the picture shifts sideways for a fraction of a second, every few seconds.
        if (_time > _nextGlitch)
        {
            _glitchRow = (float)_random.NextDouble();
            _glitchShift = (float)((_random.NextDouble() - 0.5) * 24 * scaling);
            _glitchEnd = _time + 0.08;
            _nextGlitch = _time + 5 + _random.NextDouble() * 9;
        }
        else if (_time > _glitchEnd)
        {
            _glitchRow = 99;
        }

        var pixels = Size * scaling;
        _material.SetShaderParameter(UFrame, new Vector3(Math.Max(1, pixels.X), Math.Max(1, pixels.Y), (float)(_time % 3600)));
        _material.SetShaderParameter(UGlitch, new Vector3(_glitchRow, _glitchShift, 0));
        _material.SetShaderParameter(ULevel, Math.Clamp(_level, 0, 1));
        _material.SetShaderParameter(ULight, 0f);
    }
}
