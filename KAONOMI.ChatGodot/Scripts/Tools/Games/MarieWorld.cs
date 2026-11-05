using System;
using System.Collections.Generic;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Tools.Games;

/// <summary>
/// The 3D maze of /game:marie, seen through the eyes of the player, in a viewport of its own: stone walls, a dark
/// fog, the flashlight of the player (with shadows), the health packs (green crosses), the speed bonuses (yellow
/// bolts), the weapons (glowing guns) turning in the corridors, the green exit, and MarieKilleuse herself, her picture
/// facing the player in a pink glow. A click in the maze takes the mouse (look) and the keys: ZQSD / WASD or the
/// arrows walk, the click shoots (held: again and again), 1 2 3 or the wheel choose the weapon, M the big map, Esc
/// gives the mouse back (pause). The rules are <see cref="MarieGame"/>; this only draws them.
/// </summary>
public sealed partial class MarieWorld : SubViewportContainer
{
    private const float WallHeight = 3.2f;
    private const float EyeHeight = 1.6f;
    private const float MouseSensitivity = 0.0025f;

    private readonly MarieWindowViewModel _vm;
    private readonly SubViewport _viewport;
    private Node3D _level = new();
    private readonly Camera3D _camera;
    private readonly SpotLight3D _flashlight;
    private readonly OmniLight3D _muzzle;
    private readonly Node3D _gun;
    private readonly StandardMaterial3D _gunMaterial = new() { Roughness = 0.4f, Metallic = 0.6f };
    private readonly StandardMaterial3D _gunGlow = Glow(new Color(0.85f, 0.85f, 0.95f), 0.5f);
    private Sprite3D _marie = null!;
    private OmniLight3D _marieLight = null!;
    private Label3D _marieSleep = null!;
    private readonly Dictionary<MariePickup, Node3D> _pickups = [];
    private Node3D? _exit;

    private MarieGame _game;
    private double _pitch;
    private double _yaw;
    private bool _firing;
    private double _recoil;
    private double _hitFlash;
    private double _bob;
    private double _clock;

    /// <summary>Raised for the screen: an event of the game (shot, hit, caught, pickup…).</summary>
    public event Action<string>? GameEvent;

    /// <summary>Raised when the player takes or gives back the mouse.</summary>
    public event Action<bool>? ActiveChanged;

