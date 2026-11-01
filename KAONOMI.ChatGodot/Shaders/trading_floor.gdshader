// Background of the Trading theme: the wall of a trading floor of crypto and stocks. A graphite ground with the grid
// of a chart; across the middle, candlesticks scrolling slowly to the left (green when the price rose, red when it
// fell, their wicks), a dashed line at the last price; above, a thin price line of another market with its area;
// at the bottom, the depth of the order book, bids in green rising to the middle, asks in red; a bar of the ticker
// at the very top. Everything faint, behind the windows. "energy" (0 to 1) brightens the charts while Kaonomi speaks.
shader_type canvas_item;

uniform vec4 ground : source_color = vec4(0.043, 0.055, 0.067, 1.0);
uniform vec4 grid_color : source_color = vec4(0.17, 0.19, 0.22, 1.0);
uniform vec4 up : source_color = vec4(0.055, 0.8, 0.5, 1.0);
uniform vec4 down : source_color = vec4(0.96, 0.27, 0.36, 1.0);
uniform vec4 gold : source_color = vec4(0.94, 0.73, 0.04, 1.0);
uniform float energy = 0.0;

float hash1(float p) { return fract(sin(p * 78.233) * 43758.5453); }
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

// A smooth random walk: the price at x (in candles), between 0 and 1.
float walk(float x) {
    float sum = 0.0;
    float amplitude = 0.5;
    float frequency = 0.035;
    for (int i = 0; i < 5; i++) {
        float xi = x * frequency;
        float a = hash1(floor(xi) + float(i) * 17.0);
        float b = hash1(floor(xi) + 1.0 + float(i) * 17.0);
        float f = fract(xi);
        sum += amplitude * mix(a, b, f * f * (3.0 - 2.0 * f));
        amplitude *= 0.5;
        frequency *= 2.3;
    }
    return sum / 0.97;
}

void fragment() {
    vec2 uv = SCREEN_UV;
    vec2 pixel = FRAGCOORD.xy;
    vec2 size = 1.0 / SCREEN_PIXEL_SIZE;
    float t = TIME;
    float y = 1.0 - uv.y;                       // 0 at the bottom, 1 at the top
    float glow = 1.0 + energy * 0.8;

    vec3 color = ground.rgb * mix(1.15, 0.85, uv.y);

    // The grid of the chart: horizontal levels every 60 px, vertical lines every 120 px, dotted.
    float gx = mod(pixel.x + t * 6.0, 120.0);
    float gy = mod(pixel.y, 60.0);
    float dotted = step(2.0, mod(pixel.x, 4.0));
    float gridLines = (1.0 - step(1.0, gy)) * dotted + (1.0 - step(1.0, gx)) * step(2.0, mod(pixel.y, 4.0));
    color += grid_color.rgb * gridLines * 0.45;

    // The candlesticks: one every 14 px, scrolling left, in the band from 30% to 75% of the height.
    float w = 14.0;
    float scroll = t * 4.0;                     // pixels per second
    float x = (pixel.x + scroll) / w;
    float index = floor(x);
    float inCandle = fract(x);
    float open = walk(index);
    float close = walk(index + 1.0);
    float high = max(open, close) + 0.03 * hash1(index * 3.1);
    float low = min(open, close) - 0.03 * hash1(index * 5.7);
    float band0 = 0.3;
    float band1 = 0.75;
    float yo = mix(band0, band1, open);
    float yc = mix(band0, band1, close);
    float yh = mix(band0, band1, high);
    float yl = mix(band0, band1, low);
    float rising = step(open, close);
    vec3 candle = mix(down.rgb, up.rgb, rising);
    float bodyPx = max(abs(yc - yo) * size.y, 1.5);
    float bodyY = (min(yo, yc) + max(yo, yc)) * 0.5;
    float inBody = step(abs(y - bodyY) * size.y, bodyPx * 0.5) * step(0.18, inCandle) * step(inCandle, 0.82);
    float inWick = step(yl, y) * step(y, yh) * (1.0 - step(1.0, abs(inCandle - 0.5) * w));
    color += candle * (inBody * 0.32 + inWick * 0.22) * glow;

    // The last price: a dashed line in gold at the close of the last candle on the screen.
    float lastIndex = floor((size.x + scroll) / w);
    float lastY = mix(band0, band1, walk(lastIndex + 1.0));
    float dash = step(0.5, fract(pixel.x / 10.0));
    color += gold.rgb * (1.0 - step(1.0, abs(y - lastY) * size.y)) * dash * 0.4;

    // Another market above: a thin line and the area under it, in gold.
    float x2 = (pixel.x + t * 7.0) / 6.0;
    float price2 = mix(0.8, 0.93, walk(x2 * 0.6 + 400.0));
    float lineD = abs(y - price2) * size.y;
    color += gold.rgb * (1.0 - smoothstep(0.5, 1.6, lineD)) * 0.35 * glow;
    color += gold.rgb * step(0.78, y) * step(y, price2) * 0.035;

    // The depth of the order book at the bottom: bids in green rising to the middle, asks in red on the right.
    float mid = 0.5;
    float depthX = abs(uv.x - mid) / mid;      // 0 at the middle, 1 at the edges
    float stepsX = floor(depthX * 40.0);
    float depth = 0.02 + 0.2 * pow(depthX, 1.4) + 0.012 * hash1(stepsX + step(mid, uv.x) * 100.0 + floor(t * 1.5));
    float inDepth = step(y, depth);
    vec3 side = mix(up.rgb, down.rgb, step(mid, uv.x));
    float edge = 1.0 - step(1.5, abs(y - depth) * size.y);
    color += side * (inDepth * 0.08 + edge * 0.3) * glow;
    color += gold.rgb * (1.0 - step(1.0, abs(uv.x - mid) * size.x)) * step(y, 0.24) * 0.3;

    // The ticker at the very top: small blocks of green and red sliding left.
    float tickerY = 1.0 - y;
    float inTicker = step(tickerY * size.y, 22.0) * step(6.0, tickerY * size.y);
    float tickerX = (pixel.x + t * 60.0) / 70.0;
    float item = floor(tickerX);
    float tickerUp = step(0.45, hash1(item * 2.7));
    float label = step(0.12, fract(tickerX)) * step(fract(tickerX), 0.62);
    float arrow = step(0.68, fract(tickerX)) * step(fract(tickerX), 0.86);
    color += grid_color.rgb * inTicker * label * 0.6;
    color += mix(down.rgb, up.rgb, tickerUp) * inTicker * arrow * 0.4;
    color += grid_color.rgb * (1.0 - step(1.0, abs(tickerY * size.y - 26.0))) * 0.8;

    // A vignette.
    color *= mix(0.6, 1.0, smoothstep(1.3, 0.3, distance(uv, vec2(0.5))));
    COLOR = vec4(color, 1.0);
}
