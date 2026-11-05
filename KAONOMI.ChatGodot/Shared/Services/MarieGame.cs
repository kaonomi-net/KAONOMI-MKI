using System;
using System.Collections.Generic;
using System.Linq;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The maze of /game:marie: <see cref="Cells"/> × <see cref="Cells"/> cells carved by a depth-first search (a perfect
/// maze), then a few walls opened to make loops (several ways around MarieKilleuse). It is kept as a grid of tiles,
/// (2 × cells + 1) on each side: a cell or an opening between two cells is a floor tile, the rest are walls.
/// </summary>
public sealed class MarieMaze
{
    public const int Cells = 25;

    /// <summary>The side of a tile, in metres.</summary>
    public const double Tile = 2.0;

    private readonly bool[,] _wall;

    public MarieMaze(int seed, int cells = Cells, double loops = 0.08)
    {
        CellCount = cells;
        Side = cells * 2 + 1;
        _wall = new bool[Side, Side];
        for (var y = 0; y < Side; y++)
            for (var x = 0; x < Side; x++)
                _wall[x, y] = true;
        var random = new Random(seed);
        var visited = new bool[cells, cells];
        var stack = new Stack<(int X, int Y)>();
        stack.Push((0, 0));
        visited[0, 0] = true;
        Open(0, 0);
        (int Dx, int Dy)[] steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Peek();
            var next = steps.Select(s => (X: cx + s.Dx, Y: cy + s.Dy))
                .Where(c => c.X >= 0 && c.Y >= 0 && c.X < cells && c.Y < cells && !visited[c.X, c.Y]).ToList();
            if (next.Count == 0)
            {
                stack.Pop();
                continue;
            }
            var (nx, ny) = next[random.Next(next.Count)];
            visited[nx, ny] = true;
            Open(nx, ny);
            _wall[cx + nx + 1, cy + ny + 1] = false;
            stack.Push((nx, ny));
        }
        // Loops: some inner walls between two cells opened.
        var inner = new List<(int X, int Y)>();
        for (var y = 1; y < Side - 1; y++)
            for (var x = 1; x < Side - 1; x++)
                if (_wall[x, y] && (x % 2 == 1) != (y % 2 == 1))
                    inner.Add((x, y));
        foreach (var (x, y) in inner.OrderBy(_ => random.Next()).Take((int)(inner.Count * loops)))
            _wall[x, y] = false;
    }

    public int CellCount { get; }

    /// <summary>The tiles on each side (2 × cells + 1).</summary>
    public int Side { get; }

    /// <summary>The size of the maze in metres.</summary>
    public double Size => Side * Tile;

    public bool IsWall(int x, int y) => x < 0 || y < 0 || x >= Side || y >= Side || _wall[x, y];

    private void Open(int cx, int cy) => _wall[cx * 2 + 1, cy * 2 + 1] = false;

    /// <summary>The tile of the cell (cx, cy).</summary>
    public static (int X, int Y) CellTile(int cx, int cy) => (cx * 2 + 1, cy * 2 + 1);

    /// <summary>The centre of a tile, in metres.</summary>
    public static (double X, double Z) Center(int x, int y) => ((x + 0.5) * Tile, (y + 0.5) * Tile);

    /// <summary>The tile under a point.</summary>
    public static (int X, int Y) TileAt(double x, double z) => ((int)Math.Floor(x / Tile), (int)Math.Floor(z / Tile));

    /// <summary>
    /// Moves a disc of <paramref name="radius"/> from (x, z) by (dx, dz), sliding along the walls: in small steps,
    /// each wall tile it overlaps pushes it back out along the line from its nearest point (so it slides along the
    /// walls and around the corners).
    /// </summary>
    public (double X, double Z) Move(double x, double z, double dx, double dz, double radius)
    {
        // Small steps, so that a fast move never jumps over a wall.
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dz)) / (radius * 0.5)));
        for (var k = 0; k < steps; k++)
        {
            x += dx / steps;
            z += dz / steps;
            for (var pass = 0; pass < 2; pass++)
                (x, z) = PushOut(x, z, radius);
        }
        return (x, z);
    }

    private (double X, double Z) PushOut(double x, double z, double radius)
    {
        var (tx, tz) = TileAt(x, z);
        for (var oy = -1; oy <= 1; oy++)
        {
            for (var ox = -1; ox <= 1; ox++)
            {
                if (!IsWall(tx + ox, tz + oy))
                    continue;
                var minX = (tx + ox) * Tile;
                var minZ = (tz + oy) * Tile;
                var nearestX = Math.Clamp(x, minX, minX + Tile);
                var nearestZ = Math.Clamp(z, minZ, minZ + Tile);
                var ddx = x - nearestX;
                var ddz = z - nearestZ;
                var distance = Math.Sqrt(ddx * ddx + ddz * ddz);
                if (distance >= radius)
                    continue;
                if (distance > 1e-9)
                {
                    x = nearestX + ddx / distance * radius;
                    z = nearestZ + ddz / distance * radius;
                }
                else
                {
                    // The centre inside the wall (never from a legal move): out by the nearest side.
                    var left = x - minX;
                    var right = minX + Tile - x;
                    var top = z - minZ;
                    var bottom = minZ + Tile - z;
                    var least = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
                    if (least == left) x = minX - radius;
                    else if (least == right) x = minX + Tile + radius;
                    else if (least == top) z = minZ - radius;
                    else z = minZ + Tile + radius;
                }
            }
        }
        return (x, z);
    }

    /// <summary>The distance from (x, z) along the direction (dx, dz) (normalised) to the first wall (DDA on the tiles).</summary>
    public double RayToWall(double x, double z, double dx, double dz, double max = 200)
    {
        var (tx, tz) = TileAt(x, z);
        var stepX = Math.Sign(dx);
        var stepZ = Math.Sign(dz);
        var tMaxX = dx == 0 ? double.MaxValue : ((stepX > 0 ? tx + 1 : tx) * Tile - x) / dx;
        var tMaxZ = dz == 0 ? double.MaxValue : ((stepZ > 0 ? tz + 1 : tz) * Tile - z) / dz;
        var tDeltaX = dx == 0 ? double.MaxValue : Tile / Math.Abs(dx);
        var tDeltaZ = dz == 0 ? double.MaxValue : Tile / Math.Abs(dz);
        double t = 0;
        while (t < max)
        {
            if (tMaxX < tMaxZ)
            {
                t = tMaxX;
                tMaxX += tDeltaX;
                tx += stepX;
            }
            else
            {
                t = tMaxZ;
                tMaxZ += tDeltaZ;
                tz += stepZ;
            }
            if (IsWall(tx, tz))
                return t;
        }
        return max;
    }

    /// <summary>True when nothing stands between the two points.</summary>
    public bool CanSee(double x1, double z1, double x2, double z2)
    {
        var dx = x2 - x1;
        var dz = z2 - z1;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (distance < 1e-6)
            return true;
        return RayToWall(x1, z1, dx / distance, dz / distance, distance + 1) >= distance;
    }

    /// <summary>The distances in tiles from <paramref name="from"/> to every floor tile (breadth-first), -1 where it cannot go.</summary>
    public int[,] Distances((int X, int Y) from)
    {
        var distance = new int[Side, Side];
        for (var y = 0; y < Side; y++)
            for (var x = 0; x < Side; x++)
                distance[x, y] = -1;
        if (IsWall(from.X, from.Y))
            return distance;
        var queue = new Queue<(int X, int Y)>();
        distance[from.X, from.Y] = 0;
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (IsWall(nx, ny) || distance[nx, ny] >= 0)
                    continue;
                distance[nx, ny] = distance[x, y] + 1;
                queue.Enqueue((nx, ny));
            }
        }
        return distance;
    }

    /// <summary>The tile next to <paramref name="from"/> on a shortest way to the target whose distances are <paramref name="toTarget"/>.</summary>
    public (int X, int Y) NextStep((int X, int Y) from, int[,] toTarget)
    {
        var best = from;
        var bestDistance = toTarget[from.X, from.Y] < 0 ? int.MaxValue : toTarget[from.X, from.Y];
        foreach (var (nx, ny) in new[] { (from.X + 1, from.Y), (from.X - 1, from.Y), (from.X, from.Y + 1), (from.X, from.Y - 1) })
        {
            if (IsWall(nx, ny) || toTarget[nx, ny] < 0 || toTarget[nx, ny] >= bestDistance)
                continue;
            best = (nx, ny);
            bestDistance = toTarget[nx, ny];
        }
        return best;
    }
}

