using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Views;

// GLSL of the 3D scenes of the band, without any Avalonia type so that the Godot chat (KAONOMI.ChatGodot) links this
// file and translates the same shaders to the Godot shading language: the faces of KaonomiScene and the holographic
// display of JarvisScene look and move the same in both chats.

/// <summary>Faces of <see cref="KaonomiScene"/>: their images and the GLSL that shapes, opens and lights them.</summary>
public static class FaceShaders
{
    /// <summary>The face shown for <paramref name="animation"/> (MARI for any value that is not a face).</summary>
    public static FaceModel For(SceneAnimation animation) => animation switch
    {
        SceneAnimation.Jerome => JeromeFace,
        SceneAnimation.Hal => HalFace,
        SceneAnimation.MarieKilleuse => MarieKilleuseFace,
        SceneAnimation.Musuko => MusukoFace,
        SceneAnimation.Kaonami => KaonamiFace,
        SceneAnimation.Pepper => PepperFace,
        SceneAnimation.Green => GreenFace,
        SceneAnimation.JeromeLive => JeromeLiveFace,
        SceneAnimation.Marvin => MarvinFace,
        SceneAnimation.Circuit => CircuitFace,
        SceneAnimation.Motuko => MotukoFace,
        SceneAnimation.Kaonomi => KaonomiRobotFace,
        SceneAnimation.Lin => LinFace,
        _ => MariFace
    };

    /// <summary>
    /// A face of the scene: its image and the GLSL that shapes it. <see cref="Glsl"/> holds the style (<c>#define</c>)
    /// and the mouth constants; <see cref="Shape"/> defines <c>mouthLine(x)</c> (y of the closed mouth),
    /// <c>relief(p)</c> (height of the mesh) and <c>jaw(p)</c> (share of the jaw drop), in texture coordinates
    /// (x right, y down).
    /// </summary>
    public sealed record FaceModel(string Image, string Glsl, string Shape);

    // Kaonomi robot: face oval centred at (0.5, 0.5), eyes at y 0.445, nose at 0.545, smiling mouth (closed) along
    // y 0.615 at the centre and 0.6 at its corners (x 0.41 to 0.59), chin at 0.75, headphones on the sides (x 0.17 and
    // 0.83), hair from 0.12 to 0.88 across.
    public static readonly FaceModel MariFace = new("avares://KAONOMI.Chat/Assets/kaonomi-face.png", """
        #define HOLOGRAM
        const float MOUTH_X = 0.5;        // centre of the mouth
        const float MOUTH_HALF = 0.085;   // half width of the opening
        const float MOUTH_GAP = 0.012;    // band below the mouth line stretched into the opening
        const float JAW_DROP = 0.06;      // drop of the chin, mouth wide open
        const vec3 MOUTH_DARK = vec3(0.32, 0.06, 0.13);
        const vec3 MOUTH_LIGHT = vec3(0.85, 0.36, 0.45);   // the tongue

        """, """
        // The line of the closed mouth (a smile: its corners are higher).
        float mouthLine(float x)
        {
            float d = (x - 0.5) / 0.09;
            return 0.615 - 0.015 * d * d;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.5);
            float face = 0.34 * sqrt(oval(p, vec2(0.5, 0.5), vec2(0.27, 0.28)));
            face += 0.05 * bump(p, vec2(0.5, 0.545), vec2(0.035, 0.03));             // small nose
            face += 0.02 * bump(p, vec2(0.5, 0.5), vec2(0.025, 0.05));               // nose bridge
            face += 0.04 * bump(vec2(ax, p.y), vec2(0.17, 0.565), vec2(0.07, 0.05));  // cheeks
            face -= 0.025 * bump(vec2(ax, p.y), vec2(0.135, 0.445), vec2(0.07, 0.045)); // eye sockets
            face += 0.02 * bump(p, vec2(0.5, 0.71), vec2(0.08, 0.04));               // chin
            float hair = 0.24 * sqrt(oval(p, vec2(0.5, 0.4), vec2(0.4, 0.45)));
            float phones = 0.3 * bump(vec2(ax, p.y), vec2(0.33, 0.47), vec2(0.05, 0.12)); // headphones
            float neck = 0.08 * sqrt(oval(p, vec2(0.5, 0.82), vec2(0.12, 0.12)));
            return max(max(face, max(hair, phones)), neck);
        }

        // Jaw: everything below the line of the mouth, across the chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.76, 0.84, p.y));
            float across = 1.0 - smoothstep(0.11, 0.2, abs(p.x - 0.5));
            return below * across;
        }

        """);

    // Photo portrait (cut out of its background): face axis at x 0.515, eyes at (0.405, 0.515) and (0.625, 0.51),
    // brows at y 0.465, nose tip at (0.51, 0.69), closed mouth along y 0.768 at the centre and 0.756 at its corners
    // (x 0.40 to 0.63), bearded chin down to 0.89, hair from 0.07, ears at x 0.26 and 0.755, collar and shoulders below.
    public static readonly FaceModel JeromeFace = new("avares://KAONOMI.Chat/Assets/jerome-face.png", """
        #define PORTRAIT
        const float MOUTH_X = 0.515;
        const float MOUTH_HALF = 0.08;
        const float MOUTH_GAP = 0.012;
        const float JAW_DROP = 0.045;
        const vec3 MOUTH_DARK = vec3(0.20, 0.05, 0.06);
        const vec3 MOUTH_LIGHT = vec3(0.62, 0.30, 0.30);    // the tongue

        """, """
        float mouthLine(float x)
        {
            float d = (x - 0.515) / 0.12;
            return 0.768 - 0.012 * d * d;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.515);
            float head = 0.36 * sqrt(oval(p, vec2(0.515, 0.555), vec2(0.245, 0.34)));
            head += 0.07 * bump(p, vec2(0.51, 0.67), vec2(0.035, 0.04));               // nose tip
            head += 0.04 * bump(p, vec2(0.515, 0.60), vec2(0.025, 0.08));              // nose bridge
            head += 0.035 * bump(vec2(ax, p.y), vec2(0.13, 0.63), vec2(0.06, 0.05));   // cheeks
            head -= 0.03 * bump(vec2(ax, p.y), vec2(0.11, 0.515), vec2(0.055, 0.03));  // eye sockets
            head += 0.025 * bump(vec2(ax, p.y), vec2(0.11, 0.465), vec2(0.07, 0.025)); // brow ridge
            head += 0.04 * bump(vec2(ax, p.y), vec2(0.245, 0.47), vec2(0.02, 0.04));   // ears
            // Chin and jaw: their own volume, so that they come forward instead of following the edge of the oval.
            head = max(head, 0.31 * sqrt(oval(p, vec2(0.515, 0.80), vec2(0.17, 0.12))));
            head += 0.06 * bump(p, vec2(0.515, 0.85), vec2(0.10, 0.05));               // point of the chin
            float hair = 0.27 * sqrt(oval(p, vec2(0.515, 0.32), vec2(0.25, 0.26)));
            float shoulders = 0.12 * sqrt(oval(p, vec2(0.5, 1.12), vec2(0.6, 0.35)));
            return max(max(head, hair), shoulders);
        }

        // Jaw: below the line of the mouth, down to the bearded chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.86, 0.92, p.y));
            float across = 1.0 - smoothstep(0.12, 0.2, abs(p.x - 0.515));
            return below * across;
        }

        """);

    // Eye of a computer: centre of the iris at (0.5, 0.478), glass dome of radius 0.27, red iris within 0.15, bezel
    // around 0.40, housing up to 0.48 (the pedestal below is cut off by the circle).
    public static readonly FaceModel HalFace = new("avares://KAONOMI.Chat/Assets/hal-eye.png", """
        #define HAL
        const vec2 EYE = vec2(0.5, 0.478);
        // No mouth: the eye glows with the voice instead (the mouth is out of the image and never opens).
        const float MOUTH_X = 0.5;
        const float MOUTH_HALF = 0.0;
        const float MOUTH_GAP = 1.0;
        const float JAW_DROP = 0.0;
        const vec3 MOUTH_DARK = vec3(0.0);
        const vec3 MOUTH_LIGHT = vec3(0.0);

        """, """
        float mouthLine(float x)
        {
            return -10.0;
        }

        float relief(vec2 p)
        {
            float r = length(p - EYE);
            float housing = 0.10 * sqrt(max(0.0, 1.0 - (r / 0.49) * (r / 0.49)));
            float b = (r - 0.40) / 0.04;
            float bezel = 0.06 * exp(-b * b);
            float lens = 0.22 * sqrt(max(0.0, 1.0 - (r / 0.27) * (r / 0.27)));   // glass dome
            return housing + bezel + lens;
        }

        float jaw(vec2 p)
        {
            return 0.0;
        }

        """);

