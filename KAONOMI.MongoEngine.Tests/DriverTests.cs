using MongoDB.Bson;
using MongoDB.Driver;

namespace KAONOMI.MongoEngine.Tests;

/// <summary>The official driver of MongoDB (2.25, the one of MARI and Orleans) on the engine.</summary>
public sealed class DriverTests(EngineFixture engine) : IClassFixture<EngineFixture>
{
    private IMongoCollection<BsonDocument> NewCollection(out IMongoDatabase database)
    {
        database = engine.Client().GetDatabase("t" + Guid.NewGuid().ToString("N")[..8]);
        return database.GetCollection<BsonDocument>("items");
    }

    [Fact]
    public async Task HandshakeAndServerCommands()
    {
        var admin = engine.Client().GetDatabase("admin");
        var ping = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        Assert.Equal(1.0, ping["ok"].ToDouble());
        var build = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        Assert.StartsWith("7.0", build["version"].AsString);
        var hello = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
        Assert.True(hello["isWritablePrimary"].AsBoolean);
        var unknown = await Assert.ThrowsAsync<MongoCommandException>(() => admin.RunCommandAsync<BsonDocument>(new BsonDocument("noSuchCommand", 1)));
        Assert.Equal(59, unknown.Code);
    }

    [Fact]
    public async Task InsertFindAndFilters()
    {
        var items = NewCollection(out _);
        await items.InsertManyAsync(
        [
            new BsonDocument { { "_id", 1 }, { "name", "alpha" }, { "n", 5 }, { "tags", new BsonArray { "a", "b" } }, { "sub", new BsonDocument("x", 1) } },
            new BsonDocument { { "_id", 2 }, { "name", "beta" }, { "n", 10L }, { "tags", new BsonArray { "b" } }, { "sub", new BsonDocument("x", 2) } },
            new BsonDocument { { "_id", 3 }, { "name", "Gamma" }, { "n", 7.5 } },
            new BsonDocument { { "name", "delta" }, { "n", BsonNull.Value } }
        ]);
        async Task<string> Names(FilterDefinition<BsonDocument> filter) =>
            string.Join(",", (await items.Find(filter).ToListAsync()).Select(d => d["name"].AsString));
        var f = Builders<BsonDocument>.Filter;

        Assert.Equal("alpha,beta,Gamma,delta", await Names(f.Empty));
        Assert.Equal("beta", await Names(f.Eq("_id", 2)));
        Assert.Equal("beta,Gamma", await Names(f.Gt("n", 5)));          // 10L and 7.5: numbers of any type
        Assert.Equal("alpha,Gamma", await Names(f.Lte("n", 7.5)));
        Assert.Equal("alpha,beta", await Names(f.Eq("tags", "b")));       // an element of the array
        Assert.Equal("alpha", await Names(f.All("tags", new[] { "a", "b" })));
        Assert.Equal("alpha,beta", await Names(f.In("_id", new[] { 1, 2 })));
        Assert.Equal("Gamma,delta", await Names(f.Nin("_id", new[] { 1, 2 })));
        Assert.Equal("beta", await Names(f.Eq("sub.x", 2)));
        Assert.Equal("Gamma,delta", await Names(f.Exists("sub", false)));
        Assert.Equal("delta", await Names(f.Eq("n", BsonNull.Value)));
        Assert.Equal("Gamma", await Names(f.Regex("name", new BsonRegularExpression("^g", "i"))));
        Assert.Equal("alpha,Gamma", await Names(f.Or(f.Eq("_id", 1), f.Eq("_id", 3))));
        Assert.Equal("beta", await Names(f.And(f.Gt("n", 5), f.Type("n", BsonType.Int64))));
        Assert.Equal("alpha", await Names(f.Size("tags", 2)));
        Assert.Equal("beta,Gamma,delta", await Names(f.Not(f.Lte("n", 7))));   // $not keeps null and missing (they do not compare)

        // Sort, skip, limit, projection.
        var sorted = await items.Find(f.Exists("n")).Sort(Builders<BsonDocument>.Sort.Descending("n")).Skip(1).Limit(2)
            .Project(Builders<BsonDocument>.Projection.Include("name").Exclude("_id")).ToListAsync();
        Assert.Equal("{ \"name\" : \"Gamma\" }", sorted[0].ToJson());
        Assert.Equal(4, await items.CountDocumentsAsync(f.Empty));
        Assert.Equal(new[] { "a", "b" }, (await items.DistinctAsync<string>("tags", f.Empty)).ToList());
    }

