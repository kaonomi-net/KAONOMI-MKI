// Background of the Minimal theme: a plain off-white, a touch warmer at the top, with a grain too fine to see but
// enough to keep large surfaces from banding, and a vignette so faint it only gives depth. Nothing moves.
// "energy" is accepted (the chat sets it) and left unused: the page stays still while Kaonomi speaks.
shader_type canvas_item;

uniform vec4 paper : source_color = vec4(0.985, 0.985, 0.98, 1.0);
uniform vec4 shade : source_color = vec4(0.955, 0.957, 0.96, 1.0);
uniform float energy = 0.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

void fragment() {
    vec2 uv = SCREEN_UV;
    vec3 color = mix(paper.rgb, shade.rgb, smoothstep(0.0, 1.0, uv.y));
    color *= mix(0.985, 1.0, smoothstep(1.3, 0.3, distance(uv, vec2(0.5))));
    color += (hash(FRAGCOORD.xy) - 0.5) * 0.006;
    COLOR = vec4(color, 1.0);
}