    // Robot doll with a knife: face axis at x 0.5, spiral pupils at (0.368, 0.455) and (0.635, 0.455), nose at
    // (0.5, 0.545), grin whose upper and lower teeth meet along y 0.64 at the centre, 0.622 at its left corner (x 0.405)
    // and 0.596 at its right one (x 0.595),
    // robot chin plate down to 0.75, headphones at x 0.21 and 0.79, hearts of the chest at (0.5, 0.835) and
    // (0.5, 0.935), knife blade from (0.94, 0.33) to (0.81, 0.76), the hand on its handle around (0.8, 0.88).
    public static readonly FaceModel MarieKilleuseFace = new("avares://KAONOMI.Chat/Assets/mariekilleuse-face.png", """
        #define KILLER
        const vec2 EYE_LEFT = vec2(0.368, 0.455);
        const vec2 EYE_RIGHT = vec2(0.635, 0.455);
        const vec2 BLADE_TIP = vec2(0.94, 0.33);
        const vec2 BLADE_HEEL = vec2(0.81, 0.76);
        const float MOUTH_X = 0.5;
        const float MOUTH_HALF = 0.075;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.05;
        const vec3 MOUTH_DARK = vec3(0.25, 0.02, 0.06);
        const vec3 MOUTH_LIGHT = vec3(0.75, 0.15, 0.25);    // the tongue

        """, """
        // The line between the upper and the lower teeth of the grin (tilted: its right corner is higher), so that the
        // lower teeth go down with the jaw.
        float mouthLine(float x)
        {
            float t = clamp((x - 0.5) / 0.1, -1.0, 1.0);
            float t2 = t * t;
            return 0.6392 - 0.0106 * t + 0.005 * t2 - 0.042 * t2 * t2;
        }

        // Distance from p to the blade of the knife.
        float bladeDistance(vec2 p)
        {
            vec2 axis = BLADE_HEEL - BLADE_TIP;
            float t = clamp(dot(p - BLADE_TIP, axis) / dot(axis, axis), 0.0, 1.0);
            return length(p - BLADE_TIP - axis * t);
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.5);
            float face = 0.34 * sqrt(oval(p, vec2(0.5, 0.5), vec2(0.265, 0.27)));
            face += 0.05 * bump(p, vec2(0.5, 0.545), vec2(0.03, 0.025));              // small nose
            face += 0.04 * bump(vec2(ax, p.y), vec2(0.165, 0.565), vec2(0.06, 0.045)); // cheeks
            face -= 0.025 * bump(vec2(ax, p.y), vec2(0.135, 0.455), vec2(0.065, 0.05)); // eye sockets
            face += 0.03 * bump(p, vec2(0.5, 0.72), vec2(0.09, 0.035));               // chin plate
            float hair = 0.24 * sqrt(oval(p, vec2(0.5, 0.42), vec2(0.38, 0.42)));
            float phones = 0.3 * bump(vec2(ax, p.y), vec2(0.29, 0.48), vec2(0.05, 0.12)); // headphones
            float bow = 0.2 * bump(p, vec2(0.67, 0.09), vec2(0.05, 0.04));
            float neck = 0.1 * sqrt(oval(p, vec2(0.5, 0.80), vec2(0.12, 0.08)));
            float body = 0.15 * sqrt(oval(p, vec2(0.5, 1.05), vec2(0.45, 0.3)));
            // The knife and the hand holding it come out of the picture, towards the camera.
            float hand = 0.3 * bump(p, vec2(0.8, 0.88), vec2(0.08, 0.08));
            float d = bladeDistance(p) / 0.045;
            float blade = 0.26 * exp(-d * d);
            return max(max(max(face, max(hair, phones)), max(bow, max(neck, body))), max(hand, blade));
        }

        // Jaw: the lower teeth and the robot chin plate drop like the jaw of a ventriloquist doll (between the corners
        // of the mouth, so that its dark outline is not dragged down there).
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.745, 0.79, p.y));
            float across = 1.0 - smoothstep(0.06, 0.1, abs(p.x - 0.5));
            return below * across;
        }

        // Spiral pupil: the image turns around the centre of the eye, faster near it, so that the spiral spins.
        vec2 spin(vec2 uv, vec2 eye, float angle)
        {
            vec2 d = uv - eye;
            float fall = 1.0 - smoothstep(0.018, 0.034, length(d));
            float a = angle * fall;
            return eye + mat2(cos(a), -sin(a), sin(a), cos(a)) * d;
        }

        """);

    // Robot boy: face axis at x 0.483, green irises at (0.40, 0.3825) and (0.565, 0.3825), nose tip at (0.475, 0.465),
    // closed smile along y 0.517 at the centre and 0.507 at its corners (x 0.42 to 0.545), chin at 0.605, hair from
    // 0.1, ears at x 0.30 and 0.675, robot neck and white suit below with its lights (the core at (0.5, 0.762)).
    public static readonly FaceModel MusukoFace = new("avares://KAONOMI.Chat/Assets/musuko-face.png", """
        #define MUSUKO
        const vec2 EYE_LEFT = vec2(0.40, 0.3825);
        const vec2 EYE_RIGHT = vec2(0.565, 0.3825);
        const float MOUTH_X = 0.483;
        const float MOUTH_HALF = 0.058;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.035;
        const vec3 MOUTH_DARK = vec3(0.10, 0.04, 0.08);
        const vec3 MOUTH_LIGHT = vec3(0.55, 0.28, 0.32);   // the tongue

        """, """
        float mouthLine(float x)
        {
            float d = (x - 0.483) / 0.065;
            return 0.517 - 0.01 * d * d;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.483);
            float head = 0.34 * sqrt(oval(p, vec2(0.485, 0.42), vec2(0.175, 0.2)));
            head += 0.07 * bump(p, vec2(0.475, 0.462), vec2(0.03, 0.025));              // nose tip
            head += 0.03 * bump(p, vec2(0.48, 0.42), vec2(0.02, 0.05));                 // nose bridge
            head += 0.035 * bump(vec2(ax, p.y), vec2(0.1, 0.46), vec2(0.05, 0.04));     // cheeks
            head -= 0.025 * bump(vec2(ax, p.y), vec2(0.083, 0.383), vec2(0.045, 0.025)); // eye sockets
            head += 0.02 * bump(vec2(ax, p.y), vec2(0.085, 0.33), vec2(0.06, 0.02));    // brows
            head += 0.03 * bump(p, vec2(0.483, 0.575), vec2(0.07, 0.035));              // chin
            float ears = 0.12 * bump(vec2(ax, p.y), vec2(0.19, 0.44), vec2(0.025, 0.045));
            float hair = 0.27 * sqrt(oval(p, vec2(0.49, 0.29), vec2(0.215, 0.21)));
            float neck = 0.1 * sqrt(oval(p, vec2(0.5, 0.65), vec2(0.1, 0.07)));
            float body = 0.16 * sqrt(oval(p, vec2(0.5, 1.08), vec2(0.4, 0.4)));
            return max(max(max(head, ears), hair), max(neck, body));
        }

        // Jaw: below the line of the mouth, down to the chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.59, 0.625, p.y));
            float across = 1.0 - smoothstep(0.1, 0.15, abs(p.x - 0.483));
            return below * across;
        }

        // Light of one of the glowing lights of the suit.
        float light(vec2 p, vec2 center, float size)
        {
            return exp(-length(p - center) / size);
        }

        """);

    // Masked robot: face axis at x 0.5, head from y 0.04 to the chin at 0.775 (x 0.23 to 0.775), eye holes at
    // (0.335, 0.37) and (0.668, 0.37), nostrils at y 0.567, mouth slot from x 0.44 to 0.565 and y 0.625 to 0.655, ear
    // discs at x 0.155 and 0.845, neck and collar below 0.78, shoulders on the edges.
    public static readonly FaceModel KaonamiFace = new("avares://KAONOMI.Chat/Assets/kaonami-face.png", """
        #define KAONAMI
        const vec2 EYE_LEFT = vec2(0.335, 0.37);
        const vec2 EYE_RIGHT = vec2(0.668, 0.37);
        const vec2 SLOT = vec2(0.503, 0.64);
        const float MOUTH_X = 0.503;
        const float MOUTH_HALF = 0.055;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.035;
        const vec3 MOUTH_DARK = vec3(0.02, 0.02, 0.02);
        const vec3 MOUTH_LIGHT = vec3(0.35, 0.16, 0.05);   // the glow inside

        """, """
        // Bottom edge of the mouth slot.
        float mouthLine(float x)
        {
            return 0.653;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.5);
            float head = 0.36 * sqrt(oval(p, vec2(0.5, 0.42), vec2(0.275, 0.36)));
            head -= 0.03 * bump(vec2(ax, p.y), vec2(0.167, 0.37), vec2(0.04, 0.04));   // eye holes
            head += 0.03 * bump(p, vec2(0.5, 0.71), vec2(0.1, 0.05));                  // chin
            float ears = 0.2 * bump(vec2(ax, p.y), vec2(0.345, 0.38), vec2(0.06, 0.1)); // ear discs
            float neck = 0.1 * sqrt(oval(p, vec2(0.5, 0.82), vec2(0.15, 0.08)));
            float body = 0.16 * sqrt(oval(p, vec2(0.5, 1.1), vec2(0.55, 0.32)));
            return max(max(head, ears), max(neck, body));
        }

        // Jaw: the chin under the mouth slot drops like a flap.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.75, 0.79, p.y));
            float across = 1.0 - smoothstep(0.08, 0.14, abs(p.x - 0.503));
            return below * across;
        }

        """);

