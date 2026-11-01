namespace KAONOMI.Dreamer
{
    using MARI.Framework.Builders;
    using MARI.Framework.Configurations;
    using MARI.Framework.Core.Abstractions;
    using MARI.Framework.Node.Extensions;

    using MARI.Framework.Node.Abstractions.Models;

    using KAONOMI.Core;
    using KAONOMI.Core.Helpers;
    using KAONOMI.Core.Hosting;
    using KAONOMI.Core.Models;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Console host: loads config.json and runs the dreamer sequence every 15 minutes between 22:00 and 06:00.
    /// </summary>
    internal class Program
    {
        private static readonly TimeSpan NightStart = TimeSpan.FromHours(22);
        private static readonly TimeSpan NightEnd = TimeSpan.FromHours(6);
        private static readonly TimeSpan RunInterval = TimeSpan.FromMinutes(15);

        static private SessionManager _session = null!;
        static private Config _config = null!;

        /// <summary>Loads configuration, starts the MARI host, and runs the dreamer sequence on a night schedule.</summary>
        /// <param name="args">Command-line arguments passed to the generic host.</param>
        static async Task Main(string[] args)
        {
            Config config = HelperConfigManager.GetFromFile<Config>("config.json")
                ?? throw new InvalidOperationException("config.json could not be loaded.");
            _config = config;
            string AppName = config.appName;

            // Background host: on a multi-screen PC, opened on the smallest screen (cell 0 of a 2x2 grid).
            HelperConsoleTheme.PlaceOnSmallestScreen(slot: 0, slotCount: 6);
            HelperConsoleTheme.Start(config.appName, config.appDescription, config.appDocumentationUrl, config.consoleTheme);

            _session = new SessionManager(_config);
            _session.LogSave("Application started", AppName, "Info");

            var jsonProcessSeq = Sequence.Build("dreamer", fixUid: new Guid("D3A8E6C2-1F47-4B90-8E15-7C2D9A4B6F01"), metadataBuilder: m =>
            {
                m.Description("Dreamer context update sequence")
                 .AddTags("dreamer", "context");
            })
                             .RequiredInput<string>()
                             .Use<iDreamerCollecVAgent>().Configure("DREAMER-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<iDreamerAnalysisVAgent>().Configure("DREAMER-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<iDreamerActionsVAgent>().Configure("DREAMER-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Build();

            using var host = Host.CreateDefaultBuilder(args)
                .UseMARINode(b =>
                {
                    b.WizardConfig()
                     .UseMongoCluster(mongo => mongo
                         .ConnectionString("mongodb://localhost:27017")
                         .ManualConfig((options, _) => options.DatabaseName = "kaonomi"),
                         serviceId: KaonomiSiloBuilderExtensions.ServiceId,
                         clusterId: KaonomiSiloBuilderExtensions.ClusterId)
                     .AddEndpointOptions(new ClusterNodeEndPointOptions(loopback: true, siloPort: 11112, gatewayPort: 30001))
                     .ConfigureLogging(c => c.AddConsole())
                     .AddInMemoryDefinitionProvider(d => d.SetupSequences(jsonProcessSeq));
                    b.ManualyAdvancedConfig().UseKaonomiNode("Kaonomi.Dreamer");
                })
                .Build();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await host.StartAsync(cts.Token);
            HelperConsoleTheme.Info("Sequence loop started (22:00–06:00, every 15 minutes). Press Ctrl+C to stop.");

            try
            {
                var handler = host.Services.GetRequiredService<IMARIExecutionHandler>();
                var lastRun = DateTime.MinValue;

                while (!cts.IsCancellationRequested)
                {
                    var nextRun = GetNextNightRun(DateTime.Now);
                    if (nextRun <= lastRun)
                        nextRun = GetNextNightRun(lastRun.Add(RunInterval));

                    var delay = nextRun - DateTime.Now;
                    if (delay > TimeSpan.Zero)
                    {
                        HelperConsoleTheme.Trace($"Next run at {nextRun:yyyy-MM-dd HH:mm}.");
                        await Task.Delay(delay, cts.Token);
                    }

                    if (!IsNightWindow(DateTime.Now))
                        continue;

                    await ExecuteSequenceAsync(handler, jsonProcessSeq.Uid, AppName);
                    lastRun = DateTime.Now;
                }
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

        /// <summary>Returns whether the given local time falls in the 22:00–06:00 night window.</summary>
        private static bool IsNightWindow(DateTime time)
        {
            var timeOfDay = time.TimeOfDay;
            return timeOfDay >= NightStart || timeOfDay < NightEnd;
        }

        /// <summary>Computes the next 15-minute slot that lies within the night window.</summary>
        private static DateTime GetNextNightRun(DateTime now)
        {
            var slot = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute / 15 * 15, 0);
            if (slot < now)
                slot = slot.Add(RunInterval);

            while (!IsNightWindow(slot))
                slot = slot.Add(RunInterval);

            return slot;
        }

        /// <summary>Runs the dreamer MARI sequence once and logs success or failure.</summary>
        private static async Task ExecuteSequenceAsync(IMARIExecutionHandler handler, Guid sequenceUid, string appName)
        {
            try
            {
                var result = await handler.Sequence<string>(sequenceUid)
                                       .SetInput("")
                                       .RunAsync<string>();

                result.SafeGetResult();
                _session.LogSave("Sequence executed", appName, "INFO");
                
            }
            catch (Exception ex)
            {
                _session.LogSave($"Sequence executed with Error: {ex.Message}", appName, "ERROR");
                HelperConsoleTheme.Error($"Sequence error: {ex.Message}");
            }
        }
    }
}
