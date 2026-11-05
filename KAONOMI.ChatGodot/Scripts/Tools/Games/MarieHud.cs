using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Tools.Games;

/// <summary>
/// What is drawn over the maze of /game:marie: the cross (and a mark on a hit), the life points, the speed bonus, the
/// weapons and their ammunition, the clock, where MarieKilleuse is (asleep, hunting and how far by the corridors,
/// down), the messages of the game, a red veil that beats faster as she comes near (and flashes at a catch), the map
/// of what was explored (M: big), and the screens of the start, the pause and the end.
/// </summary>
public sealed partial class MarieHud : Control
{
    private readonly MarieWindowViewModel _vm;
    private readonly MarieWorld _world;
    private readonly ColorRect _veil;
    private readonly ShaderMaterial _veilMaterial;
    private readonly List<(string Text, double Time)> _messages = [];
    private double _clock;
    private bool _bigMap;
    private int _rank;

    private const string VeilShader = """
        shader_type canvas_item;
        uniform float strength = 0.0;
        void fragment() {
            vec2 d = UV - 0.5;
            float v = smoothstep(0.28, 0.78, length(d) * 1.35);
            COLOR = vec4(0.75, 0.0, 0.06, v * strength);
        }
        """;

    public MarieHud(MarieWindowViewModel vm, MarieWorld world)
    {
        _vm = vm;
        _world = world;
        MouseFilter = MouseFilterEnum.Ignore;
        _veilMaterial = new ShaderMaterial { Shader = new Shader { Code = VeilShader } };
        _veil = new ColorRect { Material = _veilMaterial, MouseFilter = MouseFilterEnum.Ignore };
        _veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_veil);
        world.GameEvent += OnEvent;
        TreeExiting += () => world.GameEvent -= OnEvent;
    }

    private MarieGame Game => _world.Game;

    private void OnEvent(string name)
    {
        if (name.StartsWith("say:", StringComparison.Ordinal))
        {
            _messages.Add((name[4..], _clock));
            if (_messages.Count > 5)
                _messages.RemoveAt(0);
        }
        else if (name == "map")
            _bigMap = !_bigMap;
        else if (name == "restart")
        {
            _messages.Clear();
            _rank = 0;
        }
        else if (name == "escaped")
            _rank = _vm.Record();
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        // The veil: a heartbeat when she is near by the corridors, a flash at a catch.
        var near = 0.0;
        if (Game.State is MarieState.Hunting or MarieState.Resting && Game.MarieTiles >= 0)
            near = Math.Clamp(1 - Game.MarieTiles / 16.0, 0, 1);
        var beat = near > 0 ? Math.Pow(Math.Max(0, Math.Sin(_clock * (3 + near * 7))), 6) : 0;
        _veilMaterial.SetShaderParameter("strength", (float)Math.Max(_world.Hurt * 0.9, near * (0.25 + 0.35 * beat)));
        QueueRedraw();
    }

    // ── Drawing ──

    public override void _Draw()
    {
        var game = Game;
        var size = Size;
        var center = size / 2;
        var font = Eve.Eve.Display;
        var body = Eve.Eve.Body;

        // The cross, and a mark on a hit.
        var cross = new Color(1, 1, 1, 0.8f);
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            DrawLine(center + new Vector2(dx * 5, dy * 5), center + new Vector2(dx * 12, dy * 12), cross, 2);
        if (_world.HitMarker > 0)
        {
            var hit = new Color(1, 0.3f, 0.3f, (float)_world.HitMarker);
            foreach (var (dx, dy) in new[] { (1, 1), (-1, 1), (1, -1), (-1, -1) })
                DrawLine(center + new Vector2(dx * 7, dy * 7), center + new Vector2(dx * 16, dy * 16), hit, 3);
        }

        // Life points and the speed bonus, at the bottom left.
        var left = new Vector2(20, size.Y - 74);
        Bar(left, 230, 18, game.Health / (float)MarieGame.MaxHealth, game.Health > 35 ? new Color(0.3f, 0.95f, 0.45f) : new Color(1f, 0.3f, 0.25f));
        Text(font, left + new Vector2(240, 15), $"♥ {game.Health}", 18, Colors.White);
        if (game.Boost > 0)
        {
            Bar(left + new Vector2(0, 30), 230, 10, (float)(game.Boost / MarieGame.BoostSeconds), new Color(1f, 0.85f, 0.2f));
            Text(body, left + new Vector2(240, 41), $"SPEED ×2 · {game.Boost:0} s", 13, new Color(1f, 0.88f, 0.35f));
        }
        else
            Text(body, left + new Vector2(0, 46), "Speed ×0.9 of MarieKilleuse: find the yellow bolts (×2)", 12, new Color(1, 1, 1, 0.55f));

        // The weapons, at the bottom right, on a dark plate (the gun is drawn behind).
        DrawRect(new Rect2(new Vector2(size.X - 244, size.Y - 96), new Vector2(232, 86)), new Color(0, 0, 0, 0.55f));
        var y = size.Y - 24;
        for (var k = MarieWeapon.All.Count - 1; k >= 0; k--)
        {
            var weapon = MarieWeapon.All[k];
            var ammo = game.Ammo[weapon];
            var chosen = weapon == game.Weapon;
            var colour = chosen ? new Color(1f, 0.85f, 0.4f) : ammo > 0 ? new Color(1, 1, 1, 0.75f) : new Color(1, 1, 1, 0.3f);
            var line = $"{k + 1}  {weapon.Name.ToUpperInvariant()}  {ammo}";
            Text(chosen ? font : body, new Vector2(size.X - 230, y), (chosen ? "▶ " : "   ") + line, chosen ? 17 : 14, colour);
            y -= 24;
        }

        // The clock and MarieKilleuse, at the top.
        Text(font, new Vector2(20, 34), $"{(int)game.Time / 60:00}:{(int)game.Time % 60:00}", 22, Colors.White);
        var (state, stateColour) = game.State switch
        {
            MarieState.Asleep => ($"MarieKilleuse wakes up in {Math.Ceiling(game.StateTime):0} s", new Color(1f, 0.7f, 0.85f)),
            MarieState.Down => ($"MarieKilleuse is down: {Math.Ceiling(game.StateTime):0} s", new Color(0.6f, 1f, 0.6f)),
            _ => ($"MarieKilleuse: {game.MarieTiles * MarieMaze.Tile:0} m away by the corridors", game.MarieTiles < 10 ? new Color(1f, 0.35f, 0.4f) : new Color(1f, 0.7f, 0.8f))
        };
        Text(body, new Vector2(20, 60), state, 15, stateColour);
        Text(body, new Vector2(20, 82), $"Knocked down: {game.Knockdowns}", 13, new Color(1, 1, 1, 0.6f));
        if (game.State != MarieState.Down && MarieInSight(game))
        {
            Bar(new Vector2(center.X - 120, 22), 240, 8, game.MarieHealth / (float)MarieGame.MarieMaxHealth, new Color(1f, 0.3f, 0.55f));
            Text(body, new Vector2(center.X - 120, 46), "MarieKilleuse", 13, new Color(1f, 0.6f, 0.8f), 240);
        }

        // The messages, fading.
        var my = size.Y * 0.28f;
        foreach (var (text, time) in _messages)
        {
            var age = _clock - time;
            if (age > 4)
                continue;
            var alpha = (float)Math.Clamp(4 - age, 0, 1);
            Text(font, new Vector2(0, my), text, 19, new Color(1, 0.95f, 0.85f, alpha), size.X);
            my += 28;
        }

        DrawMap();

        // The screens of the start, the pause and the end.
        if (game.Outcome == MarieOutcome.Escaped)
            Screen("YOU ESCAPED!", new Color(0.4f, 1f, 0.55f),
                [$"Time {Clock(game.Time)} · {game.Health} life points left · MarieKilleuse knocked down {game.Knockdowns} time(s)",
                 _rank > 0 ? $"#{_rank} of your best escapes" : "Not among your 10 best escapes", "", "NEW GAME (or R) for another maze"]);
        else if (game.Outcome == MarieOutcome.Caught)
            Screen("MARIEKILLEUSE CAUGHT YOU", new Color(1f, 0.3f, 0.4f),
                [$"You held {Clock(game.Time)} · she was knocked down {game.Knockdowns} time(s)", "", "NEW GAME (or R) to try again"]);
        else if (!_world.Active)
            Screen(game.Time == 0 ? "MARIEKILLEUSE" : "PAUSE", new Color(1f, 0.45f, 0.7f),
                [game.Time == 0 ? "Find the exit of the maze, at the far corner. MarieKilleuse hunts you and knows the way." : "The game waits for you.",
                 "Green crosses: +30 life points · yellow bolts: twice as fast as her for 12 s · guns: knock her down for a while",
                 "", "ZQSD / WASD or arrows: walk · mouse: look · click (held) or space: shoot · 1 2 3 or wheel: weapon · M: map · Esc: pause",
                 "", "CLICK TO PLAY", .. BestLines()]);
    }

    private IEnumerable<string> BestLines()
    {
        if (_vm.Best.Count == 0)
            yield break;
        yield return "";
        yield return "Best escapes: " + string.Join(" · ", _vm.Best.Take(5).Select((r, i) => $"{i + 1}. {Clock(r.Seconds)}"));
    }

    private static string Clock(double seconds) => $"{(int)seconds / 60:00}:{(int)seconds % 60:00}";

    private bool MarieInSight(MarieGame game)
    {
        if (game.MarieDistance > 24 || !game.Maze.CanSee(game.PlayerX, game.PlayerZ, game.MarieX, game.MarieZ))
            return false;
        var dx = game.MarieX - game.PlayerX;
        var dz = game.MarieZ - game.PlayerZ;
        return dx * -Math.Sin(_world.Yaw) + dz * -Math.Cos(_world.Yaw) > 0;
    }

    private void DrawMap()
    {
        var game = Game;
        var maze = game.Maze;
        var side = _bigMap ? Math.Min(Size.X, Size.Y) * 0.78f : 190f;
        var origin = _bigMap ? (Size - new Vector2(side, side)) / 2 : new Vector2(Size.X - side - 16, 16);
        var cell = side / maze.Side;
        DrawRect(new Rect2(origin - new Vector2(4, 4), new Vector2(side + 8, side + 8)), new Color(0, 0, 0, _bigMap ? 0.78f : 0.5f));
        for (var y = 0; y < maze.Side; y++)
            for (var x = 0; x < maze.Side; x++)
            {
                if (!game.Explored(x, y))
                    continue;
                var colour = maze.IsWall(x, y) ? new Color(0.55f, 0.45f, 0.65f, 0.9f) : new Color(0.16f, 0.13f, 0.2f, 0.9f);
                DrawRect(new Rect2(origin + new Vector2(x * cell, y * cell), new Vector2(cell + 0.4f, cell + 0.4f)), colour);
            }
        foreach (var pickup in game.Pickups.Where(p => !p.Taken && game.Explored(p.X, p.Y)))
        {
            var colour = pickup.Kind switch
            {
                MariePickupKind.Health => new Color(0.3f, 1f, 0.45f),
                MariePickupKind.Speed => new Color(1f, 0.85f, 0.2f),
                _ => new Color(0.4f, 0.85f, 1f)
            };
            DrawCircle(origin + new Vector2((pickup.X + 0.5f) * cell, (pickup.Y + 0.5f) * cell), Math.Max(1.5f, cell * 0.45f), colour);
        }
        if (game.Explored(game.Exit.X, game.Exit.Y))
            DrawRect(new Rect2(origin + new Vector2(game.Exit.X * cell, game.Exit.Y * cell), new Vector2(cell, cell)), new Color(0.3f, 1f, 0.5f));
        var scale = cell / (float)MarieMaze.Tile;
        if (game.State == MarieState.Down || MarieInSight(game))
            DrawCircle(origin + new Vector2((float)game.MarieX, (float)game.MarieZ) * scale, Math.Max(2.5f, cell * 0.7f), new Color(1f, 0.3f, 0.6f));
        // The player: a triangle along the direction looked at.
        var at = origin + new Vector2((float)game.PlayerX, (float)game.PlayerZ) * scale;
        var forward = new Vector2((float)-Math.Sin(_world.Yaw), (float)-Math.Cos(_world.Yaw));
        var across = new Vector2(-forward.Y, forward.X);
        var length = Math.Max(5f, cell * 1.4f);
        DrawColoredPolygon([at + forward * length, at - forward * length * 0.5f + across * length * 0.55f, at - forward * length * 0.5f - across * length * 0.55f], Colors.White);
        if (_bigMap)
            Text(Eve.Eve.Body, origin + new Vector2(0, side + 22), "Only what you have seen · green: the exit · pink: MarieKilleuse in sight · M: close", 13, new Color(1, 1, 1, 0.7f), side);
    }

    private void Screen(string title, Color colour, IReadOnlyList<string> lines)
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.02f, 0.01f, 0.03f, 0.72f));
        var y = Size.Y * 0.32f;
        Text(Eve.Eve.Display, new Vector2(0, y), title, 40, colour, Size.X);
        y += 50;
        foreach (var line in lines)
        {
            if (line.Length > 0)
                Text(line == "CLICK TO PLAY" ? Eve.Eve.Display : Eve.Eve.Body, new Vector2(0, y), line, line == "CLICK TO PLAY" ? 24 : 15,
                    line == "CLICK TO PLAY" ? new Color(1f, 0.85f, 0.4f, 0.6f + 0.4f * (float)Math.Abs(Math.Sin(_clock * 2.5))) : new Color(1, 1, 1, 0.85f), Size.X);
            y += line.Length > 0 ? 26 : 12;
        }
    }

    private void Bar(Vector2 at, float width, float height, float fraction, Color colour)
    {
        DrawRect(new Rect2(at, new Vector2(width, height)), new Color(0, 0, 0, 0.55f));
        DrawRect(new Rect2(at + new Vector2(2, 2), new Vector2((width - 4) * Math.Clamp(fraction, 0, 1), height - 4)), colour);
    }

    /// <summary>A text with a dark outline (readable over the maze), centred in <paramref name="width"/> when given.</summary>
    private void Text(Font font, Vector2 at, string text, int size, Color colour, float width = -1)
    {
        var align = width > 0 ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        DrawStringOutline(font, at, text, align, width, size, 5, new Color(0, 0, 0, colour.A * 0.85f));
        DrawString(font, at, text, align, width, size, colour);
    }
}