    [Fact]
    public async Task UpdatesUpsertsAndDuplicateKeys()
    {
        var items = NewCollection(out _);
        await items.InsertOneAsync(new BsonDocument { { "_id", "k" }, { "count", 1 }, { "list", new BsonArray { 1 } } });
        var u = Builders<BsonDocument>.Update;
        var f = Builders<BsonDocument>.Filter;

        var result = await items.UpdateOneAsync(f.Eq("_id", "k"), u.Inc("count", 2).Push("list", 2).Set("a.b", "c").AddToSet("list", 1).Unset("missing"));
        Assert.Equal(1, result.MatchedCount);
        Assert.Equal(1, result.ModifiedCount);
        var doc = await items.Find(f.Eq("_id", "k")).FirstAsync();
        Assert.Equal("{ \"_id\" : \"k\", \"count\" : 3, \"list\" : [1, 2], \"a\" : { \"b\" : \"c\" } }", doc.ToJson());

        // The same update again changes nothing: nModified 0.
        var again = await items.UpdateOneAsync(f.Eq("_id", "k"), u.Set("a.b", "c"));
        Assert.Equal(1, again.MatchedCount);
        Assert.Equal(0, again.ModifiedCount);

        // Upsert: a new document from the equalities of the filter.
        var upsert = await items.UpdateOneAsync(f.And(f.Eq("_id", "new"), f.Eq("kind", "x")), u.Set("v", 1).SetOnInsert("created", true),
            new UpdateOptions { IsUpsert = true });
        Assert.Equal("new", upsert.UpsertedId.AsString);
        Assert.Equal("{ \"_id\" : \"new\", \"kind\" : \"x\", \"v\" : 1, \"created\" : true }", (await items.Find(f.Eq("_id", "new")).FirstAsync()).ToJson());

        // Upsert whose filter does not match but whose _id exists: E11000 (what Orleans counts on).
        var conflict = await Assert.ThrowsAsync<MongoWriteException>(() =>
            items.UpdateOneAsync(f.And(f.Eq("_id", "k"), f.Eq("count", 99)), u.Set("count", 100), new UpdateOptions { IsUpsert = true }));
        Assert.Equal(ServerErrorCategory.DuplicateKey, conflict.WriteError.Category);
        Assert.Equal(11000, conflict.WriteError.Code);
        var duplicate = await Assert.ThrowsAsync<MongoWriteException>(() => items.InsertOneAsync(new BsonDocument("_id", "k")));
        Assert.Equal(11000, duplicate.WriteError.Code);

        // Replace, multi update, delete.
        await items.ReplaceOneAsync(f.Eq("_id", "k"), new BsonDocument { { "_id", "k" }, { "replaced", true } });
        Assert.Equal("{ \"_id\" : \"k\", \"replaced\" : true }", (await items.Find(f.Eq("_id", "k")).FirstAsync()).ToJson());
        var many = await items.UpdateManyAsync(f.Empty, u.Set("all", 1));
        Assert.Equal(2, many.ModifiedCount);
        var deleted = await items.DeleteManyAsync(f.Eq("all", 1));
        Assert.Equal(2, deleted.DeletedCount);
    }

