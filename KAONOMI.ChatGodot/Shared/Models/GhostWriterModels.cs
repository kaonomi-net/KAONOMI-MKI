using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KAONOMI.Chat.Models;

/// <summary>An actor (a character) of a writing: who they are, how they speak and think, what drives them.</summary>
public sealed partial class WriterActor : ObservableObject
{
    [ObservableProperty] [property: JsonPropertyName("name")] private string _name = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("description")] private string _description = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("howHeSpeaks")] private string _howHeSpeaks = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("howHeThinks")] private string _howHeThinks = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("hisMotivation")] private string _hisMotivation = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("hisGoal")] private string _hisGoal = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("hisSecret")] private string _hisSecret = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("hisWeakness")] private string _hisWeakness = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("hisStrength")] private string _hisStrength = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("hisIQ")] private int _hisIQ = 100;
    [ObservableProperty] [property: JsonPropertyName("hisEQ")] private int _hisEQ = 100;
    [ObservableProperty] [property: JsonPropertyName("isMainCharacter")] private bool _isMainCharacter;
    [ObservableProperty] [property: JsonPropertyName("isAsperger")] private bool _isAsperger;
    [ObservableProperty] [property: JsonPropertyName("isDyslexic")] private bool _isDyslexic;
    /// <summary>Disqualified (DISQUALIFIED): the actor is kept, set aside.</summary>
    [ObservableProperty] [property: JsonPropertyName("disqualified")] private bool _disqualified;
    /// <summary>The picture of the actor (uploaded or generated with Fooocus), in Pictures/kaonomi/writer/&lt;title&gt;.</summary>
    [ObservableProperty] [property: JsonPropertyName("imagePath")] private string _imagePath = string.Empty;
}

/// <summary>A location of a writing: where it is, its history, its culture, a picture and a wiki page.</summary>
public sealed partial class WriterLocation : ObservableObject
{
    [ObservableProperty] [property: JsonPropertyName("name")] private string _name = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("description")] private string _description = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("geoLocation")] private string _geoLocation = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("history")] private string _history = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("culture")] private string _culture = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("imageUrl")] private string _imageUrl = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("wikiUrl")] private string _wikiUrl = string.Empty;
}

/// <summary>A chapter of a writing: its place, what it is about, the perception it aims at, its ambiance, and its text.</summary>
public sealed partial class WriterChapter : ObservableObject
{
    [ObservableProperty] [property: JsonPropertyName("title")] private string _title = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("position")] private int _position = 1;
    [ObservableProperty] [property: JsonPropertyName("description")] private string _description = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("perceptionGoal")] private string _perceptionGoal = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("ambiance")] private string _ambiance = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("comments")] private List<string> _comments = [];
    [ObservableProperty] [property: JsonPropertyName("text")] private string _text = string.Empty;
    /// <summary>The footnotes of the chapter, each anchored on a piece of its text (a link <c>note:&lt;number&gt;</c>).</summary>
    [JsonPropertyName("footnotes")]
    public ObservableCollection<WriterFootnote> Footnotes { get; set; } = [];

    /// <summary>The address of the link that anchors footnote <paramref name="number"/> in the text.</summary>
    public static string NoteLink(int number) => $"{NoteScheme}{number}";

    public const string NoteScheme = "note:";
}

/// <summary>A footnote of a chapter: its number, the words it is anchored on, its text and its links.</summary>
public sealed partial class WriterFootnote : ObservableObject
{
    [ObservableProperty] [property: JsonPropertyName("number")] private int _number = 1;
    [ObservableProperty] [property: JsonPropertyName("anchor")] private string _anchor = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("text")] private string _text = string.Empty;
    [ObservableProperty] [property: JsonPropertyName("links")] private List<WriterFootnoteLink> _links = [];
}

/// <summary>A link of a footnote: its title and its address.</summary>
public sealed class WriterFootnoteLink
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>A writing as the Kaonomi server stores it (a document of the <c>ghostwriter</c> index).</summary>
public sealed class GhostWriterEntry
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>The date of the last version.</summary>
    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("goals")]
    public string Goals { get; set; } = string.Empty;

    [JsonPropertyName("audienceDescription")]
    public string AudienceDescription { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("style")]
    public string Style { get; set; } = string.Empty;

    [JsonPropertyName("actors")]
    public List<WriterActor> Actors { get; set; } = [];

    [JsonPropertyName("locations")]
    public List<WriterLocation> Locations { get; set; } = [];

    [JsonPropertyName("chapters")]
    public List<WriterChapter> Chapters { get; set; } = [];

    /// <summary>The summary of the whole writing (the PUBLISH tab).</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Created { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>The date of the last version, for the lists.</summary>
    [JsonIgnore]
    public string DateLabel => Date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    /// <summary>The slug of a title, as the server makes it ("writing" when nothing is left).</summary>
    public static string Slug(string? text)
    {
        var slug = WikiPageEntry.Slug(text);
        return slug == "page" ? "writing" : slug;
    }
}

/// <summary>
/// An entry of the journal of a writing (a document of the <c>ghostwriterjournal</c> index): an action that changed its
/// text, with the texts given, generated and corrected. The server only adds entries: none is changed nor deleted.
/// </summary>
public sealed class WriterJournalEntry
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>When the server added it.</summary>
    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("writingId")]
    public string WritingId { get; set; } = string.Empty;

    [JsonPropertyName("writingTitle")]
    public string WritingTitle { get; set; } = string.Empty;

    /// <summary>The kind of action (<see cref="WriterJournalActions"/>).</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>The chapter concerned ("3. The storm"), empty for the writing as a whole.</summary>
    [JsonPropertyName("chapter")]
    public string Chapter { get; set; } = string.Empty;

    /// <summary>The text given to the action: the text before a correction, the prompt of a generation.</summary>
    [JsonPropertyName("originalText")]
    public string OriginalText { get; set; } = string.Empty;

    [JsonPropertyName("generatedText")]
    public string GeneratedText { get; set; } = string.Empty;

    [JsonPropertyName("correctedText")]
    public string CorrectedText { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>A few words on the action (a version, a count of words…).</summary>
    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    /// <summary>When the action was made in the chat.</summary>
    [JsonPropertyName("happened")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Happened { get; set; }

    /// <summary>The line of the list: the time, the action and the chapter.</summary>
    [JsonIgnore]
    public string Line => $"{(Happened ?? Date)?.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {Action}{(Chapter.Length > 0 ? " · " + Chapter : "")}";
}

/// <summary>The kinds of action of the journal of a writing.</summary>
public static class WriterJournalActions
{
    public const string Save = "save";
    public const string AutoSave = "autosave";
    public const string Delete = "delete";
    public const string DraftRestored = "draft-restored";
    public const string ChapterAdded = "chapter-added";
    public const string ChapterRemoved = "chapter-removed";
    public const string ChapterMoved = "chapter-moved";
    public const string ActorAdded = "actor-added";
    public const string ActorDisqualified = "actor-disqualified";
    public const string ActorPicture = "actor-picture";
    public const string LocationAdded = "location-added";
    public const string LocationRemoved = "location-removed";
    public const string Summary = "summary";
    public const string AiCheck = "ai-check";
    public const string RewriteToFit = "ai-rewrite";
    public const string Rewrite = "rewrite";
    public const string Translation = "translation";
    public const string Evaluation = "evaluation";
    public const string CodeGenerated = "code-generated";
    public const string ImageGenerated = "image-generated";
    public const string Dictation = "dictation";
    public const string FootnoteAdded = "footnote-added";
    public const string FootnoteRemoved = "footnote-removed";
}
