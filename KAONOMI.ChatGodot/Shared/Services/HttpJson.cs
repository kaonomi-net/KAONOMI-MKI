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

    /// <summary>Reads the JSON of <paramref name="content"/>.</summary>
    public static async Task<T?> ReadJsonAsync<T>(this HttpContent content, CancellationToken cancellationToken = default)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
    }

    private static StringContent Content<T>(T value) =>
        new(JsonSerializer.Serialize(value, Options), Encoding.UTF8, "application/json");
}
