// Background of the Matrix theme: the digital rain of the film. Columns of glyphs fall at their own speed, each a
// random pattern of small blocks (katakana-like) that flickers now and then; the head of a trail is white-green and
// bright, the trail fades to the dark green of the screen; a faint grid of phosphor and scanlines behind, a vignette.
// "energy" (0 to 1) makes the rain brighter and faster while Kaonomi speaks.
shader_type canvas_item;

uniform vec4 base_color : source_color = vec4(0.0, 0.016, 0.004, 1.0);
uniform vec4 rain : source_color = vec4(0.0, 1.0, 0.25, 1.0);
uniform vec4 head : source_color = vec4(0.8, 1.0, 0.85, 1.0);
uniform float energy = 0.0;
uniform float cell = 18.0;       // size of a glyph in pixels
uniform float density = 0.85;    // share of the columns that rain

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float hash1(float p) { return fract(sin(p * 78.233) * 43758.5453); }

// A glyph: 3 x 5 blocks lit by the bits of a random number (changed now and then), drawn inside the cell.
float glyph(vec2 inCell, float seed, float time) {
    // The glyph of this cell changes a few times a second, more often on the head.
    float frame = floor(time * (2.0 + 6.0 * hash1(seed + 3.0)));
    float bits = hash(vec2(seed, frame));
    vec2 grid = vec2(3.0, 5.0);
    vec2 pad = vec2(0.2, 0.12);
    vec2 g = (inCell - pad) / (1.0 - 2.0 * pad);
    if (g.x < 0.0 || g.x >= 1.0 || g.y < 0.0 || g.y >= 1.0)
        return 0.0;
    vec2 block = floor(g * grid);
    float index = block.y * grid.x + block.x;
    // 15 bits from a hash per block, about 55% of them lit.
    float lit = step(0.45, hash(vec2(bits * 100.0, index)));
    // Thin gaps between the blocks.
    vec2 inner = fract(g * grid);
    float gap = step(0.12, inner.x) * step(0.12, inner.y);
    return lit * gap;
}

void fragment() {
    vec2 pixel = FRAGCOORD.xy;
    vec2 uv = SCREEN_UV;
    float speedUp = 1.0 + energy * 0.8;
    vec3 color = base_color.rgb;

    // Phosphor grid and scanlines.
    float scan = 0.92 + 0.08 * sin(pixel.y * 3.1416);
    color *= scan;

    // The rain: each column has its speed, its length of trail and its phase.
    float column = floor(pixel.x / cell);
    float cseed = hash1(column * 1.7 + 0.3);
    float rains = step(1.0 - density, hash1(column * 3.1 + 7.0));
    float speed = (3.0 + 9.0 * cseed) * speedUp;           // cells per second
    float trail = 8.0 + 22.0 * hash1(column * 5.3 + 1.0);   // cells
    float rows = ceil(1.0 / (SCREEN_PIXEL_SIZE.y * cell)) + trail;
    // The head of the trail: it falls, then starts again from above after a pause of its own.
    float cycle = rows + trail * hash1(column * 9.7);
    float headRow = mod(TIME * speed + cseed * 500.0, cycle) - trail * 0.5;
    float row = floor(pixel.y / cell);
    float behind = headRow - row;                           // cells behind the head (0: the head)
    vec2 inCell = fract(pixel / cell);
    inCell.y = 1.0 - inCell.y;
    float seed = column * 131.0 + row * 7.0;
    float g = glyph(inCell, seed, TIME);
    if (rains > 0.5 && behind >= 0.0 && behind < trail) {
        float fade = pow(1.0 - behind / trail, 1.6);
        vec3 tint = mix(rain.rgb, head.rgb, smoothstep(1.5, 0.0, behind));
        // A faint glow of the cell itself, the glyph on top.
        color += rain.rgb * 0.06 * fade;
        color += tint * g * (0.35 + 0.65 * fade) * (0.85 + energy * 0.5);
    }
    // A second, dimmer and slower layer of rain, offset half a cell, for depth.
    float column2 = floor((pixel.x + cell * 0.5) / (cell * 1.4));
    float cseed2 = hash1(column2 * 2.9 + 11.0);
    float speed2 = (2.0 + 5.0 * cseed2) * speedUp;
    float trail2 = 10.0 + 20.0 * hash1(column2 * 4.1);
    float rows2 = ceil(1.0 / (SCREEN_PIXEL_SIZE.y * cell * 1.4)) + trail2;
    float headRow2 = mod(TIME * speed2 + cseed2 * 700.0, rows2 + trail2 * hash1(column2 * 6.3)) - trail2 * 0.5;
    float row2 = floor(pixel.y / (cell * 1.4));
    float behind2 = headRow2 - row2;
    if (step(0.35, hash1(column2 * 8.3 + 2.0)) > 0.5 && behind2 >= 0.0 && behind2 < trail2) {
        vec2 inCell2 = fract((pixel + vec2(cell * 0.5, 0.0)) / (cell * 1.4));
        inCell2.y = 1.0 - inCell2.y;
        float g2 = glyph(inCell2, column2 * 97.0 + row2 * 13.0 + 500.0, TIME * 0.7);
        color += rain.rgb * g2 * 0.22 * pow(1.0 - behind2 / trail2, 1.8);
    }

    // Vignette, and the screen a little brighter while Kaonomi speaks.
    color *= mix(0.45, 1.0, smoothstep(1.25, 0.25, distance(uv, vec2(0.5))));
    color *= 1.0 + energy * 0.1;
    COLOR = vec4(color, 1.0);
}
