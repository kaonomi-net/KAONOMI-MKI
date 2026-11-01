using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>
/// HTTP handlers of the chat clients, built so that a request never holds the UI thread. On Windows, the first part of
/// a request runs synchronously on the calling thread (the UI thread for a click or a timer), including the lookup of
/// the system proxy (WinHTTP auto-detection, WPAD), which can take seconds and freezes every window meanwhile.
/// </summary>
public static class HttpHandlers
{
    /// <summary>Time allowed to open the connection (the server down must not keep a request pending for long).</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Handler for a client of <paramref name="baseAddress"/>: the request is sent from the thread pool, and a local
    /// server (localhost, 127.0.0.1, ::1) is reached directly, without asking the system for a proxy.
    /// </summary>
    public static HttpMessageHandler Create(Uri? baseAddress = null) =>
        new OffUiThreadHandler(new SocketsHttpHandler
        {
            UseProxy = baseAddress is null || !baseAddress.IsLoopback,
            ConnectTimeout = ConnectTimeout
        });

    /// <summary>Wraps <paramref name="inner"/> so that its requests are sent from the thread pool.</summary>
    public static HttpMessageHandler OffUiThread(HttpMessageHandler inner) => new OffUiThreadHandler(inner);

    /// <summary>
    /// <see cref="Create"/> with a time limit on every request, its answer read in full within it
    /// (<paramref name="deadline"/> gives the limit of a request): a server that takes the connection then stalls
    /// (a search engine busy, a model loading) fails the request with its reason instead of keeping a window waiting
    /// for ever. The caller may still cancel it sooner.
    /// </summary>
    public static HttpMessageHandler CreateWithDeadline(Uri baseAddress, Func<HttpRequestMessage, TimeSpan> deadline) =>
        new OffUiThreadHandler(new DeadlineHandler(new SocketsHttpHandler
        {
            UseProxy = !baseAddress.IsLoopback,
            ConnectTimeout = ConnectTimeout
        }, deadline));

    /// <summary>Fails a request (an <see cref="HttpRequestException"/>) that has not been answered in full within its time limit.</summary>
    private sealed class DeadlineHandler(HttpMessageHandler inner, Func<HttpRequestMessage, TimeSpan> deadlineOf) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var limit = deadlineOf(request);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(limit);
            try
            {
                var response = await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
                // The answer read here, within the limit (read later, a stalled body would wait for ever).
                var body = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
                var content = new ByteArrayContent(body);
                foreach (var header in response.Content.Headers)
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content.Dispose();
                response.Content = content;
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException($"The server did not answer {request.Method} {request.RequestUri?.AbsolutePath} within {limit.TotalSeconds:0} s");
            }
        }
    }

    /// <summary>Runs the whole sending of a request (proxy lookup, connection, body serialization) on the thread pool.</summary>
    private sealed class OffUiThreadHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.Run(() => base.SendAsync(request, cancellationToken), cancellationToken);
    }
}
