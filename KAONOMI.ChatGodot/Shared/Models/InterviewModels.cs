using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// An interview of <c>/interview</c> (<c>Interview</c> on the server side, the <c>interview</c> index): the person
/// interviewed, the notes (Markdown, in the format of the rich editor) and the recordings. The server keeps its
/// version, its dates and its summary; its transcription is stored apart, paragraph by paragraph
/// (<see cref="InterviewParagraphEntry"/>).
/// </summary>
public sealed class InterviewEntry
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>The project the document is attached to (the id of a <c>Project</c>, chosen by its name in the header of the window), or null.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

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

    [JsonPropertyName("firstName")]
    public string FirstName { get; set; } = string.Empty;

    [JsonPropertyName("lastName")]
    public string LastName { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = string.Empty;

    /// <summary>Passport or Identity card (<see cref="DocumentTypes"/>).</summary>
    [JsonPropertyName("documentType")]
    public string DocumentType { get; set; } = string.Empty;

    [JsonPropertyName("documentNumber")]
    public string DocumentNumber { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    /// <summary>The summary note: a summary of the notes and of the transcription, written by the model of the chat after a save (Markdown).</summary>
    [JsonPropertyName("summaryNote")]
    public string SummaryNote { get; set; } = string.Empty;

    [JsonPropertyName("summaryNoteAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? SummaryNoteAt { get; set; }

    [JsonPropertyName("summaryNoteModel")]
    public string SummaryNoteModel { get; set; } = string.Empty;

    /// <summary>The fingerprint of the text the note sums up (it is written again only when the text changed).</summary>
    [JsonPropertyName("summaryNoteSource")]
    public string SummaryNoteSource { get; set; } = string.Empty;

    [JsonPropertyName("created")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Created { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("recordings")]
    public List<InterviewRecordingEntry> Recordings { get; set; } = [];

    /// <summary>The entities the NER of the server found in the notes and the transcription (set by the server at each save).</summary>
    [JsonPropertyName("entities")]
    public List<InterviewEntityEntry> Entities { get; set; } = [];

    /// <summary>The emotions of the paragraphs of the notes, from the deep text analysis.</summary>
    [JsonPropertyName("noteEmotions")]
    public List<InterviewNoteEmotionEntry> NoteEmotions { get; set; } = [];

    /// <summary>What the deep text analysis says of the interview as a whole (Markdown).</summary>
    [JsonPropertyName("analysis")]
    public string Analysis { get; set; } = string.Empty;

    [JsonPropertyName("analyzedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? AnalyzedAt { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>The kinds of document of identity.</summary>
    public static readonly string[] DocumentTypes = ["Passport", "Identity card"];

    /// <summary>The name of the person, for the lists ("Interview" when none was given).</summary>
    [JsonIgnore]
    public string Person => (LastName + " " + FirstName).Trim() is { Length: > 0 } name ? name : Title.Length > 0 ? Title : "Interview";

    /// <summary>The date of the last version, for the lists.</summary>
    [JsonIgnore]
    public string DateLabel => Date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    /// <summary>What the list shows under the name: the recordings, then the start of the notes.</summary>
    [JsonIgnore]
    public string Details => (Recordings.Count > 0 ? $"🎙 {Recordings.Count} · " : string.Empty)
                             + (SummaryNote.Length > 0 ? SummaryNote.Replace('\n', ' ').Replace("**", string.Empty).Replace("- ", string.Empty).Trim() : Summary);
}

/// <summary>An entity found in an interview by the NER of the server, once, with how many times it is written.</summary>
public sealed class InterviewEntityEntry
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>The id of the entity of the ontology it is, or null.</summary>
    [JsonPropertyName("ontologyId")]
    public string? OntologyId { get; set; }

    [JsonPropertyName("ontologyName")]
    public string? OntologyName { get; set; }

    /// <summary>True when it is an entity of the ontology.</summary>
    [JsonIgnore]
    public bool IsKnown => !string.IsNullOrEmpty(OntologyId);

    /// <summary>Its text with its count, for the list ("Paris ×3").</summary>
    [JsonIgnore]
    public string Label => Count > 1 ? $"{Text} ×{Count}" : Text;
}

/// <summary>An entity found in a text by the NER of the server (<c>POST /ner</c>), where it is.</summary>
public sealed class NerEntityEntry
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("ontologyId")]
    public string? OntologyId { get; set; }

    [JsonPropertyName("ontologyName")]
    public string? OntologyName { get; set; }
}

/// <summary>An emotion and how strong it is, from 0 to 10.</summary>
public sealed class InterviewEmotionEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("intensity")]
    public int Intensity { get; set; }

    /// <summary>An emoticon for the emotion <paramref name="name"/> (in English or in French), "•" for another one.</summary>
    public static string Icon(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "joy" or "joie" or "happiness" or "happy" => "😊",
        "sadness" or "tristesse" or "sad" => "😢",
        "anger" or "colère" or "colere" or "angry" => "😠",
        "fear" or "peur" or "anxiety" or "anxiété" or "anxiete" => "😨",
        "surprise" => "😮",
        "disgust" or "dégoût" or "degout" => "🤢",
        "trust" or "confiance" => "🤝",
        "anticipation" or "hope" or "espoir" => "🤞",
        "neutral" or "neutre" or "calm" or "calme" => "😐",
        "" => string.Empty,
        _ => "•"
    };
}

/// <summary>The emotions of one paragraph of the notes of an interview, from the deep text analysis.</summary>
public sealed class InterviewNoteEmotionEntry
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("excerpt")]
    public string Excerpt { get; set; } = string.Empty;

    [JsonPropertyName("emotion")]
    public string Emotion { get; set; } = string.Empty;

    [JsonPropertyName("intensity")]
    public int Intensity { get; set; }

    [JsonPropertyName("emotions")]
    public List<InterviewEmotionEntry> Emotions { get; set; } = [];
}

