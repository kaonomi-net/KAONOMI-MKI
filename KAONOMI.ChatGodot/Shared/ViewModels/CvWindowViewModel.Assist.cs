using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// The model of the chat in the CVs. ✨ writes a text of the CV open from what the CV says (its summary, the
/// description of an experience, an education or a creation; what is already typed there is taken as notes and
/// rewritten), or proposes its skills; ↶ puts back the text it replaced. ⧉ Clone copies the CV into a new one and,
/// when an adaptation is asked in its popup, has the model rewrite the wording of the copy for it: the people, the
/// employers, the schools and the dates are the ones of the original, never the ones of the model.
/// </summary>
public sealed partial class CvWindowViewModel
{
    private CancellationTokenSource? _generation;
    private Action? _undo;

    /// <summary>What the texts are written for (optional): the job aimed at, the tone, the language. Given to the model with each ✨.</summary>
    [ObservableProperty]
    private string _guidance = string.Empty;

    /// <summary>True while the model writes (a waiting popup is shown).</summary>
    [ObservableProperty]
    private bool _isGenerating;

    /// <summary>What the model is writing.</summary>
    [ObservableProperty]
    private string _generationStep = string.Empty;

    /// <summary>True when ↶ can put back the text the last ✨ replaced.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoAssistCommand))]
    private bool _canUndo;

    private void ForgetUndo()
    {
        _undo = null;
        CanUndo = false;
    }

    private void RememberUndo(Action undo)
    {
        _undo = undo;
        CanUndo = true;
    }

    /// <summary>↶ puts back the text the last ✨ replaced.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void UndoAssist()
    {
        var undo = _undo;
        ForgetUndo();
        undo?.Invoke();
        SetStatus("↶ The text before ✨ is back", false);
    }

    /// <summary>Stops the model.</summary>
    [RelayCommand]
    private void CancelGeneration() => _generation?.Cancel();

    private string LanguageName => _language().ToLowerInvariant() switch
    {
        "fr" => "French",
        "de" => "German",
        "en" => "English",
        "ja" => "Japanese",
        "nl" => "Dutch",
        _ => "French"
    };

    /// <summary>The rules every text of a CV is written with.</summary>
    private string Rules =>
        "Rules: use only the facts the CV gives; never invent an employer, a school, a diploma, a date, a figure, a technology or a result. "
        + $"Write in the language the CV is written in ({LanguageName} when it has no text yet), unless the purpose asks for another one. "
        + "Answer with the text only: no title, no introduction, no comment, no quotation marks, no Markdown emphasis.";

    private string Purpose => Guidance.Trim().Length > 0 ? $"Purpose of the CV (given by its author): {Guidance.Trim()}\n\n" : string.Empty;

