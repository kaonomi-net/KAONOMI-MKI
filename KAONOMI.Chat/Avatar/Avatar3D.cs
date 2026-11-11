using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.Views;
using SkiaSharp;

namespace KAONOMI.UI.Avatar;

/// <summary>
/// The 3D animation of the chat, drawn with OpenGL as in the Godot chat and with the same shaders, shared as they are
/// (<see cref="FaceShaders"/>, <see cref="JarvisShaders"/>):
/// <list type="bullet">
/// <item>a face (MARI, KAONOMI, JEROME, HAL, PEPPER…): its image on a grid in relief seen by a 3D camera, the head
/// swaying, the jaw dropping with the voice while the answer is read aloud and with the words shown otherwise, a thin band
/// glitching now and then;</item>
/// <item>JARVIS: the holographic display (reticle, gyroscope rings, wireframe sphere, core, voice spectrum), one ray per
/// pixel, driven by the same level.</item>
/// </list>
/// When OpenGL is not available, nothing is drawn and <see cref="Error"/> says why.
/// </summary>
public sealed class Avatar3D : OpenGlControlBase
{
    private const float FieldOfView = MathF.PI / 4;
    private const float Decay = 0.92f;          // word energy kept from one frame to the next (at 60 fps)
    private const float Attack = 0.55f;         // share of the way to a louder voice made in one frame
    private const float FaceRelease = 0.2f;     // share of the way to a quieter voice made in one frame (a face)
    private const float JarvisRelease = 0.12f;  // the same for JARVIS
    private const int GridSize = 192;

    private readonly SceneAnimation _animation;
    private readonly bool _jarvis;
    private readonly Random _random = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Gl? _gl;
    private int _program, _vao, _vertices, _indices, _texture, _indexCount;
    private int _uMvp, _uOpen, _uTime, _uGlitch, _uFace, _uFrame, _uLevel, _uLight;
    private double _last;
    private float _energy, _level;
    private float _glitchRow = 99, _glitchShift;
    private double _glitchEnd, _nextGlitch = 3;

    public Avatar3D(SceneAnimation animation)
    {
        _animation = animation == SceneAnimation.None ? SceneAnimation.Kaonomi : animation;
        _jarvis = _animation == SceneAnimation.Jarvis;
        IsHitTestVisible = false;
    }

    /// <summary>The animation shown.</summary>
    public SceneAnimation Animation => _animation;

    /// <summary>Loudness of the voice reading the answer, 0 to 1, read at each frame.</summary>
    public Func<float>? VoiceLevel { get; set; }

    /// <summary>True while the answer is read aloud (the mouth follows the voice).</summary>
    public bool Speaking { get; set; }

    /// <summary>True while the server writes the answer.</summary>
    public bool Thinking { get; set; }

    /// <summary>The distance of the camera to a face (2.55 usually): smaller, the face comes closer.</summary>
    public float Distance { get; set; } = 2.55f;