    // Robot with a tablet, the head tilted: head from y 0.04 to 0.69 (x 0.14 to 0.86), black eye lenses at (0.282, 0.357)
    // and (0.54, 0.29) in pink rings, small red mouth slanting from (0.377, 0.523) to (0.487, 0.498), ear speaker at
    // (0.83, 0.3), neck at (0.53, 0.73), tablet screen from x 0.1 to 0.68 and y 0.86 down, shoulder on the right.
    public static readonly FaceModel PepperFace = new("avares://KAONOMI.Chat/Assets/pepper-face.png", """
        #define PEPPER
        const vec2 EYE_LEFT = vec2(0.282, 0.357);
        const vec2 EYE_RIGHT = vec2(0.54, 0.29);
        const vec2 EAR = vec2(0.83, 0.3);
        const float MOUTH_X = 0.432;
        const float MOUTH_HALF = 0.05;
        const float MOUTH_GAP = 0.008;
        const float JAW_DROP = 0.025;
        const vec3 MOUTH_DARK = vec3(0.18, 0.02, 0.03);
        const vec3 MOUTH_LIGHT = vec3(0.55, 0.08, 0.1);   // the red inside

        """, """
        // Upper edge of the red mouth (slanting with the head).
        float mouthLine(float x)
        {
            return 0.527 - (x - 0.377) * 0.23;
        }

        float relief(vec2 p)
        {
            float head = 0.38 * sqrt(oval(p, vec2(0.5, 0.37), vec2(0.36, 0.33)));
            head -= 0.03 * bump(p, EYE_LEFT, vec2(0.06, 0.06));                     // eye rings
            head -= 0.03 * bump(p, EYE_RIGHT, vec2(0.06, 0.06));
            head += 0.04 * bump(p, vec2(0.42, 0.45), vec2(0.08, 0.06));             // face front
            float ear = 0.18 * bump(p, EAR, vec2(0.04, 0.09));
            float neck = 0.12 * sqrt(oval(p, vec2(0.53, 0.75), vec2(0.1, 0.08)));
            float body = 0.16 * sqrt(oval(p, vec2(0.6, 1.1), vec2(0.5, 0.35)));
            // The tablet stands out of the chest.
            vec2 t = (p - vec2(0.38, 0.93)) / vec2(0.34, 0.1);
            float tablet = 0.22 * exp(-pow(t.x * t.x, 3.0) - pow(t.y * t.y, 3.0));
            return max(max(head, ear), max(max(neck, body), tablet));
        }

        // Jaw: the lower lip of the small mouth drops with the voice.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.56, 0.6, p.y));
            float across = 1.0 - smoothstep(0.045, 0.07, abs(p.x - MOUTH_X));
            return below * across;
        }

        """);

    // Android child, face front: face axis at x 0.495, head cut by the top of the frame at y 0.083 (x 0.05 to 0.95 with
    // the ears), glowing green eyes at (0.30, 0.425) and (0.71, 0.425), brows at 0.29, nose tip at (0.498, 0.625), closed
    // lips along y 0.738 at the centre and 0.731 at their corners (x 0.418 to 0.569), chin at 0.8, green mechanical ears at
    // x 0.08 and 0.92, neck and shoulders below, cut by the bottom of the frame at 0.92.
    public static readonly FaceModel GreenFace = new("avares://KAONOMI.Chat/Assets/green-face.png", """
        #define GREEN
        const vec2 EYE_LEFT = vec2(0.30, 0.425);
        const vec2 EYE_RIGHT = vec2(0.71, 0.425);
        const float MOUTH_X = 0.495;
        const float MOUTH_HALF = 0.078;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.035;
        const vec3 MOUTH_DARK = vec3(0.02, 0.05, 0.04);
        const vec3 MOUTH_LIGHT = vec3(0.12, 0.45, 0.2);   // a green glow inside

        """, """
        float mouthLine(float x)
        {
            float d = (x - 0.495) / 0.075;
            return 0.738 - 0.007 * d * d;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.495);
            float head = 0.4 * sqrt(oval(p, vec2(0.5, 0.42), vec2(0.4, 0.45)));
            head += 0.08 * bump(p, vec2(0.498, 0.615), vec2(0.04, 0.035));               // nose tip
            head += 0.035 * bump(p, vec2(0.497, 0.53), vec2(0.025, 0.07));               // nose bridge
            head += 0.04 * bump(vec2(ax, p.y), vec2(0.2, 0.6), vec2(0.08, 0.06));        // cheeks
            head -= 0.035 * bump(vec2(ax, p.y), vec2(0.21, 0.425), vec2(0.09, 0.045));   // eye sockets
            head += 0.025 * bump(vec2(ax, p.y), vec2(0.2, 0.3), vec2(0.1, 0.025));       // brows
            head += 0.03 * bump(p, vec2(0.497, 0.775), vec2(0.08, 0.035));               // chin
            float ears = 0.16 * bump(vec2(ax, p.y), vec2(0.42, 0.47), vec2(0.04, 0.09));
            float neck = 0.1 * sqrt(oval(p, vec2(0.5, 0.88), vec2(0.15, 0.08)));
            float body = 0.16 * sqrt(oval(p, vec2(0.5, 1.12), vec2(0.6, 0.28)));
            return max(max(head, ears), max(neck, body));
        }

        // Jaw: below the lips, down to the chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.8, 0.835, p.y));
            float across = 1.0 - smoothstep(0.12, 0.18, abs(p.x - 0.495));
            return below * across;
        }

        """);

    // Jerome filmed: one frame of his video, aligned on the eyes (at (0.425, 0.4) and (0.575, 0.4)) and cut out. Hair from
    // y 0.09, nose tip at (0.5, 0.533), closed lips from x 0.435 to 0.56 along y 0.577 to 0.587 (a little lower on the
    // right), chin at 0.704, face from x 0.318 to 0.661, T-shirt and shoulders below 0.66, cut by the bottom of the frame.
    public static readonly FaceModel JeromeLiveFace = new("avares://KAONOMI.Chat/Assets/jeromelive-face.png", """
        #define LIVE
        const vec2 EYE_LEFT = vec2(0.425, 0.4);
        const vec2 EYE_RIGHT = vec2(0.575, 0.4);
        const vec2 EYE_SIZE = vec2(0.05, 0.02);     // half size of the eyelid, past the corners and the lashes
        const float MOUTH_X = 0.497;
        const float MOUTH_HALF = 0.06;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.03;
        const vec3 MOUTH_DARK = vec3(0.09, 0.03, 0.03);
        const vec3 MOUTH_LIGHT = vec3(0.5, 0.25, 0.25);   // the tongue

        """, """
        float mouthLine(float x)
        {
            float u = x - 0.497;
            return 0.5772 + 0.045 * u + 1.975 * u * u;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.49);
            float head = 0.3 * sqrt(oval(p, vec2(0.49, 0.42), vec2(0.18, 0.3)));
            head += 0.06 * bump(p, vec2(0.5, 0.52), vec2(0.022, 0.025));                // nose tip
            head += 0.025 * bump(p, vec2(0.5, 0.46), vec2(0.015, 0.05));                // nose bridge
            head += 0.03 * bump(vec2(ax, p.y), vec2(0.1, 0.5), vec2(0.045, 0.04));      // cheeks
            head -= 0.02 * bump(vec2(ax, p.y), vec2(0.075, 0.4), vec2(0.035, 0.02));    // eye sockets
            head += 0.025 * bump(p, vec2(0.497, 0.67), vec2(0.06, 0.03));               // chin
            float ears = 0.1 * bump(vec2(ax, p.y), vec2(0.175, 0.44), vec2(0.02, 0.045));
            float hair = 0.24 * sqrt(oval(p, vec2(0.49, 0.26), vec2(0.18, 0.17)));
            float neck = 0.1 * sqrt(oval(p, vec2(0.49, 0.74), vec2(0.1, 0.07)));
            float body = 0.16 * sqrt(oval(p, vec2(0.5, 1.05), vec2(0.55, 0.36)));
            return max(max(max(head, ears), hair), max(neck, body));
        }

        // Jaw: below the lips, down to the chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.69, 0.73, p.y));
            float across = 1.0 - smoothstep(0.1, 0.15, abs(p.x - 0.497));
            return below * across;
        }

        """);

