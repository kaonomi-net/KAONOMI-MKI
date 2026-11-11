using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>
/// JSON over HTTP for the services, as <c>System.Net.Http.Json</c> does it (the same web options: camelCase names,
/// case-insensitive reading, numbers read from strings) but with the <c>System.Text.Json</c> this assembly is built
/// with. <c>System.Net.Http.Json</c> belongs to the .NET runtime and uses its own <c>System.Text.Json</c>: where an
/// application loads another copy beside it (the Godot chat loads its assemblies in a load context of their own), a
/// <c>JsonObject</c> was then written as a plain object (its Parent, its Root…) and the <c>[JsonPropertyName]</c> of the
/// models ignored.
/// </summary>
public static class HttpJson
{
    /// <summary>The options of <c>System.Net.Http.Json</c> (JsonSerializerDefaults.Web).</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>POSTs <paramref name="value"/> as JSON.</summary>
    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string? requestUri, T value, CancellationToken cancellationToken = default) =>
        client.PostAsync(requestUri, Content(value), cancellationToken);

    /// <summary>PUTs <paramref name="value"/> as JSON.</summary>
    public static Task<HttpResponseMessage> PutJsonAsync<T>(this HttpClient client, string? requestUri, T value, CancellationToken cancellationToken = default) =>
        client.PutAsync(requestUri, Content(value), cancellationToken);

    /// <summary>GETs <paramref name="requestUri"/> and reads its JSON answer (an error status throws).</summary>
    public static async Task<T?> GetJsonAsync<T>(this HttpClient client, string? requestUri, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadJsonAsync<T>(cancellationToken);
    }

    /// <summary>Above this size (bytes), an answer is deserialized on the thread pool, not on the thread of the caller.</summary>
    private const int LargeAnswer = 16 * 1024;

    /// <summary>
    /// Reads the JSON of <paramref name="content"/>. The body is already in memory when it gets here (the handlers
    /// of the services buffer it), so reading it would run on the thread of the caller, the one of the interface: a
    /// large answer (a list of documents, pictures in base64) is deserialized on the thread pool instead.
    /// </summary>
    public static async Task<T?> ReadJsonAsync<T>(this HttpContent content, CancellationToken cancellationToken = default)
    {
        var bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
            return default;
        return bytes.Length < LargeAnswer
            ? JsonSerializer.Deserialize<T>(bytes, Options)
            : await Task.Run(() => JsonSerializer.Deserialize<T>(bytes, Options), cancellationToken).ConfigureAwait(false);
    }

    private static HttpContent Content<T>(T value) => new JsonBody<T>(value);

    /// <summary>
    /// A JSON body written straight to the request when it is sent: the handlers of the services send on the thread
    /// pool, so a large body (pictures in base64) is no longer serialized, then copied, on the thread of the interface.
    /// </summary>
    private sealed class JsonBody<T> : HttpContent
    {
        private readonly T _value;

        public JsonBody(T value)
        {
            _value = value;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            JsonSerializer.SerializeAsync(stream, _value, Options);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
