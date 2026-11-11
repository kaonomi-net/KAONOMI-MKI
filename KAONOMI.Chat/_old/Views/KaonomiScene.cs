using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Views;

/// <summary>
/// 3D scene of the Kaonomi band, drawn with OpenGL shaders: a face image mapped on a mesh pushed into relief along the
/// face and lit from the side, so that it has volume while the head sways. The mouth, closed in the image, opens with
/// the loudness of the voice while the answer is read aloud (<see cref="IsSpeaking"/>, <see cref="VoiceLevel"/>), and
/// with the streamed words otherwise (<see cref="Pulse"/>): the chin drops and the inside of the mouth shows.
/// <see cref="Animation"/> picks the face: MARI, the Kaonomi robot (Assets/kaonomi-face.png: face oval, nose, cheeks,
/// headphones, hair, neck; faint scan lines and a glitch now and then for a holographic touch), or JEROME, a photo
/// portrait (Assets/jerome-face.png, cut out of its background: face, nose, brows, cheeks, ears, bearded chin, hair,
/// shoulders fading out at the edges of the frame), or HAL, the red eye of a computer (Assets/hal-eye.png: a glass
/// dome over the iris, a raised bezel and housing, cut out in a circle): it has no mouth, its iris dilates and its red
/// core, halo and horizontal flare glow brighter with the voice, and breathe slowly when it is quiet; or MARIEKILLEUSE,
/// a cute-horror robot doll holding a bloody knife (Assets/mariekilleuse-face.png, cut out of its white background):
/// her robot chin plate drops like a ventriloquist doll's, her spiral pupils spin (faster while she speaks), the hearts
/// of her chest glow pink with the voice, a glint runs along the blade (which stands out of the picture) and a red
/// glitch shakes her now and then; or MUSUKO, a robot boy (Assets/musuko-face.png, cut out of his scene): his mouth
/// opens with the voice, he blinks every few seconds, and his green irises and the lights of his chest glow with the voice;
/// or KAONAMI, a worn white robot with a mask face (Assets/kaonami-face.png, cut out of its grey background): the chin
/// under its mouth slot drops with the voice and an amber light shines in its eye holes and its mouth while it speaks;
/// or PEPPER, a white humanoid robot with a tablet (Assets/pepper-face.png, cut out of its white background): its mouth
/// opens with the voice, the rings of its eyes and its ear light up in blue while it speaks, and a voice wave runs on its
/// tablet; or GREEN, a pale android child with glowing green eyes (Assets/green-face.png, cut out of its dark background):
/// its mouth opens with the voice, its pupils dilate and its eyes glow brighter with the voice, it blinks every few
/// seconds, its green ears and shoulders breathe, and a green scan line runs down now and then; or JEROMELIVE, Jerome
/// filmed (Assets/jeromelive-face.png: one frame of his video, cut out): his jaw drops with the voice and shows the
/// inside of his mouth, and he blinks every few seconds; or MARVIN, the gloomy robot (Assets/marvin-face.png, cut out of
/// its white background): no mouth, his green eye slits glow with the voice and breathe slowly when he is quiet, a green
/// light runs along his visor while he speaks, and his eyes go dark for a moment now and then.
/// </summary>
public sealed class KaonomiScene : OpenGlControlBase, IBandScene
{
    /// <summary>Background of the scene (the band background).</summary>
    public static readonly StyledProperty<Color> BackColorProperty =
        AvaloniaProperty.Register<KaonomiScene, Color>(nameof(BackColor), Color.Parse("#101010"));


    // Mesh: a grid over the image (texture coordinates 0..1, y down), shown as a 2 x 2 square in the scene.
    private const int GridSize = 192;
    private const float FieldOfView = MathF.PI / 4;
    private const float CameraDistance = 2.55f;

    private const double Decay = 0.92;          // mouth opening kept from one frame to the next (about 60 fps)
    private const float MouthAttack = 0.55f;    // share of the way to a louder level made in one frame (opens fast)
    private const float MouthRelease = 0.2f;    // share of the way to a quieter level made in one frame (closes softly)

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
    private const int GL_TEXTURE_2D = 0x0DE1;
    private const int GL_TEXTURE0 = 0x84C0;
    private const int GL_TEXTURE_MIN_FILTER = 0x2801;
    private const int GL_TEXTURE_MAG_FILTER = 0x2800;
    private const int GL_TEXTURE_WRAP_S = 0x2802;
    private const int GL_TEXTURE_WRAP_T = 0x2803;
    private const int GL_LINEAR = 0x2601;
    private const int GL_LINEAR_MIPMAP_LINEAR = 0x2703;
    private const int GL_CLAMP_TO_EDGE = 0x812F;
    private const int GL_RGBA = 0x1908;
    private const int GL_UNSIGNED_BYTE = 0x1401;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void BlendFuncProc(int source, int destination);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void GenerateMipmapProc(int target);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Uniform3fProc(int location, float x, float y, float z);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void UniformMatrix4fvProc(int location, int count, bool transpose, float[] value);

    private BlendFuncProc? _blendFunc;
    private Uniform3fProc? _uniform3f;
    private UniformMatrix4fvProc? _uniformMatrix4fv;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random = new();
    private int _program, _vertexArray, _buffer, _texture;
    private int _mvpLocation, _timeLocation, _openLocation, _glitchLocation, _textureLocation;
    private bool _ready;
    private float _energy;
    private float _mouth;
    private float _glitchRow = 99;   // band of the image (texture y) shifted sideways, out of sight when there is no glitch
    private float _glitchShift;
    private double _glitchEnd;
    private double _nextGlitch = 3;

    static KaonomiScene()
    {
        ClipToBoundsProperty.OverrideDefaultValue<KaonomiScene>(true);
        IsHitTestVisibleProperty.OverrideDefaultValue<KaonomiScene>(false);
    }

    /// <summary>
    /// Face shown (MARI, JEROME, HAL, MARIEKILLEUSE, MUSUKO, KAONAMI, PEPPER, GREEN, JEROMELIVE or MARVIN; set before the scene is first drawn, e.g. in XAML). Any other value shows MARI.
    /// </summary>
    public SceneAnimation Animation { get; set; } = SceneAnimation.Mari;

    private FaceModel Face => Animation switch
    {
        SceneAnimation.Jerome => JeromeFace,
        SceneAnimation.Hal => HalFace,
        SceneAnimation.MarieKilleuse => MarieKilleuseFace,
        SceneAnimation.Musuko => MusukoFace,
        SceneAnimation.Kaonami => KaonamiFace,
        SceneAnimation.Pepper => PepperFace,
        SceneAnimation.Green => GreenFace,
        SceneAnimation.JeromeLive => JeromeLiveFace,
        SceneAnimation.Marvin => MarvinFace,
        _ => MariFace
    };

    /// <inheritdoc cref="BackColorProperty" />
    public Color BackColor
    {
        get => GetValue(BackColorProperty);
        set => SetValue(BackColorProperty, value);
    }

    /// <summary>True while the answer is read aloud: the jaw follows <see cref="VoiceLevel"/>.</summary>
    public bool IsSpeaking { get; set; }

    /// <summary>Loudness of the voice right now (0 to 1), read on every frame from the render thread.</summary>
    public Func<float>? VoiceLevel { get; set; }

    /// <summary>A streamed word: the mouth opens, wider for longer words.</summary>
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
        _uniformMatrix4fv = Proc<UniformMatrix4fvProc>(gl, "glUniformMatrix4fv");
        if (_blendFunc is null || _uniform3f is null || _uniformMatrix4fv is null)
        {
            Trace.WriteLine("Kaonomi scene: OpenGL functions missing, scene off");
            return;
        }

