// A window of the desktop or of the stage of the voice mode: its picture with rounded corners (outside them,
// transparent: what is behind shows through), the edge smoothed over one pixel. "size" is the size of the window in
// pixels, "radius" the radius of its corners.
shader_type canvas_item;

uniform vec2 size = vec2(100.0, 100.0);
uniform float radius = 18.0;

void fragment() {
	vec4 picture = texture(TEXTURE, UV);
	vec2 half_size = size * 0.5;
	vec2 corner = abs(UV * size - half_size) - (half_size - vec2(radius));
	float distance_out = length(max(corner, 0.0)) - radius;
	COLOR = picture * COLOR;
	COLOR.a *= clamp(0.5 - distance_out, 0.0, 1.0);
}
