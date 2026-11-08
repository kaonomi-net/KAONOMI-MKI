using System.Text.Json.Serialization;

/// <summary>ROC story row deserialized from JSON for isotopy batch processing.</summary>
/// <param title="Id">Unique story identifier.</param>
/// <param title="StoryTitle">Display title of the story.</param>
/// <param title="Sentence1">First context sentence.</param>
/// <param title="Sentence2">Second context sentence.</param>
/// <param title="Sentence3">Third context sentence.</param>
/// <param title="Sentence4">Fourth context sentence.</param>
/// <param title="Sentence5">Fifth context sentence.</param>
/// <param title="Completion">Expected completion text for the story.</param>
public record StoryInput(
    [property: JsonPropertyName("Id")] string Id,
    [property: JsonPropertyName("StoryTitle")] string StoryTitle,
    [property: JsonPropertyName("Sentence1")] string Sentence1,
    [property: JsonPropertyName("Sentence2")] string Sentence2,
    [property: JsonPropertyName("Sentence3")] string Sentence3,
    [property: JsonPropertyName("Sentence4")] string Sentence4,
    [property: JsonPropertyName("Sentence5")] string Sentence5,
    [property: JsonPropertyName("Completion")] string Completion
);
