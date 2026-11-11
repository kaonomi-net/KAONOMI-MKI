using System.Text.RegularExpressions;

namespace KAONOMI.UI.Themes;

/// <summary>
/// Translates a background shader of the Godot chat (a <c>canvas_item</c> <c>.gdshader</c>, read as it is from its
/// Shaders folder) into a GLSL fragment shader:
/// <list type="bullet">
/// <item>the uniforms with a value become constants (their hints, <c>: source_color</c>, are dropped), but
/// <c>energy</c>, which the desk sets while the answer is read aloud;</item>
/// <item>the built-ins of Godot become globals set before the code of the shader runs: <c>FRAGCOORD</c> and
/// <c>SCREEN_UV</c> from the top left corner (as in Godot; OpenGL counts from the bottom), <c>SCREEN_PIXEL_SIZE</c>,
/// <c>TIME</c>, <c>COLOR</c>;</item>
/// <item><c>fragment()</c> is called from <c>main()</c>, which writes <c>COLOR</c> premultiplied (it is drawn over the
/// picture of the theme when it has one).</item>
/// </list>
/// </summary>
public static partial class GodotShader
{
    /// <summary>The vertex shader of the quad over the whole control.</summary>
    public const string Vertex = """
        in vec2 aPos;
        out vec2 vUv;
        void main() {
            vUv = vec2(aPos.x * 0.5 + 0.5, 0.5 - aPos.y * 0.5);
            gl_Position = vec4(aPos, 0.0, 1.0);
        }
        """;

    /// <summary>The fragment shader of the picture under the shader: the picture covering the control, as in Godot (KeepAspectCovered).</summary>
    public const string Picture = """
        in vec2 vUv;
        out vec4 fragColor;
        uniform sampler2D uPicture;
        uniform vec3 uFrame;      // width, height of the control, ratio width / height of the picture
        void main() {
            float control = uFrame.x / uFrame.y;
            vec2 uv = vUv - 0.5;
            if (control > uFrame.z)
                uv.y *= uFrame.z / control;
            else
                uv.x *= control / uFrame.z;
            fragColor = texture(uPicture, uv + 0.5);
        }
        """;

    /// <summary>The GLSL of <paramref name="godot"/>, to compile after the header of the context.</summary>
    public static string Translate(string godot)
    {
        var code = LineComment().Replace(godot, string.Empty);
        code = ShaderType().Replace(code, string.Empty);
        code = RenderMode().Replace(code, string.Empty);
        code = Uniform().Replace(code, m =>
        {
            var (type, name, value) = (m.Groups["type"].Value, m.Groups["name"].Value, m.Groups["value"].Value.Trim());
            return name == "energy" || value.Length == 0 ? $"uniform {type} {name};" : $"const {type} {name} = {value};";
        });
        code = FragmentFunction().Replace(code, "void godot_fragment()");
        code = Builtin().Replace(code, m => m.Value switch
        {
            "FRAGCOORD" => "kFragCoord",
            "SCREEN_UV" => "kScreenUv",
            "SCREEN_PIXEL_SIZE" => "kPixelSize",
            "TIME" => "uTime",
            _ => "kColor"
        });
        return """
            out vec4 fragColor;
            uniform vec2 uSize;
            uniform float uTime;
            vec4 kFragCoord;
            vec2 kScreenUv;
            vec2 kPixelSize;
            vec4 kColor;

            """ + code + """

            void main() {
                kFragCoord = vec4(gl_FragCoord.x, uSize.y - gl_FragCoord.y, gl_FragCoord.z, gl_FragCoord.w);
                kPixelSize = 1.0 / uSize;
                kScreenUv = kFragCoord.xy * kPixelSize;
                kColor = vec4(0.0, 0.0, 0.0, 1.0);
                godot_fragment();
                fragColor = vec4(kColor.rgb * kColor.a, kColor.a);
            }
            """;
    }

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"shader_type\s+\w+\s*;")]
    private static partial Regex ShaderType();

    [GeneratedRegex(@"render_mode[^;]*;")]
    private static partial Regex RenderMode();

    [GeneratedRegex(@"uniform\s+(?<type>\w+)\s+(?<name>\w+)\s*(?::\s*[^=;]+)?(?:=\s*(?<value>[^;]+))?;")]
    private static partial Regex Uniform();

    [GeneratedRegex(@"void\s+fragment\s*\(\s*\)")]
    private static partial Regex FragmentFunction();

    [GeneratedRegex(@"\b(FRAGCOORD|SCREEN_UV|SCREEN_PIXEL_SIZE|TIME|COLOR)\b")]
    private static partial Regex Builtin();
}