/// <summary>What lies in the maze to be picked up.</summary>
public enum MariePickupKind
{
    Health,
    Speed,
    Pistol,
    Shotgun,
    Plasma
}

public sealed class MariePickup(MariePickupKind kind, int x, int y)
{
    public MariePickupKind Kind { get; } = kind;

    /// <summary>Its tile.</summary>
    public int X { get; } = x;

    public int Y { get; } = y;

    public bool Taken { get; set; }
}

/// <summary>A weapon: its damage, how often it fires, how far, and the ammunition a pickup gives.</summary>
public sealed record MarieWeapon(string Name, int Damage, double Cooldown, double Range, double Spread, int AmmoPerPickup)
{
    public static readonly MarieWeapon Pistol = new("Pistol", 18, 0.35, 30, 4, 10);
    public static readonly MarieWeapon Shotgun = new("Shotgun", 55, 0.9, 9, 10, 5);
    public static readonly MarieWeapon Plasma = new("Plasma gun", 110, 1.3, 45, 3, 3);

    public static IReadOnlyList<MarieWeapon> All { get; } = [Pistol, Shotgun, Plasma];
}

public enum MarieState
{
    /// <summary>Waiting at the start of the game, before the hunt.</summary>
    Asleep,

    Hunting,

    /// <summary>She just caught the player and lets go for a moment.</summary>
    Resting,

