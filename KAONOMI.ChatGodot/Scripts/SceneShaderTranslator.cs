using System;
using System.Text.RegularExpressions;
using KAONOMI.Chat.Views;

namespace KAONOMI.ChatGodot;

/// <summary>
/// Turns the GLSL of the 3D scenes of the Avalonia chat (<see cref="FaceShaders"/>, <see cref="JarvisShaders"/>, shared
/// as they are) into Godot <c>canvas_item</c> shaders, so that both chats draw the same faces and the same holographic
/// display. Both are drawn in 2D, as in the Avalonia band: without depth test, in premultiplied alpha, in the colors
/// of the images (no linear light). The faces keep their own projection: the vertex shader moves each point of the
/// grid where the 3D camera of the scene sees it.
/// </summary>
public static class SceneShaderTranslator
{
    /// <summary>Godot shader of <paramref name="face"/>: the grid in relief, its jaw, its mouth and its lights.</summary>
    public static string Face(FaceShaders.FaceModel face)
    {
        var vertex = Body(FaceShaders.VertexShader, "vec2 aUv = UV;",
                "VERTEX = (clip.xy / clip.w * vec2(0.5, -0.5) + 0.5) * uSize;")
            .Replace("gl_Position =", "vec4 clip =");
        var fragment = Body(FaceShaders.FragmentShader, "", "")
            .Replace("gl_FragCoord", "FRAGCOORD")
            .Replace("fragColor =", "COLOR =");
        return "shader_type canvas_item;\n"
               + "render_mode blend_premul_alpha, unshaded;\n\n"
               + "uniform sampler2D uFace : filter_linear_mipmap, repeat_disable;   // premultiplied alpha\n"
               + "uniform mat4 uMvp;\n"
               + "uniform vec2 uSize;     // size of the scene, in its own units\n"
               + "uniform float uTime;\n"
               + "uniform float uOpen;\n"
               + "uniform vec3 uGlitch;\n"
               + "varying vec2 vUv;\n"
               + "varying float vLight;\n\n"
               + Rename(face.Glsl + FaceShaders.ShapeFunctions + face.Shape)
               + "\nvoid vertex()\n" + vertex
               + "\n\nvoid fragment()\n" + Rename(fragment) + "\n";
    }

    /// <summary>Godot shader of the JARVIS display (the whole scene in one fragment shader).</summary>
    public static string Jarvis()
    {
        var code = JarvisShaders.FragmentShader;
        // Uniforms and output are declared below; PI and TAU are built in.
        code = Regex.Replace(code, @"^\s*(uniform|out) .*$\n?", "", RegexOptions.Multiline);
        code = Regex.Replace(code, @"^\s*const float (PI|TAU) = .*$\n?", "", RegexOptions.Multiline);
        // Godot has no global variables: the sum of the colors crossed by the ray goes along as inout parameters.
        code = Regex.Replace(code, @"^\s*(vec3 gColor|float gGlow);.*$\n?", "", RegexOptions.Multiline);
        code = Regex.Replace(code, @"\b(add|gyroscope)\(", "$1(gColor, gGlow, ");
        code = Require(code, "void add(gColor, gGlow, ", "void add(inout vec3 gColor, inout float gGlow, ");
        code = Require(code, "void gyroscope(gColor, gGlow, ", "void gyroscope(inout vec3 gColor, inout float gGlow, ");
        code = Require(code, "gColor = vec3(0.0);", "vec3 gColor = vec3(0.0);");
        code = Require(code, "gGlow = 0.0;", "float gGlow = 0.0;");
        code = Require(code, "void main()", "void fragment()");
        // gl_FragCoord counts the pixels from the bottom left corner.
        code = Require(code, "gl_FragCoord.xy", "(vec2(UV.x, 1.0 - UV.y) * uFrame.xy)");
        code = Require(code, "fragColor =", "COLOR =");
        return "shader_type canvas_item;\n"
               + "render_mode blend_premul_alpha, unshaded;\n\n"
               + "uniform vec3 uFrame;      // width, height (pixels), time (seconds)\n"
               + "uniform float uLevel;     // voice level, 0 to 1\n"
               + "uniform float uLight;     // 1 when the background is light\n"
               + "uniform vec3 uGlitch;     // row (share of the height), sideways shift (pixels)\n\n"
               + ScalarMatrices(code) + "\n";
    }

    /// <summary>The block of <c>main()</c> in <paramref name="glsl"/>, with lines added at its start and end.</summary>
    private static string Body(string glsl, string prefix, string suffix)
    {
        var main = glsl.IndexOf("void main()", StringComparison.Ordinal);
        var open = glsl.IndexOf('{', main);
        var close = glsl.LastIndexOf('}');
        if (main < 0 || open < 0 || close < open)
            throw new FormatException("Shader without main()");
        return "{\n    " + prefix + glsl[(open + 1)..close].TrimEnd() + "\n    " + suffix + "\n}";
    }

    /// <summary>
    /// Function names that are reserved in Godot (<c>light</c> is a processor function there), and matrices built from
    /// their numbers.
    /// </summary>
    private static string Rename(string glsl) => ScalarMatrices(Regex.Replace(glsl, @"\blight\(", "glowAt("));

    /// <summary>
    /// Godot builds a matrix from its columns only: <c>mat2(a, b, c, d)</c> becomes <c>mat2(vec2(a, b), vec2(c, d))</c>,
    /// <c>mat3</c> of nine numbers three <c>vec3</c> columns.
    /// </summary>
    private static string ScalarMatrices(string glsl)
    {
        var result = new System.Text.StringBuilder();
        var position = 0;
        foreach (Match match in Regex.Matches(glsl, @"\bmat([23])\("))
        {
            if (match.Index < position)
                continue;
            var size = match.Groups[1].Value[0] - '0';
            // The arguments, split at the commas outside parentheses.
            var arguments = new System.Collections.Generic.List<string>();
            int depth = 0, start = match.Index + match.Length, end = start;
            for (; end < glsl.Length; end++)
            {
                var c = glsl[end];
                if (c == '(')
                    depth++;
                else if (c == ')' && depth-- == 0)
                    break;
                else if (c == ',' && depth == 0)
                {
                    arguments.Add(glsl[start..end].Trim());
                    start = end + 1;
                }
            }
            arguments.Add(glsl[start..end].Trim());
            if (arguments.Count != size * size)
                continue;
            var columns = new string[size];
            for (var column = 0; column < size; column++)
                columns[column] = $"vec{size}({string.Join(", ", arguments.GetRange(column * size, size))})";
            result.Append(glsl, position, match.Index - position).Append($"mat{size}({string.Join(", ", columns)})");
            position = end + 1;
        }
        return result.Append(glsl, position, glsl.Length - position).ToString();
    }

    private static string Require(string code, string text, string replacement)
    {
        var index = code.IndexOf(text, StringComparison.Ordinal);
        if (index < 0)
            throw new FormatException($"JARVIS shader: \"{text}\" not found");
        return code[..index] + replacement + code[(index + text.Length)..];
    }
}
