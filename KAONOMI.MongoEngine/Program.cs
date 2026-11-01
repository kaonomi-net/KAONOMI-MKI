using KAONOMI.MongoEngine;
using KAONOMI.MongoEngine.Configuration;

// KAONOMI.MongoEngine: a small server that speaks the wire protocol of MongoDB, for MARI and Orleans.
//   KAONOMI.MongoEngine [--config mongoengine.yml] [--set key=value]…   serves on net.port (27017)

string? configFile = null;
var overrides = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" or "-f" when i + 1 < args.Length:
            configFile = args[++i];
            break;
        case "--set" when i + 1 < args.Length:
            overrides.Add(args[++i]);
            break;
        case "--port" when i + 1 < args.Length:
            overrides.Add("net.port=" + args[++i]);
            break;
        case "--dbpath" when i + 1 < args.Length:
            overrides.Add("storage.dbPath=" + args[++i]);
            break;
        case "-h" or "--help":
            Console.WriteLine("""
                KAONOMI.MongoEngine: a small MongoDB compatible server (wire protocol, standalone) for MARI and Orleans.

                  KAONOMI.MongoEngine [--config mongoengine.yml] [--set key=value]... [--port 27017] [--dbpath folder]
                """);
            return 0;
    }
}
// The file of the project folder (dotnet run from the project or from the solution folder), else the copy of the build.
configFile ??= new[] { "config/mongoengine.yml", "mongoengine.yml", "KAONOMI.MongoEngine/config/mongoengine.yml",
        Path.Combine(AppContext.BaseDirectory, "config", "mongoengine.yml") }
    .FirstOrDefault(File.Exists);
var settings = EngineSettings.Load(configFile, overrides);

var host = MongoEngineHost.Start(settings);
if (host is null)
    return 1;
var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    stop.TrySetResult();
});
await Task.WhenAny(stop.Task, host.ShutdownRequested);
await host.DisposeAsync();
return 0;
