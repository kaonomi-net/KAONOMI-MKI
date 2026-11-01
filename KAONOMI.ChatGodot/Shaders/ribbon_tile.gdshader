// A tool of the ribbon of the tape: a slab of tinted glass in 3D (a quad whose rounded corners and bevel come from a
// signed distance field), lit by the lights and the sky of the shelf: a Fresnel rim (the glass brightens where it is
// seen at a glancing angle), a reflection that slides over it under the pointer ("hover" 0 to 1), a cyan edge that
// glows then, dimmer while the tool is pressed ("pressed" 0 to 1). "light_theme": frosted white glass instead of dark.
shader_type spatial;
render_mode cull_back, depth_draw_opaque, blend_mix, specular_schlick_ggx;

uniform vec4 glass : source_color = vec4(0.10, 0.11, 0.13, 0.36);
uniform vec4 edge_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float radius = 0.2;
uniform float bevel = 0.14;
uniform float hover = 0.0;
uniform float pressed = 0.0;
uniform float streak = 0.0;
uniform bool light_theme = false;

float rounded_box(vec2 p, vec2 half_size, float r) {
    vec2 q = abs(p) - half_size + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float d = rounded_box(p, vec2(1.0), radius * 2.0);
    if (d > 0.0) {
        discard;
    }
    float inside = -d;
    // The bevel: the surface turns outward near the edge, so that the lights catch the rim of the glass.
    float rim = 1.0 - smoothstep(0.0, bevel, inside);
    vec2 outward = normalize(p + vec2(1e-4));
    vec3 n = normalize(vec3(outward * rim * 0.9, 1.0 - rim * 0.6));
    NORMAL_MAP = n * 0.5 + 0.5;
    NORMAL_MAP_DEPTH = 1.0;
    // Fresnel: brighter and less transparent at a glancing angle (the tilt under the pointer shows it).
    float fresnel = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 3.0);
    // A reflection: a soft diagonal band of light that slides across the slab with "streak" (0 to 1).
    float band = UV.x * 0.75 + (1.0 - UV.y) * 0.45 - streak * 1.4 + 0.1;
    float reflection = smoothstep(0.14, 0.0, abs(band)) * (0.18 + 0.4 * hover);
    // A frost: lighter towards the top, like light caught in the thickness of the glass.
    float frost = smoothstep(0.2, 1.0, UV.y) * 0.06;
    vec3 base = light_theme ? mix(glass.rgb, vec3(1.0), 0.3) : glass.rgb;
    vec3 tint = base + frost + reflection * (light_theme ? 0.18 : 0.3) + fresnel * 0.08;
    ALBEDO = tint + hover * 0.02;
    ALPHA = clamp(glass.a + fresnel * 0.28 + rim * 0.18 + reflection * 0.18 + hover * 0.06, 0.0, 1.0);
    METALLIC = light_theme ? 0.0 : 0.25;
    ROUGHNESS = light_theme ? 0.35 : 0.12;
    SPECULAR = 0.9;
    // The edge: a hairline of light, a little brighter under the pointer, quieter while pressed; the rim lit faintly.
    float line = 1.0 - smoothstep(0.0, 0.025, inside);
    float strength = (0.18 + 0.45 * hover) * (1.0 - 0.5 * pressed);
    EMISSION = edge_color.rgb * line * strength + edge_color.rgb * (rim * 0.05 + fresnel * 0.08) * (0.5 + hover) + vec3(reflection * 0.08);
}
