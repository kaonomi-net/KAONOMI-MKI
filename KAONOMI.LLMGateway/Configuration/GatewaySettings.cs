using System.Text.Json;
using System.Text.Json.Serialization;

namespace KAONOMI.LLMGateway.Configuration;

/// <summary>The settings of the gateway: <c>config/llmgateway.json</c>, then what was changed at run time (state.json).</summary>
public sealed class GatewaySettings
{
    public int Port { get; set; } = 11435;
    public string Upstream { get; set; } = "http://localhost:11434";

    /// <summary>
    /// The API of <see cref="Upstream"/>: "ollama", or "lmstudio" for LM Studio (http://localhost:1234), which speaks
    /// the API of OpenAI only ("openai" is the same).
    /// </summary>
    public string UpstreamProvider { get; set; } = "ollama";

    /// <summary>True when the upstream speaks the API of OpenAI only (LM Studio), false for Ollama.</summary>
    [JsonIgnore]
    public bool UpstreamIsOpenAi =>
        UpstreamProvider.Trim().Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant() is "lmstudio" or "openai" or "openaicompatible";

    /// <summary>The name of the upstream for the messages: "LM Studio" or "Ollama".</summary>
    [JsonIgnore]
    public string UpstreamName => UpstreamIsOpenAi ? "LM Studio" : "Ollama";

    /// <summary><see cref="Upstream"/> without its last / nor a /v1 given with it: the paths of the requests come after.</summary>
    [JsonIgnore]
    public string UpstreamBase
    {
        get
        {
            var url = Upstream.Trim().TrimEnd('/');
            return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
        }
    }
    public string DataDirectory { get; set; } = "~/kaonomi/llmgateway";
    public CacheSettings Cache { get; set; } = new();
    public RoutingSettings Routing { get; set; } = new();
    public Dictionary<string, List<string>> Gazetteers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int LogSize { get; set; } = 500;

    /// <summary><see cref="DataDirectory"/> with ~ replaced by the home folder, made absolute.</summary>
    [JsonIgnore]
    public string ResolvedDataDirectory
    {
        get
        {
            var path = DataDirectory.Trim();
            if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
            return Path.GetFullPath(path);
        }
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// The settings of <paramref name="file"/> (the defaults when it is null or missing), the keys starting with // being
    /// comments.
    /// </summary>
    public static GatewaySettings Load(string? file)
    {
        if (file is null || !File.Exists(file))
            return new GatewaySettings();
        return JsonSerializer.Deserialize<GatewaySettings>(File.ReadAllText(file), Json) ?? new GatewaySettings();
    }

    /// <summary>
    /// The file of the project folder (dotnet run from the project or from the solution folder), else the copy of the
    /// build; null when there is none.
    /// </summary>
    public static string? Find(string? given) =>
        given ?? new[]
        {
            "config/llmgateway.json", "llmgateway.json", "KAONOMI.LLMGateway/config/llmgateway.json",
            Path.Combine(AppContext.BaseDirectory, "config", "llmgateway.json")
        }.FirstOrDefault(File.Exists);
}

public sealed class CacheSettings
{
    public bool Enabled { get; set; } = true;
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public double Threshold { get; set; } = 0.92;
    public double LexicalThreshold { get; set; } = 0.9;
    public double TtlHours { get; set; } = 72;
    public int MaxEntries { get; set; } = 5000;
    public bool EntityGuard { get; set; } = true;
    /// <summary>The answers of the model kept for a prompt before the cache answers it (one of them at random).</summary>
    public int MinAnswers { get; set; } = 3;
}

public sealed class RoutingSettings
{
    public bool Enabled { get; set; } = true;
    public bool OverrideRequested { get; set; } = true;
    public string DefaultModel { get; set; } = "gpt-oss:20b";
    /// <summary>
    /// The model an answer of <see cref="DefaultModel"/> that the user disliked is asked again to
    /// (<c>POST /gateway/feedback/dislike</c>); empty: a disliked answer is only taken out of the cache.
    /// </summary>
    public string EscalationModel { get; set; } = "";
    public List<Routing.RouteRule> Rules { get; set; } = [];
}
