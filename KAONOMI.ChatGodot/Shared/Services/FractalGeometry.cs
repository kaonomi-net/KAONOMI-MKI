using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace KAONOMI.Chat.Services;

/// <summary>The box around a drawing: the drawings are fitted in [-1, 1] by <see cref="Fit"/>.</summary>
public readonly record struct FractalBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>The scale and the shift that put the box in [-1, 1] × [-1, 1], centred, its proportions kept.</summary>
    public (double Scale, double ShiftX, double ShiftY) Fit()
    {
        var width = Math.Max(MaxX - MinX, 1e-12);
        var height = Math.Max(MaxY - MinY, 1e-12);
        var scale = 2 / Math.Max(width, height);
        return (scale, -(MinX + MaxX) / 2, -(MinY + MaxY) / 2);
    }
}

/// <summary>
/// The L-systems of /fractal: an axiom rewritten <c>depth</c> times by its rules, then drawn by a turtle. The letters
/// of the draw symbols draw a line forward, f and g move without drawing, + turns left, - turns right, | turns back,
/// [ keeps the place and the direction of the turtle and ] takes them back; any other letter only rewrites.
/// </summary>
public static class FractalLSystem
{
    /// <summary>The longest string rewritten, and the most lines drawn.</summary>
    public const int MaxLength = 6_000_000, MaxSegments = 1_500_000;

    /// <summary>
    /// The lines (x1, y1, x2, y2 each, fitted in [-1, 1], y upwards), how many there are, whether the string was cut
    /// (too long) and the error of the rules (null: none).
    /// </summary>
    public sealed record Result(float[] Segments, int Count, bool Truncated, string? Error, int Length);

    /// <summary>Reads the rules: one per line (or separated by ; or ,), <c>F=…</c>, <c>F→…</c> or <c>F->…</c>.</summary>
    public static Dictionary<char, string> ParseRules(string? text, out string? error)
    {
        error = null;
        var rules = new Dictionary<char, string>();
        foreach (var raw in (text ?? string.Empty).Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var arrow = line.IndexOf("->", StringComparison.Ordinal);
            int cut, skip;
            if (arrow >= 0)
                (cut, skip) = (arrow, 2);
            else if (line.IndexOf('→') is >= 0 and var a)
                (cut, skip) = (a, 1);
            else if (line.IndexOf('=') is >= 0 and var eq)
                (cut, skip) = (eq, 1);
            else
            {
                error = $"\"{line}\": a rule is a letter, = and what replaces it (F=F+F--F+F)";
                return rules;
            }
            var left = line[..cut].Trim();
            if (left.Length != 1)
            {
                error = $"\"{line}\": the left of a rule is one letter";
                return rules;
            }
            rules[left[0]] = line[(cut + skip)..].Replace(" ", string.Empty);
        }
        return rules;
    }

    /// <summary>Rewrites <paramref name="axiom"/> <paramref name="depth"/> times (stops before <see cref="MaxLength"/>).</summary>
    public static string Expand(string axiom, IReadOnlyDictionary<char, string> rules, int depth, out bool truncated, out int reached)
    {
        truncated = false;
        reached = 0;
        var current = axiom.Replace(" ", string.Empty);
        for (var level = 0; level < depth; level++)
        {
            long length = 0;
            foreach (var ch in current)
                length += rules.TryGetValue(ch, out var r) ? r.Length : 1;
            if (length > MaxLength)
            {
                truncated = true;
                break;
            }
            var next = new StringBuilder((int)length);
            foreach (var ch in current)
            {
                if (rules.TryGetValue(ch, out var r))
                    next.Append(r);
                else
                    next.Append(ch);
            }
            current = next.ToString();
            reached = level + 1;
        }
        return current;
    }

    /// <summary>The drawing of an L-system.</summary>
    public static Result Draw(string axiom, string rulesText, double angle, int depth, double heading, string drawSymbols)
    {
        var rules = ParseRules(rulesText, out var error);
        if (error is not null)
            return new Result([], 0, false, error, 0);
        if (string.IsNullOrWhiteSpace(axiom))
            return new Result([], 0, false, "The axiom is empty: the string the rules start from (F, F--F--F…)", 0);
        var text = Expand(axiom, rules, Math.Clamp(depth, 0, 30), out var truncated, out var reached);
        var draw = new HashSet<char>(string.IsNullOrEmpty(drawSymbols) ? "F" : drawSymbols);
        var turn = angle * Math.PI / 180;
        var x = 0.0;
        var y = 0.0;
        var direction = heading * Math.PI / 180;
        var stack = new Stack<(double X, double Y, double Direction)>();
        var points = new List<float>(Math.Min(text.Length, MaxSegments) * 4);
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        var count = 0;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '+':
                    direction += turn;
                    break;
                case '-':
                    direction -= turn;
                    break;
                case '|':
                    direction += Math.PI;
                    break;
                case '[':
                    stack.Push((x, y, direction));
                    break;
                case ']':
                    if (stack.Count > 0)
                        (x, y, direction) = stack.Pop();
                    break;
                case 'f' or 'g':
                    x += Math.Cos(direction);
                    y += Math.Sin(direction);
                    break;
                default:
                    if (!draw.Contains(ch))
                        break;
                    if (count >= MaxSegments)
                    {
                        truncated = true;
                        break;
                    }
                    var nx = x + Math.Cos(direction);
                    var ny = y + Math.Sin(direction);
                    points.Add((float)x);
                    points.Add((float)y);
                    points.Add((float)nx);
                    points.Add((float)ny);
                    minX = Math.Min(minX, Math.Min(x, nx));
                    minY = Math.Min(minY, Math.Min(y, ny));
                    maxX = Math.Max(maxX, Math.Max(x, nx));
                    maxY = Math.Max(maxY, Math.Max(y, ny));
                    x = nx;
                    y = ny;
                    count++;
                    break;
            }
        }
        var (scale, shiftX, shiftY) = new FractalBounds(minX, minY, maxX, maxY).Fit();
        var segments = points.ToArray();
        for (var k = 0; k < segments.Length; k += 2)
        {
            segments[k] = (float)((segments[k] + shiftX) * scale);
            segments[k + 1] = (float)((segments[k + 1] + shiftY) * scale);
        }
        var note = count == 0 ? "Nothing is drawn: no letter of the string is among the draw symbols" : null;
        return new Result(segments, count, truncated || reached < depth, note, text.Length);
    }
}

