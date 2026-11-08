using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// The next z of an escape-time fractal, compiled for the CPU: (z, c, zp, p, n, a, b) → next z.
/// </summary>
internal delegate Complex NextZ(Complex z, Complex c, Complex zp, Complex p, double n, Complex a, Complex b);

/// <summary>
/// The fractals of /fractal drawn on the CPU (the Godot chat draws the escape-time ones with a shader on the GPU): the
/// expression of the shading language that <see cref="FractalFormula"/> makes of a formula is compiled again, here into
/// a .NET delegate (LINQ expressions, the complex helpers of the shader written in C#, in double precision), and
/// iterated on every pixel in parallel. The values of the pixels are kept, so that a change of the colours (palette,
/// density, offset, their cycling) colours them again without iterating. The L-systems and the IFS are drawn into the
/// same kind of picture (antialiased lines; the density of the points of the chaos game).
/// </summary>
internal static class FractalCpu
{
    /// <summary>The most iterations allowed (as the loop of the shader of the Godot chat).</summary>
    public const int MaxIterations = 20000;

    /// <summary>The colour of the background (the frame of the picture).</summary>
    public const byte BackR = 5, BackG = 8, BackB = 11;

    // ── The formula ──

    private static readonly Dictionary<string, NextZ?> Compiled = new(StringComparer.Ordinal);