    public MarieWorld(MarieWindowViewModel vm)
    {
        _vm = vm;
        _game = vm.Game;
        Stretch = true;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(480, 320);

        _viewport = new SubViewport
        {
            OwnWorld3D = true,
            Msaa3D = Viewport.Msaa.Msaa2X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            PhysicsObjectPicking = false
        };
        AddChild(_viewport);

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.01f, 0.008f, 0.02f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.42f, 0.36f, 0.55f),
            AmbientLightEnergy = 0.22f,
            FogEnabled = true,
            FogLightColor = new Color(0.035f, 0.02f, 0.05f),
            FogDensity = 0.055f,
            GlowEnabled = true,
            GlowIntensity = 0.9f,
            GlowBloom = 0.08f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic
        };
        _viewport.AddChild(new WorldEnvironment { Environment = environment });

        _camera = new Camera3D { Fov = 74, Near = 0.05f, Far = 120, Current = true };
        _viewport.AddChild(_camera);
        _flashlight = new SpotLight3D
        {
            SpotRange = 24,
            SpotAngle = 30,
            SpotAngleAttenuation = 0.8f,
            LightEnergy = 5.5f,
            LightColor = new Color(1f, 0.93f, 0.8f),
            ShadowEnabled = true,
            Position = new Vector3(0.15f, -0.1f, 0)
        };
        _camera.AddChild(_flashlight);
        _camera.AddChild(new OmniLight3D { OmniRange = 4.5f, LightEnergy = 0.35f, LightColor = new Color(0.8f, 0.8f, 1f) });
        _muzzle = new OmniLight3D { OmniRange = 9, LightEnergy = 0, LightColor = new Color(1f, 0.75f, 0.35f), Position = new Vector3(0.22f, -0.15f, -0.9f) };
        _camera.AddChild(_muzzle);
        _gun = BuildGun();
        _camera.AddChild(_gun);

        _viewport.AddChild(_level);
        BuildLevel();

        _vm.Started += Restart;
        TreeExiting += () =>
        {
            _vm.Started -= Restart;
            _game.Event -= OnGameEvent;
            Release();
        };
    }

    public MarieGame Game => _game;

    /// <summary>True while the player holds the mouse (the game runs).</summary>
    public bool Active { get; private set; }

    /// <summary>The direction looked at (for the map).</summary>
    public double Yaw => _yaw;

    /// <summary>A red flash of the screen is due (a catch), from 1 down to 0.</summary>
    public double Hurt { get; private set; }

    /// <summary>A hit marker on the cross is due, from 1 down to 0.</summary>
    public double HitMarker { get; private set; }

    // ── The level ──

    private void Restart()
    {
        _game.Event -= OnGameEvent;
        _game = _vm.Game;
        _level.QueueFree();
        _level = new Node3D();
        _viewport.AddChild(_level);
        _pickups.Clear();
        BuildLevel();
        Hurt = 0;
        GameEvent?.Invoke("restart");
    }

    private void BuildLevel()
    {
        _game.Event += OnGameEvent;
        _yaw = _game.Yaw;
        _pitch = 0;
        var maze = _game.Maze;
        var tile = (float)MarieMaze.Tile;
        var size = (float)maze.Size;

        // Walls: one box per wall tile, all in one multimesh.
        var stone = new NoiseTexture2D { Width = 256, Height = 256, Seamless = true, Noise = new FastNoiseLite { Frequency = 0.035f, FractalOctaves = 5 } };
        var bumps = new NoiseTexture2D { Width = 256, Height = 256, Seamless = true, AsNormalMap = true, BumpStrength = 6, Noise = new FastNoiseLite { Frequency = 0.06f, FractalOctaves = 4 } };
        var wallMaterial = new StandardMaterial3D
        {
            AlbedoTexture = stone,
            AlbedoColor = new Color(0.62f, 0.55f, 0.66f),
            NormalEnabled = true,
            NormalTexture = bumps,
            Roughness = 0.9f,
            Uv1Triplanar = true,
            Uv1Scale = new Vector3(0.35f, 0.35f, 0.35f)
        };
        var walls = new List<Vector3>();
        for (var y = 0; y < maze.Side; y++)
            for (var x = 0; x < maze.Side; x++)
                if (maze.IsWall(x, y))
                    walls.Add(new Vector3((x + 0.5f) * tile, WallHeight / 2, (y + 0.5f) * tile));
        var multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new BoxMesh { Size = new Vector3(tile, WallHeight, tile), Material = wallMaterial },
            InstanceCount = walls.Count
        };
        for (var k = 0; k < walls.Count; k++)
            multimesh.SetInstanceTransform(k, new Transform3D(Basis.Identity, walls[k]));
        _level.AddChild(new MultiMeshInstance3D { Multimesh = multimesh });

        // Floor and ceiling.
        var floorMaterial = new StandardMaterial3D
        {
            AlbedoTexture = stone,
            AlbedoColor = new Color(0.32f, 0.28f, 0.3f),
            NormalEnabled = true,
            NormalTexture = bumps,
            Roughness = 0.75f,
            Uv1Triplanar = true,
            Uv1Scale = new Vector3(0.5f, 0.5f, 0.5f)
        };
        _level.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(size, size), Material = floorMaterial }, Position = new Vector3(size / 2, 0, size / 2) });
        var ceilingMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.07f, 0.1f), Roughness = 1 };
        _level.AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(size, size), Material = ceilingMaterial, FlipFaces = true },
            Position = new Vector3(size / 2, WallHeight, size / 2)
        });

        // A few dim lamps of colour in the maze, for the mood.
        var random = new Random(_game.Seed);
        for (var k = 0; k < 14; k++)
        {
            var (cx, cy) = MarieMaze.CellTile(random.Next(maze.CellCount), random.Next(maze.CellCount));
            var (lx, lz) = MarieMaze.Center(cx, cy);
            var colour = k % 2 == 0 ? new Color(0.55f, 0.25f, 0.9f) : new Color(0.25f, 0.6f, 0.9f);
            _level.AddChild(new OmniLight3D { Position = new Vector3((float)lx, WallHeight - 0.4f, (float)lz), OmniRange = 6, LightEnergy = 0.9f, LightColor = colour });
        }

        // The exit: a green ring standing in the last cell.
        var (ex, ez) = MarieMaze.Center(_game.Exit.X, _game.Exit.Y);
        _exit = new Node3D { Position = new Vector3((float)ex, 0, (float)ez) };
        var exitMaterial = Glow(new Color(0.2f, 1f, 0.45f), 3f);
        _exit.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.72f, OuterRadius = 0.86f, Material = exitMaterial }, Position = new Vector3(0, 1.25f, 0), RotationDegrees = new Vector3(90, 0, 0) });
        _exit.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.85f, BottomRadius = 0.85f, Height = 0.03f, Material = Glow(new Color(0.15f, 0.8f, 0.35f), 1.5f) }, Position = new Vector3(0, 0.02f, 0) });
        _exit.AddChild(new OmniLight3D { Position = new Vector3(0, 1.4f, 0), OmniRange = 7, LightEnergy = 2.2f, LightColor = new Color(0.3f, 1f, 0.5f) });
        _exit.AddChild(Label("EXIT", new Color(0.4f, 1f, 0.55f), 2.45f));
        _level.AddChild(_exit);

        foreach (var pickup in _game.Pickups)
        {
            var node = BuildPickup(pickup.Kind);
            var (px, pz) = MarieMaze.Center(pickup.X, pickup.Y);
            node.Position = new Vector3((float)px, 0, (float)pz);
            _level.AddChild(node);
            _pickups[pickup] = node;
        }

        // MarieKilleuse.
        _marie = new Sprite3D
        {
            Texture = GD.Load<Texture2D>("res://Assets/mariekilleuse-face.png"),
            Billboard = BaseMaterial3D.BillboardModeEnum.FixedY,
            PixelSize = 1.9f / 1024,
            Shaded = false,
            AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps
        };
        _level.AddChild(_marie);
        _marieLight = new OmniLight3D { OmniRange = 7.5f, LightEnergy = 1.8f, LightColor = new Color(1f, 0.25f, 0.55f) };
        _level.AddChild(_marieLight);
        _marieSleep = Label("Zzz…", new Color(1f, 0.6f, 0.8f), 0);
        _level.AddChild(_marieSleep);
        Sync(0);
    }

    private static StandardMaterial3D Glow(Color colour, float energy) => new()
    {
        AlbedoColor = colour,
        EmissionEnabled = true,
        Emission = colour,
        EmissionEnergyMultiplier = energy,
        Roughness = 0.3f
    };

    private static Label3D Label(string text, Color colour, float height) => new()
    {
        Text = text,
        Modulate = colour,
        FontSize = 56,
        OutlineSize = 10,
        OutlineModulate = new Color(0, 0, 0, 0.8f),
        PixelSize = 0.0045f,
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        Position = new Vector3(0, height, 0),
        Font = Eve.Eve.Display
    };

    private static Node3D BuildPickup(MariePickupKind kind)
    {
        var root = new Node3D();
        var spin = new Node3D { Name = "Spin", Position = new Vector3(0, 0.9f, 0) };
        root.AddChild(spin);
        Color colour;
        string text;
        switch (kind)
        {
            case MariePickupKind.Health:
                colour = new Color(0.25f, 1f, 0.4f);
                text = "+30 HP";
                var cross = Glow(colour, 2.2f);
                spin.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.16f, 0.16f), Material = cross } });
                spin.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.16f, 0.5f, 0.16f), Material = cross } });
                break;
            case MariePickupKind.Speed:
                colour = new Color(1f, 0.85f, 0.15f);
                text = "SPEED ×2";
                var bolt = Glow(colour, 2.6f);
                spin.AddChild(new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(0.28f, 0.34f, 0.08f), Material = bolt }, Position = new Vector3(0.05f, 0.14f, 0), RotationDegrees = new Vector3(0, 0, 160) });
                spin.AddChild(new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(0.28f, 0.34f, 0.08f), Material = bolt }, Position = new Vector3(-0.05f, -0.14f, 0), RotationDegrees = new Vector3(0, 0, -20) });
                break;
            default:
                (colour, text) = kind switch
                {
                    MariePickupKind.Shotgun => (new Color(1f, 0.55f, 0.2f), "SHOTGUN"),
                    MariePickupKind.Plasma => (new Color(0.3f, 0.85f, 1f), "PLASMA"),
                    _ => (new Color(0.85f, 0.85f, 0.95f), "PISTOL")
                };
                var metal = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.25f, 0.28f), Metallic = 0.8f, Roughness = 0.35f };
                var glow = Glow(colour, 2f);
                var length = kind == MariePickupKind.Pistol ? 0.34f : 0.62f;
                spin.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(length, 0.12f, 0.1f), Material = metal } });
                spin.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.1f, 0.2f, 0.08f), Material = metal }, Position = new Vector3(-length / 2 + 0.08f, -0.13f, 0), RotationDegrees = new Vector3(0, 0, -12) });
                spin.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(length * 0.7f, 0.04f, 0.11f), Material = glow }, Position = new Vector3(0.02f, 0.06f, 0) });
                break;
        }
        root.AddChild(new OmniLight3D { Position = new Vector3(0, 1f, 0), OmniRange = 3.2f, LightEnergy = 1.2f, LightColor = colour });
        root.AddChild(Label(text, colour, 1.45f));
        return root;
    }

    private Node3D BuildGun()
    {
        var gun = new Node3D { Position = new Vector3(0.24f, -0.24f, -0.45f) };
        gun.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.08f, 0.1f, 0.36f), Material = _gunMaterial } });
        gun.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.06f, 0.14f, 0.07f), Material = _gunMaterial }, Position = new Vector3(0, -0.1f, 0.11f), RotationDegrees = new Vector3(-15, 0, 0) });
        gun.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.085f, 0.02f, 0.26f), Material = _gunGlow }, Position = new Vector3(0, 0.055f, -0.02f) });
        return gun;
    }

    // ── Each frame ──

    public override void _Process(double delta)
    {
        _clock += delta;
        if (Active && _game.Outcome == MarieOutcome.Playing)
        {
            double forward = 0, right = 0;
            if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) forward += 1;
            if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) forward -= 1;
            if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) right += 1;
            if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) right -= 1;
            var before = (_game.PlayerX, _game.PlayerZ);
            _game.Update(delta, new MarieInput(forward, right, _yaw, _firing));
            var moved = Math.Abs(_game.PlayerX - before.PlayerX) + Math.Abs(_game.PlayerZ - before.PlayerZ);
            _bob += moved * 4.2;
            foreach (var message in _game.Messages)
                GameEvent?.Invoke("say:" + message);
        }
        Hurt = Math.Max(0, Hurt - delta * 1.6);
        HitMarker = Math.Max(0, HitMarker - delta * 4);
        _recoil = Math.Max(0, _recoil - delta * 6);
        _hitFlash = Math.Max(0, _hitFlash - delta * 5);
        Sync(delta);
    }

    /// <summary>Puts the camera, MarieKilleuse, the pickups and the gun where the game says.</summary>
    private void Sync(double delta)
    {
        var bob = Math.Sin(_bob) * 0.045;
        _camera.Position = new Vector3((float)_game.PlayerX, (float)(EyeHeight + bob), (float)_game.PlayerZ);
        _camera.Rotation = new Vector3((float)_pitch, (float)_yaw, 0);
        _gun.Position = new Vector3(0.24f + (float)(Math.Cos(_bob * 0.5) * 0.01), -0.24f + (float)(Math.Abs(bob) * 0.3), -0.45f + (float)(_recoil * 0.09));
        _gun.RotationDegrees = new Vector3((float)(_recoil * 14), 0, 0);
        _muzzle.LightEnergy = (float)(_recoil > 0.6 ? 7 * (_recoil - 0.6) / 0.4 : 0);
        var colour = _game.Weapon == MarieWeapon.Plasma ? new Color(0.3f, 0.85f, 1f) : _game.Weapon == MarieWeapon.Shotgun ? new Color(1f, 0.55f, 0.2f) : new Color(0.85f, 0.85f, 0.95f);
        _gunGlow.AlbedoColor = colour;
        _gunGlow.Emission = colour;
        _gunMaterial.AlbedoColor = _game.Weapon == MarieWeapon.Shotgun ? new Color(0.35f, 0.24f, 0.16f) : new Color(0.2f, 0.2f, 0.23f);

        // MarieKilleuse: standing, staggering, asleep or down.
        var pulse = (float)(0.5 + 0.5 * Math.Sin(_clock * 5));
        var down = _game.State == MarieState.Down;
        _marie.Position = new Vector3((float)_game.MarieX, down ? 0.32f : 1.02f + 0.04f * pulse, (float)_game.MarieZ);
        _marie.Scale = down ? new Vector3(1.1f, 0.32f, 1) : Vector3.One;
        _marie.Modulate = _hitFlash > 0 ? new Color(1, 0.35f, 0.35f) : down ? new Color(0.45f, 0.45f, 0.5f) : _game.State == MarieState.Asleep ? new Color(0.7f, 0.7f, 0.8f) : Colors.White;
        _marieLight.Position = new Vector3((float)_game.MarieX, 1.3f, (float)_game.MarieZ);
        _marieLight.LightEnergy = down ? 0.4f : 1.4f + 1.2f * pulse;
        _marieSleep.Visible = _game.State == MarieState.Asleep;
        _marieSleep.Position = new Vector3((float)_game.MarieX, 2.25f + 0.1f * pulse, (float)_game.MarieZ);

        foreach (var (pickup, node) in _pickups)
        {
            node.Visible = !pickup.Taken;
            if (node.GetNodeOrNull<Node3D>("Spin") is { } spin)
            {
                spin.Rotation = new Vector3(0, (float)(_clock * 1.8), 0);
                spin.Position = new Vector3(0, 0.9f + 0.1f * (float)Math.Sin(_clock * 2.4 + pickup.X), 0);
            }
        }
        if (_exit?.GetChild(0) is Node3D ring)
            ring.Rotation = new Vector3(Mathf.Pi / 2, (float)(_clock * 0.8), 0);
    }

    private void OnGameEvent(string name)
    {
        switch (name)
        {
            case "shot":
                _recoil = 1;
                break;
            case "hit":
                _hitFlash = 1;
                HitMarker = 1;
                break;
            case "caught":
                Hurt = 1;
                break;
            case "escaped" or "dead":
                Callable.From(Release).CallDeferred();
                break;
        }
        GameEvent?.Invoke(name);
    }

    // ── Mouse and keys ──

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } && !Active && _game.Outcome == MarieOutcome.Playing)
        {
            Take();
            AcceptEvent();
        }
    }

    public override void _Input(InputEvent e)
    {
        // While playing, the input comes through the relay at the root (whatever the place of the window).
        if (Active)
            return;
        // R and N start again after the end, without the mouse.
        if (_game.Outcome != MarieOutcome.Playing && IsVisibleInTree() && e is InputEventKey { Pressed: true, Echo: false } end
            && (end.PhysicalKeycode is Key.R or Key.N) && HasFocusWithin())
        {
            _vm.NewGameCommand.Execute(null);
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>An input event while playing (from <see cref="InputRelay"/>): look, shoot, weapons, map, pause.</summary>
    private void Play(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion motion:
                _yaw -= motion.Relative.X * MouseSensitivity;
                _pitch = Math.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -1.25, 1.25);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _firing = button.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _game.Cycle(1);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _game.Cycle(-1);
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                switch (key.PhysicalKeycode)
                {
                    case Key.Escape:
                        Release();
                        break;
                    case Key.Key1:
                        _game.Select(0);
                        break;
                    case Key.Key2:
                        _game.Select(1);
                        break;
                    case Key.Key3:
                        _game.Select(2);
                        break;
                    case Key.M or Key.Tab:
                        GameEvent?.Invoke("map");
                        break;
                    case Key.Space:
                        _firing = true;
                        break;
                }
                break;
            case InputEventKey { Pressed: false, PhysicalKeycode: Key.Space }:
                _firing = false;
                break;
        }
    }

    /// <summary>
    /// Put at the root of the application while playing: it gets every input event first (the window of the game may
    /// be anywhere, the mouse captured at the middle of the screen), gives it to the game and keeps it from the chat.
    /// </summary>
    private sealed partial class InputRelay(MarieWorld world) : Node
    {
        public override void _Input(InputEvent e)
        {
            if (!IsInstanceValid(world) || !world.Active)
                return;
            world.Play(e);
            GetViewport().SetInputAsHandled();
        }
    }

    private InputRelay? _relay;

    private bool HasFocusWithin()
    {
        var focus = GetViewport().GuiGetFocusOwner();
        return focus == this || focus is null;
    }

    /// <summary>Turns the eyes of the player (the yaw: 0 towards -Z; the pitch: up and down).</summary>
    public void Look(double yaw, double pitch = 0)
    {
        _yaw = yaw;
        _pitch = Math.Clamp(pitch, -1.25, 1.25);
    }

    /// <summary>Takes the mouse: the game runs.</summary>
    public void Take()
    {
        if (Active || _game.Outcome != MarieOutcome.Playing)
            return;
        GrabFocus();
        Active = true;
        if (_relay is null)
        {
            _relay = new InputRelay(this);
            GetTree().Root.CallDeferred(Node.MethodName.AddChild, _relay);
        }
        Input.MouseMode = Input.MouseModeEnum.Captured;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        ActiveChanged?.Invoke(true);
    }

    /// <summary>Gives the mouse back: the game waits.</summary>
    public void Release()
    {
        _firing = false;
        if (!Active)
            return;
        Active = false;
        _relay?.QueueFree();
        _relay = null;
        if (Input.MouseMode == Input.MouseModeEnum.Captured)
            Input.MouseMode = Input.MouseModeEnum.Visible;
        ActiveChanged?.Invoke(false);
    }

    public override void _Notification(int what)
    {
        // Another application in front, or the window hidden: pause.
        if (what == NotificationApplicationFocusOut || (what == NotificationVisibilityChanged && !IsVisibleInTree()))
            Release();
    }
}
