using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace KAONOMI.Chat.Services;

/// <summary>A group of metadata (File, Image, Exif, GPS, XMP…) and its entries, in reading order.</summary>
public sealed record MetadataGroup(string Name, IReadOnlyList<MetadataEntry> Entries);

/// <summary>One metadata value.</summary>
public sealed record MetadataEntry(string Key, string Value);

/// <summary>
/// Every metadata of a picture, shown when the mouse is over the preview of <c>/pictures</c>:
/// <list type="bullet">
/// <item>File: name, folder, size, dates, attributes (all systems);</item>
/// <item>on macOS, read through ImageIO (by a JavaScript for Automation script run with <c>osascript</c>): the image
/// properties (dimensions, depth, color model, profile, DPI…) and every dictionary of the file (TIFF, Exif, ExifAux, GPS,
/// IPTC, JFIF, PNG, GIF, HEIF, MakerApple / MakerCanon / MakerNikon…), the XMP tags (the exif and tiff ones, already in
/// the Exif and TIFF groups, are left out) and the Spotlight attributes (<c>mdls</c>: kind, where from, date added…).</item>
/// </list>
/// A few values are also shown in a readable form (exposure 1/60 s, f/1.8, GPS position in degrees, flash…).
/// </summary>
public static class PictureMetadata
{
    /// <summary>ImageIO properties of the first image and its XMP packet, as JSON (JavaScript for Automation).</summary>
    private const string ImageIoScript = """
        ObjC.import('Foundation'); ObjC.import('ImageIO');
        function run(argv) {
          const src = $.CGImageSourceCreateWithURL($.NSURL.fileURLWithPath(argv[0]), null);
          if (!src || (src.isNil && src.isNil())) return JSON.stringify({});
          const out = {};
          const props = $.CGImageSourceCopyPropertiesAtIndex(src, 0, null);
          if (props && !(props.isNil && props.isNil()))
            out.properties = ObjC.deepUnwrap(ObjC.castRefToObject(props));
          try {
            const md = $.CGImageSourceCopyMetadataAtIndex(src, 0, null);
            if (md && !(md.isNil && md.isNil())) {
              const data = ObjC.castRefToObject($.CGImageMetadataCreateXMPData(md, null));
              out.xmp = ObjC.unwrap($.NSString.alloc.initWithDataEncoding(data, $.NSUTF8StringEncoding));
            }
          } catch (e) { }
          return JSON.stringify(out, (k, v) =>
            v !== null && typeof v === 'object' && !Array.isArray(v) && v.constructor && v.constructor.name !== 'Object' ? '(binary data)' : v);
        }
        """;

    /// <summary>XMP namespaces whose tags are already shown in the Exif and TIFF groups.</summary>
    private static readonly HashSet<string> DuplicatedXmpPrefixes = new(StringComparer.OrdinalIgnoreCase) { "exif", "exifEX", "tiff" };

    /// <summary>Reads every metadata of the picture (in the background); the File group is always there.</summary>
    public static async Task<IReadOnlyList<MetadataGroup>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var groups = new List<MetadataGroup> { FileGroup(path) };
        if (!OperatingSystem.IsMacOS())
            return groups;

        try
        {
            var json = await RunAsync("/usr/bin/osascript", ["-l", "JavaScript", "-e", ImageIoScript, path], cancellationToken);
            if (!string.IsNullOrWhiteSpace(json))
                groups.AddRange(ImageIoGroups(json));
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            groups.Add(new MetadataGroup("ImageIO", [new MetadataEntry("Error", ex.Message)]));
        }

