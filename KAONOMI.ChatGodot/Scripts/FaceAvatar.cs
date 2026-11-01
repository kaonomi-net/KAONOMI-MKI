using System;
using System.IO;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Views;

namespace KAONOMI.ChatGodot;

/// <summary>What the chat tells an avatar: the voice, the words shown, the server at work.</summary>
public interface IAvatar
{
    /// <summary>Loudness of the voice reading the answer, 0 (silence) to 1, read on every frame.</summary>
    Func<float>? VoiceLevel { get; set; }

    /// <summary>True while the answer is read aloud.</summary>
    bool Speaking { get; set; }

    /// <summary>True while the Kaonomi server writes the answer.</summary>
    bool Thinking { get; set; }

    /// <summary>A word of the answer was shown (its length in characters).</summary>
    void Pulse(int length);
}

/// <summary>
/// A 3D face of the Avalonia chat (MARI, JEROME, HAL, MARIEKILLEUSE, MUSUKO, KAONAMI, PEPPER, GREEN, JEROMELIVE, MARVIN):
/// the same image on the same grid in relief, with the same shaders (<see cref="FaceShaders"/>, translated by
/// <see cref="SceneShaderTranslator"/>) and the same motion as KaonomiScene: the head sways, the jaw drops with the
/// voice while the answer is read aloud and with the words shown otherwise, a thin band glitches now and then.
/// </summary>
public partial class FaceAvatar : Control, IAvatar
{
    private const float FieldOfView = MathF.PI / 4;
    private const float CameraDistance = 2.55f;
    private const float Decay = 0.92f;          // word energy kept from one frame to the next (at 60 fps)
    private const float MouthAttack = 0.55f;    // share of the way to a louder voice made in one frame
    private const float MouthRelease = 0.2f;    // share of the way to a quieter voice made in one frame
    private const int GridSize = 192;

    private readonly FaceShaders.FaceModel _face;
    private readonly Random _random = new();
    private ShaderMaterial _material = null!;
    private ArrayMesh _mesh = null!;
    private double _time;
    private float _energy, _mouth;
    private float _glitchRow = 99, _glitchShift;
    private double _glitchEnd, _nextGlitch = 3;

    public FaceAvatar(SceneAnimation animation)
    {
        _face = FaceShaders.For(animation);
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
    }

    public Func<float>? VoiceLevel { get; set; }
    public bool Speaking { get; set; }

    /// <summary>The distance of the camera to the face (2.55 usually): smaller, the face comes closer.</summary>
    public float Distance { get; set; } = CameraDistance;

    /// <summary>The point of the face the camera aims at, in its units (the image from -1 to 1, y up): the centre usually.</summary>
    public Vector2 Aim { get; set; }
    public bool Thinking { get; set; }

