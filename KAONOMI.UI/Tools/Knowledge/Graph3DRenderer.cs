using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using KAONOMI.Chat.Services;
using KAONOMI.UI.Avatar;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>A shape of the 3D scene: its mesh, where it is, its colour (alpha below 1: translucent), its glow and its rim (the glass of a cluster).</summary>
public sealed record Solid(Mesh3D Mesh, Matrix4x4 Model, Vector4 Color, float Emission, float Rim = 0);

/// <summary>What the 3D relation graph draws: its shapes, and its lines (x, y, z, r, g, b, a per end).</summary>
public sealed record Scene3D(IReadOnlyList<Solid> Solids, float[] Lines);

/// <summary>The camera and the light of a frame.</summary>
public readonly record struct View3D(Matrix4x4 View, Matrix4x4 Projection, Vector3 Camera, Vector3 Background, Vector3 Ambient, Vector3 Fill, float FogDensity);

/// <summary>
/// Draws the 3D relation graph with OpenGL, in the manner of the Godot scene: the shapes lit by a sun from the upper
/// left and a fill light in the accent colour from behind, over an ambient light, with a little gloss and their own glow
/// (the selected element and its neighbours glow more); the lines unlit; a fog that fades what is far into the
/// background. The opaque shapes first, then the lines, then the translucent ones (the arrows fading into the past, the
/// blobs of the clusters) from the farthest to the nearest, without writing the depth. It draws a frame only when asked
/// (<see cref="OpenGlControlBase.RequestNextFrameRendering"/>), and can hand back the pixels of one for a capture.
/// </summary>
public sealed class Graph3DRenderer : OpenGlControlBase
{
    private const string SolidVertex = """
        in vec3 aPos;
        in vec3 aNormal;
        uniform mat4 uModel;
        uniform mat4 uViewProj;
        out vec3 vWorld;
        out vec3 vNormal;
        void main() {
            vec4 world = uModel * vec4(aPos, 1.0);
            vWorld = world.xyz;
            vNormal = mat3(uModel) * aNormal;
            gl_Position = uViewProj * world;
        }
        """;

    private const string SolidFragment = """
        in vec3 vWorld;
        in vec3 vNormal;
        out vec4 fragColor;
        uniform vec4 uColor;
        uniform float uEmission;
        uniform float uRim;
        uniform vec3 uCamera;
        uniform vec3 uSun;
        uniform vec3 uFillDir;
        uniform vec3 uFill;
        uniform vec3 uAmbient;
        uniform vec3 uFog;
        uniform float uFogDensity;
        void main() {
            vec3 n = normalize(vNormal);
            vec3 v = normalize(uCamera - vWorld);
            if (dot(n, v) < 0.0)
                n = -n;
            vec3 albedo = uColor.rgb;
            vec3 light = uAmbient + vec3(1.1) * max(dot(n, -uSun), 0.0) + uFill * 0.35 * max(dot(n, -uFillDir), 0.0);
            float gloss = pow(max(dot(n, normalize(v - uSun)), 0.0), 28.0) * 0.28;
            vec3 color = albedo * light * 0.82 + vec3(gloss) + albedo * uEmission;
            float rim = uRim * pow(1.0 - max(dot(n, v), 0.0), 2.0);
            color += rim * mix(vec3(1.0), albedo, 0.7);
            float alpha = clamp(uColor.a + rim * 0.5, 0.0, 1.0);
            float fog = 1.0 - exp(-uFogDensity * length(uCamera - vWorld));
            color = mix(color, uFog, clamp(fog, 0.0, 1.0));
            fragColor = vec4(color * alpha, alpha);
        }
        """;

