using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A language a cache builder can be translated into: its code (FR, EN…) and its name in English.</summary>
public sealed record CacheLanguage(string Code, string Name)
{
    /// <summary>The line of the list: the name and the code.</summary>
    public string Label => $"{Name} ({Code})";
}

/// <summary>
/// /cache: the cache builders of the server (<c>CacheBuilder</c>: prompts and their completions written by hand),
/// searched, added and edited. The newest ones are listed when the window opens and when the search box is empty.
/// </summary>
public partial class CacheWindowViewModel : ViewModelBase, INoteContextProvider
{
    private const int PageSize = 50;

    private readonly KaonomiAssistantService _assistant;

    /// <summary>Design-time constructor using default server URL from settings.</summary>
    public CacheWindowViewModel() : this(ChatSettings.Load())
    {
    }

    private CacheWindowViewModel(ChatSettings settings) : this(
        new KaonomiAssistantService(settings.ServerUrl), new OllamaModelService(settings.OllamaUrl, settings.LlmProvider), () => null, settings.LlmGatewayUrl)
    {
    }

    /// <summary>
    /// Uses the given assistant client for the API calls and lists the newest documents; the variations and the
    /// evaluation ask <paramref name="llm"/> directly with the model returned by <paramref name="model"/> (the one
    /// selected in the chat window); TO SEMANTIC CACHE writes in the LLM gateway at <paramref name="gatewayUrl"/>
    /// (<c>llmGatewayUrl</c> of config.yml when null).
    /// </summary>
    public CacheWindowViewModel(KaonomiAssistantService assistant, OllamaModelService llm, Func<string?> model, string? gatewayUrl = null)
    {
        _assistant = assistant;
        _gatewayUrl = string.IsNullOrWhiteSpace(gatewayUrl) ? ChatSettings.Load().LlmGatewayUrl : gatewayUrl;
        _llm = llm;
        _model = model;
        _ = SearchAsync(CancellationToken.None);
    }

    /// <summary>Documents found and locally created rows bound to the list.</summary>
    public ObservableCollection<CacheEntryViewModel> Results { get; } = [];

    /// <summary>True when at least one row is present.</summary>
    public bool HasResults => Results.Count > 0;