    [Fact]
    public async Task FindAndModifyBulkWriteAndIndexes()
    {
        var items = NewCollection(out _);
        var f = Builders<BsonDocument>.Filter;
        var after = await items.FindOneAndUpdateAsync(f.Eq("_id", "counter"), Builders<BsonDocument>.Update.Inc("seq", 1),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After });
        Assert.Equal(1, after["seq"].ToInt32());
        after = await items.FindOneAndUpdateAsync(f.Eq("_id", "counter"), Builders<BsonDocument>.Update.Inc("seq", 1),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After });
        Assert.Equal(2, after["seq"].ToInt32());
        var removed = await items.FindOneAndDeleteAsync(f.Eq("_id", "counter"));
        Assert.Equal(2, removed["seq"].ToInt32());

        // The bulk writes of MARI: ReplaceOneModel with upsert.
        var writes = Enumerable.Range(0, 5).Select(i => new ReplaceOneModel<BsonDocument>(f.Eq("_id", i), new BsonDocument { { "_id", i }, { "uid", $"u{i}" } }) { IsUpsert = true });
        var bulk = await items.BulkWriteAsync(writes);
        Assert.Equal(5, bulk.Upserts.Count);

        // Indexes: the two of MARI, then a unique one that holds.
        await items.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("type").Ascending("uid"),
            new CreateIndexOptions { Background = true, Name = "auto_type_uid" }));
        await items.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("uid"),
            new CreateIndexOptions { Unique = true, Name = "auto_uid" }));
        var names = (await (await items.Indexes.ListAsync()).ToListAsync()).Select(i => i["name"].AsString).ToList();
        Assert.Equal(new[] { "_id_", "auto_type_uid", "auto_uid" }, names);
        var unique = await Assert.ThrowsAsync<MongoWriteException>(() => items.InsertOneAsync(new BsonDocument { { "_id", 99 }, { "uid", "u1" } }));
        Assert.Equal(11000, unique.WriteError.Code);
    }

    [Fact]
    public async Task CursorsAndAggregations()
    {
        var items = NewCollection(out var database);
        await items.InsertManyAsync(Enumerable.Range(0, 350).Select(i => new BsonDocument
        {
            { "_id", i }, { "Discriminator", i % 2 == 0 ? "even" : "odd" }, { "Definition", new BsonDocument { { "Uid", $"d{i}" }, { "Value", i } } }
        }));
        // 350 documents: a first batch of 101, then getMore.
        var all = await items.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync();
        Assert.Equal(350, all.Count);
        Assert.Equal(Enumerable.Range(0, 350), all.Select(d => d["_id"].AsInt32));

        // The pipeline of the containers of MARI: $match, $replaceRoot, $match, $project, $limit.
        var pipeline = new EmptyPipelineDefinition<BsonDocument>()
            .Match(Builders<BsonDocument>.Filter.Eq("Discriminator", "even"))
            .ReplaceRoot(new BsonValueAggregateExpressionDefinition<BsonDocument, BsonDocument>("$Definition"))
            .Match(Builders<BsonDocument>.Filter.Gte("Value", 300))
            .Project(Builders<BsonDocument>.Projection.Include("Uid").Exclude("_id"))
            .Limit(3);
        var results = await (await items.AggregateAsync(pipeline, new AggregateOptions { BatchSize = 10000, AllowDiskUse = true })).ToListAsync();
        Assert.Equal("d300,d302,d304", string.Join(",", results.Select(r => r["Uid"].AsString)));

        var groups = await items.Aggregate().Group(new BsonDocument { { "_id", "$Discriminator" }, { "total", new BsonDocument("$sum", 1) } })
            .Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal("{ \"_id\" : \"even\", \"total\" : 175 }", groups[0].ToJson());

        Assert.Contains("items", await (await database.ListCollectionNamesAsync()).ToListAsync());
        await database.DropCollectionAsync("items");
        Assert.DoesNotContain("items", await (await database.ListCollectionNamesAsync()).ToListAsync());
    }

    [Fact]
    public async Task DataIsKeptWhenTheEngineRestarts()
    {
        var name = "persist" + Guid.NewGuid().ToString("N")[..6];
        var items = engine.Client().GetDatabase(name).GetCollection<BsonDocument>("docs");
        await items.InsertManyAsync(Enumerable.Range(0, 50).Select(i => new BsonDocument { { "_id", i }, { "v", i } }));
        await items.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 7), Builders<BsonDocument>.Update.Set("v", "changed"));
        await items.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", 8));
        await items.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("v"), new CreateIndexOptions { Name = "by_v" }));

        await engine.RestartAsync();

        var reopened = engine.Client().GetDatabase(name).GetCollection<BsonDocument>("docs");
        Assert.Equal(49, await reopened.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty));
        Assert.Equal("changed", (await reopened.Find(Builders<BsonDocument>.Filter.Eq("_id", 7)).FirstAsync())["v"].AsString);
        Assert.Empty(await reopened.Find(Builders<BsonDocument>.Filter.Eq("_id", 8)).ToListAsync());
        Assert.Contains("by_v", (await (await reopened.Indexes.ListAsync()).ToListAsync()).Select(i => i["name"].AsString));
        // The natural order is kept.
        Assert.Equal(Enumerable.Range(0, 50).Where(i => i != 8), (await reopened.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync()).Select(d => d["_id"].AsInt32));
    }
}
