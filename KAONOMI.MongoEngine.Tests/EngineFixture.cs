using KAONOMI.MongoEngine.Configuration;
using MongoDB.Driver;

namespace KAONOMI.MongoEngine.Tests;

/// <summary>An engine started in the process of the tests, on a free port, its data in a temporary folder.</summary>
public sealed class EngineFixture : IAsyncLifetime
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "kaonomi-mongoengine-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private MongoEngineHost? _host;

    public string ConnectionString => _host!.ConnectionString;

    public Task InitializeAsync()
    {
        _host = Start(["net.port=0", "storage.dbPath=" + _data]);
        return Task.CompletedTask;
    }

    private static MongoEngineHost Start(string[] overrides) =>
        MongoEngineHost.Start(EngineSettings.Load(null, overrides)) ?? throw new InvalidOperationException("The engine did not start");

    /// <summary>A client of the official driver on the engine (a new connection pool each time).</summary>
    public MongoClient Client() => new(MongoClientSettings.FromConnectionString(ConnectionString + "/?serverSelectionTimeoutMS=5000"));

    /// <summary>Stops the engine and starts it again on the same data (on a new port: the clients must be made again).</summary>
    public async Task RestartAsync()
    {
        await _host!.DisposeAsync();
        _host = Start(["net.port=0", "storage.dbPath=" + _data]);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        if (Directory.Exists(_data))
            Directory.Delete(_data, recursive: true);
    }
}
