using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// The entities of an interview and its deep text analysis. The entities are read by the NER of the server (the one of
/// <c>POST /ner</c>) each time the interview is saved, in its notes and in its transcription, and stored with it.
/// 🧠 Deep analysis asks the model of the chat for the emotions of each paragraph (stored with the paragraphs) and adds
/// to the ontology the people, organisations and places of the interview it does not know yet.
/// </summary>
public sealed partial class InterviewWindowViewModel
{
    /// <summary>The entities the NER of the server found in the notes and the transcription, the most written first (read again at each save).</summary>
    public ObservableCollection<InterviewEntityEntry> Entities { get; } = [];

    /// <summary>The entities in a few words, under the person ("12 entities: 4 PERSON, 3 ORG…").</summary>
    [ObservableProperty]
    private string _entitiesSummary = string.Empty;

    private List<InterviewNoteEmotionEntry> _noteEmotions = [];
    private DateTimeOffset? _analyzedAt;
    private CancellationTokenSource? _analysisCancel;

    /// <summary>What the deep text analysis said of the interview as a whole (Markdown); empty: never run.</summary>
    [ObservableProperty]
    private string _analysis = string.Empty;

    /// <summary>True while the deep text analysis runs (the views show their waiting animation).</summary>
    [ObservableProperty]
    private bool _isAnalyzing;

    /// <summary>What the deep text analysis is doing, for the waiting popup.</summary>
    [ObservableProperty]
    private string _analysisStep = string.Empty;

    /// <summary>Raised when a report is ready to be shown (the end of the deep text analysis, or 📊 Report): its Markdown.</summary>
    public event Action<string>? ReportReady;

    private void SetEntities(IEnumerable<InterviewEntityEntry>? entities)
    {
        Entities.Clear();
        foreach (var entity in entities ?? [])
            Entities.Add(entity);
        EntitiesSummary = Entities.Count == 0
            ? string.Empty
            : $"{Entities.Count} entities: " + string.Join(", ", Entities.GroupBy(e => e.Type).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Count()} {g.Key}"));
    }

    /// <summary>The emotions the analysis may name (its answers are held to this list).</summary>
    private static readonly string[] EmotionNames = ["joy", "sadness", "anger", "fear", "surprise", "disgust", "trust", "anticipation", "neutral"];

