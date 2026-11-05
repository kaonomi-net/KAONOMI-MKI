namespace KAONOMI.ChatGodot.Tools.Fractals;

/// <summary>
/// The shader of the escape-time fractals of /fractal, written again for each formula: the formula (translated by
/// <c>FractalFormula</c>) becomes the body of <c>next_z</c>, iterated on each pixel. The view, the parameters and the
/// colours are uniforms (changing them draws again without compiling).
/// </summary>
public static class FractalShader
{
    /// <summary>The most iterations the loop allows (the uniform <c>max_iter</c> stops it sooner).</summary>
    public const int MaxIterations = 20000;

    public static string Build(string formula) => Head + Complex + $$"""

        vec2 next_z(vec2 z, vec2 c, vec2 zp, vec2 px, float n) {
            return {{formula}};
        }

        """ + Body;

    private const string Head = """
        shader_type canvas_item;
        render_mode unshaded;

        uniform vec2 center = vec2(0.0);
        uniform float scale = 1.5;
        uniform vec2 view_size = vec2(800.0, 600.0);
        uniform int max_iter = 300;
        uniform float bailout = 64.0;
        uniform float degree = 2.0;
        uniform bool julia = false;
        uniform vec2 julia_c = vec2(0.0);
        uniform vec2 z0 = vec2(0.0);
        uniform vec2 u_a = vec2(0.0);
        uniform vec2 u_b = vec2(0.0);
        uniform int coloring = 0;
        uniform int samples = 1;
        uniform vec3 pal_a = vec3(0.5);
        uniform vec3 pal_b = vec3(0.5);
        uniform vec3 pal_c = vec3(1.0);
        uniform vec3 pal_d = vec3(0.0, 0.33, 0.67);
        uniform float density = 1.0;
        uniform float offset = 0.0;
        uniform float cycle = 0.0;
        uniform vec3 inside = vec3(0.0);

        """;

    private const string Complex = """
        vec2 cmul(vec2 a, vec2 b) { return vec2(a.x * b.x - a.y * b.y, a.x * b.y + a.y * b.x); }
        vec2 csqr(vec2 a) { return vec2(a.x * a.x - a.y * a.y, 2.0 * a.x * a.y); }
        vec2 cdiv(vec2 a, vec2 b) { float d = max(dot(b, b), 1e-30); return vec2(a.x * b.x + a.y * b.y, a.y * b.x - a.x * b.y) / d; }
        vec2 cinv(vec2 a) { return cdiv(vec2(1.0, 0.0), a); }
        vec2 cpown(vec2 a, int k) {
            vec2 r = vec2(1.0, 0.0);
            for (int j = 0; j < 32; j++) { if (j >= k) { break; } r = cmul(r, a); }
            return r;
        }
        vec2 cexp(vec2 a) { return exp(a.x) * vec2(cos(a.y), sin(a.y)); }
        vec2 clog(vec2 a) { return vec2(log(max(length(a), 1e-30)), atan(a.y, a.x)); }
        vec2 cpowr(vec2 a, float k) {
            float r = length(a);
            if (r < 1e-30) { return vec2(0.0); }
            float t = atan(a.y, a.x) * k;
            return pow(r, k) * vec2(cos(t), sin(t));
        }
        vec2 cpowc(vec2 a, vec2 w) { if (dot(a, a) < 1e-30) { return vec2(0.0); } return cexp(cmul(w, clog(a))); }
        vec2 csin(vec2 a) { return vec2(sin(a.x) * cosh(a.y), cos(a.x) * sinh(a.y)); }
        vec2 ccos(vec2 a) { return vec2(cos(a.x) * cosh(a.y), -sin(a.x) * sinh(a.y)); }
        vec2 ctan(vec2 a) { return cdiv(csin(a), ccos(a)); }
        vec2 csinh(vec2 a) { return vec2(sinh(a.x) * cos(a.y), cosh(a.x) * sin(a.y)); }
        vec2 ccosh(vec2 a) { return vec2(cosh(a.x) * cos(a.y), sinh(a.x) * sin(a.y)); }
        vec2 ctanh(vec2 a) { return cdiv(csinh(a), ccosh(a)); }
        vec2 csqrt(vec2 a) {
            float r = length(a);
            float s = a.y < 0.0 ? -1.0 : 1.0;
            return vec2(sqrt(max(0.5 * (r + a.x), 0.0)), s * sqrt(max(0.5 * (r - a.x), 0.0)));
        }
        vec2 cconj(vec2 a) { return vec2(a.x, -a.y); }
        vec2 cfold(vec2 a) { return abs(a); }
        vec2 cabs(vec2 a) { return vec2(length(a), 0.0); }
        vec2 cnorm(vec2 a) { return vec2(dot(a, a), 0.0); }
        vec2 carg(vec2 a) { return vec2(atan(a.y, a.x), 0.0); }
        vec2 cre(vec2 a) { return vec2(a.x, 0.0); }
        vec2 cim(vec2 a) { return vec2(a.y, 0.0); }
        vec2 cflip(vec2 a) { return a.yx; }

        """;

