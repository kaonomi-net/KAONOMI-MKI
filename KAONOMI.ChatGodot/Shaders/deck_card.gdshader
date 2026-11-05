// A window sent to the back of the desktop: its live picture on a card in 3D, dimmed and cooled with the depth, a
// hairline cyan edge, brighter when the pointer is over it. "light" (0 to 1) goes to 1 as the card comes to the front.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, blend_mix;

uniform sampler2D picture : source_color, filter_linear_mipmap;
uniform float light = 0.55;
uniform float hover = 0.0;
uniform vec4 edge_color : source_color = vec4(0.36, 0.76, 0.88, 1.0);
uniform bool light_theme = false;
// The size of the window in pixels and the radius of its corners: the card is cut as the window is.
uniform vec2 size = vec2(1.0, 1.0);
uniform float radius = 0.0;

void fragment() {
    vec4 texel = texture(picture, UV);
    vec3 color = texel.rgb;
    // Away from the front: darker, a little desaturated and cooled.
    float grey = dot(color, vec3(0.299, 0.587, 0.114));
    vec3 cool = mix(vec3(grey), color, 0.55) * vec3(0.86, 0.95, 1.05);
    // A light theme fades the cards towards the paper instead of darkening them.
    vec3 away = light_theme ? mix(cool, vec3(1.0), (1.0 - light) * 0.75) : cool * light;
    color = mix(away, color, smoothstep(0.92, 1.0, light));
    color += hover * 0.08;
    // The distance to the edge of the card, its corners rounded (in pixels of the window, negative inside).
    vec2 half_size = size * 0.5;
    vec2 corner = abs(UV * size - half_size) - (half_size - vec2(radius));
    float distance_out = length(max(corner, 0.0)) + min(max(corner.x, corner.y), 0.0) - radius;
    float aa = max(fwidth(distance_out), 0.0001);
    float inside = 1.0 - smoothstep(-aa, aa, distance_out);
    // A thin edge along it, stronger under the pointer.
    float width = max(size.x, size.y) * 0.003 + aa;
    float border = (1.0 - smoothstep(0.0, width, -distance_out)) * inside;
    float strength = (0.35 + 0.65 * hover) * (1.0 - smoothstep(0.92, 1.0, light));
    color = mix(color, edge_color.rgb, border * strength);
    float alpha = max(texel.a, border * strength) * mix(0.92, 1.0, light) * inside;
    ALBEDO = color;
    ALPHA = alpha;
}