    /// <summary>The types of the entities added to the ontology, and the default root of the ontology each goes under.</summary>
    private static readonly (string Type, string Root)[] OntologyRoots =
        [("PERSON", "persons"), ("ORGANISATION", "organisation"), ("LOCATION", "location"), ("MATERIAL", "material"), ("MISC", "misc")];

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)|\]\([^)]*\)|[*_#`>\[\]|~]+|<[^>]+>")]
    private static partial Regex NoteMarkup();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// The paragraphs of the notes as plain text (the Markdown marks and the pictures taken away), without the
    /// transcription and the report put in them by ⤵ Insert (the transcription is analysed from its own paragraphs).
    /// </summary>
    private static List<string> NoteParagraphs(string content)
    {
        var paragraphs = new List<string>();
        var inReport = false;
        foreach (var block in content.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (block.StartsWith("# Analyses · ", StringComparison.Ordinal) || block.StartsWith("# Deep text analysis", StringComparison.Ordinal))
                inReport = true;
            else if (block.StartsWith("# ", StringComparison.Ordinal))
                inReport = false;
            if (inReport || block.StartsWith("**[", StringComparison.Ordinal) || block.StartsWith("## Transcription", StringComparison.Ordinal))
                continue;
            var text = Spaces().Replace(NoteMarkup().Replace(block, " "), " ").Trim();
            if (text.Count(char.IsLetter) >= 12)
                paragraphs.Add(text);
        }
        return paragraphs;
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd() + "…";

    /// <summary>The English name of the language of the interview, for the prompts.</summary>
    private string LanguageName => Language switch
    {
        "fr" => "French",
        "de" => "German",
        "en" => "English",
        "ja" => "Japanese",
        "nl" => "Dutch",
        _ => "the language of the text"
    };

    private void Step(string text)
    {
        AnalysisStep = text;
        SetStatus("🧠 " + text, false);
    }

    /// <summary>Stops the deep text analysis running (what it stored so far stays).</summary>
    [RelayCommand]
    private void CancelAnalysis() => _analysisCancel?.Cancel();

    /// <summary>
    /// 🧠 Deep text analysis of the whole interview (its notes and its transcription), by the model of the chat: the
    /// interview is saved first (the NER of the server reads its entities), then the emotions of each paragraph are
    /// evaluated, then the people, organisations and places named in it that the ontology does not know yet are added
    /// to the ontology (under its default roots, with a description from the interview). The emotions are stored with
    /// the paragraphs, the report with the interview, and the report is shown.
    /// </summary>
    [RelayCommand]
    private async Task DeepAnalysisAsync()
    {
        if (IsAnalyzing)
            return;
        if (IsRecording || IsTranscribing)
        {
            SetStatus(IsRecording ? "Stop the recording first (⏹), then run the deep text analysis" : "The transcription is running: wait for it, then run the deep text analysis", true);
            return;
        }
        if (_ollama is null || _model?.Invoke() is not { Length: > 0 } model)
        {
            SetStatus("Deep text analysis: no model of the chat is available (Ollama)", true);
            return;
        }
        var notes = NoteParagraphs(Content);
        if (notes.Count == 0 && Paragraphs.Count == 0)
        {
            SetStatus("Deep text analysis: nothing to analyse yet (write notes, or record and transcribe the interview)", true);
            return;
        }
        using var cancel = new CancellationTokenSource();
        _analysisCancel = cancel;
        var token = cancel.Token;
        IsAnalyzing = true;
        var started = DateTime.Now;
        try
        {
            Step("Saving the interview and reading its entities (NER of the server)…");
            if (!await SaveCoreAsync(quiet: true))
                return;

            // 1. The emotions, a few paragraphs at a time.
            var noteEmotions = new List<InterviewNoteEmotionEntry>();
            var items = notes.Select((text, i) => (Note: i, Paragraph: (InterviewParagraphEntry?)null, Text: text))
                .Concat(Paragraphs.Select(p => (Note: -1, Paragraph: (InterviewParagraphEntry?)p, Text: p.Text)))
                .ToList();
            var analysed = new Dictionary<InterviewParagraphEntry, (string Emotion, int Intensity, List<InterviewEmotionEntry> Emotions)>();
            for (var at = 0; at < items.Count;)
            {
                token.ThrowIfCancellationRequested();
                var batch = new List<(int Note, InterviewParagraphEntry? Paragraph, string Text)>();
                var size = 0;
                while (at < items.Count && batch.Count < 10 && (batch.Count == 0 || size + Math.Min(items[at].Text.Length, 1200) <= 5000))
                {
                    size += Math.Min(items[at].Text.Length, 1200);
                    batch.Add(items[at++]);
                }
                Step($"Emotions: paragraphs {at - batch.Count + 1} to {at} of {items.Count} ({model})…");
                var answers = await AskEmotionsAsync(model, batch.Select(b => b.Text).ToList(), token);
                for (var i = 0; i < batch.Count; i++)
                {
                    if (!answers.TryGetValue(i + 1, out var answer))
                        continue;
                    if (batch[i].Paragraph is { } paragraph)
                        analysed[paragraph] = answer;
                    else
                        noteEmotions.Add(new InterviewNoteEmotionEntry
                        {
                            Index = batch[i].Note,
                            Excerpt = Cut(batch[i].Text, 160),
                            Emotion = answer.Emotion,
                            Intensity = answer.Intensity,
                            Emotions = answer.Emotions
                        });
                }
            }
            // New entries in place of the former ones: the list shows their emotion.
            for (var i = 0; i < Paragraphs.Count; i++)
            {
                if (!analysed.TryGetValue(Paragraphs[i], out var answer))
                    continue;
                var copy = JsonSerializer.Deserialize<InterviewParagraphEntry>(JsonSerializer.Serialize(Paragraphs[i]))!;
                copy.Emotion = answer.Emotion;
                copy.EmotionIntensity = answer.Intensity;
                copy.Emotions = answer.Emotions;
                Paragraphs[i] = copy;
            }
            _noteEmotions = noteEmotions;

            // 2. The ontology.
            Step($"Ontology: choosing the entities of the interview to add ({model})…");
            var (created, known) = await GenerateOntologyAsync(model, notes, token);

            // 3. Stored: the emotions with the paragraphs, the report with the interview.
            Step("Storing the emotions of the paragraphs and the report…");
            foreach (var file in Paragraphs.Select(p => p.AudioFile).Distinct().ToList())
            {
                var result = await _server.SaveInterviewTranscriptAsync(new InterviewTranscriptRequest
                {
                    InterviewId = InterviewId,
                    AudioFile = file,
                    Paragraphs = TranscriptOf(file)
                }, token);
                if (result.ReturnedId is null or "-1")
                    throw new HttpRequestException("the emotions of " + file + " were not stored: " + (result.Message ?? "no answer"));
            }
            _analyzedAt = DateTimeOffset.Now;
            Analysis = Report(model, created, known);
            if (!await SaveCoreAsync(quiet: true))
                return;
            var seconds = (DateTime.Now - started).TotalSeconds;
            SetStatus($"🧠 Deep text analysis done in {seconds:0} s: {analysed.Count + noteEmotions.Count} paragraph(s) with their emotions, {created.Count} entit{(created.Count == 1 ? "y" : "ies")} added to the ontology", false);
            ActivityLog.Info("Interview", $"Deep text analysis of {InterviewId} by {model}: {analysed.Count + noteEmotions.Count} paragraph(s), {created.Count} ontology entities created, {seconds:0} s");
            RefreshResults(reveal: true);
            _ = UpdateSummaryAsync();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            SetStatus("Deep text analysis cancelled (the entities already added to the ontology stay)", true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            SetStatus("Deep text analysis: " + ex.Message, true);
            ActivityLog.Error("Interview", "Deep text analysis: " + ex.Message);
        }
        finally
        {
            _analysisCancel = null;
            IsAnalyzing = false;
            AnalysisStep = string.Empty;
        }
    }

    /// <summary>
    /// The emotions of <paramref name="texts"/> by <paramref name="model"/>, its answer held to a schema: for each
    /// paragraph (numbered from 1) the emotion that dominates, its intensity from 0 to 10, and every emotion present.
    /// </summary>
    private async Task<Dictionary<int, (string Emotion, int Intensity, List<InterviewEmotionEntry> Emotions)>> AskEmotionsAsync(
        string model, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var names = new JsonArray(EmotionNames.Select(n => (JsonNode)n).ToArray());
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["paragraphs"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["id"] = new JsonObject { ["type"] = "integer" },
                            ["emotion"] = new JsonObject { ["type"] = "string", ["enum"] = names.DeepClone() },
                            ["intensity"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 10 },
                            ["emotions"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["name"] = new JsonObject { ["type"] = "string", ["enum"] = names.DeepClone() },
                                        ["intensity"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 10 }
                                    },
                                    ["required"] = new JsonArray("name", "intensity")
                                }
                            }
                        },
                        ["required"] = new JsonArray("id", "emotion", "intensity", "emotions")
                    }
                }
            },
            ["required"] = new JsonArray("paragraphs")
        };
        var prompt = new StringBuilder("Paragraphs of an interview (notes of the interviewer, and what was said, transcribed from a recording):\n\n");
        for (var i = 0; i < texts.Count; i++)
            prompt.Append($"[{i + 1}] {Cut(texts[i], 1200)}\n\n");
        var answer = await _ollama!.ChatJsonAsync(model,
            "You evaluate the emotions expressed in each paragraph of an interview, from its words only. For each paragraph give its "
            + "number (id), the emotion that dominates, the intensity of that emotion from 0 (absent) to 10 (overwhelming), and the list "
            + "of every emotion present with its intensity (the dominant one included). Use only these emotions: "
            + string.Join(", ", EmotionNames) + ". A factual paragraph without feeling is neutral, with a low intensity. Answer for every "
            + "paragraph, in JSON.",
            prompt.ToString(), schema, cancellationToken);
        var found = new Dictionary<int, (string, int, List<InterviewEmotionEntry>)>();
        using var document = JsonDocument.Parse(answer);
        if (!document.RootElement.TryGetProperty("paragraphs", out var list) || list.ValueKind != JsonValueKind.Array)
            return found;
        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) || number < 1 || number > texts.Count)
                continue;
            var emotion = Text(item, "emotion");
            if (!EmotionNames.Contains(emotion))
                continue;
            var intensity = IntensityOf(item);
            var emotions = new List<InterviewEmotionEntry>();
            if (item.TryGetProperty("emotions", out var all) && all.ValueKind == JsonValueKind.Array)
                foreach (var one in all.EnumerateArray())
                {
                    var name = Text(one, "name");
                    var level = IntensityOf(one);
                    if (EmotionNames.Contains(name) && level > 0 && emotions.All(x => x.Name != name))
                        emotions.Add(new InterviewEmotionEntry { Name = name, Intensity = level });
                }
            if (emotions.Count == 0)
                emotions.Add(new InterviewEmotionEntry { Name = emotion, Intensity = intensity });
            found[number] = (emotion, intensity, emotions.OrderByDescending(x => x.Intensity).ToList());
        }
        return found;
    }

    /// <summary>The text of the property <paramref name="name"/> of <paramref name="item"/>, trimmed ("" when it is not a text).</summary>
    private static string Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? string.Empty).Trim()
            : string.Empty;

    /// <summary>The <c>intensity</c> of <paramref name="item"/>, from 0 to 10.</summary>
    private static int IntensityOf(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("intensity", out var value) && value.ValueKind == JsonValueKind.Number
            ? (int)Math.Clamp(Math.Round(value.GetDouble()), 0, 10)
            : 0;

    /// <summary>
    /// Adds to the ontology the people, organisations and places of the interview it does not know yet: the entities
    /// the NER found that are not in the ontology (and the person interviewed) are given to <paramref name="model"/>
    /// with the sentences they are written in; it keeps the real ones, names each, gives its type and a description
    /// from the interview. Each is created under the default root of its type, marked RESTRICTED, tagged INTERVIEW.
    /// A name the model gives that is written nowhere in the interview is left out. Returns the entities created, and
    /// the names left out because the ontology already has them.
    /// </summary>
    private async Task<(List<OntologyEntry> Created, List<string> Known)> GenerateOntologyAsync(string model, List<string> notes, CancellationToken cancellationToken)
    {
        var created = new List<OntologyEntry>();
        var known = new List<string>();
        var candidates = Entities
            .Where(e => !e.IsKnown && e.Type is "PERSON" or "ORG" or "LOC" or "ORGANISATION" or "LOCATION" or "MISC" && e.Text.Count(char.IsLetter) >= 2)
            .Take(60)
            .Select(e => (e.Text, e.Type))
            .ToList();
        if (PersonName.Length > 0 && candidates.All(c => !c.Text.Equals(PersonName, StringComparison.OrdinalIgnoreCase)))
            candidates.Insert(0, (PersonName, "PERSON"));
        if (candidates.Count == 0)
            return (created, known);

        var corpus = notes.Concat(Paragraphs.Select(p => p.Text)).ToList();
        var prompt = new StringBuilder("Entities found by a named entity recogniser in an interview, each with sentences it is written in:\n\n");
        foreach (var (text, type) in candidates)
        {
            prompt.Append($"- {text} ({type})");
            if (text.Equals(PersonName, StringComparison.OrdinalIgnoreCase))
                prompt.Append(" — the person interviewed");
            prompt.Append('\n');
            foreach (var sentence in corpus.Where(c => c.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(2))
            {
                var from = Math.Max(0, sentence.IndexOf(text, StringComparison.OrdinalIgnoreCase) - 140);
                prompt.Append($"    \"{(from > 0 ? "…" : string.Empty)}{Cut(sentence[from..], 320)}\"\n");
            }
        }
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["entities"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(OntologyRoots.Select(r => (JsonNode)r.Type).ToArray()) },
                            ["description"] = new JsonObject { ["type"] = "string" },
                            ["synonyms"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                        },
                        ["required"] = new JsonArray("name", "type", "description", "synonyms")
                    }
                }
            },
            ["required"] = new JsonArray("entities")
        };
        var answer = await _ollama!.ChatJsonAsync(model,
            "You build an ontology from an interview. From the list given, keep only the real, specific named entities: a person, an "
            + "organisation (company, institution, team), a place, a material thing or product, or another named thing (MISC). Leave out "
            + "common nouns, fragments, mistakes of the transcription and duplicates. For each entity kept give: its name as it should be "
            + "written (taken from the list, never invented), its type among PERSON, ORGANISATION, LOCATION, MATERIAL, MISC, a description "
            + $"of one or two sentences in {LanguageName} saying only what the interview says about it, and its synonyms: the other "
            + "entries of the list that name the same entity. Answer in JSON.",
            prompt.ToString(), schema, cancellationToken);

        using var document = JsonDocument.Parse(answer);
        if (!document.RootElement.TryGetProperty("entities", out var list) || list.ValueKind != JsonValueKind.Array)
            return (created, known);

        // What the ontology already has: its names and synonyms (the deleted ones too: they are not made again).
        var nodes = await _server.GetLastOntologiesAsync("*", 10000, cancellationToken);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.Name))
                existing.Add(node.Name.Trim());
            foreach (var synonym in node.Synonyms ?? [])
                if (!string.IsNullOrWhiteSpace(synonym))
                    existing.Add(synonym.Trim());
        }
        var ids = nodes.Select(n => n.Id).ToHashSet();
        var date = Recordings.FirstOrDefault()?.RecordedAt.ToLocalTime().ToString("yyyy-MM-dd") ?? DateTime.Now.ToString("yyyy-MM-dd");

        foreach (var item in list.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Text(item, "name");
            var type = Text(item, "type");
            var description = Text(item, "description");
            var synonyms = item.TryGetProperty("synonyms", out var sy) && sy.ValueKind == JsonValueKind.Array
                ? sy.EnumerateArray().Select(s => s.ValueKind == JsonValueKind.String ? (s.GetString() ?? string.Empty).Trim() : string.Empty)
                    .Where(s => s.Length > 0 && !s.Equals(name, StringComparison.OrdinalIgnoreCase) && candidates.Any(c => c.Text.Equals(s, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            var root = OntologyRoots.FirstOrDefault(r => r.Type == type);
            // Never an entity the interview does not write: a name made up by the model is left out.
            var written = name.Equals(PersonName, StringComparison.OrdinalIgnoreCase) || corpus.Any(c => c.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (name.Length < 2 || root.Root is null || !written || created.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (existing.Contains(name) || synonyms.Any(existing.Contains))
            {
                known.Add(name);
                continue;
            }
            // The default root of its type, created when the ontology was never opened.
            var rootId = "ontology-root-" + root.Root;
            if (!ids.Contains(rootId))
            {
                var made = await _server.CreateOntologyAsync(new OntologyEntry
                {
                    Id = rootId,
                    Name = root.Root,
                    EntityType = root.Type,
                    Description = $"Default root of the ontology: {root.Root}.",
                    IsOntologyClass = true,
                    Confidentiality = OntologyWindowViewModel.DefaultConfidentiality,
                    IsDeleted = false,
                    Date = DateTime.Now,
                    DateCreated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    AppName = "KAONOMI.Chat",
                    MachineName = Environment.MachineName,
                    UserName = Environment.UserName
                }, cancellationToken);
                if (!made.IsSuccess)
                    throw new HttpRequestException($"the root \"{root.Root}\" of the ontology was not created: {made.Message}");
                ids.Add(rootId);
            }
            var example = corpus.FirstOrDefault(c => c.Contains(name, StringComparison.OrdinalIgnoreCase));
            var entry = new OntologyEntry
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                EntityType = root.Type,
                Description = description,
                Synonyms = synonyms,
                SentencesExemples = example is null ? [] : [Cut(example, 300)],
                Tags = ["INTERVIEW"],
                Comment = $"Created by the deep text analysis of the interview \"{Label}\" of {date} ({InterviewId}), model {model}",
                NodeParentId = rootId,
                IsOntologyClass = false,
                // What an interview says of someone is not public.
                Confidentiality = "RESTRICTED(NR)",
                IsDeleted = false,
                Date = DateTime.Now,
                DateCreated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                AppName = "KAONOMI.Chat",
                // An entity made from the interview is of its project.
                ProjectId = ProjectId,
                MachineName = Environment.MachineName,
                UserName = Environment.UserName
            };
            Step($"Ontology: adding {name} ({root.Type})…");
            var result = await _server.CreateOntologyAsync(entry, cancellationToken);
            if (!result.IsSuccess)
            {
                ActivityLog.Error("Interview", $"Ontology entity \"{name}\" not created: {result.Message}");
                continue;
            }
            created.Add(entry);
            existing.Add(name);
            foreach (var synonym in synonyms)
                existing.Add(synonym);
            ActivityLog.Info("Interview", $"Ontology entity created from the interview {InterviewId}: \"{name}\" ({root.Type})");
        }
        return (created, known);
    }

    /// <summary>The report of the interview as Markdown: the emotions (overall, then paragraph by paragraph), the entities of the NER, the ontology.</summary>
    private string Report(string? model, IReadOnlyList<OntologyEntry>? created, IReadOnlyList<string>? known)
    {
        var text = new StringBuilder($"# Analyses · {Label}\n\n");
        text.Append(_analyzedAt is { } at ? $"Deep text analysis of {at.ToLocalTime():yyyy-MM-dd HH:mm}" : "No deep text analysis yet");
        if (model is not null)
            text.Append($" · {model}");
        text.Append("\n\n");
        SummaryReport(text);
        VoiceReport(text);
        text.Append("## Emotions\n\n");
        var all = _noteEmotions.Select(n => (n.Emotion, n.Intensity))
            .Concat(Paragraphs.Where(p => p.Emotion.Length > 0).Select(p => (p.Emotion, Intensity: p.EmotionIntensity))).ToList();
        if (all.Count == 0)
            text.Append("No paragraph analysed yet: 🧠 Deep analysis.\n\n");
        else
        {
            foreach (var group in all.GroupBy(a => a.Emotion).OrderByDescending(g => g.Count()))
                text.Append($"- {InterviewEmotionEntry.Icon(group.Key)} **{group.Key}**: {group.Count()} paragraph(s), mean intensity {group.Average(a => a.Intensity):0.0}/10\n");
            if (_noteEmotions.Count > 0)
            {
                text.Append("\n### Notes\n\n");
                foreach (var note in _noteEmotions.OrderBy(n => n.Index))
                    text.Append($"- **Note {note.Index + 1}** {InterviewEmotionEntry.Icon(note.Emotion)} {note.Emotion} {note.Intensity}/10 — {note.Excerpt}\n");
            }
            if (Paragraphs.Any(p => p.Emotion.Length > 0))
            {
                text.Append("\n### Transcription\n\n");
                foreach (var paragraph in Paragraphs.Where(p => p.Emotion.Length > 0))
                    text.Append($"- **[{paragraph.TimeLabel}]** {paragraph.EmotionLabel} — {Cut(paragraph.Text, 160)}\n");
            }
            text.Append('\n');
        }
        text.Append("## Entities (NER)\n\n");
        if (Entities.Count == 0)
            text.Append("No entity found (they are read each time the interview is saved).\n\n");
        else
        {
            foreach (var group in Entities.GroupBy(e => e.Type).OrderByDescending(g => g.Sum(e => e.Count)))
                text.Append($"- **{group.Key}**: {string.Join(", ", group.Take(40).Select(e => e.Label + (e.IsKnown ? " ✓" : string.Empty)))}\n");
            text.Append("\n✓: an entity of the ontology.\n\n");
        }
        if (created is not null)
        {
            text.Append("## Ontology\n\n");
            if (created.Count == 0)
                text.Append("No entity added.\n");
            foreach (var entry in created)
                text.Append($"- **{entry.Name}** ({entry.EntityType}): {entry.Description}\n");
            if (known is { Count: > 0 })
                text.Append($"\nAlready in the ontology: {string.Join(", ", known)}.\n");
        }
        else if (Analysis.IndexOf("## Ontology", StringComparison.Ordinal) is >= 0 and var ontology)
        {
            // What the last deep text analysis added to the ontology, as it was stored with the interview.
            text.Append(Analysis[ontology..]);
        }
        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>📊 Report: every result known now (the voice, the emotions, the entities, the ontology) in a window.</summary>
    [RelayCommand]
    private void ShowReport() => ReportReady?.Invoke(Report(null, null, null));

    /// <summary>Puts <paramref name="markdown"/> (the report) at the end of the notes.</summary>
    public void InsertReport(string markdown)
    {
        var notes = Content.TrimEnd();
        Content = (notes.Length > 0 ? notes + "\n\n" : string.Empty) + markdown.Trim() + "\n";
        SetStatus("The report was added at the end of the notes", false);
    }
}