    // Circuit: an android woman (a photo cut out of its white background): eyes at (0.414, 0.398) and (0.584, 0.399), nose
    // tip at (0.498, 0.51), lips slightly apart along y 0.59 (x 0.439 to 0.557), chin at 0.69, face from x 0.32 to 0.68,
    // black hair parted in the middle from y 0.05, transparent circuit panels on her temples, circuit collar from 0.65 and
    // shoulders below, faded out above the bottom of the original (0.82). Same life as JEROMELIVE, and the green
    // circuits glow with the voice.
    public static readonly FaceModel CircuitFace = new("avares://KAONOMI.Chat/Assets/circuit-face.png", """
        #define LIVE
        #define CIRCUIT
        const vec2 EYE_LEFT = vec2(0.414, 0.398);
        const vec2 EYE_RIGHT = vec2(0.584, 0.399);
        const vec2 EYE_SIZE = vec2(0.058, 0.03);    // half size of the eyelid, past the corners and the lashes
        const float MOUTH_X = 0.499;
        const float MOUTH_HALF = 0.052;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.03;
        const vec3 MOUTH_DARK = vec3(0.12, 0.03, 0.04);
        const vec3 MOUTH_LIGHT = vec3(0.6, 0.28, 0.3);   // the tongue

        """, """
        float mouthLine(float x)
        {
            float u = x - 0.499;
            return 0.59 - 0.3 * u * u;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.497);
            float head = 0.3 * sqrt(oval(p, vec2(0.497, 0.43), vec2(0.18, 0.27)));
            head += 0.06 * bump(p, vec2(0.498, 0.51), vec2(0.022, 0.026));              // nose tip
            head += 0.025 * bump(p, vec2(0.498, 0.45), vec2(0.015, 0.05));              // nose bridge
            head += 0.03 * bump(vec2(ax, p.y), vec2(0.11, 0.51), vec2(0.05, 0.045));    // cheeks
            head -= 0.02 * bump(vec2(ax, p.y), vec2(0.085, 0.398), vec2(0.04, 0.022));  // eye sockets
            head += 0.02 * bump(p, vec2(0.499, 0.6), vec2(0.035, 0.015));               // lips
            head += 0.025 * bump(p, vec2(0.499, 0.67), vec2(0.06, 0.025));              // chin
            float hair = 0.24 * sqrt(oval(p, vec2(0.497, 0.3), vec2(0.21, 0.25)));
            float neck = 0.1 * sqrt(oval(p, vec2(0.497, 0.73), vec2(0.12, 0.08)));
            float body = 0.14 * sqrt(oval(p, vec2(0.5, 1.0), vec2(0.5, 0.26)));
            return max(max(head, hair), max(neck, body));
        }

        // Jaw: below the lips, down to the chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.67, 0.71, p.y));
            float across = 1.0 - smoothstep(0.1, 0.15, abs(p.x - 0.499));
            return below * across;
        }

        """);

    // Motuko: an android woman (a photo cut out of its white background): eyes at (0.425, 0.402) and (0.574, 0.399), nose
    // tip at (0.499, 0.5), closed lips along y 0.562 (x 0.447 to 0.556), chin at 0.642, face from x 0.33 to 0.66, dark hair
    // from y 0.08 down to the jaw on both sides, high white collar from 0.61, white armour shoulders and chest below, cut
    // by the bottom and the sides of the frame. Same life as JEROMELIVE, and the amber lights of the armour glow.
    public static readonly FaceModel MotukoFace = new("avares://KAONOMI.Chat/Assets/motuko-face.png", """
        #define LIVE
        #define MOTUKO
        const vec2 EYE_LEFT = vec2(0.425, 0.402);
        const vec2 EYE_RIGHT = vec2(0.574, 0.399);
        const vec2 EYE_SIZE = vec2(0.051, 0.024);   // half size of the eyelid, past the corners and the lashes
        const float MOUTH_X = 0.502;
        const float MOUTH_HALF = 0.05;
        const float MOUTH_GAP = 0.009;
        const float JAW_DROP = 0.028;
        const vec3 MOUTH_DARK = vec3(0.14, 0.04, 0.05);
        const vec3 MOUTH_LIGHT = vec3(0.62, 0.3, 0.32);   // the tongue

        """, """
        float mouthLine(float x)
        {
            float u = x - 0.502;
            return 0.563 - 0.3 * u * u;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.5);
            float head = 0.3 * sqrt(oval(p, vec2(0.5, 0.42), vec2(0.17, 0.27)));
            head += 0.06 * bump(p, vec2(0.499, 0.5), vec2(0.02, 0.024));                // nose tip
            head += 0.025 * bump(p, vec2(0.499, 0.445), vec2(0.014, 0.05));             // nose bridge
            head += 0.03 * bump(vec2(ax, p.y), vec2(0.1, 0.5), vec2(0.045, 0.04));      // cheeks
            head -= 0.02 * bump(vec2(ax, p.y), vec2(0.075, 0.4), vec2(0.035, 0.02));    // eye sockets
            head += 0.02 * bump(p, vec2(0.502, 0.57), vec2(0.03, 0.012));               // lips
            head += 0.025 * bump(p, vec2(0.502, 0.625), vec2(0.06, 0.025));             // chin
            float hair = 0.25 * sqrt(oval(p, vec2(0.5, 0.32), vec2(0.23, 0.25)));
            float neck = 0.1 * sqrt(oval(p, vec2(0.5, 0.68), vec2(0.09, 0.07)));
            float collar = 0.14 * sqrt(oval(p, vec2(0.5, 0.72), vec2(0.17, 0.12)));
            float body = 0.17 * sqrt(oval(p, vec2(0.5, 1.05), vec2(0.6, 0.36)));
            float shoulders = 0.08 * bump(vec2(ax, p.y), vec2(0.42, 0.9), vec2(0.1, 0.12));
            return max(max(max(head, hair), max(neck, collar)), body + shoulders);
        }

        // Jaw: below the lips, down to the chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.62, 0.66, p.y));
            float across = 1.0 - smoothstep(0.09, 0.14, abs(p.x - 0.502));
            return below * across;
        }

        """);

    // Kaonomi robot (a photo cut out of a rainy street): egg-shaped head centred at (0.506, 0.358) (x 0.386 to 0.626, y
    // 0.205 to 0.51) in a hood (x 0.22 to 0.78, from y 0.13), its single yellow eye at (0.555, 0.33) in a ring of radius
    // 0.054, cables at the neck, a wide chest plate from y 0.5, arms on the sides, legs cut by the bottom of the frame.
    // No mouth: the eye speaks.
    public static readonly FaceModel KaonomiRobotFace = new("avares://KAONOMI.Chat/Assets/kaonomi-robot-face.png", """
        #define KROBOT
        const vec2 EYE = vec2(0.555, 0.33);
        const float EYE_RING = 0.054;
        const float MOUTH_X = 0.5;
        const float MOUTH_HALF = 0.0;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.0;
        const vec3 MOUTH_DARK = vec3(0.0);
        const vec3 MOUTH_LIGHT = vec3(0.0);

        """, """
        float mouthLine(float x)
        {
            return 0.6;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.5);
            // The egg-shaped head stands out of its hood; the eye ring is set into it.
            float head = 0.38 * sqrt(oval(p, vec2(0.506, 0.358), vec2(0.12, 0.155)));
            head += 0.025 * bump(p, EYE, vec2(EYE_RING, EYE_RING));
            float hood = 0.26 * sqrt(oval(p, vec2(0.5, 0.42), vec2(0.29, 0.3)));
            float chest = 0.24 * sqrt(oval(p, vec2(0.5, 0.78), vec2(0.3, 0.26)));
            float arms = 0.16 * bump(vec2(ax, p.y), vec2(0.34, 0.7), vec2(0.07, 0.22));
            return max(max(head, hood), max(chest, arms));
        }

        // No jaw: the robot has no mouth.
        float jaw(vec2 p)
        {
            return 0.0;
        }

        """);

    // Lin: a woman (a photo cut out of a hospital room), her head a little tilted: eyes at (0.425, 0.407) and (0.576,
    // 0.391), nose tip at (0.506, 0.494), closed lips along y 0.567 at their centre (x 0.458 to 0.569, a little higher on
    // the right), chin at 0.669, face from x 0.33 to 0.67, ears at x 0.31 and 0.7, dark hair tied back from y 0.14 with a
    // bun on her left, neck and green scrubs below, faded out above the bottom of the original. Same life as JEROMELIVE.
    public static readonly FaceModel LinFace = new("avares://KAONOMI.Chat/Assets/lin-face.png", """
        #define LIVE
        const vec2 EYE_LEFT = vec2(0.425, 0.407);
        const vec2 EYE_RIGHT = vec2(0.576, 0.391);
        const vec2 EYE_SIZE = vec2(0.051, 0.024);   // half size of the eyelid, past the corners and the lashes
        const float MOUTH_X = 0.513;
        const float MOUTH_HALF = 0.05;
        const float MOUTH_GAP = 0.009;
        const float JAW_DROP = 0.028;
        const vec3 MOUTH_DARK = vec3(0.14, 0.04, 0.05);
        const vec3 MOUTH_LIGHT = vec3(0.6, 0.3, 0.3);   // the tongue

        """, """
        float mouthLine(float x)
        {
            float u = x - 0.513;
            return 0.567 - 0.09 * u - 0.3 * u * u;
        }

        float relief(vec2 p)
        {
            float ax = abs(p.x - 0.502);
            float head = 0.3 * sqrt(oval(p, vec2(0.502, 0.44), vec2(0.17, 0.26)));
            head += 0.06 * bump(p, vec2(0.506, 0.494), vec2(0.022, 0.026));             // nose tip
            head += 0.025 * bump(p, vec2(0.503, 0.44), vec2(0.015, 0.05));              // nose bridge
            head += 0.03 * bump(vec2(ax, p.y), vec2(0.1, 0.5), vec2(0.045, 0.04));      // cheeks
            head -= 0.02 * bump(vec2(ax, p.y), vec2(0.076, 0.4), vec2(0.035, 0.02));    // eye sockets
            head += 0.02 * bump(p, vec2(0.513, 0.575), vec2(0.03, 0.012));              // lips
            head += 0.025 * bump(p, vec2(0.513, 0.645), vec2(0.06, 0.025));             // chin
            float ears = 0.1 * bump(vec2(ax, p.y), vec2(0.195, 0.45), vec2(0.02, 0.045));
            float hair = 0.24 * sqrt(oval(p, vec2(0.502, 0.33), vec2(0.2, 0.2)));
            float bun = 0.12 * bump(p, vec2(0.38, 0.62), vec2(0.04, 0.05));
            float neck = 0.1 * sqrt(oval(p, vec2(0.52, 0.71), vec2(0.1, 0.07)));
            float body = 0.16 * sqrt(oval(p, vec2(0.52, 1.0), vec2(0.46, 0.3)));
            return max(max(max(head, ears), max(hair, bun)), max(neck, body));
        }

        // Jaw: below the lips, down to the chin, drops with the opening of the mouth.
        float jaw(vec2 p)
        {
            float below = smoothstep(mouthLine(p.x), mouthLine(p.x) + MOUTH_GAP, p.y) * (1.0 - smoothstep(0.65, 0.69, p.y));
            float across = 1.0 - smoothstep(0.1, 0.15, abs(p.x - 0.51));
            return below * across;
        }

        """);