    /// <summary>Knocked out by the weapons: down for a while, then back far away.</summary>
    Down
}

public enum MarieOutcome
{
    Playing,
    Escaped,
    Caught
}

/// <summary>What the player does during a step: the move (forward and right, -1 to 1), the direction looked at, a shot.</summary>
public readonly record struct MarieInput(double Forward, double Right, double Yaw, bool Fire);

/// <summary>
/// /game:marie: the player walks the maze from its corner to the exit at the opposite corner, hunted by
/// MarieKilleuse, who always knows the shortest way to them. Normally the player is a bit slower than her; a speed
/// bonus makes them twice as fast as her for a while. She takes life points at each catch; the health packs give some
/// back; the weapons (pistol, shotgun, plasma gun) knock her down for a while, after which she comes back from far
/// away. The whole game is here, step by step (<see cref="Update"/>), the view only draws it.
/// </summary>
public sealed class MarieGame
{
    public const double MarieSpeed = 3.0;
    public const double PlayerSpeed = MarieSpeed * 0.9;
    public const double BoostSpeed = MarieSpeed * 2;
    public const double BoostSeconds = 12;
    public const int MaxHealth = 100;
    public const int HealthPack = 30;
    public const int CatchDamage = 20;
    public const double CatchDistance = 1.0;
    public const double RestSeconds = 2.0;

    /// <summary>After a catch, the player runs at the bonus speed for so long (adrenaline), to get away.</summary>
    public const double AdrenalineSeconds = 2.5;
    public const int MarieMaxHealth = 150;
    public const double DownSeconds = 12;
    public const double WakeSeconds = 5;
    public const double PlayerRadius = 0.35;
    public const double PickupDistance = 1.0;
    public const double ExitDistance = 1.1;

