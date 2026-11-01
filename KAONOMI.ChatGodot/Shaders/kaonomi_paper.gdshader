// Background of the Kaonomi theme, in the style of www.kaonomi.net: white paper turning to a very light blue, a faint
// network of hexagons drawn with thin grey lines (like the drawings of the site), a few nodes in turquoise and in the
// gold of the logo joined by thin lines, drifting slowly. "energy" (0 to 1) lights the nodes while Kaonomi speaks.
shader_type canvas_item;

uniform float energy = 0.0;
uniform vec4 paper : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform vec4 sky : source_color = vec4(0.953, 0.973, 0.988, 1.0);
uniform vec4 line_color : source_color = vec4(0.62, 0.68, 0.73, 1.0);
uniform vec4 turquoise : source_color = vec4(0.102, 0.561, 0.659, 1.0);
uniform vec4 gold : source_color = vec4(0.694, 0.553, 0.114, 1.0);

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

// Distance to the edge of the nearest hexagon (0 on an edge).
float hexEdge(vec2 p) {
    const vec2 s = vec2(1.0, 1.7320508);
    vec4 centers = floor(vec4(p, p - vec2(0.5, 1.0)) / s.xyxy) + 0.5;
    vec4 offsets = vec4(p - centers.xy * s, p - (centers.zw + 0.5) * s);
    vec2 h = dot(offsets.xy, offsets.xy) < dot(offsets.zw, offsets.zw) ? offsets.xy : offsets.zw;
    h = abs(h);
    return 0.5 - max(dot(h, s * 0.5), h.x);
}

float segment(vec2 p, vec2 a, vec2 b) {
    vec2 pa = p - a, ba = b - a;
    float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
    return length(pa - ba * h);
}

void fragment() {
    vec2 uv = SCREEN_UV;
    float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
    vec2 p = vec2(uv.x * aspect, uv.y);
    vec3 color = mix(paper.rgb, sky.rgb, smoothstep(0.0, 1.0, uv.y * 0.7 + uv.x * 0.3));

    // Hexagons, larger and fainter towards the middle, as on the pages of the site.
    float edge = hexEdge(p * 9.0 + vec2(TIME * 0.01, 0.0));
    float hex = 1.0 - smoothstep(0.0, 0.025, edge);
    float mask = smoothstep(0.15, 0.85, distance(uv, vec2(0.45, 0.5)) * 1.4);
    color = mix(color, line_color.rgb, hex * mask * 0.22);

    // A network of nodes on a coarse grid, each joined to its right and lower neighbours.
    vec2 grid = p * 4.0;
    vec2 cell = floor(grid);
    float lines = 1e3;
    float dots = 1e3;
    vec3 dotColor = turquoise.rgb;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec2 c = cell + vec2(float(x), float(y));
            vec2 node = c + 0.2 + 0.6 * vec2(hash(c), hash(c + 7.0)) + 0.05 * sin(TIME * 0.3 + hash(c) * 6.28);
            vec2 right = c + vec2(1.0, 0.0);
            vec2 nodeRight = right + 0.2 + 0.6 * vec2(hash(right), hash(right + 7.0)) + 0.05 * sin(TIME * 0.3 + hash(right) * 6.28);
            vec2 down = c + vec2(0.0, 1.0);
            vec2 nodeDown = down + 0.2 + 0.6 * vec2(hash(down), hash(down + 7.0)) + 0.05 * sin(TIME * 0.3 + hash(down) * 6.28);
            if (hash(c + 3.0) > 0.45) lines = min(lines, segment(grid, node, nodeRight));
            if (hash(c + 5.0) > 0.55) lines = min(lines, segment(grid, node, nodeDown));
            float d = length(grid - node);
            if (d < dots) {
                dots = d;
                dotColor = hash(c + 11.0) > 0.8 ? gold.rgb : turquoise.rgb;
            }
        }
    }
    float px = 4.0 * SCREEN_PIXEL_SIZE.y;
    color = mix(color, line_color.rgb, (1.0 - smoothstep(0.0, px * 1.2, lines)) * 0.28 * mask);
    float ring = 1.0 - smoothstep(px * 0.8, px * 2.0, abs(dots - 0.045));
    float core = 1.0 - smoothstep(0.0, 0.03, dots);
    color = mix(color, dotColor, (ring * 0.45 + core * (0.25 + 0.6 * energy)) * mask);

    COLOR = vec4(color, 1.0);
}
