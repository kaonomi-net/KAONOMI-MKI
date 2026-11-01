using System.Collections.Generic;
namespace KAONOMI.Core.Models;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Analysis of a picture, made in the image analysis window of the chat (a double-click in <c>/pictures</c>): the link to
/// the picture, its metadata, the settings of its view (brightness, contrast, filters, orientation, zoom), every action
/// of the analysis, the rectangles that annotate parts of it, the comments (kept, never removed), the description written
/// by the AI and the place where it was taken. One analysis per picture, stored in the <c>imageanalysis</c> index: its id
/// is <see cref="IdFor"/> the path of the picture, so that the analysis is found again from the picture.
/// </summary>
public class ImageAnalysis : EntityBase, ISoftDeletable, ITagged
{
    /// <summary>The tags: #TOPIC, !ALERT, ?QUESTION, &amp;LINK, in upper case, no space (<see cref="TagRules"/>).</summary>
    public List<string> Tags { get; set; } = new List<string>();

    /// <summary>Path of the picture on the machine of the chat.</summary>
    public string imagePath { get; set; } = string.Empty;

    /// <summary>Link to the picture (file:// URL of <see cref="imagePath"/>).</summary>
    public string? imageUrl { get; set; }

    /// <summary>File name of the picture.</summary>
    public string? fileName { get; set; }

    /// <summary>Title of the picture, typed in the image analysis window (by default its file name).</summary>
    public string? title { get; set; }

    /// <summary>
    /// "image" for a picture analysed in the window, "subimage" for the part of a picture cut out by an annotation
    /// rectangle (saved in Pictures/kaonomi/imageparts).
    /// </summary>
    public string category { get; set; } = "image";

    /// <summary>For a subimage: the id of the analysis of the picture it was cut from, and the path of that picture.</summary>
    public string? parentId { get; set; }
    public string? parentImagePath { get; set; }

    /// <summary>Width and height of the picture, in pixels.</summary>
    public int width { get; set; }
    public int height { get; set; }

    /// <summary>
    /// Metadata of the picture (File, Image, Exif, GPS…), one entry per value (a list rather than a dictionary: the keys
    /// would become hundreds of Elasticsearch fields, some with dots in their names).
    /// </summary>
    public List<ImageMetadataEntry> metadata { get; set; } = [];

    /// <summary>Brightness of the view, -100 to 100 (0: unchanged).</summary>
    public int brightness { get; set; }

    /// <summary>Contrast of the view, -100 to 100 (0: unchanged).</summary>
    public int contrast { get; set; }

    /// <summary>Filters of the view, in the order they apply: monochrome, binarization, edges, invert.</summary>
    public List<string> filters { get; set; } = [];

    /// <summary>Threshold of the binarization, 0 to 255.</summary>
    public int binarizationThreshold { get; set; } = 128;

    /// <summary>Rotation of the view, 0, 90, 180 or 270 degrees clockwise.</summary>
    public int rotation { get; set; }

    /// <summary>Mirrored horizontally / vertically.</summary>
    public bool flipHorizontal { get; set; }
    public bool flipVertical { get; set; }

    /// <summary>Zoom of the view (1: 100 %).</summary>
    public double zoom { get; set; } = 1;

    /// <summary>Every action of the analysis, oldest first (filter, rotation, annotation, AI analysis, save…).</summary>
    public List<ImageAnalysisAction> actions { get; set; } = [];

    /// <summary>Rectangles that annotate parts of the picture, each with its comment.</summary>
    public List<ImageAnnotation> annotations { get; set; } = [];

    /// <summary>Comments on the picture, oldest first; new ones are added, none is ever removed.</summary>
    public List<ImageComment> comments { get; set; } = [];

    /// <summary>Description of the picture written by the AI (where it could have been taken included).</summary>
    public string? aiAnalysis { get; set; }

    /// <summary>Model that wrote <see cref="aiAnalysis"/> (e.g. mistral-small3.2).</summary>
    public string? aiModel { get; set; }

    /// <summary>Date of <see cref="aiAnalysis"/>.</summary>
    public DateTime? aiDate { get; set; }

    /// <summary>Where the picture was taken: latitude and longitude in degrees, and the name of the place.</summary>
    public double? latitude { get; set; }
    public double? longitude { get; set; }
    public string? locationName { get; set; }

    /// <summary>Soft delete: the analysis is kept but left out of the searches.</summary>
    public bool isDeleted { get; set; }

    /// <summary>
    /// The id of the analysis of the picture at <paramref name="imagePath"/>: the SHA-256 of its full path (case kept on
    /// Linux, ignored on macOS and Windows), so that one picture has one analysis.
    /// </summary>
    public static string IdFor(string imagePath)
    {
        var path = Path.GetFullPath(imagePath.Trim());
        if (!OperatingSystem.IsLinux())
            path = path.ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
    }
}

/// <summary>A metadata value of the picture: its group (Exif, GPS…), its key and its value.</summary>
public class ImageMetadataEntry : EntityBase
{
    public string group { get; set; } = string.Empty;
    public string key { get; set; } = string.Empty;
    public string value { get; set; } = string.Empty;
}

/// <summary>An action of the analysis: what was done (<c>date</c>: when), and its value.</summary>
public class ImageAnalysisAction : EntityBase
{
    /// <summary>brightness, contrast, filter, rotate, flip, zoom, annotate, comment, aiAnalysis, geolocate, save…</summary>
    public string action { get; set; } = string.Empty;

    public string? value { get; set; }
}

/// <summary>
/// A rectangle on the picture with its comment (<c>comment</c>, <c>date</c> and <c>id</c> of <see cref="EntityBase"/>);
/// position and size in shares of the picture (0 to 1).
/// </summary>
public class ImageAnnotation : EntityBase
{
    public double x { get; set; }
    public double y { get; set; }
    public double width { get; set; }
    public double height { get; set; }

    /// <summary>The part of the picture in the rectangle, saved as a picture of its own (a subimage).</summary>
    public string? imagePartPath { get; set; }
}

/// <summary>A comment on the picture: its text (<c>date</c> and <c>userName</c> of <see cref="EntityBase"/>).</summary>
public class ImageComment : EntityBase
{
    public string text { get; set; } = string.Empty;
}