    private readonly Random _random;
    private int[,] _toPlayer;
    private (int X, int Y) _playerTileWhenComputed = (-1, -1);
    private (int X, int Y) _marieTarget;
    private readonly List<string> _messages = [];
    private readonly bool[,] _explored;

    public MarieGame(int seed)
    {
        Seed = seed;
        _random = new Random(seed * 31 + 7);
        Maze = new MarieMaze(seed);
        _explored = new bool[Maze.Side, Maze.Side];
        (PlayerX, PlayerZ) = MarieMaze.Center(1, 1);
        // Looking along the corridor that leaves the start.
        Yaw = Maze.IsWall(2, 1) ? Math.PI : -Math.PI / 2;
        Exit = MarieMaze.CellTile(Maze.CellCount - 1, Maze.CellCount - 1);
        Ammo = new Dictionary<MarieWeapon, int> { [MarieWeapon.Pistol] = 6, [MarieWeapon.Shotgun] = 0, [MarieWeapon.Plasma] = 0 };
        Weapon = MarieWeapon.Pistol;

        // MarieKilleuse starts at the cell of the middle, far enough.
        var middle = MarieMaze.CellTile(Maze.CellCount / 2, Maze.CellCount / 2);
        (MarieX, MarieZ) = MarieMaze.Center(middle.X, middle.Y);
        _marieTarget = middle;
        MarieHealth = MarieMaxHealth;
        State = MarieState.Asleep;
        StateTime = WakeSeconds;
        _toPlayer = Maze.Distances(MarieMaze.TileAt(PlayerX, PlayerZ));

        PlacePickups();
        Explore();
    }

    public int Seed { get; }

    public MarieMaze Maze { get; }

    // ── The player ──

    public double PlayerX { get; private set; }

    public double PlayerZ { get; private set; }

    /// <summary>The direction looked at, in radians (0: towards -Z, as a camera of Godot).</summary>
    public double Yaw { get; private set; }

    public int Health { get; private set; } = MaxHealth;

    /// <summary>The seconds of speed bonus left.</summary>
    public double Boost { get; private set; }

    public MarieWeapon Weapon { get; private set; }

    public Dictionary<MarieWeapon, int> Ammo { get; }

    private double _reload;

    /// <summary>The seconds since the start (the clock stops at the end).</summary>
    public double Time { get; private set; }

    /// <summary>The times MarieKilleuse was knocked down.</summary>
    public int Knockdowns { get; private set; }

    public MarieOutcome Outcome { get; private set; }

    // ── MarieKilleuse ──

    public double MarieX { get; private set; }

    public double MarieZ { get; private set; }

    public int MarieHealth { get; private set; }

    public MarieState State { get; private set; }

    /// <summary>The seconds left in her state (asleep, resting, down).</summary>
    public double StateTime { get; private set; }

    /// <summary>Raised for the view at each event of a step (a shot, a hit, a catch, a pickup…).</summary>
    public event Action<string>? Event;

    public (int X, int Y) Exit { get; }

    public List<MariePickup> Pickups { get; } = [];

    /// <summary>The messages of the last step (pickups, catches…), for the screen.</summary>
    public IReadOnlyList<string> Messages => _messages;

    public double MarieDistance => Math.Sqrt((MarieX - PlayerX) * (MarieX - PlayerX) + (MarieZ - PlayerZ) * (MarieZ - PlayerZ));

    /// <summary>The tiles the player has seen (for the map).</summary>
    public bool Explored(int x, int y) => x >= 0 && y >= 0 && x < Maze.Side && y < Maze.Side && _explored[x, y];

    /// <summary>The way along the corridors to MarieKilleuse, in tiles (-1: unknown).</summary>
    public int MarieTiles
    {
        get
        {
            var (mx, mz) = MarieMaze.TileAt(MarieX, MarieZ);
            return mx >= 0 && mz >= 0 && mx < Maze.Side && mz < Maze.Side ? _toPlayer[mx, mz] : -1;
        }
    }