        _program = gl.CreateProgram();
        var vertex = gl.CreateShader(GL_VERTEX_SHADER);
        var fragment = gl.CreateShader(GL_FRAGMENT_SHADER);
        // Shared shader code, with the relief, jaw and mouth of the face (and its style) in between.
        var face = Header(GlVersion) + Face.Glsl + ShapeFunctions + Face.Shape;
        var errors = gl.CompileShaderAndGetError(vertex, face + VertexShader)
                     ?? gl.CompileShaderAndGetError(fragment, face + FragmentShader);
        if (errors is null)
        {
            gl.AttachShader(_program, vertex);
            gl.AttachShader(_program, fragment);
            gl.BindAttribLocationString(_program, 0, "aUv");
            errors = gl.LinkProgramAndGetError(_program);
        }
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);
        if (errors is not null)
        {
            Trace.WriteLine($"Kaonomi scene: shaders not built ({errors})");
            return;
        }

        _mvpLocation = gl.GetUniformLocationString(_program, "uMvp");
        _timeLocation = gl.GetUniformLocationString(_program, "uTime");
        _openLocation = gl.GetUniformLocationString(_program, "uOpen");
        _glitchLocation = gl.GetUniformLocationString(_program, "uGlitch");
        _textureLocation = gl.GetUniformLocationString(_program, "uFace");

        _texture = LoadTexture(gl, Face.Image);

        // Two triangles per grid cell, texture coordinates only (the relief is computed by the shader).
        var vertices = BuildVertices();
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
        _ready = _texture != 0;
    }

    /// <summary>Uploads the face image (premultiplied RGBA, with mipmaps when available); 0 when it cannot be read.</summary>
    private static int LoadTexture(GlInterface gl, string image)
    {
        byte[] pixels;
        int width, height;
        try
        {
            using var bitmap = new Bitmap(AssetLoader.Open(new Uri(image)));
            width = bitmap.PixelSize.Width;
            height = bitmap.PixelSize.Height;
            pixels = new byte[width * height * 4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, width * 4);
            }
            finally
            {
                handle.Free();
            }

            // OpenGL ES has no BGRA upload: the pixels are turned into premultiplied RGBA here.
            var bgra = bitmap.Format is not { } format || format == PixelFormats.Bgra8888;
            var premultiplied = bitmap.AlphaFormat is not AlphaFormat.Unpremul;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (bgra)
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                if (!premultiplied)
                {
                    var alpha = pixels[i + 3];
                    pixels[i] = (byte)(pixels[i] * alpha / 255);
                    pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
                    pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Kaonomi scene: {image} not loaded ({ex.Message})");
            return 0;
        }

        var texture = gl.GenTexture();
        gl.ActiveTexture(GL_TEXTURE0);
        gl.BindTexture(GL_TEXTURE_2D, texture);
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, pinned.AddrOfPinnedObject());
        }
        finally
        {
            pinned.Free();
        }

        var generateMipmap = Proc<GenerateMipmapProc>(gl, "glGenerateMipmap");
        generateMipmap?.Invoke(GL_TEXTURE_2D);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, generateMipmap is null ? GL_LINEAR : GL_LINEAR_MIPMAP_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        return texture;
    }

    /// <inheritdoc />
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (_texture != 0)
            gl.DeleteTexture(_texture);
        if (_buffer != 0)
            gl.DeleteBuffer(_buffer);
        if (_vertexArray != 0)
            gl.DeleteVertexArray(_vertexArray);
        if (_program != 0)
            gl.DeleteProgram(_program);
        _texture = _buffer = _vertexArray = _program = 0;
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
            var time = (float)seconds;

            // Mouth: follows the loudness of the voice while the answer is read aloud, opened by the words otherwise.
            _energy = (float)(_energy * Decay);
            var voice = IsSpeaking ? Math.Clamp(VoiceLevel?.Invoke() ?? 0, 0, 1) : 0;
            _mouth += (voice - _mouth) * (voice > _mouth ? MouthAttack : MouthRelease);
            var open = IsSpeaking ? _mouth : _energy;

            // A thin band of the image shifts sideways for a fraction of a second, every few seconds.
            if (seconds > _nextGlitch)
            {
                _glitchRow = (float)_random.NextDouble();
                _glitchShift = (float)((_random.NextDouble() - 0.5) * 0.04);
                _glitchEnd = seconds + 0.1;
                _nextGlitch = seconds + 4 + _random.NextDouble() * 8;
            }
            else if (seconds > _glitchEnd)
            {
                _glitchRow = 99;
            }

            gl.UseProgram(_program);
            gl.BindVertexArray(_vertexArray);
            gl.ActiveTexture(GL_TEXTURE0);
            gl.BindTexture(GL_TEXTURE_2D, _texture);
            gl.Uniform1i(_textureLocation, 0);
            gl.Disable(GL_DEPTH_TEST);
            gl.Enable(GL_BLEND);
            _blendFunc!(GL_ONE, GL_ONE_MINUS_SRC_ALPHA);   // premultiplied alpha: the cut-out face over the band

            _uniform3f!(_glitchLocation, _glitchRow, _glitchShift, 0);
            gl.Uniform1f(_timeLocation, time);
            gl.Uniform1f(_openLocation, Math.Clamp(open, 0, 1));

            // The head sways slowly, as if it looked around.
            var model = Matrix4x4.CreateRotationY(0.2f * MathF.Sin(time * 0.37f))
                        * Matrix4x4.CreateRotationX(0.05f * MathF.Sin(time * 0.29f));
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, CameraDistance), Vector3.Zero, Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, width / (float)height, 0.1f, 20);
            var mvp = model * view * projection;

            // System.Numerics matrices are row-major with row vectors: as stored, they are the column-major
            // matrix GLSL expects for "uMvp * position".
            _uniformMatrix4fv!(_mvpLocation, 1, false,
            [
                mvp.M11, mvp.M12, mvp.M13, mvp.M14,
                mvp.M21, mvp.M22, mvp.M23, mvp.M24,
                mvp.M31, mvp.M32, mvp.M33, mvp.M34,
                mvp.M41, mvp.M42, mvp.M43, mvp.M44
            ]);
            gl.DrawArrays(GL_TRIANGLES, 0, (IntPtr)(GridSize * GridSize * 6));

            gl.BindTexture(GL_TEXTURE_2D, 0);
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

    /// <summary>Two triangles per cell of the grid; 2 floats per vertex (texture coordinates).</summary>
    private static float[] BuildVertices()
    {
        var vertices = new float[GridSize * GridSize * 6 * 2];
        var index = 0;
        for (var row = 0; row < GridSize; row++)
            for (var column = 0; column < GridSize; column++)
            {
                float u0 = column / (float)GridSize, u1 = (column + 1) / (float)GridSize;
                float v0 = row / (float)GridSize, v1 = (row + 1) / (float)GridSize;
                foreach (var (u, v) in new[] { (u0, v0), (u1, v0), (u1, v1), (u0, v0), (u1, v1), (u0, v1) })
                {
                    vertices[index++] = u;
                    vertices[index++] = v;
                }
            }
        return vertices;
    }

    private static T? Proc<T>(GlInterface gl, string name) where T : Delegate =>
        gl.GetProcAddress(name) is var address && address != IntPtr.Zero
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    /// <summary>GLSL version line for the context: OpenGL ES 3 (Windows ANGLE) or desktop OpenGL 3.2+ (macOS, Linux).</summary>
    private static string Header(GlVersion version) => version.Type == GlProfileType.OpenGLES
        ? "#version 300 es\nprecision highp float;\n"
        : "#version 150\n";

    /// <summary>
    /// A face of the scene: its image and the GLSL that shapes it. <see cref="Glsl"/> holds the style (<c>#define</c>)
    /// and the mouth constants; <see cref="Shape"/> defines <c>mouthLine(x)</c> (y of the closed mouth),
    /// <c>relief(p)</c> (height of the mesh) and <c>jaw(p)</c> (share of the jaw drop), in texture coordinates
    /// (x right, y down).
    /// </summary>
    private sealed record FaceModel(string Image, string Glsl, string Shape);

    // Kaonomi robot: face oval centred at (0.5, 0.5), eyes at y 0.445, nose at 0.545, smiling mouth (closed) along
    // y 0.615 at the centre and 0.6 at its corners (x 0.41 to 0.59), chin at 0.75, headphones on the sides (x 0.17 and
    // 0.83), hair from 0.12 to 0.88 across.
    private static readonly FaceModel MariFace = new("avares://KAONOMI.Chat/Assets/kaonomi-face.png", """
        #define HOLOGRAM
        const float MOUTH_X = 0.5;        // centre of the mouth
        const float MOUTH_HALF = 0.085;   // half width of the opening
        const float MOUTH_GAP = 0.012;    // band below the mouth line stretched into the opening
        const float JAW_DROP = 0.06;      // drop of the chin, mouth wide open
        const vec3 MOUTH_DARK = vec3(0.32, 0.06, 0.13);
        const vec3 MOUTH_LIGHT = vec3(0.85, 0.36, 0.45);   // the tongue

        """, """
        // The line of the closed mouth (a smile: its corners are higher).
        float mouthLine(float x)
        {
            float d = (x - 0.5) / 0.09;
            return 0.615 - 0.015 * d * d;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.5);
            float face = 0.34 * sqrt(oval(p, vec2(0.5, 0.5), vec2(0.27, 0.28)));
            face += 0.05 * bump(p, vec2(0.5, 0.545), vec2(0.035, 0.03));             // small nose
            face += 0.02 * bump(p, vec2(0.5, 0.5), vec2(0.025, 0.05));               // nose bridge
            face += 0.04 * bump(vec2(ax, p.y), vec2(0.17, 0.565), vec2(0.07, 0.05));  // cheeks
            face -= 0.025 * bump(vec2(ax, p.y), vec2(0.135, 0.445), vec2(0.07, 0.045)); // eye sockets
            face += 0.02 * bump(p, vec2(0.5, 0.71), vec2(0.08, 0.04));               // chin
            float hair = 0.24 * sqrt(oval(p, vec2(0.5, 0.4), vec2(0.4, 0.45)));
            float phones = 0.3 * bump(vec2(ax, p.y), vec2(0.33, 0.47), vec2(0.05, 0.12)); // headphones
            float neck = 0.08 * sqrt(oval(p, vec2(0.5, 0.82), vec2(0.12, 0.12)));
            return max(max(face, max(hair, phones)), neck);
        }

        // Jaw: everything below the line of the mouth, across the chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.76, 0.84, p.y));
            float across = 1.0 - smoothstep(0.11, 0.2, abs(p.x - 0.5));
            return below * across;
        }

        """);

    // Photo portrait (cut out of its background): face axis at x 0.515, eyes at (0.405, 0.515) and (0.625, 0.51),
    // brows at y 0.465, nose tip at (0.51, 0.69), closed mouth along y 0.768 at the centre and 0.756 at its corners
    // (x 0.40 to 0.63), bearded chin down to 0.89, hair from 0.07, ears at x 0.26 and 0.755, collar and shoulders below.
    private static readonly FaceModel JeromeFace = new("avares://KAONOMI.Chat/Assets/jerome-face.png", """
        #define PORTRAIT
        const float MOUTH_X = 0.515;
        const float MOUTH_HALF = 0.08;
        const float MOUTH_GAP = 0.012;
        const float JAW_DROP = 0.045;
        const vec3 MOUTH_DARK = vec3(0.20, 0.05, 0.06);
        const vec3 MOUTH_LIGHT = vec3(0.62, 0.30, 0.30);    // the tongue

        """, """
        float mouthLine(float x)
        {
            float d = (x - 0.515) / 0.12;
            return 0.768 - 0.012 * d * d;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.515);
            float head = 0.36 * sqrt(oval(p, vec2(0.515, 0.555), vec2(0.245, 0.34)));
            head += 0.07 * bump(p, vec2(0.51, 0.67), vec2(0.035, 0.04));               // nose tip
            head += 0.04 * bump(p, vec2(0.515, 0.60), vec2(0.025, 0.08));              // nose bridge
            head += 0.035 * bump(vec2(ax, p.y), vec2(0.13, 0.63), vec2(0.06, 0.05));   // cheeks
            head -= 0.03 * bump(vec2(ax, p.y), vec2(0.11, 0.515), vec2(0.055, 0.03));  // eye sockets
            head += 0.025 * bump(vec2(ax, p.y), vec2(0.11, 0.465), vec2(0.07, 0.025)); // brow ridge
            head += 0.04 * bump(vec2(ax, p.y), vec2(0.245, 0.47), vec2(0.02, 0.04));   // ears
            // Chin and jaw: their own volume, so that they come forward instead of following the edge of the oval.
            head = max(head, 0.31 * sqrt(oval(p, vec2(0.515, 0.80), vec2(0.17, 0.12))));
            head += 0.06 * bump(p, vec2(0.515, 0.85), vec2(0.10, 0.05));               // point of the chin
            float hair = 0.27 * sqrt(oval(p, vec2(0.515, 0.32), vec2(0.25, 0.26)));
            float shoulders = 0.12 * sqrt(oval(p, vec2(0.5, 1.12), vec2(0.6, 0.35)));
            return max(max(head, hair), shoulders);
        }

        // Jaw: below the line of the mouth, down to the bearded chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.86, 0.92, p.y));
            float across = 1.0 - smoothstep(0.12, 0.2, abs(p.x - 0.515));
            return below * across;
        }

        """);

    // Eye of a computer: centre of the iris at (0.5, 0.478), glass dome of radius 0.27, red iris within 0.15, bezel
    // around 0.40, housing up to 0.48 (the pedestal below is cut off by the circle).
    private static readonly FaceModel HalFace = new("avares://KAONOMI.Chat/Assets/hal-eye.png", """
        #define HAL
        const vec2 EYE = vec2(0.5, 0.478);
        // No mouth: the eye glows with the voice instead (the mouth is out of the image and never opens).
        const float MOUTH_X = 0.5;
        const float MOUTH_HALF = 0.0;
        const float MOUTH_GAP = 1.0;
        const float JAW_DROP = 0.0;
        const vec3 MOUTH_DARK = vec3(0.0);
        const vec3 MOUTH_LIGHT = vec3(0.0);

        """, """
        float mouthLine(float x)
        {
            return -10.0;
        }

        float relief(vec2 p)
        {
            float r = length(p - EYE);
            float housing = 0.10 * sqrt(max(0.0, 1.0 - (r / 0.49) * (r / 0.49)));
            float b = (r - 0.40) / 0.04;
            float bezel = 0.06 * exp(-b * b);
            float lens = 0.22 * sqrt(max(0.0, 1.0 - (r / 0.27) * (r / 0.27)));   // glass dome
            return housing + bezel + lens;
        }

        float jaw(vec2 p)
        {
            return 0.0;
        }

        """);

    // Robot doll with a knife: face axis at x 0.5, spiral pupils at (0.368, 0.455) and (0.635, 0.455), nose at
    // (0.5, 0.545), grin whose upper and lower teeth meet along y 0.64 at the centre, 0.622 at its left corner (x 0.405)
    // and 0.596 at its right one (x 0.595),
    // robot chin plate down to 0.75, headphones at x 0.21 and 0.79, hearts of the chest at (0.5, 0.835) and
    // (0.5, 0.935), knife blade from (0.94, 0.33) to (0.81, 0.76), the hand on its handle around (0.8, 0.88).
    private static readonly FaceModel MarieKilleuseFace = new("avares://KAONOMI.Chat/Assets/mariekilleuse-face.png", """
        #define KILLER
        const vec2 EYE_LEFT = vec2(0.368, 0.455);
        const vec2 EYE_RIGHT = vec2(0.635, 0.455);
        const vec2 BLADE_TIP = vec2(0.94, 0.33);
        const vec2 BLADE_HEEL = vec2(0.81, 0.76);
        const float MOUTH_X = 0.5;
        const float MOUTH_HALF = 0.075;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.05;
        const vec3 MOUTH_DARK = vec3(0.25, 0.02, 0.06);
        const vec3 MOUTH_LIGHT = vec3(0.75, 0.15, 0.25);    // the tongue

        """, """
        // The line between the upper and the lower teeth of the grin (tilted: its right corner is higher), so that the
        // lower teeth go down with the jaw.
        float mouthLine(float x)
        {
            float t = clamp((x - 0.5) / 0.1, -1.0, 1.0);
            float t2 = t * t;
            return 0.6392 - 0.0106 * t + 0.005 * t2 - 0.042 * t2 * t2;
        }

        // Distance from p to the blade of the knife.
        float bladeDistance(vec2 p)
        {
            vec2 axis = BLADE_HEEL - BLADE_TIP;
            float t = clamp(dot(p - BLADE_TIP, axis) / dot(axis, axis), 0.0, 1.0);
            return length(p - BLADE_TIP - axis * t);
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.5);
            float face = 0.34 * sqrt(oval(p, vec2(0.5, 0.5), vec2(0.265, 0.27)));
            face += 0.05 * bump(p, vec2(0.5, 0.545), vec2(0.03, 0.025));              // small nose
            face += 0.04 * bump(vec2(ax, p.y), vec2(0.165, 0.565), vec2(0.06, 0.045)); // cheeks
            face -= 0.025 * bump(vec2(ax, p.y), vec2(0.135, 0.455), vec2(0.065, 0.05)); // eye sockets
            face += 0.03 * bump(p, vec2(0.5, 0.72), vec2(0.09, 0.035));               // chin plate
            float hair = 0.24 * sqrt(oval(p, vec2(0.5, 0.42), vec2(0.38, 0.42)));
            float phones = 0.3 * bump(vec2(ax, p.y), vec2(0.29, 0.48), vec2(0.05, 0.12)); // headphones
            float bow = 0.2 * bump(p, vec2(0.67, 0.09), vec2(0.05, 0.04));
            float neck = 0.1 * sqrt(oval(p, vec2(0.5, 0.80), vec2(0.12, 0.08)));
            float body = 0.15 * sqrt(oval(p, vec2(0.5, 1.05), vec2(0.45, 0.3)));
            // The knife and the hand holding it come out of the picture, towards the camera.
            float hand = 0.3 * bump(p, vec2(0.8, 0.88), vec2(0.08, 0.08));
            float d = bladeDistance(p) / 0.045;
            float blade = 0.26 * exp(-d * d);
            return max(max(max(face, max(hair, phones)), max(bow, max(neck, body))), max(hand, blade));
        }

        // Jaw: the lower teeth and the robot chin plate drop like the jaw of a ventriloquist doll (between the corners
        // of the mouth, so that its dark outline is not dragged down there).
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.745, 0.79, p.y));
            float across = 1.0 - smoothstep(0.06, 0.1, abs(p.x - 0.5));
            return below * across;
        }

        // Spiral pupil: the image turns around the centre of the eye, faster near it, so that the spiral spins.
        vec2 spin(vec2 uv, vec2 eye, float angle)
        {
            vec2 d = uv - eye;
            float fall = 1.0 - smoothstep(0.018, 0.034, length(d));
            float a = angle * fall;
            return eye + mat2(cos(a), -sin(a), sin(a), cos(a)) * d;
        }

        """);

    // Robot boy: face axis at x 0.483, green irises at (0.40, 0.3825) and (0.565, 0.3825), nose tip at (0.475, 0.465),
    // closed smile along y 0.517 at the centre and 0.507 at its corners (x 0.42 to 0.545), chin at 0.605, hair from
    // 0.1, ears at x 0.30 and 0.675, robot neck and white suit below with its lights (the core at (0.5, 0.762)).
    private static readonly FaceModel MusukoFace = new("avares://KAONOMI.Chat/Assets/musuko-face.png", """
        #define MUSUKO
        const vec2 EYE_LEFT = vec2(0.40, 0.3825);
        const vec2 EYE_RIGHT = vec2(0.565, 0.3825);
        const float MOUTH_X = 0.483;
        const float MOUTH_HALF = 0.058;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.035;
        const vec3 MOUTH_DARK = vec3(0.10, 0.04, 0.08);
        const vec3 MOUTH_LIGHT = vec3(0.55, 0.28, 0.32);   // the tongue

        """, """
        float mouthLine(float x)
        {
            float d = (x - 0.483) / 0.065;
            return 0.517 - 0.01 * d * d;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.483);
            float head = 0.34 * sqrt(oval(p, vec2(0.485, 0.42), vec2(0.175, 0.2)));
            head += 0.07 * bump(p, vec2(0.475, 0.462), vec2(0.03, 0.025));              // nose tip
            head += 0.03 * bump(p, vec2(0.48, 0.42), vec2(0.02, 0.05));                 // nose bridge
            head += 0.035 * bump(vec2(ax, p.y), vec2(0.1, 0.46), vec2(0.05, 0.04));     // cheeks
            head -= 0.025 * bump(vec2(ax, p.y), vec2(0.083, 0.383), vec2(0.045, 0.025)); // eye sockets
            head += 0.02 * bump(vec2(ax, p.y), vec2(0.085, 0.33), vec2(0.06, 0.02));    // brows
            head += 0.03 * bump(p, vec2(0.483, 0.575), vec2(0.07, 0.035));              // chin
            float ears = 0.12 * bump(vec2(ax, p.y), vec2(0.19, 0.44), vec2(0.025, 0.045));
            float hair = 0.27 * sqrt(oval(p, vec2(0.49, 0.29), vec2(0.215, 0.21)));
            float neck = 0.1 * sqrt(oval(p, vec2(0.5, 0.65), vec2(0.1, 0.07)));
            float body = 0.16 * sqrt(oval(p, vec2(0.5, 1.08), vec2(0.4, 0.4)));
            return max(max(max(head, ears), hair), max(neck, body));
        }

        // Jaw: below the line of the mouth, down to the chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.59, 0.625, p.y));
            float across = 1.0 - smoothstep(0.1, 0.15, abs(p.x - 0.483));
            return below * across;
        }

        // Light of one of the glowing lights of the suit.
        float light(vec2 p, vec2 center, float size)
        {
            return exp(-length(p - center) / size);
        }

        """);

    // Masked robot: face axis at x 0.5, head from y 0.04 to the chin at 0.775 (x 0.23 to 0.775), eye holes at
    // (0.335, 0.37) and (0.668, 0.37), nostrils at y 0.567, mouth slot from x 0.44 to 0.565 and y 0.625 to 0.655, ear
    // discs at x 0.155 and 0.845, neck and collar below 0.78, shoulders on the edges.
    private static readonly FaceModel KaonamiFace = new("avares://KAONOMI.Chat/Assets/kaonami-face.png", """
        #define KAONAMI
        const vec2 EYE_LEFT = vec2(0.335, 0.37);
        const vec2 EYE_RIGHT = vec2(0.668, 0.37);
        const vec2 SLOT = vec2(0.503, 0.64);
        const float MOUTH_X = 0.503;
        const float MOUTH_HALF = 0.055;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.035;
        const vec3 MOUTH_DARK = vec3(0.02, 0.02, 0.02);
        const vec3 MOUTH_LIGHT = vec3(0.35, 0.16, 0.05);   // the glow inside

        """, """
        // Bottom edge of the mouth slot.
        float mouthLine(float x)
        {
            return 0.653;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.5);
            float head = 0.36 * sqrt(oval(p, vec2(0.5, 0.42), vec2(0.275, 0.36)));
            head -= 0.03 * bump(vec2(ax, p.y), vec2(0.167, 0.37), vec2(0.04, 0.04));   // eye holes
            head += 0.03 * bump(p, vec2(0.5, 0.71), vec2(0.1, 0.05));                  // chin
            float ears = 0.2 * bump(vec2(ax, p.y), vec2(0.345, 0.38), vec2(0.06, 0.1)); // ear discs
            float neck = 0.1 * sqrt(oval(p, vec2(0.5, 0.82), vec2(0.15, 0.08)));
            float body = 0.16 * sqrt(oval(p, vec2(0.5, 1.1), vec2(0.55, 0.32)));
            return max(max(head, ears), max(neck, body));
        }

        // Jaw: the chin under the mouth slot drops like a flap.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.75, 0.79, p.y));
            float across = 1.0 - smoothstep(0.08, 0.14, abs(p.x - 0.503));
            return below * across;
        }

        """);

    // Robot with a tablet, the head tilted: head from y 0.04 to 0.69 (x 0.14 to 0.86), black eye lenses at (0.282, 0.357)
    // and (0.54, 0.29) in pink rings, small red mouth slanting from (0.377, 0.523) to (0.487, 0.498), ear speaker at
    // (0.83, 0.3), neck at (0.53, 0.73), tablet screen from x 0.1 to 0.68 and y 0.86 down, shoulder on the right.
    private static readonly FaceModel PepperFace = new("avares://KAONOMI.Chat/Assets/pepper-face.png", """
        #define PEPPER
        const vec2 EYE_LEFT = vec2(0.282, 0.357);
        const vec2 EYE_RIGHT = vec2(0.54, 0.29);
        const vec2 EAR = vec2(0.83, 0.3);
        const float MOUTH_X = 0.432;
        const float MOUTH_HALF = 0.05;
        const float MOUTH_GAP = 0.008;
        const float JAW_DROP = 0.025;
        const vec3 MOUTH_DARK = vec3(0.18, 0.02, 0.03);
        const vec3 MOUTH_LIGHT = vec3(0.55, 0.08, 0.1);   // the red inside

        """, """
        // Upper edge of the red mouth (slanting with the head).
        float mouthLine(float x)
        {
            return 0.527 - (x - 0.377) * 0.23;
        }

        float relief(vec2 p)
        {
            float head = 0.38 * sqrt(oval(p, vec2(0.5, 0.37), vec2(0.36, 0.33)));
            head -= 0.03 * bump(p, EYE_LEFT, vec2(0.06, 0.06));                     // eye rings
            head -= 0.03 * bump(p, EYE_RIGHT, vec2(0.06, 0.06));
            head += 0.04 * bump(p, vec2(0.42, 0.45), vec2(0.08, 0.06));             // face front
            float ear = 0.18 * bump(p, EAR, vec2(0.04, 0.09));
            float neck = 0.12 * sqrt(oval(p, vec2(0.53, 0.75), vec2(0.1, 0.08)));
            float body = 0.16 * sqrt(oval(p, vec2(0.6, 1.1), vec2(0.5, 0.35)));
            // The tablet stands out of the chest.
            vec2 t = (p - vec2(0.38, 0.93)) / vec2(0.34, 0.1);
            float tablet = 0.22 * exp(-pow(t.x * t.x, 3.0) - pow(t.y * t.y, 3.0));
            return max(max(head, ear), max(max(neck, body), tablet));
        }

        // Jaw: the lower lip of the small mouth drops with the voice.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.56, 0.6, p.y));
            float across = 1.0 - smoothstep(0.045, 0.07, abs(p.x - MOUTH_X));
            return below * across;
        }

        """);

    // Android child, face front: face axis at x 0.495, head cut by the top of the frame at y 0.083 (x 0.05 to 0.95 with
    // the ears), glowing green eyes at (0.30, 0.425) and (0.71, 0.425), brows at 0.29, nose tip at (0.498, 0.625), closed
    // lips along y 0.738 at the centre and 0.731 at their corners (x 0.418 to 0.569), chin at 0.8, green mechanical ears at
    // x 0.08 and 0.92, neck and shoulders below, cut by the bottom of the frame at 0.92.
    private static readonly FaceModel GreenFace = new("avares://KAONOMI.Chat/Assets/green-face.png", """
        #define GREEN
        const vec2 EYE_LEFT = vec2(0.30, 0.425);
        const vec2 EYE_RIGHT = vec2(0.71, 0.425);
        const float MOUTH_X = 0.495;
        const float MOUTH_HALF = 0.078;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.035;
        const vec3 MOUTH_DARK = vec3(0.02, 0.05, 0.04);
        const vec3 MOUTH_LIGHT = vec3(0.12, 0.45, 0.2);   // a green glow inside

        """, """
        float mouthLine(float x)
        {
            float d = (x - 0.495) / 0.075;
            return 0.738 - 0.007 * d * d;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.495);
            float head = 0.4 * sqrt(oval(p, vec2(0.5, 0.42), vec2(0.4, 0.45)));
            head += 0.08 * bump(p, vec2(0.498, 0.615), vec2(0.04, 0.035));               // nose tip
            head += 0.035 * bump(p, vec2(0.497, 0.53), vec2(0.025, 0.07));               // nose bridge
            head += 0.04 * bump(vec2(ax, p.y), vec2(0.2, 0.6), vec2(0.08, 0.06));        // cheeks
            head -= 0.035 * bump(vec2(ax, p.y), vec2(0.21, 0.425), vec2(0.09, 0.045));   // eye sockets
            head += 0.025 * bump(vec2(ax, p.y), vec2(0.2, 0.3), vec2(0.1, 0.025));       // brows
            head += 0.03 * bump(p, vec2(0.497, 0.775), vec2(0.08, 0.035));               // chin
            float ears = 0.16 * bump(vec2(ax, p.y), vec2(0.42, 0.47), vec2(0.04, 0.09));
            float neck = 0.1 * sqrt(oval(p, vec2(0.5, 0.88), vec2(0.15, 0.08)));
            float body = 0.16 * sqrt(oval(p, vec2(0.5, 1.12), vec2(0.6, 0.28)));
            return max(max(head, ears), max(neck, body));
        }

        // Jaw: below the lips, down to the chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.8, 0.835, p.y));
            float across = 1.0 - smoothstep(0.12, 0.18, abs(p.x - 0.495));
            return below * across;
        }

        """);

    // Jerome filmed: one frame of his video, aligned on the eyes (at (0.425, 0.4) and (0.575, 0.4)) and cut out. Hair from
    // y 0.09, nose tip at (0.5, 0.533), closed lips from x 0.435 to 0.56 along y 0.577 to 0.587 (a little lower on the
    // right), chin at 0.704, face from x 0.318 to 0.661, T-shirt and shoulders below 0.66, cut by the bottom of the frame.
    private static readonly FaceModel JeromeLiveFace = new("avares://KAONOMI.Chat/Assets/jeromelive-face.png", """
        #define LIVE
        const vec2 EYE_LEFT = vec2(0.425, 0.4);
        const vec2 EYE_RIGHT = vec2(0.575, 0.4);
        const float MOUTH_X = 0.497;
        const float MOUTH_HALF = 0.06;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.03;
        const vec3 MOUTH_DARK = vec3(0.09, 0.03, 0.03);
        const vec3 MOUTH_LIGHT = vec3(0.5, 0.25, 0.25);   // the tongue

        """, """
        float mouthLine(float x)
        {
            float u = x - 0.497;
            return 0.5772 + 0.045 * u + 1.975 * u * u;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.49);
            float head = 0.3 * sqrt(oval(p, vec2(0.49, 0.42), vec2(0.18, 0.3)));
            head += 0.06 * bump(p, vec2(0.5, 0.52), vec2(0.022, 0.025));                // nose tip
            head += 0.025 * bump(p, vec2(0.5, 0.46), vec2(0.015, 0.05));                // nose bridge
            head += 0.03 * bump(vec2(ax, p.y), vec2(0.1, 0.5), vec2(0.045, 0.04));      // cheeks
            head -= 0.02 * bump(vec2(ax, p.y), vec2(0.075, 0.4), vec2(0.035, 0.02));    // eye sockets
            head += 0.025 * bump(p, vec2(0.497, 0.67), vec2(0.06, 0.03));               // chin
            float ears = 0.1 * bump(vec2(ax, p.y), vec2(0.175, 0.44), vec2(0.02, 0.045));
            float hair = 0.24 * sqrt(oval(p, vec2(0.49, 0.26), vec2(0.18, 0.17)));
            float neck = 0.1 * sqrt(oval(p, vec2(0.49, 0.74), vec2(0.1, 0.07)));
            float body = 0.16 * sqrt(oval(p, vec2(0.5, 1.05), vec2(0.55, 0.36)));
            return max(max(max(head, ears), hair), max(neck, body));
        }

        // Jaw: below the lips, down to the chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.69, 0.73, p.y));
            float across = 1.0 - smoothstep(0.1, 0.15, abs(p.x - 0.497));
            return below * across;
        }

        """);

    // Marvin: round head centred at (0.5, 0.37) (x 0.197 to 0.808, y 0.07 to 0.68), visor line across it at y 0.43, green
    // eye slits under it at (0.289, 0.43) and (0.637, 0.449), black neck at y 0.66, white shoulders and chest below,
    // cut by the bottom of the frame. No mouth: the eyes and the visor speak.
    private static readonly FaceModel MarvinFace = new("avares://KAONOMI.Chat/Assets/marvin-face.png", """
        #define MARVIN
        const vec2 EYE_LEFT = vec2(0.289, 0.43);
        const vec2 EYE_RIGHT = vec2(0.637, 0.449);
        const float MOUTH_X = 0.5;
        const float MOUTH_HALF = 0.0;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.0;
        const vec3 MOUTH_DARK = vec3(0.0);
        const vec3 MOUTH_LIGHT = vec3(0.0);

        """, """
        float mouthLine(float x)
        {
            return 0.6;
        }

        float relief(vec2 p)
        {
            // The head is a sphere; the eye slits are set a little into it.
            float head = 0.45 * sqrt(oval(p, vec2(0.502, 0.375), vec2(0.306, 0.306)));
            head -= 0.012 * bump(p, EYE_LEFT, vec2(0.035, 0.022));
            head -= 0.012 * bump(p, EYE_RIGHT, vec2(0.035, 0.022));
            float neck = 0.08 * sqrt(oval(p, vec2(0.5, 0.68), vec2(0.12, 0.05)));
            float body = 0.2 * sqrt(oval(p, vec2(0.5, 0.98), vec2(0.36, 0.32)));
            return max(head, max(neck, body));
        }

        // No jaw: Marvin has no mouth.
        float jaw(vec2 p)
        {
            return 0.0;
        }

        """);

    // Helpers of the shapes of the faces.
    private const string ShapeFunctions = """
        float bump(vec2 p, vec2 center, vec2 size)
        {
            vec2 d = (p - center) / size;
            return exp(-dot(d, d));
        }

        float oval(vec2 p, vec2 center, vec2 radius)
        {
            vec2 d = (p - center) / radius;
            return max(0.0, 1.0 - dot(d, d));
        }

        """;

    private const string VertexShader = """
        in vec2 aUv;
        uniform mat4 uMvp;
        uniform float uOpen;
        out vec2 vUv;
        out float vLight;

        void main()
        {
            vUv = aUv;
            float z = relief(aUv);
            float e = 0.004;
            vec3 normal = normalize(vec3(-(relief(aUv + vec2(e, 0.0)) - z) / e * 0.5, (relief(aUv + vec2(0.0, e)) - z) / e * 0.5, 1.0));
            vLight = 0.7 + 0.3 * dot(normal, normalize(vec3(-0.4, 0.4, 0.8)));

            vec2 position = vec2(aUv.x * 2.0 - 1.0, 1.0 - aUv.y * 2.0);
            position.y -= uOpen * JAW_DROP * jaw(aUv);
            gl_Position = uMvp * vec4(position, z, 1.0);
        }
        """;

    private const string FragmentShader = """
        in vec2 vUv;
        in float vLight;
        uniform sampler2D uFace;
        uniform float uTime;
        uniform float uOpen;
        uniform vec3 uGlitch;
        out vec4 fragColor;

        void main()
        {
            vec2 uv = vUv;
        #if defined(HOLOGRAM) || defined(KILLER)
            // Glitch: a thin band of the image slides sideways.
            if (abs(uv.y - uGlitch.x) < 0.012)
                uv.x += uGlitch.y;
        #endif
        #ifdef MUSUKO
            // A blink every few seconds: the eyelids come down over the eyes (the skin just under each eye, darker,
            // with the line of the lashes at their edge).
            float blinkPhase = fract(uTime / 4.3);
            float blink = blinkPhase < 0.045 ? sin(blinkPhase / 0.045 * 3.14159) : 0.0;
            vec2 eye = uv.x < 0.483 ? EYE_LEFT : EYE_RIGHT;
            vec2 e = (uv - eye) / vec2(0.05, 0.024);
            float lid = -1.0 + 2.0 * blink;   // edge of the eyelid, from above the eye (-1) to below it (1)
            // Share of the eyelid over this point: inside the eye, above the edge of the lid (soft edges).
            float covered = blink > 0.0 ? (1.0 - smoothstep(0.75, 1.05, dot(e, e))) * (1.0 - smoothstep(lid - 0.12, lid, e.y)) : 0.0;
        #endif
        #ifdef KILLER
            // The spiral pupils spin, faster while she speaks.
            float turn = uTime * 2.2 + uOpen * 3.0;
            uv = spin(spin(uv, EYE_LEFT, turn), EYE_RIGHT, turn);
        #endif
        #ifdef GREEN
            // The pupils dilate with the voice and breathe slowly when it is quiet (the inside of each eye is magnified
            // around its centre).
            vec2 eye = uv.x < 0.505 ? EYE_LEFT : EYE_RIGHT;
            vec2 fromEye = uv - eye;
            float inEye = 1.0 - smoothstep(0.05, 0.1, length(fromEye / vec2(1.0, 0.55)));
            uv = eye + fromEye * (1.0 - (0.12 * uOpen + 0.03 * sin(uTime * 1.5)) * inEye);
            // A blink every few seconds: the eyelids come down over the eyes.
            float blinkPhase = fract(uTime / 5.1);
            float blink = blinkPhase < 0.05 ? sin(blinkPhase / 0.05 * 3.14159) : 0.0;
            vec2 e = (vUv - eye) / vec2(0.11, 0.05);
            float lid = -1.0 + 2.0 * blink;   // edge of the eyelid, from above the eye (-1) to below it (1)
            float covered = blink > 0.0 ? (1.0 - smoothstep(0.75, 1.05, dot(e, e))) * (1.0 - smoothstep(lid - 0.12, lid, e.y)) : 0.0;
        #endif
        #ifdef HAL
            // The iris dilates with the voice and breathes slowly when it is quiet.
            vec2 fromEye = uv - EYE;
            float dilate = (0.10 * uOpen + 0.02 * sin(uTime * 1.3)) * (1.0 - smoothstep(0.12, 0.22, length(fromEye)));
            uv = EYE + fromEye * (1.0 - dilate);
        #endif
            vec4 color = texture(uFace, uv);   // premultiplied alpha
        #ifdef LIVE
            // A blink every few seconds: the eyelids (the skin just above each eye) come down over the eyes.
            float blinkPhase = fract(uTime / 4.7);
            float blink = blinkPhase < 0.04 ? sin(blinkPhase / 0.04 * 3.14159) : 0.0;
            vec2 eye = uv.x < 0.5 ? EYE_LEFT : EYE_RIGHT;
            vec2 e = (uv - eye) / vec2(0.04, 0.016);
            float lid = -1.0 + 2.0 * blink;   // edge of the eyelid, from above the eye (-1) to below it (1)
            float covered = blink > 0.0 ? (1.0 - smoothstep(0.7, 1.05, dot(e, e))) * (1.0 - smoothstep(lid - 0.15, lid, e.y)) : 0.0;
            if (covered > 0.0)
            {
                vec4 skin = texture(uFace, vec2(uv.x, eye.y - 0.025));
                float lash = smoothstep(0.25, 0.0, abs(e.y - lid)) * (1.0 - smoothstep(0.7, 1.0, abs(e.x)));
                color = mix(color, vec4(mix(skin.rgb * 0.92, vec3(0.12, 0.08, 0.07) * skin.a, lash), skin.a), covered);
            }
        #endif

            // Open mouth: the thin band just below the line of the mouth is stretched when the jaw drops; it shows
            // the inside of the mouth (dark, the tongue lighter at the bottom), shaped like a lens.
            float depth = (uv.y - mouthLine(uv.x)) / MOUTH_GAP;
            float lens = 2.0 * depth - 1.0;
            float halfWidth = MOUTH_HALF * sqrt(max(0.0, 1.0 - lens * lens));
            float inside = smoothstep(0.0, 0.08, depth) * (1.0 - smoothstep(0.92, 1.0, depth))
                           * (1.0 - smoothstep(halfWidth - 0.012, halfWidth, abs(uv.x - MOUTH_X)))
                           * smoothstep(0.02, 0.15, uOpen);
            vec3 mouth = mix(MOUTH_DARK, MOUTH_LIGHT, smoothstep(0.45, 0.95, depth));
            color.rgb = mix(color.rgb, mouth * color.a, inside);

        #if defined(PORTRAIT) || defined(LIVE)
            // Softened light from the relief (the photo has its own); the shoulders, cut straight by the frame at the
            // bottom and on the sides, fade out instead.
            color.rgb *= mix(1.0, vLight, 0.6);
            color *= (1.0 - smoothstep(0.9, 1.0, vUv.y)) * smoothstep(0.0, 0.08, vUv.x) * (1.0 - smoothstep(0.92, 1.0, vUv.x));
        #elif defined(PEPPER)
            // Softened light from the relief; the body, cut by the frame, fades out at the bottom and on the right.
            color.rgb *= mix(1.0, vLight, 0.5);
            color *= (1.0 - smoothstep(0.92, 1.0, vUv.y)) * (1.0 - smoothstep(0.93, 1.0, vUv.x));
            float breath = 0.5 + 0.5 * sin(uTime * 1.4);
            float level = 0.12 + 0.12 * breath + 0.9 * uOpen;
            // The rings around the eye lenses and the ear speaker light up in blue while it speaks.
            float rings = 0.0;
            float rl = length(vUv - EYE_LEFT), rr = length(vUv - EYE_RIGHT);
            float dl = (rl - 0.062) / 0.012, dr = (rr - 0.064) / 0.012;
            rings += exp(-dl * dl) + exp(-dr * dr);
            vec2 e = (vUv - EAR) / vec2(0.035, 0.085);
            float de = (length(e) - 0.8) / 0.25;
            float ear = exp(-de * de);
            vec3 blue = vec3(0.25, 0.65, 1.0);
            color.rgb += blue * (rings + 0.8 * ear) * level * color.a;
            // The tablet screen (its dark glass) glows softly, with a voice wave running across it.
            float glass = (1.0 - smoothstep(0.25, 0.45, max(color.r, max(color.g, color.b))))
                          * step(0.1, vUv.x) * step(vUv.x, 0.68) * step(0.86, vUv.y);
            float wave = 0.93 + 0.025 * uOpen * sin(vUv.x * 70.0 - uTime * 9.0) * sin(vUv.x * 13.0 + uTime * 2.0);
            float dw = (vUv.y - wave) / 0.004;
            float line = exp(-dw * dw);
            color.rgb += glass * color.a * (vec3(0.1, 0.25, 0.4) * (0.4 + 0.6 * level) + blue * line * (0.3 + 0.9 * uOpen));
        #elif defined(KAONAMI)
            // Softened light from the relief; the shoulders, cut by the frame, fade out at the bottom and on the sides.
            color.rgb *= mix(1.0, vLight, 0.55);
            color *= (1.0 - smoothstep(0.9, 1.0, vUv.y)) * smoothstep(0.02, 0.1, vUv.x) * (1.0 - smoothstep(0.9, 0.98, vUv.x));
            // An amber light in the dark of the eye holes and of the mouth slot: faint when quiet, bright while speaking.
            float dark = 1.0 - smoothstep(0.08, 0.3, max(color.r, max(color.g, color.b)));
            float flicker = 0.85 + 0.15 * sin(uTime * 23.0) * sin(uTime * 7.0);
            float holes = exp(-length(vUv - EYE_LEFT) / 0.016) + exp(-length(vUv - EYE_RIGHT) / 0.016);
            vec2 s = (vUv - SLOT) / vec2(0.06, 0.012);
            float slot = exp(-dot(s, s));
            float shine = (holes + slot) * dark * (0.12 + 1.1 * uOpen * flicker);
            color.rgb += vec3(1.0, 0.55, 0.15) * shine * color.a;
        #elif defined(GREEN)
            if (covered > 0.0)
            {
                // Skin of the eyelid: the pale skin above the eye, darker at the line of the lashes.
                vec4 skin = texture(uFace, vec2(vUv.x, eye.y - 0.075));
                float lash = smoothstep(0.22, 0.0, abs(e.y - lid)) * (1.0 - smoothstep(0.7, 1.0, abs(e.x)));
                vec4 eyelid = vec4(mix(skin.rgb * 0.9, vec3(0.02, 0.03, 0.03) * skin.a, lash), skin.a);
                color = mix(color, eyelid, covered);
            }
            // Softened light from the relief; the head, cut by the top of the frame, and the shoulders, cut at the bottom,
            // fade out.
            color.rgb *= mix(1.0, vLight, 0.5);
            color *= smoothstep(0.083, 0.14, vUv.y) * (1.0 - smoothstep(0.86, 0.92, vUv.y));
            // The green of the eyes, the ears and the shoulders glows brighter with the voice and breathes when quiet.
            float breath = 0.5 + 0.5 * sin(uTime * 1.4);
            float greenness = smoothstep(0.08, 0.3, color.g - max(color.r, color.b));
            float eyes = (1.0 - covered) * (exp(-length((vUv - EYE_LEFT) / vec2(1.0, 0.55)) / 0.05)
                                            + exp(-length((vUv - EYE_RIGHT) / vec2(1.0, 0.55)) / 0.05));
            vec3 green = vec3(0.25, 1.0, 0.4);
            color.rgb += green * greenness * (0.08 + 0.12 * breath + 0.7 * uOpen) * color.a;
            vec3 glow = green * eyes * (0.12 + 0.08 * breath + 0.8 * uOpen);
            color.rgb += glow;
            color.a = clamp(max(color.a, glow.g * 0.7), 0.0, 1.0);
            // A green scan line runs down the face now and then.
            float scan = smoothstep(0.012, 0.0, abs(fract(uTime * 0.09) * 1.6 - 0.3 - vUv.y));
            color.rgb += green * scan * 0.18 * color.a;
        #elif defined(MARVIN)
            // Softened light from the relief; the body, cut by the frame, fades out at the bottom.
            color.rgb *= mix(1.0, vLight, 0.5);
            color *= 1.0 - smoothstep(0.9, 1.0, vUv.y);
            // The eyes go dark for a moment every few seconds (a weary blink).
            float blinkPhase = fract(uTime / 6.3);
            float awake = blinkPhase < 0.06 ? 1.0 - sin(blinkPhase / 0.06 * 3.14159) : 1.0;
            float green = smoothstep(0.08, 0.3, color.g - max(color.r, color.b));
            color.rgb = mix(color.rgb, color.rgb * vec3(0.25, 0.3, 0.25), green * (1.0 - awake));
            // The eyes glow with the voice, and breathe slowly when he is quiet; the glow spreads beyond the slits.
            float breath = 0.5 + 0.5 * sin(uTime * 0.9);
            float level = awake * (0.1 + 0.12 * breath + 1.0 * uOpen);
            vec3 eyeGreen = vec3(0.35, 1.0, 0.3);
            vec2 dl = (vUv - EYE_LEFT) / vec2(1.0, 0.6), dr = (vUv - EYE_RIGHT) / vec2(1.0, 0.6);
            float halo = exp(-length(dl) / 0.035) + exp(-length(dr) / 0.035);
            color.rgb += eyeGreen * green * level * color.a;
            vec3 glow = eyeGreen * halo * level * 0.6;
            // While he speaks, a green light runs along the visor line, from one side of the head to the other.
            float visor = 0.418 + (vUv.x - 0.289) * 0.055;
            float onLine = exp(-pow((vUv.y - visor) / 0.006, 2.0)) * (1.0 - smoothstep(0.27, 0.3, abs(vUv.x - 0.502)));
            float sweep = exp(-pow((vUv.x - (0.2 + 0.6 * fract(uTime * 0.6))) / 0.06, 2.0));
            glow += eyeGreen * onLine * (0.15 + sweep) * uOpen * 0.8;
            color.rgb += glow;
            color.a = clamp(max(color.a, glow.g * 0.7), 0.0, 1.0);
        #elif defined(MUSUKO)
            if (covered > 0.0)
            {
                // Skin of the eyelid: the average of the skin above and below the eye, with the lashes at its edge.
                vec4 skin = 0.5 * (texture(uFace, vec2(uv.x, eye.y - 0.036)) + texture(uFace, vec2(uv.x, eye.y + 0.05)));
                float lash = smoothstep(0.22, 0.0, abs(e.y - lid)) * (1.0 - smoothstep(0.7, 1.0, abs(e.x)));
                vec4 eyelid = vec4(mix(skin.rgb * 0.92, vec3(0.03, 0.04, 0.07) * skin.a, lash), skin.a);
                color = mix(color, eyelid, covered);
            }
            // Softened light from the relief; the suit, cut by the frame at the bottom and on the sides, fades out.
            color.rgb *= mix(1.0, vLight, 0.5);
            color *= (1.0 - smoothstep(0.9, 1.0, vUv.y)) * smoothstep(0.08, 0.16, vUv.x) * (1.0 - smoothstep(0.84, 0.92, vUv.x));
            // The green irises and the lights of the suit glow with the voice, and breathe slowly when he is quiet.
            float breath = 0.5 + 0.5 * sin(uTime * 1.6);
            float irises = (1.0 - covered) * light(vUv, EYE_LEFT, 0.012) + light(vUv, EYE_RIGHT, 0.012);
            color.rgb += vec3(0.25, 1.0, 0.35) * irises * (0.1 + 0.6 * uOpen) * color.a;
            float lights = light(vUv, vec2(0.5, 0.762), 0.03) + light(vUv, vec2(0.51, 0.985), 0.04)
                           + 0.7 * (light(vUv, vec2(0.24, 0.86), 0.018) + light(vUv, vec2(0.31, 0.835), 0.018)
                                    + light(vUv, vec2(0.695, 0.82), 0.018) + light(vUv, vec2(0.755, 0.835), 0.018));
            vec3 glow = vec3(0.3, 0.95, 1.0) * lights * (0.15 + 0.2 * breath + 0.9 * uOpen);
            color.rgb += glow;
            color.a = clamp(max(color.a, max(glow.g, glow.b) * 0.8), 0.0, 1.0);
        #elif defined(KILLER)
            // Softened light from the relief; the body, cut by the frame at the bottom, fades out.
            color.rgb *= mix(1.0, vLight, 0.55);
            color *= 1.0 - smoothstep(0.92, 1.0, vUv.y);
            // A glint runs along the blade every few seconds (on the grey steel only, not on the blood).
            float steel = 1.0 - smoothstep(0.08, 0.2, max(color.r, max(color.g, color.b)) - min(color.r, min(color.g, color.b)));
            vec2 axis = normalize(BLADE_HEEL - BLADE_TIP);
            float along = dot(vUv - BLADE_TIP, axis) / length(BLADE_HEEL - BLADE_TIP);
            float sweep = fract(uTime * 0.22) * 2.2 - 0.6;
            float g = (along - sweep) / 0.05;
            float glint = exp(-g * g) * (1.0 - smoothstep(0.03, 0.05, bladeDistance(vUv)));
            color.rgb += vec3(1.0) * glint * steel * 0.8 * color.a;
            // The hearts of the chest glow pink with the voice, and beat slowly when she is quiet.
            float beat = pow(0.5 + 0.5 * sin(uTime * 2.6), 4.0);
            float hearts = exp(-length(vUv - vec2(0.5, 0.835)) / 0.018) + exp(-length(vUv - vec2(0.5, 0.935)) / 0.03);
            color.rgb += vec3(1.0, 0.35, 0.75) * hearts * (0.15 + 0.25 * beat + 0.9 * uOpen);
            // During a glitch the picture splits into red and cyan.
            float glitching = step(uGlitch.x, 90.0);
            color.r += glitching * 0.25 * texture(uFace, uv + vec2(0.006, 0.0)).a;
            color.gb *= 1.0 - glitching * 0.15;
        #elif defined(HAL)
            // Cut out in a circle around the housing, lit from the relief; the red core, its halo and the horizontal
            // flare glow brighter with the voice (added light, also beyond the circle).
            float r = length(vUv - EYE);
            color *= mix(1.0, vLight, 0.5) * (1.0 - smoothstep(0.46, 0.5, r));
            float breath = 0.5 + 0.5 * sin(uTime * 1.3);
            float core = exp(-r * r / 0.0012) * (0.35 + 0.9 * uOpen + 0.15 * breath);
            float halo = exp(-r / 0.07) * (0.12 + 0.5 * uOpen + 0.05 * breath);
            float flare = exp(-abs(vUv.y - EYE.y) * 160.0) * exp(-abs(vUv.x - EYE.x) * 4.0) * (0.25 + 0.75 * uOpen);
            vec3 glow = vec3(1.0, 0.18, 0.08) * (halo + flare) + vec3(1.0, 0.75, 0.55) * core;
            color.rgb += glow;
            color.a = clamp(max(color.a, max(glow.r, max(glow.g, glow.b))), 0.0, 1.0);
        #else
            // Light from the relief, faint scan lines and a slow scan glow running down.
            float scanLines = 0.94 + 0.06 * sin(gl_FragCoord.y * 2.2);
            float scan = 0.08 * smoothstep(0.06, 0.0, abs(fract(uTime * 0.12) * 1.4 - 0.2 - uv.y));
            color.rgb *= vLight * scanLines;
            color.rgb += scan * color.a * vec3(0.55, 0.85, 1.0);
        #endif
            fragColor = color;
        }
        """;
}