    // Marvin: round head centred at (0.5, 0.37) (x 0.197 to 0.808, y 0.07 to 0.68), visor line across it at y 0.43, green
    // eye slits under it at (0.289, 0.43) and (0.637, 0.449), black neck at y 0.66, white shoulders and chest below,
    // cut by the bottom of the frame. No mouth: the eyes and the visor speak.
    public static readonly FaceModel MarvinFace = new("avares://KAONOMI.Chat/Assets/marvin-face.png", """
        #define MARVIN
        const vec2 EYE_LEFT = vec2(0.289, 0.43);
        const vec2 EYE_RIGHT = vec2(0.637, 0.449);
        const float MOUTH_X = 0.5;
        const float MOUTH_HALF = 0.0;
        const float MOUTH_GAP = 0.01;
        const float JAW_DROP = 0.0;
        const vec3 MOUTH_DARK = vec3(0.0);
        const vec3 MOUTH_LIGHT = vec3(0.0);

        """, """
        float mouthLine(float x)
        {
            return 0.6;
        }

        float relief(vec2 p)
        {
            // The head is a sphere; the eye slits are set a little into it.
            float head = 0.45 * sqrt(oval(p, vec2(0.502, 0.375), vec2(0.306, 0.306)));
            head -= 0.012 * bump(p, EYE_LEFT, vec2(0.035, 0.022));
            head -= 0.012 * bump(p, EYE_RIGHT, vec2(0.035, 0.022));
            float neck = 0.08 * sqrt(oval(p, vec2(0.5, 0.68), vec2(0.12, 0.05)));
            float body = 0.2 * sqrt(oval(p, vec2(0.5, 0.98), vec2(0.36, 0.32)));
            return max(head, max(neck, body));
        }

        // No jaw: Marvin has no mouth.
        float jaw(vec2 p)
        {
            return 0.0;
        }

        """);

    // Helpers of the shapes of the faces.
    public const string ShapeFunctions = """
        float bump(vec2 p, vec2 center, vec2 size)
        {
            vec2 d = (p - center) / size;
            return exp(-dot(d, d));
        }

        float oval(vec2 p, vec2 center, vec2 radius)
        {
            vec2 d = (p - center) / radius;
            return max(0.0, 1.0 - dot(d, d));
        }

        """;

    public const string VertexShader = """
        in vec2 aUv;
        uniform mat4 uMvp;
        uniform float uOpen;
        out vec2 vUv;
        out float vLight;

        void main()
        {
            vUv = aUv;
            float z = relief(aUv);
            float e = 0.004;
            vec3 normal = normalize(vec3(-(relief(aUv + vec2(e, 0.0)) - z) / e * 0.5, (relief(aUv + vec2(0.0, e)) - z) / e * 0.5, 1.0));
            vLight = 0.7 + 0.3 * dot(normal, normalize(vec3(-0.4, 0.4, 0.8)));

            vec2 position = vec2(aUv.x * 2.0 - 1.0, 1.0 - aUv.y * 2.0);
            position.y -= uOpen * JAW_DROP * jaw(aUv);
            gl_Position = uMvp * vec4(position, z, 1.0);
        }
        """;

