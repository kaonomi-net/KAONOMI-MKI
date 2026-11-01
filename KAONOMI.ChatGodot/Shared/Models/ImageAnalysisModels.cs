using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Analysis of a picture of the <c>/imageanalysis</c> APIs (<c>ImageAnalysis</c> on the server side, index
/// <c>imageanalysis</c>): the link to the picture, its metadata, the settings of the view, the actions of the analysis,
/// the annotations, the comments, the AI description and the place. One per picture.
/// </summary>
public sealed class ImageAnalysisEntry
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("imagePath")]
    public string ImagePath { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    /// <summary>Title of the picture (by default its file name).</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>"image", or "subimage" for the part of a picture cut out by an annotation rectangle.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "image";

    /// <summary>For a subimage: the id of the analysis of the picture it was cut from, and its path.</summary>
    [JsonPropertyName("parentId")]
    public string? ParentId { get; set; }

    [JsonPropertyName("parentImagePath")]
    public string? ParentImagePath { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("metadata")]
    public List<ImageMetadataItem> Metadata { get; set; } = [];

    [JsonPropertyName("brightness")]
    public int Brightness { get; set; }

    [JsonPropertyName("contrast")]
    public int Contrast { get; set; }

    [JsonPropertyName("filters")]
    public List<string> Filters { get; set; } = [];

    [JsonPropertyName("binarizationThreshold")]
    public int BinarizationThreshold { get; set; } = 128;

    [JsonPropertyName("rotation")]
    public int Rotation { get; set; }

    [JsonPropertyName("flipHorizontal")]
    public bool FlipHorizontal { get; set; }

    [JsonPropertyName("flipVertical")]
    public bool FlipVertical { get; set; }

    [JsonPropertyName("zoom")]
    public double Zoom { get; set; } = 1;

    [JsonPropertyName("actions")]
    public List<ImageAnalysisActionItem> Actions { get; set; } = [];

    [JsonPropertyName("annotations")]
    public List<ImageAnnotationItem> Annotations { get; set; } = [];

    [JsonPropertyName("comments")]
    public List<ImageCommentItem> Comments { get; set; } = [];

    [JsonPropertyName("aiAnalysis")]
    public string? AiAnalysis { get; set; }

    [JsonPropertyName("aiModel")]
    public string? AiModel { get; set; }

    [JsonPropertyName("aiDate")]
    public DateTimeOffset? AiDate { get; set; }

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("locationName")]
    public string? LocationName { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>
    /// The id the server gives to the analysis of the picture at <paramref name="path"/> (as <c>ImageAnalysis.IdFor</c>
    /// in KAONOMI.Core): the SHA-256 of its full path, lower-cased except on Linux.
    /// </summary>
    public static string IdFor(string path)
    {
        var full = System.IO.Path.GetFullPath(path.Trim());
        if (!OperatingSystem.IsLinux())
            full = full.ToLowerInvariant();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full))).ToLowerInvariant();
    }
}

public sealed class ImageMetadataItem
{
    [JsonPropertyName("group")]
    public string Group { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

public sealed class ImageAnalysisActionItem
{
    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

/// <summary>A rectangle on the picture (shares of the original picture, 0 to 1) and its comment.</summary>
public sealed class ImageAnnotationItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }

    /// <summary>The part of the picture in the rectangle, saved as a subimage in Pictures/kaonomi/imageparts.</summary>
    [JsonPropertyName("imagePartPath")]
    public string? ImagePartPath { get; set; }
}

public sealed class ImageCommentItem
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }
}
