using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

namespace KAONOMI.Chat.Views;

/// <summary>
/// 3D scene of the Kaonomi band in the spirit of an AI assistant's holographic display (<c>animation: JARVIS</c> in
/// config.yml), drawn by one OpenGL fragment shader that casts a ray per pixel:
/// a reticle facing the viewer (tick marks, dashed rings, amber arcs that grow with the voice, a radar sweep and
/// flickering blocks), two gyroscope rings tilted in 3D with orbiting particles, and a wireframe sphere with a glowing
/// core that pulses with the voice. On wide bands, a voice spectrum of bars spreads on both sides. The voice level is
/// the loudness while the answer is read aloud (<see cref="IsSpeaking"/>, <see cref="VoiceLevel"/>), the streamed
/// words otherwise (<see cref="Pulse"/>). Scan lines and a glitch now and then keep a holographic touch.
/// </summary>
public sealed class JarvisScene : OpenGlControlBase, IBandScene
{
    /// <summary>Background of the scene (the band background); a light one switches the hologram to deep colors.</summary>
    public static readonly StyledProperty<Color> BackColorProperty =
        AvaloniaProperty.Register<JarvisScene, Color>(nameof(BackColor), Color.Parse("#101010"));

    private const double Decay = 0.92;          // word energy kept from one frame to the next (about 60 fps)
    private const float LevelAttack = 0.55f;    // share of the way to a louder level made in one frame
    private const float LevelRelease = 0.12f;   // share of the way to a quieter level made in one frame

    // OpenGL constants (not all exposed by GlInterface).
    private const int GL_ARRAY_BUFFER = 0x8892;
    private const int GL_STATIC_DRAW = 0x88E4;
    private const int GL_FLOAT = 0x1406;
    private const int GL_TRIANGLES = 0x0004;
    private const int GL_COLOR_BUFFER_BIT = 0x4000;
    private const int GL_DEPTH_BUFFER_BIT = 0x0100;
    private const int GL_BLEND = 0x0BE2;
    private const int GL_DEPTH_TEST = 0x0B71;
    private const int GL_ONE = 0x0001;
    private const int GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    private const int GL_VERTEX_SHADER = 0x8B31;
    private const int GL_FRAGMENT_SHADER = 0x8B30;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void BlendFuncProc(int source, int destination);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Uniform3fProc(int location, float x, float y, float z);

    private BlendFuncProc? _blendFunc;
    private Uniform3fProc? _uniform3f;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();
    private int _program, _vertexArray, _buffer;
    private int _frameLocation, _levelLocation, _lightLocation, _glitchLocation;
    private bool _ready;
    private float _energy;
    private float _level;
    private float _glitchRow = 99;   // band of the height shifted sideways, out of sight when there is no glitch
    private float _glitchShift;
    private double _glitchEnd;
    private double _nextGlitch = 3;

    static JarvisScene()
    {
        ClipToBoundsProperty.OverrideDefaultValue<JarvisScene>(true);
        IsHitTestVisibleProperty.OverrideDefaultValue<JarvisScene>(false);
    }

    /// <inheritdoc cref="BackColorProperty" />
    public Color BackColor
    {
        get => GetValue(BackColorProperty);
        set => SetValue(BackColorProperty, value);
    }

    /// <inheritdoc />
    public bool IsSpeaking { get; set; }

    /// <inheritdoc />
    public Func<float>? VoiceLevel { get; set; }

    /// <inheritdoc />
    public void Pulse(string word)
    {
        var strength = Math.Clamp(word.Trim().Length / 7.0, 0.3, 1.0);
        _energy = (float)Math.Min(1.0, _energy + strength * 0.6);
    }

