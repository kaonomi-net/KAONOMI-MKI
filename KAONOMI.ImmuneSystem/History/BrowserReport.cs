namespace KAONOMI.ImmuneSystem.History;

/// <summary>A browser profile whose history was read (or could not be).</summary>
public sealed record BrowserProfile
{
    public required string Browser { get; init; }
    public required string Profile { get; init; }
    public required string Path { get; init; }
    public bool Readable { get; init; }

    /// <summary>Why it could not be read (Safari: Full Disk Access).</summary>
    public string? Error { get; init; }

    /// <summary>All the visits of the history (every site).</summary>
    public long Visits { get; init; }

    /// <summary>The visits to generative AI services.</summary>
    public long AiVisits { get; init; }

    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}

/// <summary>A generative AI service visited: how often, since when, in which browsers, month by month.</summary>
public sealed record AiServiceUse
{
    public required string Name { get; init; }

    /// <summary><c>chat</c>, <c>image</c>, <c>video</c>, <c>audio</c>, <c>code</c> or <c>writing</c>.</summary>
    public required string Category { get; init; }

    public long Visits { get; init; }
    public DateTime FirstVisit { get; init; }
    public DateTime LastVisit { get; init; }
    public required IReadOnlyList<string> Browsers { get; init; }

    /// <summary>Visits per month (<c>yyyy-MM</c>).</summary>
    public required IReadOnlyDictionary<string, long> Months { get; init; }
}

/// <summary>An image or an Office file downloaded by a browser.</summary>
public sealed record BrowserDownload
{
    public required string Browser { get; init; }
    public required string Profile { get; init; }
    public required string File { get; init; }

    /// <summary><c>image</c> or <c>office</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Where it was downloaded from.</summary>
    public string? Source { get; init; }

    /// <summary>The page that offered it.</summary>
    public string? Referrer { get; init; }

    public DateTime? Time { get; init; }
    public long? Size { get; init; }

    /// <summary>True when the file is still there.</summary>
    public bool Exists { get; init; }

    /// <summary>The AI service it comes from, when it does.</summary>
    public string? Service { get; init; }

    /// <summary>What the scan found in it: <c>ai-image</c> or <c>office-code</c>.</summary>
    public string? Finding { get; set; }
}

/// <summary>What the browser histories say: the profiles read, the AI services used, the images and Office files downloaded.</summary>
public sealed record BrowserReport
{
    public required IReadOnlyList<BrowserProfile> Profiles { get; init; }
    public required IReadOnlyList<AiServiceUse> AiServices { get; init; }
    public required IReadOnlyList<BrowserDownload> Downloads { get; init; }
    public long Visits { get; init; }
    public long AiVisits { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
}
