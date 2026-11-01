using System.Net;
using System.Net.Sockets;
using KAONOMI.MongoEngine.Configuration;
using KAONOMI.MongoEngine.Engine;
using MongoDB.Bson;

namespace KAONOMI.MongoEngine.Wire;

/// <summary>A connection of a client (its id is the <c>connectionId</c> of <c>hello</c>).</summary>
public sealed class ClientConnection(long id, string remote)
{
    public long Id { get; } = id;
    public string Remote { get; } = remote;
    public BsonDocument? ClientMetadata { get; set; }
}

/// <summary>
/// The TCP server: one task per connection, which reads a message, runs its command and writes the reply, in order
/// (as a MongoDB connection does).
/// </summary>
public sealed class WireServer : IAsyncDisposable
{
    private const int MaxMessageSize = 48_000_000;

    private readonly EngineSettings _settings;
    private readonly CommandHandler _commands;
    private readonly EngineLog _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = new();
    private TcpListener? _listener;
    private Task? _accept;
    private long _nextConnection;
    private int _open;
    private int _nextResponse;

    public int Port { get; private set; }

    public WireServer(EngineSettings settings, CommandHandler commands, EngineLog log)
    {
        _settings = settings;
        _commands = commands;
        _log = log;
    }

    public static IPAddress Address(string bindIp) => bindIp switch
    {
        "0.0.0.0" or "*" => IPAddress.Any,
        "localhost" => IPAddress.Loopback,
        _ => IPAddress.TryParse(bindIp.Split(',')[0].Trim(), out var ip) ? ip : Dns.GetHostAddresses(bindIp).First()
    };

    public void Start()
    {
        _listener = new TcpListener(Address(_settings.BindIp), _settings.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.Warn("NETWORK", $"accept failed: {ex.Message}");
                continue;
            }
            if (Interlocked.Increment(ref _open) > _settings.MaxConnections)
            {
                Interlocked.Decrement(ref _open);
                _log.Warn("NETWORK", $"Connection refused because there are too many open connections ({_settings.MaxConnections})");
                client.Dispose();
                continue;
            }
            var connection = new ClientConnection(Interlocked.Increment(ref _nextConnection), client.Client.RemoteEndPoint?.ToString() ?? "?");
            var task = Serve(client, connection);
            lock (_connections)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(task);
            }
        }
    }

    private async Task Serve(TcpClient client, ClientConnection connection)
    {
        _log.Debug("NETWORK", $"Connection accepted conn{connection.Id} from {connection.Remote} ({_open} open)");
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                var message = await WireProtocol.ReadAsync(stream, MaxMessageSize, _stop.Token);
                if (message is null)
                    break;
                var (requestId, opCode, body) = message.Value;
                WireRequest request;
                try
                {
                    request = WireProtocol.Parse(requestId, opCode, body);
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or FormatException)
                {
                    _log.Warn("NETWORK", $"conn{connection.Id}: {ex.Message}");
                    break;
                }
                var reply = _commands.Execute(request, connection);
                if (request.MoreToCome)
                    continue; // unacknowledged write: no reply
                var bytes = WireProtocol.Reply(request, Interlocked.Increment(ref _nextResponse), reply);
                await stream.WriteAsync(bytes, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("NETWORK", $"conn{connection.Id}: {ex}");
        }
        finally
        {
            client.Dispose();
            Interlocked.Decrement(ref _open);
            _commands.ConnectionClosed(connection);
            _log.Debug("NETWORK", $"Connection ended conn{connection.Id}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener?.Stop();
        if (_accept is not null)
            await _accept;
        Task[] open;
        lock (_connections)
            open = _connections.ToArray();
        await Task.WhenAll(open).WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
    }
}
