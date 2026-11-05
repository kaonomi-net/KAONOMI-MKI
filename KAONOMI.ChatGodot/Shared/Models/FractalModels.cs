using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>The three kinds of fractals of /fractal.</summary>
public enum FractalFamily
{
    /// <summary>A formula iterated on each point of the complex plane (Mandelbrot, Julia, Newton…), drawn by the GPU.</summary>
    EscapeTime,

    /// <summary>A Lindenmayer system: an axiom rewritten by rules, then drawn by a turtle (Koch, dragon, plants…).</summary>
    LSystem,

    /// <summary>An iterated function system: affine maps drawn by the chaos game (Barnsley fern, Sierpinski…).</summary>
    Ifs
}

/// <summary>How a point of an escape-time fractal is coloured.</summary>
public enum FractalColoring
{
    /// <summary>By the smoothed number of iterations before |z| leaves the bailout radius (Mandelbrot, Julia…).</summary>
    Escape,

    /// <summary>By the root z converges to (its angle) and the iterations it took (Newton…).</summary>
    Convergence
}

/// <summary>
/// A cosine palette (after Inigo Quilez): colour(t) = a + b × cos(2π (c × t + d)), each a colour of three numbers.
/// </summary>
public sealed record FractalPalette(string Name, double[] A, double[] B, double[] C, double[] D)
{
    /// <summary>The palettes offered, the first one by default.</summary>
    public static IReadOnlyList<FractalPalette> All { get; } =
    [
        new("Rainbow", [0.5, 0.5, 0.5], [0.5, 0.5, 0.5], [1.0, 1.0, 1.0], [0.00, 0.33, 0.67]),
        new("Ember", [0.5, 0.5, 0.5], [0.5, 0.5, 0.5], [1.0, 1.0, 1.0], [0.00, 0.10, 0.20]),
        new("Lagoon", [0.5, 0.5, 0.5], [0.5, 0.5, 0.5], [1.0, 1.0, 0.5], [0.80, 0.90, 0.30]),
        new("Sunset", [0.5, 0.5, 0.5], [0.5, 0.5, 0.5], [1.0, 0.7, 0.4], [0.00, 0.15, 0.20]),
        new("Candy", [0.5, 0.5, 0.5], [0.5, 0.5, 0.5], [2.0, 1.0, 0.0], [0.50, 0.20, 0.25]),
        new("Rust", [0.8, 0.5, 0.4], [0.2, 0.4, 0.2], [2.0, 1.0, 1.0], [0.00, 0.25, 0.25]),
        new("EVE", [0.35, 0.55, 0.65], [0.35, 0.40, 0.35], [1.0, 1.0, 1.0], [0.55, 0.60, 0.65]),
        new("Mono", [0.5, 0.5, 0.5], [0.5, 0.5, 0.5], [1.0, 1.0, 1.0], [0.00, 0.00, 0.00])
    ];

    /// <summary>The palette named <paramref name="name"/>, else the first one.</summary>
    public static FractalPalette Find(string? name)
    {
        foreach (var palette in All)
            if (string.Equals(palette.Name, name, StringComparison.OrdinalIgnoreCase))
                return palette;
        return All[0];
    }

    /// <summary>The colour at <paramref name="t"/> (its three components, 0 to 1).</summary>
    public (double R, double G, double B) At(double t)
    {
        double Channel(int k) => Math.Clamp(A[k] + B[k] * Math.Cos(2 * Math.PI * (C[k] * t + D[k])), 0, 1);
        return (Channel(0), Channel(1), Channel(2));
    }
}

/// <summary>
/// A fractal of /fractal: an example or one made by the user (saved as JSON in ~/kaonomi/fractals). Only the fields of
/// its <see cref="Family"/> matter; the view (<see cref="CenterX"/>, <see cref="CenterY"/>, <see cref="Scale"/>) is kept
/// with it.
/// </summary>
public sealed class FractalDefinition
{
    public string Name { get; set; } = "New fractal";

    public string Description { get; set; } = string.Empty;

