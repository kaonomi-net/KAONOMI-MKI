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
    /// Console host of the web scraper: runs the webscrapper sequence (collect the URLs of the active collection plans →
    /// visit the pages → save them) every <see cref="WebScrapperConfig.scrapeIntervalMinutes"/> minutes.
    /// Command line: <c>--once</c> runs once and stops; <c>--url &lt;url&gt;</c> (repeatable) visits these URLs instead of
    /// the collection plans, in the mode of <c>--mode fixed|explore|random</c> (fixed by default), with the keywords of
    /// <c>--keywords "a,b"</c>; <c>--plan &lt;id&gt;</c> visits that collection plan now (active or not; with --once, what
    /// the Launch agent button of the collection plan window does).
    /// </summary>
    internal class Program
    {
        private static SessionManager _session = null!;

        static async Task Main(string[] args)
        {
            var config = HelperConfigManager.GetFromFile<WebScrapperConfig>("config.json")
                ?? throw new InvalidOperationException("config.json could not be loaded.");
            var appName = config.appName;
            var (request, once, hostArgs) = ParseArguments(args);

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
            HelperConsoleTheme.Info(once ? "One run, then stop." : $"A run every {config.scrapeIntervalMinutes} min. Press Ctrl+C to stop.");

            try
            {
                var handler = host.Services.GetRequiredService<IMARIExecutionHandler>();
                var input = SequenceJson.Write(request);
                do
                {
                    await ExecuteSequenceAsync(handler, sequence.Uid, appName, input);
                    if (once)
                        break;
                    await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, config.scrapeIntervalMinutes)), cts.Token);
                } while (!cts.IsCancellationRequested);
            }
            catch (OperationCanceledException)
            {
                HelperConsoleTheme.Info("Sequence loop stopped.");
            }
            finally
            {
                await host.StopAsync();
            }
        }

        /// <summary>The URLs, mode and keywords of the command line, --once, and the arguments left for the host.</summary>
        private static (ScrapeRequest Request, bool Once, string[] HostArgs) ParseArguments(string[] args)
        {
            var request = new ScrapeRequest();
            var once = false;
            var rest = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--once":
                        once = true;
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
            return (request, once, rest.ToArray());
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
