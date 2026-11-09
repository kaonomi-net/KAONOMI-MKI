namespace KAONOMI.Agent.WebScrapper
{
    using KAONOMI.Agent.WebScrapper.Agents;
    using KAONOMI.Agent.WebScrapper.Scraping;
    using KAONOMI.Core;
    using KAONOMI.Core.Helpers;
    using KAONOMI.Core.Hosting;
    using MARI.Framework.Builders;
    using MARI.Framework.Configurations;
    using MARI.Framework.Core.Abstractions;
    using MARI.Framework.Node.Abstractions.Models;
    using MARI.Framework.Node.Extensions;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Console host of the web scraper, one-shot: started on demand (LAUNCH AGENT of the collection plan window of the
    /// chat, /process, a terminal), it runs the webscrapper sequence once (collect the URLs → visit the pages → save them)
    /// and stops. It never runs in a loop.
    /// Command line: <c>--url &lt;url&gt;</c> (repeatable) visits these URLs instead of the collection plans, in the mode
    /// of <c>--mode fixed|explore|random</c> (fixed by default), with the keywords of <c>--keywords "a,b"</c>;
    /// <c>--plan &lt;id&gt;</c> visits that collection plan now, active or not (what LAUNCH AGENT does); without them,
    /// the active collection plans. <c>--once</c>, from before, is still accepted (it changes nothing).
    /// </summary>
    internal class Program
    {
        private static SessionManager _session = null!;

        static async Task Main(string[] args)
        {
            var config = HelperConfigManager.GetFromFile<WebScrapperConfig>("config.json")
                ?? throw new InvalidOperationException("config.json could not be loaded.");
            var appName = config.appName;
            var (request, hostArgs) = ParseArguments(args);

            HelperConsoleTheme.PlaceOnSmallestScreen(slot: 6, slotCount: 7);
            HelperConsoleTheme.Start(config.appName, config.appDescription, config.appDocumentationUrl, config.consoleTheme);

            _session = new SessionManager(config);
            _session.LogSave("Application started", appName, "Info");
            HelperConsoleTheme.Info($"Screenshots and pictures in {config.ResolvedOutputFolder}");

            var sequence = Sequence.Build("webscrapper", fixUid: new Guid("4E8A2C71-5B3D-4F96-A1E7-9C0D6B2F8A35"), metadataBuilder: m =>
            {
                m.Description("Web scraper of the collection plans: pages, screenshots, pictures, paragraphs and entities")
                 .AddTags("webscrapper", "collectionplan", "ner");
            })
                .RequiredInput<string>()
                .Use<iWebScrapperCollecVAgent>().Configure("WEBSCRAPPER-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                .Use<iWebScrapperAnalysisVAgent>().Configure("WEBSCRAPPER-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                .Use<iWebScrapperActionsVAgent>().Configure("WEBSCRAPPER-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                .Build();

            using var host = Host.CreateDefaultBuilder(hostArgs)
                .UseMARINode(b =>
                {
                    b.WizardConfig()
                     .UseMongoCluster(mongo => mongo
                         .ConnectionString("mongodb://localhost:27017")
                         .ManualConfig((options, _) => options.DatabaseName = "kaonomi"),
                         serviceId: KaonomiSiloBuilderExtensions.ServiceId,
                         clusterId: KaonomiSiloBuilderExtensions.ClusterId)
                     .AddEndpointOptions(new ClusterNodeEndPointOptions(loopback: true, siloPort: 11118, gatewayPort: 30007))
                     .ConfigureLogging(c => c.AddConsole())
                     .AddInMemoryDefinitionProvider(d => d.SetupSequences(sequence));
                    b.ManualyAdvancedConfig().UseKaonomiNode("Kaonomi.WebScrapper");
                })
                .Build();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await host.StartAsync(cts.Token);
            HelperConsoleTheme.Info("One run, then stop.");

            try
            {
                var handler = host.Services.GetRequiredService<IMARIExecutionHandler>();
                var run = ExecuteSequenceAsync(handler, sequence.Uid, appName, SequenceJson.Write(request));
                // Ctrl+C stops the run (what was saved stays).
                await Task.WhenAny(run, Task.Delay(Timeout.Infinite, cts.Token));
                if (!run.IsCompleted)
                    HelperConsoleTheme.Info("Run stopped (Ctrl+C).");
            }
            finally
            {
                await host.StopAsync();
            }
        }

        /// <summary>The URLs, plan, mode and keywords of the command line, and the arguments left for the host.</summary>
        private static (ScrapeRequest Request, string[] HostArgs) ParseArguments(string[] args)
        {
            var request = new ScrapeRequest();
            var rest = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--once":
                        // From when the agent ran in a loop: every run is one run now.
                        break;
                    case "--url" when i + 1 < args.Length:
                        request.urls.Add(args[++i]);
                        break;
                    case "--plan" when i + 1 < args.Length:
                        request.planId = args[++i];
                        break;
                    case "--mode" when i + 1 < args.Length:
                        request.mode = args[++i];
                        break;
                    case "--keywords" when i + 1 < args.Length:
                        request.keywords = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        break;
                    default:
                        rest.Add(args[i]);
                        break;
                }
            }
            return (request, rest.ToArray());
        }

        /// <summary>Runs the webscrapper sequence once and logs success or failure.</summary>
        private static async Task ExecuteSequenceAsync(IMARIExecutionHandler handler, Guid sequenceUid, string appName, string input)
        {
            try
            {
                var result = await handler.Sequence<string>(sequenceUid).SetInput(input).RunAsync<string>();
                var summary = result.SafeGetResult();
                _session.LogSave($"Sequence executed: {summary}", appName, "INFO");
                HelperConsoleTheme.Info($"Run done: {summary}");
            }
            catch (Exception ex)
            {
                _session.LogSave($"Sequence executed with Error: {ex.Message}", appName, "ERROR");
                HelperConsoleTheme.Error($"Sequence error: {ex.Message}");
            }
        }
    }
}
