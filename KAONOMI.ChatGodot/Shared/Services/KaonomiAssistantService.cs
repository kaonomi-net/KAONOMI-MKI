using System;
using System.Collections.Generic;
using System.Net.Http;
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
        // Sent from the thread pool, without proxy lookup for a local server: never a frozen window (Windows); every
        // request within its time limit (DeadlineOf), the caller may cancel it sooner.
        _httpClient = new HttpClient(HttpHandlers.CreateWithDeadline(baseAddress, DeadlineOf))
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>The time limit of a request that reads or writes documents: the search engine answers in less than a second.</summary>
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromMinutes(2);

    /// <summary>The time limit of the long requests: the agent sequence with a local model, an image drawn by Fooocus.</summary>
    public static readonly TimeSpan LongRequestDeadline = TimeSpan.FromMinutes(20);

    private static TimeSpan DeadlineOf(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath.TrimEnd('/') is { } path
        && (path.EndsWith("/aiassistant", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/image/generate", StringComparison.OrdinalIgnoreCase))
            ? LongRequestDeadline
            : RequestDeadline;

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
        CancellationToken cancellationToken = default,
        string? sessionId = null,
        bool regenerate = false)
    {
        var request = new AssistantRequest { Model = model, Messages = messages, Stream = false, SessionId = sessionId, Regenerate = regenerate };

        using var response = await _httpClient.PostJsonAsync("aiassistant", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<AssistantResponse>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>
    /// A loose search of <paramref name="term"/> in <paramref name="indices"/> at once (<c>POST /search</c>; every
    /// searchable index when empty), <paramref name="size"/> hits per index at most.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAllAsync(string term, IReadOnlyList<string> indices, int size, CancellationToken cancellationToken = default)
    {
        var request = new SearchRequest { Term = term, Indices = [.. indices], Size = size };
        using var response = await _httpClient.PostJsonAsync("search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<SearchHit>>(cancellationToken) ?? [];
    }

    /// <summary>
    /// The documents of the last <paramref name="minutes"/> minutes in <paramref name="indices"/> (<c>POST /recent</c>;
    /// every searchable index when empty), the newest first, <paramref name="size"/> per index at most, with their text.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> GetRecentAsync(int minutes, IReadOnlyList<string> indices, int size, CancellationToken cancellationToken = default)
    {
        var request = new SearchRequest { Term = string.Empty, Indices = [.. indices], Size = size, Minutes = minutes };
        using var response = await _httpClient.PostJsonAsync("recent", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<SearchHit>>(cancellationToken) ?? [];
    }

    /// <summary>
    /// The entities found by the NER in the dialogs of the last <paramref name="minutes"/> minutes (all when 0), counted,
    /// the <paramref name="size"/> most frequent first, via <c>GET /detectedentity/top</c>.
    /// </summary>
    public async Task<IReadOnlyList<DetectedEntityCount>> GetDetectedEntitiesTopAsync(int minutes, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"detectedentity/top?minutes={minutes}&size={size}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<DetectedEntityCount>>(cancellationToken) ?? [];
    }

    /// <summary>The symbols found by the symbolic patterns in the last <paramref name="minutes"/> minutes, counted (<c>GET /detectedsymbol/top</c>).</summary>
    public async Task<IReadOnlyList<DetectedSymbolCount>> GetDetectedSymbolsTopAsync(int minutes, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"detectedsymbol/top?minutes={minutes}&size={size}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<DetectedSymbolCount>>(cancellationToken) ?? [];
    }

    /// <summary>The tags found through the detections in the last <paramref name="minutes"/> minutes, counted (<c>GET /detectedtag/top</c>).</summary>
    public async Task<IReadOnlyList<DetectedTagCount>> GetDetectedTagsTopAsync(int minutes, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"detectedtag/top?minutes={minutes}&size={size}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<DetectedTagCount>>(cancellationToken) ?? [];
    }

    /// <summary>The texts of the last <paramref name="size"/> white notes, newest first, via <c>GET /whitenote/last</c>.</summary>
    public async Task<IReadOnlyList<WhiteNoteEntry>> GetLastWhiteNotesAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"whitenote/last?size={size}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<WhiteNoteEntry>>(cancellationToken) ?? [];
    }

    // ── Clusters (the groups of elements drawn with the pen of the graph of relations) ──

    /// <summary>Creates a cluster via <c>PUT /cluster</c>.</summary>
    public async Task<ServiceReturn> CreateClusterAsync(ClusterEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("cluster", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates a cluster via <c>POST /cluster/update</c>.</summary>
    public async Task<ServiceReturn> UpdateClusterAsync(ClusterEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("cluster/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Loads a cluster via <c>GET /cluster/{id}</c>; null when the server returns nothing.</summary>
    public async Task<ClusterEntry?> GetClusterAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"cluster/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ClusterEntry>(cancellationToken);
    }

    /// <summary>Searches the clusters via <c>POST /cluster/search</c> (name, description, tags, members; "*": the newest ones).</summary>
    public async Task<IReadOnlyList<ClusterEntry>> SearchClustersAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("cluster/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<ClusterEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent clusters via <c>POST /cluster/last</c>.</summary>
    public async Task<IReadOnlyList<ClusterEntry>> GetLastClustersAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("cluster/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<ClusterEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Marks a cluster deleted via <c>DELETE /cluster/{id}</c> (it is never erased).</summary>
    public async Task<ServiceReturn> DeleteClusterAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"cluster/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    // ── Interviews (/interview): the interview, and its transcription paragraph by paragraph ──

    /// <summary>Loads an interview via <c>GET /interview/{id}</c>; null when it does not exist.</summary>
    public async Task<InterviewEntry?> GetInterviewAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"interview/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<InterviewEntry>(cancellationToken);
    }

    /// <summary>The paragraphs of the transcription of an interview via <c>GET /interview/{id}/paragraphs</c>, by recording then by rank.</summary>
    public async Task<IReadOnlyList<InterviewParagraphEntry>> GetInterviewParagraphsAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"interview/{Uri.EscapeDataString(id)}/paragraphs", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<InterviewParagraphEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Saves an interview via <c>POST /interview/update</c> (created, or a new version of it).</summary>
    public async Task<ServiceReturn> SaveInterviewAsync(InterviewEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("interview/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the interviews via <c>POST /interview/search</c> (the person, the notes, the tags).</summary>
    public async Task<IReadOnlyList<InterviewEntry>> SearchInterviewsAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("interview/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<InterviewEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> interviews changed last via <c>POST /interview/last</c>.</summary>
    public async Task<IReadOnlyList<InterviewEntry>> GetLastInterviewsAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("interview/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<InterviewEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Saves one paragraph of a transcription via <c>PUT /interviewparagraph</c> (the live transcription).</summary>
    public async Task<ServiceReturn> SaveInterviewParagraphAsync(InterviewParagraphEntry paragraph, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("interviewparagraph", paragraph, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Saves the transcription of one recording via <c>POST /interview/transcript</c>, in place of the one stored.</summary>
    public async Task<ServiceReturn> SaveInterviewTranscriptAsync(InterviewTranscriptRequest transcript, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("interview/transcript", transcript, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Marks an interview and its paragraphs deleted via <c>DELETE /interview/{id}</c> (nothing is erased).</summary>
    public async Task<ServiceReturn> DeleteInterviewAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"interview/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    // ── CVs (/cv) ──

    /// <summary>Loads a CV via <c>GET /cv/{id}</c>; null when it does not exist.</summary>
    public async Task<CvEntry?> GetCvAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"cv/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<CvEntry>(cancellationToken);
    }

    /// <summary>Saves a CV via <c>POST /cv/update</c> (created, or a new version of it).</summary>
    public async Task<ServiceReturn> SaveCvAsync(CvEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("cv/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the CVs via <c>POST /cv/search</c> (the person, the summary, the skills, the experiences…).</summary>
    public async Task<IReadOnlyList<CvEntry>> SearchCvsAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("cv/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<CvEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> CVs changed last via <c>POST /cv/last</c>.</summary>
    public async Task<IReadOnlyList<CvEntry>> GetLastCvsAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("cv/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<CvEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Searches the projects via <c>POST /project/search</c> (name, description, goals, tags, comment; "*": the newest ones).</summary>
    public async Task<IReadOnlyList<ProjectEntry>> SearchProjectsAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("project/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<ProjectEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Loads a project via <c>GET /project/{id}</c>; null when the server has none.</summary>
    public async Task<ProjectEntry?> GetProjectAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"project/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ProjectEntry>(cancellationToken);
    }

    /// <summary>Creates a project via <c>PUT /project</c>.</summary>
    public async Task<ServiceReturn> CreateProjectAsync(ProjectEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("project", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates a project via <c>POST /project/update</c>.</summary>
    public async Task<ServiceReturn> UpdateProjectAsync(ProjectEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("project/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Marks a project deleted via <c>DELETE /project/{id}</c> (nothing is erased).</summary>
    public async Task<ServiceReturn> DeleteProjectAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"project/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Marks a CV deleted via <c>DELETE /cv/{id}</c> (nothing is erased).</summary>
    public async Task<ServiceReturn> DeleteCvAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"cv/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    // ── Wiki (the pages of the wiki of Kaonomi, /wiki) ──

    /// <summary>Loads a page of the wiki via <c>GET /wiki/{id}</c>; null when it does not exist.</summary>
    public async Task<WikiPageEntry?> GetWikiPageAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"wiki/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<WikiPageEntry>(cancellationToken);
    }

    /// <summary>The pages that link to the page <paramref name="id"/> via <c>GET /wiki/{id}/backlinks</c>.</summary>
    public async Task<IReadOnlyList<WikiPageEntry>> GetWikiBacklinksAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"wiki/{Uri.EscapeDataString(id)}/backlinks", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<WikiPageEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Creates a page via <c>PUT /wiki</c> ("-1" when a page of that slug exists).</summary>
    public async Task<ServiceReturn> CreateWikiPageAsync(WikiPageEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("wiki", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Saves a new version of a page via <c>POST /wiki/update</c>.</summary>
    public async Task<ServiceReturn> UpdateWikiPageAsync(WikiPageEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("wiki/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the pages via <c>POST /wiki/search</c> (title, content, summary, tags, author).</summary>
    public async Task<IReadOnlyList<WikiPageEntry>> SearchWikiPagesAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("wiki/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<WikiPageEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> pages changed last via <c>POST /wiki/last</c>.</summary>
    public async Task<IReadOnlyList<WikiPageEntry>> GetLastWikiPagesAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("wiki/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<WikiPageEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Marks a page deleted via <c>DELETE /wiki/{id}</c> (it is never erased).</summary>
    public async Task<ServiceReturn> DeleteWikiPageAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"wiki/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    // ── BPMN (the AIMN diagrams of /bpmnai in the bpmn index, the infrastructures of /infra in the infra index: route "infra"; their XML and comment) ──

    /// <summary>Loads a diagram via <c>GET /bpmn/{id}</c>; null when it does not exist.</summary>
    public async Task<BpmnEntry?> GetBpmnAsync(string id, CancellationToken cancellationToken = default, string route = "bpmn")
    {
        using var response = await _httpClient.GetAsync($"{route}/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<BpmnEntry>(cancellationToken);
    }

    /// <summary>Creates a diagram via <c>PUT /bpmn</c> ("-1" when a diagram of that slug exists).</summary>
    public async Task<ServiceReturn> CreateBpmnAsync(BpmnEntry entry, CancellationToken cancellationToken = default, string route = "bpmn")
    {
        using var response = await _httpClient.PutJsonAsync(route, entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Saves a new version of a diagram via <c>POST /bpmn/update</c> (created when it does not exist).</summary>
    public async Task<ServiceReturn> UpdateBpmnAsync(BpmnEntry entry, CancellationToken cancellationToken = default, string route = "bpmn")
    {
        using var response = await _httpClient.PostJsonAsync($"{route}/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the diagrams via <c>POST /bpmn/search</c> (title, comment, summary, elements, tags, author).</summary>
    public async Task<IReadOnlyList<BpmnEntry>> SearchBpmnAsync(string term, int size, CancellationToken cancellationToken = default, string route = "bpmn")
    {
        using var response = await _httpClient.PostJsonAsync($"{route}/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<BpmnEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> diagrams changed last via <c>POST /bpmn/last</c>.</summary>
    public async Task<IReadOnlyList<BpmnEntry>> GetLastBpmnAsync(int size, CancellationToken cancellationToken = default, string route = "bpmn")
    {
        using var response = await _httpClient.PostJsonAsync($"{route}/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<BpmnEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Marks a diagram deleted via <c>DELETE /bpmn/{id}</c> (it is never erased).</summary>
    public async Task<ServiceReturn> DeleteBpmnAsync(string id, CancellationToken cancellationToken = default, string route = "bpmn")
    {
        using var response = await _httpClient.DeleteAsync($"{route}/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    // ── Mind maps (the maps of /mindmap, their FreeMind XML and comment in the mindmap index) ──

    /// <summary>Loads a map via <c>GET /mindmap/{id}</c>; null when it does not exist.</summary>
    public async Task<MindmapEntry?> GetMindmapAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"mindmap/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<MindmapEntry>(cancellationToken);
    }

    /// <summary>Creates a map via <c>PUT /mindmap</c> ("-1" when a map of that slug exists).</summary>
    public async Task<ServiceReturn> CreateMindmapAsync(MindmapEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("mindmap", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Saves a new version of a map via <c>POST /mindmap/update</c> (created when it does not exist).</summary>
    public async Task<ServiceReturn> UpdateMindmapAsync(MindmapEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("mindmap/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the maps via <c>POST /mindmap/search</c> (title, comment, summary, nodes, tags, author).</summary>
    public async Task<IReadOnlyList<MindmapEntry>> SearchMindmapAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("mindmap/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<MindmapEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> maps changed last via <c>POST /mindmap/last</c>.</summary>
    public async Task<IReadOnlyList<MindmapEntry>> GetLastMindmapAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("mindmap/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<MindmapEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Marks a map deleted via <c>DELETE /mindmap/{id}</c> (it is never erased).</summary>
    public async Task<ServiceReturn> DeleteMindmapAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"mindmap/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    // ── Ghost writer (the writings of /write: general information, actors, locations, chapters, in the ghostwriter index) ──

    /// <summary>Loads a writing via <c>GET /ghostwriter/{id}</c>; null when it does not exist.</summary>
    public async Task<GhostWriterEntry?> GetGhostWriterAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"ghostwriter/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<GhostWriterEntry>(cancellationToken);
    }

    /// <summary>Creates a writing via <c>PUT /ghostwriter</c> ("-1" when a writing of that slug exists).</summary>
    public async Task<ServiceReturn> CreateGhostWriterAsync(GhostWriterEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("ghostwriter", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Saves a new version of a writing via <c>POST /ghostwriter/update</c> (created when it does not exist).</summary>
    public async Task<ServiceReturn> UpdateGhostWriterAsync(GhostWriterEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("ghostwriter/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the writings via <c>POST /ghostwriter/search</c> (title, description, goals, audience, style, author).</summary>
    public async Task<IReadOnlyList<GhostWriterEntry>> SearchGhostWriterAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("ghostwriter/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<GhostWriterEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> writings changed last via <c>POST /ghostwriter/last</c>.</summary>
    public async Task<IReadOnlyList<GhostWriterEntry>> GetLastGhostWriterAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("ghostwriter/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<GhostWriterEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Adds an entry to the journal of a writing via <c>PUT /ghostwriter/journal</c> (the journal only grows).</summary>
    public async Task<ServiceReturn> AddWriterJournalAsync(WriterJournalEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("ghostwriter/journal", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>The <paramref name="size"/> last entries of the journal of the writing <paramref name="writingId"/> via <c>POST /ghostwriter/journal/search</c>, newest first.</summary>
    public async Task<IReadOnlyList<WriterJournalEntry>> GetWriterJournalAsync(string writingId, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("ghostwriter/journal/search", new CacheSearchRequest { Term = writingId, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<WriterJournalEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Marks a writing deleted via <c>DELETE /ghostwriter/{id}</c> (it is never erased).</summary>
    public async Task<ServiceReturn> DeleteGhostWriterAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"ghostwriter/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    // ── Presets (the voice, the effects, the 3D animation and the personality of the avatar) ──

    /// <summary>Creates a preset via <c>PUT /preset</c>.</summary>
    public async Task<ServiceReturn> CreatePresetAsync(PresetEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("preset", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates a preset via <c>POST /preset/update</c>.</summary>
    public async Task<ServiceReturn> UpdatePresetAsync(PresetEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("preset/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Loads a preset via <c>GET /preset/{id}</c>; null when the server returns nothing.</summary>
    public async Task<PresetEntry?> GetPresetAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"preset/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<PresetEntry>(cancellationToken);
    }

    /// <summary>Searches the presets via <c>POST /preset/search</c> (name, description, voice, animation, persona, tags; "*": the newest ones).</summary>
    public async Task<IReadOnlyList<PresetEntry>> SearchPresetsAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("preset/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<PresetEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent presets via <c>POST /preset/last</c>.</summary>
    public async Task<IReadOnlyList<PresetEntry>> GetLastPresetsAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("preset/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<PresetEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Marks a preset deleted via <c>DELETE /preset/{id}</c> (it is never erased).</summary>
    public async Task<ServiceReturn> DeletePresetAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"preset/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the cache builders via <c>POST /cachebuilder/search</c> (title, prompts, completions, model, comment, tags; "*": the newest ones).</summary>
    public async Task<IReadOnlyList<CacheBuilderEntry>> SearchCacheBuildersAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("cachebuilder/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<CacheBuilderEntry>>(cancellationToken) ?? [];
    }

    /// <summary>
    /// Writes the document of a recording of /recorder in the search engine via <c>POST /audiorecord/update</c> (created
    /// the first time, written over afterwards: its id is the one of the document).
    /// </summary>
    public async Task<ServiceReturn> SaveAudioRecordAsync(RecorderInfo info, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("audiorecord/update", info, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Loads a cache builder via <c>GET /cachebuilder/{id}</c>; null when the server returns nothing.</summary>
    public async Task<CacheBuilderEntry?> GetCacheBuilderAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"cachebuilder/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<CacheBuilderEntry>(cancellationToken);
    }

    /// <summary>Creates a cache builder via <c>PUT /cachebuilder</c>.</summary>
    public async Task<ServiceReturn> CreateCacheBuilderAsync(CacheBuilderEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("cachebuilder", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates a cache builder via <c>POST /cachebuilder/update</c>.</summary>
    public async Task<ServiceReturn> UpdateCacheBuilderAsync(CacheBuilderEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("cachebuilder/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>
    /// Publishes a saved cache builder via <c>POST /cachebuilder/{id}/publish</c>: every prompt and completion pair
    /// becomes a document of the index <c>cache</c>.
    /// </summary>
    public async Task<ServiceReturn> PublishCacheBuilderAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync($"cachebuilder/{Uri.EscapeDataString(id)}/publish", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches symbolic patterns via <c>POST /symbolicpattern/search</c>.</summary>
    public async Task<IReadOnlyList<SymbolicPatternEntry>> SearchSymbolicPatternsAsync(
        string term,
        int size,
        CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostJsonAsync("symbolicpattern/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<SymbolicPatternEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Up to <paramref name="size"/> symbolic patterns, deleted ones included, via <c>POST /symbolicpattern/last</c>.</summary>
    public async Task<IReadOnlyList<SymbolicPatternEntry>> GetLastSymbolicPatternsAsync(
        int size,
        CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = "*", Size = size };

        using var response = await _httpClient.PostJsonAsync("symbolicpattern/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<SymbolicPatternEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Updates a symbolic pattern via <c>POST /symbolicpattern/update</c>.</summary>
    public async Task<ServiceReturn> UpdateSymbolicPatternAsync(
        SymbolicPatternEntry entry,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("symbolicpattern/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Creates a symbolic pattern via <c>PUT /symbolicpattern</c>.</summary>
    public async Task<ServiceReturn> CreateSymbolicPatternAsync(
        SymbolicPatternEntry entry,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("symbolicpattern", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the journal (<c>text</c> field only) via <c>POST /diary/search</c>; deleted entries are left out.</summary>
    public async Task<IReadOnlyList<DiaryEntry>> SearchDiaryAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("diary/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<DiaryEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent journal entries, newest first, via <c>POST /diary/last</c>.</summary>
    public async Task<IReadOnlyList<DiaryEntry>> GetLastDiaryAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("diary/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<DiaryEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Creates a journal entry via <c>PUT /diary</c>.</summary>
    public async Task<ServiceReturn> CreateDiaryAsync(DiaryEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("diary", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates a journal entry via <c>POST /diary/update</c>.</summary>
    public async Task<ServiceReturn> UpdateDiaryAsync(DiaryEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("diary/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Deletes a journal entry via <c>DELETE /diary/{id}</c> (soft delete: it is marked isDeleted).</summary>
    public async Task<ServiceReturn> DeleteDiaryAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"diary/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the YouTube videos (title and description) via <c>POST /youtubevideo/search</c>.</summary>
    public async Task<IReadOnlyList<YoutubeVideoEntry>> SearchYoutubeVideosAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("youtubevideo/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<YoutubeVideoEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent YouTube videos, newest first, via <c>POST /youtubevideo/last</c>.</summary>
    public async Task<IReadOnlyList<YoutubeVideoEntry>> GetLastYoutubeVideosAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("youtubevideo/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<YoutubeVideoEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Adds a YouTube video via <c>PUT /youtubevideo</c>.</summary>
    public async Task<ServiceReturn> CreateYoutubeVideoAsync(YoutubeVideoEntry video, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("youtubevideo", video, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates a YouTube video via <c>POST /youtubevideo/update</c>.</summary>
    public async Task<ServiceReturn> UpdateYoutubeVideoAsync(YoutubeVideoEntry video, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("youtubevideo/update", video, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Deletes a YouTube video via <c>DELETE /youtubevideo/{id}</c>.</summary>
    public async Task<ServiceReturn> DeleteYoutubeVideoAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"youtubevideo/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Loads a document via <c>GET /document/{id}</c>; null when it does not exist.</summary>
    public async Task<DocumentEntry?> GetDocumentAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"document/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<DocumentEntry>(cancellationToken);
    }

    /// <summary>Fuzzy search of the documents (title, description, content) via <c>POST /document/search</c>.</summary>
    public async Task<IReadOnlyList<DocumentEntry>> SearchDocumentsAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("document/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<DocumentEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recently saved documents, newest first, via <c>POST /document/last</c>.</summary>
    public async Task<IReadOnlyList<DocumentEntry>> GetLastDocumentsAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("document/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<DocumentEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Saves a document: <c>PUT /document</c> for a new one (no id), <c>POST /document/update</c> otherwise.</summary>
    public async Task<ServiceReturn> SaveDocumentAsync(DocumentEntry document, CancellationToken cancellationToken = default)
    {
        using var response = document.Id is null
            ? await _httpClient.PutJsonAsync("document", document, cancellationToken)
            : await _httpClient.PostJsonAsync("document/update", document, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
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

        using var response = await _httpClient.PostJsonAsync("idea/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<IdeaEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent ideas, newest first, via <c>POST /idea/last</c>.</summary>
    public async Task<IReadOnlyList<IdeaEntry>> GetLastIdeasAsync(int size, CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = "*", Size = size };

        using var response = await _httpClient.PostJsonAsync("idea/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<IdeaEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Creates an idea via <c>PUT /idea</c>.</summary>
    public async Task<ServiceReturn> CreateIdeaAsync(IdeaEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("idea", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates an idea via <c>POST /idea/update</c>.</summary>
    public async Task<ServiceReturn> UpdateIdeaAsync(IdeaEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("idea/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Deletes an idea via <c>DELETE /idea/{id}</c>.</summary>
    public async Task<ServiceReturn> DeleteIdeaAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"idea/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>
    /// The <paramref name="size"/> most recent tasks, newest first, without the deleted ones, via <c>POST /task/last</c>.
    /// </summary>
    public async Task<IReadOnlyList<TaskEntry>> GetLastTasksAsync(int size, CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = "*", Size = size };

        using var response = await _httpClient.PostJsonAsync("task/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<TaskEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Creates a task via <c>PUT /task</c>.</summary>
    public async Task<ServiceReturn> CreateTaskAsync(TaskEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("task", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates a task via <c>POST /task/update</c>.</summary>
    public async Task<ServiceReturn> UpdateTaskAsync(TaskEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("task/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Soft-deletes a task via <c>DELETE /task/{id}</c> (it gets <c>isDeleted = true</c>).</summary>
    public async Task<ServiceReturn> DeleteTaskAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"task/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>
    /// Searches intels in all their text fields via <c>POST /intel/search</c>; <c>*</c> returns the newest ones. Deleted
    /// intels are left out.
    /// </summary>
    public async Task<IReadOnlyList<IntelEntry>> SearchIntelsAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostJsonAsync("intel/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<IntelEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent intels, newest first, via <c>POST /intel/last</c>.</summary>
    public async Task<IReadOnlyList<IntelEntry>> GetLastIntelsAsync(int size, CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = "*", Size = size };

        using var response = await _httpClient.PostJsonAsync("intel/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<IntelEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Creates an intel via <c>PUT /intel</c>.</summary>
    public async Task<ServiceReturn> CreateIntelAsync(IntelEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("intel", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates an intel via <c>POST /intel/update</c>.</summary>
    public async Task<ServiceReturn> UpdateIntelAsync(IntelEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("intel/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Soft-deletes an intel via <c>DELETE /intel/{id}</c> (it gets <c>isDeleted = true</c>).</summary>
    public async Task<ServiceReturn> DeleteIntelAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"intel/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Saves an activity of the journal via <c>PUT /useractivity</c> (index useractivities).</summary>
    public async Task<ServiceReturn> SaveUserActivityAsync(UserActivityEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("useractivity", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>
    /// The activities of the journal matching <paramref name="term"/> via <c>POST /useractivity/search</c>, or the
    /// <paramref name="size"/> newest via <c>POST /useractivity/last</c> when it is empty or <c>*</c>.
    /// </summary>
    public async Task<IReadOnlyList<UserActivityEntry>> GetUserActivitiesAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        var last = string.IsNullOrWhiteSpace(term) || term.Trim() == "*";
        var request = new CacheSearchRequest { Term = last ? "*" : term.Trim(), Size = size };

        using var response = await _httpClient.PostJsonAsync(last ? "useractivity/last" : "useractivity/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<UserActivityEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Saves the score of a finished game via <c>PUT /gamescore</c>.</summary>
    public async Task<ServiceReturn> SaveGameScoreAsync(GameScoreEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("gamescore", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>The <paramref name="size"/> best scores of <paramref name="game"/>, the highest first, via <c>POST /gamescore/best</c>.</summary>
    public async Task<IReadOnlyList<GameScoreEntry>> GetBestGameScoresAsync(string game, int size, CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = game, Size = size };

        using var response = await _httpClient.PostJsonAsync("gamescore/best", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<GameScoreEntry>>(cancellationToken) ?? [];
    }

    /// <summary>
    /// Searches collection plans in all their text fields via <c>POST /collectionplan/search</c>; <c>*</c> returns the
    /// newest ones. Deleted plans are left out.
    /// </summary>
    public async Task<IReadOnlyList<CollectionPlanEntry>> SearchCollectionPlansAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostJsonAsync("collectionplan/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<CollectionPlanEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent collection plans, newest first, via <c>POST /collectionplan/last</c>.</summary>
    public async Task<IReadOnlyList<CollectionPlanEntry>> GetLastCollectionPlansAsync(int size, CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = "*", Size = size };

        using var response = await _httpClient.PostJsonAsync("collectionplan/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<CollectionPlanEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Creates a collection plan via <c>PUT /collectionplan</c>.</summary>
    public async Task<ServiceReturn> CreateCollectionPlanAsync(CollectionPlanEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("collectionplan", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Updates a collection plan via <c>POST /collectionplan/update</c>.</summary>
    public async Task<ServiceReturn> UpdateCollectionPlanAsync(CollectionPlanEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("collectionplan/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Soft-deletes a collection plan via <c>DELETE /collectionplan/{id}</c> (it gets <c>isDeleted = true</c>).</summary>
    public async Task<ServiceReturn> DeleteCollectionPlanAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"collectionplan/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
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

        using var response = await _httpClient.PostJsonAsync("ontology/search", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<OntologyEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Loads up to <paramref name="size"/> ontology nodes via <c>POST /ontology/last</c> (<c>*</c> matches all).</summary>
    public async Task<IReadOnlyList<OntologyEntry>> GetLastOntologiesAsync(
        string term,
        int size,
        CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostJsonAsync("ontology/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<OntologyEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Updates an ontology node via <c>POST /ontology/update</c>.</summary>
    public async Task<ServiceReturn> UpdateOntologyAsync(OntologyEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("ontology/update", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Creates an ontology node via <c>PUT /ontology</c>.</summary>
    public async Task<ServiceReturn> CreateOntologyAsync(OntologyEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("ontology", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Client of the running application (set by the main window), used by windows without their own client.</summary>
    public static KaonomiAssistantService? Shared { get; set; }

    /// <summary>Sends an anonymous note via <c>PUT /whitenote</c>.</summary>
    public async Task<ServiceReturn> CreateWhiteNoteAsync(WhiteNoteEntry note, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("whitenote", note, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Saves an annotation via <c>PUT /annotation</c> (created without an id, replaced with one).</summary>
    public async Task<ServiceReturn> SaveAnnotationAsync(AnnotationEntry annotation, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("annotation", annotation, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the annotations via <c>POST /annotation/search</c> (empty or <c>*</c>: the newest ones, <c>POST /annotation/last</c>).</summary>
    public async Task<List<AnnotationEntry>> SearchAnnotationsAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        var route = string.IsNullOrWhiteSpace(term) || term.Trim() == "*" ? "annotation/last" : "annotation/search";
        using var response = await _httpClient.PostJsonAsync(route, new CacheSearchRequest { Term = string.IsNullOrWhiteSpace(term) ? "*" : term.Trim(), Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<AnnotationEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Deletes an annotation via <c>DELETE /annotation/{id}</c> (soft delete).</summary>
    public async Task<ServiceReturn> DeleteAnnotationAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"annotation/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Sends an activity log entry via <c>PUT /log</c> (used by <see cref="ActivityLog"/>).</summary>
    public async Task SaveLogAsync(LogEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("log", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Erases ALL the logs via <c>DELETE /log</c>; <c>ReturnedId</c> is the number erased.</summary>
    public async Task<ServiceReturn> DeleteAllLogsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync("log", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>All the logs of every application of the last <paramref name="minutes"/> minutes, newest first, via <c>GET /log/since</c>.</summary>
    public async Task<IReadOnlyList<LogEntry>> GetLogsSinceAsync(int minutes, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"log/since?minutes={minutes}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<LogEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent logs of every application, newest first, via <c>GET /log/last</c>.</summary>
    public async Task<IReadOnlyList<LogEntry>> GetLastLogsAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"log/last?size={size}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<LogEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Token consumption of the LLM calls of the last <paramref name="minutes"/> minutes, via <c>GET /tokensusage/last</c>.</summary>
    public async Task<IReadOnlyList<TokenUsageEntry>> GetTokensUsageAsync(int minutes, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"tokensusage/last?minutes={minutes}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<TokenUsageEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Saves a prompt and its completion via <c>PUT /completion</c>.</summary>
    public async Task<ServiceReturn> SaveCompletionAsync(CompletionEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("completion", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Searches the completions (prompt and completion) via <c>POST /completion/search</c>.</summary>
    public async Task<IReadOnlyList<CompletionEntry>> SearchCompletionsAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("completion/search", new CacheSearchRequest { Term = term, Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<CompletionEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The advanced search of the completions via <c>POST /completion/query</c> (text, period, models, languages, topics, collections, order).</summary>
    public async Task<IReadOnlyList<CompletionEntry>> QueryCompletionsAsync(CompletionQuery query, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("completion/query", query, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<CompletionEntry>>(cancellationToken) ?? [];
    }

    /// <summary>The <paramref name="size"/> most recent completions, newest first, via <c>POST /completion/last</c>.</summary>
    public async Task<IReadOnlyList<CompletionEntry>> GetLastCompletionsAsync(int size, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("completion/last", new CacheSearchRequest { Term = "*", Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<CompletionEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Deletes a completion via <c>DELETE /completion/{id}</c>.</summary>
    public async Task<ServiceReturn> DeleteCompletionAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"completion/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Sends a measure of the <c>/system</c> window via <c>PUT /systemlog</c>.</summary>
    public async Task SaveSystemLogAsync(SystemLogEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("systemlog", entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Route of the evaluation service on the Kaonomi server.</summary>
    private const string EvaluationRoute = "evaluation";

    /// <summary>Sends a like / dislike with its explanation via <c>PUT /evaluation</c>.</summary>
    public async Task<ServiceReturn> CreateEvaluationAsync(EvaluationEntry entry, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync(EvaluationRoute, entry, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Loads the latest ticker messages via <c>POST /messageflow/last</c>.</summary>
    public async Task<IReadOnlyList<MessageFlowEntry>> GetLastMessageFlowsAsync(
        string term,
        int size,
        CancellationToken cancellationToken = default)
    {
        var request = new CacheSearchRequest { Term = term, Size = size };

        using var response = await _httpClient.PostJsonAsync("messageflow/last", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadJsonAsync<List<MessageFlowEntry>>(cancellationToken) ?? [];
    }

    /// <summary>
    /// The relations matching <paramref name="term"/> (nature, title, description, windows, objects, information) via
    /// <c>POST /relation/search</c>; empty or <c>*</c>: the newest ones via <c>POST /relation/last</c>.
    /// </summary>
    public async Task<IReadOnlyList<RelationEntry>> SearchRelationsAsync(string term, int size, CancellationToken cancellationToken = default)
    {
        var route = string.IsNullOrWhiteSpace(term) || term.Trim() == "*" ? "relation/last" : "relation/search";
        using var response = await _httpClient.PostJsonAsync(route, new CacheSearchRequest { Term = string.IsNullOrWhiteSpace(term) ? "*" : term.Trim(), Size = size }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<List<RelationEntry>>(cancellationToken) ?? [];
    }

    /// <summary>Saves a link between two windows via <c>PUT /relation</c> (index <c>relation</c>).</summary>
    public async Task<ServiceReturn> SaveRelationAsync(RelationEntry relation, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("relation", relation, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>Deletes a relation via <c>DELETE /relation/{id}</c> (soft delete: it is marked isDeleted, with all its versions).</summary>
    public async Task<ServiceReturn> DeleteRelationAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync($"relation/{Uri.EscapeDataString(id)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>One image analysis by its id via <c>GET /imageanalysis/{id}</c>; null when it does not exist.</summary>
    public async Task<ImageAnalysisEntry?> GetImageAnalysisAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"imageanalysis/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ImageAnalysisEntry>(cancellationToken);
    }

    /// <summary>The analysis of the picture at <paramref name="path"/> via <c>GET /imageanalysis/image</c>; null when it has none yet.</summary>
    public async Task<ImageAnalysisEntry?> GetImageAnalysisOfImageAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"imageanalysis/image?path={Uri.EscapeDataString(path)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ImageAnalysisEntry>(cancellationToken);
    }

    /// <summary>Saves the analysis of a picture via <c>PUT /imageanalysis</c> (one per picture: the server gives its id).</summary>
    public async Task<ServiceReturn> SaveImageAnalysisAsync(ImageAnalysisEntry analysis, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutJsonAsync("imageanalysis", analysis, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ServiceReturn>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>
    /// What an image generation accepts (styles, performances, aspect ratios, models) and the defaults of Fooocus, via
    /// <c>GET /image/options</c>; throws when the server or the Fooocus API does not answer.
    /// </summary>
    public async Task<ImageOptions> GetImageOptionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("image/options", cancellationToken);
        await EnsureFooocusAsync(response, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ImageOptions>(cancellationToken) ?? new ImageOptions();
    }

    /// <summary>
    /// Generates images with Fooocus via <c>POST /image/generate</c> (it answers once they are done: about a minute per
    /// image); a failure of Fooocus comes back in <see cref="ImageGenerationResult.Error"/>, other errors throw.
    /// </summary>
    public async Task<ImageGenerationResult> GenerateImagesAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostJsonAsync("image/generate", request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.BadRequest
            && await response.Content.ReadJsonAsync<ImageGenerationResult>(cancellationToken) is { Error: { } } failed)
        {
            // The Fooocus API is not started: the error says how to start it.
            if (failed.Error.Contains("not reachable", StringComparison.OrdinalIgnoreCase))
                failed.Error = $"Fooocus is not started ({failed.Error}). Start its API with: {FooocusApiCommand}";
            return failed;
        }
        await EnsureFooocusAsync(response, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadJsonAsync<ImageGenerationResult>(cancellationToken)
               ?? throw new HttpRequestException("The Kaonomi server returned an empty response.");
    }

    /// <summary>How to start the Fooocus API: from /process, or by hand in the Python of the installed Fooocus.</summary>
    public const string FooocusApiCommand = "/process (Fooocus API; fooocus.directory in config.yml), or KAONOMI.Server/Fooocus/fooocus_api.py --fooocus <folder of Fooocus> in the Python of Fooocus";

    /// <summary>
    /// The server answered 503 for an image route: the Fooocus API (127.0.0.1:8888) is not started; the error says so,
    /// with the command that starts it, rather than the raw answer of the server.
    /// </summary>
    private static async Task EnsureFooocusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
            return;
        var detail = string.Empty;
        try
        {
            detail = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement
                .TryGetProperty("detail", out var value) ? value.GetString() ?? string.Empty : string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
        }
        throw new HttpRequestException(
            $"Fooocus is not started{(detail.Length > 0 ? $" ({detail})" : string.Empty)}. Start its API with: {FooocusApiCommand}",
            null,
            response.StatusCode);
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
