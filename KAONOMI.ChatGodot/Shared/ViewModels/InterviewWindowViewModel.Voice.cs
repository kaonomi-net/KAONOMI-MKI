using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// The audio analysis of an interview (🎚 Audio analysis: the pitch of the voice and how much it moves, its level, how
/// fast the words come, the pauses, over each recording and each paragraph of its transcription;
/// <see cref="VoiceAnalyzer"/>), and the results of every analysis (the voice, the emotions, the entities, the
/// ontology) kept in view: <see cref="Results"/>, shown as soon as an interview that has some is opened.
/// </summary>
public sealed partial class InterviewWindowViewModel
{
    /// <summary>The results of every analysis of the interview open, as Markdown (see <see cref="RefreshResults"/>).</summary>
    [ObservableProperty]
    private string _results = string.Empty;

    /// <summary>True when the interview open has results of an analysis (entities, emotions, voice).</summary>
    [ObservableProperty]
    private bool _hasResults;

    /// <summary>The panel on the right shows the results of the analyses (true) or the transcription (false).</summary>
    [ObservableProperty]
    private bool _showResults;

    /// <summary>Shows the results of the analyses, or the transcription (the two tabs of the panel on the right; the parameter: "results" or anything else).</summary>
    [RelayCommand]
    private void ShowTab(string? tab) => ShowResults = tab == "results";

    /// <summary>
    /// Writes <see cref="Results"/> again from what is known now; with <paramref name="reveal"/>, the results are put
    /// in view when there are some (an interview just opened, an analysis just done).
    /// </summary>
    private void RefreshResults(bool reveal = false)
    {
        HasResults = SummaryNote.Length > 0 || Entities.Count > 0 || _noteEmotions.Count > 0 || Recordings.Any(r => r.Voice is not null)
                     || Paragraphs.Any(p => p.Emotion.Length > 0 || p.Voice is not null);
        Results = Report(null, null, null);
        if (reveal)
            ShowResults = HasResults;
    }

