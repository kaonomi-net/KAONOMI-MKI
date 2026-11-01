using Godot;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// The shapes of the elements of the 3D relation graph, by entity type (the types of config.yml and of the ontology,
/// with their usual spellings): a human body for a person, a group of three for a nationality or group (NORP), a
/// building for an organisation, a factory for a facility, a globe for a country or city (GPE), a map pin for a place,
/// a parcel for a product, a flag for an event, a painting on its easel for a work of art, a gavel for a law, a speech
/// bubble for a language, a calendar for a date, an hourglass for a time, the % sign for a percentage, coins for money,
/// a weight for a quantity, a podium for an ordinal, a die for a number, an apple for food, geometric solids for
/// mathematics, a robot head for AI, a flowchart for an algorithm, bricks for a material, a tree for an ontology class,
/// a CD-ROM for a software, a PCI card for a hardware, a gem for the miscellaneous ones; a sphere for an element that is
/// not an entity. Each one is a single mesh (its parts merged), about the size of the sphere, centred on its place and
/// facing the front (the newest dates), so that it takes the one material of the element.
/// </summary>
internal static class EntityShapes
{
    /// <summary>The mesh of an element of entity type <paramref name="entityType"/> (null: not an entity), for a sphere of <paramref name="size"/>.</summary>
    public static Mesh For(string? entityType, float size) => entityType?.Trim().ToUpperInvariant().Replace(' ', '_') switch
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
        "MISC" or "OTHERS" or "OTHER" => Gem(size),
        null => new SphereMesh { Radius = size * 0.55f, Height = size * 1.1f, RadialSegments = 32, Rings = 16 },
        // Any other entity type: the gem of the miscellaneous ones.
        _ => Gem(size)
    };

    // ── The shapes of the other entity types ──

    private static CylinderMesh Cylinder(float top, float bottom, float height, int segments = 24) =>
        new() { TopRadius = top, BottomRadius = bottom, Height = height, RadialSegments = segments, Rings = 1 };

    private static SphereMesh Ball(float radius, int segments = 20) =>
        new() { Radius = radius, Height = radius * 2, RadialSegments = segments, Rings = segments / 2 };

    private static BoxMesh Block(float x, float y, float z) => new() { Size = new Vector3(x, y, z) };

    /// <summary>A ring standing in the plane of the screen (a torus turned to face the front).</summary>
    private static TorusMesh Torus(float inner, float outer) => new() { InnerRadius = inner, OuterRadius = outer, Rings = 32, RingSegments = 12 };

    private static readonly Basis Standing = Basis.FromEuler(new Vector3(Mathf.Pi / 2, 0, 0));

    private static Transform3D At(Basis basis, float x, float y, float z) => new(basis, new Vector3(x, y, z));

    private static Basis Turn(float x, float y, float z) => Basis.FromEuler(new Vector3(Mathf.DegToRad(x), Mathf.DegToRad(y), Mathf.DegToRad(z)));

    /// <summary>A rod of <paramref name="radius"/> from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static void Rod(SurfaceTool tool, Vector3 a, Vector3 b, float radius, Transform3D outer)
    {
        var d = b - a;
        var basis = new Basis(new Quaternion(Vector3.Up, d.Normalized()));
        tool.AppendFrom(Cylinder(radius, radius, d.Length(), 10), 0, outer * new Transform3D(basis, (a + b) / 2));
    }

    /// <summary>Three people side by side, the middle one a little in front and taller.</summary>
    private static Mesh Group(float s)
    {
        var tool = new SurfaceTool();
        var person = Person(s);
        tool.AppendFrom(person, 0, new Transform3D(Basis.Identity.Scaled(Vector3.One * 0.62f), new Vector3(-0.3f * s, -0.12f * s, -0.05f * s)));
        tool.AppendFrom(person, 0, new Transform3D(Basis.Identity.Scaled(Vector3.One * 0.62f), new Vector3(0.3f * s, -0.12f * s, -0.05f * s)));
        tool.AppendFrom(person, 0, new Transform3D(Basis.Identity.Scaled(Vector3.One * 0.75f), new Vector3(0, -0.05f * s, 0.08f * s)));
        return tool.Commit();
    }

    /// <summary>An office building: a tower with rows of windows, a lower wing, an antenna on the roof.</summary>
    private static Mesh Building(float s)
    {
        var tool = new SurfaceTool();
        var turn = Turn(0, -20, 0);
        Add(tool, Block(0.36f * s, 0.9f * s, 0.36f * s), At(turn, -0.1f * s, 0, 0));
        Add(tool, Block(0.32f * s, 0.55f * s, 0.32f * s), At(turn, 0.22f * s, -0.175f * s, 0));
        for (var row = 0; row < 5; row++)
            for (var col = 0; col < 3; col++)
                Add(tool, Block(0.07f * s, 0.09f * s, 0.03f * s), At(turn, (-0.21f + col * 0.11f) * s, (0.3f - row * 0.16f) * s, 0.185f * s));
        for (var row = 0; row < 3; row++)
            Add(tool, Block(0.2f * s, 0.07f * s, 0.03f * s), At(turn, 0.22f * s, (0.0f - row * 0.15f) * s, 0.165f * s));
        Rod(tool, new Vector3(-0.1f * s, 0.45f * s, 0), new Vector3(-0.1f * s, 0.62f * s, 0), 0.015f * s, new Transform3D(turn, Vector3.Zero));
        return tool.Commit();
    }

    /// <summary>A factory: a hall with a saw-tooth roof and two chimneys.</summary>
    private static Mesh Factory(float s)
    {
        var tool = new SurfaceTool();
        var turn = Turn(0, -25, 0);
        Add(tool, Block(0.84f * s, 0.38f * s, 0.42f * s), At(turn, 0, -0.25f * s, 0));
        for (var i = 0; i < 3; i++)
            Add(tool, new PrismMesh { Size = new Vector3(0.28f, 0.16f, 0.42f) * s, LeftToRight = 0 }, At(turn, (-0.28f + i * 0.28f) * s, 0.02f * s, 0));
        Add(tool, Cylinder(0.045f * s, 0.06f * s, 0.55f * s), At(turn, 0.28f * s, 0.2f * s, -0.12f * s));
        Add(tool, Cylinder(0.04f * s, 0.055f * s, 0.42f * s), At(turn, 0.12f * s, 0.14f * s, -0.12f * s));
        for (var i = 0; i < 4; i++)
            Add(tool, Block(0.12f * s, 0.1f * s, 0.03f * s), At(turn, (-0.3f + i * 0.2f) * s, -0.25f * s, 0.215f * s));
        return tool.Commit();
    }

    /// <summary>A globe on its stand, its meridian ring tilted like the Earth's axis.</summary>
    private static Mesh Globe(float s)
    {
        var tool = new SurfaceTool();
        Add(tool, Ball(0.34f * s, 32), At(Basis.Identity, 0, 0.1f * s, 0));
        Add(tool, Torus(0.38f * s, 0.42f * s), At(Turn(0, 0, 23) * Turn(0, 0, 90), 0, 0.1f * s, 0));
        Add(tool, Cylinder(0.04f * s, 0.05f * s, 0.16f * s), At(Basis.Identity, 0, -0.33f * s, 0));
        Add(tool, Cylinder(0.2f * s, 0.24f * s, 0.05f * s), At(Basis.Identity, 0, -0.43f * s, 0));
        return tool.Commit();
    }

    /// <summary>A map pin: a ball on a point, with a ring in its head.</summary>
    private static Mesh Pin(float s)
    {
        var tool = new SurfaceTool();
        Add(tool, Ball(0.27f * s, 28), At(Basis.Identity, 0, 0.2f * s, 0));
        Add(tool, Cylinder(0.24f * s, 0, 0.5f * s), At(Basis.Identity, 0, -0.18f * s, 0));
        Add(tool, Torus(0.08f * s, 0.13f * s), At(Standing, 0, 0.22f * s, 0.24f * s));
        return tool.Commit();
    }

    /// <summary>A parcel: a box crossed by two ribbons, a bow on top.</summary>
    private static Mesh Parcel(float s)
    {
        var tool = new SurfaceTool();
        var turn = Turn(0, -25, 0);
        Add(tool, Block(0.6f * s, 0.45f * s, 0.45f * s), At(turn, 0, -0.12f * s, 0));
        Add(tool, Block(0.62f * s, 0.47f * s, 0.08f * s), At(turn, 0, -0.12f * s, 0));
        Add(tool, Block(0.08f * s, 0.47f * s, 0.47f * s), At(turn, 0, -0.12f * s, 0));
        Add(tool, Torus(0.04f * s, 0.1f * s), At(turn * Turn(90, 0, 40), -0.08f * s, 0.18f * s, 0));
        Add(tool, Torus(0.04f * s, 0.1f * s), At(turn * Turn(90, 0, -40), 0.08f * s, 0.18f * s, 0));
        return tool.Commit();
    }

    /// <summary>A flag on its pole, standing on a base, a ball at the top of the pole.</summary>
    private static Mesh Flag(float s)
    {
        var tool = new SurfaceTool();
        Add(tool, Cylinder(0.022f * s, 0.022f * s, 1.0f * s, 12), At(Basis.Identity, -0.3f * s, 0, 0));
        Add(tool, Ball(0.045f * s), At(Basis.Identity, -0.3f * s, 0.52f * s, 0));
        Add(tool, Cylinder(0.14f * s, 0.18f * s, 0.05f * s), At(Basis.Identity, -0.3f * s, -0.5f * s, 0));
        // The cloth in two panels, as if it floated.
        Add(tool, Block(0.3f * s, 0.32f * s, 0.02f * s), At(Turn(0, 12, 0), -0.13f * s, 0.3f * s, 0.015f * s));
        Add(tool, Block(0.3f * s, 0.32f * s, 0.02f * s), At(Turn(0, -12, 0), 0.155f * s, 0.29f * s, 0.015f * s));
        return tool.Commit();
    }

    /// <summary>A painting in its frame, on an easel of three legs.</summary>
    private static Mesh Painting(float s)
    {
        var tool = new SurfaceTool();
        var lean = Turn(-10, -15, 0);
        var frame = new Transform3D(lean, new Vector3(0, 0.15f * s, 0.04f * s));
        Add(tool, Block(0.56f * s, 0.42f * s, 0.02f * s), frame);
        foreach (var (w, h, x, y) in new[] { (0.66f, 0.06f, 0f, 0.24f), (0.66f, 0.06f, 0f, -0.24f), (0.06f, 0.54f, 0.3f, 0f), (0.06f, 0.54f, -0.3f, 0f) })
            Add(tool, Block(w * s, h * s, 0.06f * s), frame * At(Basis.Identity, x * s, y * s, 0.01f * s));
        var easel = new Transform3D(Turn(0, -15, 0), Vector3.Zero);
        Rod(tool, new Vector3(-0.22f, -0.55f, 0.12f) * s, new Vector3(-0.05f, 0.45f, 0) * s, 0.02f * s, easel);
        Rod(tool, new Vector3(0.22f, -0.55f, 0.12f) * s, new Vector3(0.05f, 0.45f, 0) * s, 0.02f * s, easel);
        Rod(tool, new Vector3(0, -0.55f, -0.3f) * s, new Vector3(0, 0.4f, -0.02f) * s, 0.02f * s, easel);
        Add(tool, Block(0.5f * s, 0.03f * s, 0.08f * s), easel * At(Basis.Identity, 0, -0.1f * s, 0.07f * s));
        return tool.Commit();
    }

    /// <summary>A judge's gavel above its sound block.</summary>
    private static Mesh Gavel(float s)
    {
        var tool = new SurfaceTool();
        // Turned a little and tilted, the handle going down to the right.
        var pose = new Transform3D(Turn(0, -20, -20), Vector3.Zero);
        var lying = Turn(0, 0, 90);
        Add(tool, Cylinder(0.11f * s, 0.11f * s, 0.42f * s), pose * At(lying, -0.1f * s, 0.22f * s, 0));
        foreach (var end in new[] { -1, 1 })
            Add(tool, Cylinder(0.125f * s, 0.125f * s, 0.04f * s), pose * At(lying, (-0.1f + end * 0.19f) * s, 0.22f * s, 0));
        Rod(tool, new Vector3(-0.1f, 0.11f, 0) * s, new Vector3(-0.1f, -0.3f, 0) * s, 0.03f * s, pose);
        Add(tool, Cylinder(0.22f * s, 0.24f * s, 0.09f * s), At(Turn(0, -20, 0), 0.1f * s, -0.42f * s, 0));
        return tool.Commit();
    }

    /// <summary>A speech bubble with its tail, three dots on its front.</summary>
    private static Mesh SpeechBubble(float s)
    {
        var tool = new SurfaceTool();
        Add(tool, Ball(0.4f * s, 32), new Transform3D(Basis.Identity.Scaled(new Vector3(1.25f, 0.85f, 0.45f)), new Vector3(0, 0.08f * s, 0)));
        Add(tool, Cylinder(0.11f * s, 0, 0.3f * s), At(Turn(0, 0, -35), -0.26f * s, -0.32f * s, 0));
        for (var i = -1; i <= 1; i++)
            Add(tool, Ball(0.055f * s), At(Basis.Identity, i * 0.16f * s, 0.08f * s, 0.17f * s));
        return tool.Commit();
    }

    /// <summary>A calendar: its page with a header, its rings at the top and the squares of the days.</summary>
    private static Mesh Calendar(float s)
    {
        var tool = new SurfaceTool();
        var turn = Turn(-8, -15, 0);
        Add(tool, Block(0.72f * s, 0.66f * s, 0.06f * s), At(turn, 0, -0.06f * s, 0));
        Add(tool, Block(0.72f * s, 0.16f * s, 0.1f * s), At(turn, 0, 0.2f * s, 0.02f * s));
        foreach (var x in new[] { -0.2f, 0.2f })
            Add(tool, Torus(0.035f * s, 0.07f * s), At(turn * Turn(0, 90, 0), x * s, 0.3f * s, 0));
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 4; col++)
                Add(tool, Block(0.11f * s, 0.09f * s, 0.03f * s), At(turn, (-0.24f + col * 0.16f) * s, (0.0f - row * 0.13f) * s, 0.04f * s));
        return tool.Commit();
    }

    /// <summary>An hourglass: two glass cones point to point between two discs, held by three rods.</summary>
    private static Mesh Hourglass(float s)
    {
        var tool = new SurfaceTool();
        foreach (var y in new[] { -0.46f, 0.46f })
            Add(tool, Cylinder(0.3f * s, 0.3f * s, 0.06f * s), At(Basis.Identity, 0, y * s, 0));
        Add(tool, Cylinder(0.24f * s, 0.03f * s, 0.42f * s), At(Basis.Identity, 0, 0.22f * s, 0));
        Add(tool, Cylinder(0.03f * s, 0.24f * s, 0.42f * s), At(Basis.Identity, 0, -0.22f * s, 0));
        for (var i = 0; i < 3; i++)
        {
            var angle = i * Mathf.Tau / 3 + 0.4f;
            var x = Mathf.Cos(angle) * 0.27f * s;
            var z = Mathf.Sin(angle) * 0.27f * s;
            Rod(tool, new Vector3(x, -0.43f * s, z), new Vector3(x, 0.43f * s, z), 0.022f * s, Transform3D.Identity);
        }
        return tool.Commit();
    }

    /// <summary>The % sign: two rings and the slanted bar between them.</summary>
    private static Mesh Percent(float s)
    {
        var tool = new SurfaceTool();
        Add(tool, Torus(0.07f * s, 0.15f * s), At(Standing, -0.22f * s, 0.24f * s, 0));
        Add(tool, Torus(0.07f * s, 0.15f * s), At(Standing, 0.22f * s, -0.24f * s, 0));
        Add(tool, Block(0.09f * s, 1.0f * s, 0.09f * s), At(Turn(0, 0, -38), 0, 0, 0));
        return tool.Commit();
    }

    /// <summary>Money: two stacks of coins and one coin standing in front.</summary>
    private static Mesh Coins(float s)
    {
        var tool = new SurfaceTool();
        for (var i = 0; i < 6; i++)
            Add(tool, Cylinder(0.24f * s, 0.24f * s, 0.06f * s, 32), At(Basis.Identity, (-0.17f + (i % 2) * 0.012f) * s, (-0.42f + i * 0.065f) * s, -0.05f * s));
        for (var i = 0; i < 3; i++)
            Add(tool, Cylinder(0.2f * s, 0.2f * s, 0.06f * s, 32), At(Basis.Identity, (0.25f - (i % 2) * 0.01f) * s, (-0.42f + i * 0.065f) * s, -0.1f * s));
        Add(tool, Cylinder(0.2f * s, 0.2f * s, 0.06f * s, 32), At(Standing, 0.2f * s, -0.16f * s, 0.22f * s));
        Add(tool, Cylinder(0.12f * s, 0.12f * s, 0.07f * s, 32), At(Standing, 0.2f * s, -0.16f * s, 0.22f * s));
        return tool.Commit();
    }

    /// <summary>A weight: a truncated cone with its handle.</summary>
    private static Mesh Weight(float s)
    {
        var tool = new SurfaceTool();
        Add(tool, Cylinder(0.24f * s, 0.38f * s, 0.5f * s, 32), At(Basis.Identity, 0, -0.22f * s, 0));
        Add(tool, Torus(0.11f * s, 0.18f * s), At(Standing, 0, 0.16f * s, 0));
        return tool.Commit();
    }

    /// <summary>A podium: the steps of the second, first and third places.</summary>
    private static Mesh Podium(float s)
    {
        var tool = new SurfaceTool();
        var turn = Turn(0, -20, 0);
        foreach (var (x, h) in new[] { (-0.32f, 0.4f), (0f, 0.6f), (0.32f, 0.28f) })
            Add(tool, Block(0.3f * s, h * s, 0.34f * s), At(turn, x * s, (-0.45f + h / 2) * s, 0));
        Add(tool, Ball(0.08f * s), At(turn, 0, 0.23f * s, 0));
        return tool.Commit();
    }

    /// <summary>A die, turned so that three faces show, with its pips.</summary>
    private static Mesh Die(float s)
    {
        var tool = new SurfaceTool();
        var turn = Turn(-25, -35, 0);
        var half = 0.3f;
        Add(tool, Block(0.6f * s, 0.6f * s, 0.6f * s), At(turn, 0, 0, 0));
        var pip = Ball(0.05f * s, 12);
        void Pips(Vector3 normal, Vector3 u, Vector3 v, (float, float)[] spots)
        {
            foreach (var (a, b) in spots)
                Add(tool, pip, At(turn, (normal * half + u * a + v * b).X * s, (normal * half + u * a + v * b).Y * s, (normal * half + u * a + v * b).Z * s));
        }
        Pips(Vector3.Back, Vector3.Right, Vector3.Up, [(-0.15f, 0.15f), (0.15f, 0.15f), (0, 0), (-0.15f, -0.15f), (0.15f, -0.15f)]);
        Pips(Vector3.Up, Vector3.Right, Vector3.Back, [(-0.15f, -0.15f), (0, 0), (0.15f, 0.15f)]);
        Pips(Vector3.Right, Vector3.Back, Vector3.Up, [(-0.12f, 0.12f), (0.12f, -0.12f)]);
        return tool.Commit();
    }

    /// <summary>An apple: its round body, its stalk and a leaf.</summary>
    private static Mesh Apple(float s)
    {
        var tool = new SurfaceTool();
        Add(tool, Ball(0.38f * s, 32), new Transform3D(Basis.Identity.Scaled(new Vector3(1, 0.88f, 1)), new Vector3(0, -0.08f * s, 0)));
        Rod(tool, new Vector3(0, 0.2f, 0) * s, new Vector3(0.04f, 0.42f, 0) * s, 0.025f * s, Transform3D.Identity);
        Add(tool, Ball(0.1f * s), new Transform3D(Turn(0, 0, 30) * Basis.Identity.Scaled(new Vector3(1.6f, 0.45f, 0.8f)), new Vector3(0.17f * s, 0.36f * s, 0)));
        return tool.Commit();
    }

    /// <summary>Geometric solids: a cube, a ball and a pyramid on top of them.</summary>
    private static Mesh Solids(float s)
    {
        var tool = new SurfaceTool();
        Add(tool, Block(0.34f * s, 0.34f * s, 0.34f * s), At(Turn(0, 20, 0), -0.2f * s, -0.28f * s, 0));
        Add(tool, Ball(0.18f * s, 28), At(Basis.Identity, 0.25f * s, -0.27f * s, 0.05f * s));
        Add(tool, Cylinder(0, 0.28f * s, 0.42f * s, 4), At(Turn(0, 45, 0), 0, 0.2f * s, 0));
        return tool.Commit();
    }

    /// <summary>A robot head: its face with two eyes and a mouth, its ears and its antenna.</summary>
    private static Mesh RobotHead(float s)
    {
        var tool = new SurfaceTool();
        var turn = Turn(0, -15, 0);
        Add(tool, Block(0.56f * s, 0.46f * s, 0.44f * s), At(turn, 0, -0.08f * s, 0));
        foreach (var x in new[] { -0.13f, 0.13f })
            Add(tool, Ball(0.075f * s, 16), At(turn, x * s, 0.0f, 0.22f * s));
        Add(tool, Block(0.26f * s, 0.05f * s, 0.03f * s), At(turn, 0, -0.17f * s, 0.225f * s));
        foreach (var x in new[] { -0.31f, 0.31f })
            Add(tool, Cylinder(0.07f * s, 0.07f * s, 0.08f * s), At(turn * Turn(0, 0, 90), x * s, -0.08f * s, 0));
        Rod(tool, new Vector3(0, 0.15f, 0) * s, new Vector3(0, 0.36f, 0) * s, 0.018f * s, new Transform3D(turn, Vector3.Zero));
        Add(tool, Ball(0.05f * s), At(turn, 0, 0.39f * s, 0));
        return tool.Commit();
    }

    /// <summary>A flowchart: a step, a decision (a diamond) and its two branches, joined by lines.</summary>
    private static Mesh Flowchart(float s)
    {
        var tool = new SurfaceTool();
        var plane = new Transform3D(Turn(0, -15, 0), Vector3.Zero);
        Add(tool, Block(0.42f * s, 0.16f * s, 0.08f * s), plane * At(Basis.Identity, 0, 0.42f * s, 0));
        Add(tool, Block(0.26f * s, 0.26f * s, 0.08f * s), plane * At(Turn(0, 0, 45), 0, 0.03f * s, 0));
        Add(tool, Block(0.32f * s, 0.16f * s, 0.08f * s), plane * At(Basis.Identity, -0.3f * s, -0.38f * s, 0));
        Add(tool, Block(0.32f * s, 0.16f * s, 0.08f * s), plane * At(Basis.Identity, 0.3f * s, -0.38f * s, 0));
        Rod(tool, new Vector3(0, 0.34f, 0) * s, new Vector3(0, 0.21f, 0) * s, 0.018f * s, plane);
        Rod(tool, new Vector3(-0.17f, 0.0f, 0) * s, new Vector3(-0.3f, -0.3f, 0) * s, 0.018f * s, plane);
        Rod(tool, new Vector3(0.17f, 0.0f, 0) * s, new Vector3(0.3f, -0.3f, 0) * s, 0.018f * s, plane);
        return tool.Commit();
    }

    /// <summary>A material: three bricks, two below and one above.</summary>
    private static Mesh Bricks(float s)
    {
        var tool = new SurfaceTool();
        var turn = Turn(0, -25, 0);
        foreach (var (x, y) in new[] { (-0.21f, -0.25f), (0.21f, -0.25f), (0f, -0.04f) })
            Add(tool, Block(0.4f * s, 0.19f * s, 0.24f * s), At(turn, x * s, y * s, 0));
        Add(tool, Block(0.4f * s, 0.19f * s, 0.24f * s), At(turn * Turn(0, 90, 0), 0, 0.17f * s, 0));
        return tool.Commit();
    }

    /// <summary>An ontology class: a node and its three children, joined by branches.</summary>
    private static Mesh Tree(float s)
    {
        var tool = new SurfaceTool();
        var root = new Vector3(0, 0.33f, 0) * s;
        Add(tool, Ball(0.14f * s), At(Basis.Identity, root.X, root.Y, root.Z));
        foreach (var x in new[] { -0.33f, 0f, 0.33f })
        {
            var child = new Vector3(x, -0.3f, x == 0 ? 0.1f : 0) * s;
            Add(tool, Block(0.17f * s, 0.17f * s, 0.17f * s), At(Basis.Identity, child.X, child.Y, child.Z));
            Rod(tool, root, child, 0.022f * s, Transform3D.Identity);
        }
        return tool.Commit();
    }

    /// <summary>A gem (an octahedron): the shape of the miscellaneous entities.</summary>
    private static Mesh Gem(float s)
    {
        var tool = new SurfaceTool();
        Add(tool, Cylinder(0, 0.36f * s, 0.42f * s, 4), At(Turn(0, 20, 0), 0, 0.21f * s, 0));
        Add(tool, Cylinder(0.36f * s, 0, 0.42f * s, 4), At(Turn(0, 20, 0), 0, -0.21f * s, 0));
        return tool.Commit();
    }

    /// <summary>A human body: a head, a torso, two arms a little apart and two legs, standing (from -0.53 to +0.56 of the size).</summary>
    private static Mesh Person(float s)
    {
        var tool = new SurfaceTool();
        Add(tool, new SphereMesh { Radius = 0.14f * s, Height = 0.28f * s, RadialSegments = 24, Rings = 12 }, At(0, 0.42f * s, 0));
        Add(tool, new CapsuleMesh { Radius = 0.12f * s, Height = 0.44f * s, RadialSegments = 20, Rings = 6 },
            At(0, 0.1f * s, 0).ScaledLocal(new Vector3(1.3f, 1, 0.75f)));
        foreach (var side in new[] { -1, 1 })
        {
            Add(tool, new CapsuleMesh { Radius = 0.05f * s, Height = 0.4f * s, RadialSegments = 12, Rings = 4 },
                At(side * 0.21f * s, 0.08f * s, 0).RotatedLocal(Vector3.Back, side * Mathf.DegToRad(12)));
            Add(tool, new CapsuleMesh { Radius = 0.065f * s, Height = 0.5f * s, RadialSegments = 12, Rings = 4 },
                At(side * 0.075f * s, -0.28f * s, 0));
        }
        return tool.Commit();
    }

    /// <summary>A CD-ROM standing, a little turned and leaning back: a thin flat disc with its hole, the thicker ring around the hole, and the edge of its inner band.</summary>
    private static Mesh CdRom(float s)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        var standing = Basis.FromEuler(new Vector3(Mathf.DegToRad(-25), Mathf.DegToRad(-20), 0));
        Ring(tool, standing, 0.1f * s, 0.6f * s, 0.025f * s);
        Ring(tool, standing, 0.1f * s, 0.19f * s, 0.1f * s);
        Ring(tool, standing, 0.27f * s, 0.29f * s, 0.045f * s);
        return tool.Commit();
    }

    /// <summary>A flat ring facing the front (+Z) turned by <paramref name="basis"/>: its two faces, its outer and inner edges.</summary>
    private static void Ring(SurfaceTool tool, Basis basis, float inner, float outer, float thickness)
    {
        const int segments = 72;
        var half = thickness / 2;
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            tool.SetNormal(basis * normal);
            foreach (var v in new[] { a, b, c, a, c, d })
                tool.AddVertex(basis * v);
        }
        for (var i = 0; i < segments; i++)
        {
            var a0 = i * Mathf.Tau / segments;
            var a1 = (i + 1) * Mathf.Tau / segments;
            var d0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0);
            var d1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0);
            var front = Vector3.Back * half;
            var back = Vector3.Forward * half;
            // Godot draws clockwise faces as the front ones.
            Quad(d0 * inner + front, d0 * outer + front, d1 * outer + front, d1 * inner + front, Vector3.Back);
            Quad(d1 * inner + back, d1 * outer + back, d0 * outer + back, d0 * inner + back, Vector3.Forward);
            Quad(d0 * outer + front, d0 * outer + back, d1 * outer + back, d1 * outer + front, (d0 + d1).Normalized());
            Quad(d1 * inner + front, d1 * inner + back, d0 * inner + back, d0 * inner + front, -(d0 + d1).Normalized());
        }
    }

    /// <summary>A PCI card standing: its board, its chips, its gold fingers (with the key notch) and its metal bracket.</summary>
    private static Mesh PciCard(float s)
    {
        var tool = new SurfaceTool();
        // A little turned, so that its depth (the chips) shows from the front.
        var turn = new Transform3D(Basis.FromEuler(new Vector3(0, Mathf.DegToRad(-20), 0)), Vector3.Zero);
        void Box(float width, float height, float depth, float x, float y, float z) =>
            Add(tool, new BoxMesh { Size = new Vector3(width, height, depth) * s }, turn * At(x * s, y * s, z * s));
        Box(1.1f, 0.5f, 0.03f, 0, 0, 0);
        // The chips: a large processor with its heat sink, two memories, a small controller.
        Box(0.26f, 0.22f, 0.05f, -0.18f, 0.04f, 0.04f);
        for (var fin = 0; fin < 5; fin++)
            Box(0.022f, 0.2f, 0.06f, -0.29f + fin * 0.055f, 0.04f, 0.095f);
        Box(0.12f, 0.08f, 0.035f, 0.12f, 0.12f, 0.03f);
        Box(0.12f, 0.08f, 0.035f, 0.12f, -0.02f, 0.03f);
        Box(0.08f, 0.08f, 0.03f, 0.33f, 0.08f, 0.03f);
        // The fingers of the edge connector under the board, with the key notch.
        for (var finger = 0; finger < 22; finger++)
        {
            if (finger is 7 or 8)
                continue;
            Box(0.022f, 0.07f, 0.034f, -0.38f + finger * 0.032f, -0.285f, 0);
        }
        // The bracket at the end: a tall plate, its tab bent at the top.
        Box(0.02f, 0.66f, 0.13f, 0.57f, 0.03f, 0.05f);
        Box(0.1f, 0.02f, 0.13f, 0.53f, 0.36f, 0.05f);
        return tool.Commit();
    }

    private static Transform3D At(float x, float y, float z) => new(Basis.Identity, new Vector3(x, y, z));

    private static void Add(SurfaceTool tool, PrimitiveMesh part, Transform3D transform) =>
        tool.AppendFrom(part, 0, transform);
}