/// <summary>
/// What the audio analysis measures of a voice, over a whole recording or over one paragraph of it: its pitch and how
/// much it moves, its loudness, how fast the words come and how much of the time is silence.
/// </summary>
public sealed class InterviewVoiceEntry
{
    /// <summary>The mean pitch of the voiced sounds, in hertz (0: no voiced sound found).</summary>
    [JsonPropertyName("pitchHz")]
    public double PitchHz { get; set; }

    [JsonPropertyName("pitchMinHz")]
    public double PitchMinHz { get; set; }

    [JsonPropertyName("pitchMaxHz")]
    public double PitchMaxHz { get; set; }

    /// <summary>The standard deviation of the pitch around its mean, in semitones.</summary>
    [JsonPropertyName("pitchVariation")]
    public double PitchVariation { get; set; }

    /// <summary>The mean level of the speech, in decibels under full scale.</summary>
    [JsonPropertyName("loudnessDb")]
    public double LoudnessDb { get; set; }

    [JsonPropertyName("loudnessVariation")]
    public double LoudnessVariation { get; set; }

    /// <summary>The words per minute, the pauses counted in the time.</summary>
    [JsonPropertyName("wordsPerMinute")]
    public double WordsPerMinute { get; set; }

    /// <summary>The words per minute of speech only.</summary>
    [JsonPropertyName("articulationRate")]
    public double ArticulationRate { get; set; }

    [JsonPropertyName("speechSeconds")]
    public double SpeechSeconds { get; set; }

    [JsonPropertyName("pauseSeconds")]
    public double PauseSeconds { get; set; }

    [JsonPropertyName("pauseCount")]
    public int PauseCount { get; set; }

    /// <summary>The voice in a few figures ("♪ 182 Hz ±2.1 st · 148 w/min").</summary>
    [JsonIgnore]
    public string Label =>
        (PitchHz > 0 ? $"♪ {PitchHz:0} Hz ±{PitchVariation:0.0} st" : "♪ —")
        + (WordsPerMinute > 0 ? $" · {WordsPerMinute:0} w/min" : string.Empty);

    /// <summary>Every figure, for a tooltip or a line of the report.</summary>
    [JsonIgnore]
    public string Details =>
        (PitchHz > 0 ? $"pitch {PitchHz:0} Hz ({PitchMinHz:0}–{PitchMaxHz:0} Hz), variation ±{PitchVariation:0.0} semitones" : "no voiced sound")
        + $" · level {LoudnessDb:0.0} dB (±{LoudnessVariation:0.0} dB)"
        + (WordsPerMinute > 0 ? $" · {WordsPerMinute:0} words/min ({ArticulationRate:0} without the pauses)" : string.Empty)
        + $" · speech {SpeechSeconds:0.0} s, {PauseCount} pause(s) of {PauseSeconds:0.0} s";
}