    /// <summary>The answer of the model as a text of the CV: no code fence, no quotation marks around it.</summary>
    private static string Clean(string answer)
    {
        var text = LatexText.Convert(answer).Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
            text = System.Text.RegularExpressions.Regex.Replace(text, @"^```[^\n]*\n|\n?```$", string.Empty).Trim();
        if (text.Length > 1 && text[0] is '"' or '«' or '“' && text[^1] is '"' or '»' or '”')
            text = text[1..^1].Trim();
        // A list written on one line ("- a. - b."): each point on its line.
        if (text.StartsWith("- ", StringComparison.Ordinal))
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<=[.;:!?…])[ \t]+-[ \t]+(?=\S)", "\n- ");
        return text;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the model of the chat, the waiting popup shown; null (with the reason in the
    /// status) when there is no model, when it is cancelled or when the model fails.
    /// </summary>
    private async Task<T?> GenerateAsync<T>(string step, Func<string, CancellationToken, Task<T>> work) where T : class
    {
        if (IsGenerating)
            return null;
        if (_ollama is null || _model?.Invoke() is not { Length: > 0 } model)
        {
            SetStatus("No model to write with: choose one in the chat", true);
            return null;
        }
        using var cancel = new CancellationTokenSource();
        _generation = cancel;
        GenerationStep = step;
        IsGenerating = true;
        SetStatus("✨ " + step, false);
        try
        {
            return await work(model, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus(cancel.IsCancellationRequested ? "✨ Cancelled: nothing was changed" : "The model took too long to answer: nothing was changed", !cancel.IsCancellationRequested);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            ActivityLog.Error("Cv", step + ": " + ex.Message);
            SetStatus("The model could not write: " + ex.Message, true);
            return null;
        }
        finally
        {
            _generation = null;
            IsGenerating = false;
        }
    }

    /// <summary>Asks the model one text of the CV; null when it gave none.</summary>
    private async Task<string?> WriteAsync(string step, string task, string subject)
    {
        var cv = ToMarkdown(Snapshot());
        var text = await GenerateAsync(step, async (model, token) =>
            Clean(await _ollama!.ChatAsync(model, task + "\n\n" + Rules, $"{Purpose}{subject}\n\nThe whole CV, for context:\n\n\"\"\"\n{cv}\n\"\"\"", token)));
        if (text is { Length: 0 })
        {
            SetStatus("The model gave no text: nothing was changed", true);
            return null;
        }
        return text;
    }

    /// <summary>✨ of the summary: written from the CV, or the one typed rewritten.</summary>
    [RelayCommand]
    private async Task AssistSummaryAsync()
    {
        var before = Summary;
        var subject = before.Trim().Length > 0
            ? $"Rewrite this summary (keep what it says, make it clearer and stronger):\n\n\"\"\"\n{before.Trim()}\n\"\"\""
            : "Write the summary: the CV has none yet.";
        var text = await WriteAsync("Writing the summary",
            "You write the summary (the profile) at the top of a CV: 3 to 5 sentences in one paragraph, without \"I\", saying who the person is professionally, "
            + "what they are good at and what they have done that shows it.", subject);
        if (text is null)
            return;
        Summary = text;
        RememberUndo(() => Summary = before);
        SetStatus("✨ Summary written: read it again (↶ puts the former text back)", false);
    }

    /// <summary>✨ of an experience: its description written from its position, its technologies and the notes typed in it.</summary>
    [RelayCommand]
    private async Task AssistExperienceAsync(CvExperienceItem? item)
    {
        if (item is null)
            return;
        if (item.Position.Trim().Length == 0 && item.Company.Trim().Length == 0 && item.Description.Trim().Length == 0)
        {
            SetStatus("Give the position or the company of the experience first (and a few notes in its description: what was done there)", true);
            return;
        }
        var before = item.Description;
        var subject = $"The experience: {item.Position.Trim()} at {item.Company.Trim()} ({CvDates.Period(item.Start, item.End)}).\n"
                      + (item.Technologies.Trim().Length > 0 ? $"Technologies: {item.Technologies.Trim()}\n" : string.Empty)
                      + (before.Trim().Length > 0
                          ? $"Notes of the author about it (the facts to write from):\n\n\"\"\"\n{before.Trim()}\n\"\"\""
                          : "The author gave no notes about it: stay general, say only what the position and the technologies imply.");
        var text = await WriteAsync("Writing the description of the experience",
            "You write the description of one experience of a CV: 3 to 6 short lines, each starting with \"- \" and an action verb, saying what was done and what it brought.", subject);
        if (text is null)
            return;
        item.Description = text;
        RememberUndo(() => item.Description = before);
        SetStatus(before.Trim().Length > 0
            ? "✨ Description written from your notes: read it again (↶ puts them back)"
            : "✨ Description written without notes: it only says what the position implies, complete it (↶ empties it again)", false);
    }

    /// <summary>✨ of an education: its description.</summary>
    [RelayCommand]
    private async Task AssistEducationAsync(CvEducationItem? item)
    {
        if (item is null)
            return;
        if (item.Degree.Trim().Length == 0 && item.Institution.Trim().Length == 0 && item.Description.Trim().Length == 0)
        {
            SetStatus("Give the degree or the institution of the education first", true);
            return;
        }
        var before = item.Description;
        var subject = $"The education: {item.Degree.Trim()}, {item.FieldOfStudy.Trim()}, at {item.Institution.Trim()} ({CvDates.Period(item.Start, item.End)}).\n"
                      + (before.Trim().Length > 0
                          ? $"Notes of the author about it (the facts to write from):\n\n\"\"\"\n{before.Trim()}\n\"\"\""
                          : "The author gave no notes about it: one sentence saying what this education covers, nothing more.");
        var text = await WriteAsync("Writing the description of the education",
            "You write the description of one education of a CV: 1 to 3 short sentences (what was studied, a speciality, a thesis or a distinction when the notes give one).", subject);
        if (text is null)
            return;
        item.Description = text;
        RememberUndo(() => item.Description = before);
        SetStatus("✨ Description of the education written: read it again (↶ puts the former text back)", false);
    }

    /// <summary>✨ of a creation: its description.</summary>
    [RelayCommand]
    private async Task AssistCreationAsync(CvCreationItem? item)
    {
        if (item is null)
            return;
        if (item.Title.Trim().Length == 0 && item.Description.Trim().Length == 0)
        {
            SetStatus("Give the title of the creation first (and a few notes in its description)", true);
            return;
        }
        var before = item.Description;
        var subject = $"The creation: {item.Title.Trim()}" + (item.Url.Trim().Length > 0 ? $" ({item.Url.Trim()})" : string.Empty) + ".\n"
                      + (before.Trim().Length > 0
                          ? $"Notes of the author about it (the facts to write from):\n\n\"\"\"\n{before.Trim()}\n\"\"\""
                          : "The author gave no notes about it: one sentence from its title only, nothing more.");
        var text = await WriteAsync("Writing the description of the creation",
            "You write the description of one creation shown in a CV (a project, a product, a publication): 1 to 3 short sentences saying what it is and the part the person took in it.", subject);
        if (text is null)
            return;
        item.Description = text;
        RememberUndo(() => item.Description = before);
        SetStatus("✨ Description of the creation written: read it again (↶ puts the former text back)", false);
    }

    private sealed class SkillsAnswer
    {
        [JsonPropertyName("skills")]
        public List<string> Skills { get; set; } = [];
    }

    /// <summary>✨ of the skills: the ones the experiences, the educations and the creations show, added to the ones typed.</summary>
    [RelayCommand]
    private async Task AssistSkillsAsync()
    {
        var before = Skills;
        var known = CvDates.Split(before);
        var cv = ToMarkdown(Snapshot());
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["skills"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
            ["required"] = new JsonArray("skills")
        };
        var answer = await GenerateAsync("Looking for the skills the CV shows", async (model, token) =>
        {
            var json = await _ollama!.ChatJsonAsync(model,
                "You list the skills a CV shows: the ones its experiences, its technologies, its educations and its creations prove, each in 1 to 4 words, the most "
                + "important first, 20 at most. Never a skill the CV gives no sign of. The skills already listed are kept; write them as they are. "
                + $"The names of technologies stay as they are; every other skill is written in the language the CV is written in ({LanguageName} when unsure). Answer in JSON: {{\"skills\": [\"…\"]}}.",
                $"{Purpose}Skills already listed: {(known.Count > 0 ? string.Join(", ", known) : "none")}\n\nThe CV:\n\n\"\"\"\n{cv}\n\"\"\"", schema, token);
            return JsonSerializer.Deserialize<SkillsAnswer>(json, JsonTransfer.ReadOptions) ?? new SkillsAnswer();
        });
        if (answer is null)
            return;
        var added = CvDates.Split(string.Join(",", answer.Skills)).Where(s => !known.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        if (added.Count == 0)
        {
            SetStatus("✨ The model found no skill to add to the ones listed", false);
            return;
        }
        Skills = string.Join(", ", known.Concat(added));
        RememberUndo(() => Skills = before);
        SetStatus($"✨ {added.Count} skill(s) added: {Cut(string.Join(", ", added), 140)} — keep only the true ones (↶ takes them off)", false);
    }

    // ── Clone ──

    private sealed class RewriteAnswer
    {
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
        [JsonPropertyName("skills")] public List<string> Skills { get; set; } = [];
        [JsonPropertyName("experiences")] public List<RewriteItem> Experiences { get; set; } = [];
        [JsonPropertyName("educations")] public List<RewriteItem> Educations { get; set; } = [];
        [JsonPropertyName("creations")] public List<RewriteItem> Creations { get; set; } = [];
    }

    private sealed class RewriteItem
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("field")] public string Field { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("technologies")] public List<string> Technologies { get; set; } = [];
    }

    private static JsonObject RewriteItems(bool technologies, bool field = false)
    {
        var properties = new JsonObject
        {
            ["index"] = new JsonObject { ["type"] = "integer" },
            ["title"] = new JsonObject { ["type"] = "string" },
            ["description"] = new JsonObject { ["type"] = "string" }
        };
        var required = new JsonArray("index", "title", "description");
        if (field)
        {
            properties["field"] = new JsonObject { ["type"] = "string" };
            required.Add("field");
        }
        if (technologies)
        {
            properties["technologies"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
            required.Add("technologies");
        }
        return new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required } };
    }

