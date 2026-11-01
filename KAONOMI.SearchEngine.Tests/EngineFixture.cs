using System.Net;
using System.Net.Sockets;
using KAONOMI.Core.Data;
using KAONOMI.Core.Models;
using KAONOMI.SearchEngine.Configuration;

namespace KAONOMI.SearchEngine.Tests;

/// <summary>An engine started in the process of the tests, on a free port, its data in a temporary folder.</summary>
public sealed class EngineFixture : IAsyncLifetime
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "kaonomi-searchengine-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private SearchEngineHost? _host;

    public string Url => _host!.Url;

    public async Task InitializeAsync()
    {
        var settings = EngineSettings.Load(null, ["http.port=" + FreePort(), "path.data=" + _data, "cluster.name=tests", "node.name=tests"]);
        _host = await SearchEngineHost.StartAsync(settings) ?? throw new InvalidOperationException("The engine did not start");
    }

    /// <summary>The repository of KAONOMI (NEST 7.17) on the engine, its indices named with <paramref name="prefix"/>.</summary>
    public ElasticRepository Repository(string prefix) => new(new ElasticServerSettings { url = Url, prefix = prefix });

    /// <summary>Stops the engine and starts it again on the same data (what was written must still be there).</summary>
    public async Task RestartAsync()
    {
        var settings = _host!.Settings;
        await _host.DisposeAsync();
        _host = await SearchEngineHost.StartAsync(settings) ?? throw new InvalidOperationException("The engine did not start again");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        if (Directory.Exists(_data))
            Directory.Delete(_data, recursive: true);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