    /// <summary>The voice in the report: each recording analysed as a whole, what stands out, then paragraph by paragraph.</summary>
    private void VoiceReport(StringBuilder text)
    {
        text.Append("## Voice (audio)\n\n");
        var analysed = Recordings.Where(r => r.Voice is not null).ToList();
        if (analysed.Count == 0)
        {
            text.Append("No recording analysed yet: 🎚 Audio analysis.\n\n");
            return;
        }
        foreach (var recording in analysed)
        {
            var voice = recording.Voice!;
            text.Append($"### {recording.FileName}\n\n");
            if (voice.PitchHz > 0)
                text.Append($"- **Pitch**: {voice.PitchHz:0} Hz on average, from {voice.PitchMinHz:0} to {voice.PitchMaxHz:0} Hz; it moves by ±{voice.PitchVariation:0.0} semitones ({PitchWord(voice.PitchVariation)})\n");
            else
                text.Append("- **Pitch**: no voiced sound found\n");
            text.Append($"- **Level**: {voice.LoudnessDb:0.0} dB on average, ±{voice.LoudnessVariation:0.0} dB\n");
            if (voice.WordsPerMinute > 0)
                text.Append($"- **Speed**: {voice.WordsPerMinute:0} words a minute over the recording, {voice.ArticulationRate:0} while speaking ({SpeedWord(voice.ArticulationRate)})\n");
            var total = voice.SpeechSeconds + voice.PauseSeconds;
            text.Append($"- **Speech and silence**: {InterviewParagraphEntry.Clock(voice.SpeechSeconds)} of speech, {voice.PauseCount} pause(s) for {InterviewParagraphEntry.Clock(voice.PauseSeconds)}"
                        + (total > 0 ? $" ({voice.PauseSeconds / total * 100:0}% of the time)" : string.Empty) + "\n");

            // What stands out among its paragraphs, against the recording as a whole.
            var paragraphs = Paragraphs.Where(p => p.AudioFile == recording.FileName && p.Voice is not null).OrderBy(p => p.Index).ToList();
            var timed = paragraphs.Where(p => p.Voice!.WordsPerMinute > 0 && p.EndSeconds - p.StartSeconds >= 3).ToList();
            var voiced = paragraphs.Where(p => p.Voice!.PitchHz > 0).ToList();
            if (timed.Count >= 3 || voiced.Count >= 3)
            {
                text.Append('\n');
                if (timed.Count >= 3)
                {
                    var fastest = timed.MaxBy(p => p.Voice!.WordsPerMinute)!;
                    var slowest = timed.MinBy(p => p.Voice!.WordsPerMinute)!;
                    text.Append($"- Fastest: **[{fastest.TimeLabel}]** {fastest.Voice!.WordsPerMinute:0} words/min — {Cut(fastest.Text, 110)}\n");
                    text.Append($"- Slowest: **[{slowest.TimeLabel}]** {slowest.Voice!.WordsPerMinute:0} words/min — {Cut(slowest.Text, 110)}\n");
                }
                if (voiced.Count >= 3)
                {
                    var highest = voiced.MaxBy(p => p.Voice!.PitchHz)!;
                    var moving = voiced.MaxBy(p => p.Voice!.PitchVariation)!;
                    var flattest = voiced.MinBy(p => p.Voice!.PitchVariation)!;
                    var loudest = paragraphs.MaxBy(p => p.Voice!.LoudnessDb)!;
                    text.Append($"- Highest voice: **[{highest.TimeLabel}]** {highest.Voice!.PitchHz:0} Hz — {Cut(highest.Text, 110)}\n");
                    text.Append($"- Most moving voice: **[{moving.TimeLabel}]** ±{moving.Voice!.PitchVariation:0.0} semitones — {Cut(moving.Text, 110)}\n");
                    text.Append($"- Flattest voice: **[{flattest.TimeLabel}]** ±{flattest.Voice!.PitchVariation:0.0} semitones — {Cut(flattest.Text, 110)}\n");
                    text.Append($"- Loudest: **[{loudest.TimeLabel}]** {loudest.Voice!.LoudnessDb:0.0} dB — {Cut(loudest.Text, 110)}\n");
                }
            }
            if (paragraphs.Count > 0)
            {
                text.Append("\nBy paragraph:\n\n");
                foreach (var paragraph in paragraphs)
                {
                    var voice0 = paragraph.Voice!;
                    text.Append($"- **[{paragraph.TimeLabel}]** {voice0.Label}, {voice0.LoudnessDb:0.0} dB"
                                + (voice0.PauseCount > 0 ? $", {voice0.PauseCount} pause(s)" : string.Empty)
                                + (paragraph.Emotion.Length > 0 ? $" · {paragraph.EmotionLabel}" : string.Empty)
                                + $" — {Cut(paragraph.Text, 90)}\n");
                }
            }
            text.Append('\n');
        }
    }

    private static string PitchWord(double semitones) => semitones switch
    {
        < 1.5 => "a flat, monotone voice",
        < 3 => "an even voice",
        < 4.5 => "a lively voice",
        _ => "a very expressive or agitated voice"
    };

    private static string SpeedWord(double wordsPerMinute) => wordsPerMinute switch
    {
        < 110 => "slow",
        < 160 => "a usual speed",
        < 200 => "fast",
        _ => "very fast"
    };

