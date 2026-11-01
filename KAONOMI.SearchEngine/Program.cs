using KAONOMI.SearchEngine;
using KAONOMI.SearchEngine.Configuration;
using KAONOMI.SearchEngine.Engine;

// KAONOMI.SearchEngine: a small Elasticsearch 7.14 (one node, one shard per index) for the needs of KAONOMI.
//   KAONOMI.SearchEngine [--config file.yml] [-E key=value]…                 serves the REST API
//   KAONOMI.SearchEngine import --from http://host:9200 [--indices a,b] …   copies indices of an Elasticsearch

var arguments = args.ToList();
var command = arguments.Count > 0 && !arguments[0].StartsWith('-') ? arguments[0] : "serve";
if (command != "serve")
    arguments.RemoveAt(0);

string? configFile = null;
var overrides = new List<string>();
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < arguments.Count; i++)
{
    switch (arguments[i])
    {
        case "--config" or "-c" when i + 1 < arguments.Count:
            configFile = arguments[++i];
            break;
        case "-E" when i + 1 < arguments.Count:
            overrides.Add(arguments[++i]);
            break;
        case var flag when flag.StartsWith("--", StringComparison.Ordinal):
            options[flag[2..]] = i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--", StringComparison.Ordinal) ? arguments[++i] : "true";
            break;
        case "-h" or "help":
            command = "help";
            break;
    }
}
// The file of the project folder (dotnet run from the project or from the solution folder), else the copy of the build.
configFile ??= new[] { "config/searchengine.yml", "searchengine.yml", "KAONOMI.SearchEngine/config/searchengine.yml",
        Path.Combine(AppContext.BaseDirectory, "config", "searchengine.yml") }
    .FirstOrDefault(File.Exists);
var settings = EngineSettings.Load(configFile, overrides);

switch (command)
{
    case "serve":
        return await Serve(settings);
    case "import":
        return await Importer.RunAsync(settings, options);
    case "check":
        return Check(settings);
    default:
        Console.WriteLine("""
            KAONOMI.SearchEngine: a small Elasticsearch 7.14 compatible search engine.

              KAONOMI.SearchEngine [--config searchengine.yml] [-E key=value]...
                  Serves the REST API (http.port, 9200 by default).

              KAONOMI.SearchEngine check [--config searchengine.yml]
                  Checks the Lucene indices of path.data (the engine must not be running).

              KAONOMI.SearchEngine import --from http://localhost:9200 [--indices a,b] [--user u --password p]
                                          [--batch 2000] [--overwrite] [--config searchengine.yml]
                  Copies the indices of an Elasticsearch (mappings and documents, ids kept) into path.data.
                  The engine must not be running on the same path.data.
            """);
        return command == "help" ? 0 : 1;
}

static async Task<int> Serve(EngineSettings settings)
{
    var host = await SearchEngineHost.StartAsync(settings);
    if (host is null)
        return 1;
    await host.WaitForShutdownAsync();
    return 0;
}

// Every index of path.data checked by Lucene (segments, files, postings, stored fields, doc values).
static int Check(EngineSettings settings)
{
    var log = EngineLog.For(settings);
    var folder = Path.Combine(settings.DataPath, "indices");
    if (!Directory.Exists(folder))
    {
        log.Error("check", $"no index in [{settings.DataPath}]");
        return 1;
    }
    FileStream dataLock;
    try
    {
        dataLock = IndexManager.LockDataPath(settings.DataPath);
    }
    catch (DataPathInUseException ex)
    {
        log.Error("check", ex.Message + ": stop the engine first");
        return 1;
    }
    using (dataLock)
    {
        var broken = 0;
        foreach (var index in Directory.GetDirectories(folder).OrderBy(f => f, StringComparer.Ordinal))
        {
            using var directory = Lucene.Net.Store.FSDirectory.Open(Path.Combine(index, "lucene"));
            var status = new Lucene.Net.Index.CheckIndex(directory).DoCheckIndex();
            var docs = status.SegmentInfos.Sum(s => s.DocCount - s.NumDeleted);
            if (status.Clean)
                log.Info("check", $"[{Path.GetFileName(index)}] clean: {status.NumSegments} segment(s), {docs} document(s)");
            else
            {
                broken++;
                log.Error("check", $"[{Path.GetFileName(index)}] BROKEN: {(status.MissingSegments ? "segments missing" : $"{status.NumBadSegments} bad segment(s)")}");
            }
        }
        return broken == 0 ? 0 : 2;
    }
}