    /// <summary>Why nothing is drawn (OpenGL missing, a shader refused), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>A word of the answer was shown: the mouth opens, wider for longer words.</summary>
    public void Pulse(int length) => _energy = Math.Min(1, _energy + Math.Clamp(length / 7f, 0.3f, 1f) * 0.6f);

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = new Gl(gl);
            if (_jarvis)
                InitJarvis(_gl);
            else
                InitFace(_gl);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or NotSupportedException)
        {
            Error = ex.Message;
            ActivityLog.Error("Avatar", $"The 3D animation {_animation} cannot be drawn: {ex.Message}");
        }
    }

    private void InitFace(Gl gl)
    {
        var face = FaceShaders.For(_animation);
        var common = face.Glsl + FaceShaders.ShapeFunctions + face.Shape;
        _program = gl.Program(gl.Header + common + FaceShaders.VertexShader, gl.Header + common + FaceShaders.FragmentShader, "aUv");
        _uMvp = gl.Uniform(_program, "uMvp");
        _uOpen = gl.Uniform(_program, "uOpen");
        _uTime = gl.Uniform(_program, "uTime");
        _uGlitch = gl.Uniform(_program, "uGlitch");
        _uFace = gl.Uniform(_program, "uFace");

        // The grid over the image (texture coordinates 0..1, y down); the vertex shader places its points in relief.
        const int side = GridSize + 1;
        var uvs = new float[side * side * 2];
        for (var row = 0; row < side; row++)
            for (var column = 0; column < side; column++)
            {
                uvs[(row * side + column) * 2] = column / (float)GridSize;
                uvs[(row * side + column) * 2 + 1] = row / (float)GridSize;
            }
        var indexes = new uint[GridSize * GridSize * 6];
        var index = 0;
        for (var row = 0; row < GridSize; row++)
            for (var column = 0; column < GridSize; column++)
            {
                var corner = (uint)(row * side + column);
                foreach (var point in new[] { corner, corner + 1, corner + side + 1, corner, corner + side + 1, corner + side })
                    indexes[index++] = point;
            }
        _indexCount = indexes.Length;
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vertices = gl.GenBuffer();
        gl.BindBuffer(Gl.ArrayBuffer, _vertices);
        gl.BufferData(Gl.ArrayBuffer, uvs);
        gl.VertexAttribPointer(0, 2, 8, 0);
        _indices = gl.GenBuffer();
        gl.BindBuffer(Gl.ElementArrayBuffer, _indices);
        gl.BufferData(Gl.ElementArrayBuffer, indexes);
        gl.BindVertexArray(0);

        var (rgba, width, height) = LoadFace(face.Image);
        _texture = gl.Texture(rgba, width, height);
    }

    private void InitJarvis(Gl gl)
    {
        _program = gl.Program(gl.Header + JarvisShaders.VertexShader, gl.Header + JarvisShaders.FragmentShader, "aPos");
        _uFrame = gl.Uniform(_program, "uFrame");
        _uLevel = gl.Uniform(_program, "uLevel");
        _uLight = gl.Uniform(_program, "uLight");
        _uGlitch = gl.Uniform(_program, "uGlitch");
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vertices = gl.GenBuffer();
        gl.BindBuffer(Gl.ArrayBuffer, _vertices);
        gl.BufferData(Gl.ArrayBuffer, new float[] { -1, -1, 1, -1, -1, 1, 1, 1 });
        gl.VertexAttribPointer(0, 2, 8, 0);
        gl.BindVertexArray(0);
    }

    /// <summary>
    /// The image of a face (Assets next to the program, the file name of its avares:// address), as premultiplied RGBA,
    /// top row first, as the shaders expect.
    /// </summary>
    private static (byte[] Rgba, int Width, int Height) LoadFace(string image)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", Path.GetFileName(new Uri(image).AbsolutePath));
        using var codec = SKCodec.Create(path) ?? throw new IOException($"the face image {path} cannot be read");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = SKBitmap.Decode(codec, info) ?? throw new IOException($"the face image {path} cannot be decoded");
        return (bitmap.Bytes, bitmap.Width, bitmap.Height);
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var frames = (float)Math.Clamp((now - _last) * 60, 0, 10);
        _last = now;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Max(1, (int)(Bounds.Width * scaling));
        var height = Math.Max(1, (int)(Bounds.Height * scaling));
        if (_gl is not { } g)
            return;
        g.BindFramebuffer(fb);
        g.Viewport(0, 0, width, height);
        g.Clear(0, 0, 0, 0);
        if (Error is not null || _program == 0)
            return;

        // The level: the loudness of the voice while the answer is read aloud, the words shown otherwise.
        _energy *= MathF.Pow(Decay, frames);
        var voice = Speaking ? Math.Clamp(VoiceLevel?.Invoke() ?? 0, 0, 1) : 0;
        var target = _jarvis ? (Speaking ? voice : _energy) : voice;
        var share = target > _level ? Attack : _jarvis ? JarvisRelease : FaceRelease;
        _level += (target - _level) * (1 - MathF.Pow(1 - share, frames));
        var open = _jarvis ? _level : Speaking ? _level : _energy;

        // A thin band of the picture shifts sideways for a fraction of a second, every few seconds.
        if (now > _nextGlitch)
        {
            _glitchRow = (float)_random.NextDouble();
            _glitchShift = _jarvis ? (float)((_random.NextDouble() - 0.5) * 24 * scaling) : (float)((_random.NextDouble() - 0.5) * 0.04);
            _glitchEnd = now + (_jarvis ? 0.08 : 0.1);
            _nextGlitch = now + (_jarvis ? 5 + _random.NextDouble() * 9 : 4 + _random.NextDouble() * 8);
        }
        else if (now > _glitchEnd)
            _glitchRow = 99;

        var time = (float)(now % 3600);
        g.Disable(Gl.DepthTest);
        g.Disable(Gl.CullFace);
        g.Enable(Gl.Blend);
        g.BlendFunc(Gl.One, Gl.OneMinusSrcAlpha);
        g.UseProgram(_program);
        g.BindVertexArray(_vao);
        if (_jarvis)
        {
            g.Uniform3(_uFrame, width, height, time);
            g.Uniform3(_uGlitch, _glitchRow, _glitchShift, 0);
            g.Uniform1(_uLevel, Math.Clamp(_level, 0, 1));
            g.Uniform1(_uLight, 0f);
            g.DrawArrays(Gl.TriangleStrip, 0, 4);
        }
        else
        {
            // The head sways slowly, as if it looked around (the matrices of the Godot chat).
            var model = Matrix4x4.CreateRotationY(0.2f * MathF.Sin(time * 0.37f)) * Matrix4x4.CreateRotationX(0.05f * MathF.Sin(time * 0.29f));
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, Distance), Vector3.Zero, Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, width / (float)height, 0.02f, 40);
            g.UniformMatrix4(_uMvp, model * view * projection);
            g.Uniform1(_uOpen, Math.Clamp(open, 0, 1));
            g.Uniform1(_uTime, time);
            g.Uniform3(_uGlitch, _glitchRow, _glitchShift, 0);
            g.BindTexture(_texture);
            g.Uniform1(_uFace, 0);
            g.DrawElements(Gl.Triangles, _indexCount);
        }
        g.BindVertexArray(0);
        g.UseProgram(0);
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl is not { } g)
            return;
        if (_program != 0)
            g.DeleteProgram(_program);
        if (_vertices != 0)
            g.DeleteBuffer(_vertices);
        if (_indices != 0)
            g.DeleteBuffer(_indices);
        if (_vao != 0)
            g.DeleteVertexArray(_vao);
        if (_texture != 0)
            g.DeleteTexture(_texture);
        _program = _vertices = _indices = _vao = _texture = 0;
        _gl = null;
    }
}
