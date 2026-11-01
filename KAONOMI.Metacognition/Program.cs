namespace KAONOMI.Metacognition
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
    /// Console host: loads config.json and runs the metacognition sequence every second.
    /// </summary>
    internal class Program
    {
        static private SessionManager _session = null!;
        static private Config _config = null!;

        /// <summary>Loads configuration, starts the MARI host, and runs the metacognition sequence every second.</summary>
        /// <param name="args">Command-line arguments passed to the generic host.</param>
        static async Task Main(string[] args)
        {
            Config config = HelperConfigManager.GetFromFile<Config>("config.json")
                ?? throw new InvalidOperationException("config.json could not be loaded.");
            _config = config;
            string AppName = config.appName;

            // Background host: on a multi-screen PC, opened on the smallest screen (cell 1 of a 2x2 grid).
            HelperConsoleTheme.PlaceOnSmallestScreen(slot: 1, slotCount: 6);
            HelperConsoleTheme.Start(config.appName, config.appDescription, config.appDocumentationUrl, config.consoleTheme);

            _session = new SessionManager(_config);
            _session.LogSave("Application started", AppName, "Info");

            var jsonProcessSeq = Sequence.Build("metacognition", fixUid: new Guid("A8E4C1D2-6B3F-4E91-9C70-2F5A8D14E6B3"), metadataBuilder: m =>
            {
                m.Description("Metacognition context update sequence")
                 .AddTags("metacognition", "context");
            })
                             .RequiredInput<string>()
                             .Use<iMetaCognitionCollecVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<iMetaCognitionAnalysisVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<iMetaCognitionActionsVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
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
                     .AddEndpointOptions(new ClusterNodeEndPointOptions(loopback: true, siloPort: 11115, gatewayPort: 30004))
                     .ConfigureLogging(c => c.AddConsole())
                     .AddInMemoryDefinitionProvider(d => d.SetupSequences(jsonProcessSeq));
                    b.ManualyAdvancedConfig().UseKaonomiNode("Kaonomi.Metacognition", dashboardPort: 8085);
                })
                .Build();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await host.StartAsync(cts.Token);
            HelperConsoleTheme.Info("Sequence loop started. Press Ctrl+C to stop.");

            try
            {
                var handler = host.Services.GetRequiredService<IMARIExecutionHandler>();

                while (!cts.IsCancellationRequested)
                {
                    await ExecuteSequenceAsync(handler, jsonProcessSeq.Uid, AppName);
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
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

        /// <summary>Runs the metacognition MARI sequence once and logs success or failure.</summary>
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
