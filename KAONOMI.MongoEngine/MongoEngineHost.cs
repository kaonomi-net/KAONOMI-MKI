using System.Net.Sockets;
using KAONOMI.MongoEngine.Configuration;
using KAONOMI.MongoEngine.Engine;
using KAONOMI.MongoEngine.Storage;
using KAONOMI.MongoEngine.Wire;

namespace KAONOMI.MongoEngine;

/// <summary>The engine running: its storage and its TCP server (also started by the tests, in their process).</summary>
public sealed class MongoEngineHost : IAsyncDisposable
{
    private readonly WireServer _server;
    private readonly EngineLog _log;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed when a client sent the <c>shutdown</c> command: the process disposes the host and exits.</summary>
    public Task ShutdownRequested => _shutdown.Task;

    public StorageEngine Storage { get; }
    public EngineSettings Settings { get; }

    /// <summary>The connection string of the engine (<c>mongodb://127.0.0.1:27017</c>).</summary>
    public string ConnectionString { get; }

    private MongoEngineHost(WireServer server, StorageEngine storage, EngineSettings settings, EngineLog log)
    {
        _server = server;
        Storage = storage;
        Settings = settings;
        _log = log;
        var host = settings.BindIp is "0.0.0.0" or "*" ? "127.0.0.1" : settings.BindIp.Split(',')[0].Trim();
        ConnectionString = $"mongodb://{host}:{server.Port}";
    }

    /// <summary>Opens the databases of <c>storage.dbPath</c> and listens, or returns null (logged) when it cannot.</summary>
    public static MongoEngineHost? Start(EngineSettings settings)
    {
        var log = EngineLog.For(settings);
        log.Info("CONTROL", $"KAONOMI.MongoEngine version {CommandHandler.Version} (wire protocol of MongoDB), config [{settings.File ?? "defaults"}], dbPath [{settings.DbPath}]");
        // The port first: a MongoDB already running there keeps its data to itself.
        if (settings.Port != 0)
        {
            try
            {
                var probe = new TcpListener(WireServer.Address(settings.BindIp), settings.Port);
                probe.Start();
                probe.Stop();
            }
            catch (SocketException)
            {
                log.Error("NETWORK", $"cannot listen on {settings.BindIp}:{settings.Port}: the port is used (a MongoDB or another engine is running?)");
                return null;
            }
        }
        StorageEngine storage;
        try
        {
            storage = new StorageEngine(settings, log);
        }
        catch (DataPathInUseException ex)
        {
            log.Error("STORAGE", ex.Message);
            return null;
        }
        log.Info("STORAGE", $"{storage.DatabaseNames().Count} database(s), {storage.CollectionCount} collection(s) loaded");
        var handler = new CommandHandler(storage, log);
        var server = new WireServer(settings, handler, log);
        try
        {
            server.Start();
        }
        catch (SocketException ex)
        {
            log.Error("NETWORK", $"cannot listen on {settings.BindIp}:{settings.Port}: {ex.Message}");
            storage.Dispose();
            return null;
        }
        log.Info("NETWORK", $"Waiting for connections on {settings.BindIp}:{server.Port}");
        var host = new MongoEngineHost(server, storage, settings, log);
        handler.ShutdownRequested += () => _ = Task.Run(async () =>
        {
            await Task.Delay(300); // the reply of the shutdown command first
            host._shutdown.TrySetResult();
        });
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        _log.Info("CONTROL", "shutting down");
        await _server.DisposeAsync();
        Storage.Dispose();
        _log.Info("CONTROL", "now exiting");
    }
}