    private void PlacePickups()
    {
        var start = MarieMaze.TileAt(PlayerX, PlayerZ);
        var fromStart = Maze.Distances(start);
        var cells = new List<(int X, int Y)>();
        for (var cy = 0; cy < Maze.CellCount; cy++)
            for (var cx = 0; cx < Maze.CellCount; cx++)
            {
                var tile = MarieMaze.CellTile(cx, cy);
                if (fromStart[tile.X, tile.Y] > 6 && tile != Exit)
                    cells.Add(tile);
            }
        var order = cells.OrderBy(_ => _random.Next()).ToList();
        var kinds = Enumerable.Repeat(MariePickupKind.Health, 10)
            .Concat(Enumerable.Repeat(MariePickupKind.Speed, 7))
            .Concat(Enumerable.Repeat(MariePickupKind.Pistol, 4))
            .Concat(Enumerable.Repeat(MariePickupKind.Shotgun, 3))
            .Concat(Enumerable.Repeat(MariePickupKind.Plasma, 2));
        var k = 0;
        foreach (var kind in kinds)
        {
            if (k >= order.Count)
                break;
            Pickups.Add(new MariePickup(kind, order[k].X, order[k].Y));
            k++;
        }
    }

    /// <summary>Chooses the weapon <paramref name="index"/> (0 pistol, 1 shotgun, 2 plasma) if it has ammunition.</summary>
    public bool Select(int index)
    {
        if (index < 0 || index >= MarieWeapon.All.Count)
            return false;
        var weapon = MarieWeapon.All[index];
        if (Ammo[weapon] <= 0)
        {
            Say($"No ammunition for the {weapon.Name.ToLowerInvariant()}");
            return false;
        }
        Weapon = weapon;
        Event?.Invoke("switch");
        return true;
    }

    /// <summary>The next weapon with ammunition (<paramref name="step"/> 1 or -1).</summary>
    public void Cycle(int step)
    {
        var index = MarieWeapon.All.ToList().IndexOf(Weapon);
        for (var k = 1; k <= MarieWeapon.All.Count; k++)
        {
            var next = MarieWeapon.All[((index + step * k) % 3 + 3) % 3];
            if (Ammo[next] > 0)
            {
                Weapon = next;
                Event?.Invoke("switch");
                return;
            }
        }
    }

    /// <summary>One step of the game, <paramref name="dt"/> seconds long.</summary>
    public void Update(double dt, MarieInput input)
    {
        _messages.Clear();
        if (Outcome != MarieOutcome.Playing)
            return;
        dt = Math.Min(dt, 0.1);
        Time += dt;
        Yaw = input.Yaw;

        // ── The player moves ──
        var speed = Boost > 0 ? BoostSpeed : PlayerSpeed;
        Boost = Math.Max(0, Boost - dt);
        var forwardX = -Math.Sin(Yaw);
        var forwardZ = -Math.Cos(Yaw);
        var rightX = Math.Cos(Yaw);
        var rightZ = -Math.Sin(Yaw);
        var mx = forwardX * input.Forward + rightX * input.Right;
        var mz = forwardZ * input.Forward + rightZ * input.Right;
        var length = Math.Sqrt(mx * mx + mz * mz);
        if (length > 1)
        {
            mx /= length;
            mz /= length;
        }
        (PlayerX, PlayerZ) = Maze.Move(PlayerX, PlayerZ, mx * speed * dt, mz * speed * dt, PlayerRadius);
        Explore();

        // ── Pickups and the exit ──
        foreach (var pickup in Pickups.Where(p => !p.Taken))
        {
            var (px, pz) = MarieMaze.Center(pickup.X, pickup.Y);
            if (Math.Abs(px - PlayerX) > PickupDistance || Math.Abs(pz - PlayerZ) > PickupDistance)
                continue;
            if (pickup.Kind == MariePickupKind.Health && Health >= MaxHealth)
                continue;
            pickup.Taken = true;
            Take(pickup.Kind);
        }
        var (ex, ez) = MarieMaze.Center(Exit.X, Exit.Y);
        if (Math.Abs(ex - PlayerX) < ExitDistance && Math.Abs(ez - PlayerZ) < ExitDistance)
        {
            Outcome = MarieOutcome.Escaped;
            Say("You escaped from the maze!");
            Event?.Invoke("escaped");
            return;
        }

        // ── Shooting ──
        _reload = Math.Max(0, _reload - dt);
        if (input.Fire && _reload <= 0)
            Fire();

        // ── MarieKilleuse ──
        UpdateMarie(dt);
    }

