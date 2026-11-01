namespace KAONOMI.Core.Models;

/// <summary>
/// Request of an image generation by Fooocus (Stable Diffusion XL), through the Fooocus API of the server
/// (<c>KAONOMI.Server/Fooocus/fooocus_api.py</c>). The fields left empty take the defaults of Fooocus.
/// </summary>
public class ImageGenerationRequest : EntityBase
{
    /// <summary>What the image shows.</summary>
    public string prompt { get; set; } = string.Empty;

    /// <summary>What the image must not show; null: the default negative prompt of Fooocus.</summary>
    public string? negativePrompt { get; set; }

    /// <summary>Fooocus styles (e.g. "Fooocus V2", "Fooocus Photograph"); null: the default ones (GET /image/options lists them).</summary>
    public List<string>? styles { get; set; }

    /// <summary>Quality, Speed (default), Extreme Speed, Lightning or Hyper-SD.</summary>
    public string? performance { get; set; }

    /// <summary>Size as width*height (e.g. 1152*896, 1024*1024, 896*1152); null: 1152*896.</summary>
    public string? aspectRatio { get; set; }

    /// <summary>Number of images, 1 to 32.</summary>
    public int imageNumber { get; set; } = 1;

    /// <summary>Seed of the first image; -1: a random one.</summary>
    public long seed { get; set; } = -1;

    /// <summary>png, jpeg or webp.</summary>
    public string outputFormat { get; set; } = "png";

    /// <summary>Checkpoint file (GET /image/options lists them); null: the default model of Fooocus.</summary>
    public string? baseModel { get; set; }

    /// <summary>Guidance scale (CFG); null: the default of Fooocus.</summary>
    public double? guidanceScale { get; set; }

    /// <summary>True: the images are returned in base 64 as well as their file paths.</summary>
    public bool includeBase64 { get; set; } = true;
}

/// <summary>Result of an image generation: the images, the seed used, the model and the time taken.</summary>
public class ImageGenerationResult : EntityBase
{
    /// <summary>The generated images.</summary>
    public List<GeneratedImage> images { get; set; } = [];

    /// <summary>Seed of the first image (the next ones follow it).</summary>
    public long seed { get; set; }

    /// <summary>Time of the generation, in seconds.</summary>
    public double seconds { get; set; }

    /// <summary>Checkpoint used.</summary>
    public string? model { get; set; }

    /// <summary>Why no image was generated (Fooocus API unreachable, bad request…); null on success.</summary>
    public string? error { get; set; }
}

/// <summary>A generated image: its file (outputs folder of Fooocus) and its data in base 64 when asked.</summary>
public class GeneratedImage : EntityBase
{
    /// <summary>Path of the image file on the machine of Fooocus.</summary>
    public string path { get; set; } = string.Empty;

    /// <summary>The image file in base 64 (PNG, JPEG or WebP); null when not asked.</summary>
    public string? base64 { get; set; }
}
