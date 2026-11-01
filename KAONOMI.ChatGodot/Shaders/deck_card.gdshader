// A window sent to the back of the desktop: its live picture on a card in 3D, dimmed and cooled with the depth, a
// hairline cyan edge, brighter when the pointer is over it. "light" (0 to 1) goes to 1 as the card comes to the front.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, blend_mix;

uniform sampler2D picture : source_color, filter_linear_mipmap;
uniform float light = 0.55;
uniform float hover = 0.0;
uniform vec4 edge_color : source_color = vec4(0.36, 0.76, 0.88, 1.0);
uniform bool light_theme = false;

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
    // A thin edge around the card, stronger under the pointer.
    vec2 d = min(UV, 1.0 - UV);
    float border = 1.0 - smoothstep(0.0, 0.004, min(d.x, d.y * 1.4));
    float strength = (0.35 + 0.65 * hover) * (1.0 - smoothstep(0.92, 1.0, light));
    color = mix(color, edge_color.rgb, border * strength);
    float alpha = max(texel.a, border * strength) * mix(0.92, 1.0, light);
    ALBEDO = color;
    ALPHA = alpha;
}