    private void Take(MariePickupKind kind)
    {
        switch (kind)
        {
            case MariePickupKind.Health:
                Health = Math.Min(MaxHealth, Health + HealthPack);
                Say($"+{HealthPack} life points");
                break;
            case MariePickupKind.Speed:
                Boost = BoostSeconds;
                Say($"Speed ×2 for {BoostSeconds:0} s: twice as fast as MarieKilleuse!");
                break;
            default:
                var weapon = kind switch
                {
                    MariePickupKind.Shotgun => MarieWeapon.Shotgun,
                    MariePickupKind.Plasma => MarieWeapon.Plasma,
                    _ => MarieWeapon.Pistol
                };
                Ammo[weapon] += weapon.AmmoPerPickup;
                // A better weapon is taken in hand at once.
                if (MarieWeapon.All.ToList().IndexOf(weapon) > MarieWeapon.All.ToList().IndexOf(Weapon) || Ammo[Weapon] == 0)
                    Weapon = weapon;
                Say($"{weapon.Name}: +{weapon.AmmoPerPickup} ammunition");
                break;
        }
        Event?.Invoke("pickup");
    }

    private void Fire()
    {
        if (Ammo[Weapon] <= 0)
        {
            Say("Click! No ammunition: find weapons, or run");
            _reload = 0.4;
            Event?.Invoke("empty");
            return;
        }
        Ammo[Weapon]--;
        _reload = Weapon.Cooldown;
        Event?.Invoke("shot");
        if (State == MarieState.Down)
            return;
        // A hit when MarieKilleuse is in front, within the spread of the weapon, in range and in sight.
        var dx = MarieX - PlayerX;
        var dz = MarieZ - PlayerZ;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        var forwardX = -Math.Sin(Yaw);
        var forwardZ = -Math.Cos(Yaw);
        var along = (dx * forwardX + dz * forwardZ);
        if (along <= 0 || distance > Weapon.Range)
        {
            if (Ammo[Weapon] == 0)
                Cycle(1);
            return;
        }
        var across = Math.Abs(dx * forwardZ - dz * forwardX);
        var tolerance = 0.55 + distance * Math.Tan(Weapon.Spread * Math.PI / 180);
        if (across > tolerance || !Maze.CanSee(PlayerX, PlayerZ, MarieX, MarieZ))
        {
            if (Ammo[Weapon] == 0)
                Cycle(1);
            return;
        }
        // The shotgun hurts more up close.
        var damage = Weapon == MarieWeapon.Shotgun ? (int)(Weapon.Damage * Math.Clamp(1.3 - distance / Weapon.Range, 0.35, 1.0)) : Weapon.Damage;
        MarieHealth -= damage;
        Event?.Invoke("hit");
        if (MarieHealth <= 0)
        {
            MarieHealth = 0;
            State = MarieState.Down;
            StateTime = DownSeconds;
            Knockdowns++;
            Say($"MarieKilleuse is down for {DownSeconds:0} s: run!");
            Event?.Invoke("down");
        }
        else
        {
            // She staggers a moment.
            if (State == MarieState.Hunting)
            {
                State = MarieState.Resting;
                StateTime = 0.35;
            }
        }
        if (Ammo[Weapon] == 0)
            Cycle(1);
    }