        try
        {
            var spotlight = await RunAsync("/usr/bin/mdls", [path], cancellationToken);
            if (SpotlightGroup(spotlight) is { Entries.Count: > 0 } group)
                groups.Add(group);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Spotlight attributes are optional.
        }
        return groups;
    }

    /// <summary>Name, folder, size, dates and attributes of the file.</summary>
    private static MetadataGroup FileGroup(string path)
    {
        var info = new FileInfo(path);
        var entries = new List<MetadataEntry>
        {
            new("Name", info.Name),
            new("Folder", info.DirectoryName ?? string.Empty),
            new("Format", info.Extension.TrimStart('.').ToUpperInvariant()),
        };
        if (info.Exists)
        {
            entries.Add(new("Size", $"{PictureLibrary.FormatSize(info.Length)} ({info.Length:N0} bytes)"));
            entries.Add(new("Created", info.CreationTime.ToString("F")));
            entries.Add(new("Modified", info.LastWriteTime.ToString("F")));
            entries.Add(new("Last opened", info.LastAccessTime.ToString("F")));
            entries.Add(new("Attributes", info.Attributes.ToString()));
            entries.Add(new("Read only", info.IsReadOnly ? "Yes" : "No"));
        }
        return new MetadataGroup("File", entries);
    }

    /// <summary>
    /// The ImageIO properties: the top-level values as "Image", each "{Dictionary}" as its own group (nested values
    /// with dotted keys), then the XMP tags.
    /// </summary>
    private static IEnumerable<MetadataGroup> ImageIoGroups(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            var image = new List<MetadataEntry>();
            var dictionaries = new List<MetadataGroup>();
            foreach (var property in properties.EnumerateObject().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    var name = property.Name.Trim('{', '}');
                    var entries = new List<MetadataEntry>();
                    Flatten(entries, string.Empty, property.Value);
                    AddReadable(name, entries);
                    dictionaries.Add(new MetadataGroup(name, entries));
                }
                else
                {
                    image.Add(new MetadataEntry(property.Name, Text(property.Value)));
                }
            }
            if (image.Count > 0)
                yield return new MetadataGroup("Image", image);
            // Most useful first: TIFF (camera), Exif, GPS, IPTC; then the others (maker notes last).
            string[] order = ["TIFF", "Exif", "ExifAux", "GPS", "IPTC", "JFIF", "PNG", "GIF", "HEIF", "WebP"];
            foreach (var group in dictionaries.OrderBy(g => Array.IndexOf(order, g.Name) is var i and >= 0 ? i : g.Name.StartsWith("Maker", StringComparison.Ordinal) ? 1000 : 500)
                         .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                yield return group;
        }

        if (root.TryGetProperty("xmp", out var xmp) && xmp.ValueKind == JsonValueKind.String
            && XmpGroup(xmp.GetString()!) is { Entries.Count: > 0 } xmpGroup)
            yield return xmpGroup;
    }

    /// <summary>Adds the values of <paramref name="element"/>; objects become dotted keys, arrays comma-separated lists.</summary>
    private static void Flatten(List<MetadataEntry> entries, string prefix, JsonElement element)
    {
        foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var key = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
                Flatten(entries, key, property.Value);
            else
                entries.Add(new MetadataEntry(key, Text(property.Value)));
        }
    }

    /// <summary>A JSON value as text: numbers without useless decimals, arrays joined.</summary>
    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer.ToString(CultureInfo.InvariantCulture)
            : Math.Round(value.GetDouble(), 6).ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Text)),
        JsonValueKind.Object => string.Join(", ", value.EnumerateObject().Select(p => $"{p.Name}: {Text(p.Value)}")),
        _ => string.Empty
    };

    /// <summary>Readable forms of a few Exif and GPS values, added after the raw ones.</summary>
    private static void AddReadable(string group, List<MetadataEntry> entries)
    {
        string? Get(string key) => entries.FirstOrDefault(e => e.Key == key)?.Value;
        double? Number(string key) => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        var readable = new List<MetadataEntry>();

        if (group == "Exif")
        {
            if (Number("ExposureTime") is > 0 and var exposure)
                readable.Add(new("Exposure (readable)", exposure >= 1 ? $"{exposure:0.#} s" : $"1/{Math.Round(1 / exposure)} s"));
            if (Number("FNumber") is { } aperture)
                readable.Add(new("Aperture (readable)", $"f/{aperture:0.#}"));
            if (Number("FocalLength") is { } focal)
                readable.Add(new("Focal length (readable)", Number("FocalLenIn35mmFilm") is { } equivalent
                    ? $"{focal:0.##} mm ({equivalent:0} mm in 35 mm)"
                    : $"{focal:0.##} mm"));
            if (Number("Flash") is { } flash)
                readable.Add(new("Flash (readable)", ((int)flash & 1) == 1 ? "Fired" : "Did not fire"));
            if (Get("DateTimeOriginal") is { } taken)
                readable.Add(new("Taken (readable)", taken + (Get("OffsetTimeOriginal") is { } offset ? $" ({offset})" : string.Empty)));
        }
        else if (group == "GPS" && Number("Latitude") is { } latitude && Number("Longitude") is { } longitude)
        {
            var lat = Get("LatitudeRef") == "S" ? -latitude : latitude;
            var lon = Get("LongitudeRef") == "W" ? -longitude : longitude;
            readable.Add(new("Position (readable)", $"{lat.ToString("0.######", CultureInfo.InvariantCulture)}, {lon.ToString("0.######", CultureInfo.InvariantCulture)}"));
            readable.Add(new("Map", $"https://www.openstreetmap.org/?mlat={lat.ToString(CultureInfo.InvariantCulture)}&mlon={lon.ToString(CultureInfo.InvariantCulture)}&zoom=16"));
            if (Number("Altitude") is { } altitude)
                readable.Add(new("Altitude (readable)", $"{(Get("AltitudeRef") == "1" ? -altitude : altitude):0.#} m"));
        }
        entries.InsertRange(0, readable);
    }

    /// <summary>
    /// The XMP tags of the packet (attributes and elements of every rdf:Description, lists joined), except the exif and
    /// tiff ones already shown.
    /// </summary>
    private static MetadataGroup XmpGroup(string xmp)
    {
        var entries = new List<MetadataEntry>();
        try
        {
            var document = XDocument.Parse(xmp);
            XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
            foreach (var description in document.Descendants(rdf + "Description"))
            {
                foreach (var attribute in description.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace != rdf))
                    Add(description, attribute.Name, attribute.Value);
                foreach (var element in description.Elements())
                    Add(description, element.Name, element.Descendants(rdf + "li").Any()
                        ? string.Join(", ", element.Descendants(rdf + "li").Select(li => li.Value.Trim()))
                        : element.Value.Trim());
            }
        }
        catch (System.Xml.XmlException)
        {
            entries.Add(new MetadataEntry("Error", "The XMP packet cannot be read"));
        }
        return new MetadataGroup("XMP", entries);

        void Add(XElement scope, XName name, string value)
        {
            var prefix = scope.GetPrefixOfNamespace(name.Namespace) ?? name.NamespaceName;
            if (!DuplicatedXmpPrefixes.Contains(prefix) && value.Length > 0)
                entries.Add(new MetadataEntry($"{prefix}:{name.LocalName}", value));
        }
    }

    /// <summary>
    /// The Spotlight attributes of <c>mdls</c> ("kMDItemName = value", lists in parentheses on several lines), without
    /// the internal ones (leading underscore) and the empty ones.
    /// </summary>
    private static MetadataGroup SpotlightGroup(string output)
    {
        var entries = new List<MetadataEntry>();
        string? key = null;
        List<string>? list = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (list is not null)
            {
                if (line.Trim() == ")")
                {
                    if (key is not null && list.Count > 0)
                        entries.Add(new MetadataEntry(key, string.Join(", ", list)));
                    list = null;
                    key = null;
                }
                else if (!line.TrimStart().StartsWith('{') && !line.TrimStart().StartsWith('}') && !line.Contains(" = ", StringComparison.Ordinal))
                {
                    list.Add(line.Trim().TrimEnd(',').Trim('"'));
                }
                continue;
            }

            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator <= 0)
                continue;
            var name = line[..separator].Trim();
            var value = line[(separator + 3)..].Trim();
            if (name.StartsWith('_') || name.EndsWith("_Ranking", StringComparison.Ordinal))
            {
                if (value == "(")
                    list = [];   // skipped list
                continue;
            }
            name = name.StartsWith("kMDItem", StringComparison.Ordinal) ? name["kMDItem".Length..] : name;
            if (value == "(")
            {
                key = name;
                list = [];
            }
            else if (value != "(null)" && value != "\"\"")
            {
                entries.Add(new MetadataEntry(name, value.Trim('"')));
            }
        }
        return new MetadataGroup("Spotlight", entries);
    }

    /// <summary>Runs a tool and returns its output (killed on cancellation or after 10 s).</summary>
    private static async Task<string> RunAsync(string tool, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} could not be started");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var kill = timeout.Token.Register(() =>
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        _ = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return await output;
    }
}