    /// <summary>What the model reads of the CV to rewrite: its texts, each item of its lists under its number.</summary>
    private static string RewriteSource(CvEntry cv)
    {
        var text = new StringBuilder();
        text.Append("TITLE: ").Append(cv.Title).Append("\n\nSUMMARY:\n").Append(cv.Summary).Append("\n\nSKILLS: ").Append(string.Join(", ", cv.Skills)).Append("\n\n");
        for (var i = 0; i < cv.Experiences.Count; i++)
        {
            var e = cv.Experiences[i];
            text.Append($"EXPERIENCE {i + 1} — title: {e.Position} — employer: {e.Company} — {CvDates.Period(CvDates.Format(e.StartDate), CvDates.Format(e.EndDate))}\n")
                .Append($"technologies: {string.Join(", ", e.Technologies)}\ndescription:\n{e.Description}\n\n");
        }
        for (var i = 0; i < cv.Educations.Count; i++)
        {
            var e = cv.Educations[i];
            text.Append($"EDUCATION {i + 1} — title: {e.Degree} — field: {e.FieldOfStudy} — school: {e.Institution}\ndescription:\n{e.Description}\n\n");
        }
        for (var i = 0; i < cv.Creations.Count; i++)
        {
            var c = cv.Creations[i];
            text.Append($"CREATION {i + 1} — title: {c.Title}\ndescription:\n{c.Description}\n\n");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The wording of <paramref name="cv"/> rewritten by the model for <paramref name="adaptation"/>, in place: its
    /// title, its summary, its skills, the title and the description of each experience, education and creation. The
    /// person, the employers, the schools, the dates and the links are not given to the model to change. Gives the
    /// skills it wrote that the original did not list, and how many items of the lists it did not give back.
    /// </summary>
    private async Task<(List<string> NewSkills, int Missing)> RewriteAsync(CvEntry cv, string adaptation, string model, CancellationToken token)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string" },
                ["summary"] = new JsonObject { ["type"] = "string" },
                ["skills"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["experiences"] = RewriteItems(technologies: true),
                ["educations"] = RewriteItems(technologies: false, field: true),
                ["creations"] = RewriteItems(technologies: false)
            },
            ["required"] = new JsonArray("title", "summary", "skills", "experiences", "educations", "creations")
        };
        var json = await _ollama!.ChatJsonAsync(model,
            "You adapt a CV to what its author asks. You rewrite its wording only: its title (the headline: the job aimed at), its summary (3 to 5 sentences, "
            + "without \"I\"), its skills (the ones that serve the request first; the others may be left out), and for each numbered experience, education and "
            + "creation its title, its description, for an experience its technologies and for an education its field of study. Give back every numbered "
            + "item, each one with its number in \"index\": an item you have nothing to change in is given back as it is (translated when a translation is asked). "
            + "In a description each point is on its own line. "
            + "The title of the CV names the job aimed at; the summary does not: it never gives the person a job title, a seniority or a domain (a bank, a sector) "
            + "that the experiences do not show, it says what they did that serves the job aimed at. "
            + "When another language is asked, every text is in that language at the end, the short ones too (a one-line description, a field of study). "
            + "Descriptions of experiences: 3 to 6 short lines, each starting with \"- \" and an action verb, bringing forward what serves the request. "
            + "A title is changed only to translate it or to name the same job the way the request does: never a higher or another job than the one held. "
            + "Strict rule: every fact comes from the CV. Never invent or change an employer, a school, a diploma, a date, a figure, a result, a technology or a skill; "
            + "what the CV does not say is not written, even when the request would like it. An empty description stays empty unless the item gives facts to write from. "
            + $"Keep the language of the CV ({LanguageName} when unsure) unless the request asks for another language: then translate everything. "
            + "Answer in JSON only.",
            $"The adaptation asked:\n\n\"\"\"\n{adaptation}\n\"\"\"\n\nThe CV:\n\n\"\"\"\n{RewriteSource(cv)}\n\"\"\"", schema, token);
        var answer = JsonSerializer.Deserialize<RewriteAnswer>(json, JsonTransfer.ReadOptions)
                     ?? throw new InvalidOperationException("the model gave no CV back");

        static string Text(string? written, string original) => Clean(written ?? string.Empty) is { Length: > 0 } text ? text : original;

        var given = new HashSet<string>();
        var formerSkills = cv.Skills.Concat(cv.Experiences.SelectMany(e => e.Technologies)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        cv.Title = Text(answer.Title, cv.Title);
        cv.Summary = Text(answer.Summary, cv.Summary);
        var skills = CvDates.Split(string.Join(",", answer.Skills ?? []));
        if (skills.Count > 0)
            cv.Skills = skills;
        foreach (var item in answer.Experiences ?? [])
        {
            if (item.Index < 1 || item.Index > cv.Experiences.Count)
                continue;
            var experience = cv.Experiences[item.Index - 1];
            given.Add("x" + item.Index);
            experience.Position = Text(item.Title, experience.Position);
            experience.Description = Text(item.Description, experience.Description);
            var technologies = CvDates.Split(string.Join(",", item.Technologies ?? []));
            if (technologies.Count > 0)
                experience.Technologies = technologies;
        }
        foreach (var item in answer.Educations ?? [])
        {
            if (item.Index < 1 || item.Index > cv.Educations.Count)
                continue;
            var education = cv.Educations[item.Index - 1];
            given.Add("e" + item.Index);
            education.Degree = Text(item.Title, education.Degree);
            education.FieldOfStudy = Text(item.Field, education.FieldOfStudy);
            education.Description = Text(item.Description, education.Description);
        }
        foreach (var item in answer.Creations ?? [])
        {
            if (item.Index < 1 || item.Index > cv.Creations.Count)
                continue;
            var creation = cv.Creations[item.Index - 1];
            given.Add("c" + item.Index);
            creation.Title = Text(item.Title, creation.Title);
            creation.Description = Text(item.Description, creation.Description);
        }
        var newSkills = cv.Skills.Concat(cv.Experiences.SelectMany(e => e.Technologies)).Where(s => !formerSkills.Contains(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (newSkills, cv.Experiences.Count + cv.Educations.Count + cv.Creations.Count - given.Count);
    }

    private sealed class CloneResult
    {
        public List<string> NewSkills { get; init; } = [];
        public int Missing { get; init; }
    }

    /// <summary>
    /// ⧉ Clone: a popup asks the adaptation to make (empty: a plain copy); the CV open is saved when it has changes,
    /// copied into a new CV, the copy rewritten by the model for the adaptation, saved and opened. The original is
    /// not changed.
    /// </summary>
    [RelayCommand]
    private async Task CloneAsync()
    {
        if (Held("clone the CV"))
            return;
        var source = Snapshot();
        if (source.Person.Length == 0 && source.Summary.Length == 0 && source.Experiences.Count == 0 && source.Educations.Count == 0)
        {
            SetStatus("Nothing to clone yet: fill in the CV, or open one of the list", true);
            return;
        }
        if (BadDate() is { Length: > 0 } bad)
        {
            SetStatus($"A date of {bad} is not understood: write a month (2021-03), a year (2021), or leave it empty", true);
            return;
        }
        var adaptation = await AskAsync("Clone the CV",
            $"The CV \"{Label}\" is copied into a new one; the original is not changed.\n\n"
            + "Describe the adaptation to make: the model of the chat rewrites the copy for it (the job or the offer aimed at, what to bring forward, "
            + "the tone, the length, another language…). The employers, the schools and the dates stay the ones of the original.\n\n"
            + "Leave it empty for a plain copy.",
            Guidance.Trim());
        if (adaptation is null)
            return;
        adaptation = adaptation.Trim();
        // The original first: the clone takes its place in the window.
        if ((IsDirty || IsNew) && !await SaveEntryAsync(source, quiet: true))
            return;
        var label = Label;
        var clone = JsonSerializer.Deserialize<CvEntry>(JsonSerializer.Serialize(source))!;
        clone.Id = Guid.NewGuid().ToString();
        clone.ClonedFrom = source.Id ?? string.Empty;
        clone.Adaptation = adaptation;
        clone.Date = null;
        clone.Created = null;
        clone.Version = 0;
        var newSkills = new List<string>();
        var missing = 0;
        var started = DateTime.UtcNow;
        if (adaptation.Length > 0)
        {
            var result = await GenerateAsync("Rewriting the copy of the CV for the adaptation asked", async (model, token) =>
            {
                var (skills, left) = await RewriteAsync(clone, adaptation, model, token);
                return new CloneResult { NewSkills = skills, Missing = left };
            });
            if (result is null)
            {
                SetStatus(Status + " — the CV was not cloned", IsError);
                return;
            }
            newSkills = result.NewSkills;
            missing = result.Missing;
        }
        if (clone.Title == source.Title)
            clone.Title = (source.Title.Length > 0 ? source.Title : source.Person) + (adaptation.Length > 0 ? " (adapted)" : " (copy)");
        if (!await SaveEntryAsync(clone, quiet: true))
            return;
        var saved = await _server.GetCvAsync(clone.Id) ?? clone;
        Load(saved);
        ActivityLog.Info("Cv", adaptation.Length > 0
            ? $"CV {source.Id} cloned into {clone.Id}, rewritten by {_model?.Invoke()} in {(DateTime.UtcNow - started).TotalSeconds:0} s: {Cut(adaptation, 120)}"
            : $"CV {source.Id} cloned into {clone.Id} (plain copy)");
        SetStatus(adaptation.Length == 0
            ? $"⧉ Copy of \"{label}\" opened and saved"
            : $"⧉ Clone of \"{label}\" rewritten, saved and opened: read it against the original"
              + (missing > 0 ? $" — {missing} item(s) the model did not give back are as in the original" : string.Empty)
              + (newSkills.Count > 0 ? $" — {newSkills.Count} skill(s) or technology(ies) the original did not list, check them: {Cut(string.Join(", ", newSkills), 120)}" : string.Empty), false);
    }

    /// <summary>A name of file for the export of the CV open (without extension).</summary>
    public string ExportName => CvExport.FileName(Snapshot());

    /// <summary>
    /// Writes the CV open, as it is typed (saved or not), to <paramref name="path"/>: a Word document when
    /// <paramref name="word"/>, else a PDF. True when the file is written.
    /// </summary>
    public async Task<bool> ExportAsync(string path, bool word)
    {
        var cv = Snapshot();
        IsBusy = true;
        SetStatus(word ? "Writing the Word document…" : "Writing the PDF…", false);
        try
        {
            await Task.Run(() =>
            {
                if (word)
                {
                    using var stream = System.IO.File.Create(path);
                    CvExport.Word(stream, cv, _language());
                }
                else
                    System.IO.File.WriteAllBytes(path, CvExport.Pdf(cv, _language()));
            });
            ActivityLog.Info("Cv", $"CV {CvId} exported to {path}");
            SetStatus($"✓ Exported to {path}", false);
            return true;
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Cv", $"Export to {path}: {ex.Message}");
            SetStatus("The CV was not exported: " + ex.Message, true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The CV open in Markdown, for the clipboard.</summary>
    public string Markdown() => ToMarkdown(Snapshot());
}