    /// <summary>The search text; empty: the newest documents.</summary>
    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand), nameof(GeneratePromptsCommand), nameof(GenerateCompletionsCommand), nameof(EvaluateCommand), nameof(PublishCommand), nameof(TranslateCommand), nameof(ToSemanticCacheCommand))]
    private CacheEntryViewModel? _selectedResult;

    [ObservableProperty]
    private string _status = "Loading…";

    [ObservableProperty]
    private bool _isError;

    /// <summary>Inserts a new unsaved document at the top of the list.</summary>
    [RelayCommand]
    private void New()
    {
        var entry = CacheEntryViewModel.CreateNew(_assistant);
        Results.Insert(0, entry);
        SelectedResult = entry;
        OnPropertyChanged(nameof(HasResults));
        ActivityLog.Info("Cache", "New cache builder started (unsaved)");
    }

    private bool CanClone() => SelectedResult is not null;

    /// <summary>Duplicates the selected document as a new unsaved one.</summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private void Clone()
    {
        var source = SelectedResult!;
        var clone = source.Clone();
        Results.Insert(Results.IndexOf(source) + 1, clone);
        SelectedResult = clone;
        clone.Status = "Unsaved clone";
        ActivityLog.Info("Cache", $"Cache builder cloned from {source.Id ?? "(new)"} (unsaved)");
    }

    /// <summary>
    /// Lists the documents matching <see cref="Term"/> (title, prompts, completions, model, comment, tags), or the
    /// newest ones when it is empty. The documents not saved yet stay at the top of the list.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var term = Term.Trim();
        var what = term.Length == 0 ? "the newest documents" : $"\"{term}\"";
        IsError = false;
        Status = $"Searching for {what}…";

        try
        {
            var entries = await _assistant.SearchCacheBuildersAsync(term.Length == 0 ? "*" : term, PageSize, cancellationToken);
            var unsaved = Results.Where(r => r.IsNew).ToList();

            Results.Clear();
            foreach (var entry in unsaved)
                Results.Add(entry);
            foreach (var entry in entries)
                Results.Add(new CacheEntryViewModel(entry, _assistant));

            SelectedResult = Results.FirstOrDefault();
            Status = entries.Count == 0
                ? term.Length == 0 ? "No document yet: NEW creates the first one" : $"No results for {what}"
                : term.Length == 0 ? $"{entries.Count} newest document(s)" : $"{entries.Count} result(s) for {what}";
            ActivityLog.Info("Cache", $"Search {what}: {entries.Count} result(s)");
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Cache", $"Search {what} failed: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Cache", $"Search {what} failed", ex);
        }
        finally
        {
            OnPropertyChanged(nameof(HasResults));
        }
    }

    // ── The model: variations of the first prompt and of the first completion, evaluation of every pair ──

    private const int DefaultVariations = 5;
    private const int MaxVariations = 30;

    private const string PromptVariationsSystem =
        "You rewrite a question a user asks an assistant. Write different wordings of the question given by the user: "
        + "each one keeps exactly the same meaning and the same intention, in the same language, as a user would type it "
        + "(vary the words, the word order, the register, the length; some may be short or familiar). Do not answer the "
        + "question, do not add or remove information, no numbering. Answer with a JSON object whose \"variations\" field "
        + "is the array of the wordings.";

    private const string CompletionVariationsSystem =
        "You rewrite the answer of an assistant. Write different wordings of the answer given by the user: each one keeps "
        + "exactly the same meaning, the same facts and the same intention, in the same language and with a comparable "
        + "length and tone, and still answers the question. Do not add or remove information, no numbering. Answer with a "
        + "JSON object whose \"variations\" field is the array of the wordings.";

    private const string EvaluationSystem =
        "You judge whether an answer suits a question, for a cache that will serve this answer to that question. Give a "
        + "score from 0 to 10: 10 the answer is correct, complete and answers exactly that question in its language; 5 it "
        + "answers only in part or awkwardly; 0 it is wrong or off topic. Answer with a JSON object: \"score\" (an integer "
        + "from 0 to 10) and \"reason\" (one short sentence, in the language of the question).";

    private static readonly JsonNode VariationsSchema = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["variations"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
        },
        ["required"] = new JsonArray("variations")
    };

    private static readonly JsonNode EvaluationSchema = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["score"] = new JsonObject { ["type"] = "integer" },
            ["reason"] = new JsonObject { ["type"] = "string" }
        },
        ["required"] = new JsonArray("score", "reason")
    };

    private readonly OllamaModelService _llm;
    private readonly Func<string?> _model;
    private CancellationTokenSource? _work;

    /// <summary>
    /// Asks the user a line of text in a popup (title, question, initial value); null when cancelled. Set by the view.
    /// </summary>
    public Func<string, string, string, Task<string?>> AskAsync { get; set; } = (_, _, initial) => Task.FromResult<string?>(initial);

    /// <summary>True while the model writes variations or evaluates.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GeneratePromptsCommand), nameof(GenerateCompletionsCommand), nameof(EvaluateCommand), nameof(PublishCommand), nameof(TranslateCommand), nameof(ToSemanticCacheCommand), nameof(CancelWorkCommand))]
    private bool _isWorking;

    /// <summary>True while the model writes variations of the first prompt (the waiting animation of the prompts).</summary>
    [ObservableProperty]
    private bool _isGeneratingPrompts;

    /// <summary>True while the model writes variations of the first completion (the waiting animation of the completions).</summary>
    [ObservableProperty]
    private bool _isGeneratingCompletions;

    private bool CanWork() => SelectedResult is not null && !IsWorking;

    /// <summary>
    /// Asks how many (popup), then has the model write that many wordings of the first prompt, with the same meaning
    /// and the same intention, and adds them to the prompts of the selected document.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task GeneratePromptsAsync() => GenerateAsync(prompts: true);

    /// <summary>The same for the first completion (the first prompt is given to the model as the question it answers).</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task GenerateCompletionsAsync() => GenerateAsync(prompts: false);

    private async Task GenerateAsync(bool prompts)
    {
        var entry = SelectedResult!;
        var what = prompts ? "prompt" : "completion";
        var source = (prompts ? entry.PromptTexts : entry.CompletionTexts).FirstOrDefault();
        if (source is null)
        {
            Report($"Write a first {what}: the variations are made from it", true);
            return;
        }
        if (Model() is not { } model)
            return;

        var answer = await AskAsync($"Variations of the first {what}",
            $"How many variations of the first {what} (same meaning, same intention) should {model} write? (1 to {MaxVariations})",
            DefaultVariations.ToString());
        if (answer is null)
            return;
        if (!int.TryParse(answer.Trim(), out var count) || count < 1 || count > MaxVariations)
        {
            Report($"\"{answer.Trim()}\" is not a number from 1 to {MaxVariations}: nothing generated", true);
            return;
        }

        var existing = prompts ? entry.PromptTexts : entry.CompletionTexts;
        var language = string.IsNullOrWhiteSpace(entry.Language)
            ? "in the language of the text between the markers (never translate it: a French text gives French wordings)"
            : $"in the language of the document, whose code is {entry.Language.Trim().ToUpperInvariant()} (never another language)";
        var request = (prompts ? "Question to reword:\n" : $"Question:\n<<<\n{entry.PromptTexts.FirstOrDefault() ?? "(none)"}\n>>>\n\nAnswer to reword:\n")
                      + $"<<<\n{source}\n>>>\n\n"
                      + (existing.Count > 1 ? "Wordings that already exist (write different ones):\n" + string.Join("\n", existing.Skip(1).Select(t => "- " + t)) + "\n\n" : string.Empty)
                      + $"Write exactly {count} different wording(s), {language}. Answer with the JSON object {{\"variations\": [...]}} only.";

        if (prompts)
            IsGeneratingPrompts = true;
        else
            IsGeneratingCompletions = true;
        try
        {
            await GenerateWorkAsync();
        }
        finally
        {
            IsGeneratingPrompts = false;
            IsGeneratingCompletions = false;
        }

        Task GenerateWorkAsync() => WorkAsync(async ct =>
        {
            Report($"Asking {model} for {count} variation(s) of the first {what}…", false);
            var json = await _llm.ChatJsonAsync(model, prompts ? PromptVariationsSystem : CompletionVariationsSystem, request, VariationsSchema, ct);
            var variations = Variations(json).Where(v => !string.Equals(v, source, StringComparison.OrdinalIgnoreCase)).Take(count).ToList();
            var added = prompts ? entry.AddPrompts(variations) : entry.AddCompletions(variations);
            Report(added == 0
                ? $"{model} wrote no new {what}: try again"
                : $"{added} {what}(s) added by {model}{(added < count ? $" ({count} asked)" : string.Empty)} · read them, then SAVE", added == 0);
            ActivityLog.Info("Cache", $"{added} {what} variation(s) generated by {model} for {entry.Id ?? "(new)"}");
        });
    }

    /// <summary>
    /// Has the model mark every prompt with every completion (0 to 10, one call per pair) and shows the marks, the
    /// worst first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task EvaluateAsync()
    {
        var entry = SelectedResult!;
        var prompts = entry.PromptTexts;
        var completions = entry.CompletionTexts;
        if (prompts.Count == 0 || completions.Count == 0)
        {
            Report("At least one prompt and one completion are needed to evaluate", true);
            return;
        }
        if (Model() is not { } model)
            return;

        await WorkAsync(async ct =>
        {
            var total = prompts.Count * completions.Count;
            var results = new List<CacheEvaluation>();
            var failed = 0;
            foreach (var prompt in prompts)
            {
                foreach (var completion in completions)
                {
                    Report($"{model} evaluates pair {results.Count + failed + 1} of {total}…", false);
                    var json = await _llm.ChatJsonAsync(model, EvaluationSystem,
                        $"Question:\n<<<\n{prompt}\n>>>\n\nAnswer:\n<<<\n{completion}\n>>>\n\nAnswer with the JSON object {{\"score\": 0-10, \"reason\": \"...\"}} only. Write the reason in the language of the question{(string.IsNullOrWhiteSpace(entry.Language) ? string.Empty : $" ({entry.Language.Trim().ToUpperInvariant()})")}.",
                        EvaluationSchema, ct);
                    if (Evaluation(json) is var (score, reason))
                        results.Add(new CacheEvaluation(prompt, completion, score, reason));
                    else
                        failed++;
                    entry.ShowEvaluations(results);
                }
            }
            Report($"{results.Count} pair(s) evaluated by {model}{(failed > 0 ? $", {failed} without a readable mark" : string.Empty)} · {entry.EvaluationSummary}", failed > 0 && results.Count == 0);
            ActivityLog.Info("Cache", $"Evaluation by {model} of {entry.Id ?? "(new)"}: {entry.EvaluationSummary}");
        });
    }

    /// <summary>
    /// Publishes the selected document: every prompt with every completion becomes a document of the index
    /// <c>cache</c> of the server (the pairs published before and removed since are withdrawn). The document must be
    /// saved first: the server publishes what it holds.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task PublishAsync()
    {
        var entry = SelectedResult!;
        if (entry.IsNew || entry.IsDirty || entry.Id is not { } id)
        {
            Report("SAVE the document first: what is published is what the server holds", true);
            return;
        }

        await WorkAsync(async ct =>
        {
            Report($"Publishing {entry.PromptTexts.Count} prompt(s) x {entry.CompletionTexts.Count} completion(s)…", false);
            var result = await _assistant.PublishCacheBuilderAsync(id, ct);
            Report(result.Message ?? (result.IsSuccess ? "Published" : "The Kaonomi server rejected the publication"), !result.IsSuccess);
            if (result.IsSuccess)
                ActivityLog.Info("Cache", $"Cache builder {id} published: {result.Message}");
            else
                ActivityLog.Error("Cache", $"Cache builder {id} not published: {result.Message}");
        });
    }

    // ── The semantic cache of the LLM gateway ──

    private readonly string _gatewayUrl;
    private LlmGatewayClient? _gateway;

    /// <summary>
    /// Sends the selected document (as it is in the editor) to the semantic cache of the LLM gateway
    /// (<c>POST /gateway/cache/import</c>): one phrase per prompt, every completion as one of its answers, for every
    /// model and every context. The gateway adds only what it does not keep yet (an answer already kept for the same
    /// prompt is skipped), so the button can be clicked again after the document changed: only the new entries go in.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task ToSemanticCacheAsync()
    {
        var entry = SelectedResult!;
        var prompts = entry.PromptTexts;
        var completions = entry.CompletionTexts;
        if (prompts.Count == 0 || completions.Count == 0)
        {
            Report("At least one prompt and one completion are needed for the semantic cache", true);
            return;
        }

        var phrases = new JsonArray();
        foreach (var prompt in prompts)
        {
            var answers = new JsonArray();
            foreach (var completion in completions)
                answers.Add(completion);
            phrases.Add(new JsonObject { ["prompt"] = prompt, ["model"] = "*", ["answers"] = answers });
        }
        var document = new JsonObject { ["format"] = "kaonomi-llmgateway-cache", ["version"] = 1, ["phrases"] = phrases };

        await WorkAsync(async ct =>
        {
            Report($"Sending {prompts.Count} prompt(s) x {completions.Count} completion(s) to the semantic cache of the LLM gateway…", false);
            GatewayImportResult? result;
            try
            {
                _gateway ??= new LlmGatewayClient(_gatewayUrl);
                result = await _gateway.ImportAsync(document.ToJsonString(), ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null)
            {
                Report($"LLM gateway unreachable at {_gatewayUrl} ({ex.Message})", true);
                ActivityLog.Error("Cache", "Semantic cache not updated: LLM gateway unreachable", ex);
                return;
            }
            if (result is null)
            {
                Report("The LLM gateway returned an empty response", true);
                return;
            }
            var errors = result.Errors is { Count: > 0 } ? " · " + string.Join("; ", result.Errors) : string.Empty;
            Report($"Semantic cache: {result.Added} new entry(ies) added, {result.Skipped} already there ({result.Phrases} prompt(s)){errors}", result.Added == 0 && errors.Length > 0);
            ActivityLog.Info("Cache", $"Cache builder {entry.Id ?? "(new)"} sent to the semantic cache: {result.Added} added, {result.Skipped} already there");
        });
    }

    // ── The translation: the selected document in another language, finished in a new window ──

    private const string TranslationSystem =
        "You are a professional translator. Translate the text given by the user into the language asked. Keep the "
        + "meaning, the intention, the tone and the layout (line breaks, Markdown, lists); leave the proper nouns, the "
        + "numbers, the code and the URLs as they are. The text may be a question: translate it, never answer it. Add "
        + "nothing. Answer with a JSON object whose \"translation\" field is the translated text.";

    private static readonly JsonNode TranslationSchema = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["translation"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("translation")
    };

    /// <summary>The languages offered by the translation popup.</summary>
    public static IReadOnlyList<CacheLanguage> Languages { get; } =
    [
        new("EN", "English"), new("FR", "French"), new("DE", "German"), new("ES", "Spanish"), new("IT", "Italian"),
        new("PT", "Portuguese"), new("NL", "Dutch"), new("RU", "Russian"), new("ZH", "Chinese"), new("JA", "Japanese"),
        new("KO", "Korean"), new("AR", "Arabic")
    ];

    /// <summary>The language chosen in the translation popup.</summary>
    [ObservableProperty]
    private CacheLanguage _translationLanguage = Languages[0];

    /// <summary>True while the model translates (the waiting animation of the popup).</summary>
    [ObservableProperty]
    private bool _isTranslating;

    /// <summary>
    /// Raised when a translation is ready: a new cache builder window holding the translated document, not saved yet
    /// (to read, correct, SAVE and PUBLISH), and the code of its language. The view opens it.
    /// </summary>
    public event Action<CacheWindowViewModel, CacheLanguage>? TranslationReady;

    /// <summary>
    /// Translates the selected document (its title, each prompt, each completion: one call of the model each) into
    /// <see cref="TranslationLanguage"/>, then raises <see cref="TranslationReady"/> with a new window where the
    /// translation is a new unsaved document. The original is not changed; the tags and the related entities of the
    /// translation are left empty, so that its SAVE detects them in the translated text.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task TranslateAsync()
    {
        var entry = SelectedResult!;
        var language = TranslationLanguage;
        var source = entry.ToEntry();
        if (source.Prompts!.Count == 0 && source.Completions!.Count == 0)
        {
            Report("Nothing to translate: write a prompt or a completion first", true);
            return;
        }
        if (string.Equals(source.Language, language.Code, StringComparison.OrdinalIgnoreCase))
        {
            Report($"The document is already in {language.Name}: choose another language", true);
            return;
        }
        if (Model() is not { } model)
            return;

        CacheBuilderEntry? translated = null;
        IsTranslating = true;
        try
        {
            await WorkAsync(async ct =>
            {
                var total = source.Prompts.Count + source.Completions!.Count + (source.Title.Length > 0 ? 1 : 0);
                var done = 0;
                async Task<string> OneAsync(string text)
                {
                    Report($"{model} translates into {language.Name}: text {++done} of {total}…", false);
                    var json = await _llm.ChatJsonAsync(model, TranslationSystem,
                        $"Translate into {language.Name} ({language.Code}) the text between the markers:\n<<<\n{text}\n>>>\n\nAnswer with the JSON object {{\"translation\": \"...\"}} only.",
                        TranslationSchema, ct);
                    return JsonObjectOf(json)?["translation"] is JsonValue value && value.TryGetValue<string>(out var result) && result.Trim().Length > 0
                        ? result.Trim()
                        : throw new InvalidOperationException($"{model} gave no translation for \"{ActivityLog.Excerpt(text)}\": try again, or with another model");
                }

                var title = source.Title.Length > 0 ? await OneAsync(source.Title) : string.Empty;
                var prompts = new List<string>();
                foreach (var prompt in source.Prompts)
                    prompts.Add(await OneAsync(prompt));
                var completions = new List<string>();
                foreach (var completion in source.Completions)
                    completions.Add(await OneAsync(completion));

                translated = new CacheBuilderEntry
                {
                    Date = DateTimeOffset.Now,
                    AppName = "KAONOMI.Chat",
                    MachineName = Environment.MachineName,
                    UserName = Environment.UserName,
                    Title = title,
                    Language = language.Code,
                    Model = source.Model,
                    Prompts = prompts.Distinct().ToList(),
                    Completions = completions.Distinct().ToList(),
                    Comment = $"Translated into {language.Name} by {model}"
                              + (string.IsNullOrWhiteSpace(source.Language) ? string.Empty : $" from {source.Language}")
                              + (source.Id is null ? string.Empty : $" (cache builder {source.Id})"),
                    Events = source.Events,
                    Signals = source.Signals,
                    ExcludedEntities = source.ExcludedEntities
                };
            });
        }
        finally
        {
            IsTranslating = false;
        }

        if (translated is null)
            return;
        Report($"Translated into {language.Name} by {model}: finish it in the new window (read, SAVE, PUBLISH)", false);
        ActivityLog.Info("Cache", $"Cache builder {source.Id ?? "(new)"} translated into {language.Code} by {model}");
        var window = new CacheWindowViewModel(_assistant, _llm, _model, _gatewayUrl);
        window.AddUnsaved(translated, $"Unsaved translation ({language.Name}): read it, then SAVE");
        TranslationReady?.Invoke(window, language);
    }

    /// <summary>Puts a document that is not saved yet at the top of the list, selected (the search keeps it there).</summary>
    private void AddUnsaved(CacheBuilderEntry entry, string status)
    {
        entry.Id = null;
        var row = new CacheEntryViewModel(entry, _assistant) { Status = status };
        Results.Insert(0, row);
        SelectedResult = row;
        OnPropertyChanged(nameof(HasResults));
    }

    private bool CanCancelWork() => IsWorking;

    /// <summary>Stops the generation or the evaluation that runs.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelWork))]
    private void CancelWork() => _work?.Cancel();

    /// <summary>The model selected in the chat window; says so when there is none.</summary>
    private string? Model()
    {
        if (!string.IsNullOrWhiteSpace(_model()))
            return _model();
        Report("Select a model in the chat window first", true);
        return null;
    }

    private void Report(string status, bool isError)
    {
        Status = status;
        IsError = isError;
    }

    /// <summary>Runs a piece of work with the model: one at a time, cancellable, its errors in the status bar.</summary>
    private async Task WorkAsync(Func<CancellationToken, Task> work)
    {
        using var source = new CancellationTokenSource();
        _work = source;
        IsWorking = true;
        try
        {
            await work(source.Token);
        }
        catch (OperationCanceledException)
        {
            Report("Cancelled", false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            Report($"{_llm.ProviderName} unreachable at {_llm.BaseAddress} ({ex.Message})", true);
            ActivityLog.Error("Cache", "Model unreachable", ex);
        }
        catch (Exception ex)
        {
            Report(ex.Message, true);
            ActivityLog.Error("Cache", "The model call failed", ex);
        }
        finally
        {
            _work = null;
            IsWorking = false;
        }
    }

    /// <summary>The object of a JSON answer, even inside a code fence or after a reasoning block; null when there is none.</summary>
    private static JsonNode? JsonObjectOf(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            return JsonNode.Parse(answer[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The wordings of a <c>{"variations": [...]}</c> answer: trimmed, without numbering, the doubles left out.</summary>
    internal static List<string> Variations(string? answer)
    {
        var list = new List<string>();
        if (JsonObjectOf(answer)?["variations"] is not JsonArray array)
            return list;
        foreach (var node in array)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
                continue;
            text = text.Trim();
            if (text.Length > 0 && !list.Contains(text, StringComparer.OrdinalIgnoreCase))
                list.Add(text);
        }
        return list;
    }

    /// <summary>The mark (kept from 0 to 10) and the reason of a <c>{"score": …, "reason": …}</c> answer; null when unreadable.</summary>
    internal static (int Score, string Reason)? Evaluation(string? answer)
    {
        if (JsonObjectOf(answer) is not JsonObject json || json["score"] is not JsonValue value)
            return null;
        double score;
        if (value.TryGetValue<double>(out var number))
            score = number;
        else if (value.TryGetValue<string>(out var text) && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            score = parsed;
        else
            return null;
        var reason = json["reason"] is JsonValue r && r.TryGetValue<string>(out var why) ? why.Trim() : string.Empty;
        return ((int)Math.Round(Math.Clamp(score, 0, 10)), reason);
    }

    /// <inheritdoc />
    public string NoteWindowName => "Cache builder";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        searchTerm = Term,
        resultCount = Results.Count,
        selected = SelectedResult?.ToEntry()
    };
}
