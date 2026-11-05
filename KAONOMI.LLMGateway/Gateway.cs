using System.Diagnostics;
using System.Text.Json;
using KAONOMI.LLMGateway.Cache;
using KAONOMI.LLMGateway.Configuration;
using KAONOMI.LLMGateway.Entities;
using KAONOMI.LLMGateway.Routing;

namespace KAONOMI.LLMGateway;

/// <summary>
/// The state of the gateway: its settings (those of the file, then the ones changed at run time, kept in state.json),
/// the extractor of entities, the router, the cache, the embedder in use, the models installed in Ollama (read every
/// minute) and the log of the requests.
/// </summary>
public sealed class Gateway : IDisposable
{
    private readonly string _stateFile;
    private readonly Timer _models;
    private IReadOnlyCollection<string>? _installed;
    private volatile CacheSettings _cacheSettings;
    private volatile RoutingSettings _routingSettings;

    public Gateway(GatewaySettings settings, HttpClient http)
    {
        Settings = settings;
        Http = http;
        Directory.CreateDirectory(settings.ResolvedDataDirectory);
        _stateFile = Path.Combine(settings.ResolvedDataDirectory, "state.json");
        LoadState();
        _cacheSettings = settings.Cache;
        _routingSettings = settings.Routing;
        Extractor = new EntityExtractor(settings.Gazetteers);
        Router = new ModelRouter(() => _routingSettings, () => _installed);
        Cache = new SemanticCache(() => _cacheSettings, Path.Combine(settings.ResolvedDataDirectory, "cache.json"));
        Log = new RequestLog(() => Settings.LogSize);
        Embedder = new LexicalEmbedder();
        _models = new Timer(_ => _ = RefreshModelsAsync(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    public GatewaySettings Settings { get; }
    public HttpClient Http { get; }
    public EntityExtractor Extractor { get; }
    public ModelRouter Router { get; }
    public SemanticCache Cache { get; }
    public RequestLog Log { get; }
    public IEmbedder Embedder { get; private set; }
    public string EmbedderNote { get; private set; } = "the local embedding of the words, until Ollama is read";
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public bool UpstreamOk { get; private set; }
    public IReadOnlyCollection<string> InstalledModels => _installed ?? [];

    public CacheSettings CacheSettings => _cacheSettings;
    public RoutingSettings RoutingSettings => _routingSettings;

    /// <summary>The settings of the cache replaced (the embedder is chosen again), kept in state.json.</summary>
    public async Task SetCacheSettingsAsync(CacheSettings settings)
    {
        _cacheSettings = settings;
        Settings.Cache = settings;
        SaveState();
        await RefreshModelsAsync();
    }

    /// <summary>The settings of the routing (and its rules) replaced, kept in state.json.</summary>
    public void SetRoutingSettings(RoutingSettings settings)
    {
        _routingSettings = settings;
        Settings.Routing = settings;
        SaveState();
    }

    /// <summary>Forgets what was changed at run time: the settings of the file come back at the next start.</summary>
    public bool ResetState()
    {
        if (!File.Exists(_stateFile))
            return false;
        File.Delete(_stateFile);
        return true;
    }

    /// <summary>
    /// Reads the models of Ollama: the router skips a rule whose model is missing; the cache uses the embedding model
    /// when it is installed, the local embedding of the words otherwise.
    /// </summary>
    public async Task RefreshModelsAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await Http.GetAsync($"{Settings.Upstream.TrimEnd('/')}/api/tags", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            _installed = document.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "").Where(n => n.Length > 0).ToList();
            UpstreamOk = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            UpstreamOk = false;
            EmbedderNote = $"Ollama not reached at {Settings.Upstream} ({ex.Message}): the local embedding of the words";
            if (Embedder is not LexicalEmbedder)
                Embedder = new LexicalEmbedder();
            return;
        }
        var model = _cacheSettings.EmbeddingModel?.Trim();
        if (!string.IsNullOrEmpty(model) && ModelRouter.IsInstalled(_installed, model))
        {
            var name = _installed.First(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase) || string.Equals(m, model + ":latest", StringComparison.OrdinalIgnoreCase));
            if (Embedder.Name != "ollama:" + name)
            {
                var candidate = new OllamaEmbedder(Http, Settings.Upstream, name);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await candidate.EmbedAsync("ping", timeout.Token);
                    Embedder = candidate;
                    EmbedderNote = $"the embedding model {name} of Ollama";
                    Console.WriteLine($"[cache] embeddings by {name}");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    Embedder = new LexicalEmbedder();
                    EmbedderNote = $"{name} failed ({ex.Message}): the local embedding of the words";
                }
            }
        }
        else
        {
            if (Embedder is not LexicalEmbedder)
                Embedder = new LexicalEmbedder();
            EmbedderNote = string.IsNullOrEmpty(model)
                ? "no embedding model set: the local embedding of the words"
                : $"{model} is not installed in Ollama (ollama pull {model}): the local embedding of the words";
        }
    }

    /// <summary>The embedding of a prompt; the local one when the model of Ollama fails (the cache then misses).</summary>
    public async Task<(IEmbedder Embedder, float[] Vector)> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var embedder = Embedder;
        try
        {
            return (embedder, await embedder.EmbedAsync(text, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException && embedder is not LexicalEmbedder)
        {
            Console.WriteLine($"[cache] {embedder.Name} failed: {ex.Message}");
            var lexical = new LexicalEmbedder();
            return (lexical, await lexical.EmbedAsync(text, cancellationToken));
        }
    }

    private sealed class State
    {
        public CacheSettings? Cache { get; set; }
        public RoutingSettings? Routing { get; set; }
    }

    private void LoadState()
    {
        if (!File.Exists(_stateFile))
            return;
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_stateFile), GatewaySettings.Json);
            if (state?.Cache is { } cache)
                Settings.Cache = cache;
            if (state?.Routing is { } routing)
                Settings.Routing = routing;
            Console.WriteLine($"[state] settings changed at run time read from {_stateFile} (POST /gateway/reset forgets them)");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[state] {_stateFile} ignored: {ex.Message}");
        }
    }

    private void SaveState()
    {
        var temporary = _stateFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new State { Cache = _cacheSettings, Routing = _routingSettings }, GatewaySettings.Json));
        File.Move(temporary, _stateFile, overwrite: true);
    }

    public static long Elapsed(Stopwatch stopwatch) => (long)stopwatch.Elapsed.TotalMilliseconds;

    public void Dispose()
    {
        _models.Dispose();
        Cache.Dispose();
    }
}
