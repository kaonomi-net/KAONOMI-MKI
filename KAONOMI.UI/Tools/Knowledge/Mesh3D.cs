using System;
using System.Collections.Generic;
using System.Numerics;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// A mesh of the 3D relation graph: triangles with a normal at each corner (positions and normals interleaved, six
/// floats a corner), and the primitives of Godot it is built from (box, cylinder, sphere, capsule, torus, prism) with
/// their sizes and placement, so that the shapes of the Godot chat are drawn the same. The transforms are the ones of
/// System.Numerics (row vectors: <c>a * b</c> applies a, then b).
/// </summary>
public sealed class Mesh3D
{
    private readonly List<float> _data = [];

    /// <summary>The corners: x, y, z, nx, ny, nz each.</summary>
    public float[] Data => [.. _data];

    /// <summary>The number of corners (three a triangle).</summary>
    public int Count => _data.Count / 6;

    /// <summary>The centre of its corners (for sorting the translucent meshes).</summary>
    public Vector3 Centre
    {
        get
        {
            if (Count == 0)
                return Vector3.Zero;
            var sum = Vector3.Zero;
            for (var i = 0; i < _data.Count; i += 6)
                sum += new Vector3(_data[i], _data[i + 1], _data[i + 2]);
            return sum / Count;
        }
    }

    public void Corner(Vector3 p, Vector3 n)
    {
        _data.Add(p.X);
        _data.Add(p.Y);
        _data.Add(p.Z);
        _data.Add(n.X);
        _data.Add(n.Y);
        _data.Add(n.Z);
    }

