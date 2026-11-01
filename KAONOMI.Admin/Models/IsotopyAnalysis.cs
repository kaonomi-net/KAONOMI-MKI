using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Admin.Models;

/// <summary>Named isotopy with its associated word list.</summary>
/// <param name="IsotopyName">Label of the isotopy dimension.</param>
/// <param name="Wordlist">Words belonging to this isotopy.</param>
public record IsotopyItem(
    [property: JsonPropertyName("IsotopyName")] string IsotopyName,
    [property: JsonPropertyName("Wordlist")] IReadOnlyList<string> Wordlist
);

/// <summary>Root JSON document containing all isotopy analyses for a story.</summary>
/// <param name="Isotopies">Collection of isotopy items.</param>
public record IsotopyAnalysis(
    [property: JsonPropertyName("Isotopies")] IReadOnlyList<IsotopyItem> Isotopies
);