    /// <summary>
    /// 🎚 Audio analysis of the recordings of the interview (of <paramref name="recording"/> alone when one is given):
    /// the pitch of the voice and its variation, its level, the speed of the words and the pauses, over the whole
    /// recording and over each paragraph of its transcription. The figures are stored with the recordings and the
    /// paragraphs, and shown with the other results.
    /// </summary>
    [RelayCommand]
    private async Task AnalyzeAudioAsync(InterviewRecordingEntry? recording)
    {
        if (IsAnalyzing)
            return;
        if (IsRecording || IsTranscribing)
        {
            SetStatus(IsRecording ? "Stop the recording first (⏹), then run the audio analysis" : "The transcription is running: wait for it, then run the audio analysis", true);
            return;
        }
        var files = recording is not null ? [recording] : Recordings.ToList();
        if (files.Count == 0)
        {
            SetStatus("Audio analysis: no recording yet (⏺ Record first)", true);
            return;
        }
        using var cancel = new CancellationTokenSource();
        _analysisCancel = cancel;
        var token = cancel.Token;
        IsAnalyzing = true;
        var started = DateTime.Now;
        var done = 0;
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(file.Path))
                {
                    SetStatus($"The recording is not there any more: {file.Path}", true);
                    continue;
                }
                var name = file.FileName;
                var paragraphs = Paragraphs.Where(p => p.AudioFile == name).OrderBy(p => p.Index).ToList();
                var spans = paragraphs.Select(p => (p.StartSeconds, p.EndSeconds, p.Text)).ToList();
                Step($"Audio: reading the voice of {name} (pitch, level, pauses)…");
                var percent = -1;
                var (whole, each) = await Task.Run(() => VoiceAnalyzer.Analyze(file.Path, spans, share =>
                {
                    // Told a few times only: from the thread that reads the file.
                    var now = (int)(share * 100) / 5 * 5;
                    if (now == percent)
                        return;
                    percent = now;
                    AnalysisStep = $"Audio: reading the voice of {name} (pitch, level, pauses)… {now}%";
                }, token), token);

                // New entries in place of the former ones: the lists show their figures.
                for (var i = 0; i < paragraphs.Count; i++)
                {
                    var at = Paragraphs.IndexOf(paragraphs[i]);
                    if (at < 0)
                        continue;
                    var copy = JsonSerializer.Deserialize<InterviewParagraphEntry>(JsonSerializer.Serialize(paragraphs[i]))!;
                    copy.Voice = each[i];
                    Paragraphs[at] = copy;
                }
                var index = Recordings.IndexOf(file);
                if (index >= 0)
                {
                    var copy = JsonSerializer.Deserialize<InterviewRecordingEntry>(JsonSerializer.Serialize(file))!;
                    copy.Voice = whole;
                    Recordings[index] = copy;
                }
                if (paragraphs.Count > 0)
                {
                    Step($"Audio: storing the figures of {name}…");
                    var result = await _server.SaveInterviewTranscriptAsync(new InterviewTranscriptRequest
                    {
                        InterviewId = InterviewId,
                        AudioFile = name,
                        Paragraphs = Paragraphs.Where(p => p.AudioFile == name).OrderBy(p => p.Index).ToList()
                    }, token);
                    if (result.ReturnedId is null or "-1")
                        throw new HttpRequestException("the figures of " + name + " were not stored: " + (result.Message ?? "no answer"));
                }
                done++;
            }
            if (done == 0)
                return;
            Step("Audio: saving the interview…");
            if (!await SaveCoreAsync(quiet: true))
                return;
            RefreshResults(reveal: true);
            var seconds = (DateTime.Now - started).TotalSeconds;
            SetStatus($"🎚 Audio analysis done in {seconds:0} s: {done} recording(s); its figures are in the results on the right", false);
            ActivityLog.Info("Interview", $"Audio analysis of {InterviewId}: {done} recording(s) in {seconds:0} s");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            RefreshResults();
            SetStatus("Audio analysis cancelled", true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or JsonException)
        {
            RefreshResults();
            SetStatus("Audio analysis: " + ex.Message, true);
            ActivityLog.Error("Interview", "Audio analysis: " + ex.Message);
        }
        finally
        {
            _analysisCancel = null;
            IsAnalyzing = false;
            AnalysisStep = string.Empty;
        }
    }
}
