namespace KAONOMI.Server
{
    using MARI.Framework.Builders;
    using MARI.Framework.Configurations;
    using MARI.Framework.Core.Abstractions;

    using MARI.Framework.Node.Abstractions.Models;

    using KAONOMI.Core;
    using KAONOMI.Core.Helpers;
    using KAONOMI.Core.Hosting;
    using KAONOMI.Core.Models;
    using KAONOMI.Core.Models.LLM;

    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using System.Diagnostics;
    using System.Runtime.InteropServices;

    /// <summary>
    /// ASP.NET Core host for the Kaonomi dialog pipeline, Swagger UI, and REST endpoints.
    /// </summary>
    internal class Program
    {
        static private SessionManager _session;
        static private Config _config;
        static private string _defaultModel;
        static private string _idDialog;

        /// <summary>Port of the Orleans dashboard (self-hosted by the silo, separate from the Swagger/REST ports).</summary>
        private const int DashboardPort = 8090;

        /// <summary>Entry point: minimal ASP.NET Core pipeline with OpenAPI.</summary>
        static void Main(string[] args)
        {
            _defaultModel = "gpt-oss:20b";

            // Load configuration
            Config config = HelperConfigManager.GetFromFile<Config>("config.json");
            _config = config;
            string AppName = config.appName;

            HelperConsoleTheme.Start(config.appName, config.appDescription, config.appDocumentationUrl, config.consoleTheme);

            _session = new SessionManager(_config);
            _session.LogSave("Application started", AppName, "Info");

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddSwaggerGen(s =>
            {
                s.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo() { Title = "Tuto Demo", Version = "v1" });
                // One Swagger section per resource (first path segment: cache, messageflow, ontology...), in alphabetical
                // order, and inside a section the routes in alphabetical order, then GET, POST, PUT, DELETE.
                s.TagActionsBy(api => [SwaggerSection(api.RelativePath)]);
                s.OrderActionsBy(api => $"{SwaggerSection(api.RelativePath)}|{api.RelativePath}|{SwaggerMethodRank(api.HttpMethod)}");
            }).AddEndpointsApiExplorer();

            var jsonProcessSeq = Sequence.Build("jfo", fixUid: new Guid("D1F7C7EB-77F7-488A-91D7-77E4D5D16448"), metadataBuilder: m =>
            {
                m.Description("Dialog Sequence")
                 .AddTags("chatbot", "nexai");
            })
                             .RequiredInput<string>()
                             .Use<IDialogInboundGuardRailVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogPreProcessingVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogCacheVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogEntityVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogSymbolicAnalysisVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogForecastingValidatorVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogModelSelectionVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogProcessorVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogPostProcessingVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogEmotionEngineVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogForecastingVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogContextUpdateVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Use<IDialogOutboundGuardrailVAgent>().Configure("JFO-TABLE").Call((g, i, ctx) => g.DoProcess(i, ctx)).Return
                             .Build();

            builder.Host.UseMARINode(b =>
            {
                b.WizardConfig()
                .UseMongoCluster(mongo => mongo
                    .ConnectionString("mongodb://localhost:27017")
                    .ManualConfig((options, _) => options.DatabaseName = "kaonomi"),
                    serviceId: KaonomiSiloBuilderExtensions.ServiceId,
                    clusterId: KaonomiSiloBuilderExtensions.ClusterId)
                .AddEndpointOptions(new ClusterNodeEndPointOptions(loopback: true, siloPort: 11111, gatewayPort: 30000))
                .ConfigureLogging(c => c.AddConsole())
                .AddInMemoryDefinitionProvider(d => d.SetupSequences(jsonProcessSeq));
                b.ManualyAdvancedConfig().UseKaonomiNode("Kaonomi.Server", dashboardPort: DashboardPort);
            });

            /*  Without MongoDB, in-memory only
             *  
                         using var host = Host.CreateDefaultBuilder(args)
                .UseMARINode(b =>
                {
                    b.WizardConfig()
                     .NoCluster()
                     .ConfigureLogging(c => c.AddConsole())
                     .AddInMemoryDefinitionProvider(d => d.SetupSequences(jsonProcessSeq));
                })
                .Build();
             
             */

            var app = builder.Build();
            app.UseSwagger();



            

            app.MapPost("/aiassistant", async (DialogGenericRequest d, [FromServices] IMARIExecutionHandler handler) =>
            {
                DialogGenericResponse k = new DialogGenericResponse();
                try
                {
                    Dialog dialog = new Dialog();
                    dialog.userName = Environment.UserName;
                    dialog.machineName = Environment.MachineName;
                    dialog.date = DateTime.Now;
                    dialog.request = d;
                    string newid = _session.DialogSave(dialog);

                    _idDialog = newid;

                    var result = await handler.Sequence<string>(jsonProcessSeq.Uid)
                                           .SetInput(_idDialog)
                                           .RunAsync<string>();

                    var content = result.SafeGetResult();
                    _session.LogSave("Sequence executed", AppName, "INFO");
                    dialog = _session.DialogGet(newid);
                    k = dialog.response;
                }
                catch (Exception ex)
                {
                    _session.LogSave("Sequence executed with Error", AppName, "ERROR");
                }
                return k;
            });

            app.MapApiEndpoints(_session);


            app.UseSwaggerUI();

            // Open Swagger and the dashboard when the application starts
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                // Swagger is served by Kestrel, on the address the server actually listens on (applicationUrl /
                // ASPNETCORE_URLS); the Orleans dashboard has its own port.
                var serverUrl = app.Urls.FirstOrDefault(u => u.StartsWith("http://")) ?? app.Urls.FirstOrDefault();
                if (serverUrl is not null)
                    OpenUrl($"{serverUrl.TrimEnd('/')}/swagger");
                OpenUrl($"http://localhost:{DashboardPort}/");
                OpenUrl("http://localhost:5601/app/dashboards#/view/2a813290-bffc-11f1-877e-559a6b435d7a?_g=(filters:!(),refreshInterval:(pause:!t,value:0),time:(from:now-24h%2Fh,to:now))");
            });

            app.Run();
        }

        /// <summary>Swagger section of a route: its first path segment (<c>ontology/{id}</c> → <c>ontology</c>), "general" for the root.</summary>
        private static string SwaggerSection(string? relativePath)
        {
            var segment = (relativePath ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrEmpty(segment) || segment.StartsWith('{') ? "general" : segment.ToLowerInvariant();
        }

        /// <summary>Sort rank of an HTTP method in Swagger: GET, POST, PUT, PATCH, DELETE, then the others.</summary>
        private static int SwaggerMethodRank(string? method) => method?.ToUpperInvariant() switch
        {
            "GET" => 0,
            "POST" => 1,
            "PUT" => 2,
            "PATCH" => 3,
            "DELETE" => 4,
            _ => 5
        };

        /// <summary>
        /// Opens a URL in the default browser, in a cross-platform way.
        /// </summary>
        private static void OpenUrl(string url)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", url);
                }
            }
            catch (Exception ex)
            {
                HelperConsoleTheme.Warning($"Could not open URL {url}: {ex.Message}");
            }
        }
    }
}