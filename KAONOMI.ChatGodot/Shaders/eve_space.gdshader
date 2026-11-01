// Background of the chat in the manner of EVE Online: deep space with layers of stars and a coloured nebula drifting
// slowly, a gas giant rising in the lower right corner (banded clouds, lit from the upper left, a thin blue
// atmosphere), a distant sun glare in the upper left and a vignette. "energy" (0 to 1) brightens it while Kaonomi
// speaks.
shader_type canvas_item;

uniform vec4 base_color : source_color = vec4(0.008, 0.016, 0.03, 1.0);
uniform vec4 hud : source_color = vec4(0.30, 0.55, 0.75, 1.0);
uniform vec4 accent : source_color = vec4(0.75, 0.32, 0.55, 1.0);
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
        p = p * 2.03 + vec2(1.7, 9.2);
        amplitude *= 0.5;
    }
    return sum;
}

// Distance to the edge of the nearest hexagon (0 on an edge).
float hexEdge(vec2 p) {
    const vec2 s = vec2(1.0, 1.7320508);
    vec4 centers = floor(vec4(p, p - vec2(0.5, 1.0)) / s.xyxy) + 0.5;
    vec4 offsets = vec4(p - centers.xy * s, p - (centers.zw + 0.5) * s);
    vec2 h = dot(offsets.xy, offsets.xy) < dot(offsets.zw, offsets.zw) ? offsets.xy : offsets.zw;
    h = abs(h);
    return 0.5 - max(dot(h, s * 0.5), h.x);
}

void fragment() {
    vec2 uv = SCREEN_UV;
    float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
    vec2 p = vec2(uv.x * aspect, uv.y);
    vec2 pixel = FRAGCOORD.xy;
    vec3 color = mix(base_color.rgb, vec3(0.012, 0.03, 0.05), uv.y);

    // Nebula: faint teal and amber dust.
    float dust = fbm(p * 2.2 + vec2(TIME * 0.004, 0.0));
    color += hud.rgb * 0.16 * smoothstep(0.35, 0.95, dust) * smoothstep(1.3, 0.0, distance(uv, vec2(0.25, 0.3)));
    color += accent.rgb * 0.12 * smoothstep(0.4, 0.95, fbm(p * 1.4 + 7.0)) * smoothstep(1.1, 0.0, distance(uv, vec2(0.6, 0.15)));
    color += vec3(0.9, 0.55, 0.25) * 0.05 * smoothstep(0.55, 1.0, fbm(p * 3.0 + 13.0)) * smoothstep(0.8, 0.0, distance(uv, vec2(0.45, 0.55)));
    // A distant sun in the upper left: a soft glare and a faint horizontal flare.
    vec2 sun = vec2(0.08 * aspect, 0.1);
    float ds = distance(p, sun);
    color += vec3(1.0, 0.92, 0.8) * (0.012 / (ds + 0.02) * 0.35 + exp(-ds * 9.0) * 0.12);
    color += vec3(0.6, 0.75, 1.0) * exp(-abs(p.y - sun.y) * 160.0) * exp(-abs(p.x - sun.x) * 3.0) * 0.18;

    // Stars: three layers, some of them tinted, twinkling.
    for (int layer = 0; layer < 3; layer++) {
        float size = 2.0 + float(layer) * 1.5;
        vec2 cell = floor(pixel / size + float(layer) * 31.0);
        float star = hash(cell);
        float threshold = 0.9975 + float(layer) * 0.0008;
        float twinkle = 0.6 + 0.4 * sin(TIME * (0.8 + star * 2.5) + star * 50.0);
        vec3 tint = mix(vec3(0.75, 0.85, 1.0), vec3(1.0, 0.85, 0.7), hash(cell + 3.0));
        color += tint * step(threshold, star) * twinkle * (0.9 - float(layer) * 0.25);
    }

    // Gas giant in the lower right corner.
    vec2 center = vec2(aspect * 0.98, 1.38);
    float radius = 0.78;
    vec2 d = (p - center) / radius;
    float r = length(d);
    vec3 light = normalize(vec3(-0.65, -0.55, 0.55));
    if (r < 1.0) {
        vec3 n = vec3(d, sqrt(1.0 - r * r));
        float lit = clamp(dot(n, light), 0.0, 1.0);
        // Bands of clouds following the latitude (tilted), stirred by noise, drifting slowly.
        vec2 surface = vec2(n.x / (n.z + 0.6) + TIME * 0.003, n.y * 1.0 + 0.25 * n.x);
        float bands = sin(surface.y * 26.0 + fbm(surface * vec2(3.0, 10.0)) * 5.0);
        float storms = fbm(surface * vec2(6.0, 18.0));
        vec3 cloud = mix(vec3(0.16, 0.12, 0.1), vec3(0.42, 0.32, 0.24), 0.5 + 0.5 * bands);
        cloud = mix(cloud, vec3(0.55, 0.42, 0.3), smoothstep(0.55, 0.8, storms) * 0.6);
        vec3 planet = cloud * (0.04 + 1.1 * pow(lit, 1.3));
        // Terminator scattering and the atmosphere seen at the limb.
        planet += vec3(0.25, 0.55, 0.85) * pow(1.0 - n.z, 3.0) * (0.15 + 0.85 * lit);
        color = mix(color, planet, smoothstep(1.0, 0.995, r));
    }
    // Atmosphere glow beyond the limb, brighter on the lit side.
    float side = clamp(dot(normalize(vec3(d, 0.0)), light) + 0.3, 0.0, 1.0);
    color += vec3(0.3, 0.65, 1.0) * exp(-max(r - 1.0, 0.0) * 40.0) * smoothstep(0.97, 1.0, r) * 0.6 * side;
    color += vec3(0.2, 0.45, 0.8) * exp(-max(r - 1.0, 0.0) * 9.0) * step(1.0, r) * 0.12 * side;

    // Vignette.
    color *= mix(0.5, 1.0, smoothstep(1.2, 0.3, distance(uv, vec2(0.5))));
    color *= 1.0 + energy * 0.15;
    COLOR = vec4(color, 1.0);
}
