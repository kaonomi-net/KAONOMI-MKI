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

    /// <summary>Runs the whole sending of a request (proxy lookup, connection, body serialization) on the thread pool.</summary>
    private sealed class OffUiThreadHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.Run(() => base.SendAsync(request, cancellationToken), cancellationToken);
    }
}
