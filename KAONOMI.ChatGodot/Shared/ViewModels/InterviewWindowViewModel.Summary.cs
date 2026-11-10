using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// The summary note of an interview: each time the interview is saved, the model of the chat writes a summary of its
/// notes and of its transcription, stored with the interview (<c>summaryNote</c>) and shown first among its results.
/// The save itself does not wait for it: the note is written after, in the background, then stored by a second,
/// quiet save. It is written again only when the text changed since the last one.
/// </summary>
public sealed partial class InterviewWindowViewModel
{
    /// <summary>Under this many letters there is nothing to sum up.</summary>
    private const int SummaryMinimumLetters = 200;

    /// <summary>What the model reads at most: the start and the end of a longer interview.</summary>
    private const int SummaryMaximumLength = 14000;

    /// <summary>The summary note of the interview open (Markdown); empty: none yet.</summary>
    [ObservableProperty]
    private string _summaryNote = string.Empty;

    /// <summary>True while the summary note is being written by the model.</summary>
    [ObservableProperty]
    private bool _isSummarising;

    /// <summary>The results say that the note is being written, then show it.</summary>
    partial void OnIsSummarisingChanged(bool value) => RefreshResults();

    private DateTimeOffset? _summaryNoteAt;
    private string _summaryNoteModel = string.Empty;
    private string _summaryNoteSource = string.Empty;
    private CancellationTokenSource? _summaryCancel;

    /// <summary>What the note sums up: the notes paragraph by paragraph, then the transcription, each paragraph after its time.</summary>
    private string SummarySource()
    {
        var text = new StringBuilder();
        var notes = NoteParagraphs(Content);
        if (notes.Count > 0)
        {
            text.Append("NOTES OF THE INTERVIEWER\n\n");
            foreach (var note in notes)
                text.Append(note).Append("\n\n");
        }
        if (Paragraphs.Count > 0)
        {
            text.Append("TRANSCRIPTION OF THE RECORDING\n\n");
            foreach (var paragraph in Paragraphs)
                text.Append($"[{paragraph.TimeLabel}] {paragraph.Text}\n\n");
        }
        return text.ToString().TrimEnd();
    }

    private static string Fingerprint(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Gives up the note being written (another interview is opened, the window closes).</summary>
    private void CancelSummary()
    {
        _summaryCancel?.Cancel();
        _summaryCancel = null;
        IsSummarising = false;
    }

    /// <summary>
    /// After a save: writes the summary note when the text changed since the last one, then stores it with a quiet
    /// save (kept for the next save when the interview has changes not saved, or is busy).
    /// </summary>
    private async Task UpdateSummaryAsync()
    {
        if (_ollama is null || _model?.Invoke() is not { Length: > 0 } model)
            return;
        var source = SummarySource();
        if (source.Count(char.IsLetter) < SummaryMinimumLetters)
            return;
        var fingerprint = Fingerprint(source);
        if (fingerprint == _summaryNoteSource && SummaryNote.Length > 0)
            return;
        // One at a time: the one being written is for an older text.
        _summaryCancel?.Cancel();
        using var cancel = new CancellationTokenSource();
        _summaryCancel = cancel;
        var interview = InterviewId;
        IsSummarising = true;
        try
        {
            if (source.Length > SummaryMaximumLength)
                source = source[..(SummaryMaximumLength * 2 / 3)] + "\n\n[…]\n\n" + source[^(SummaryMaximumLength / 3)..];
            var person = PersonName.Length > 0 ? PersonName : "the person interviewed";
            var answer = await _ollama.ChatAsync(model,
                $"You write the summary note of an interview, in {LanguageName}, for someone who has not read it. Write only what the text "
                + "says: never add a fact, a name, a date or a judgement that is not in it; what is unclear or contradictory is said to be so. "
                + "Format, in Markdown, without a title: first two or three sentences saying who was interviewed and what the interview is "
                + "about; then a list of 4 to 8 points with the facts that matter (people, organisations, places, dates, figures), in the "
                + "order they come; then, when there are some, a last line starting with \"À vérifier :\" or its equivalent in the language, "
                + "with what stays unclear. No introduction, no conclusion, no comment about the task.",
                $"Interview of {person}.\n\n\"\"\"\n{source}\n\"\"\"", cancel.Token);
            // Another interview was opened meanwhile: the note is not for it.
            if (cancel.IsCancellationRequested || InterviewId != interview)
                return;
            var note = LatexText.Convert(answer).Trim();
            if (note.StartsWith("```", StringComparison.Ordinal))
                note = System.Text.RegularExpressions.Regex.Replace(note, @"^```[^\n]*\n|\n?```$", string.Empty).Trim();
            if (note.Length == 0)
                return;
            SummaryNote = note;
            _summaryNoteAt = DateTimeOffset.Now;
            _summaryNoteModel = model;
            _summaryNoteSource = fingerprint;
            RefreshResults(reveal: !ShowResults && !HasResults);
            ActivityLog.Info("Interview", $"Summary note of {interview} written by {model} ({note.Length} chars)");
            if (!IsDirty && !IsRecording && !IsTranscribing && !IsAnalyzing)
            {
                var error = IsError;
                var said = Status;
                if (await SaveCoreAsync(quiet: true) && !error)
                    SetStatus((said.Length > 0 ? said + " · " : string.Empty) + "📝 summary note written", false);
            }
            else
                // Stored with the next save.
                IsDirty = true;
        }
        catch (OperationCanceledException)
        {
            // Given up for a newer text, or another interview.
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            // The interview is saved all the same: only its note is missing.
            ActivityLog.Error("Interview", "Summary note: " + ex.Message);
            if (!IsError)
                SetStatus("The interview is saved, but its summary note could not be written: " + ex.Message, true);
        }
        finally
        {
            if (ReferenceEquals(_summaryCancel, cancel))
            {
                _summaryCancel = null;
                IsSummarising = false;
            }
        }
    }

    /// <summary>The summary note in the report, first.</summary>
    private void SummaryReport(StringBuilder text)
    {
        text.Append("## Summary\n\n");
        if (SummaryNote.Length > 0)
        {
            text.Append(SummaryNote).Append("\n\n");
            if (_summaryNoteAt is { } at)
                text.Append($"*Written {at.ToLocalTime():yyyy-MM-dd HH:mm}" + (_summaryNoteModel.Length > 0 ? $" by {_summaryNoteModel}" : string.Empty)
                            + (IsSummarising ? "; being written again…" : string.Empty) + "*\n\n");
        }
        else
            text.Append(IsSummarising ? "The summary note is being written…\n\n" : "No summary note yet: it is written when the interview is saved (with enough text).\n\n");
    }
}
