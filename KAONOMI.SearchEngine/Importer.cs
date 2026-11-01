using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KAONOMI.SearchEngine.Configuration;
using KAONOMI.SearchEngine.Engine;

namespace KAONOMI.SearchEngine;

/// <summary>
/// Copies indices of a running Elasticsearch into the data folder of the engine: the mapping of each index, then its
/// documents (read with the scroll API, ids and sources kept as they are).
/// </summary>
public static class Importer
{
    public static async Task<int> RunAsync(EngineSettings settings, IReadOnlyDictionary<string, string> options)
    {
        var log = EngineLog.For(settings);
        if (!options.TryGetValue("from", out var from))
        {
            log.Error("import", "--from http://host:9200 is required");
            return 1;
        }
        var batch = options.TryGetValue("batch", out var b) ? int.Parse(b) : 2000;
        var overwrite = options.ContainsKey("overwrite");
        var patterns = options.TryGetValue("indices", out var list)
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["*"];

        using var http = new HttpClient { BaseAddress = new Uri(from.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
        if (options.TryGetValue("user", out var user))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{options.GetValueOrDefault("password", string.Empty)}")));

        IndexManager indices;
        try
        {
            indices = new IndexManager(settings);
        }
        catch (Exception ex) when (ex is DataPathInUseException or Lucene.Net.Store.LockObtainFailedException)
        {
            log.Error("import", $"path.data [{settings.DataPath}] is used by a running engine: stop it first");
            return 1;
        }
        using (indices)
        {
            using var catalog = JsonDocument.Parse(await http.GetStringAsync("_cat/indices?format=json&h=index,docs.count"));
            var names = catalog.RootElement.EnumerateArray()
                .Select(e => (Name: e.GetProperty("index").GetString()!, Docs: long.TryParse(e.GetProperty("docs.count").GetString(), out var n) ? n : 0))
                .Where(e => !e.Name.StartsWith('.') && patterns.Any(p => Matches(p, e.Name)))
                .OrderBy(e => e.Docs)
                .ToList();
            log.Info("import", $"{names.Count} index(es) to copy from {from}: {string.Join(", ", names.Select(n => $"{n.Name} ({n.Docs})"))}");

            foreach (var (name, docs) in names)
            {
                if (indices.Find(name) is not null)
                {
                    if (!overwrite)
                    {
                        log.Warn("import", $"[{name}] exists already: skipped (--overwrite to copy it again)");
                        continue;
                    }
                    indices.Delete(name);
                }
                using var mapping = JsonDocument.Parse(await http.GetStringAsync($"{Uri.EscapeDataString(name)}/_mapping"));
                var mappings = mapping.RootElement.GetProperty(name).GetProperty("mappings");
                using var body = JsonDocument.Parse($"{{\"mappings\":{mappings.GetRawText()}}}");
                var index = indices.Create(name, body.RootElement);
                await CopyAsync(http, index, name, docs, batch, log);
            }
        }
        log.Info("import", "done");
        return 0;
    }

    private static async Task CopyAsync(HttpClient http, EsIndex index, string name, long expected, int batch, EngineLog log)
    {
        var watch = Stopwatch.StartNew();
        long copied = 0, failed = 0;
        var lastReport = 0L;
        var response = await PostAsync(http, $"{Uri.EscapeDataString(name)}/_search?scroll=5m", $"{{\"size\":{batch},\"sort\":[\"_doc\"]}}");
        string? scrollId = null;
        try
        {
            while (true)
            {
                using var page = JsonDocument.Parse(response);
                scrollId = page.RootElement.GetProperty("_scroll_id").GetString();
                var hits = page.RootElement.GetProperty("hits").GetProperty("hits");
                if (hits.GetArrayLength() == 0)
                    break;
                foreach (var hit in hits.EnumerateArray())
                {
                    try
                    {
                        index.Import(hit.GetProperty("_id").GetString()!, Encoding.UTF8.GetBytes(hit.GetProperty("_source").GetRawText()));
                        copied++;
                    }
                    catch (Http.EsException ex)
                    {
                        failed++;
                        if (failed <= 10)
                            log.Warn("import", $"[{name}] document [{hit.GetProperty("_id").GetString()}] refused: {ex.Message}");
                    }
                }
                if (watch.ElapsedMilliseconds - lastReport > 5000)
                {
                    lastReport = watch.ElapsedMilliseconds;
                    var rate = copied / Math.Max(1, watch.Elapsed.TotalSeconds);
                    log.Info("import", $"[{name}] {copied}/{expected} documents ({rate:F0}/s)");
                }
                response = await PostAsync(http, "_search/scroll", JsonSerializer.Serialize(new { scroll = "5m", scroll_id = scrollId }));
            }
        }
        finally
        {
            if (scrollId is not null)
                await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "_search/scroll")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { scroll_id = scrollId }), Encoding.UTF8, "application/json")
                });
            index.Refresh();
            index.Commit();
        }
        log.Info("import", $"[{name}] {copied} documents copied in {watch.Elapsed.TotalSeconds:F1} s{(failed > 0 ? $", {failed} refused" : string.Empty)}");
    }

    private static async Task<string> PostAsync(HttpClient http, string path, string body)
    {
        using var response = await http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{path}: HTTP {(int)response.StatusCode} {text}");
        return text;
    }

    private static bool Matches(string pattern, string name) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$");
}