    /// <inheritdoc />
    protected override void OnOpenGlInit(GlInterface gl)
    {
        _blendFunc = Proc<BlendFuncProc>(gl, "glBlendFunc");
        _uniform3f = Proc<Uniform3fProc>(gl, "glUniform3f");
        if (_blendFunc is null || _uniform3f is null)
        {
            Trace.WriteLine("Jarvis scene: OpenGL functions missing, scene off");
            return;
        }

        _program = gl.CreateProgram();
        var vertex = gl.CreateShader(GL_VERTEX_SHADER);
        var fragment = gl.CreateShader(GL_FRAGMENT_SHADER);
        var errors = gl.CompileShaderAndGetError(vertex, Header(GlVersion) + VertexShader)
                     ?? gl.CompileShaderAndGetError(fragment, Header(GlVersion) + FragmentShader);
        if (errors is null)
        {
            gl.AttachShader(_program, vertex);
            gl.AttachShader(_program, fragment);
            gl.BindAttribLocationString(_program, 0, "aPos");
            errors = gl.LinkProgramAndGetError(_program);
        }
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);
        if (errors is not null)
        {
            Trace.WriteLine($"Jarvis scene: shaders not built ({errors})");
            return;
        }

        _frameLocation = gl.GetUniformLocationString(_program, "uFrame");
        _levelLocation = gl.GetUniformLocationString(_program, "uLevel");
        _lightLocation = gl.GetUniformLocationString(_program, "uLight");
        _glitchLocation = gl.GetUniformLocationString(_program, "uGlitch");