/// <summary>
/// The iterated function systems of /fractal: affine maps x' = a x + b y + e, y' = c x + d y + f, one chosen at
/// random at each step with its weight p (by default its area |ad - bc|): the chaos game draws the attractor.
/// </summary>
public static class FractalIfs
{
    public const int MaxPoints = 5_000_000;

    public sealed record Map(double A, double B, double C, double D, double E, double F, double P);

    /// <summary>The points (x, y each, fitted in [-1, 1], y upwards), the map that made each one, and the error (null: none).</summary>
    public sealed record Result(float[] Points, byte[] Maps, int MapCount, string? Error);

    /// <summary>Reads the maps: one per line, <c>a b c d e f</c> and an optional weight p; # starts a comment.</summary>
    public static List<Map> Parse(string? text, out string? error)
    {
        error = null;
        var maps = new List<Map>();
        var lineNumber = 0;
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            lineNumber++;
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
                continue;
            var parts = line.Split([' ', '\t', ';', ','], StringSplitOptions.RemoveEmptyEntries);
            var numbers = new List<double>();
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    error = $"Line {lineNumber}: '{part}' is not a number (write 0.5, not 0,5)";
                    return maps;
                }
                numbers.Add(value);
            }
            if (numbers.Count is not (6 or 7))
            {
                error = $"Line {lineNumber}: a map is 6 numbers (a b c d e f) and an optional weight p, not {numbers.Count}";
                return maps;
            }
            var weight = numbers.Count == 7 ? numbers[6] : Math.Max(Math.Abs(numbers[0] * numbers[3] - numbers[1] * numbers[2]), 0.01);
            maps.Add(new Map(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5], Math.Max(weight, 0)));
        }
        if (maps.Count == 0)
            error = "No map: write one per line, a b c d e f [p]";
        else if (maps.Count > 255)
            error = "At most 255 maps";
        else if (maps.Sum(m => m.P) <= 0)
            error = "The weights p are all 0";
        return maps;
    }

    /// <summary>Plays the chaos game <paramref name="count"/> times.</summary>
    public static Result Generate(string? text, int count, int seed = 7)
    {
        var maps = Parse(text, out var error);
        if (error is not null)
            return new Result([], [], 0, error);
        count = Math.Clamp(count, 1000, MaxPoints);
        var total = maps.Sum(m => m.P);
        var cumulative = new double[maps.Count];
        var running = 0.0;
        for (var k = 0; k < maps.Count; k++)
        {
            running += maps[k].P / total;
            cumulative[k] = running;
        }
        var random = new Random(seed);
        var points = new float[count * 2];
        var chosen = new byte[count];
        double x = 0, y = 0;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        const int warmUp = 30;
        for (var step = -warmUp; step < count; step++)
        {
            var r = random.NextDouble();
            var index = Array.FindIndex(cumulative, v => r <= v);
            if (index < 0)
                index = maps.Count - 1;
            var m = maps[index];
            (x, y) = (m.A * x + m.B * y + m.E, m.C * x + m.D * y + m.F);
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y) || Math.Abs(x) > 1e9 || Math.Abs(y) > 1e9)
                return new Result([], [], 0, "The points run away to infinity: each map must shrink (|a d - b c| < 1, its numbers small)");
            if (step < 0)
                continue;
            points[step * 2] = (float)x;
            points[step * 2 + 1] = (float)y;
            chosen[step] = (byte)index;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        var (scale, shiftX, shiftY) = new FractalBounds(minX, minY, maxX, maxY).Fit();
        for (var k = 0; k < points.Length; k += 2)
        {
            points[k] = (float)((points[k] + shiftX) * scale);
            points[k + 1] = (float)((points[k + 1] + shiftY) * scale);
        }
        return new Result(points, chosen, maps.Count, null);
    }
}
