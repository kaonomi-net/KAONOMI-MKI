using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Client for the Kaonomi server, which runs the agent sequence before calling Ollama.
/// </summary>
public sealed class KaonomiAssistantService
{
    private readonly HttpClient _httpClient;

    /// <summary>Creates a client targeting the given Kaonomi server base URL.</summary>
    public KaonomiAssistantService(string baseUrl)
    {
        var baseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        // Sent from the thread pool, without proxy lookup for a local server: never a frozen window (Windows).
        _httpClient = new HttpClient(HttpHandlers.Create(baseAddress))
        {
            BaseAddress = baseAddress,
            // The agent sequence plus a local model can take minutes; cancellation is handled by the caller.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>Normalized base URI used for API requests.</summary>
    public Uri BaseAddress => _httpClient.BaseAddress!;

    /// <summary>
    /// True when the Kaonomi REST server answers at its base address within <paramref name="timeout"/> (any HTTP
    /// status counts: the server is up); false when it cannot be reached.
    /// </summary>
    public async Task<bool> IsReachableAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var response = await _httpClient.GetAsync(string.Empty, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Sends a chat completion request to <c>POST /aiassistant</c>.</summary>
    public async Task<AssistantResponse> SendAsync(
        string model,
        IReadOnlyList<AssistantMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var request = new AssistantRequest { Model = model, Messages = messages, Stream = false };

        using var response = await _httpClient.PostAsJsonAsync("aiassistant", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<AssistantResponse>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches cached dialogs via <c>POST /cache/search</c>.</summary>
    public async Task<IReadOnlyList<CacheEntry>> SearchCacheAsync(
        string term,
        int size,
        CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostAsJsonAsync("cache/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<CacheEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Updates an existing cache document via <c>POST /cache/update</c>.</summary>
    public async Task<ServiceReturn> UpdateCacheAsync(CacheEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("cache/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Creates a cache document via <c>PUT /cache</c>.</summary>
    public async Task<ServiceReturn> CreateCacheAsync(CacheEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("cache", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches symbolic patterns via <c>POST /symbolicpattern/search</c>.</summary>
    public async Task<IReadOnlyList<SymbolicPatternEntry>> SearchSymbolicPatternsAsync(
        string term,
        int size,
        CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostAsJsonAsync("symbolicpattern/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<SymbolicPatternEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Updates a symbolic pattern via <c>POST /symbolicpattern/update</c>.</summary>
    public async Task<ServiceReturn> UpdateSymbolicPatternAsync(
        SymbolicPatternEntry entry,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("symbolicpattern/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Creates a symbolic pattern via <c>PUT /symbolicpattern</c>.</summary>
    public async Task<ServiceReturn> CreateSymbolicPatternAsync(
        SymbolicPatternEntry entry,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("symbolicpattern", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the journal (<c>text</c> field only) via <c>POST /diary/search</c>; deleted entries are left out.</summary>
    public async Task<IReadOnlyList<DiaryEntry>> SearchDiaryAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("diary/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<DiaryEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent journal entries, newest first, via <c>POST /diary/last</c>.</summary>
    public async Task<IReadOnlyList<DiaryEntry>> GetLastDiaryAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("diary/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<DiaryEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Creates a journal entry via <c>PUT /diary</c>.</summary>
    public async Task<ServiceReturn> CreateDiaryAsync(DiaryEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("diary", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates a journal entry via <c>POST /diary/update</c>.</summary>
    public async Task<ServiceReturn> UpdateDiaryAsync(DiaryEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("diary/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Deletes a journal entry via <c>DELETE /diary/{id}</c> (soft delete: it is marked isDeleted).</summary>
    public async Task<ServiceReturn> DeleteDiaryAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"diary/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the YouTube videos (title and description) via <c>POST /youtubevideo/search</c>.</summary>
    public async Task<IReadOnlyList<YoutubeVideoEntry>> SearchYoutubeVideosAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("youtubevideo/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<YoutubeVideoEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent YouTube videos, newest first, via <c>POST /youtubevideo/last</c>.</summary>
    public async Task<IReadOnlyList<YoutubeVideoEntry>> GetLastYoutubeVideosAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("youtubevideo/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<YoutubeVideoEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Adds a YouTube video via <c>PUT /youtubevideo</c>.</summary>
    public async Task<ServiceReturn> CreateYoutubeVideoAsync(YoutubeVideoEntry video, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("youtubevideo", video, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates a YouTube video via <c>POST /youtubevideo/update</c>.</summary>
    public async Task<ServiceReturn> UpdateYoutubeVideoAsync(YoutubeVideoEntry video, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("youtubevideo/update", video, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Deletes a YouTube video via <c>DELETE /youtubevideo/{id}</c>.</summary>
    public async Task<ServiceReturn> DeleteYoutubeVideoAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"youtubevideo/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>
    /// Searches ideas (title, description, category, tags) via <c>POST /idea/search</c>; <c>*</c> returns the newest ones.
    /// </summary>
    public async Task<IReadOnlyList<IdeaEntry>> SearchIdeasAsync(
        string term,
        int size,
        CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostAsJsonAsync("idea/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<IdeaEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent ideas, newest first, via <c>POST /idea/last</c>.</summary>
    public async Task<IReadOnlyList<IdeaEntry>> GetLastIdeasAsync(int size, CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = "*", Size = size };

        using var response = await _httpClient.PostAsJsonAsync("idea/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<IdeaEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Creates an idea via <c>PUT /idea</c>.</summary>
    public async Task<ServiceReturn> CreateIdeaAsync(IdeaEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("idea", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates an idea via <c>POST /idea/update</c>.</summary>
    public async Task<ServiceReturn> UpdateIdeaAsync(IdeaEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("idea/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Deletes an idea via <c>DELETE /idea/{id}</c>.</summary>
    public async Task<ServiceReturn> DeleteIdeaAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"idea/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>
    /// The <paramref name="size"/> most recent tasks, newest first, without the deleted ones, via <c>POST /task/last</c>.
    /// </summary>
    public async Task<IReadOnlyList<TaskEntry>> GetLastTasksAsync(int size, CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = "*", Size = size };

        using var response = await _httpClient.PostAsJsonAsync("task/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<TaskEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Creates a task via <c>PUT /task</c>.</summary>
    public async Task<ServiceReturn> CreateTaskAsync(TaskEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("task", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates a task via <c>POST /task/update</c>.</summary>
    public async Task<ServiceReturn> UpdateTaskAsync(TaskEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("task/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Soft-deletes a task via <c>DELETE /task/{id}</c> (it gets <c>isDeleted = true</c>).</summary>
    public async Task<ServiceReturn> DeleteTaskAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"task/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>
    /// Searches intels in all their text fields via <c>POST /intel/search</c>; <c>*</c> returns the newest ones. Deleted
    /// intels are left out.
    /// </summary>
    public async Task<IReadOnlyList<IntelEntry>> SearchIntelsAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostAsJsonAsync("intel/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<IntelEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent intels, newest first, via <c>POST /intel/last</c>.</summary>
    public async Task<IReadOnlyList<IntelEntry>> GetLastIntelsAsync(int size, CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = "*", Size = size };

        using var response = await _httpClient.PostAsJsonAsync("intel/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<IntelEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Creates an intel via <c>PUT /intel</c>.</summary>
    public async Task<ServiceReturn> CreateIntelAsync(IntelEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("intel", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates an intel via <c>POST /intel/update</c>.</summary>
    public async Task<ServiceReturn> UpdateIntelAsync(IntelEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("intel/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Soft-deletes an intel via <c>DELETE /intel/{id}</c> (it gets <c>isDeleted = true</c>).</summary>
    public async Task<ServiceReturn> DeleteIntelAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"intel/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Loads one ontology node via <c>GET /ontology/{id}</c>; null when the server returns nothing.</summary>
    public async Task<OntologyEntry?> GetOntologyAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"ontology/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(body)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<OntologyEntry>(body);
    }

    /// <summary>Searches the ontology <c>description</c> field via <c>POST /ontology/search</c>.</summary>
    public async Task<IReadOnlyList<OntologyEntry>> SearchOntologiesAsync(
        string term,
        int size,
        CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostAsJsonAsync("ontology/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<OntologyEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Loads up to <paramref name="size"/> ontology nodes via <c>POST /ontology/last</c> (<c>*</c> matches all).</summary>
    public async Task<IReadOnlyList<OntologyEntry>> GetLastOntologiesAsync(
        string term,
        int size,
        CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostAsJsonAsync("ontology/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<OntologyEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Updates an ontology node via <c>POST /ontology/update</c>.</summary>
    public async Task<ServiceReturn> UpdateOntologyAsync(OntologyEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("ontology/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Creates an ontology node via <c>PUT /ontology</c>.</summary>
    public async Task<ServiceReturn> CreateOntologyAsync(OntologyEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("ontology", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Client of the running application (set by the main window), used by windows without their own client.</summary>
    public static KaonomiAssistantService? Shared { get; set; }

    /// <summary>Sends an anonymous note via <c>PUT /whitenote</c>.</summary>
    public async Task<ServiceReturn> CreateWhiteNoteAsync(WhiteNoteEntry note, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("whitenote", note, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Sends an activity log entry via <c>PUT /log</c> (used by <see cref="ActivityLog"/>).</summary>
    public async Task SaveLogAsync(LogEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("log", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>All the logs of every application of the last <paramref name="minutes"/> minutes, newest first, via <c>GET /log/since</c>.</summary>
    public async Task<IReadOnlyList<LogEntry>> GetLogsSinceAsync(int minutes, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"log/since?minutes={minutes}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<LogEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent logs of every application, newest first, via <c>GET /log/last</c>.</summary>
    public async Task<IReadOnlyList<LogEntry>> GetLastLogsAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"log/last?size={size}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<LogEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Token consumption of the LLM calls of the last <paramref name="minutes"/> minutes, via <c>GET /tokensusage/last</c>.</summary>
    public async Task<IReadOnlyList<TokenUsageEntry>> GetTokensUsageAsync(int minutes, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"tokensusage/last?minutes={minutes}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<TokenUsageEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Saves a prompt and its completion via <c>PUT /completion</c>.</summary>
    public async Task<ServiceReturn> SaveCompletionAsync(CompletionEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("completion", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the completions (prompt and completion) via <c>POST /completion/search</c>.</summary>
    public async Task<IReadOnlyList<CompletionEntry>> SearchCompletionsAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("completion/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<CompletionEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent completions, newest first, via <c>POST /completion/last</c>.</summary>
    public async Task<IReadOnlyList<CompletionEntry>> GetLastCompletionsAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("completion/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<CompletionEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Deletes a completion via <c>DELETE /completion/{id}</c>.</summary>
    public async Task<ServiceReturn> DeleteCompletionAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"completion/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Sends a measure of the <c>/system</c> window via <c>PUT /systemlog</c>.</summary>
    public async Task SaveSystemLogAsync(SystemLogEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync("systemlog", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Route of the evaluation service on the Kaonomi server.</summary>
    private const string EvaluationRoute = "evaluation";

    /// <summary>Sends a like / dislike with its explanation via <c>PUT /evaluation</c>.</summary>
    public async Task<ServiceReturn> CreateEvaluationAsync(EvaluationEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsJsonAsync(EvaluationRoute, entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Loads the latest ticker messages via <c>POST /messageflow/last</c>.</summary>
    public async Task<IReadOnlyList<MessageFlowEntry>> GetLastMessageFlowsAsync(
        string term,
        int size,
        CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostAsJsonAsync("messageflow/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<List<MessageFlowEntry>>(cancellationToken) ?? [];
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"The Kaonomi server responded {(int)response.StatusCode} ({response.ReasonPhrase}): {body}",
            null,
            response.StatusCode);
    }
}
