// Background of the Police theme: the city map of a dispatch center at night. A navy ground, the blocks of the city
// between streets (avenues brighter, side streets dim), a river crossing it, patrol cars as blue dots driving along
// the streets, two incidents marked by red rings that pulse, and at the top a slow glow of a light bar, blue on the
// left and red on the right, alternating every three seconds (soft, never a strobe). "energy" (0 to 1) brightens the
// patrols and the incidents while Kaonomi speaks.
shader_type canvas_item;

uniform vec4 navy : source_color = vec4(0.035, 0.06, 0.12, 1.0);
uniform vec4 navy_deep : source_color = vec4(0.02, 0.03, 0.07, 1.0);
uniform vec4 street : source_color = vec4(0.45, 0.58, 0.78, 1.0);
uniform vec4 water : source_color = vec4(0.05, 0.12, 0.22, 1.0);
uniform vec4 patrol : source_color = vec4(0.3, 0.6, 1.0, 1.0);
uniform vec4 siren_red : source_color = vec4(0.95, 0.2, 0.25, 1.0);
uniform vec4 siren_blue : source_color = vec4(0.2, 0.4, 1.0, 1.0);
uniform float energy = 0.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float hash1(float p) { return fract(sin(p * 78.233) * 43758.5453); }

// Thin line at the multiples of 1 of v (anti-aliased by the derivative).
float gridLine(float v, float width) {
    float d = abs(fract(v + 0.5) - 0.5);
    float aa = fwidth(v);
    return 1.0 - smoothstep(width * aa, (width + 1.0) * aa, d);
}

void fragment() {
    vec2 uv = SCREEN_UV;
    float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
    vec2 p = vec2(uv.x * aspect, uv.y);
    float t = TIME;

    vec3 color = mix(navy.rgb, navy_deep.rgb, smoothstep(0.2, 1.3, distance(uv, vec2(0.5, 0.55))));

    // The city: a grid turned a little, its blocks of uneven size (a warp along each axis).
    float angle = 0.12;
    vec2 q = mat2(vec2(cos(angle), sin(angle)), vec2(-sin(angle), cos(angle))) * p;
    vec2 warped = q * vec2(22.0, 18.0) + vec2(0.35 * sin(q.y * 3.0), 0.3 * sin(q.x * 2.5));
    float side = max(gridLine(warped.x, 0.6), gridLine(warped.y, 0.6));
    float avenue = max(gridLine(warped.x / 5.0, 1.2), gridLine(warped.y / 4.0, 1.2));
    // The blocks: some lit a little (a building, a square), most dark.
    vec2 block = floor(warped);
    float lit = step(0.82, hash(block)) * 0.035;
    color += street.rgb * lit;

    // The river: a winding band across the city, the streets stopping at its banks, its banks drawn.
    float riverY = 0.62 + 0.08 * sin(p.x * 3.0 + 0.6) + 0.03 * sin(p.x * 9.0);
    float riverD = abs(p.y - riverY);
    float inRiver = 1.0 - smoothstep(0.03, 0.034, riverD);
    float bank = (1.0 - smoothstep(0.0, 0.003, abs(riverD - 0.032)));
    color += street.rgb * (side * 0.06 + avenue * 0.16) * (1.0 - inRiver);
    color = mix(color, water.rgb, inRiver * 0.85);
    color += street.rgb * bank * 0.18;
    // Two bridges.
    for (int i = 0; i < 2; i++) {
        float bx = (0.3 + 0.4 * float(i)) * aspect;
        float bridge = (1.0 - smoothstep(0.004, 0.007, abs(p.x - bx))) * (1.0 - smoothstep(0.034, 0.04, riverD));
        color = mix(color, street.rgb * 0.35, bridge);
    }

    // The patrols: blue dots driving along the avenues, one way or the other, at their own speed, a short trail.
    for (int i = 0; i < 10; i++) {
        float fi = float(i);
        bool horizontal = hash1(fi * 2.3) > 0.5;
        float lane = floor(hash1(fi * 5.7) * 5.0);
        float speed = (0.01 + 0.02 * hash1(fi * 7.1)) * (hash1(fi * 9.3) > 0.5 ? 1.0 : -1.0);
        float along = fract(hash1(fi * 11.9) + t * speed);
        // The position of the car in the warped space of the city, then its distance in pixels of the warped grid.
        vec2 car = horizontal ? vec2(along * 110.0, (lane + 0.0) * 4.0) : vec2((lane + 0.0) * 5.0, along * 90.0);
        vec2 d = warped - car;
        float dist = length(d);
        float spot = 1.0 - smoothstep(0.12, 0.2, dist);
        vec2 back = horizontal ? vec2(-sign(speed), 0.0) : vec2(0.0, -sign(speed));
        float trail = (1.0 - smoothstep(0.0, 0.08, abs(horizontal ? d.y : d.x))) * smoothstep(1.6, 0.0, dot(d, back)) * step(0.0, dot(d, back));
        color += patrol.rgb * (spot * 0.9 + trail * 0.18) * (1.0 + energy);
    }

    // The incidents: red rings that pulse out from a point, every two seconds, two places of the city.
    for (int i = 0; i < 2; i++) {
        float fi = float(i);
        vec2 c = vec2((0.2 + 0.55 * hash1(fi * 3.3 + 1.0)) * aspect, 0.2 + 0.35 * hash1(fi * 4.4 + 2.0));
        float r = distance(p, c);
        float phase = fract(t * 0.5 + fi * 0.5);
        float ring = (1.0 - smoothstep(0.0, 0.004, abs(r - phase * 0.06))) * (1.0 - phase);
        float core = 1.0 - smoothstep(0.004, 0.007, r);
        color += siren_red.rgb * (ring * 0.6 + core * 0.8) * (1.0 + energy);
    }

    // The light bar: a soft glow at the top, blue on the left and red on the right, taking turns every 3 s.
    float turn = 0.5 + 0.5 * sin(t * 2.0944);
    float top = smoothstep(0.35, 0.0, uv.y);
    color += siren_blue.rgb * top * smoothstep(0.7, 0.0, uv.x) * 0.1 * turn;
    color += siren_red.rgb * top * smoothstep(0.3, 1.0, uv.x) * 0.08 * (1.0 - turn);

    // Scanlines of the display and a vignette.
    color *= 0.96 + 0.04 * sin(FRAGCOORD.y * 3.1416 * 0.5);
    color *= mix(0.55, 1.0, smoothstep(1.3, 0.3, distance(uv, vec2(0.5))));
    COLOR = vec4(color, 1.0);
}
