using System.Collections.Generic;
using Godot;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// The 3D shelf behind the ribbon of the tape: a scene of Godot (its own world, a camera looking straight at it, a
/// key light and a lamp that follows the pointer, a glow) drawn in a viewport laid under the strip of tools. Each big
/// tool of the ribbon (<see cref="Track"/>) gets a slab of glass (<c>ribbon_tile.gdshader</c>) placed exactly under its
/// button, which tilts towards the pointer and lifts when the tool is hovered, sinks while it is pressed, and follows
/// the button when the strip scrolls or the window is resized. The buttons stay 2D, for the clicks, the icons, the
/// names and the tooltips; this draws the material under them.
/// </summary>
public partial class RibbonShelf : SubViewportContainer
{
    private const float CameraDistance = 400;
    private const float Tilt = 0.12f;

    private static readonly Shader TileShader = GD.Load<Shader>("res://Shaders/ribbon_tile.gdshader");

    private sealed class Tile
    {
        public required Button Button { get; init; }
        public required MeshInstance3D Mesh { get; init; }
        public required ShaderMaterial Material { get; init; }
        public float Hover, Pressed, Streak;
        public Vector2 Tilt;
        public float Lift;
    }

    private readonly SubViewport _scene;
    private readonly Camera3D _camera;
    private readonly OmniLight3D _lamp;
    private readonly Node3D _tiles;
    private readonly List<Tile> _list = [];

    public RibbonShelf()
    {
        Stretch = true;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _scene = new SubViewport
        {
            OwnWorld3D = true,
            TransparentBg = true,
            Msaa3D = Viewport.Msaa.Msaa4X,
            HandleInputLocally = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenParentVisible
        };
        AddChild(_scene);
        // A sky of deep blue and cold light, seen only in the reflections of the glass (the background stays transparent).
        var sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.08f, 0.09f, 0.11f),
                SkyHorizonColor = new Color(0.55f, 0.58f, 0.62f),
                GroundHorizonColor = new Color(0.35f, 0.37f, 0.4f),
                GroundBottomColor = new Color(0.03f, 0.03f, 0.04f),
                SunAngleMax = 20,
                SunCurve = 0.3f
            }
        };
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0, 0, 0, 0),
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.6f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            GlowEnabled = true,
            GlowIntensity = 0.35f,
            GlowBloom = 0f,
            GlowHdrThreshold = 1.0f
        };
        _scene.AddChild(new WorldEnvironment { Environment = environment });
        _camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, KeepAspect = Camera3D.KeepAspectEnum.Height, Current = true, Near = 1, Far = 1000 };
        _camera.Position = new Vector3(0, 0, CameraDistance);
        _scene.AddChild(_camera);
        // A key light from the top left, and the lamp that follows the pointer.
        var key = new DirectionalLight3D { LightEnergy = 0.8f, LightColor = new Color(0.96f, 0.97f, 1f) };
        key.RotationDegrees = new Vector3(-35, -30, 0);
        _scene.AddChild(key);
        _lamp = new OmniLight3D { LightEnergy = 1.2f, OmniRange = 240, OmniAttenuation = 1.2f, LightColor = new Color(0.95f, 0.97f, 1f) };
        _scene.AddChild(_lamp);
        _tiles = new Node3D();
        _scene.AddChild(_tiles);
    }

    /// <summary>Gives <paramref name="button"/> its slab of glass.</summary>
    public void Track(Button button)
    {
        var material = new ShaderMaterial { Shader = TileShader };
        var mesh = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(56, 56) }, MaterialOverride = material };
        _tiles.AddChild(mesh);
        _list.Add(new Tile { Button = button, Mesh = mesh, Material = material });
        Restyle();
    }

    /// <summary>The colours of the theme in use (called again when the theme changes).</summary>
    public void Restyle()
    {
        foreach (var tile in _list)
        {
            tile.Material.SetShaderParameter("light_theme", Eve.Eve.IsLight);
            tile.Material.SetShaderParameter("glass", Eve.Eve.IsLight ? new Color(1f, 1f, 1f, 0.5f) : new Color(0.10f, 0.11f, 0.13f, 0.36f));
            tile.Material.SetShaderParameter("edge_color", Eve.Eve.IsLight ? new Color(0.2f, 0.22f, 0.25f) : new Color(1f, 1f, 1f));
        }
    }

    public override void _Process(double delta)
    {
        var size = Size;
        if (size.X < 2 || size.Y < 2)
            return;
        _camera.Size = size.Y;
        var origin = GetGlobalRect().Position;
        var pointer = GetGlobalMousePosition() - origin;
        _lamp.Position = new Vector3(pointer.X - size.X / 2, size.Y / 2 - pointer.Y, 70);
        var step = (float)Mathf.Min(1, delta * 12);
        foreach (var tile in _list)
        {
            var button = tile.Button;
            var shown = button.IsVisibleInTree();
            tile.Mesh.Visible = shown;
            if (!shown)
                continue;
            var rect = button.GetGlobalRect();
            var local = rect.Position - origin;
            var center = local + rect.Size / 2;
            ((QuadMesh)tile.Mesh.Mesh).Size = rect.Size - new Vector2(4, 4);
            var mode = button.GetDrawMode();
            var hovered = mode is BaseButton.DrawMode.Hover or BaseButton.DrawMode.HoverPressed;
            var pressed = mode is BaseButton.DrawMode.Pressed or BaseButton.DrawMode.HoverPressed;
            var hoverTarget = hovered ? 1f : 0f;
            var pressedTarget = pressed ? 1f : 0f;
            tile.Hover = Mathf.Lerp(tile.Hover, hoverTarget, step);
            tile.Pressed = Mathf.Lerp(tile.Pressed, pressedTarget, step);
            // Towards the pointer while hovered: the slab tilts up to Tilt radians and lifts; pressed: it sinks flat.
            var offset = hovered ? ((pointer - center) / (rect.Size / 2)).Clamp(-Vector2.One, Vector2.One) : Vector2.Zero;
            var tiltTarget = new Vector2(-offset.Y, offset.X) * Tilt * (1 - pressedTarget * 0.7f);
            tile.Tilt = tile.Tilt.Lerp(tiltTarget, step);
            tile.Lift = Mathf.Lerp(tile.Lift, hovered ? (pressed ? -3 : 5) : 0, step);
            tile.Mesh.Position = new Vector3(center.X - size.X / 2, size.Y / 2 - center.Y, tile.Lift);
            tile.Mesh.Rotation = new Vector3(tile.Tilt.X, tile.Tilt.Y, 0);
            // The reflection slides from the left to the right of the slab as the pointer comes over it.
            tile.Streak = Mathf.Lerp(tile.Streak, hovered ? 0.5f + offset.X * 0.5f : -0.3f, step * 0.7f);
            tile.Material.SetShaderParameter("hover", tile.Hover);
            tile.Material.SetShaderParameter("pressed", tile.Pressed);
            tile.Material.SetShaderParameter("streak", tile.Streak);
        }
    }
}
