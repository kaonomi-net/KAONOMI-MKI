// Overlay of the Ghost theme, drawn over its picture (Assets/ghost-city.jpg: the Major on a rooftop over Niihama at
// night, cyan and magenta neons, rain): a veil of the deep blue of the night so that the windows stay readable, rain
// in thin streaks, a cyan and magenta breath of the neons rising from the city, bands of thermoptic glitch now and
// then, faint scanlines and a vignette. "energy" (0 to 1) wakes the neons while Kaonomi speaks.
shader_type canvas_item;

uniform vec4 veil : source_color = vec4(0.02, 0.06, 0.1, 1.0);
uniform float veil_strength = 0.28;
uniform vec4 neon_cyan : source_color = vec4(0.18, 0.9, 0.88, 1.0);
uniform vec4 neon_magenta : source_color = vec4(1.0, 0.3, 0.65, 1.0);
uniform float energy = 0.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float hash1(float p) { return fract(sin(p * 78.233) * 43758.5453); }

void fragment() {
    vec2 uv = SCREEN_UV;
    vec2 pixel = FRAGCOORD.xy;
    float t = TIME;

    // The veil and the vignette: darker at the edges, the picture showing through.
    float vignette = smoothstep(1.3, 0.3, distance(uv, vec2(0.5)));
    vec3 color = veil.rgb;
    float alpha = veil_strength + (1.0 - vignette) * 0.45;

    // The breath of the neons: cyan from the left of the city, magenta from the right, slow.
    float breath = 0.5 + 0.5 * sin(t * 0.7);
    vec3 glow = neon_cyan.rgb * smoothstep(0.9, 0.0, distance(uv, vec2(0.2, 0.8))) * (0.08 + 0.06 * breath)
              + neon_magenta.rgb * smoothstep(0.9, 0.0, distance(uv, vec2(0.75, 0.3))) * (0.07 + 0.06 * (1.0 - breath));
    glow *= 1.0 + energy * 2.0;

    // The rain: thin streaks falling fast, two layers, lit by the neons.
    vec3 rain = vec3(0.0);
    for (int layer = 0; layer < 2; layer++) {
        float fl = float(layer);
        vec2 q = vec2(pixel.x + fl * 37.0 + pixel.y * 0.05, pixel.y + t * (750.0 + fl * 350.0));
        vec2 size = vec2(8.0 + fl * 6.0, 64.0);
        vec2 cellR = floor(q / size);
        float drop = step(0.9 - fl * 0.05, hash(cellR));
        vec2 inR = fract(q / size);
        float streak = drop * smoothstep(0.53, 0.5, abs(inR.x - 0.5)) * smoothstep(0.0, 0.3, inR.y) * smoothstep(1.0, 0.7, inR.y);
        vec3 tint = mix(neon_cyan.rgb, neon_magenta.rgb, uv.x) * 0.5 + 0.5;
        rain += tint * streak * (0.16 - fl * 0.06);
    }

    // Thermoptic glitch: a band shifted and tinted for a few frames, now and then.
    float band = floor(uv.y * 28.0);
    float glitchTime = floor(t * 3.0);
    float glitch = step(0.988, hash(vec2(band, glitchTime))) * step(0.5, hash1(glitchTime));
    vec3 glitchTint = mix(neon_cyan.rgb, neon_magenta.rgb, hash(vec2(glitchTime, band))) * glitch * 0.35 * fract(uv.x * 30.0 + glitchTime);

    // Scanlines.
    float scan = 0.04 * (0.5 + 0.5 * sin(pixel.y * 3.1416 * 0.5));

    // The veil is blended by alpha; the lights are added (premultiplied in the colour, alpha covers them).
    vec3 light = glow + rain + glitchTint;
    float lightAlpha = clamp(max(max(light.r, light.g), light.b), 0.0, 1.0);
    float a = clamp(alpha + scan + lightAlpha * 0.6, 0.0, 0.85);
    vec3 result = mix(color, light + color * 0.3, clamp(lightAlpha * 2.0, 0.0, 1.0));
    result *= 1.0 + energy * 0.1;
    COLOR = vec4(result, a);
}