        // One quad over the whole control: every pixel casts its own ray in the fragment shader.
        float[] vertices = [-1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1];
        _vertexArray = gl.GenVertexArray();
        gl.BindVertexArray(_vertexArray);
        _buffer = gl.GenBuffer();
        gl.BindBuffer(GL_ARRAY_BUFFER, _buffer);
        var handle = GCHandle.Alloc(vertices, GCHandleType.Pinned);
        try
        {
            gl.BufferData(GL_ARRAY_BUFFER, (IntPtr)(vertices.Length * sizeof(float)), handle.AddrOfPinnedObject(), GL_STATIC_DRAW);
        }
        finally
        {
            handle.Free();
        }
        gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, 2 * sizeof(float), IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        gl.BindVertexArray(0);
        _ready = true;
    }

    /// <inheritdoc />
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_buffer != 0)
            gl.DeleteBuffer(_buffer);
        if (_vertexArray != 0)
            gl.DeleteVertexArray(_vertexArray);
        if (_program != 0)
            gl.DeleteProgram(_program);
        _buffer = _vertexArray = _program = 0;
        _ready = false;
    }

    /// <inheritdoc />
    protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Max(1, (int)(Bounds.Width * scaling));
        var height = Math.Max(1, (int)(Bounds.Height * scaling));
        gl.Viewport(0, 0, width, height);

        var back = BackColor;
        gl.ClearColor(back.R / 255f, back.G / 255f, back.B / 255f, 1);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);

        if (_ready)
        {
            var seconds = _clock.Elapsed.TotalSeconds;

            // Voice level: the loudness while the answer is read aloud, the streamed words otherwise.
            _energy = (float)(_energy * Decay);
            var target = IsSpeaking ? Math.Clamp(VoiceLevel?.Invoke() ?? 0, 0, 1) : _energy;
            _level += (target - _level) * (target > _level ? LevelAttack : LevelRelease);

            // A thin band of the picture shifts sideways for a fraction of a second, every few seconds.
            if (seconds > _nextGlitch)
            {
                _glitchRow = (float)_random.NextDouble();
                _glitchShift = (float)((_random.NextDouble() - 0.5) * 24 * scaling);
                _glitchEnd = seconds + 0.08;
                _nextGlitch = seconds + 5 + _random.NextDouble() * 9;
            }
            else if (seconds > _glitchEnd)
            {
                _glitchRow = 99;
            }

            // Relative luminance of the background: a light band gets deep colors drawn over it instead of glow.
            var light = (0.2126 * back.R + 0.7152 * back.G + 0.0722 * back.B) / 255 > 0.5 ? 1f : 0f;

            gl.UseProgram(_program);
            gl.BindVertexArray(_vertexArray);
            gl.Disable(GL_DEPTH_TEST);
            gl.Enable(GL_BLEND);
            _blendFunc!(GL_ONE, GL_ONE_MINUS_SRC_ALPHA);   // premultiplied alpha over the band

            // Wrapped so that the float time keeps its precision when the chat stays open for days.
            _uniform3f!(_frameLocation, width, height, (float)(seconds % 3600));
            _uniform3f!(_glitchLocation, _glitchRow, _glitchShift, 0);
            gl.Uniform1f(_levelLocation, Math.Clamp(_level, 0, 1));
            gl.Uniform1f(_lightLocation, light);
            gl.DrawArrays(GL_TRIANGLES, 0, (IntPtr)6);

            gl.BindVertexArray(0);
            gl.Disable(GL_BLEND);
        }

        // Continuous animation while the band is shown.
        RequestNextFrameRendering();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BackColorProperty)
            Dispatcher.UIThread.Post(RequestNextFrameRendering);
    }

    private static T? Proc<T>(GlInterface gl, string name) where T : Delegate =>
        gl.GetProcAddress(name) is var address && address != IntPtr.Zero
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    /// <summary>GLSL version line for the context: OpenGL ES 3 (Windows ANGLE) or desktop OpenGL 3.2+ (macOS, Linux).</summary>
    private static string Header(GlVersion version) => version.Type == GlProfileType.OpenGLES
        ? "#version 300 es\nprecision highp float;\n"
        : "#version 150\n";

    private const string VertexShader = """
        in vec2 aPos;

        void main()
        {
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    // Camera on the z axis looking at the origin; the hologram is centred on the origin, the reticle in the plane z = 0
    // facing the camera, the gyroscope rings in tilted planes through the origin, the sphere at the origin. Sizes are
    // in scene units: with CAMERA / FOCAL = 2, a radius of 0.92 fills 0.46 of the band height.
    private const string FragmentShader = """
        uniform vec3 uFrame;      // width, height (pixels), time (seconds)
        uniform float uLevel;     // voice level, 0 to 1
        uniform float uLight;     // 1 when the band background is light
        uniform vec3 uGlitch;     // row (share of the height), sideways shift (pixels)
        out vec4 fragColor;

        const float PI = 3.14159265;
        const float TAU = 6.28318531;
        const float CAMERA = 3.4;
        const float FOCAL = 1.7;

        vec3 gColor;   // sum of the colors of the elements crossed by the ray
        float gGlow;   // sum of their intensities

        void add(vec3 color, float amount)
        {
            gColor += color * amount;
            gGlow += amount;
        }

        float hash(float n) { return fract(sin(n) * 43758.5453); }

        // 1 on a line at distance 0, fading to 0 at distance w.
        float lineAt(float d, float w) { return 1.0 - smoothstep(0.0, w, abs(d)); }

        // 1 between a and b, with soft edges of width w.
        float bandAt(float x, float a, float b, float w) { return smoothstep(a - w, a, x) * (1.0 - smoothstep(b, b + w, x)); }

        // n radial marks per turn (a = angle in turns), turned by spin: 1 on a mark.
        float marks(float a, float r, float n, float spin, float w)
        {
            float f = fract(a * n + spin);
            return lineAt(min(f, 1.0 - f) / n * TAU * r, w);
        }

        mat3 rotX(float a) { float c = cos(a); float s = sin(a); return mat3(1.0, 0.0, 0.0, 0.0, c, s, 0.0, -s, c); }
        mat3 rotY(float a) { float c = cos(a); float s = sin(a); return mat3(c, 0.0, -s, 0.0, 1.0, 0.0, s, 0.0, c); }
        mat3 rotZ(float a) { float c = cos(a); float s = sin(a); return mat3(c, s, 0.0, -s, c, 0.0, 0.0, 0.0, 1.0); }

        // Where the ray crosses the plane z = 0 of a layer turned by m (p in the layer, t along the ray).
        bool plane(vec3 ro, vec3 rd, mat3 m, out vec2 p, out float t)
        {
            vec3 o = ro * m;   // transpose(m) * ro: into the layer
            vec3 d = rd * m;
            p = vec2(0.0);
            t = 0.0;
            if (abs(d.z) < 0.0001)
                return false;
            t = -o.z / d.z;
            p = o.xy + t * d.xy;
            return t > 0.0;
        }

        // A gyroscope ring tilted by m: a thin ring, dashes and three particles orbiting on it.
        void gyroscope(vec3 ro, vec3 rd, mat3 m, float radius, float speed, vec3 color, float w)
        {
            vec2 p;
            float t;
            if (!plane(ro, rd, m, p, t))
                return;
            float fade = clamp(1.3 - 0.6 * t / CAMERA, 0.35, 1.0);   // the far side of the ring is dimmer
            float r = length(p);
            float a = atan(p.y, p.x) / TAU + 0.5;
            add(color, fade * 0.55 * lineAt(r - radius, w));
            add(color, fade * 0.3 * lineAt(r - radius - 0.035, w) * step(0.5, fract(a * 24.0 + uFrame.z * speed * 0.2)));
            for (int i = 0; i < 3; i++)
            {
                float angle = uFrame.z * speed + float(i) * TAU / 3.0;
                float d = length(p - radius * vec2(cos(angle), sin(angle)));
                add(color, fade * 0.0009 / (d * d + 0.0006));
            }
        }

        void main()
        {
            vec2 res = uFrame.xy;
            float time = uFrame.z;
            vec2 frag = gl_FragCoord.xy;
            if (abs(frag.y / res.y - uGlitch.x) < 0.012)
                frag.x += uGlitch.y;

            float unit = min(res.x, res.y);
            vec2 uv = (frag - 0.5 * res) / unit;
            float px = 1.0 / unit;
            float w = 1.6 * px * CAMERA / FOCAL;   // about 1.6 pixel in the plane of the origin
            float level = uLevel;

            vec3 cyan = mix(vec3(0.30, 0.82, 1.00), vec3(0.00, 0.42, 0.72), uLight);
            vec3 ice = mix(vec3(0.75, 0.95, 1.00), vec3(0.00, 0.30, 0.55), uLight);
            vec3 amber = mix(vec3(1.00, 0.62, 0.20), vec3(0.85, 0.40, 0.00), uLight);
            gColor = vec3(0.0);
            gGlow = 0.0;

            vec3 ro = vec3(0.0, 0.0, CAMERA);
            vec3 rd = normalize(vec3(uv, -FOCAL));
            // The whole hologram sways slightly, as if it floated.
            mat3 sway = rotY(0.14 * sin(time * 0.31)) * rotX(0.07 * sin(time * 0.23));

            // Faint dot grid behind the hologram.
            vec2 cell = fract(frag / (unit * 0.055)) - 0.5;
            add(cyan, 0.07 * lineAt(length(cell), 0.06));

            // Reticle facing the viewer.
            vec2 p;
            float t;
            if (plane(ro, rd, sway, p, t))
            {
                float r = length(p);
                float a = atan(p.y, p.x) / TAU + 0.5;   // angle in turns
                add(cyan, 0.5 * lineAt(r - 0.80, w));
                add(cyan, 0.45 * marks(a, r, 90.0, time * 0.01, w) * bandAt(r, 0.72, 0.76, w));
                add(cyan, 0.7 * marks(a, r, 12.0, time * 0.01, 1.5 * w) * bandAt(r, 0.69, 0.78, w));
                add(ice, 0.8 * marks(a, r, 4.0, 0.0, 1.5 * w) * bandAt(r, 0.84, 0.92, w));
                add(cyan, 0.4 * lineAt(r - 0.66, 1.4 * w) * step(0.35, fract(a * 36.0 - time * 0.08)));

                // Amber arcs: three of them, longer with the voice.
                float arc = fract(a * 3.0 + time * 0.05);
                add(amber, 0.85 * bandAt(r, 0.585, 0.615, w) * bandAt(arc, 0.0, 0.10 + 0.20 * level, 0.004));
                add(amber, 0.35 * lineAt(r - 0.63, w) * bandAt(fract(a * 3.0 - time * 0.03 + 0.5), 0.0, 0.25, 0.004));

                // Inner double ring, flickering blocks and a radar sweep.
                add(cyan, 0.5 * (lineAt(r - 0.48, w) + lineAt(r - 0.465, w)));
                float block = floor(a * 60.0);
                add(ice, bandAt(r, 0.385, 0.405, w) * step(0.3, fract(a * 60.0))
                         * (0.15 + 0.6 * level * step(0.4, hash(block + floor(time * 10.0)))));
                add(cyan, 0.35 * pow(fract(a - time * 0.25), 6.0) * bandAt(r, 0.30, 0.46, w));
            }

            // Two gyroscope rings tilted in 3D around the core.
            gyroscope(ro, rd, sway * rotY(time * 0.35) * rotX(1.25), 0.34, 1.1, ice, w);
            gyroscope(ro, rd, sway * rotZ(-0.6) * rotX(time * 0.27) * rotY(1.15), 0.30, -0.8, cyan, w);

            // Wireframe sphere around the core.
            float radius = 0.17 + 0.012 * level;
            float b = dot(ro, rd);
            float h = b * b - dot(ro, ro) + radius * radius;
            if (h > 0.0)
            {
                vec3 n = (ro + (-b - sqrt(h)) * rd) / radius;
                vec3 q = n * rotY(time * 0.6) * rotX(0.4);
                float lat = acos(clamp(q.y, -1.0, 1.0)) / PI;
                float lon = atan(q.z, q.x) / TAU + 0.5;
                float sw = 1.4 * px / radius;
                float grid = lineAt((fract(lat * 7.0 + 0.5) - 0.5) / 7.0 * PI, sw)
                             + lineAt((fract(lon * 12.0 + 0.5) - 0.5) / 12.0 * TAU * sin(lat * PI), sw);
                float rim = pow(1.0 - abs(dot(n, rd)), 3.0);
                add(cyan, 0.3 * min(grid, 1.0) + 0.5 * rim);
            }

            // Glowing core, pulsing with the voice.
            float d = length(cross(rd, -ro));
            float pulse = 0.5 + 0.9 * level + 0.06 * sin(time * 3.0);
            add(ice, 0.0028 * pulse / (d * d + 0.0012));

            // Voice spectrum on both sides, when the band is wide enough.
            float halfWidth = 0.5 * res.x / unit;
            float start = 0.56;
            float span = halfWidth - start - 0.06;
            float ax = abs(uv.x) - start;
            if (span > 0.15 && ax > 0.0 && ax < span)
            {
                float barStep = 0.022;
                float i = floor(ax / barStep);
                float side = uv.x < 0.0 ? 57.0 : 0.0;
                float wave = 0.5 + 0.5 * sin(i * 0.9 + time * 7.0 + hash(i + side) * TAU);
                float envelope = 1.0 - ax / span;
                float barHeight = 0.015 + (0.04 + 0.34 * level) * wave * envelope;
                float bar = step(fract(ax / barStep), 0.55) * (1.0 - smoothstep(barHeight, barHeight + px, abs(uv.y)));
                add(cyan, 0.5 * bar * (0.6 + 0.4 * envelope));
                add(cyan, 0.25 * lineAt(uv.y, px) * envelope);
                // Brackets above and below the spectrum.
                add(cyan, 0.3 * lineAt(abs(uv.y) - 0.42, px) * step(ax, 0.25));
            }

            // Scan lines.
            float scan = 0.9 + 0.1 * sin(frag.y * 2.0);
            gColor *= scan;
            gGlow *= scan;

            // Dark band: the hologram glows (added light); light band: deep colors are painted over it.
            vec3 glowColor = min(gColor, vec3(1.0));
            float glowAlpha = clamp(max(gColor.r, max(gColor.g, gColor.b)), 0.0, 1.0);
            float paintAlpha = clamp(gGlow, 0.0, 1.0);
            vec3 paintColor = gColor / max(gGlow, 0.0001) * paintAlpha;
            fragColor = mix(vec4(glowColor, glowAlpha), vec4(paintColor, paintAlpha), uLight);
        }
        """;
}
