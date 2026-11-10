using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// An interview (<c>/interview</c> in the chat), one document of the <c>interview</c> index: the person interviewed
    /// (name, address, email, phone, passport or identity card), the notes of the interviewer (Markdown, in the format
    /// of the rich editor of the chat) and its recordings (WAV files of <c>~/kaonomi/audio/interview</c>). Its
    /// transcription is kept apart, paragraph by paragraph (<see cref="InterviewParagraph"/>). The server keeps its
    /// version (one more at each save), its date of creation and its summary. Never erased (<see cref="ISoftDeletable"/>).
    /// </summary>
    public class Interview : EntityBase, ISoftDeletable
    {
        /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
        public bool isDeleted { get; set; } = false;

        public string firstName { get; set; } = string.Empty;

        public string lastName { get; set; } = string.Empty;

        public string address { get; set; } = string.Empty;

        public string email { get; set; } = string.Empty;

        public string phone { get; set; } = string.Empty;

        /// <summary>The kind of the document of identity: Passport or Identity card.</summary>
        public string documentType { get; set; } = string.Empty;

        /// <summary>The number of the passport or of the identity card.</summary>
        public string documentNumber { get; set; } = string.Empty;

        /// <summary>The notes of the interview, in Markdown.</summary>
        public string content { get; set; } = string.Empty;

        /// <summary>The start of its notes, without formatting (set by the server).</summary>
        public string summary { get; set; } = string.Empty;

        /// <summary>
        /// The summary note of the interview: a summary of its notes and of its transcription, written by the model of
        /// the chat after a save (Markdown). <see cref="summary"/> is only the start of the notes.
        /// </summary>
        public string summaryNote { get; set; } = string.Empty;

        /// <summary>When <see cref="summaryNote"/> was written, and by which model.</summary>
        public DateTime? summaryNoteAt { get; set; }

        public string summaryNoteModel { get; set; } = string.Empty;

        /// <summary>The fingerprint of the text <see cref="summaryNote"/> sums up: the note is written again only when the text changed.</summary>
        public string summaryNoteSource { get; set; } = string.Empty;

        /// <summary>The language spoken (fr, de, en…), the one of the transcription.</summary>
        public string language { get; set; } = string.Empty;

        /// <summary>When the interview was created (<see cref="EntityBase.date"/> is the date of its last version).</summary>
        public DateTime? created { get; set; }

        /// <summary>The version of the interview: 1 at its creation, one more at each save.</summary>
        public int version { get; set; }

        /// <summary>Its recordings, in the order they were made.</summary>
        public List<InterviewRecording> recordings { get; set; } = new List<InterviewRecording>();

        /// <summary>
        /// The entities the NER of the server (<c>POST /ner</c>) finds in its notes and in its transcription, each one
        /// once with how many times it is written; set by the server each time the interview is saved.
        /// </summary>
        public List<InterviewEntity> entities { get; set; } = new List<InterviewEntity>();

        /// <summary>The emotions of the paragraphs of its notes, from the deep text analysis (those of the transcription are on its paragraphs).</summary>
        public List<InterviewNoteEmotion> noteEmotions { get; set; } = new List<InterviewNoteEmotion>();

        /// <summary>What the deep text analysis says of the interview as a whole (Markdown): the emotions along it, the entities it added to the ontology.</summary>
        public string analysis { get; set; } = string.Empty;

        /// <summary>When the deep text analysis was run last (null: never).</summary>
        public DateTime? analyzedAt { get; set; }

        /// <summary>
        /// The interview made ready to be saved: its texts trimmed, its title the name of the person when none is
        /// given ("Interview" when there is no name either), its summary the start of its notes.
        /// </summary>
        public void Normalize()
        {
            firstName = (firstName ?? string.Empty).Trim();
            lastName = (lastName ?? string.Empty).Trim();
            address = (address ?? string.Empty).Trim();
            email = (email ?? string.Empty).Trim();
            phone = (phone ?? string.Empty).Trim();
            documentType = (documentType ?? string.Empty).Trim();
            documentNumber = (documentNumber ?? string.Empty).Trim();
            language = (language ?? string.Empty).Trim();
            content ??= string.Empty;
            recordings ??= new List<InterviewRecording>();
            entities ??= new List<InterviewEntity>();
            noteEmotions ??= new List<InterviewNoteEmotion>();
            analysis ??= string.Empty;
            summaryNote ??= string.Empty;
            summaryNoteModel ??= string.Empty;
            summaryNoteSource ??= string.Empty;
            title = (title ?? string.Empty).Trim();
            if (title.Length == 0)
            {
                title = (lastName + " " + firstName).Trim();
            }
            if (title.Length == 0)
            {
                title = "Interview";
            }
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString();
            }
            summary = Wiki.Summary(content);
        }
    }

    /// <summary>
    /// An entity found in an interview by the NER of the server: its text as written first, its type (PERSON, ORG, LOC,
    /// DATE, EMAIL…, or a type of the ontology), how many times it is written, and the entity of the ontology it is
    /// when it is known there.
    /// </summary>
    public class InterviewEntity
    {
        public string text { get; set; } = string.Empty;

        public string type { get; set; } = string.Empty;

        /// <summary>How many times it is written in the notes and the transcription.</summary>
        public int count { get; set; }

        /// <summary>0 to 1: the best score of its occurrences.</summary>
        public double score { get; set; }

        /// <summary>"xlmr", "rule", "ontology" or "xlmr+ontology" (see <see cref="NerEntity.source"/>).</summary>
        public string source { get; set; } = string.Empty;

        public string? ontologyId { get; set; }

        public string? ontologyName { get; set; }

        /// <summary>
        /// The entities of <paramref name="found"/> once each (the same type and the same text, whatever the case), the
        /// most written first.
        /// </summary>
        public static List<InterviewEntity> Merge(IEnumerable<NerEntity> found)
        {
            return found
                .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.text))
                .GroupBy(e => (type: (e.type ?? string.Empty).ToUpperInvariant(), text: (e.ontologyName ?? e.text).Trim().ToLowerInvariant()))
                .Select(g =>
                {
                    var best = g.OrderByDescending(e => e.ontologyId is not null).ThenByDescending(e => e.score).First();
                    return new InterviewEntity
                    {
                        text = (best.ontologyName ?? best.text).Trim(),
                        type = g.Key.type,
                        count = g.Count(),
                        score = Math.Round(g.Max(e => e.score), 3),
                        source = best.source ?? string.Empty,
                        ontologyId = best.ontologyId,
                        ontologyName = best.ontologyName
                    };
                })
                .OrderByDescending(e => e.count).ThenBy(e => e.type, StringComparer.Ordinal).ThenBy(e => e.text, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>An emotion and how strong it is, from 0 (absent) to 10.</summary>
    public class InterviewEmotion
    {
        public string name { get; set; } = string.Empty;

        public int intensity { get; set; }
    }

    /// <summary>The emotions of one paragraph of the notes of an interview, from the deep text analysis.</summary>
    public class InterviewNoteEmotion
    {
        /// <summary>Its rank among the paragraphs of the notes (0 for the first).</summary>
        public int index { get; set; }

        /// <summary>The start of the paragraph, to find it again.</summary>
        public string excerpt { get; set; } = string.Empty;

        /// <summary>The emotion that dominates.</summary>
        public string emotion { get; set; } = string.Empty;

        public int intensity { get; set; }

        public List<InterviewEmotion> emotions { get; set; } = new List<InterviewEmotion>();
    }

    /// <summary>
    /// What the audio analysis measures of a voice, over a whole recording or over one paragraph of it: its pitch and how
    /// much it moves, its loudness, how fast the words come and how much of the time is silence.
    /// </summary>
    public class InterviewVoice
    {
        /// <summary>The mean pitch of the voiced sounds, in hertz (0: no voiced sound found).</summary>
        public double pitchHz { get; set; }

        /// <summary>The low and the high of the pitch (its 5th and 95th percentiles), in hertz.</summary>
        public double pitchMinHz { get; set; }

        public double pitchMaxHz { get; set; }

        /// <summary>How much the pitch moves around its mean: its standard deviation, in semitones (a flat voice is under 2).</summary>
        public double pitchVariation { get; set; }

        /// <summary>The mean level of the speech, in decibels under full scale (negative).</summary>
        public double loudnessDb { get; set; }

        /// <summary>How much the level moves: its standard deviation, in decibels.</summary>
        public double loudnessVariation { get; set; }

        /// <summary>The words per minute, the pauses counted in the time.</summary>
        public double wordsPerMinute { get; set; }

        /// <summary>The words per minute of speech only (the pauses left out).</summary>
        public double articulationRate { get; set; }

        /// <summary>The seconds of speech, and of silence (pauses of 0.3 s or more).</summary>
        public double speechSeconds { get; set; }

        public double pauseSeconds { get; set; }

        public int pauseCount { get; set; }
    }

    /// <summary>A recording of an interview: a WAV file on the machine of the interviewer.</summary>
    public class InterviewRecording
    {
        /// <summary>The full path of the WAV file.</summary>
        public string path { get; set; } = string.Empty;

        /// <summary>The name of the file (the key of its paragraphs: <see cref="InterviewParagraph.audioFile"/>).</summary>
        public string fileName { get; set; } = string.Empty;

        public DateTime recordedAt { get; set; }

        public double durationSeconds { get; set; }

        public int sampleRate { get; set; }

        /// <summary>True once its transcription is stored (<see cref="InterviewParagraph"/>).</summary>
        public bool transcribed { get; set; }

        public int paragraphCount { get; set; }

        /// <summary>The voice over the whole recording, from the audio analysis (null: not analysed).</summary>
        public InterviewVoice? voice { get; set; }
    }

    /// <summary>
    /// A paragraph of the transcription of an interview, index <c>interviewparagraph</c>: what was said between two
    /// silences of a second or more in a recording. Its id is made of the interview, the recording and its rank, so
    /// that a recording transcribed again writes over its paragraphs.
    /// </summary>
    public class InterviewParagraph : EntityBase, ISoftDeletable
    {
        /// <summary>The id of its <see cref="Interview"/>.</summary>
        public string interviewId { get; set; } = string.Empty;

        /// <summary>The name of the person interviewed.</summary>
        public string person { get; set; } = string.Empty;

        /// <summary>The name of the WAV file it was said in (<see cref="InterviewRecording.fileName"/>).</summary>
        public string audioFile { get; set; } = string.Empty;

        /// <summary>Its rank in the recording (0 for the first).</summary>
        public int index { get; set; }

        public string text { get; set; } = string.Empty;

        /// <summary>Where it starts in the recording, in seconds.</summary>
        public double startSeconds { get; set; }

        /// <summary>Where it ends in the recording, in seconds.</summary>
        public double endSeconds { get; set; }

        public string language { get; set; } = string.Empty;

        /// <summary>The entities the NER of the server finds in <see cref="text"/> (positions in the paragraph); set by the server when the paragraph is saved.</summary>
        public List<NerEntity> entities { get; set; } = new List<NerEntity>();

        /// <summary>The emotion that dominates in the paragraph, from the deep text analysis (empty: not analysed).</summary>
        public string emotion { get; set; } = string.Empty;

        /// <summary>How strong <see cref="emotion"/> is, from 0 to 10.</summary>
        public int emotionIntensity { get; set; }

        /// <summary>Every emotion of the paragraph with its intensity.</summary>
        public List<InterviewEmotion> emotions { get; set; } = new List<InterviewEmotion>();

        /// <summary>The voice while the paragraph is said, from the audio analysis (null: not analysed).</summary>
        public InterviewVoice? voice { get; set; }

        public bool isDeleted { get; set; }

        /// <summary>The id of the paragraph <paramref name="index"/> of the recording <paramref name="audioFile"/> of an interview.</summary>
        public static string IdOf(string interviewId, string audioFile, int index) =>
            $"{interviewId}-{Wiki.Slug(Path.GetFileNameWithoutExtension(audioFile ?? string.Empty))}-{index:D4}";
    }

    /// <summary>The transcription of one recording of an interview, saved at once (<c>POST /interview/transcript</c>): it replaces the one stored.</summary>
    public class InterviewTranscript
    {
        public string interviewId { get; set; } = string.Empty;

        public string audioFile { get; set; } = string.Empty;

        public List<InterviewParagraph> paragraphs { get; set; } = new List<InterviewParagraph>();
    }
}
