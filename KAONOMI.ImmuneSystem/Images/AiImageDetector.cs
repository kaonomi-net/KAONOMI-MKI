namespace KAONOMI.ImmuneSystem.Images;

using System.Text.RegularExpressions;

/// <summary>
/// Tells whether an image says it was made by an AI. It reads what the image declares, not its pixels:
/// <list type="bullet">
/// <item>the IPTC digital source type (XMP or C2PA): <c>trainedAlgorithmicMedia</c> (generated), <c>compositeWithTrainedAlgorithmicMedia</c> (edited);</item>
/// <item>the parameters written by the generators: AUTOMATIC1111 / Forge, ComfyUI, InvokeAI, NovelAI, Fooocus, Midjourney;</item>
/// <item>the name of a generator in the metadata (C2PA, XMP, EXIF Software…);</item>
/// <item>the file name given by the generators (Gemini_Generated_Image_…, DALL·E 2024-…, ComfyUI_00001_…), a weak hint.</item>
/// </list>
/// An image without metadata (a screenshot, a re-saved or messaging-app copy) is not detected; invisible watermarks such as
/// SynthID cannot be read without their maker's tools.
/// </summary>
public static class AiImageDetector
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".webp", ".heic", ".heif", ".avif", ".tif", ".tiff", ".gif", ".bmp", ".jxl",
    };

    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Generators named in metadata. <c>Strict</c>: a short name, only trusted in metadata read by structure.</summary>
    private static readonly (string Name, Regex Pattern, bool Strict)[] Generators =
    [
        ("Midjourney", new(@"\bMidjourney\b", Ci), false),
        ("DALL·E", new(@"\bDALL.{0,3}E(?: ?[23])?\b"), false),
        ("OpenAI (ChatGPT)", new(@"\b(?:OpenAI|ChatGPT|GPT-4o|gpt-image-\d)\b"), false),
        ("Google Generative AI (Gemini / Imagen)", new(@"Google Generative AI|\bImagen [234]\b|\bSynthID\b|\bImageFX\b"), false),
        ("Gemini", new(@"\bGemini\b"), true),
        ("Adobe Firefly", new(@"\bAdobe Firefly\b|\bFirefly\b|\bGenerative (?:Fill|Expand)\b"), false),
        ("Stable Diffusion", new(@"\bStable[ _-]?Diffusion\b|stable-diffusion-webui|\bAUTOMATIC1111\b|\bSDXL\b|\bStability AI\b", Ci), false),
        ("Microsoft Designer / Bing Image Creator", new(@"Bing Image Creator|Microsoft Designer|Image Creator from (?:Microsoft )?Designer|Copilot Designer"), false),
        ("FLUX (Black Forest Labs)", new(@"\bFLUX\.1\b|Black Forest Labs"), false),
        ("ComfyUI", new(@"\bComfyUI\b"), false),
        ("InvokeAI", new(@"\bInvoke ?AI\b", Ci), false),
        ("NovelAI", new(@"\bNovelAI\b"), false),
        ("Fooocus", new(@"\bFooocus\b"), false),
        ("Draw Things", new(@"\bDraw Things\b"), false),
        ("DiffusionBee", new(@"\bDiffusionBee\b"), false),
        ("Leonardo.Ai", new(@"\bLeonardo\.?Ai\b", Ci), false),
        ("Ideogram", new(@"\bIdeogram\b"), false),
        ("Meta AI", new(@"\bMeta AI\b|Imagined with AI|Imagine with Meta"), false),
        ("Grok (xAI)", new(@"\bGrok\b|\bxAI\b"), true),
        ("Runway", new(@"\bRunway ?(?:ML|Gen-?\d)\b"), false),
        ("Canva AI", new(@"Canva.{0,12}(?:Magic Media|Dream Lab)"), false),
        ("Playground AI", new(@"\bPlayground ?(?:AI|v[23])\b"), false),
        ("Recraft", new(@"\bRecraft\b"), true),
        ("Kandinsky", new(@"\bKandinsky \d"), false),
        ("Krea", new(@"\bKrea AI\b"), false),
        ("Craiyon", new(@"\bCraiyon\b"), false),
        ("NightCafe", new(@"\bNightCafe\b"), false),
        ("Dream by WOMBO", new(@"\bWOMBO\b"), true),
        ("Freepik AI", new(@"\bFreepik (?:Pikaso|AI)\b"), false),
    ];

    private static readonly Regex AiMention = new(
        @"\b(?:AI[- ]generated|generated (?:by|with) (?:an )?AI|made with AI|created (?:by|with) (?:a )?generative AI|g[ée]n[ée]r[ée]e? par (?:l'|une )?IA|KI-generiert)\b", Ci);

    private static readonly Regex SdParameters = new(@"Steps: \d+, Sampler: [^,]+", RegexOptions.CultureInvariant);

    private static readonly (Regex Pattern, string Generator)[] FileNames =
    [
        (new(@"^Gemini_Generated_Image_", Ci), "Gemini"),
        (new(@"^image_?fx\b", Ci), "Google ImageFX"),
        (new(@"^DALL.{0,3}E[ _]\d{4}-\d\d-\d\d", Ci), "DALL·E"),
        (new(@"^ChatGPT Image\b", Ci), "OpenAI (ChatGPT)"),
        (new(@"^ComfyUI_\d+", Ci), "ComfyUI"),
        (new(@"^\d{5}-\d{6,}(?:-.+)?\.(?:png|jpe?g|webp)$", Ci), "Stable Diffusion WebUI"),
        (new(@"^Firefly[ _]", Ci), "Adobe Firefly"),
        (new(@"^(?:grok|aurora)[-_ ]?image", Ci), "Grok (xAI)"),
        (new(@"^Leonardo_", Ci), "Leonardo.Ai"),
        (new(@"^Ideogram", Ci), "Ideogram"),
        (new(@"^OIG\d?\.", Ci), "Bing Image Creator"),
        (new(@"^Designer(?: \(\d+\))?\.", Ci), "Microsoft Designer"),
        (new(@"midjourney", Ci), "Midjourney"),
        (new(@"^[a-z0-9_.]+_.+_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?:_\d)?\.(?:png|webp|jpe?g)$", Ci), "Midjourney"),
        (new(@"(?:^|[-_ ])ai[-_ ]?generated(?:[-_ .]|$)", Ci), null!),
    ];

    private static readonly string[] Structured = ["jpeg", "png", "webp", "heic", "avif", "jxl"];

    /// <summary>The image as an AI image, or null when nothing says it is one.</summary>
    public static AiImage? Inspect(FileInfo file)
    {
        var meta = ImageMetadata.Read(file.FullName);
        var text = meta.Text;
        var evidence = new List<Evidence>();
        string? generator = null;
        string? prompt = null;
        var edited = false;

        // 1. IPTC digital source type, written in XMP or in the actions of a C2PA manifest.
        var where = meta.HasC2pa ? "c2pa" : "xmp";
        if (text.Contains("digitalsourcetype/trainedAlgorithmicMedia", StringComparison.Ordinal))
        {
            evidence.Add(new(where, "IPTC digital source type: trainedAlgorithmicMedia (created by a generative AI)", Confidence.High));
        }
        else if (text.Contains("compositeWithTrainedAlgorithmicMedia", StringComparison.Ordinal))
        {
            edited = true;
            evidence.Add(new(where, "IPTC digital source type: compositeWithTrainedAlgorithmicMedia (a part was made by a generative AI)", Confidence.High));
        }

        // 2. The parameters left by the generators.
        GeneratorParameters(meta, text, evidence, ref generator, ref prompt);

        // 3. A generator named in the metadata.
        var structured = Structured.Contains(meta.Format);
        foreach (var (name, pattern, strict) in Generators)
        {
            if (strict && !structured)
                continue;
            var m = pattern.Match(text);
            if (!m.Success)
                continue;
            generator ??= name;
            evidence.Add(new(where == "c2pa" && Near(text, m.Index, "c2pa") ? "c2pa" : "metadata", $"Names the generator: {m.Value}", Confidence.Medium));
            break;
        }
        if (structured && AiMention.Match(text) is { Success: true } mention)
            evidence.Add(new("metadata", $"Says: {Excerpt(text, mention)}", Confidence.Medium));

        // 4. The name given by the generator to the file.
        foreach (var (pattern, name) in FileNames)
        {
            if (!pattern.IsMatch(file.Name))
                continue;
            generator ??= name;
            evidence.Add(new("file-name", name is null ? "The name says it was generated by an AI" : $"Named as the files of {name}", Confidence.Low));
            break;
        }

        if (evidence.Count == 0)
            return null;

        var confidence = evidence.Max(e => e.Confidence);
        if (confidence == Confidence.Medium && evidence.Count(e => e.Confidence >= Confidence.Medium) >= 2)
            confidence = Confidence.High;

        return new AiImage
        {
            Path = file.FullName,
            Size = file.Length,
            Modified = file.LastWriteTime,
            Format = meta.Format is "" or "unknown" ? file.Extension.TrimStart('.').ToLowerInvariant() : meta.Format,
            Kind = edited ? "ai-edited" : "ai-generated",
            Confidence = confidence,
            Generator = generator,
            ContentCredentials = meta.HasC2pa,
            Prompt = prompt is null ? null : Cut(prompt.Trim(), 1000),
            Evidence = evidence,
        };
    }

    private static void GeneratorParameters(ImageMetadata meta, string text, List<Evidence> evidence, ref string? generator, ref string? prompt)
    {
        var png = meta.PngText;
        string? Get(string key) => png.TryGetValue(key, out var v) ? v : null;

        if (Get("parameters") is { } parameters)
        {
            if (parameters.TrimStart().StartsWith('{') && parameters.Contains("Fooocus", StringComparison.OrdinalIgnoreCase))
            {
                generator ??= "Fooocus";
                prompt ??= JsonString(parameters, "prompt");
                evidence.Add(new("png:parameters", "Generation parameters of Fooocus", Confidence.High));
            }
            else if (SdParameters.IsMatch(parameters))
            {
                var model = Regex.Match(parameters, @"Model: ([^,\n]+)");
                generator ??= model.Success ? $"Stable Diffusion ({model.Groups[1].Value.Trim()})" : "Stable Diffusion";
                prompt ??= SdPrompt(parameters);
                evidence.Add(new("png:parameters", $"Generation parameters of a Stable Diffusion WebUI: {SdParameters.Match(parameters).Value}", Confidence.High));
            }
        }
        else if (SdParameters.Match(text) is { Success: true } sd)
        {
            generator ??= "Stable Diffusion";
            prompt ??= SdPrompt(text[Math.Max(0, sd.Index - 2000)..sd.Index]);
            evidence.Add(new("exif:usercomment", $"Generation parameters of a Stable Diffusion WebUI: {sd.Value}", Confidence.High));
        }

        if (Get("workflow") is not null || Get("prompt") is { } p && p.TrimStart().StartsWith('{') && p.Contains("class_type", StringComparison.Ordinal))
        {
            generator ??= "ComfyUI";
            prompt ??= Get("prompt") is { } graph ? JsonString(graph, "text") : null;
            evidence.Add(new("png:workflow", "ComfyUI workflow and prompt graph", Confidence.High));
        }
        else if (Get("prompt") is { } plain && !plain.TrimStart().StartsWith('{'))
        {
            prompt ??= plain;
            evidence.Add(new("png:prompt", "A prompt is written in the image", Confidence.Medium));
        }

        if ((Get("invokeai_metadata") ?? Get("invokeai_graph") ?? Get("sd-metadata") ?? Get("Dream")) is { } invoke)
        {
            generator ??= "InvokeAI";
            prompt ??= JsonString(invoke, "positive_prompt") ?? JsonString(invoke, "prompt");
            evidence.Add(new("png:invokeai", "Generation metadata of InvokeAI", Confidence.High));
        }

        if (Get("Software") is "NovelAI" || Get("Comment") is { } comment && comment.Contains("\"n_samples\"", StringComparison.Ordinal))
        {
            generator ??= "NovelAI";
            prompt ??= Get("Description");
            evidence.Add(new("png:comment", "Generation parameters of NovelAI", Confidence.High));
        }

        if (Get("fooocus_scheme") is not null && !evidence.Any(e => e.Detail.Contains("Fooocus")))
        {
            generator ??= "Fooocus";
            evidence.Add(new("png:fooocus_scheme", "Generation parameters of Fooocus", Confidence.High));
        }

        if (Get("Description") is { } description && description.Contains("Job ID:", StringComparison.Ordinal))
        {
            generator ??= "Midjourney";
            prompt ??= description[..description.IndexOf("Job ID:", StringComparison.Ordinal)];
            evidence.Add(new("png:description", "Prompt and job id of Midjourney", Confidence.High));
        }
    }

    private static string SdPrompt(string parameters)
    {
        var end = parameters.IndexOf("Negative prompt:", StringComparison.Ordinal);
        if (end < 0)
            end = parameters.IndexOf("Steps:", StringComparison.Ordinal);
        var s = end < 0 ? parameters : parameters[..end];
        // In EXIF the text is after "UNICODE" or "ASCII".
        var marker = s.LastIndexOf("UNICODE", StringComparison.Ordinal);
        if (marker < 0)
            marker = s.LastIndexOf("ASCII", StringComparison.Ordinal);
        return marker >= 0 ? s[(marker + (s[marker] == 'U' ? 7 : 5))..] : s;
    }

    private static string? JsonString(string json, string key)
    {
        var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!m.Success || m.Groups[1].Length == 0)
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>($"\"{m.Groups[1].Value}\"");
        }
        catch (System.Text.Json.JsonException)
        {
            return m.Groups[1].Value;
        }
    }

    private static bool Near(string text, int index, string word)
    {
        var from = Math.Max(0, index - 4000);
        return text.AsSpan(from, Math.Min(text.Length, index + 4000) - from).Contains(word, StringComparison.Ordinal);
    }

    private static string Excerpt(string text, Match m)
    {
        var from = m.Index;
        var to = Math.Min(text.Length, m.Index + m.Length + 60);
        var s = text[from..to];
        var stop = s.IndexOfAny(['\n', '\r', '<', '"', '\u0001', '\u0002', '\u0003']);
        if (stop >= m.Length)
            s = s[..stop];
        return Cut(new string(s.Where(c => c >= ' ' && c != '\u007f').ToArray()).Trim(), 120);
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