    public const string FragmentShader = """
        in vec2 vUv;
        in float vLight;
        uniform sampler2D uFace;
        uniform float uTime;
        uniform float uOpen;
        uniform vec3 uGlitch;
        out vec4 fragColor;

        void main()
        {
            vec2 uv = vUv;
        #if defined(HOLOGRAM) || defined(KILLER)
            // Glitch: a thin band of the image slides sideways.
            if (abs(uv.y - uGlitch.x) < 0.012)
                uv.x += uGlitch.y;
        #endif
        #ifdef MUSUKO
            // A blink every few seconds: the eyelids come down over the eyes (the skin just under each eye, darker,
            // with the line of the lashes at their edge).
            float blinkPhase = fract(uTime / 4.3);
            float blink = blinkPhase < 0.045 ? sin(blinkPhase / 0.045 * 3.14159) : 0.0;
            vec2 eye = uv.x < 0.483 ? EYE_LEFT : EYE_RIGHT;
            vec2 e = (uv - eye) / vec2(0.05, 0.024);
            float lid = -1.0 + 2.0 * blink;   // edge of the eyelid, from above the eye (-1) to below it (1)
            // Share of the eyelid over this point: inside the eye, above the edge of the lid (soft edges).
            float covered = blink > 0.0 ? (1.0 - smoothstep(0.75, 1.05, dot(e, e))) * (1.0 - smoothstep(lid - 0.12, lid, e.y)) : 0.0;
        #endif
        #ifdef KILLER
            // The spiral pupils spin, faster while she speaks.
            float turn = uTime * 2.2 + uOpen * 3.0;
            uv = spin(spin(uv, EYE_LEFT, turn), EYE_RIGHT, turn);
        #endif
        #ifdef GREEN
            // The pupils dilate with the voice and breathe slowly when it is quiet (the inside of each eye is magnified
            // around its centre).
            vec2 eye = uv.x < 0.505 ? EYE_LEFT : EYE_RIGHT;
            vec2 fromEye = uv - eye;
            float inEye = 1.0 - smoothstep(0.05, 0.1, length(fromEye / vec2(1.0, 0.55)));
            uv = eye + fromEye * (1.0 - (0.12 * uOpen + 0.03 * sin(uTime * 1.5)) * inEye);
            // A blink every few seconds: the eyelids come down over the eyes.
            float blinkPhase = fract(uTime / 5.1);
            float blink = blinkPhase < 0.05 ? sin(blinkPhase / 0.05 * 3.14159) : 0.0;
            vec2 e = (vUv - eye) / vec2(0.11, 0.05);
            float lid = -1.0 + 2.0 * blink;   // edge of the eyelid, from above the eye (-1) to below it (1)
            float covered = blink > 0.0 ? (1.0 - smoothstep(0.75, 1.05, dot(e, e))) * (1.0 - smoothstep(lid - 0.12, lid, e.y)) : 0.0;
        #endif
        #ifdef HAL
            // The iris dilates with the voice and breathes slowly when it is quiet.
            vec2 fromEye = uv - EYE;
            float dilate = (0.10 * uOpen + 0.02 * sin(uTime * 1.3)) * (1.0 - smoothstep(0.12, 0.22, length(fromEye)));
            uv = EYE + fromEye * (1.0 - dilate);
        #endif
            vec4 color = texture(uFace, uv);   // premultiplied alpha
        #ifdef LIVE
            // A blink every few seconds: the eyelids come down over the eyes. An eyelid (EYE_SIZE, a little larger than
            // the eye, so that its corners and lashes are covered too) takes the colour of the skin above and below the
            // eye, its edge soft; when closed, a thin soft line of lashes lies across the lower half of the eye.
            float blinkPhase = fract(uTime / 4.7);
            float blink = blinkPhase < 0.05 ? sin(blinkPhase / 0.05 * 3.14159) : 0.0;
            vec2 eye = uv.x < 0.5 ? EYE_LEFT : EYE_RIGHT;
            vec2 e = (uv - eye) / EYE_SIZE;
            float lid = -1.1 + 1.7 * blink;   // edge of the eyelid, from above the eye (-1.1) to its lower half (0.6)
            float eyeArea = 1.0 - smoothstep(0.4, 1.0, dot(e, e));
            float covered = blink > 0.0 ? eyeArea * (1.0 - smoothstep(lid - 0.3, lid, e.y)) : 0.0;
            // Sampled outside any branch: inside one, the edge of the eyelid would pick a wrong mipmap (a dotted outline).
            // The skin beyond the crease above the eye, and below the shadow under it.
            vec4 above = texture(uFace, vec2(uv.x, eye.y - EYE_SIZE.y * 2.1));
            vec4 below = texture(uFace, vec2(uv.x, eye.y + EYE_SIZE.y * 2.0));
            vec4 skin = mix(above, below, 0.5);
            float lash = smoothstep(0.22, 0.0, abs(e.y - lid)) * smoothstep(0.3, 1.0, blink) * (1.0 - smoothstep(0.55, 0.95, abs(e.x)));
            // The eyelid is rounded: a little lighter at its middle, a little darker towards its edge and the lashes.
            float shade = 0.97 + 0.05 * (1.0 - clamp(dot(e, e), 0.0, 1.0)) - 0.05 * smoothstep(-0.2, lid, e.y);
            vec3 lid3 = mix(skin.rgb * shade, vec3(0.16, 0.11, 0.1) * skin.a, 0.55 * lash);
            color = mix(color, vec4(lid3, skin.a), covered);
        #endif

            // Open mouth: the thin band just below the line of the mouth is stretched when the jaw drops; it shows
            // the inside of the mouth (dark, the tongue lighter at the bottom), shaped like a lens.
            float depth = (uv.y - mouthLine(uv.x)) / MOUTH_GAP;
            float lens = 2.0 * depth - 1.0;
            float halfWidth = MOUTH_HALF * sqrt(max(0.0, 1.0 - lens * lens));
            float inside = smoothstep(0.0, 0.08, depth) * (1.0 - smoothstep(0.92, 1.0, depth))
                           * (1.0 - smoothstep(halfWidth - 0.012, halfWidth, abs(uv.x - MOUTH_X)))
                           * smoothstep(0.02, 0.15, uOpen);
            vec3 mouth = mix(MOUTH_DARK, MOUTH_LIGHT, smoothstep(0.45, 0.95, depth));
            color.rgb = mix(color.rgb, mouth * color.a, inside);

        #if defined(PORTRAIT) || defined(LIVE)
            // Softened light from the relief (the photo has its own); the shoulders, cut straight by the frame at the
            // bottom and on the sides, fade out instead.
            color.rgb *= mix(1.0, vLight, 0.6);
            color *= (1.0 - smoothstep(0.9, 1.0, vUv.y)) * smoothstep(0.0, 0.08, vUv.x) * (1.0 - smoothstep(0.92, 1.0, vUv.x));
        #ifdef CIRCUIT
            // The green circuits glow with the voice (softly breathing when she is quiet), and a light runs down them.
            float greenness = clamp((color.g - max(color.r, color.b)) * 5.0, 0.0, 1.0);
            float level = 0.12 + 0.1 * (0.5 + 0.5 * sin(uTime * 1.6)) + 0.9 * uOpen;
            float runY = (vUv.y - fract(uTime * 0.3) * 1.3 + 0.15) / 0.04;
            float run = exp(-runY * runY);
            color.rgb += vec3(0.3, 1.0, 0.5) * greenness * (level + 0.8 * run) * color.a;
        #endif
        #ifdef MOTUKO
            // The amber lights of the armour (under the chin only, away from the lips) glow with the voice.
            float amber = clamp((color.r - color.b - 0.3) * 4.0, 0.0, 1.0) * step(0.66, vUv.y);
            float glow = 0.15 + 0.1 * (0.5 + 0.5 * sin(uTime * 2.0)) + 1.2 * uOpen;
            color.rgb += vec3(1.0, 0.6, 0.15) * amber * glow * color.a;
        #endif
        #elif defined(PEPPER)
            // Softened light from the relief; the body, cut by the frame, fades out at the bottom and on the right.
            color.rgb *= mix(1.0, vLight, 0.5);
            color *= (1.0 - smoothstep(0.92, 1.0, vUv.y)) * (1.0 - smoothstep(0.93, 1.0, vUv.x));
            float breath = 0.5 + 0.5 * sin(uTime * 1.4);
            float level = 0.12 + 0.12 * breath + 0.9 * uOpen;
            // The rings around the eye lenses and the ear speaker light up in blue while it speaks.
            float rings = 0.0;
            float rl = length(vUv - EYE_LEFT), rr = length(vUv - EYE_RIGHT);
            float dl = (rl - 0.062) / 0.012, dr = (rr - 0.064) / 0.012;
            rings += exp(-dl * dl) + exp(-dr * dr);
            vec2 e = (vUv - EAR) / vec2(0.035, 0.085);
            float de = (length(e) - 0.8) / 0.25;
            float ear = exp(-de * de);
            vec3 blue = vec3(0.25, 0.65, 1.0);
            color.rgb += blue * (rings + 0.8 * ear) * level * color.a;
            // The tablet screen (its dark glass) glows softly, with a voice wave running across it.
            float glass = (1.0 - smoothstep(0.25, 0.45, max(color.r, max(color.g, color.b))))
                          * step(0.1, vUv.x) * step(vUv.x, 0.68) * step(0.86, vUv.y);
            float wave = 0.93 + 0.025 * uOpen * sin(vUv.x * 70.0 - uTime * 9.0) * sin(vUv.x * 13.0 + uTime * 2.0);
            float dw = (vUv.y - wave) / 0.004;
            float line = exp(-dw * dw);
            color.rgb += glass * color.a * (vec3(0.1, 0.25, 0.4) * (0.4 + 0.6 * level) + blue * line * (0.3 + 0.9 * uOpen));
        #elif defined(KAONAMI)
            // Softened light from the relief; the shoulders, cut by the frame, fade out at the bottom and on the sides.
            color.rgb *= mix(1.0, vLight, 0.55);
            color *= (1.0 - smoothstep(0.9, 1.0, vUv.y)) * smoothstep(0.02, 0.1, vUv.x) * (1.0 - smoothstep(0.9, 0.98, vUv.x));
            // An amber light in the dark of the eye holes and of the mouth slot: faint when quiet, bright while speaking.
            float dark = 1.0 - smoothstep(0.08, 0.3, max(color.r, max(color.g, color.b)));
            float flicker = 0.85 + 0.15 * sin(uTime * 23.0) * sin(uTime * 7.0);
            float holes = exp(-length(vUv - EYE_LEFT) / 0.016) + exp(-length(vUv - EYE_RIGHT) / 0.016);
            vec2 s = (vUv - SLOT) / vec2(0.06, 0.012);
            float slot = exp(-dot(s, s));
            float shine = (holes + slot) * dark * (0.12 + 1.1 * uOpen * flicker);
            color.rgb += vec3(1.0, 0.55, 0.15) * shine * color.a;
        #elif defined(GREEN)
            if (covered > 0.0)
            {
                // Skin of the eyelid: the pale skin above the eye, darker at the line of the lashes.
                vec4 skin = texture(uFace, vec2(vUv.x, eye.y - 0.075));
                float lash = smoothstep(0.22, 0.0, abs(e.y - lid)) * (1.0 - smoothstep(0.7, 1.0, abs(e.x)));
                vec4 eyelid = vec4(mix(skin.rgb * 0.9, vec3(0.02, 0.03, 0.03) * skin.a, lash), skin.a);
                color = mix(color, eyelid, covered);
            }
            // Softened light from the relief; the head, cut by the top of the frame, and the shoulders, cut at the bottom,
            // fade out.
            color.rgb *= mix(1.0, vLight, 0.5);
            color *= smoothstep(0.083, 0.14, vUv.y) * (1.0 - smoothstep(0.86, 0.92, vUv.y));
            // The green of the eyes, the ears and the shoulders glows brighter with the voice and breathes when quiet.
            float breath = 0.5 + 0.5 * sin(uTime * 1.4);
            float greenness = smoothstep(0.08, 0.3, color.g - max(color.r, color.b));
            float eyes = (1.0 - covered) * (exp(-length((vUv - EYE_LEFT) / vec2(1.0, 0.55)) / 0.05)
                                            + exp(-length((vUv - EYE_RIGHT) / vec2(1.0, 0.55)) / 0.05));
            vec3 green = vec3(0.25, 1.0, 0.4);
            color.rgb += green * greenness * (0.08 + 0.12 * breath + 0.7 * uOpen) * color.a;
            vec3 glow = green * eyes * (0.12 + 0.08 * breath + 0.8 * uOpen);
            color.rgb += glow;
            color.a = clamp(max(color.a, glow.g * 0.7), 0.0, 1.0);
            // A green scan line runs down the face now and then.
            float scan = smoothstep(0.012, 0.0, abs(fract(uTime * 0.09) * 1.6 - 0.3 - vUv.y));
            color.rgb += green * scan * 0.18 * color.a;
        #elif defined(KROBOT)
            // Softened light from the relief; the legs, cut by the frame, fade out at the bottom.
            color.rgb *= mix(1.0, vLight, 0.5);
            color *= 1.0 - smoothstep(0.9, 1.0, vUv.y);
            // The yellow eye glows with the voice, and breathes slowly when the robot is quiet; its glow spreads around.
            float breath = 0.5 + 0.5 * sin(uTime * 1.1);
            float level = 0.12 + 0.12 * breath + 1.1 * uOpen;
            vec3 amber = vec3(1.0, 0.72, 0.18);
            vec2 d = vUv - EYE;
            float r = length(d);
            float core = exp(-r * r / (0.012 * 0.012));
            float halo = exp(-r / 0.03);
            // While it speaks, an arc of light turns along the ring around the eye.
            float angle = atan(d.y, d.x);
            float arc = pow(0.5 + 0.5 * cos(angle - uTime * 4.0), 6.0);
            float ring = exp(-pow((r - EYE_RING * 0.82) / 0.005, 2.0)) * arc * uOpen;
            vec3 glow = amber * (core * 0.9 + halo * 0.5) * level + amber * ring * 1.2;
            color.rgb += glow;
            color.a = clamp(max(color.a, glow.g * 0.7), 0.0, 1.0);
        #elif defined(MARVIN)
            // Softened light from the relief; the body, cut by the frame, fades out at the bottom.
            color.rgb *= mix(1.0, vLight, 0.5);
            color *= 1.0 - smoothstep(0.9, 1.0, vUv.y);
            // The eyes go dark for a moment every few seconds (a weary blink).
            float blinkPhase = fract(uTime / 6.3);
            float awake = blinkPhase < 0.06 ? 1.0 - sin(blinkPhase / 0.06 * 3.14159) : 1.0;
            float green = smoothstep(0.08, 0.3, color.g - max(color.r, color.b));
            color.rgb = mix(color.rgb, color.rgb * vec3(0.25, 0.3, 0.25), green * (1.0 - awake));
            // The eyes glow with the voice, and breathe slowly when he is quiet; the glow spreads beyond the slits.
            float breath = 0.5 + 0.5 * sin(uTime * 0.9);
            float level = awake * (0.1 + 0.12 * breath + 1.0 * uOpen);
            vec3 eyeGreen = vec3(0.35, 1.0, 0.3);
            vec2 dl = (vUv - EYE_LEFT) / vec2(1.0, 0.6), dr = (vUv - EYE_RIGHT) / vec2(1.0, 0.6);
            float halo = exp(-length(dl) / 0.035) + exp(-length(dr) / 0.035);
            color.rgb += eyeGreen * green * level * color.a;
            vec3 glow = eyeGreen * halo * level * 0.6;
            // While he speaks, a green light runs along the visor line, from one side of the head to the other.
            float visor = 0.418 + (vUv.x - 0.289) * 0.055;
            float onLine = exp(-pow((vUv.y - visor) / 0.006, 2.0)) * (1.0 - smoothstep(0.27, 0.3, abs(vUv.x - 0.502)));
            float sweep = exp(-pow((vUv.x - (0.2 + 0.6 * fract(uTime * 0.6))) / 0.06, 2.0));
            glow += eyeGreen * onLine * (0.15 + sweep) * uOpen * 0.8;
            color.rgb += glow;
            color.a = clamp(max(color.a, glow.g * 0.7), 0.0, 1.0);
        #elif defined(MUSUKO)
            if (covered > 0.0)
            {
                // Skin of the eyelid: the average of the skin above and below the eye, with the lashes at its edge.
                vec4 skin = 0.5 * (texture(uFace, vec2(uv.x, eye.y - 0.036)) + texture(uFace, vec2(uv.x, eye.y + 0.05)));
                float lash = smoothstep(0.22, 0.0, abs(e.y - lid)) * (1.0 - smoothstep(0.7, 1.0, abs(e.x)));
                vec4 eyelid = vec4(mix(skin.rgb * 0.92, vec3(0.03, 0.04, 0.07) * skin.a, lash), skin.a);
                color = mix(color, eyelid, covered);
            }
            // Softened light from the relief; the suit, cut by the frame at the bottom and on the sides, fades out.
            color.rgb *= mix(1.0, vLight, 0.5);
            color *= (1.0 - smoothstep(0.9, 1.0, vUv.y)) * smoothstep(0.08, 0.16, vUv.x) * (1.0 - smoothstep(0.84, 0.92, vUv.x));
            // The green irises and the lights of the suit glow with the voice, and breathe slowly when he is quiet.
            float breath = 0.5 + 0.5 * sin(uTime * 1.6);
            float irises = (1.0 - covered) * light(vUv, EYE_LEFT, 0.012) + light(vUv, EYE_RIGHT, 0.012);
            color.rgb += vec3(0.25, 1.0, 0.35) * irises * (0.1 + 0.6 * uOpen) * color.a;
            float lights = light(vUv, vec2(0.5, 0.762), 0.03) + light(vUv, vec2(0.51, 0.985), 0.04)
                           + 0.7 * (light(vUv, vec2(0.24, 0.86), 0.018) + light(vUv, vec2(0.31, 0.835), 0.018)
                                    + light(vUv, vec2(0.695, 0.82), 0.018) + light(vUv, vec2(0.755, 0.835), 0.018));
            vec3 glow = vec3(0.3, 0.95, 1.0) * lights * (0.15 + 0.2 * breath + 0.9 * uOpen);
            color.rgb += glow;
            color.a = clamp(max(color.a, max(glow.g, glow.b) * 0.8), 0.0, 1.0);
        #elif defined(KILLER)
            // Softened light from the relief; the body, cut by the frame at the bottom, fades out.
            color.rgb *= mix(1.0, vLight, 0.55);
            color *= 1.0 - smoothstep(0.92, 1.0, vUv.y);
            // A glint runs along the blade every few seconds (on the grey steel only, not on the blood).
            float steel = 1.0 - smoothstep(0.08, 0.2, max(color.r, max(color.g, color.b)) - min(color.r, min(color.g, color.b)));
            vec2 axis = normalize(BLADE_HEEL - BLADE_TIP);
            float along = dot(vUv - BLADE_TIP, axis) / length(BLADE_HEEL - BLADE_TIP);
            float sweep = fract(uTime * 0.22) * 2.2 - 0.6;
            float g = (along - sweep) / 0.05;
            float glint = exp(-g * g) * (1.0 - smoothstep(0.03, 0.05, bladeDistance(vUv)));
            color.rgb += vec3(1.0) * glint * steel * 0.8 * color.a;
            // The hearts of the chest glow pink with the voice, and beat slowly when she is quiet.
            float beat = pow(0.5 + 0.5 * sin(uTime * 2.6), 4.0);
            float hearts = exp(-length(vUv - vec2(0.5, 0.835)) / 0.018) + exp(-length(vUv - vec2(0.5, 0.935)) / 0.03);
            color.rgb += vec3(1.0, 0.35, 0.75) * hearts * (0.15 + 0.25 * beat + 0.9 * uOpen);
            // During a glitch the picture splits into red and cyan.
            float glitching = step(uGlitch.x, 90.0);
            color.r += glitching * 0.25 * texture(uFace, uv + vec2(0.006, 0.0)).a;
            color.gb *= 1.0 - glitching * 0.15;
        #elif defined(HAL)
            // Cut out in a circle around the housing, lit from the relief; the red core, its halo and the horizontal
            // flare glow brighter with the voice (added light, also beyond the circle).
            float r = length(vUv - EYE);
            color *= mix(1.0, vLight, 0.5) * (1.0 - smoothstep(0.46, 0.5, r));
            float breath = 0.5 + 0.5 * sin(uTime * 1.3);
            float core = exp(-r * r / 0.0012) * (0.35 + 0.9 * uOpen + 0.15 * breath);
            float halo = exp(-r / 0.07) * (0.12 + 0.5 * uOpen + 0.05 * breath);
            float flare = exp(-abs(vUv.y - EYE.y) * 160.0) * exp(-abs(vUv.x - EYE.x) * 4.0) * (0.25 + 0.75 * uOpen);
            vec3 glow = vec3(1.0, 0.18, 0.08) * (halo + flare) + vec3(1.0, 0.75, 0.55) * core;
            color.rgb += glow;
            color.a = clamp(max(color.a, max(glow.r, max(glow.g, glow.b))), 0.0, 1.0);
        #else
            // Light from the relief, faint scan lines and a slow scan glow running down.
            float scanLines = 0.94 + 0.06 * sin(gl_FragCoord.y * 2.2);
            float scan = 0.08 * smoothstep(0.06, 0.0, abs(fract(uTime * 0.12) * 1.4 - 0.2 - uv.y));
            color.rgb *= vLight * scanLines;
            color.rgb += scan * color.a * vec3(0.55, 0.85, 1.0);
        #endif
            fragColor = color;
        }
        """;
}

