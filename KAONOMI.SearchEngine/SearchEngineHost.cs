using System.Net;
using KAONOMI.SearchEngine.Configuration;
using KAONOMI.SearchEngine.Engine;
using KAONOMI.SearchEngine.Http;

namespace KAONOMI.SearchEngine;

/// <summary>The engine running: its indices and its HTTP server (also started by the tests, in their process).</summary>
public sealed class SearchEngineHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public IndexManager Indices { get; }
    public EngineSettings Settings { get; }

    /// <summary>The address of the REST API (<c>http://127.0.0.1:9200</c>).</summary>
    public string Url { get; }

    private SearchEngineHost(WebApplication app, IndexManager indices, EngineSettings settings, string url)
    {
        _app = app;
        Indices = indices;
        Settings = settings;
        Url = url;
    }

    /// <summary>Opens the indices of <c>path.data</c> and starts listening, or returns null (logged) when it cannot.</summary>
    public static async Task<SearchEngineHost?> StartAsync(EngineSettings settings)
    {
        Lucene.Net.Search.BooleanQuery.MaxClauseCount = settings.MaxClauseCount;
        var log = EngineLog.For(settings);
        log.Info("node", $"version[{EsHttpHandler.Version}] KAONOMI.SearchEngine, config [{settings.File ?? "defaults"}], data [{settings.DataPath}]");

        // The port first: an engine already running there keeps its indices to itself.
        if (PortInUse(settings))
        {
            log.Error("http", $"cannot listen on {settings.Host}:{settings.Port}: the port is used (an engine or an Elasticsearch is running?)");
            return null;
        }
        IndexManager indices;
        try
        {
            indices = new IndexManager(settings);
        }
        catch (Exception ex) when (ex is DataPathInUseException or Lucene.Net.Store.LockObtainFailedException)
        {
            log.Error("node", ex.Message);
            return null;
        }
        log.Info("gateway", $"recovered [{indices.All.Count}] indices into cluster_state");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
            kestrel.AddServerHeader = false;
            switch (settings.Host)
            {
                case "0.0.0.0" or "_site_" or "_global_":
                    kestrel.ListenAnyIP(settings.Port);
                    break;
                case "localhost" or "_local_":
                    kestrel.ListenLocalhost(settings.Port);
                    break;
                default:
                    var address = IPAddress.TryParse(settings.Host, out var ip) ? ip : Dns.GetHostAddresses(settings.Host).First();
                    kestrel.Listen(address, settings.Port);
                    break;
            }
        });
        var app = builder.Build();
        var handler = new EsHttpHandler(indices, settings, log);
        handler.ShutdownRequested += () => _ = Task.Run(async () =>
        {
            await Task.Delay(300); // the answer of POST /_shutdown first
            app.Lifetime.StopApplication();
        });
        app.Run(handler.HandleAsync);
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            log.Info("node", "stopping ...");
            indices.Dispose();
            log.Info("node", "stopped");
        });
        try
        {
            await app.StartAsync();
        }
        catch (IOException ex)
        {
            log.Error("http", $"cannot listen on {settings.Host}:{settings.Port}: {ex.Message}");
            indices.Dispose();
            return null;
        }
        var bound = $"{settings.Host}:{settings.Port}";
        log.Info("http", $"publish_address {{{bound}}}, bound_addresses {{{bound}}}");
        log.Info("node", $"[{settings.NodeName}] started (cluster [{settings.ClusterName}], security {(settings.SecurityEnabled ? "on" : "off")})");
        var host = settings.Host is "0.0.0.0" or "_site_" or "_global_" or "_local_" ? "127.0.0.1" : settings.Host;
        return new SearchEngineHost(app, indices, settings, $"http://{host}:{settings.Port}");
    }

    private static bool PortInUse(EngineSettings settings)
    {
        var address = settings.Host switch
        {
            "0.0.0.0" or "_site_" or "_global_" => IPAddress.Any,
            "localhost" or "_local_" => IPAddress.Loopback,
            _ => IPAddress.TryParse(settings.Host, out var ip) ? ip : Dns.GetHostAddresses(settings.Host).First()
        };
        try
        {
            var probe = new System.Net.Sockets.TcpListener(address, settings.Port);
            probe.Start();
            probe.Stop();
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true;
        }
    }

    public Task WaitForShutdownAsync() => _app.WaitForShutdownAsync();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
