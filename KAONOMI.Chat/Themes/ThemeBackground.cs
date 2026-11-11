using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using KAONOMI.Chat.Services;
using KAONOMI.UI.Avatar;
using SkiaSharp;

namespace KAONOMI.UI.Themes;

/// <summary>
/// The animated background of a theme, behind the desk, the bar of commands and the chat (their panels are translucent,
/// as in the Godot chat): the shader of the theme from the Godot chat (<see cref="GodotShader"/>) drawn over the whole
/// window at each frame, over the picture of the theme when it has one (Ghost). <see cref="Energy"/> brightens it while
/// the answer is read aloud. When OpenGL or the shader fails, nothing is drawn: the colour of the window shows.
/// </summary>
public sealed class ThemeBackground : OpenGlControlBase
{
    private readonly Palette _palette;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Gl? _gl;
    private int _program, _pictureProgram, _vao, _quad, _picture;
    private int _uSize, _uTime, _uEnergy, _uFrame, _uPicture;
    private float _pictureRatio = 1, _energy;

    public ThemeBackground(Palette palette)
    {
        _palette = palette;
        IsHitTestVisible = false;
    }

    /// <summary>The theme drawn.</summary>
    public Palette Palette => _palette;

    /// <summary>0 to 1, read at each frame: the background brightens (the Godot chat sets it while Kaonomi speaks).</summary>
    public Func<float>? Energy { get; set; }

    /// <summary>Why nothing is drawn, or null.</summary>
    public string? Error { get; private set; }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            var g = _gl = new Gl(gl);
            var fragment = GodotShader.Translate(File.ReadAllText(_palette.ShaderPath));
            _program = g.Program(g.Header + GodotShader.Vertex, g.Header + fragment, "aPos");
            _uSize = g.Uniform(_program, "uSize");
            _uTime = g.Uniform(_program, "uTime");
            _uEnergy = g.Uniform(_program, "energy");
            _vao = g.GenVertexArray();
            g.BindVertexArray(_vao);
            _quad = g.GenBuffer();
            g.BindBuffer(Gl.ArrayBuffer, _quad);
            g.BufferData(Gl.ArrayBuffer, new float[] { -1, -1, 1, -1, -1, 1, 1, 1 });
            g.VertexAttribPointer(0, 2, 8, 0);
            g.BindVertexArray(0);
            if (_palette.PicturePath is { } path && File.Exists(path))
            {
                using var codec = SKCodec.Create(path) ?? throw new IOException($"the picture {path} cannot be read");
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var bitmap = SKBitmap.Decode(codec, info) ?? throw new IOException($"the picture {path} cannot be decoded");
                _picture = g.Texture(bitmap.Bytes, bitmap.Width, bitmap.Height);
                _pictureRatio = bitmap.Width / (float)bitmap.Height;
                _pictureProgram = g.Program(g.Header + GodotShader.Vertex, g.Header + GodotShader.Picture, "aPos");
                _uFrame = g.Uniform(_pictureProgram, "uFrame");
                _uPicture = g.Uniform(_pictureProgram, "uPicture");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            ActivityLog.Error("Theme", $"The background of the theme {_palette.Name} cannot be drawn: {ex.Message}");
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is not { } g)
            return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Max(1, (int)(Bounds.Width * scaling));
        var height = Math.Max(1, (int)(Bounds.Height * scaling));
        g.BindFramebuffer(fb);
        g.Viewport(0, 0, width, height);
        var space = _palette.Space;
        g.Clear(space.R / 255f, space.G / 255f, space.B / 255f, 1);
        if (Error is not null || _program == 0)
            return;
        // The energy follows the voice softly, as the Godot chat tweens it.
        _energy += ((Energy?.Invoke() ?? 0) - _energy) * 0.08f;

        g.Disable(Gl.DepthTest);
        g.Disable(Gl.CullFace);
        g.Enable(Gl.Blend);
        g.BlendFunc(Gl.One, Gl.OneMinusSrcAlpha);
        g.BindVertexArray(_vao);
        if (_picture != 0)
        {
            g.UseProgram(_pictureProgram);
            g.Uniform3(_uFrame, width, height, _pictureRatio);
            g.BindTexture(_picture);
            g.Uniform1(_uPicture, 0);
            g.DrawArrays(Gl.TriangleStrip, 0, 4);
        }
        g.UseProgram(_program);
        g.Uniform2(_uSize, width, height);
        g.Uniform1(_uTime, (float)(_clock.Elapsed.TotalSeconds % 3600));
        if (_uEnergy >= 0)
            g.Uniform1(_uEnergy, Math.Clamp(_energy, 0, 1));
        g.DrawArrays(Gl.TriangleStrip, 0, 4);
        g.BindVertexArray(0);
        g.UseProgram(0);
        ScheduleNextFrame();
    }

    /// <summary>
    /// The next frame, 30 times a second at most: asked at the end of each frame without a pause, the animation was
    /// drawn (and the whole window composed again) at the rate of the screen, even with nothing to show.
    /// </summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);
    private Action? _nextFrame;

    private void ScheduleNextFrame()
    {
        _nextFrame ??= () => Avalonia.Threading.DispatcherTimer.RunOnce(RequestNextFrameRendering, FrameInterval);
        Avalonia.Threading.Dispatcher.UIThread.Post(_nextFrame);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl is not { } g)
            return;
        if (_program != 0)
            g.DeleteProgram(_program);
        if (_pictureProgram != 0)
            g.DeleteProgram(_pictureProgram);
        if (_quad != 0)
            g.DeleteBuffer(_quad);
        if (_vao != 0)
            g.DeleteVertexArray(_vao);
        if (_picture != 0)
            g.DeleteTexture(_picture);
        _program = _pictureProgram = _quad = _vao = _picture = 0;
        _gl = null;
    }
}