    private const string LineVertex = """
        in vec3 aPos;
        in vec4 aColor;
        uniform mat4 uViewProj;
        out vec4 vColor;
        out vec3 vWorld;
        void main() {
            vColor = aColor;
            vWorld = aPos;
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    private const string LineFragment = """
        in vec4 vColor;
        in vec3 vWorld;
        out vec4 fragColor;
        uniform vec3 uCamera;
        uniform vec3 uFog;
        uniform float uFogDensity;
        void main() {
            float fog = clamp(1.0 - exp(-uFogDensity * length(uCamera - vWorld)), 0.0, 1.0);
            vec3 color = mix(vColor.rgb, uFog, fog);
            fragColor = vec4(color * vColor.a, vColor.a);
        }
        """;

    private sealed record GpuMesh(int Vao, int Buffer, int Count);

    private readonly Dictionary<Mesh3D, GpuMesh> _meshes = [];
    private Gl? _gl;
    private int _solidProgram, _lineProgram, _lineVao, _lineBuffer;
    private int _uModel, _uViewProj, _uColor, _uEmission, _uRim, _uCamera, _uSun, _uFillDir, _uFill, _uAmbient, _uFog, _uFogDensity;
    private int _lViewProj, _lCamera, _lFog, _lFogDensity;
    private float[]? _uploadedLines;
    private Action<byte[], int, int>? _capture;

    public Graph3DRenderer()
    {
        IsHitTestVisible = false;
    }

    /// <summary>What is drawn (read at each frame).</summary>
    public Scene3D? Scene { get; set; }

    /// <summary>The camera and the light of the frame (asked at each frame, with the size in pixels).</summary>
    public Func<int, int, View3D>? Camera { get; set; }

    /// <summary>The direction of the sun (from the light to the scene).</summary>
    public static readonly Vector3 SunDirection = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, Mesh3D.Turn(-50, 35, 0)));

    /// <summary>The direction of the fill light.</summary>
    public static readonly Vector3 FillDirection = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, Mesh3D.Turn(30, -140, 0)));

    /// <summary>Why nothing is drawn, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>The next frame drawn is handed to <paramref name="done"/> (RGBA, bottom row first, its width and height in pixels).</summary>
    public void Capture(Action<byte[], int, int> done)
    {
        _capture = done;
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            var g = _gl = new Gl(gl);
            _solidProgram = g.Program(g.Header + SolidVertex, g.Header + SolidFragment, "aPos", "aNormal");
            foreach (var (name, set) in new (string, Action<int>)[]
                     {
                         ("uModel", v => _uModel = v), ("uViewProj", v => _uViewProj = v), ("uColor", v => _uColor = v), ("uEmission", v => _uEmission = v),
                         ("uRim", v => _uRim = v), ("uCamera", v => _uCamera = v), ("uSun", v => _uSun = v), ("uFillDir", v => _uFillDir = v),
                         ("uFill", v => _uFill = v), ("uAmbient", v => _uAmbient = v), ("uFog", v => _uFog = v), ("uFogDensity", v => _uFogDensity = v)
                     })
                set(g.Uniform(_solidProgram, name));
            _lineProgram = g.Program(g.Header + LineVertex, g.Header + LineFragment, "aPos", "aColor");
            _lViewProj = g.Uniform(_lineProgram, "uViewProj");
            _lCamera = g.Uniform(_lineProgram, "uCamera");
            _lFog = g.Uniform(_lineProgram, "uFog");
            _lFogDensity = g.Uniform(_lineProgram, "uFogDensity");
            _lineVao = g.GenVertexArray();
            _lineBuffer = g.GenBuffer();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Error = ex.Message;
            ActivityLog.Error("Relation", $"The 3D graph cannot be drawn: {ex.Message}");
        }
    }

    private GpuMesh Upload(Gl g, Mesh3D mesh)
    {
        if (_meshes.TryGetValue(mesh, out var gpu))
            return gpu;
        var vao = g.GenVertexArray();
        g.BindVertexArray(vao);
        var buffer = g.GenBuffer();
        g.BindBuffer(Gl.ArrayBuffer, buffer);
        g.BufferData(Gl.ArrayBuffer, mesh.Data);
        g.VertexAttribPointer(0, 3, 24, 0);
        g.VertexAttribPointer(1, 3, 24, 12);
        g.BindVertexArray(0);
        return _meshes[mesh] = new GpuMesh(vao, buffer, mesh.Count);
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
        var view = Camera?.Invoke(width, height) ?? default;
        g.Clear(view.Background.X, view.Background.Y, view.Background.Z, 1, depth: true);
        if (Error is not null || _solidProgram == 0 || Scene is not { } scene)
        {
            Captured(g, width, height);
            return;
        }
        var viewProjection = view.View * view.Projection;

        // The meshes of the scene drawn before and not any more are freed.
        var used = scene.Solids.Select(s => s.Mesh).ToHashSet();
        foreach (var gone in _meshes.Keys.Where(m => !used.Contains(m)).ToList())
        {
            g.DeleteBuffer(_meshes[gone].Buffer);
            g.DeleteVertexArray(_meshes[gone].Vao);
            _meshes.Remove(gone);
        }

        g.Enable(Gl.DepthTest);
        g.DepthFunc(Gl.LessEqual);
        g.Disable(Gl.CullFace);
        g.Enable(Gl.Blend);
        g.BlendFunc(Gl.One, Gl.OneMinusSrcAlpha);
        g.DepthMask(true);

        g.UseProgram(_solidProgram);
        g.UniformMatrix4(_uViewProj, viewProjection);
        g.Uniform3(_uCamera, view.Camera.X, view.Camera.Y, view.Camera.Z);
        g.Uniform3(_uSun, SunDirection.X, SunDirection.Y, SunDirection.Z);
        g.Uniform3(_uFillDir, FillDirection.X, FillDirection.Y, FillDirection.Z);
        g.Uniform3(_uFill, view.Fill.X, view.Fill.Y, view.Fill.Z);
        g.Uniform3(_uAmbient, view.Ambient.X, view.Ambient.Y, view.Ambient.Z);
        g.Uniform3(_uFog, view.Background.X, view.Background.Y, view.Background.Z);
        g.Uniform1(_uFogDensity, view.FogDensity);
        void Draw(Solid solid)
        {
            var mesh = Upload(g, solid.Mesh);
            g.UniformMatrix4(_uModel, solid.Model);
            g.Uniform4(_uColor, solid.Color.X, solid.Color.Y, solid.Color.Z, solid.Color.W);
            g.Uniform1(_uEmission, solid.Emission);
            g.Uniform1(_uRim, solid.Rim);
            g.BindVertexArray(mesh.Vao);
            g.DrawArrays(Gl.Triangles, 0, mesh.Count);
        }
        foreach (var solid in scene.Solids.Where(s => s.Color.W >= 0.999f))
            Draw(solid);

        // The lines: translucent, over the shapes, behind what is nearer.
        if (scene.Lines.Length > 0)
        {
            g.DepthMask(false);
            g.UseProgram(_lineProgram);
            g.UniformMatrix4(_lViewProj, viewProjection);
            g.Uniform3(_lCamera, view.Camera.X, view.Camera.Y, view.Camera.Z);
            g.Uniform3(_lFog, view.Background.X, view.Background.Y, view.Background.Z);
            g.Uniform1(_lFogDensity, view.FogDensity);
            g.BindVertexArray(_lineVao);
            g.BindBuffer(Gl.ArrayBuffer, _lineBuffer);
            if (!ReferenceEquals(_uploadedLines, scene.Lines))
            {
                g.BufferData(Gl.ArrayBuffer, scene.Lines);
                g.VertexAttribPointer(0, 3, 28, 0);
                g.VertexAttribPointer(1, 4, 28, 12);
                _uploadedLines = scene.Lines;
            }
            g.DrawArrays(Gl.Lines, 0, scene.Lines.Length / 7);
            g.UseProgram(_solidProgram);
        }

        // The translucent shapes, the farthest first, without hiding what is behind them.
        g.DepthMask(false);
        foreach (var solid in scene.Solids.Where(s => s.Color.W < 0.999f)
                     .OrderByDescending(s => Vector3.DistanceSquared(Vector3.Transform(s.Mesh.Centre, s.Model), view.Camera)))
            Draw(solid);
        g.DepthMask(true);
        g.BindVertexArray(0);
        g.UseProgram(0);
        Captured(g, width, height);
    }

    private void Captured(Gl g, int width, int height)
    {
        if (_capture is not { } done)
            return;
        _capture = null;
        done(g.ReadPixels(width, height), width, height);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_gl is not { } g)
            return;
        foreach (var mesh in _meshes.Values)
        {
            g.DeleteBuffer(mesh.Buffer);
            g.DeleteVertexArray(mesh.Vao);
        }
        _meshes.Clear();
        if (_solidProgram != 0)
            g.DeleteProgram(_solidProgram);
        if (_lineProgram != 0)
            g.DeleteProgram(_lineProgram);
        if (_lineBuffer != 0)
            g.DeleteBuffer(_lineBuffer);
        if (_lineVao != 0)
            g.DeleteVertexArray(_lineVao);
        _solidProgram = _lineProgram = _lineBuffer = _lineVao = 0;
        _uploadedLines = null;
        _gl = null;
    }
}
