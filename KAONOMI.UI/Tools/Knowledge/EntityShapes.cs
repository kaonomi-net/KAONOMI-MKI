using System;
using System.Collections.Concurrent;
using System.Numerics;
using static KAONOMI.UI.Tools.Knowledge.Mesh3D;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The shapes of the elements of the 3D relation graph, by entity type, as in the Godot chat (<c>EntityShapes.cs</c>
/// there, part for part): a human body for a person, a group of three for a nationality or group (NORP), a building for
/// an organisation, a factory for a facility, a globe for a country or city (GPE), a map pin for a place, a parcel for a
/// product, a flag for an event, a painting on its easel for a work of art, a gavel for a law, a speech bubble for a
/// language, a calendar for a date, an hourglass for a time, the % sign for a percentage, coins for money, a weight for a
/// quantity, a podium for an ordinal, a die for a number, an apple for food, geometric solids for mathematics, a robot
/// head for AI, a flowchart for an algorithm, bricks for a material, a tree for an ontology class, a CD-ROM for a
/// software, a PCI card for a hardware, a gem for the others; a sphere for an element that is not an entity. Each is one
/// mesh, about the size of the sphere, built once per size.
/// </summary>
internal static class EntityShapes
{
    private static readonly ConcurrentDictionary<(string, float), Mesh3D> Cache = new();

    /// <summary>The mesh of an element of entity type <paramref name="entityType"/> (null: not an entity), for a sphere of <paramref name="size"/>.</summary>
    public static Mesh3D For(string? entityType, float size)
    {
        var key = entityType?.Trim().ToUpperInvariant().Replace(' ', '_');
        return Cache.GetOrAdd((key ?? "", size), _ => Build(key, size));
    }

    private static Mesh3D Build(string? key, float size) => key switch
    {
        "PERSON" or "PER" or "PERSONNE" => Person(size),
        "NORP" or "GROUP" => Group(size),
        "ORG" or "ORGANISATION" or "ORGANIZATION" or "COMPANY" => Building(size),
        "FAC" or "FACILITY" => Factory(size),
        "GPE" or "COUNTRY" or "CITY" => Globe(size),
        "LOC" or "LOCATION" or "PLACE" or "LIEU" => Pin(size),
        "PRODUCT" or "PORTFOLIO" => Parcel(size),
        "EVENT" => Flag(size),
        "WORK_OF_ART" or "ART" => Painting(size),
        "LAW" => Gavel(size),
        "LANGUAGE" => SpeechBubble(size),
        "DATE" => Calendar(size),
        "TIME" => Hourglass(size),
        "PERCENT" => Percent(size),
        "MONEY" or "PROGRAMME" or "PROGRAM" => Coins(size),
        "QUANTITY" => Weight(size),
        "ORDINAL" or "PROFESSION" or "OCCUPATION" => Podium(size),
        "CARDINAL" or "NUMBER" => Die(size),
        "FOOD" => Apple(size),
        "MATHEMATIC" or "MATHEMATICS" or "MATH" => Solids(size),
        "AI" => RobotHead(size),
        "ALGORITHM" => Flowchart(size),
        "MATERIAL" => Bricks(size),
        "CLASS" or "DOMAIN" => Tree(size),
        "SOFTWARE" => CdRom(size),
        "HARDWARE" => PciCard(size),
        null => Sphere(size * 0.55f, size * 1.1f, 32, 16),
        _ => Gem(size)
    };

    private static Mesh3D Ball(float radius, int segments = 20) => Sphere(radius, radius * 2, segments, segments / 2);

    private static readonly Matrix4x4 Standing = Turn(90, 0, 0);

    private static Matrix4x4 Scale(float x, float y, float z) => Matrix4x4.CreateScale(x, y, z);

    /// <summary>A rod of <paramref name="radius"/> from <paramref name="a"/> to <paramref name="b"/>, then moved by <paramref name="outer"/>.</summary>
    private static void Rod(Mesh3D tool, Vector3 a, Vector3 b, float radius, Matrix4x4 outer)
    {
        var d = b - a;
        var mid = (a + b) / 2;
        tool.Add(Cylinder(radius, radius, d.Length(), 10), UpTo(d) * Matrix4x4.CreateTranslation(mid) * outer);
    }

    private static Mesh3D Group(float s)
    {
        var tool = new Mesh3D();
        var person = Person(s);
        tool.Add(person, Matrix4x4.CreateScale(0.62f) * At(-0.3f * s, -0.12f * s, -0.05f * s));
        tool.Add(person, Matrix4x4.CreateScale(0.62f) * At(0.3f * s, -0.12f * s, -0.05f * s));
        tool.Add(person, Matrix4x4.CreateScale(0.75f) * At(0, -0.05f * s, 0.08f * s));
        return tool;
    }

    private static Mesh3D Building(float s)
    {
        var tool = new Mesh3D();
        var turn = Turn(0, -20, 0);
        tool.Add(Box(0.36f * s, 0.9f * s, 0.36f * s), At(turn, -0.1f * s, 0, 0));
        tool.Add(Box(0.32f * s, 0.55f * s, 0.32f * s), At(turn, 0.22f * s, -0.175f * s, 0));
        for (var row = 0; row < 5; row++)
            for (var col = 0; col < 3; col++)
                tool.Add(Box(0.07f * s, 0.09f * s, 0.03f * s), At(turn, (-0.21f + col * 0.11f) * s, (0.3f - row * 0.16f) * s, 0.185f * s));
        for (var row = 0; row < 3; row++)
            tool.Add(Box(0.2f * s, 0.07f * s, 0.03f * s), At(turn, 0.22f * s, (0.0f - row * 0.15f) * s, 0.165f * s));
        Rod(tool, new Vector3(-0.1f * s, 0.45f * s, 0), new Vector3(-0.1f * s, 0.62f * s, 0), 0.015f * s, turn);
        return tool;
    }

    private static Mesh3D Factory(float s)
    {
        var tool = new Mesh3D();
        var turn = Turn(0, -25, 0);
        tool.Add(Box(0.84f * s, 0.38f * s, 0.42f * s), At(turn, 0, -0.25f * s, 0));
        for (var i = 0; i < 3; i++)
            tool.Add(Prism(new Vector3(0.28f, 0.16f, 0.42f) * s, 0), At(turn, (-0.28f + i * 0.28f) * s, 0.02f * s, 0));
        tool.Add(Cylinder(0.045f * s, 0.06f * s, 0.55f * s), At(turn, 0.28f * s, 0.2f * s, -0.12f * s));
        tool.Add(Cylinder(0.04f * s, 0.055f * s, 0.42f * s), At(turn, 0.12f * s, 0.14f * s, -0.12f * s));
        for (var i = 0; i < 4; i++)
            tool.Add(Box(0.12f * s, 0.1f * s, 0.03f * s), At(turn, (-0.3f + i * 0.2f) * s, -0.25f * s, 0.215f * s));
        return tool;
    }

    private static Mesh3D Globe(float s)
    {
        var tool = new Mesh3D();
        tool.Add(Ball(0.34f * s, 32), At(0, 0.1f * s, 0));
        tool.Add(Torus(0.38f * s, 0.42f * s), At(Turn(0, 0, 90) * Turn(0, 0, 23), 0, 0.1f * s, 0));
        tool.Add(Cylinder(0.04f * s, 0.05f * s, 0.16f * s), At(0, -0.33f * s, 0));
        tool.Add(Cylinder(0.2f * s, 0.24f * s, 0.05f * s), At(0, -0.43f * s, 0));
        return tool;
    }

    private static Mesh3D Pin(float s)
    {
        var tool = new Mesh3D();
        tool.Add(Ball(0.27f * s, 28), At(0, 0.2f * s, 0));
        tool.Add(Cylinder(0.24f * s, 0, 0.5f * s), At(0, -0.18f * s, 0));
        tool.Add(Torus(0.08f * s, 0.13f * s), At(Standing, 0, 0.22f * s, 0.24f * s));
        return tool;
    }

    private static Mesh3D Parcel(float s)
    {
        var tool = new Mesh3D();
        var turn = Turn(0, -25, 0);
        tool.Add(Box(0.6f * s, 0.45f * s, 0.45f * s), At(turn, 0, -0.12f * s, 0));
        tool.Add(Box(0.62f * s, 0.47f * s, 0.08f * s), At(turn, 0, -0.12f * s, 0));
        tool.Add(Box(0.08f * s, 0.47f * s, 0.47f * s), At(turn, 0, -0.12f * s, 0));
        tool.Add(Torus(0.04f * s, 0.1f * s), At(Turn(90, 0, 40) * turn, -0.08f * s, 0.18f * s, 0));
        tool.Add(Torus(0.04f * s, 0.1f * s), At(Turn(90, 0, -40) * turn, 0.08f * s, 0.18f * s, 0));
        return tool;
    }

    private static Mesh3D Flag(float s)
    {
        var tool = new Mesh3D();
        tool.Add(Cylinder(0.022f * s, 0.022f * s, 1.0f * s, 12), At(-0.3f * s, 0, 0));
        tool.Add(Ball(0.045f * s), At(-0.3f * s, 0.52f * s, 0));
        tool.Add(Cylinder(0.14f * s, 0.18f * s, 0.05f * s), At(-0.3f * s, -0.5f * s, 0));
        tool.Add(Box(0.3f * s, 0.32f * s, 0.02f * s), At(Turn(0, 12, 0), -0.13f * s, 0.3f * s, 0.015f * s));
        tool.Add(Box(0.3f * s, 0.32f * s, 0.02f * s), At(Turn(0, -12, 0), 0.155f * s, 0.29f * s, 0.015f * s));
        return tool;
    }

    private static Mesh3D Painting(float s)
    {
        var tool = new Mesh3D();
        var frame = At(Turn(-10, -15, 0), 0, 0.15f * s, 0.04f * s);
        tool.Add(Box(0.56f * s, 0.42f * s, 0.02f * s), frame);
        foreach (var (w, h, x, y) in new[] { (0.66f, 0.06f, 0f, 0.24f), (0.66f, 0.06f, 0f, -0.24f), (0.06f, 0.54f, 0.3f, 0f), (0.06f, 0.54f, -0.3f, 0f) })
            tool.Add(Box(w * s, h * s, 0.06f * s), At(x * s, y * s, 0.01f * s) * frame);
        var easel = Turn(0, -15, 0);
        Rod(tool, new Vector3(-0.22f, -0.55f, 0.12f) * s, new Vector3(-0.05f, 0.45f, 0) * s, 0.02f * s, easel);
        Rod(tool, new Vector3(0.22f, -0.55f, 0.12f) * s, new Vector3(0.05f, 0.45f, 0) * s, 0.02f * s, easel);
        Rod(tool, new Vector3(0, -0.55f, -0.3f) * s, new Vector3(0, 0.4f, -0.02f) * s, 0.02f * s, easel);
        tool.Add(Box(0.5f * s, 0.03f * s, 0.08f * s), At(0, -0.1f * s, 0.07f * s) * easel);
        return tool;
    }

    private static Mesh3D Gavel(float s)
    {
        var tool = new Mesh3D();
        var pose = Turn(0, -20, -20);
        var lying = Turn(0, 0, 90);
        tool.Add(Cylinder(0.11f * s, 0.11f * s, 0.42f * s), At(lying, -0.1f * s, 0.22f * s, 0) * pose);
        foreach (var end in new[] { -1, 1 })
            tool.Add(Cylinder(0.125f * s, 0.125f * s, 0.04f * s), At(lying, (-0.1f + end * 0.19f) * s, 0.22f * s, 0) * pose);
        Rod(tool, new Vector3(-0.1f, 0.11f, 0) * s, new Vector3(-0.1f, -0.3f, 0) * s, 0.03f * s, pose);
        tool.Add(Cylinder(0.22f * s, 0.24f * s, 0.09f * s), At(Turn(0, -20, 0), 0.1f * s, -0.42f * s, 0));
        return tool;
    }

    private static Mesh3D SpeechBubble(float s)
    {
        var tool = new Mesh3D();
        tool.Add(Ball(0.4f * s, 32), Scale(1.25f, 0.85f, 0.45f) * At(0, 0.08f * s, 0));
        tool.Add(Cylinder(0.11f * s, 0, 0.3f * s), At(Turn(0, 0, -35), -0.26f * s, -0.32f * s, 0));
        for (var i = -1; i <= 1; i++)
            tool.Add(Ball(0.055f * s), At(i * 0.16f * s, 0.08f * s, 0.17f * s));
        return tool;
    }

    private static Mesh3D Calendar(float s)
    {
        var tool = new Mesh3D();
        var turn = Turn(-8, -15, 0);
        tool.Add(Box(0.72f * s, 0.66f * s, 0.06f * s), At(turn, 0, -0.06f * s, 0));
        tool.Add(Box(0.72f * s, 0.16f * s, 0.1f * s), At(turn, 0, 0.2f * s, 0.02f * s));
        foreach (var x in new[] { -0.2f, 0.2f })
            tool.Add(Torus(0.035f * s, 0.07f * s), At(Turn(0, 90, 0) * turn, x * s, 0.3f * s, 0));
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 4; col++)
                tool.Add(Box(0.11f * s, 0.09f * s, 0.03f * s), At(turn, (-0.24f + col * 0.16f) * s, (0.0f - row * 0.13f) * s, 0.04f * s));
        return tool;
    }

    private static Mesh3D Hourglass(float s)
    {
        var tool = new Mesh3D();
        foreach (var y in new[] { -0.46f, 0.46f })
            tool.Add(Cylinder(0.3f * s, 0.3f * s, 0.06f * s), At(0, y * s, 0));
        tool.Add(Cylinder(0.24f * s, 0.03f * s, 0.42f * s), At(0, 0.22f * s, 0));
        tool.Add(Cylinder(0.03f * s, 0.24f * s, 0.42f * s), At(0, -0.22f * s, 0));
        for (var i = 0; i < 3; i++)
        {
            var angle = i * MathF.Tau / 3 + 0.4f;
            var x = MathF.Cos(angle) * 0.27f * s;
            var z = MathF.Sin(angle) * 0.27f * s;
            Rod(tool, new Vector3(x, -0.43f * s, z), new Vector3(x, 0.43f * s, z), 0.022f * s, Matrix4x4.Identity);
        }
        return tool;
    }

    private static Mesh3D Percent(float s)
    {
        var tool = new Mesh3D();
        tool.Add(Torus(0.07f * s, 0.15f * s), At(Standing, -0.22f * s, 0.24f * s, 0));
        tool.Add(Torus(0.07f * s, 0.15f * s), At(Standing, 0.22f * s, -0.24f * s, 0));
        tool.Add(Box(0.09f * s, 1.0f * s, 0.09f * s), Turn(0, 0, -38));
        return tool;
    }

    private static Mesh3D Coins(float s)
    {
        var tool = new Mesh3D();
        for (var i = 0; i < 6; i++)
            tool.Add(Cylinder(0.24f * s, 0.24f * s, 0.06f * s, 32), At((-0.17f + (i % 2) * 0.012f) * s, (-0.42f + i * 0.065f) * s, -0.05f * s));
        for (var i = 0; i < 3; i++)
            tool.Add(Cylinder(0.2f * s, 0.2f * s, 0.06f * s, 32), At((0.25f - (i % 2) * 0.01f) * s, (-0.42f + i * 0.065f) * s, -0.1f * s));
        tool.Add(Cylinder(0.2f * s, 0.2f * s, 0.06f * s, 32), At(Standing, 0.2f * s, -0.16f * s, 0.22f * s));
        tool.Add(Cylinder(0.12f * s, 0.12f * s, 0.07f * s, 32), At(Standing, 0.2f * s, -0.16f * s, 0.22f * s));
        return tool;
    }

    private static Mesh3D Weight(float s)
    {
        var tool = new Mesh3D();
        tool.Add(Cylinder(0.24f * s, 0.38f * s, 0.5f * s, 32), At(0, -0.22f * s, 0));
        tool.Add(Torus(0.11f * s, 0.18f * s), At(Standing, 0, 0.16f * s, 0));
        return tool;
    }

    private static Mesh3D Podium(float s)
    {
        var tool = new Mesh3D();
        var turn = Turn(0, -20, 0);
        foreach (var (x, h) in new[] { (-0.32f, 0.4f), (0f, 0.6f), (0.32f, 0.28f) })
            tool.Add(Box(0.3f * s, h * s, 0.34f * s), At(turn, x * s, (-0.45f + h / 2) * s, 0));
        tool.Add(Ball(0.08f * s), At(turn, 0, 0.23f * s, 0));
        return tool;
    }

    private static Mesh3D Die(float s)
    {
        var tool = new Mesh3D();
        var turn = Turn(-25, -35, 0);
        const float half = 0.3f;
        tool.Add(Box(0.6f * s, 0.6f * s, 0.6f * s), turn);
        var pip = Ball(0.05f * s, 12);
        void Pips(Vector3 normal, Vector3 u, Vector3 v, (float, float)[] spots)
        {
            foreach (var (a, b) in spots)
            {
                var p = (normal * half + u * a + v * b) * s;
                tool.Add(pip, At(turn, p.X, p.Y, p.Z));
            }
        }
        Pips(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY, [(-0.15f, 0.15f), (0.15f, 0.15f), (0, 0), (-0.15f, -0.15f), (0.15f, -0.15f)]);
        Pips(Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, [(-0.15f, -0.15f), (0, 0), (0.15f, 0.15f)]);
        Pips(Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY, [(-0.12f, 0.12f), (0.12f, -0.12f)]);
        return tool;
    }

    private static Mesh3D Apple(float s)
    {
        var tool = new Mesh3D();
        tool.Add(Ball(0.38f * s, 32), Scale(1, 0.88f, 1) * At(0, -0.08f * s, 0));
        Rod(tool, new Vector3(0, 0.2f, 0) * s, new Vector3(0.04f, 0.42f, 0) * s, 0.025f * s, Matrix4x4.Identity);
        tool.Add(Ball(0.1f * s), Scale(1.6f, 0.45f, 0.8f) * Turn(0, 0, 30) * At(0.17f * s, 0.36f * s, 0));
        return tool;
    }

    private static Mesh3D Solids(float s)
    {
        var tool = new Mesh3D();
        tool.Add(Box(0.34f * s, 0.34f * s, 0.34f * s), At(Turn(0, 20, 0), -0.2f * s, -0.28f * s, 0));
        tool.Add(Ball(0.18f * s, 28), At(0.25f * s, -0.27f * s, 0.05f * s));
        tool.Add(Cylinder(0, 0.28f * s, 0.42f * s, 4), At(Turn(0, 45, 0), 0, 0.2f * s, 0));
        return tool;
    }

    private static Mesh3D RobotHead(float s)
    {
        var tool = new Mesh3D();
        var turn = Turn(0, -15, 0);
        tool.Add(Box(0.56f * s, 0.46f * s, 0.44f * s), At(turn, 0, -0.08f * s, 0));
        foreach (var x in new[] { -0.13f, 0.13f })
            tool.Add(Ball(0.075f * s, 16), At(turn, x * s, 0.0f, 0.22f * s));
        tool.Add(Box(0.26f * s, 0.05f * s, 0.03f * s), At(turn, 0, -0.17f * s, 0.225f * s));
        foreach (var x in new[] { -0.31f, 0.31f })
            tool.Add(Cylinder(0.07f * s, 0.07f * s, 0.08f * s), At(Turn(0, 0, 90) * turn, x * s, -0.08f * s, 0));
        Rod(tool, new Vector3(0, 0.15f, 0) * s, new Vector3(0, 0.36f, 0) * s, 0.018f * s, turn);
        tool.Add(Ball(0.05f * s), At(turn, 0, 0.39f * s, 0));
        return tool;
    }

    private static Mesh3D Flowchart(float s)
    {
        var tool = new Mesh3D();
        var plane = Turn(0, -15, 0);
        tool.Add(Box(0.42f * s, 0.16f * s, 0.08f * s), At(0, 0.42f * s, 0) * plane);
        tool.Add(Box(0.26f * s, 0.26f * s, 0.08f * s), At(Turn(0, 0, 45), 0, 0.03f * s, 0) * plane);
        tool.Add(Box(0.32f * s, 0.16f * s, 0.08f * s), At(-0.3f * s, -0.38f * s, 0) * plane);
        tool.Add(Box(0.32f * s, 0.16f * s, 0.08f * s), At(0.3f * s, -0.38f * s, 0) * plane);
        Rod(tool, new Vector3(0, 0.34f, 0) * s, new Vector3(0, 0.21f, 0) * s, 0.018f * s, plane);
        Rod(tool, new Vector3(-0.17f, 0.0f, 0) * s, new Vector3(-0.3f, -0.3f, 0) * s, 0.018f * s, plane);
        Rod(tool, new Vector3(0.17f, 0.0f, 0) * s, new Vector3(0.3f, -0.3f, 0) * s, 0.018f * s, plane);
        return tool;
    }

    private static Mesh3D Bricks(float s)
    {
        var tool = new Mesh3D();
        var turn = Turn(0, -25, 0);
        foreach (var (x, y) in new[] { (-0.21f, -0.25f), (0.21f, -0.25f), (0f, -0.04f) })
            tool.Add(Box(0.4f * s, 0.19f * s, 0.24f * s), At(turn, x * s, y * s, 0));
        tool.Add(Box(0.4f * s, 0.19f * s, 0.24f * s), At(Turn(0, 90, 0) * turn, 0, 0.17f * s, 0));
        return tool;
    }

    private static Mesh3D Tree(float s)
    {
        var tool = new Mesh3D();
        var root = new Vector3(0, 0.33f, 0) * s;
        tool.Add(Ball(0.14f * s), Matrix4x4.CreateTranslation(root));
        foreach (var x in new[] { -0.33f, 0f, 0.33f })
        {
            var child = new Vector3(x, -0.3f, x == 0 ? 0.1f : 0) * s;
            tool.Add(Box(0.17f * s, 0.17f * s, 0.17f * s), Matrix4x4.CreateTranslation(child));
            Rod(tool, root, child, 0.022f * s, Matrix4x4.Identity);
        }
        return tool;
    }

    private static Mesh3D Gem(float s)
    {
        var tool = new Mesh3D();
        tool.Add(Cylinder(0, 0.36f * s, 0.42f * s, 4), At(Turn(0, 20, 0), 0, 0.21f * s, 0));
        tool.Add(Cylinder(0.36f * s, 0, 0.42f * s, 4), At(Turn(0, 20, 0), 0, -0.21f * s, 0));
        return tool;
    }

    private static Mesh3D Person(float s)
    {
        var tool = new Mesh3D();
        tool.Add(Sphere(0.14f * s, 0.28f * s, 24, 12), At(0, 0.42f * s, 0));
        tool.Add(Capsule(0.12f * s, 0.44f * s, 20, 6), Scale(1.3f, 1, 0.75f) * At(0, 0.1f * s, 0));
        foreach (var side in new[] { -1, 1 })
        {
            tool.Add(Capsule(0.05f * s, 0.4f * s, 12, 4), Matrix4x4.CreateRotationZ(side * 12 * MathF.PI / 180) * At(side * 0.21f * s, 0.08f * s, 0));
            tool.Add(Capsule(0.065f * s, 0.5f * s, 12, 4), At(side * 0.075f * s, -0.28f * s, 0));
        }
        return tool;
    }

    private static Mesh3D CdRom(float s)
    {
        var tool = new Mesh3D();
        var standing = Turn(-25, -20, 0);
        Ring(tool, standing, 0.1f * s, 0.6f * s, 0.025f * s);
        Ring(tool, standing, 0.1f * s, 0.19f * s, 0.1f * s);
        Ring(tool, standing, 0.27f * s, 0.29f * s, 0.045f * s);
        return tool;
    }

    /// <summary>A flat ring facing the front (+Z) turned by <paramref name="basis"/>: its two faces, its outer and inner edges.</summary>
    private static void Ring(Mesh3D tool, Matrix4x4 basis, float inner, float outer, float thickness)
    {
        const int segments = 72;
        var half = thickness / 2;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal) =>
            tool.Quad(Vector3.Transform(a, basis), Vector3.Transform(b, basis), Vector3.Transform(c, basis), Vector3.Transform(d, basis), Vector3.Normalize(Vector3.TransformNormal(normal, basis)));
        for (var i = 0; i < segments; i++)
        {
            var a0 = i * MathF.Tau / segments;
            var a1 = (i + 1) * MathF.Tau / segments;
            var d0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0);
            var d1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0);
            var front = Vector3.UnitZ * half;
            var back = -Vector3.UnitZ * half;
            Quad(d0 * inner + front, d0 * outer + front, d1 * outer + front, d1 * inner + front, Vector3.UnitZ);
            Quad(d1 * inner + back, d1 * outer + back, d0 * outer + back, d0 * inner + back, -Vector3.UnitZ);
            Quad(d0 * outer + front, d0 * outer + back, d1 * outer + back, d1 * outer + front, Vector3.Normalize(d0 + d1));
            Quad(d1 * inner + front, d1 * inner + back, d0 * inner + back, d0 * inner + front, -Vector3.Normalize(d0 + d1));
        }
    }

    private static Mesh3D PciCard(float s)
    {
        var tool = new Mesh3D();
        var turn = Turn(0, -20, 0);
        void Block(float width, float height, float depth, float x, float y, float z) =>
            tool.Add(Box(width * s, height * s, depth * s), At(x * s, y * s, z * s) * turn);
        Block(1.1f, 0.5f, 0.03f, 0, 0, 0);
        Block(0.26f, 0.22f, 0.05f, -0.18f, 0.04f, 0.04f);
        for (var fin = 0; fin < 5; fin++)
            Block(0.022f, 0.2f, 0.06f, -0.29f + fin * 0.055f, 0.04f, 0.095f);
        Block(0.12f, 0.08f, 0.035f, 0.12f, 0.12f, 0.03f);
        Block(0.12f, 0.08f, 0.035f, 0.12f, -0.02f, 0.03f);
        Block(0.08f, 0.08f, 0.03f, 0.33f, 0.08f, 0.03f);
        for (var finger = 0; finger < 22; finger++)
        {
            if (finger is 7 or 8)
                continue;
            Block(0.022f, 0.07f, 0.034f, -0.38f + finger * 0.032f, -0.285f, 0);
        }
        Block(0.02f, 0.66f, 0.13f, 0.57f, 0.03f, 0.05f);
        Block(0.1f, 0.02f, 0.13f, 0.53f, 0.36f, 0.05f);
        return tool;
    }
}