    private const string Body = """
        vec3 palette(float t) {
            return clamp(pal_a + pal_b * cos(6.2831853 * (pal_c * t + pal_d)), 0.0, 1.0);
        }

        vec3 shade(vec2 pos, float shift) {
            vec2 z = julia ? pos : z0;
            vec2 c = julia ? julia_c : pos;
            vec2 zp = vec2(0.0);
            float b2 = bailout * bailout;
            int count = 0;
            bool done = false;
            for (int k = 0; k < 20000; k++) {
                if (k >= max_iter) { break; }
                vec2 next = next_z(z, c, zp, pos, float(k));
                zp = z;
                z = next;
                count = k + 1;
                if (isnan(z.x) || isnan(z.y) || isinf(z.x) || isinf(z.y)) { break; }
                if (coloring == 0) {
                    if (dot(z, z) > b2) { done = true; break; }
                } else {
                    vec2 d = z - zp;
                    if (dot(d, d) < 1e-12) { done = true; break; }
                    if (dot(z, z) > 1e16) { break; }
                }
            }
            if (!done) { return inside; }
            if (coloring == 0) {
                float mu = float(count) + 1.0 - log(max(log(length(z)), 1e-6)) / log(max(degree, 1.0001));
                // The square root spreads the colours both near the set (many iterations) and far from it (few).
                return palette(sqrt(max(mu, 0.0)) * 0.15 * density + shift);
            }
            float hue = atan(z.y, z.x) / 6.2831853;
            float light = clamp(1.0 - log(float(count) + 1.0) / log(float(max_iter) + 1.0), 0.0, 1.0);
            // The hue tells the root; bands of the iterations show how fast the point got there.
            return palette(hue + float(count) * 0.04 * density + shift) * (0.25 + 0.75 * light);
        }

        void fragment() {
            float unit = 2.0 * scale / view_size.y;
            vec2 aspect = vec2(view_size.x / view_size.y, 1.0);
            vec2 base = center + vec2(UV.x - 0.5, 0.5 - UV.y) * aspect * 2.0 * scale;
            int s = clamp(samples, 1, 3);
            float shift = offset + TIME * cycle;
            vec3 sum = vec3(0.0);
            for (int sx = 0; sx < 3; sx++) {
                if (sx >= s) { break; }
                for (int sy = 0; sy < 3; sy++) {
                    if (sy >= s) { break; }
                    vec2 jitter = (vec2(float(sx), float(sy)) + 0.5) / float(s) - 0.5;
                    sum += shade(base + vec2(jitter.x, -jitter.y) * unit, shift);
                }
            }
            COLOR = vec4(sum / float(s * s), 1.0);
        }
        """;
}