    /// <summary>A word shown: the mouth opens, wider for longer words.</summary>
    public void Pulse(int length)
    {
        var strength = Math.Clamp(length / 7f, 0.3f, 1f);
        _energy = Math.Min(1, _energy + strength * 0.6f);
    }

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = new Shader { Code = SceneShaderTranslator.Face(_face) } };
        if (LoadTexture(_face.Image) is { } texture)
            _material.SetShaderParameter("uFace", texture);
        Material = _material;
        _mesh = BuildMesh();
        Resized += QueueRedraw;
    }

    public override void _Draw() => DrawMesh(_mesh, null);

    public override void _Process(double delta)
    {
        _time += delta;
        var frames = (float)(delta * 60);

        // Mouth: follows the loudness of the voice while the answer is read aloud, opened by the words otherwise.
        _energy *= MathF.Pow(Decay, frames);
        var voice = Speaking ? Math.Clamp(VoiceLevel?.Invoke() ?? 0, 0, 1) : 0;
        var share = voice > _mouth ? MouthAttack : MouthRelease;
        _mouth += (voice - _mouth) * (1 - MathF.Pow(1 - share, frames));
        var open = Speaking ? _mouth : _energy;

        // A thin band of the image shifts sideways for a fraction of a second, every few seconds.
        if (_time > _nextGlitch)
        {
            _glitchRow = (float)_random.NextDouble();
            _glitchShift = (float)((_random.NextDouble() - 0.5) * 0.04);
            _glitchEnd = _time + 0.1;
            _nextGlitch = _time + 4 + _random.NextDouble() * 8;
        }
        else if (_time > _glitchEnd)
        {
            _glitchRow = 99;
        }

        // The head sways slowly, as if it looked around (the same matrices as KaonomiScene).
        var time = (float)(_time % 3600);
        var size = new Vector2(Math.Max(1, Size.X), Math.Max(1, Size.Y));
        var model = System.Numerics.Matrix4x4.CreateRotationY(0.2f * MathF.Sin(time * 0.37f))
                    * System.Numerics.Matrix4x4.CreateRotationX(0.05f * MathF.Sin(time * 0.29f));
        var view = System.Numerics.Matrix4x4.CreateLookAt(new System.Numerics.Vector3(Aim.X, Aim.Y, Distance),
            new System.Numerics.Vector3(Aim.X, Aim.Y, 0), System.Numerics.Vector3.UnitY);
        var projection = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, size.X / size.Y, 0.02f, 40);
        var m = model * view * projection;
        // Row vectors in System.Numerics: its rows are the columns of the matrix of the shader.
        var mvp = new Projection(new Vector4(m.M11, m.M12, m.M13, m.M14), new Vector4(m.M21, m.M22, m.M23, m.M24),
            new Vector4(m.M31, m.M32, m.M33, m.M34), new Vector4(m.M41, m.M42, m.M43, m.M44));

        _material.SetShaderParameter("uMvp", mvp);
        _material.SetShaderParameter("uSize", size);
        _material.SetShaderParameter("uTime", time);
        _material.SetShaderParameter("uOpen", Math.Clamp(open, 0, 1));
        _material.SetShaderParameter("uGlitch", new Vector3(_glitchRow, _glitchShift, 0));
    }

    /// <summary>
    /// The face image, copied next to the assembly from the Avalonia chat (KAONOMI.Chat/Assets), with premultiplied
    /// alpha and mipmaps as the shaders expect; null when it cannot be read.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<SceneAnimation, ImageTexture?> Pictures = [];

    /// <summary>
    /// The picture of the face of <paramref name="animation"/> (the image of its 3D face, read once), for a thumbnail;
    /// null for JARVIS (a holographic display, no picture) and NONE.
    /// </summary>
    public static ImageTexture? PictureOf(SceneAnimation animation)
    {
        if (animation is SceneAnimation.Jarvis or SceneAnimation.None)
            return null;
        if (Pictures.TryGetValue(animation, out var cached))
            return cached;
        ImageTexture? texture;
        try
        {
            texture = LoadTexture(FaceShaders.For(animation).Image);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UriFormatException)
        {
            texture = null;
        }
        Pictures[animation] = texture;
        return texture;
    }

    private static ImageTexture? LoadTexture(string image)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", Path.GetFileName(new Uri(image).AbsolutePath));
        var picture = Image.LoadFromFile(path);
        if (picture is null || picture.IsEmpty())
        {
            GD.PushWarning($"Face image {path} not loaded");
            return null;
        }
        picture.Convert(Image.Format.Rgba8);
        picture.PremultiplyAlpha();
        picture.GenerateMipmaps();
        return ImageTexture.CreateFromImage(picture);
    }

    /// <summary>
    /// The grid over the image (texture coordinates 0..1, y down): the vertex shader places its points, the positions
    /// given here only keep the mesh from being culled.
    /// </summary>
    private static ArrayMesh BuildMesh()
    {
        const int side = GridSize + 1;
        var positions = new Vector2[side * side];
        var uvs = new Vector2[side * side];
        for (var row = 0; row < side; row++)
            for (var column = 0; column < side; column++)
            {
                var uv = new Vector2(column / (float)GridSize, row / (float)GridSize);
                uvs[row * side + column] = uv;
                positions[row * side + column] = uv * 8192;
            }
        var indexes = new int[GridSize * GridSize * 6];
        var index = 0;
        for (var row = 0; row < GridSize; row++)
            for (var column = 0; column < GridSize; column++)
            {
                var corner = row * side + column;
                foreach (var point in new[] { corner, corner + 1, corner + side + 1, corner, corner + side + 1, corner + side })
                    indexes[index++] = point;
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indexes;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