    /// <summary>A flat triangle (its normal from its corners).</summary>
    public void Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        n = n.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(n);
        Corner(a, n);
        Corner(b, n);
        Corner(c, n);
    }

    /// <summary>A flat quad a b c d (two triangles), with the normal given.</summary>
    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
    {
        foreach (var p in new[] { a, b, c, a, c, d })
            Corner(p, n);
    }

    /// <summary>Adds <paramref name="part"/> moved by <paramref name="transform"/> (its normals turned with it).</summary>
    public Mesh3D Add(Mesh3D part, Matrix4x4 transform)
    {
        var normals = Matrix4x4.Invert(transform, out var inverse) ? Matrix4x4.Transpose(inverse) : transform;
        var d = part._data;
        for (var i = 0; i < d.Count; i += 6)
        {
            var p = Vector3.Transform(new Vector3(d[i], d[i + 1], d[i + 2]), transform);
            var n = Vector3.TransformNormal(new Vector3(d[i + 3], d[i + 4], d[i + 5]), normals);
            Corner(p, n.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(n));
        }
        return this;
    }

    // ── The primitives of Godot ──

    /// <summary>A box of <paramref name="x"/> × <paramref name="y"/> × <paramref name="z"/> centred on the origin (Godot's BoxMesh).</summary>
    public static Mesh3D Box(float x, float y, float z)
    {
        var m = new Mesh3D();
        var h = new Vector3(x, y, z) / 2;
        Vector3 P(float a, float b, float c) => new(a * h.X, b * h.Y, c * h.Z);
        m.Quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), Vector3.UnitZ);
        m.Quad(P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), -Vector3.UnitZ);
        m.Quad(P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), Vector3.UnitX);
        m.Quad(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), -Vector3.UnitX);
        m.Quad(P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1), Vector3.UnitY);
        m.Quad(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), -Vector3.UnitY);
        return m;
    }

    /// <summary>
    /// A cylinder (or a cone, a radius at 0) along Y, centred, its caps closed (Godot's CylinderMesh); with few sides (a
    /// pyramid, a gem) its faces are flat.
    /// </summary>
    public static Mesh3D Cylinder(float top, float bottom, float height, int segments = 24)
    {
        var m = new Mesh3D();
        var y0 = -height / 2;
        var y1 = height / 2;
        var flat = segments <= 8;
        var slope = (bottom - top) / Math.Max(height, 1e-4f);
        Vector3 Dir(int i) => new(MathF.Cos(i * MathF.Tau / segments), 0, MathF.Sin(i * MathF.Tau / segments));
        Vector3 SideNormal(Vector3 d) => Vector3.Normalize(new Vector3(d.X, slope, d.Z));
        for (var i = 0; i < segments; i++)
        {
            var (d0, d1) = (Dir(i), Dir(i + 1));
            var (b0, b1, t0, t1) = (d0 * bottom + new Vector3(0, y0, 0), d1 * bottom + new Vector3(0, y0, 0), d0 * top + new Vector3(0, y1, 0), d1 * top + new Vector3(0, y1, 0));
            if (flat)
            {
                var n = SideNormal(Vector3.Normalize(d0 + d1));
                m.Quad(b0, t0, t1, b1, n);
            }
            else
            {
                var (n0, n1) = (SideNormal(d0), SideNormal(d1));
                m.Corner(b0, n0); m.Corner(t0, n0); m.Corner(t1, n1);
                m.Corner(b0, n0); m.Corner(t1, n1); m.Corner(b1, n1);
            }
            if (top > 0)
            {
                m.Corner(new Vector3(0, y1, 0), Vector3.UnitY); m.Corner(t1, Vector3.UnitY); m.Corner(t0, Vector3.UnitY);
            }
            if (bottom > 0)
            {
                m.Corner(new Vector3(0, y0, 0), -Vector3.UnitY); m.Corner(b0, -Vector3.UnitY); m.Corner(b1, -Vector3.UnitY);
            }
        }
        return m;
    }

    /// <summary>A sphere of <paramref name="radius"/>, <paramref name="height"/> high (an ellipsoid when it is not twice the radius; Godot's SphereMesh).</summary>
    public static Mesh3D Sphere(float radius, float height, int radial = 32, int rings = 16)
    {
        var m = new Mesh3D();
        var stretch = height / (2 * radius);
        Vector3 Unit(int ring, int seg)
        {
            var theta = MathF.PI * ring / rings;
            var phi = MathF.Tau * seg / radial;
            return new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
        }
        Vector3 P(Vector3 u) => new(u.X * radius, u.Y * radius * stretch, u.Z * radius);
        Vector3 N(Vector3 u) => Vector3.Normalize(new Vector3(u.X, u.Y / stretch, u.Z));
        for (var r = 0; r < rings; r++)
            for (var s = 0; s < radial; s++)
            {
                var (a, b, c, d) = (Unit(r, s), Unit(r + 1, s), Unit(r + 1, s + 1), Unit(r, s + 1));
                m.Corner(P(a), N(a)); m.Corner(P(b), N(b)); m.Corner(P(c), N(c));
                m.Corner(P(a), N(a)); m.Corner(P(c), N(c)); m.Corner(P(d), N(d));
            }
        return m;
    }

    /// <summary>A capsule along Y, <paramref name="height"/> high in all (its two half spheres included; Godot's CapsuleMesh).</summary>
    public static Mesh3D Capsule(float radius, float height, int radial = 16, int rings = 6)
    {
        var m = new Mesh3D();
        var half = Math.Max(0, height / 2 - radius);
        var stacks = rings * 2;
        // Rings from the top pole to the bottom one; the upper half shifted up, the lower one down.
        Vector3 Unit(int ring, int seg)
        {
            var theta = MathF.PI * ring / stacks;
            var phi = MathF.Tau * seg / radial;
            return new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Cos(theta), MathF.Sin(theta) * MathF.Sin(phi));
        }
        Vector3 P(Vector3 u, bool upper) => u * radius + new Vector3(0, upper ? half : -half, 0);
        for (var r = 0; r < stacks; r++)
        {
            var upper = r < rings;
            for (var s = 0; s < radial; s++)
            {
                var (a, b, c, d) = (Unit(r, s), Unit(r + 1, s), Unit(r + 1, s + 1), Unit(r, s + 1));
                var lowerB = r + 1 <= rings;
                m.Corner(P(a, upper), a); m.Corner(P(b, lowerB), b); m.Corner(P(c, lowerB), c);
                m.Corner(P(a, upper), a); m.Corner(P(c, lowerB), c); m.Corner(P(d, upper), d);
            }
            // The cylinder between the two halves, at the equator.
            if (r == rings - 1)
                for (var s = 0; s < radial; s++)
                {
                    var (d0, d1) = (Unit(rings, s), Unit(rings, s + 1));
                    m.Corner(P(d0, true), d0); m.Corner(P(d0, false), d0); m.Corner(P(d1, false), d1);
                    m.Corner(P(d0, true), d0); m.Corner(P(d1, false), d1); m.Corner(P(d1, true), d1);
                }
        }
        return m;
    }

    /// <summary>A torus around Y, from <paramref name="inner"/> to <paramref name="outer"/> (Godot's TorusMesh).</summary>
    public static Mesh3D Torus(float inner, float outer, int rings = 32, int ringSegments = 12)
    {
        var m = new Mesh3D();
        var centre = (inner + outer) / 2;
        var tube = (outer - inner) / 2;
        (Vector3 P, Vector3 N) At(int i, int j)
        {
            var phi = MathF.Tau * i / rings;
            var theta = MathF.Tau * j / ringSegments;
            var ring = new Vector3(MathF.Cos(phi), 0, MathF.Sin(phi));
            var n = ring * MathF.Cos(theta) + Vector3.UnitY * MathF.Sin(theta);
            return (ring * centre + n * tube, n);
        }
        for (var i = 0; i < rings; i++)
            for (var j = 0; j < ringSegments; j++)
            {
                var (a, b, c, d) = (At(i, j), At(i + 1, j), At(i + 1, j + 1), At(i, j + 1));
                m.Corner(a.P, a.N); m.Corner(b.P, b.N); m.Corner(c.P, c.N);
                m.Corner(a.P, a.N); m.Corner(c.P, c.N); m.Corner(d.P, d.N);
            }
        return m;
    }

    /// <summary>A prism: a triangle in XY (its top at <paramref name="leftToRight"/> of the width) drawn out along Z (Godot's PrismMesh).</summary>
    public static Mesh3D Prism(Vector3 size, float leftToRight)
    {
        var m = new Mesh3D();
        var h = size / 2;
        var apexX = -h.X + size.X * leftToRight;
        var (l, r, t) = (new Vector2(-h.X, -h.Y), new Vector2(h.X, -h.Y), new Vector2(apexX, h.Y));
        Vector3 F(Vector2 p) => new(p.X, p.Y, h.Z);
        Vector3 B(Vector2 p) => new(p.X, p.Y, -h.Z);
        m.Triangle(F(l), F(r), F(t));
        m.Triangle(B(r), B(l), B(t));
        foreach (var (a, b) in new[] { (l, r), (r, t), (t, l) })
            m.Triangle(F(a), B(a), B(b));
        foreach (var (a, b) in new[] { (l, r), (r, t), (t, l) })
            m.Triangle(F(a), B(b), F(b));
        return m;
    }

    // ── Transforms in the manner of Godot ──

    /// <summary>A rotation of Godot's Basis.FromEuler, in degrees (Y, then X, then Z: roll first).</summary>
    public static Matrix4x4 Turn(float x, float y, float z) =>
        Matrix4x4.CreateFromYawPitchRoll(y * MathF.PI / 180, x * MathF.PI / 180, z * MathF.PI / 180);

    /// <summary>A rotation, then a move to (x, y, z).</summary>
    public static Matrix4x4 At(Matrix4x4 basis, float x, float y, float z) => basis * Matrix4x4.CreateTranslation(x, y, z);

    public static Matrix4x4 At(float x, float y, float z) => Matrix4x4.CreateTranslation(x, y, z);

    /// <summary>The rotation that brings +Y onto <paramref name="direction"/> (Godot's Quaternion(Vector3.Up, d)).</summary>
    public static Matrix4x4 UpTo(Vector3 direction)
    {
        var d = Vector3.Normalize(direction);
        var dot = Vector3.Dot(Vector3.UnitY, d);
        if (dot > 0.9999f)
            return Matrix4x4.Identity;
        if (dot < -0.9999f)
            return Matrix4x4.CreateRotationX(MathF.PI);
        return Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitY, d)), MathF.Acos(dot));
    }
}
