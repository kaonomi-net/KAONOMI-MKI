namespace KAONOMI.ImmuneSystem;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>How sure a finding is: <c>low</c> (a hint, such as a file name), <c>medium</c>, <c>high</c> (an explicit mark).</summary>
public enum Confidence { Low, Medium, High }

/// <summary>How much a piece of code in an Office file deserves a look: <c>info</c> to <c>high</c> (runs by itself and calls the system).</summary>
public enum Severity { Info, Low, Medium, High }

/// <summary>One clue: where it was found (<c>c2pa</c>, <c>xmp</c>, <c>png:parameters</c>, <c>file-name</c>…) and what it says.</summary>
public sealed record Evidence(string Source, string Detail, Confidence Confidence);

/// <summary>An image that says (in its metadata or its name) that it was generated or edited by an AI.</summary>
public sealed record AiImage
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public required string Format { get; init; }

    /// <summary><c>ai-generated</c>, or <c>ai-edited</c> when only a part of the image comes from an AI.</summary>
    public required string Kind { get; init; }

    public Confidence Confidence { get; init; }

    /// <summary>The generator named in the metadata (Midjourney, DALL·E, Google Generative AI…), when there is one.</summary>
    public string? Generator { get; init; }

    /// <summary>True when the image carries a C2PA (Content Credentials) manifest.</summary>
    public bool ContentCredentials { get; init; }

    /// <summary>The prompt, when the generator left it in the file.</summary>
    public string? Prompt { get; init; }

    public required IReadOnlyList<Evidence> Evidence { get; init; }

    /// <summary>Where the file was downloaded from (browser history, macOS "where from", Windows Zone.Identifier).</summary>
    public string? Origin { get; init; }
}

/// <summary>One piece of code (or one way to run code) found in an Office file.</summary>
public sealed record CodeFinding
{
    /// <summary>
    /// <c>vba-macro</c>, <c>xlm-macro</c>, <c>python-in-excel</c>, <c>power-query</c>, <c>activex</c>, <c>ole-object</c>,
    /// <c>embedded-file</c>, <c>dde</c>, <c>remote-template</c>, <c>external-object</c>, <c>ribbon-callbacks</c>,
    /// <c>office-addin</c>, <c>code-text</c>, <c>encrypted</c>.
    /// </summary>
    public required string Kind { get; init; }

    public Severity Severity { get; init; }

    /// <summary>Where it is in the file: a part of the package, a stream, a module.</summary>
    public required string Location { get; init; }

    public required string Description { get; init; }

    /// <summary>The names that make the code run by itself (AutoOpen, Workbook_Open, Auto_Open…).</summary>
    public IReadOnlyList<string>? AutoExec { get; init; }

    /// <summary>What the code can do that deserves a look (Shell, CreateObject, URLDownloadToFile…).</summary>
    public IReadOnlyList<string>? Suspicious { get; init; }

    /// <summary>Addresses (URLs, IPs) written in the code.</summary>
    public IReadOnlyList<string>? Iocs { get; init; }

    /// <summary>Number of lines of code.</summary>
    public int? Lines { get; init; }

    /// <summary>The code itself (cut after <c>--max-code</c> characters; left out with <c>--no-code</c>).</summary>
    public string? Code { get; init; }
}

/// <summary>An Office file with code, or with a way to run code.</summary>
public sealed record OfficeFile
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    /// <summary>The extension, without the dot (docm, xlsx, ppt…).</summary>
    public required string Format { get; init; }

    /// <summary><c>ooxml</c> (Office 2007+ zip package), <c>cfb</c> (Office 97-2003 compound file) or <c>rtf</c>.</summary>
    public required string Container { get; init; }

    /// <summary>The highest severity of the findings.</summary>
    public Severity Severity { get; init; }

    public required IReadOnlyList<CodeFinding> Findings { get; init; }

    /// <summary>Where the file was downloaded from (browser history, macOS "where from", Windows Zone.Identifier).</summary>
    public string? Origin { get; init; }
}

public sealed record ScanError(string Path, string Error);

public sealed record ScanStats
{
    public long Directories { get; set; }
    public long Files { get; set; }
    public long ImagesInspected { get; set; }
    public long OfficeFilesInspected { get; set; }
    public long SkippedTooLarge { get; set; }
    public long Errors { get; set; }
    public long AiImages { get; set; }
    public long OfficeFilesWithCode { get; set; }
    public long BrowserProfiles { get; set; }
    public long AiServices { get; set; }
}

/// <summary>The JSON written at the end of a scan.</summary>
public sealed record ScanReport
{
    public string Tool { get; init; } = "KAONOMI.ImmuneSystem";
    public string Version { get; init; } = "1.0.0";
    public required string Machine { get; init; }
    public required string User { get; init; }
    public required string Os { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>True when the scan was stopped (Ctrl+C) before the end: the report holds what was found until then.</summary>
    public bool Interrupted { get; init; }

    public required IReadOnlyList<string> Roots { get; init; }
    public required object Options { get; init; }
    public required ScanStats Stats { get; init; }
    public required IReadOnlyList<AiImage> AiImages { get; init; }
    public required IReadOnlyList<OfficeFile> OfficeFiles { get; init; }

    /// <summary>What the browser histories say (null with <c>--no-history</c>).</summary>
    public History.BrowserReport? Browsers { get; init; }

    /// <summary>The files that could not be read (the first 500).</summary>
    public required IReadOnlyList<ScanError> Errors { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };
}
