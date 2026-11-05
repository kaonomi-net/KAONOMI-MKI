// The shadow of a window of the desktop, drawn behind it on a rectangle larger by "margin" on each side: soft, a
// little lower than the window ("offset"), its corners rounded as the window's ("radius"). Nothing is drawn under the
// window itself, so that its glass is not darkened. "size" is the size of the rectangle of the shadow in pixels.
shader_type canvas_item;

uniform vec2 size = vec2(100.0, 100.0);
uniform float margin = 28.0;
uniform float radius = 12.0;
uniform vec2 offset = vec2(0.0, 6.0);
uniform vec4 shadow_color : source_color = vec4(0.0, 0.0, 0.0, 0.45);

// The distance to a rectangle with rounded corners centred on 0 (negative inside).
float rounded_box(vec2 p, vec2 half_size, float r) {
	vec2 q = abs(p) - (half_size - vec2(r));
	return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

void fragment() {
	vec2 p = UV * size - size * 0.5;
	vec2 half_window = max(size * 0.5 - vec2(margin), vec2(radius));
	float window_distance = rounded_box(p, half_window, radius);
	float shadow_distance = rounded_box(p - offset, half_window, radius);
	float spread = max(margin - length(offset), 1.0);
	float fall = 1.0 - clamp(shadow_distance / spread, 0.0, 1.0);
	float alpha = shadow_color.a * fall * fall;
	// Outside the window only (smoothed over one pixel at its edge).
	alpha *= smoothstep(-0.5, 0.5, window_distance);
	COLOR = vec4(shadow_color.rgb, alpha);
}
