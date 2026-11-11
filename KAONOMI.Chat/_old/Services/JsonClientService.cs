using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>Minimal HTTP client used by the <c>/json</c> window.</summary>
public sealed class JsonClientService
{
    // Sent from the thread pool: the proxy lookup of Windows must not freeze the windows.
    private readonly HttpClient _httpClient = new(HttpHandlers.Create())
    {
        // Cancellation is handled by the caller.
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>Sends an HTTP request with JSON accept header and optional JSON body.</summary>
    public async Task<JsonClientResponse> SendAsync(
        string method,
        string url,
        string? body,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(body))
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var stopwatch = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        return new JsonClientResponse((int)response.StatusCode, response.ReasonPhrase, content, stopwatch.Elapsed);
    }
}
