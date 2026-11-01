using MongoDB.Bson;
using MongoDB.Driver;

namespace KAONOMI.MongoEngine.Tests;

/// <summary>
/// The commands that Orleans.Providers.MongoDB sent to MongoDB for the membership of the KAONOMI cluster (captured
/// with the profiler, 2026-10-29): a silo joins, becomes active, says it is alive, shuts down; a write with an old etag
/// is refused (duplicate key: the upsert cannot insert the document of the table again).
/// </summary>
public sealed class MembershipTests(EngineFixture engine) : IClassFixture<EngineFixture>
{
    private static BsonDocument Member(string name, string status, int code, string etag) => new()
    {
        { "Etag", etag }, { "FaultZone", 0 }, { "HostName", "Jeromes-MacBook-Pro.local" }, { "IAmAliveTime", "2026-10-29 14:40:00.323 GMT" },
        { "ProxyPort", 30006 }, { "RoleName", "KAONOMI.ACHEngine" }, { "SiloAddress", "127.0.0.1:11117@152289599" }, { "SiloName", name },
        { "StartTime", "2026-10-29 14:39:59.000 GMT" }, { "Status", code }, { "StatusText", status }, { "SuspectTimes", new BsonArray() },
        { "Timestamp", new BsonDateTime(new DateTime(2026, 10, 29, 14, 40, 0, DateTimeKind.Utc)) }, { "UpdateZone", 0 }
    };

    [Fact]
    public async Task TheMembershipTableOfOrleans()
    {
        var table = engine.Client().GetDatabase("kaonomi" + Guid.NewGuid().ToString("N")[..6]).GetCollection<BsonDocument>("OrleansMembershipSingle");
        const string silo = "127_0_0_1:11117@152289599";
        var members = $"Members.{silo}";

        // The first silo creates the table: no document yet, the upsert inserts it.
        var created = await table.UpdateOneAsync(
            new BsonDocument { { "_id", "kaonomi" }, { "VersionEtag", "v0" }, { members, new BsonDocument("$exists", false) } },
            new BsonDocument("$set", new BsonDocument { { members, Member("Kaonomi.ACHEngine", "Joining", 2, "e1") }, { "Version", 1 }, { "VersionEtag", "v1" } }),
            new UpdateOptions { IsUpsert = true });
        Assert.NotNull(created.UpsertedId);

        // Joining → Active: the etags of the table and of the member are checked.
        var active = await table.UpdateOneAsync(
            new BsonDocument { { "_id", "kaonomi" }, { "VersionEtag", "v1" }, { $"{members}.Etag", "e1" } },
            new BsonDocument("$set", new BsonDocument { { members, Member("Kaonomi.ACHEngine", "Active", 3, "e2") }, { "Version", 2 }, { "VersionEtag", "v2" } }),
            new UpdateOptions { IsUpsert = true });
        Assert.Equal(1, active.MatchedCount);
        Assert.Equal(1, active.ModifiedCount);

        // Another silo with the old etag: refused with a duplicate key error, the table is unchanged.
        var stale = await Assert.ThrowsAsync<MongoWriteException>(() => table.UpdateOneAsync(
            new BsonDocument { { "_id", "kaonomi" }, { "VersionEtag", "v1" }, { $"{members}.Etag", "e1" } },
            new BsonDocument("$set", new BsonDocument { { members, Member("Kaonomi.ACHEngine", "Dead", 6, "e9") }, { "Version", 2 }, { "VersionEtag", "v9" } }),
            new UpdateOptions { IsUpsert = true }));
        Assert.Equal(11000, stale.WriteError.Code);

        // I am alive: one field of the member, no upsert.
        var alive = await table.UpdateOneAsync(
            new BsonDocument { { "_id", "kaonomi" }, { members, new BsonDocument("$exists", true) } },
            new BsonDocument("$set", new BsonDocument($"{members}.IAmAliveTime", "2026-10-29 14:40:57.295 GMT")));
        Assert.Equal(1, alive.ModifiedCount);

        // The read of the table (find {_id}, limit 1).
        var document = await table.Find(new BsonDocument("_id", "kaonomi")).Limit(1).FirstAsync();
        var member = document["Members"][silo].AsBsonDocument;
        Assert.Equal("Active", member["StatusText"].AsString);
        Assert.Equal("2026-10-29 14:40:57.295 GMT", member["IAmAliveTime"].AsString);
        Assert.Equal("v2", document["VersionEtag"].AsString);
        Assert.Equal(2, document["Version"].AsInt32);

        // Cleanup of a dead silo.
        await table.UpdateOneAsync(new BsonDocument("_id", "kaonomi"), new BsonDocument("$unset", new BsonDocument(members, "")));
        Assert.Equal(0, (await table.Find(new BsonDocument("_id", "kaonomi")).FirstAsync())["Members"].AsBsonDocument.ElementCount);
    }

    /// <summary>Many writers at once with the same etag: exactly one wins (the update is atomic).</summary>
    [Fact]
    public async Task ConcurrentWritersOnlyOneWins()
    {
        var table = engine.Client().GetDatabase("race" + Guid.NewGuid().ToString("N")[..6]).GetCollection<BsonDocument>("OrleansMembershipSingle");
        await table.InsertOneAsync(new BsonDocument { { "_id", "kaonomi" }, { "VersionEtag", "v0" }, { "Version", 0 } });
        var attempts = Enumerable.Range(0, 32).Select(async i =>
        {
            try
            {
                var result = await table.UpdateOneAsync(new BsonDocument { { "_id", "kaonomi" }, { "VersionEtag", "v0" } },
                    new BsonDocument("$set", new BsonDocument { { "VersionEtag", $"w{i}" }, { "Version", 1 } }), new UpdateOptions { IsUpsert = true });
                return result.ModifiedCount == 1;
            }
            catch (MongoWriteException ex) when (ex.WriteError.Code == 11000)
            {
                return false;
            }
        });
        var won = (await Task.WhenAll(attempts)).Count(w => w);
        Assert.Equal(1, won);
    }
}