/// <summary>A recording of an interview: a WAV file of <c>~/kaonomi/audio/interview</c>.</summary>
public sealed class InterviewRecordingEntry
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; set; }

    [JsonPropertyName("durationSeconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("sampleRate")]
    public int SampleRate { get; set; }

    [JsonPropertyName("transcribed")]
    public bool Transcribed { get; set; }

    [JsonPropertyName("paragraphCount")]
    public int ParagraphCount { get; set; }

    /// <summary>The voice over the whole recording, from the audio analysis (null: not analysed).</summary>
    [JsonPropertyName("voice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InterviewVoiceEntry? Voice { get; set; }

    /// <summary>When it was made, how long it is and where its transcription stands, for the list.</summary>
    [JsonIgnore]
    public string Label =>
        $"{RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {InterviewParagraphEntry.Clock(DurationSeconds)} · " +
        (Transcribed ? $"{ParagraphCount} paragraph(s)" : "not transcribed") + (Voice is null ? string.Empty : " · " + Voice.Label);
}

/// <summary>
/// A paragraph of the transcription of an interview (the <c>interviewparagraph</c> index): what was said between two
/// silences of a second or more in a recording.
/// </summary>
public sealed class InterviewParagraphEntry
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    /// <summary>The project the document is attached to (the id of a <c>Project</c>, chosen by its name in the header of the window), or null.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Date { get; set; }

    [JsonPropertyName("machineName")]
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    public string? UserName { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("interviewId")]
    public string InterviewId { get; set; } = string.Empty;

    [JsonPropertyName("person")]
    public string Person { get; set; } = string.Empty;

    [JsonPropertyName("audioFile")]
    public string AudioFile { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("startSeconds")]
    public double StartSeconds { get; set; }

    [JsonPropertyName("endSeconds")]
    public double EndSeconds { get; set; }

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    /// <summary>The entities of its text (set by the server when it is saved).</summary>
    [JsonPropertyName("entities")]
    public List<NerEntityEntry> Entities { get; set; } = [];

    /// <summary>The emotion that dominates in it, from the deep text analysis (empty: not analysed).</summary>
    [JsonPropertyName("emotion")]
    public string Emotion { get; set; } = string.Empty;

    [JsonPropertyName("emotionIntensity")]
    public int EmotionIntensity { get; set; }

    [JsonPropertyName("emotions")]
    public List<InterviewEmotionEntry> Emotions { get; set; } = [];

    /// <summary>The voice while it is said, from the audio analysis (null: not analysed).</summary>
    [JsonPropertyName("voice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InterviewVoiceEntry? Voice { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>Its emotion for the list ("😠 anger 7/10"); empty when it was not analysed.</summary>
    [JsonIgnore]
    public string EmotionLabel => Emotion.Length == 0 ? string.Empty : $"{InterviewEmotionEntry.Icon(Emotion)} {Emotion} {EmotionIntensity}/10";

    /// <summary>Every emotion of the paragraph with its intensity, for the tooltip.</summary>
    [JsonIgnore]
    public string EmotionDetails => string.Join(" · ", Emotions.Select(e => $"{e.Name} {e.Intensity}/10"));

    /// <summary>Where it starts in its recording (00:01:23).</summary>
    [JsonIgnore]
    public string TimeLabel => Clock(StartSeconds);

    /// <summary><paramref name="seconds"/> as hh:mm:ss.</summary>
    public static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }
}

/// <summary>The transcription of one recording, saved at once in place of the one stored (<c>POST /interview/transcript</c>).</summary>
public sealed class InterviewTranscriptRequest
{
    [JsonPropertyName("interviewId")]
    public required string InterviewId { get; init; }

    [JsonPropertyName("audioFile")]
    public required string AudioFile { get; init; }

    [JsonPropertyName("paragraphs")]
    public required List<InterviewParagraphEntry> Paragraphs { get; init; }
}