/// <summary>Shaders of <see cref="JarvisScene"/>: a full-band quad and the fragment shader that casts a ray per pixel.</summary>
public static class JarvisShaders
{
    public const string VertexShader = """
        in vec2 aPos;

        void main()
        {
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    // Camera on the z axis looking at the origin; the hologram is centred on the origin, the reticle in the plane z = 0
    // facing the camera, the gyroscope rings in tilted planes through the origin, the sphere at the origin. Sizes are
    // in scene units: with CAMERA / FOCAL = 2, a radius of 0.92 fills 0.46 of the band height.
    public const string FragmentShader = """
        uniform vec3 uFrame;      // width, height (pixels), time (seconds)
        uniform float uLevel;     // voice level, 0 to 1
        uniform float uLight;     // 1 when the band background is light
        uniform vec3 uGlitch;     // row (share of the height), sideways shift (pixels)
        out vec4 fragColor;

        const float PI = 3.14159265;
        const float TAU = 6.28318531;
        const float CAMERA = 3.4;
        const float FOCAL = 1.7;

        vec3 gColor;   // sum of the colors of the elements crossed by the ray
        float gGlow;   // sum of their intensities

        void add(vec3 color, float amount)
        {
            gColor += color * amount;
            gGlow += amount;
        }

        float hash(float n) { return fract(sin(n) * 43758.5453); }

        // 1 on a line at distance 0, fading to 0 at distance w.
        float lineAt(float d, float w) { return 1.0 - smoothstep(0.0, w, abs(d)); }

