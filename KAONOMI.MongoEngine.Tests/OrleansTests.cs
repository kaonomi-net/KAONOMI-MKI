using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Orleans.Configuration;
using Orleans.Providers.MongoDB.Configuration;

namespace KAONOMI.MongoEngine.Tests;

/// <summary>
/// Real Orleans silos (8.0, Orleans.Providers.MongoDB 8.0.3, the clustering of MARI) on the engine: they join the
/// cluster, see each other, and are marked dead when they stop.
/// </summary>
public sealed class OrleansTests(EngineFixture engine) : IClassFixture<EngineFixture>
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private IHost Silo(string database, string name) => new HostBuilder()
        .UseOrleans(silo => silo
            .UseMongoDBClient(engine.ConnectionString)
            .UseMongoDBClustering(o =>
            {
                o.DatabaseName = database;
                o.Strategy = MongoDBMembershipStrategy.SingleDocument;
            })
            .Configure<ClusterOptions>(o =>
            {
                o.ClusterId = "kaonomi";
                o.ServiceId = "kaonomi-tests";
            })
            .Configure<SiloOptions>(o => o.SiloName = name)
            .ConfigureEndpoints(IPAddress.Loopback, FreePort(), FreePort()))
        .Build();

    [Fact]
    public async Task SilosJoinSeeEachOtherAndLeave()
    {
        var database = "orleans" + Guid.NewGuid().ToString("N")[..6];
        var table = engine.Client().GetDatabase(database).GetCollection<BsonDocument>("OrleansMembershipSingle");

        using var first = Silo(database, "first");
        using var second = Silo(database, "second");
        await first.StartAsync();
        await second.StartAsync();

        var document = await table.Find(new BsonDocument("_id", "kaonomi")).FirstAsync();
        var members = document["Members"].AsBsonDocument.Elements.Select(e => e.Value.AsBsonDocument).ToList();
        Assert.Equal(2, members.Count);
        Assert.All(members, m => Assert.Equal("Active", m["StatusText"].AsString));
        Assert.Equal(new[] { "first", "second" }, members.Select(m => m["SiloName"].AsString).Order());

        await second.StopAsync();
        await first.StopAsync();

        document = await table.Find(new BsonDocument("_id", "kaonomi")).FirstAsync();
        Assert.All(document["Members"].AsBsonDocument.Elements, e => Assert.Equal("Dead", e.Value["StatusText"].AsString));
    }
}