    private void UpdateMarie(double dt)
    {
        if (State != MarieState.Hunting)
        {
            StateTime -= dt;
            if (StateTime > 0)
                return;
            if (State == MarieState.Down)
                Respawn();
            if (State == MarieState.Asleep)
                Say("MarieKilleuse is coming for you…");
            State = MarieState.Hunting;
            Event?.Invoke("hunt");
        }

        var playerTile = MarieMaze.TileAt(PlayerX, PlayerZ);
        if (playerTile != _playerTileWhenComputed)
        {
            _toPlayer = Maze.Distances(playerTile);
            _playerTileWhenComputed = playerTile;
        }
        var marieTile = MarieMaze.TileAt(MarieX, MarieZ);
        double targetX, targetZ;
        if (marieTile == playerTile || (MarieDistance < MarieMaze.Tile * 1.5 && Maze.CanSee(MarieX, MarieZ, PlayerX, PlayerZ)))
            (targetX, targetZ) = (PlayerX, PlayerZ);
        else
        {
            // Towards the centre of the next tile of the shortest way (once at the centre of her tile).
            var (cx, cz) = MarieMaze.Center(_marieTarget.X, _marieTarget.Y);
            if (Math.Abs(cx - MarieX) < 0.05 && Math.Abs(cz - MarieZ) < 0.05 || Maze.IsWall(_marieTarget.X, _marieTarget.Y)
                || Math.Abs(_marieTarget.X - marieTile.X) + Math.Abs(_marieTarget.Y - marieTile.Y) > 1)
                _marieTarget = Maze.NextStep(marieTile, _toPlayer);
            (targetX, targetZ) = MarieMaze.Center(_marieTarget.X, _marieTarget.Y);
        }
        var dx = targetX - MarieX;
        var dz = targetZ - MarieZ;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        var step = MarieSpeed * dt;
        if (distance > 1e-6)
        {
            var move = Math.Min(step, distance);
            (MarieX, MarieZ) = Maze.Move(MarieX, MarieZ, dx / distance * move, dz / distance * move, 0.3);
        }

        if (MarieDistance < CatchDistance)
        {
            Health = Math.Max(0, Health - CatchDamage);
            Event?.Invoke("caught");
            if (Health == 0)
            {
                Outcome = MarieOutcome.Caught;
                Say("MarieKilleuse caught you…");
                Event?.Invoke("dead");
                return;
            }
            Say($"MarieKilleuse caught you: -{CatchDamage} life points · adrenaline, run!");
            State = MarieState.Resting;
            StateTime = RestSeconds;
            Boost = Math.Max(Boost, AdrenalineSeconds);
        }
    }

    /// <summary>After a knock-down: back on her feet at the floor tile farthest from the player.</summary>
    private void Respawn()
    {
        var playerTile = MarieMaze.TileAt(PlayerX, PlayerZ);
        var distances = Maze.Distances(playerTile);
        var best = (X: 1, Y: 1);
        var bestDistance = -1;
        for (var y = 1; y < Maze.Side; y += 2)
            for (var x = 1; x < Maze.Side; x += 2)
                if (distances[x, y] > bestDistance && (x, y) != Exit)
                {
                    bestDistance = distances[x, y];
                    best = (x, y);
                }
        (MarieX, MarieZ) = MarieMaze.Center(best.X, best.Y);
        _marieTarget = best;
        MarieHealth = MarieMaxHealth;
        Say("MarieKilleuse is back on her feet, somewhere far away…");
    }

    private void Explore()
    {
        var (tx, tz) = MarieMaze.TileAt(PlayerX, PlayerZ);
        for (var oy = -2; oy <= 2; oy++)
            for (var ox = -2; ox <= 2; ox++)
            {
                var x = tx + ox;
                var y = tz + oy;
                if (x >= 0 && y >= 0 && x < Maze.Side && y < Maze.Side)
                    _explored[x, y] = true;
            }
    }

    private void Say(string message) => _messages.Add(message);
}
