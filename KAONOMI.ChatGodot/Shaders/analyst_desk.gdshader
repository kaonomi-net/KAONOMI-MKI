// Background of the Analyst theme: the wall of an operations room, made for hours of work. A dark slate, slightly
// blue, with a very faint dot grid; a graticule of a world display in the lower right corner, drawn in thin lines at
// low contrast, with a few dim markers; a radar sweep that turns once every twenty seconds, barely visible; a slow,
// large drift of light so that the wall is not dead; no flicker, no bright spot. "energy" (0 to 1) lifts the sweep
// and the markers a little while Kaonomi speaks.
shader_type canvas_item;

uniform vec4 slate : source_color = vec4(0.11, 0.13, 0.16, 1.0);
uniform vec4 slate_deep : source_color = vec4(0.08, 0.095, 0.12, 1.0);
uniform vec4 ink : source_color = vec4(0.5, 0.62, 0.74, 1.0);
uniform vec4 amber : source_color = vec4(0.85, 0.64, 0.28, 1.0);
uniform float energy = 0.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

void fragment() {
    vec2 uv = SCREEN_UV;
    float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
    vec2 p = vec2(uv.x * aspect, uv.y);
    vec2 pixel = FRAGCOORD.xy;
    float t = TIME;

    // The slate: a touch lighter at the top left, a slow drift of light across it (a minute per cycle).
    vec3 color = mix(slate.rgb, slate_deep.rgb, smoothstep(0.0, 1.4, distance(uv, vec2(0.2, 0.1))));
    color += ink.rgb * 0.025 * noise(vec2(p.x * 1.5 + t * 0.01, p.y * 1.5 - t * 0.006));

    // The dot grid: a dot every 24 pixels, very faint, a little stronger every 120 pixels.
    vec2 g = mod(pixel, 24.0);
    float dot = smoothstep(1.4, 0.6, length(g - vec2(12.0)));
    float major = step(mod(pixel.x, 120.0), 24.0) * step(mod(pixel.y, 120.0), 24.0);
    color += ink.rgb * dot * (0.045 + 0.04 * major);

    // The world display in the lower right: a disc with its graticule (meridians and parallels), thin and dim.
    vec2 center = vec2(aspect * 0.86, 0.78);
    float radius = 0.26;
    vec2 d = (p - center) / radius;
    float r = length(d);
    if (r < 1.0) {
        float z = sqrt(1.0 - r * r);
        // Longitude and latitude of the point of the globe, the globe turning very slowly.
        float lon = atan(d.x, z) + t * 0.02;
        float lat = asin(clamp(d.y, -1.0, 1.0));
        float meridians = smoothstep(0.06, 0.0, abs(fract(lon / 0.5236 + 0.5) - 0.5)) ;   // every 30°
        float parallels = smoothstep(0.06, 0.0, abs(fract(lat / 0.5236 + 0.5) - 0.5));
        float lines = max(meridians, parallels) * smoothstep(0.0, 0.25, z);
        color += ink.rgb * lines * 0.07;
        // A few markers: dim dots that breathe slowly, one of them amber.
        for (int i = 0; i < 5; i++) {
            float fi = float(i);
            float mlon = hash(vec2(fi, 1.0)) * 6.283;
            float mlat = (hash(vec2(fi, 2.0)) - 0.5) * 2.4;
            vec3 m = vec3(cos(mlat) * sin(mlon - t * 0.02), sin(mlat), cos(mlat) * cos(mlon - t * 0.02));
            if (m.z > 0.0) {
                float dm = distance(d, m.xy);
                float breath = 0.6 + 0.4 * sin(t * 0.8 + fi);
                vec3 tint = i == 2 ? amber.rgb : ink.rgb;
                color += tint * smoothstep(0.03, 0.0, dm) * 0.18 * breath * (1.0 + energy);
            }
        }
        color += ink.rgb * smoothstep(0.03, 0.0, abs(r - 0.985)) * 0.08;
    }
    // The radar sweep: a thin wedge turning once every twenty seconds, fading behind, barely there.
    float angle = atan(d.y, d.x);
    float sweep = mod(t * 0.3141593, 6.2831853) - 3.1415927;
    float behind = mod(angle - sweep, 6.2831853);
    float trail = smoothstep(0.9, 0.0, behind) * step(r, 1.0);
    color += ink.rgb * trail * (0.03 + energy * 0.05);

    // A faint vignette, no more.
    color *= mix(0.86, 1.0, smoothstep(1.3, 0.3, distance(uv, vec2(0.5))));
    COLOR = vec4(color, 1.0);
}
