namespace KAONOMI.ImmuneSystem.History;

/// <summary>A generative AI service, recognised by its address.</summary>
public sealed record AiServiceInfo(string Name, string Category, string[] Hosts, string? Path = null)
{
    public bool Matches(Uri uri) =>
        Hosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
        && (Path is null || uri.AbsolutePath.StartsWith(Path, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The generative AI services looked for in the histories, by category: <c>chat</c> (assistants), <c>image</c>,
/// <c>video</c>, <c>audio</c> (music, voices), <c>code</c>, <c>writing</c> (texts, slides).
/// </summary>
public static class AiServices
{
    public static readonly AiServiceInfo[] All =
    [
        // The ones with a path first: their host also serves other things.
        new("Bing Image Creator", "image", ["bing.com"], "/images/create"),
        new("Google ImageFX / Whisk / Flow", "image", ["labs.google"], "/fx"),
        new("Hugging Face Chat", "chat", ["huggingface.co"], "/chat"),
        new("Grok", "chat", ["x.com"], "/i/grok"),
        new("GitHub Copilot", "code", ["github.com"], "/copilot"),
        new("Freepik AI", "image", ["freepik.com"], "/pikaso"),
        new("Canva AI", "image", ["canva.com"], "/ai"),
        new("Sora", "video", ["sora.com", "sora.chatgpt.com"]),

        new("ChatGPT", "chat", ["chatgpt.com", "chat.openai.com"]),
        new("Claude", "chat", ["claude.ai"]),
        new("Gemini", "chat", ["gemini.google.com", "bard.google.com"]),
        new("Google AI Studio", "chat", ["aistudio.google.com"]),
        new("NotebookLM", "chat", ["notebooklm.google.com"]),
        new("Microsoft Copilot", "chat", ["copilot.microsoft.com", "copilot.cloud.microsoft"]),
        new("Perplexity", "chat", ["perplexity.ai"]),
        new("Le Chat (Mistral)", "chat", ["chat.mistral.ai"]),
        new("DeepSeek", "chat", ["chat.deepseek.com"]),
        new("Grok", "chat", ["grok.com"]),
        new("Meta AI", "chat", ["meta.ai"]),
        new("Poe", "chat", ["poe.com"]),
        new("Character.AI", "chat", ["character.ai"]),
        new("Pi", "chat", ["pi.ai"]),
        new("Qwen Chat", "chat", ["chat.qwen.ai"]),
        new("Kimi", "chat", ["kimi.com", "kimi.moonshot.cn"]),
        new("You.com", "chat", ["you.com"]),

        new("Midjourney", "image", ["midjourney.com"]),
        new("Leonardo.Ai", "image", ["leonardo.ai"]),
        new("Ideogram", "image", ["ideogram.ai"]),
        new("Adobe Firefly", "image", ["firefly.adobe.com"]),
        new("Microsoft Designer", "image", ["designer.microsoft.com"]),
        new("Playground", "image", ["playground.com", "playgroundai.com"]),
        new("Krea", "image", ["krea.ai"]),
        new("Civitai", "image", ["civitai.com"]),
        new("Tensor.Art", "image", ["tensor.art"]),
        new("NightCafe", "image", ["nightcafe.studio"]),
        new("Craiyon", "image", ["craiyon.com"]),
        new("DreamStudio", "image", ["dreamstudio.ai", "stability.ai"]),
        new("NovelAI", "image", ["novelai.net"]),
        new("Recraft", "image", ["recraft.ai"]),
        new("Lexica", "image", ["lexica.art"]),
        new("OpenArt", "image", ["openart.ai"]),
        new("getimg.ai", "image", ["getimg.ai"]),
        new("Dream by WOMBO", "image", ["dream.ai"]),
        new("Artbreeder", "image", ["artbreeder.com"]),
        new("Clipdrop", "image", ["clipdrop.co"]),
        new("Black Forest Labs (FLUX)", "image", ["bfl.ai", "blackforestlabs.ai"]),

        new("Runway", "video", ["runwayml.com"]),
        new("Pika", "video", ["pika.art"]),
        new("Luma Dream Machine", "video", ["lumalabs.ai"]),
        new("Kling AI", "video", ["klingai.com"]),
        new("Hailuo AI", "video", ["hailuoai.video"]),
        new("HeyGen", "video", ["heygen.com"]),
        new("Synthesia", "video", ["synthesia.io"]),
        new("InVideo AI", "video", ["invideo.io"]),

        new("Suno", "audio", ["suno.com", "suno.ai"]),
        new("Udio", "audio", ["udio.com"]),
        new("ElevenLabs", "audio", ["elevenlabs.io"]),

        new("Cursor", "code", ["cursor.com", "cursor.sh"]),
        new("v0", "code", ["v0.dev", "v0.app"]),
        new("Bolt", "code", ["bolt.new"]),
        new("Lovable", "code", ["lovable.dev"]),
        new("Replit", "code", ["replit.com"]),
        new("Windsurf", "code", ["windsurf.com", "codeium.com"]),

        new("Gamma", "writing", ["gamma.app"]),
        new("Jasper", "writing", ["jasper.ai"]),
        new("Writesonic", "writing", ["writesonic.com"]),
        new("QuillBot", "writing", ["quillbot.com"]),
    ];

    /// <summary>The service of an address, or null.</summary>
    public static AiServiceInfo? Of(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        return All.FirstOrDefault(s => s.Matches(uri));
    }
}
