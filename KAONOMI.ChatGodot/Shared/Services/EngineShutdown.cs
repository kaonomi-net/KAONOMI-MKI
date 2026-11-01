using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>
/// Stops the engines of KAONOMI when the chat closes (<c>/bye</c>): the search engine with <c>POST /_shutdown</c> of
/// KAONOMI.SearchEngine, then the Mongo engine with the <c>shutdown</c> command of MongoDB (KAONOMI.MongoEngine honours
/// it), each one waited for until its port is closed. An engine that is not running is left alone, and so is a real
/// Elasticsearch (it has no such API: it answers an error). Every method gives a line for the activity log and never
/// throws, apart from the cancellation of <c>/bye</c>.
/// </summary>
public static class EngineShutdown
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Asks KAONOMI.SearchEngine at <paramref name="url"/> (<c>http://localhost:9200</c>) to stop, and waits for it.</summary>
    public static async Task<string> StopSearchEngineAsync(string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return $"Search engine: no address to stop ({url})";
        if (!await PortOpenAsync(uri.Host, uri.Port, cancellationToken))
            return $"Search engine at {uri}: not running";
        try
        {
            using var http = new HttpClient { Timeout = AskTimeout };
            using var response = await http.PostAsync(new Uri(uri, "_shutdown"), new StringContent(string.Empty), cancellationToken);
            if (!response.IsSuccessStatusCode)
                return $"Search engine at {uri}: not stopped (HTTP {(int)response.StatusCode} on POST /_shutdown: a real Elasticsearch?)";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return $"Search engine at {uri}: not stopped ({ex.Message})";
        }
        return await WaitClosedAsync("Search engine", uri.ToString(), uri.Host, uri.Port, cancellationToken);
    }

    /// <summary>
    /// Sends the <c>shutdown</c> command to the Mongo engine at <paramref name="address"/> (<c>localhost:27017</c>, a
    /// <c>mongodb://</c> prefix allowed) and waits for it to stop.
    /// </summary>
    public static async Task<string> StopMongoEngineAsync(string? address, CancellationToken cancellationToken)
    {
        var (host, port) = ParseAddress(address);
        if (host is null)
            return $"Mongo engine: no address to stop ({address})";
        if (!await PortOpenAsync(host, port, cancellationToken))
            return $"Mongo engine at {host}:{port}: not running";
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AskTimeout);
            await client.ConnectAsync(host, port, timeout.Token);
            using var stream = client.GetStream();
            await stream.WriteAsync(ShutdownMessage(), timeout.Token);
            // The reply ({ ok: 1 }) or the end of the connection: either way the command reached the engine.
            try
            {
                await stream.ReadAtLeastAsync(new byte[16], 16, throwOnEndOfStream: false, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return $"Mongo engine at {host}:{port}: not stopped ({ex.Message})";
        }
        return await WaitClosedAsync("Mongo engine", $"{host}:{port}", host, port, cancellationToken);
    }

    /// <summary>The OP_MSG of <c>{ shutdown: 1, $db: "admin" }</c>: header, flag bits, one section of kind 0 holding the document.</summary>
    private static byte[] ShutdownMessage()
    {
        using var document = new MemoryStream();
        using (var bson = new BinaryWriter(document, Encoding.UTF8, leaveOpen: true))
        {
            bson.Write(0); // the length, written at the end
            bson.Write((byte)0x10); // int32 "shutdown": 1
            bson.Write("shutdown"u8);
            bson.Write((byte)0);
            bson.Write(1);
            bson.Write((byte)0x02); // string "$db": "admin"
            bson.Write("$db"u8);
            bson.Write((byte)0);
            bson.Write("admin".Length + 1);
            bson.Write("admin"u8);
            bson.Write((byte)0);
            bson.Write((byte)0); // the end of the document
        }
        var body = document.ToArray();
        BitConverter.GetBytes(body.Length).CopyTo(body, 0);

        using var message = new MemoryStream();
        using (var writer = new BinaryWriter(message, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(16 + 4 + 1 + body.Length); // messageLength
            writer.Write(1); // requestID
            writer.Write(0); // responseTo
            writer.Write(2013); // OP_MSG
            writer.Write(0); // flagBits
            writer.Write((byte)0); // section kind 0: a body
            writer.Write(body);
        }
        return message.ToArray();
    }

    private static (string? Host, int Port) ParseAddress(string? address)
    {
        var text = address?.Trim() ?? string.Empty;
        if (text.StartsWith("mongodb://", StringComparison.OrdinalIgnoreCase))
            text = text["mongodb://".Length..];
        var slash = text.IndexOf('/');
        if (slash >= 0)
            text = text[..slash];
        if (text.Length == 0)
            return (null, 0);
        var colon = text.LastIndexOf(':');
        if (colon < 0)
            return (text, 27017);
        return int.TryParse(text[(colon + 1)..], out var port) && port > 0 ? (text[..colon], port) : (null, 0);
    }

    /// <summary>True when something accepts a connection on <paramref name="host"/>:<paramref name="port"/> (1 s at most).</summary>
    public static async Task<bool> PortOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<string> WaitClosedAsync(string what, string address, string host, int port, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < StopTimeout)
        {
            await Task.Delay(250, cancellationToken);
            if (!await PortOpenAsync(host, port, cancellationToken))
                return $"{what} at {address}: stopped ({(DateTime.UtcNow - started).TotalSeconds:0.0} s)";
        }
        return $"{what} at {address}: still listening after {StopTimeout.TotalSeconds:0} s";
    }
}
