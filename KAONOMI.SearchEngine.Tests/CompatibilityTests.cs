using KAONOMI.Core.Data;
using KAONOMI.Core.Models;
using Xunit.Abstractions;

namespace KAONOMI.SearchEngine.Tests;

/// <summary>
/// The same scenario on a real Elasticsearch 7.14 (<c>ES_URL</c>, <c>http://localhost:9200</c> by default) and on the
/// engine: the results must be the same, line by line. Without a reachable Elasticsearch the test only says so. The
/// indices of the test on Elasticsearch (<c>zz_setest_…</c>) are deleted at the end.
/// </summary>
public sealed class CompatibilityTests(EngineFixture engine, ITestOutputHelper output) : IClassFixture<EngineFixture>
{
    private static readonly string ElasticUrl = Environment.GetEnvironmentVariable("ES_URL") ?? "http://localhost:9200";

    [Fact]
    public async Task SameResultsAsElasticsearch()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            await http.GetStringAsync(ElasticUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            output.WriteLine($"No Elasticsearch at {ElasticUrl}: comparison not run");
            return;
        }

        var prefix = "zz_setest_" + Guid.NewGuid().ToString("N")[..6] + "_";
        try
        {
            var expected = Scenario.Run(new ElasticRepository(new ElasticServerSettings { url = ElasticUrl, prefix = prefix }));
            var actual = Scenario.Run(engine.Repository(prefix));
            for (var i = 0; i < Math.Max(expected.Count, actual.Count); i++)
            {
                var e = i < expected.Count ? expected[i] : "(none)";
                var a = i < actual.Count ? actual[i] : "(none)";
                output.WriteLine(e == a ? $"same  {e}" : $"DIFF  elasticsearch: {e}\n      engine:        {a}");
            }
            Assert.Equal(expected, actual);
        }
        finally
        {
            using var cleanup = new HttpClient();
            await cleanup.DeleteAsync($"{ElasticUrl}/{prefix}*");
        }
    }
}