    public FractalFamily Family { get; set; }

    // ── The view ──

    /// <summary>The centre of the view: a point of the complex plane, or of the drawing fitted in [-1, 1].</summary>
    public double CenterX { get; set; }

    public double CenterY { get; set; }

    /// <summary>Half the height of the view, in the units of the plane (smaller: zoomed in).</summary>
    public double Scale { get; set; } = 1.5;

    public string Palette { get; set; } = "Rainbow";

    /// <summary>How fast the colours go round the palette.</summary>
    public double Density { get; set; } = 1;

    /// <summary>Where the palette starts (0 to 1).</summary>
    public double Offset { get; set; }

    // ── Escape time ──

    /// <summary>The iteration: the next z from z, c, zp (the z before), a, b, p (the pixel), n (the iteration).</summary>
    public string Formula { get; set; } = "z^2 + c";

    /// <summary>False: c is the pixel and z starts at z0 (Mandelbrot); true: z starts at the pixel and c is fixed (Julia).</summary>
    public bool Julia { get; set; }

    public double Cx { get; set; }

    public double Cy { get; set; }

    public double Z0x { get; set; }

    public double Z0y { get; set; }

    public double Ax { get; set; }

    public double Ay { get; set; }

    public double Bx { get; set; }

    public double By { get; set; }

    public int Iterations { get; set; } = 300;

    public double Bailout { get; set; } = 64;

    /// <summary>The degree of the formula (2 for z² + c), for the smooth colouring.</summary>
    public double Degree { get; set; } = 2;

    public FractalColoring Coloring { get; set; }

    // ── L-system ──

    public string Axiom { get; set; } = "F";

    /// <summary>The rules, one per line: <c>F=F+F--F+F</c>.</summary>
    public string Rules { get; set; } = string.Empty;

    /// <summary>The turn of + and -, in degrees.</summary>
    public double Angle { get; set; } = 90;

    /// <summary>The rewrites of the axiom.</summary>
    public int Depth { get; set; } = 4;

    /// <summary>The direction the turtle starts in, in degrees (0: to the right, 90: up).</summary>
    public double Heading { get; set; }

    /// <summary>The letters that draw a line (the others only rewrite).</summary>
    public string DrawSymbols { get; set; } = "FGAB";

    // ── IFS ──

    /// <summary>The affine maps, one per line: <c>a b c d e f [p]</c> for x' = a x + b y + e, y' = c x + d y + f.</summary>
    public string Maps { get; set; } = string.Empty;

    /// <summary>The points of the chaos game.</summary>
    public int Points { get; set; } = 400_000;

    /// <summary>True for the examples (never saved nor deleted).</summary>
    [JsonIgnore]
    public bool IsExample { get; set; }

    /// <summary>The file it was read from or saved to (null: an example, or not saved yet).</summary>
    [JsonIgnore]
    public string? FilePath { get; set; }

    public FractalDefinition Clone()
    {
        var copy = (FractalDefinition)MemberwiseClone();
        copy.IsExample = false;
        copy.FilePath = null;
        return copy;
    }
}

/// <summary>The classic fractals offered by /fractal.</summary>
public static class FractalExamples
{
    public static IReadOnlyList<FractalDefinition> All { get; } = Build();

