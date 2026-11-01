using System.Text.Json;
using KAONOMI.Core.Models;
using Xunit.Abstractions;

namespace KAONOMI.SearchEngine.Tests;

/// <summary>The repository of KAONOMI on the engine: what it saves, finds, sorts and deletes.</summary>
public sealed class RepositoryTests(EngineFixture engine, ITestOutputHelper output) : IClassFixture<EngineFixture>
{
    private static string Prefix() => "t" + Guid.NewGuid().ToString("N")[..6] + "_";

    [Fact]
    public void ScenarioGivesTheExpectedResults()
    {
        var lines = Scenario.Run(engine.Repository(Prefix()));
        foreach (var line in lines)
            output.WriteLine(line);
        string Line(string label) => lines.Single(l => l.StartsWith(label + ": ", StringComparison.Ordinal))[(label.Length + 2)..];

        Assert.Equal("diary-04 Cassoulet, confit de canard et haricots tarbais joy 2026-10-01T12:00:00.0004936Z False", Line("load"));
        Assert.Equal("null", Line("load missing"));
        // diary-02, 05, 08, 11… are deleted (one in three).
        string Sorted(string label) => string.Join(",", Line(label).Split(',', StringSplitOptions.RemoveEmptyEntries).Order());
        Assert.Equal("diary-00,diary-01,diary-04,diary-12,diary-13,diary-16", Sorted("search cassoulet"));
        Assert.Equal("diary-01,diary-04,diary-13,diary-16", Sorted("search and/or"));
        Assert.Equal("diary-03,diary-15", Sorted("search or"));
        Assert.Contains("diary-11", Line("search deleted kept"));
        Assert.Contains("diary-04", Line("search typo"));
        Assert.Equal("diary-06", Line("search part of a word").Split(',').OrderBy(x => x).First());
        Assert.Equal(7, Line("search all").Split(',').Length);
        Assert.Equal("diary-23,diary-22,diary-21,diary-20,diary-19", Line("last"));
        Assert.Equal("diary-22,diary-21,diary-19,diary-18,diary-16", Line("last not deleted"));
        Assert.Equal("diary-23,diary-22,diary-21,diary-20,diary-19,diary-18", Line("since"));
        Assert.Equal("diary-23,diary-22,diary-21,diary-20", Line("since all"));
        // A diary is never erased: a delete marks it deleted (again and again), and it can still be loaded.
        Assert.Equal("True", Line("delete"));
        Assert.Equal("True", Line("delete again"));
        Assert.Equal("isDeleted True", Line("load deleted"));
        Assert.Equal("diary-01", Line("search after update"));
        Assert.Equal("diary-12", Line("search after delete")); // diary-00 is deleted (left out), its "(bis)" stays
        Assert.Contains("diary-00", Line("search after delete kept")); // still in the index, found when the deleted ones are kept
        Assert.Equal(string.Empty, Line("no index last"));
        Assert.Equal(string.Empty, Line("no index search"));
        Assert.Equal("null", Line("no index load"));
    }

    [Fact]
    public void SinceAllReadsMoreThanTenThousandDocuments()
    {
        var repository = engine.Repository(Prefix());
        const int count = 12_345;
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            repository.Save(new Log { id = $"log-{i:00000}", date = start.AddSeconds(i), comment = "log " + i, Type = "INFO" }));
        repository.SaveSearchable(new Log { id = "log-last", date = start.AddSeconds(count), comment = "last" });

        var all = repository.SinceAll<Log>(start.AddSeconds(100));
        Assert.Equal(count - 100 + 1, all.Count);
        Assert.Equal("log-last", all.First().id);
        Assert.Equal("log-00100", all.Last().id);
        Assert.Equal(all.Count, all.Select(l => l.id).Distinct().Count());

        var last = repository.Last(10);
        Assert.Equal("log-last", last.First().id);
        Assert.Equal(10, last.Count);
        Assert.Equal(10_000, repository.Since<Log>(start).Count);
    }

    [Fact]
    public async Task DocumentsAreKeptWhenTheEngineRestarts()
    {
        var prefix = Prefix();
        var repository = engine.Repository(prefix);
        var diary = new Diary { id = "kept", text = "Un texte gardé", date = DateTime.UtcNow, feeling = "joy" };
        repository.Save(diary);
        repository.Save(new Diary { id = "kept-2", text = "Un autre", date = DateTime.UtcNow });

        await engine.RestartAsync();

        var loaded = (Diary?)repository.Load<Diary>("kept");
        Assert.Equal("Un texte gardé", loaded?.text);
        Assert.Contains(repository.Search<Diary>("garde", ["text"], 10), d => d.id == "kept");
        // The versions go on after the restart.
        using var http = new HttpClient();
        var response = await http.PutAsync($"{engine.Url}/{prefix}diary/_doc/kept",
            new StringContent("{\"text\":\"v2\"}", System.Text.Encoding.UTF8, "application/json"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("_version").GetInt32());
        Assert.Equal("updated", json.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public void NestedObjectsAndListsAreSearchable()
    {
        var repository = engine.Repository(Prefix());
        var document = new Document
        {
            id = "doc-1",
            title = "Manuel",
            category = "text",
            date = DateTime.UtcNow,
            contents = [new Content { content = "Première version du manuel de Lucene", version = 1, date = DateTime.UtcNow }]
        };
        repository.SaveSearchable(document);
        Assert.Single(repository.Search<Document>("lucene", ["title", "description", "contents.content"], 10, nameof(Document.isDeleted)));
        Assert.Single(repository.Search<Document>("lucen", ["contents.content"], 10));
        Assert.Empty(repository.Search<Document>("lucene", ["title"], 10));
    }
}
