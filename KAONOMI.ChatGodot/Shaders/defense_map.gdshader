// Background of the Defense theme: the tactical map of a command post. A dark olive ground, the contour lines of a
// topographic map (every fifth one thicker), a grid of map squares with its tick marks, a few unit symbols in the
// manner of NATO (friendly rectangles in blue-green, hostile diamonds in red, unknown in amber) that blink slowly,
// a scan line sweeping down the map every twelve seconds, a faint film grain and a vignette. "energy" (0 to 1)
// brightens the symbols and the scan while Kaonomi speaks.
shader_type canvas_item;

uniform vec4 ground : source_color = vec4(0.075, 0.085, 0.06, 1.0);
uniform vec4 ground_deep : source_color = vec4(0.045, 0.05, 0.035, 1.0);
uniform vec4 contour : source_color = vec4(0.55, 0.58, 0.38, 1.0);
uniform vec4 grid_color : source_color = vec4(0.62, 0.66, 0.5, 1.0);
uniform vec4 friendly : source_color = vec4(0.4, 0.7, 0.75, 1.0);
uniform vec4 hostile : source_color = vec4(0.85, 0.32, 0.25, 1.0);
uniform vec4 unknown : source_color = vec4(0.95, 0.68, 0.22, 1.0);
uniform float energy = 0.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p) {
    float sum = 0.0;
    float amplitude = 0.5;
    for (int i = 0; i < 5; i++) {
        sum += amplitude * noise(p);
        p = p * 2.02 + vec2(3.1, 7.7);
        amplitude *= 0.5;
    }
    return sum;
}

// A unit symbol at c: 0 friendly (rectangle), 1 hostile (diamond), 2 unknown (quatrefoil-like square on its corner).
float symbol(vec2 p, vec2 c, float kind, float size) {
    vec2 d = p - c;
    float line = 0.0;
    float w = size * 0.12;
    if (kind < 0.5) {
        vec2 q = abs(d) - vec2(size * 1.5, size);
        float edge = abs(max(q.x, q.y));
        line = 1.0 - smoothstep(0.0, w, edge);
        // The cross of an infantry unit inside.
        vec2 n = d / vec2(size * 1.5, size);
        float cross = (1.0 - smoothstep(0.0, 0.08, abs(n.x - n.y))) + (1.0 - smoothstep(0.0, 0.08, abs(n.x + n.y)));
        line = max(line, cross * step(abs(n.x), 1.0) * step(abs(n.y), 1.0) * 0.8);
    } else if (kind < 1.5) {
        float edge = abs(abs(d.x) + abs(d.y) - size * 1.3);
        line = 1.0 - smoothstep(0.0, w, edge);
    } else {
        float edge = abs(max(abs(d.x), abs(d.y)) - size) ;
        float circleEdge = abs(length(d) - size * 1.05);
        line = 1.0 - smoothstep(0.0, w, min(edge, circleEdge));
    }
    return line;
}

void fragment() {
    vec2 uv = SCREEN_UV;
    float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
    vec2 p = vec2(uv.x * aspect, uv.y);
    vec2 pixel = FRAGCOORD.xy;
    float t = TIME;

    // The ground: dark olive, darker at the edges.
    vec3 color = mix(ground.rgb, ground_deep.rgb, smoothstep(0.2, 1.2, distance(uv, vec2(0.45, 0.45))));

    // The relief: the height of the terrain, and its contour lines (every fifth one thicker).
    float height = fbm(p * 2.6 + vec2(4.0, 1.0));
    float levels = height * 28.0;
    float aa = fwidth(levels);
    float line = 1.0 - smoothstep(0.0, aa * 1.2, abs(fract(levels) - 0.5) * 2.0 - (1.0 - aa * 1.2));
    float index = step(0.5, 1.0 - abs(fract(levels / 5.0 + 0.1) - 0.5) * 2.0 - 0.6);
    float minor = 1.0 - smoothstep(0.0, aa * 1.5, abs(fract(levels + 0.5) - 0.5));
    float major = 1.0 - smoothstep(0.0, aa * 2.5, abs(fract(levels / 5.0 + 0.5) - 0.5) * 5.0);
    color += contour.rgb * (minor * 0.07 + major * 0.11);
    // The low ground a little darker, the heights a little lighter (a hint of hill shading).
    color *= 0.92 + 0.16 * height;

    // The grid of map squares: lines every 160 px, tick marks every 32 px along them.
    vec2 g = mod(pixel, 160.0);
    float gridLine = (1.0 - smoothstep(0.0, 1.2, min(g.x, 160.0 - g.x))) + (1.0 - smoothstep(0.0, 1.2, min(g.y, 160.0 - g.y)));
    vec2 tk = mod(pixel, 32.0);
    float ticks = (1.0 - smoothstep(0.0, 1.0, min(tk.x, 32.0 - tk.x))) * (1.0 - smoothstep(4.0, 5.0, min(g.y, 160.0 - g.y)))
                + (1.0 - smoothstep(0.0, 1.0, min(tk.y, 32.0 - tk.y))) * (1.0 - smoothstep(4.0, 5.0, min(g.x, 160.0 - g.x)));
    color += grid_color.rgb * (clamp(gridLine, 0.0, 1.0) * 0.09 + clamp(ticks, 0.0, 1.0) * 0.08);

    // The units: a few symbols spread over the map, blinking slowly, a small drift (they move).
    for (int i = 0; i < 9; i++) {
        float fi = float(i);
        vec2 c = vec2(hash(vec2(fi, 1.3)) * aspect, 0.1 + hash(vec2(fi, 4.7)) * 0.8);
        c += vec2(sin(t * 0.03 + fi), cos(t * 0.025 + fi * 1.7)) * 0.01;
        float kind = floor(hash(vec2(fi, 9.1)) * 3.0);
        float s = symbol(p, c, kind, 0.012);
        vec3 tint = kind < 0.5 ? friendly.rgb : (kind < 1.5 ? hostile.rgb : unknown.rgb);
        float blink = 0.55 + 0.45 * sin(t * (0.6 + 0.4 * hash(vec2(fi, 2.0))) + fi * 2.0);
        color += tint * s * (0.35 + 0.25 * blink) * (1.0 + energy);
    }

    // The scan: a thin bright line sweeping down every twelve seconds, a soft trail above it.
    float scanY = fract(t / 12.0);
    float dy = uv.y - scanY;
    float scan = (1.0 - smoothstep(0.0, 0.002, abs(dy))) * 0.12 + smoothstep(-0.08, 0.0, dy) * step(dy, 0.0) * 0.04;
    color += grid_color.rgb * scan * (1.0 + energy * 1.5);

    // Film grain and a vignette.
    color += (hash(pixel + floor(t * 12.0)) - 0.5) * 0.012;
    color *= mix(0.6, 1.0, smoothstep(1.3, 0.3, distance(uv, vec2(0.5))));
    COLOR = vec4(color, 1.0);
}