    /// <summary>The delegate of the expression <paramref name="glsl"/> (null when it cannot be compiled).</summary>
    public static NextZ? Compile(string? glsl)
    {
        if (string.IsNullOrWhiteSpace(glsl))
            return null;
        lock (Compiled)
        {
            if (Compiled.TryGetValue(glsl, out var known))
                return known;
            NextZ? result;
            try
            {
                result = new GlslCompiler(glsl).Compile();
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
            {
                result = null;
            }
            if (Compiled.Count > 64)
                Compiled.Clear();
            Compiled[glsl] = result;
            return result;
        }
    }

    /// <summary>
    /// The expressions written by <see cref="FractalFormula"/>: numbers, <c>vec2(x, y)</c>, the variables z, c, zp, px,
    /// u_a, u_b and n, + - * / between them, unary minus, parentheses and the complex helpers of the shader.
    /// </summary>
    private sealed class GlslCompiler(string text)
    {
        private readonly ParameterExpression _z = Expression.Parameter(typeof(Complex), "z");
        private readonly ParameterExpression _c = Expression.Parameter(typeof(Complex), "c");
        private readonly ParameterExpression _zp = Expression.Parameter(typeof(Complex), "zp");
        private readonly ParameterExpression _p = Expression.Parameter(typeof(Complex), "px");
        private readonly ParameterExpression _n = Expression.Parameter(typeof(double), "n");
        private readonly ParameterExpression _a = Expression.Parameter(typeof(Complex), "u_a");
        private readonly ParameterExpression _b = Expression.Parameter(typeof(Complex), "u_b");
        private int _at;

        public NextZ Compile()
        {
            var body = AsComplex(Sum());
            Skip();
            if (_at < text.Length)
                throw new FormatException($"Unexpected '{text[_at]}' at {_at}");
            return Expression.Lambda<NextZ>(body, _z, _c, _zp, _p, _n, _a, _b).Compile();
        }

        private void Skip()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
                _at++;
        }

        private bool Take(char ch)
        {
            Skip();
            if (_at < text.Length && text[_at] == ch)
            {
                _at++;
                return true;
            }
            return false;
        }

        private void Expect(char ch)
        {
            if (!Take(ch))
                throw new FormatException($"'{ch}' expected at {_at}");
        }

        private static Expression AsComplex(Expression e) =>
            e.Type == typeof(Complex) ? e : Expression.New(typeof(Complex).GetConstructor([typeof(double), typeof(double)])!, e, Expression.Constant(0.0));

        private static Expression Call(string name, params Expression[] args)
        {
            var types = new Type[args.Length];
            for (var i = 0; i < args.Length; i++)
                types[i] = args[i].Type;
            var method = typeof(CMath).GetMethod(name, types) ?? throw new FormatException($"No helper {name}");
            return Expression.Call(method, args);
        }

        // sum := product (('+' | '-') product)*
        private Expression Sum()
        {
            var left = Product();
            while (true)
            {
                Skip();
                if (_at >= text.Length || text[_at] is not ('+' or '-'))
                    return left;
                var op = text[_at++];
                var right = Product();
                if (left.Type == typeof(double) && right.Type == typeof(double))
                    left = op == '+' ? Expression.Add(left, right) : Expression.Subtract(left, right);
                else
                    left = Call(op == '+' ? nameof(CMath.Add) : nameof(CMath.Sub), AsComplex(left), AsComplex(right));
            }
        }

        // product := unary (('*' | '/') unary)*
        private Expression Product()
        {
            var left = Unary();
            while (true)
            {
                Skip();
                if (_at >= text.Length || text[_at] is not ('*' or '/'))
                    return left;
                var op = text[_at++];
                var right = Unary();
                left = (left.Type == typeof(double), right.Type == typeof(double), op) switch
                {
                    (true, true, '*') => Expression.Multiply(left, right),
                    (true, true, _) => Expression.Divide(left, right),
                    (true, false, '*') => Call(nameof(CMath.Scale), right, left),
                    (false, true, '*') => Call(nameof(CMath.Scale), left, right),
                    (false, true, _) => Call(nameof(CMath.DivS), left, right),
                    (_, _, '*') => Call(nameof(CMath.cmul), AsComplex(left), AsComplex(right)),
                    _ => Call(nameof(CMath.cdiv), AsComplex(left), AsComplex(right))
                };
            }
        }

        // unary := '-' unary | atom
        private Expression Unary()
        {
            if (Take('-'))
            {
                var value = Unary();
                return value.Type == typeof(double) ? Expression.Negate(value) : Call(nameof(CMath.Neg), value);
            }
            if (Take('+'))
                return Unary();
            return Atom();
        }

        private Expression Atom()
        {
            Skip();
            if (_at >= text.Length)
                throw new FormatException("Value expected at the end");
            var ch = text[_at];
            if (ch == '(')
            {
                _at++;
                var inner = Sum();
                Expect(')');
                return inner;
            }
            if (char.IsDigit(ch) || ch == '.')
            {
                var start = _at;
                while (_at < text.Length && (char.IsDigit(text[_at]) || text[_at] == '.'))
                    _at++;
                if (_at < text.Length && text[_at] is 'e' or 'E')
                {
                    _at++;
                    if (_at < text.Length && text[_at] is '+' or '-')
                        _at++;
                    while (_at < text.Length && char.IsDigit(text[_at]))
                        _at++;
                }
                return Expression.Constant(double.Parse(text[start.._at], NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                var start = _at;
                while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] == '_'))
                    _at++;
                var name = text[start.._at];
                switch (name)
                {
                    case "z": return _z;
                    case "c": return _c;
                    case "zp": return _zp;
                    case "px": return _p;
                    case "u_a": return _a;
                    case "u_b": return _b;
                    case "n": return _n;
                }
                Expect('(');
                var args = new List<Expression> { Sum() };
                while (Take(','))
                    args.Add(Sum());
                Expect(')');
                if (name == "vec2")
                {
                    if (args.Count != 2 || args[0].Type != typeof(double) || args[1].Type != typeof(double))
                        throw new FormatException("vec2 takes two reals");
                    return Expression.New(typeof(Complex).GetConstructor([typeof(double), typeof(double)])!, args[0], args[1]);
                }
                if (name == "cpown")
                    return Call(nameof(CMath.cpown), AsComplex(args[0]), Expression.Convert(args[1], typeof(int)));
                if (name == "cpowr")
                    return Call(nameof(CMath.cpowr), AsComplex(args[0]), args[1].Type == typeof(double) ? args[1] : Expression.Property(args[1], nameof(Complex.Real)));
                for (var i = 0; i < args.Count; i++)
                    args[i] = AsComplex(args[i]);
                return Call(name, [.. args]);
            }
            throw new FormatException($"Unexpected '{ch}' at {_at}");
        }
    }

    /// <summary>The complex helpers of the shader of the Godot chat, the same definitions in double precision.</summary>
    public static class CMath
    {
        public static Complex Add(Complex a, Complex b) => new(a.Real + b.Real, a.Imaginary + b.Imaginary);
        public static Complex Sub(Complex a, Complex b) => new(a.Real - b.Real, a.Imaginary - b.Imaginary);
        public static Complex Neg(Complex a) => new(-a.Real, -a.Imaginary);
        public static Complex Scale(Complex a, double k) => new(a.Real * k, a.Imaginary * k);
        public static Complex DivS(Complex a, double k) => new(a.Real / k, a.Imaginary / k);
        public static Complex cmul(Complex a, Complex b) => new(a.Real * b.Real - a.Imaginary * b.Imaginary, a.Real * b.Imaginary + a.Imaginary * b.Real);
        public static Complex csqr(Complex a) => new(a.Real * a.Real - a.Imaginary * a.Imaginary, 2 * a.Real * a.Imaginary);

        public static Complex cdiv(Complex a, Complex b)
        {
            var d = Math.Max(b.Real * b.Real + b.Imaginary * b.Imaginary, 1e-30);
            return new((a.Real * b.Real + a.Imaginary * b.Imaginary) / d, (a.Imaginary * b.Real - a.Real * b.Imaginary) / d);
        }

        public static Complex cinv(Complex a) => cdiv(new Complex(1, 0), a);

        public static Complex cpown(Complex a, int k)
        {
            var r = new Complex(1, 0);
            for (var j = 0; j < 32 && j < k; j++)
                r = cmul(r, a);
            return r;
        }

        public static Complex cexp(Complex a)
        {
            var e = Math.Exp(a.Real);
            return new(e * Math.Cos(a.Imaginary), e * Math.Sin(a.Imaginary));
        }

        public static Complex clog(Complex a) => new(Math.Log(Math.Max(Length(a), 1e-30)), Math.Atan2(a.Imaginary, a.Real));

        public static Complex cpowr(Complex a, double k)
        {
            var r = Length(a);
            if (r < 1e-30)
                return Complex.Zero;
            var t = Math.Atan2(a.Imaginary, a.Real) * k;
            var m = Math.Pow(r, k);
            return new(m * Math.Cos(t), m * Math.Sin(t));
        }

        public static Complex cpowc(Complex a, Complex w) => a.Real * a.Real + a.Imaginary * a.Imaginary < 1e-30 ? Complex.Zero : cexp(cmul(w, clog(a)));
        public static Complex csin(Complex a) => new(Math.Sin(a.Real) * Math.Cosh(a.Imaginary), Math.Cos(a.Real) * Math.Sinh(a.Imaginary));
        public static Complex ccos(Complex a) => new(Math.Cos(a.Real) * Math.Cosh(a.Imaginary), -Math.Sin(a.Real) * Math.Sinh(a.Imaginary));
        public static Complex ctan(Complex a) => cdiv(csin(a), ccos(a));
        public static Complex csinh(Complex a) => new(Math.Sinh(a.Real) * Math.Cos(a.Imaginary), Math.Cosh(a.Real) * Math.Sin(a.Imaginary));
        public static Complex ccosh(Complex a) => new(Math.Cosh(a.Real) * Math.Cos(a.Imaginary), Math.Sinh(a.Real) * Math.Sin(a.Imaginary));
        public static Complex ctanh(Complex a) => cdiv(csinh(a), ccosh(a));

        public static Complex csqrt(Complex a)
        {
            var r = Length(a);
            var s = a.Imaginary < 0 ? -1.0 : 1.0;
            return new(Math.Sqrt(Math.Max(0.5 * (r + a.Real), 0)), s * Math.Sqrt(Math.Max(0.5 * (r - a.Real), 0)));
        }

        public static Complex cconj(Complex a) => new(a.Real, -a.Imaginary);
        public static Complex cfold(Complex a) => new(Math.Abs(a.Real), Math.Abs(a.Imaginary));
        public static Complex cabs(Complex a) => new(Length(a), 0);
        public static Complex cnorm(Complex a) => new(a.Real * a.Real + a.Imaginary * a.Imaginary, 0);
        public static Complex carg(Complex a) => new(Math.Atan2(a.Imaginary, a.Real), 0);
        public static Complex cre(Complex a) => new(a.Real, 0);
        public static Complex cim(Complex a) => new(a.Imaginary, 0);
        public static Complex cflip(Complex a) => new(a.Imaginary, a.Real);

        private static double Length(Complex a) => Math.Sqrt(a.Real * a.Real + a.Imaginary * a.Imaginary);
    }

    // ── Escape time ──

    /// <summary>The settings of an escape-time picture (a copy of the view model, read on the interface thread).</summary>
    public sealed record EscapeSettings(
        NextZ Next, double CenterX, double CenterY, double Scale, int Iterations, double Bailout, double Degree, bool Julia,
        Complex C, Complex Z0, Complex A, Complex B, FractalColoring Coloring, int Samples);

    /// <summary>
    /// The values of the samples of an escape-time picture: for Escape, the place in the palette (NaN inside the set);
    /// for Convergence, the hue (NaN when it did not settle) and the iterations.
    /// </summary>
    public sealed class EscapeValues(int width, int height, int samples, FractalColoring coloring, int iterations)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int Samples { get; } = samples;
        public FractalColoring Coloring { get; } = coloring;
        public int Iterations { get; } = iterations;
        public float[] Value { get; } = new float[width * height * samples * samples];
        public float[] Count { get; } = coloring == FractalColoring.Escape ? [] : new float[width * height * samples * samples];
    }

    /// <summary>Iterates every sample of every pixel (in parallel, the rows shared out); null when cancelled.</summary>
    public static EscapeValues? Iterate(EscapeSettings s, int width, int height, CancellationToken token)
    {
        var samples = Math.Clamp(s.Samples, 1, 3);
        var iterations = Math.Clamp(s.Iterations, 1, MaxIterations);
        var values = new EscapeValues(width, height, samples, s.Coloring, iterations);
        var unit = 2 * s.Scale / height;
        var aspect = (double)width / height;
        var b2 = Math.Max(s.Bailout, 1.0001);
        b2 *= b2;
        var logDegree = Math.Log(Math.Max(s.Degree, 1.0001));
        var escape = s.Coloring == FractalColoring.Escape;
        var next = s.Next;
        try
        {
            Parallel.For(0, height, new ParallelOptions { CancellationToken = token }, y =>
            {
                if (token.IsCancellationRequested)
                    return;
                for (var x = 0; x < width; x++)
                {
                    var baseX = s.CenterX + ((x + 0.5) / width - 0.5) * aspect * 2 * s.Scale;
                    var baseY = s.CenterY + (0.5 - (y + 0.5) / height) * 2 * s.Scale;
                    for (var sx = 0; sx < samples; sx++)
                        for (var sy = 0; sy < samples; sy++)
                        {
                            var jx = (sx + 0.5) / samples - 0.5;
                            var jy = (sy + 0.5) / samples - 0.5;
                            var pos = new Complex(baseX + jx * unit, baseY - jy * unit);
                            var z = s.Julia ? pos : s.Z0;
                            var c = s.Julia ? s.C : pos;
                            var zp = Complex.Zero;
                            var count = 0;
                            var done = false;
                            for (var k = 0; k < iterations; k++)
                            {
                                var nz = next(z, c, zp, pos, k, s.A, s.B);
                                zp = z;
                                z = nz;
                                count = k + 1;
                                if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary))
                                    break;
                                var m = z.Real * z.Real + z.Imaginary * z.Imaginary;
                                if (escape)
                                {
                                    if (m > b2)
                                    {
                                        done = true;
                                        break;
                                    }
                                }
                                else
                                {
                                    var dx = z.Real - zp.Real;
                                    var dy = z.Imaginary - zp.Imaginary;
                                    if (dx * dx + dy * dy < 1e-12)
                                    {
                                        done = true;
                                        break;
                                    }
                                    if (m > 1e16)
                                        break;
                                }
                            }
                            var at = ((y * width + x) * samples + sx) * samples + sy;
                            if (!done)
                            {
                                values.Value[at] = float.NaN;
                                continue;
                            }
                            if (escape)
                            {
                                var length = Math.Sqrt(z.Real * z.Real + z.Imaginary * z.Imaginary);
                                var mu = count + 1 - Math.Log(Math.Max(Math.Log(length), 1e-6)) / logDegree;
                                // The square root spreads the colours both near the set (many iterations) and far from it (few).
                                values.Value[at] = (float)(Math.Sqrt(Math.Max(mu, 0)) * 0.15);
                            }
                            else
                            {
                                values.Value[at] = (float)(Math.Atan2(z.Imaginary, z.Real) / (2 * Math.PI));
                                values.Count[at] = count;
                            }
                        }
                }
            });
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        return token.IsCancellationRequested ? null : values;
    }

    /// <summary>The colours of <paramref name="values"/> (BGRA, opaque) with the palette, its density and its shift.</summary>
    public static byte[] Colorize(EscapeValues values, FractalPalette palette, double density, double shift)
    {
        var bytes = new byte[values.Width * values.Height * 4];
        var samples = values.Samples;
        var n = samples * samples;
        var logMax = Math.Log(values.Iterations + 1.0);
        var escape = values.Coloring == FractalColoring.Escape;
        Parallel.For(0, values.Height, y =>
        {
            for (var x = 0; x < values.Width; x++)
            {
                double r = 0, g = 0, b = 0;
                var first = (y * values.Width + x) * n;
                for (var k = 0; k < n; k++)
                {
                    var v = values.Value[first + k];
                    if (float.IsNaN(v))
                        continue;
                    if (escape)
                    {
                        var (pr, pg, pb) = palette.At(v * density + shift);
                        r += pr;
                        g += pg;
                        b += pb;
                    }
                    else
                    {
                        var count = values.Count[first + k];
                        var light = Math.Clamp(1 - Math.Log(count + 1.0) / logMax, 0, 1);
                        // The hue tells the root; bands of the iterations show how fast the point got there.
                        var (pr, pg, pb) = palette.At(v + count * 0.04 * density + shift);
                        var f = 0.25 + 0.75 * light;
                        r += pr * f;
                        g += pg * f;
                        b += pb * f;
                    }
                }
                var o = (y * values.Width + x) * 4;
                bytes[o] = (byte)Math.Clamp(b / n * 255, 0, 255);
                bytes[o + 1] = (byte)Math.Clamp(g / n * 255, 0, 255);
                bytes[o + 2] = (byte)Math.Clamp(r / n * 255, 0, 255);
                bytes[o + 3] = 255;
            }
        });
        return bytes;
    }

    // ── IFS ──

    /// <summary>The density of the points of the chaos game seen in the view, coloured by the map that made each one (BGRA).</summary>
    public static byte[] RasterIfs(FractalIfs.Result cloud, int width, int height, double cx, double cy, double scale, FractalPalette palette, double density, double offset)
    {
        var counts = new int[width * height];
        var red = new float[width * height];
        var green = new float[width * height];
        var blue = new float[width * height];
        var colours = new (float R, float G, float B)[Math.Max(cloud.MapCount, 1)];
        for (var k = 0; k < colours.Length; k++)
        {
            var (r, g, b) = palette.At(k / (double)colours.Length * density + offset);
            colours[k] = ((float)r, (float)g, (float)b);
        }
        var half = height / 2.0;
        var max = 0;
        var points = cloud.Points;
        for (var k = 0; k < cloud.Maps.Length; k++)
        {
            var x = (int)(width / 2.0 + (points[k * 2] - cx) / scale * half);
            var y = (int)(half - (points[k * 2 + 1] - cy) / scale * half);
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                continue;
            var at = y * width + x;
            var c = colours[cloud.Maps[k]];
            red[at] += c.R;
            green[at] += c.G;
            blue[at] += c.B;
            if (++counts[at] > max)
                max = counts[at];
        }
        var bytes = new byte[width * height * 4];
        var logMax = Math.Log(1 + Math.Max(max, 1));
        for (var at = 0; at < counts.Length; at++)
        {
            var n = counts[at];
            var o = at * 4;
            if (n == 0)
            {
                bytes[o] = BackB;
                bytes[o + 1] = BackG;
                bytes[o + 2] = BackR;
                bytes[o + 3] = 255;
                continue;
            }
            // Log density, lifted so that a single point shows: the dense parts bright, the thin ones dim.
            var light = Math.Pow(Math.Log(1 + n) / logMax, 0.45) * 0.85 + 0.15;
            bytes[o] = (byte)Math.Clamp(blue[at] / n * light * 255, 0, 255);
            bytes[o + 1] = (byte)Math.Clamp(green[at] / n * light * 255, 0, 255);
            bytes[o + 2] = (byte)Math.Clamp(red[at] / n * light * 255, 0, 255);
            bytes[o + 3] = 255;
        }
        return bytes;
    }

    // ── L-system ──

    /// <summary>
    /// The lines of an L-system (in the units of the drawing, fitted in [-1, 1]) drawn through the view, antialiased,
    /// one colour per line along the path of the turtle (lifted a little so that no line vanishes in the dark).
    /// </summary>
    public static byte[] RasterLines(FractalLSystem.Result lines, int width, int height, double cx, double cy, double scale, FractalPalette palette, double density, double offset)
    {
        var r = new float[width * height];
        var g = new float[width * height];
        var b = new float[width * height];
        Array.Fill(r, BackR / 255f);
        Array.Fill(g, BackG / 255f);
        Array.Fill(b, BackB / 255f);
        var half = Math.Max(height / 2.0, 1);
        var zoom = half / scale;
        var ox = width / 2.0 - cx * zoom;
        var oy = height / 2.0 + cy * zoom;
        var steps = Math.Max(lines.Count - 1, 1);
        var s = lines.Segments;
        for (var k = 0; k < lines.Count; k++)
        {
            var (pr, pg, pb) = palette.At(k / (double)steps * density + offset);
            var cr = (float)(0.3 + 0.7 * pr);
            var cg = (float)(0.3 + 0.7 * pg);
            var cb = (float)(0.3 + 0.7 * pb);
            var x0 = ox + s[k * 4] * zoom;
            var y0 = oy - s[k * 4 + 1] * zoom;
            var x1 = ox + s[k * 4 + 2] * zoom;
            var y1 = oy - s[k * 4 + 3] * zoom;
            WuLine(r, g, b, width, height, x0, y0, x1, y1, cr, cg, cb);
        }
        var bytes = new byte[width * height * 4];
        for (var at = 0; at < r.Length; at++)
        {
            var o = at * 4;
            bytes[o] = (byte)Math.Clamp(b[at] * 255, 0, 255);
            bytes[o + 1] = (byte)Math.Clamp(g[at] * 255, 0, 255);
            bytes[o + 2] = (byte)Math.Clamp(r[at] * 255, 0, 255);
            bytes[o + 3] = 255;
        }
        return bytes;
    }

    /// <summary>An antialiased line (Xiaolin Wu), blended over what is there.</summary>
    private static void WuLine(float[] r, float[] g, float[] b, int width, int height, double x0, double y0, double x1, double y1, float cr, float cg, float cb)
    {
        // Lines wholly outside are skipped; very long ones are clipped to a margin around the picture.
        if (Math.Max(x0, x1) < -2 || Math.Min(x0, x1) > width + 2 || Math.Max(y0, y1) < -2 || Math.Min(y0, y1) > height + 2)
            return;
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1))
            return;
        void Plot(int x, int y, double c)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height || c <= 0)
                return;
            var at = y * width + x;
            var a = (float)Math.Min(c, 1);
            r[at] += (cr - r[at]) * a;
            g[at] += (cg - g[at]) * a;
            b[at] += (cb - b[at]) * a;
        }
        var steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
        if (steep)
        {
            (x0, y0) = (y0, x0);
            (x1, y1) = (y1, x1);
        }
        if (x0 > x1)
        {
            (x0, x1) = (x1, x0);
            (y0, y1) = (y1, y0);
        }
        var limit = (steep ? height : width) + 2;
        if (x0 < -2)
        {
            y0 += (y1 - y0) * (-2 - x0) / Math.Max(x1 - x0, 1e-9);
            x0 = -2;
        }
        if (x1 > limit)
        {
            y1 -= (y1 - y0) * (x1 - limit) / Math.Max(x1 - x0, 1e-9);
            x1 = limit;
        }
        var dx = x1 - x0;
        var gradient = dx < 1e-9 ? 1 : (y1 - y0) / dx;
        var intery = y0 + gradient * (Math.Round(x0) - x0);
        for (var x = (int)Math.Round(x0); x <= (int)Math.Round(x1); x++)
        {
            var yi = (int)Math.Floor(intery);
            var f = intery - yi;
            if (steep)
            {
                Plot(yi, x, 1 - f);
                Plot(yi + 1, x, f);
            }
            else
            {
                Plot(x, yi, 1 - f);
                Plot(x, yi + 1, f);
            }
            intery += gradient;
        }
    }
}