    private static List<FractalDefinition> Build()
    {
        FractalDefinition Escape(string name, string description, string formula, double cx, double cy, double scale, Action<FractalDefinition>? more = null)
        {
            var d = new FractalDefinition { Name = name, Description = description, Family = FractalFamily.EscapeTime, Formula = formula, CenterX = cx, CenterY = cy, Scale = scale, IsExample = true };
            more?.Invoke(d);
            return d;
        }
        FractalDefinition Julia(string name, string description, double cx, double cy, string palette = "Rainbow") =>
            Escape(name, description, "z^2 + c", 0, 0, 1.4, d => { d.Julia = true; d.Cx = cx; d.Cy = cy; d.Palette = palette; d.Iterations = 400; });
        FractalDefinition LSystem(string name, string description, string axiom, string rules, double angle, int depth, double heading = 0, string draw = "FGAB", string palette = "EVE")
            => new() { Name = name, Description = description, Family = FractalFamily.LSystem, Axiom = axiom, Rules = rules, Angle = angle, Depth = depth, Heading = heading, DrawSymbols = draw, Palette = palette, Scale = 1.08, IsExample = true };
        FractalDefinition Ifs(string name, string description, string maps, string palette = "Lagoon")
            => new() { Name = name, Description = description, Family = FractalFamily.Ifs, Maps = maps, Palette = palette, Scale = 1.08, IsExample = true };

        return
        [
            // ── Escape time ──
            Escape("Mandelbrot set", "z → z² + c, z starting at 0 and c the pixel: the points whose orbit stays bounded (Benoît Mandelbrot, 1980).",
                "z^2 + c", -0.6, 0, 1.25),
            Escape("Mandelbrot · Seahorse valley", "The valley between the main cardioid and the bulb of period 2, full of seahorse spirals.",
                "z^2 + c", -0.7453, 0.1127, 0.0065, d => { d.Iterations = 800; d.Palette = "Lagoon"; d.Density = 2; }),
            Escape("Mandelbrot · Elephant valley", "The valley on the right of the main cardioid, its trunks curling around tiny copies of the set.",
                "z^2 + c", 0.298, 0.018, 0.012, d => { d.Iterations = 700; d.Palette = "Sunset"; d.Density = 1.5; }),
            Julia("Julia · Douady rabbit", "The Julia set of c = -0.123 + 0.745i: a rabbit with three ears at every point.", -0.123, 0.745),
            Julia("Julia · Dendrite", "c = i: a tree with no interior, every branch splitting forever.", 0, 1, "Ember"),
            Julia("Julia · San Marco", "c = -0.75: the basilica of San Marco and its reflection in the water.", -0.75, 0, "Sunset"),
            Julia("Julia · Siegel disk", "c = -0.391 - 0.587i: the orbits turn around a disk without ever entering it.", -0.391, -0.587, "Lagoon"),
            Julia("Julia · Spirals", "c = -0.8 + 0.156i: double spirals strung along a curve.", -0.8, 0.156, "Candy"),
            Julia("Julia · Fatou dust", "c = 0.37 + 0.1i, just outside the Mandelbrot set: the Julia set breaks into a dust of points (a Cantor set).", 0.37, 0.1, "Ember"),
            Escape("Burning Ship", "z → (|Re z| + i|Im z|)² + c (Michael Michelitsch, Otto Rössler, 1992): the ship burns along the real axis.",
                "fold(z)^2 + c", -0.45, -0.5, 1.1, d => { d.Palette = "Ember"; }),
            Escape("Burning Ship · Armada", "The small ships along the mast of the Burning Ship.",
                "fold(z)^2 + c", -1.762, -0.028, 0.045, d => { d.Palette = "Ember"; d.Iterations = 500; d.Density = 1.5; }),
            Escape("Tricorn (Mandelbar)", "z → conj(z)² + c: the complex conjugate gives three corners.",
                "conj(z)^2 + c", -0.3, 0, 1.4, d => { d.Palette = "Candy"; }),
            Escape("Multibrot 3", "z → z³ + c: two-fold symmetry.", "z^3 + c", 0, 0, 1.25, d => { d.Degree = 3; d.Palette = "Lagoon"; }),
            Escape("Multibrot 5", "z → z⁵ + c: four-fold symmetry.", "z^5 + c", 0, 0, 1.25, d => { d.Degree = 5; d.Palette = "Sunset"; }),
            Escape("Newton · z³ - 1", "Newton's method on z³ - 1 from each pixel: the three basins of the cube roots of 1, braided on their borders.",
                "z - (z^3 - 1) / (3*z^2)", 0, 0, 1.6, d => { d.Julia = true; d.Coloring = FractalColoring.Convergence; d.Iterations = 60; d.Palette = "Rainbow"; }),
            Escape("Newton · z⁵ - 1", "Newton's method on z⁵ - 1: five basins.",
                "z - (z^5 - 1) / (5*z^4)", 0, 0, 1.6, d => { d.Julia = true; d.Coloring = FractalColoring.Convergence; d.Iterations = 60; d.Palette = "Candy"; }),
            Escape("Newton · z³ - 2z + 2", "Newton's method on z³ - 2z + 2: in the black regions it never finds a root, caught in the cycle 0 → 1 → 0.",
                "z - (z^3 - 2*z + 2) / (3*z^2 - 2)", 0, 0, 1.8, d => { d.Julia = true; d.Coloring = FractalColoring.Convergence; d.Iterations = 80; d.Palette = "Sunset"; }),
            Escape("Phoenix", "z → z² + c + a·zp, zp being the z before (Shigehiro Ushiki, 1988), c = 0.5667, a = -0.5.",
                "z^2 + c + a*zp", 0, 0, 1.3, d => { d.Julia = true; d.Cx = 0.5667; d.Ax = -0.5; d.Palette = "Ember"; d.Iterations = 400; d.Bailout = 4; }),
            Escape("Magnet I", "z → ((z² + c - 1) / (2z + c - 2))²: a model of magnetism (renormalisation of the Ising model).",
                "((z^2 + c - 1) / (2*z + c - 2))^2", 1.4, 0, 2.6, d => { d.Bailout = 100; d.Iterations = 200; d.Palette = "Rust"; }),
            Escape("Sine Julia", "z → c · sin(z), c = 1 + 0.3i: a Julia set of a transcendental function.",
                "c * sin(z)", 0, 0, 3.2, d => { d.Julia = true; d.Cx = 1; d.Cy = 0.3; d.Bailout = 50; d.Iterations = 200; d.Palette = "Lagoon"; }),
            Escape("Mandelbrot · Mini Mandelbrot", "On the antenna, at c ≈ -1.7549: a whole copy of the set, its own bulbs and antennas (period 3).",
                "z^2 + c", -1.7549, 0, 0.03, d => { d.Iterations = 600; d.Palette = "Lagoon"; d.Density = 1.5; }),

            // ── L-systems ──
            LSystem("Koch snowflake", "Each segment gets a triangular bump, forever (Helge von Koch, 1904): an infinite perimeter around a finite area.",
                "F--F--F", "F=F+F--F+F", 60, 4),
            LSystem("Koch curve (quadratic)", "The bump is a square: the quadratic type 1 Koch curve.", "F", "F=F+F-F-F+F", 90, 4),
            LSystem("Sierpinski triangle", "A triangle with its middle taken away, in each of the three left, forever (Wacław Sierpiński, 1915).",
                "F-G-G", "F=F-G+F+G-F\nG=GG", 120, 6, palette: "Rainbow"),
            LSystem("Sierpinski arrowhead", "The Sierpinski triangle drawn in one stroke.", "A", "A=B-A-B\nB=A+B+A", 60, 8, palette: "Sunset"),
            LSystem("Dragon curve", "Fold a strip of paper in two again and again, then open it at right angles (Heighway, Harter, Banks).",
                "FX", "X=X+YF+\nY=-FX-Y", 90, 12, palette: "Ember"),
            LSystem("Lévy C curve", "Each segment becomes the two sides of a right isosceles triangle (Paul Lévy, 1938).",
                "F", "F=+F--F+", 45, 12, palette: "Candy"),
            LSystem("Hilbert curve", "A curve going through every point of the square (David Hilbert, 1891).",
                "A", "A=+BF-AFA-FB+\nB=-AF+BFB+FA-", 90, 6, palette: "Lagoon"),
            LSystem("Peano curve", "The first curve filling the square (Giuseppe Peano, 1890).",
                "X", "X=XFYFX+F+YFXFY-F-XFYFX\nY=YFXFY-F-XFYFX+F+YFXFY", 90, 4, palette: "Rust"),
            LSystem("Gosper curve", "The flowsnake: it tiles the plane with hexagonal islands (Bill Gosper, 1973).",
                "A", "A=A-B--B+A++AA+B-\nB=+A-BB--B-A++A+B", 60, 4, palette: "Lagoon"),
            LSystem("Fractal plant", "A plant of Aristid Lindenmayer (The Algorithmic Beauty of Plants): [ and ] keep and take back the place of the turtle.",
                "X", "X=F+[[X]-X]-F[-FX]+X\nF=FF", 25, 6, heading: 65, palette: "Lagoon"),
            LSystem("Bush", "A bush of The Algorithmic Beauty of Plants.", "F", "F=FF+[+F-F-F]-[-F+F+F]", 22.5, 4, heading: 90, palette: "Rust"),
            LSystem("Koch island (Minkowski)", "The quadratic Koch island: a square whose sides grow eight segments each.",
                "F+F+F+F", "F=F+F-F-FF+F+F-F", 90, 3, palette: "EVE"),

            // ── IFS ──
            Ifs("Barnsley fern", "Four affine maps chosen at random with their weights (Michael Barnsley, 1988): a fern grows.",
                "0     0     0     0.16  0  0     0.01\n0.85  0.04 -0.04  0.85  0  1.6   0.85\n0.2  -0.26  0.23  0.22  0  1.6   0.07\n-0.15 0.28  0.26  0.24  0  0.44  0.07"),
            Ifs("Sierpinski triangle (chaos game)", "Three maps halving the distance to a corner: the chaos game draws the Sierpinski triangle.",
                "0.5 0 0 0.5 0    0\n0.5 0 0 0.5 0.5  0\n0.5 0 0 0.5 0.25 0.433", "Sunset"),
            Ifs("Sierpinski carpet", "Eight maps of a third: the square with its middle taken away, forever.",
                "0.333 0 0 0.333 0     0\n0.333 0 0 0.333 0.333 0\n0.333 0 0 0.333 0.667 0\n0.333 0 0 0.333 0     0.333\n0.333 0 0 0.333 0.667 0.333\n0.333 0 0 0.333 0     0.667\n0.333 0 0 0.333 0.333 0.667\n0.333 0 0 0.333 0.667 0.667", "Candy"),
            Ifs("Koch curve (IFS)", "Four maps of a third, two of them turned by 60°.",
                "0.3333 0 0 0.3333 0 0\n0.1667 -0.2887 0.2887 0.1667 0.3333 0\n0.1667 0.2887 -0.2887 0.1667 0.5 0.2887\n0.3333 0 0 0.3333 0.6667 0", "EVE"),
            Ifs("Heighway dragon (IFS)", "Two maps turning by 45° and 135°, each shrinking by √2.",
                "0.5 -0.5 0.5 0.5 0 0\n-0.5 -0.5 0.5 -0.5 1 0", "Ember"),
            Ifs("Fractal tree", "A trunk and branches, each one a smaller copy of the tree.",
                "0 0 0 0.5 0 0 0.05\n0.42 -0.42 0.42 0.42 0 0.2 0.4\n0.42 0.42 -0.42 0.42 0 0.2 0.4\n0.1 0 0 0.1 0 0.2 0.15", "Lagoon"),
            Ifs("Maple leaf", "Four maps drawing a maple leaf.",
                "0.14 0.01 0 0.51 -0.08 -1.31 0.1\n0.43 0.52 -0.45 0.5 1.49 -0.75 0.35\n0.45 -0.49 0.47 0.47 -1.62 -0.74 0.35\n0.49 0 0 0.51 0.02 1.62 0.2", "Ember"),
            Ifs("Spiral", "A map turning and shrinking a little, and another one putting a copy at its side.",
                "0.787879 -0.424242 0.242424 0.859848 1.758647 1.408065 0.9\n-0.121212 0.257576 0.151515 0.053030 -6.721654 1.377236 0.05\n0.181818 -0.136364 0.090909 0.181818 6.086107 1.568035 0.05", "Rainbow")
        ];
    }
}
