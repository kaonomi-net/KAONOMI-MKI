// Solid glowing parts of the hologram (eyes, mouth, equalizer bars, core): additive light whose strength follows
// "intensity" and the voice ("energy").
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;

uniform vec4 color : source_color = vec4(0.2, 0.95, 1.0, 1.0);
uniform float intensity = 2.0;
uniform float energy = 0.0;

void fragment() {
    ALBEDO = color.rgb * (intensity + energy * 2.5);
}