        // 1 between a and b, with soft edges of width w.
        float bandAt(float x, float a, float b, float w) { return smoothstep(a - w, a, x) * (1.0 - smoothstep(b, b + w, x)); }

        // n radial marks per turn (a = angle in turns), turned by spin: 1 on a mark.
        float marks(float a, float r, float n, float spin, float w)
        {
            float f = fract(a * n + spin);
            return lineAt(min(f, 1.0 - f) / n * TAU * r, w);
        }

        mat3 rotX(float a) { float c = cos(a); float s = sin(a); return mat3(1.0, 0.0, 0.0, 0.0, c, s, 0.0, -s, c); }
        mat3 rotY(float a) { float c = cos(a); float s = sin(a); return mat3(c, 0.0, -s, 0.0, 1.0, 0.0, s, 0.0, c); }
        mat3 rotZ(float a) { float c = cos(a); float s = sin(a); return mat3(c, s, 0.0, -s, c, 0.0, 0.0, 0.0, 1.0); }

        // Where the ray crosses the plane z = 0 of a layer turned by m (p in the layer, t along the ray).
        bool plane(vec3 ro, vec3 rd, mat3 m, out vec2 p, out float t)
        {
            vec3 o = ro * m;   // transpose(m) * ro: into the layer
            vec3 d = rd * m;
            p = vec2(0.0);
            t = 0.0;
            if (abs(d.z) < 0.0001)
                return false;
            t = -o.z / d.z;
            p = o.xy + t * d.xy;
            return t > 0.0;
        }

        // A gyroscope ring tilted by m: a thin ring, dashes and three particles orbiting on it.
        void gyroscope(vec3 ro, vec3 rd, mat3 m, float radius, float speed, vec3 color, float w)
        {
            vec2 p;
            float t;
            if (!plane(ro, rd, m, p, t))
                return;
            float fade = clamp(1.3 - 0.6 * t / CAMERA, 0.35, 1.0);   // the far side of the ring is dimmer
            float r = length(p);
            float a = atan(p.y, p.x) / TAU + 0.5;
            add(color, fade * 0.55 * lineAt(r - radius, w));
            add(color, fade * 0.3 * lineAt(r - radius - 0.035, w) * step(0.5, fract(a * 24.0 + uFrame.z * speed * 0.2)));
            for (int i = 0; i < 3; i++)
            {
                float angle = uFrame.z * speed + float(i) * TAU / 3.0;
                float d = length(p - radius * vec2(cos(angle), sin(angle)));
                add(color, fade * 0.0009 / (d * d + 0.0006));
            }
        }

        void main()
        {
            vec2 res = uFrame.xy;
            float time = uFrame.z;
            vec2 frag = gl_FragCoord.xy;
            if (abs(frag.y / res.y - uGlitch.x) < 0.012)
                frag.x += uGlitch.y;

            float unit = min(res.x, res.y);
            vec2 uv = (frag - 0.5 * res) / unit;
            float px = 1.0 / unit;
            float w = 1.6 * px * CAMERA / FOCAL;   // about 1.6 pixel in the plane of the origin
            float level = uLevel;

            vec3 cyan = mix(vec3(0.30, 0.82, 1.00), vec3(0.00, 0.42, 0.72), uLight);
            vec3 ice = mix(vec3(0.75, 0.95, 1.00), vec3(0.00, 0.30, 0.55), uLight);
            vec3 amber = mix(vec3(1.00, 0.62, 0.20), vec3(0.85, 0.40, 0.00), uLight);
            gColor = vec3(0.0);
            gGlow = 0.0;

            vec3 ro = vec3(0.0, 0.0, CAMERA);
            vec3 rd = normalize(vec3(uv, -FOCAL));
            // The whole hologram sways slightly, as if it floated.
            mat3 sway = rotY(0.14 * sin(time * 0.31)) * rotX(0.07 * sin(time * 0.23));

            // Faint dot grid behind the hologram.
            vec2 cell = fract(frag / (unit * 0.055)) - 0.5;
            add(cyan, 0.07 * lineAt(length(cell), 0.06));

            // Reticle facing the viewer.
            vec2 p;
            float t;
            if (plane(ro, rd, sway, p, t))
            {
                float r = length(p);
                float a = atan(p.y, p.x) / TAU + 0.5;   // angle in turns
                add(cyan, 0.5 * lineAt(r - 0.80, w));
                add(cyan, 0.45 * marks(a, r, 90.0, time * 0.01, w) * bandAt(r, 0.72, 0.76, w));
                add(cyan, 0.7 * marks(a, r, 12.0, time * 0.01, 1.5 * w) * bandAt(r, 0.69, 0.78, w));
                add(ice, 0.8 * marks(a, r, 4.0, 0.0, 1.5 * w) * bandAt(r, 0.84, 0.92, w));
                add(cyan, 0.4 * lineAt(r - 0.66, 1.4 * w) * step(0.35, fract(a * 36.0 - time * 0.08)));

                // Amber arcs: three of them, longer with the voice.
                float arc = fract(a * 3.0 + time * 0.05);
                add(amber, 0.85 * bandAt(r, 0.585, 0.615, w) * bandAt(arc, 0.0, 0.10 + 0.20 * level, 0.004));
                add(amber, 0.35 * lineAt(r - 0.63, w) * bandAt(fract(a * 3.0 - time * 0.03 + 0.5), 0.0, 0.25, 0.004));

                // Inner double ring, flickering blocks and a radar sweep.
                add(cyan, 0.5 * (lineAt(r - 0.48, w) + lineAt(r - 0.465, w)));
                float block = floor(a * 60.0);
                add(ice, bandAt(r, 0.385, 0.405, w) * step(0.3, fract(a * 60.0))
                         * (0.15 + 0.6 * level * step(0.4, hash(block + floor(time * 10.0)))));
                add(cyan, 0.35 * pow(fract(a - time * 0.25), 6.0) * bandAt(r, 0.30, 0.46, w));
            }

            // Two gyroscope rings tilted in 3D around the core.
            gyroscope(ro, rd, sway * rotY(time * 0.35) * rotX(1.25), 0.34, 1.1, ice, w);
            gyroscope(ro, rd, sway * rotZ(-0.6) * rotX(time * 0.27) * rotY(1.15), 0.30, -0.8, cyan, w);

            // Wireframe sphere around the core.
            float radius = 0.17 + 0.012 * level;
            float b = dot(ro, rd);
            float h = b * b - dot(ro, ro) + radius * radius;
            if (h > 0.0)
            {
                vec3 n = (ro + (-b - sqrt(h)) * rd) / radius;
                vec3 q = n * rotY(time * 0.6) * rotX(0.4);
                float lat = acos(clamp(q.y, -1.0, 1.0)) / PI;
                float lon = atan(q.z, q.x) / TAU + 0.5;
                float sw = 1.4 * px / radius;
                float grid = lineAt((fract(lat * 7.0 + 0.5) - 0.5) / 7.0 * PI, sw)
                             + lineAt((fract(lon * 12.0 + 0.5) - 0.5) / 12.0 * TAU * sin(lat * PI), sw);
                float rim = pow(1.0 - abs(dot(n, rd)), 3.0);
                add(cyan, 0.3 * min(grid, 1.0) + 0.5 * rim);
            }

            // Glowing core, pulsing with the voice.
            float d = length(cross(rd, -ro));
            float pulse = 0.5 + 0.9 * level + 0.06 * sin(time * 3.0);
            add(ice, 0.0028 * pulse / (d * d + 0.0012));

            // Voice spectrum on both sides, when the band is wide enough.
            float halfWidth = 0.5 * res.x / unit;
            float start = 0.56;
            float span = halfWidth - start - 0.06;
            float ax = abs(uv.x) - start;
            if (span > 0.15 && ax > 0.0 && ax < span)
            {
                float barStep = 0.022;
                float i = floor(ax / barStep);
                float side = uv.x < 0.0 ? 57.0 : 0.0;
                float wave = 0.5 + 0.5 * sin(i * 0.9 + time * 7.0 + hash(i + side) * TAU);
                float envelope = 1.0 - ax / span;
                float barHeight = 0.015 + (0.04 + 0.34 * level) * wave * envelope;
                float bar = step(fract(ax / barStep), 0.55) * (1.0 - smoothstep(barHeight, barHeight + px, abs(uv.y)));
                add(cyan, 0.5 * bar * (0.6 + 0.4 * envelope));
                add(cyan, 0.25 * lineAt(uv.y, px) * envelope);
                // Brackets above and below the spectrum.
                add(cyan, 0.3 * lineAt(abs(uv.y) - 0.42, px) * step(ax, 0.25));
            }

            // Scan lines.
            float scan = 0.9 + 0.1 * sin(frag.y * 2.0);
            gColor *= scan;
            gGlow *= scan;

            // Dark band: the hologram glows (added light); light band: deep colors are painted over it.
            vec3 glowColor = min(gColor, vec3(1.0));
            float glowAlpha = clamp(max(gColor.r, max(gColor.g, gColor.b)), 0.0, 1.0);
            float paintAlpha = clamp(gGlow, 0.0, 1.0);
            vec3 paintColor = gColor / max(gGlow, 0.0001) * paintAlpha;
            fragColor = mix(vec4(glowColor, glowAlpha), vec4(paintColor, paintAlpha), uLight);
        }
        """;
}
