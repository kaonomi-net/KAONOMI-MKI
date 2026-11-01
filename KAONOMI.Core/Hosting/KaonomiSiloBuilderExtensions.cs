namespace KAONOMI.Core.Hosting
{
    using Microsoft.Extensions.DependencyInjection;

    using Orleans.Configuration;
    using Orleans.Hosting;
    using Orleans.Metadata;
    using Orleans.Runtime;
    using Orleans.Runtime.Placement;

    /// <summary>
    /// Shared cluster identity so every Kaonomi process appears on the same Orleans dashboard.
    /// </summary>
    public static class KaonomiSiloBuilderExtensions
    {
        /// <summary>Orleans service id shared by all Kaonomi silos.</summary>
        public const string ServiceId = "kaonomi";

        /// <summary>Orleans cluster id shared by all Kaonomi silos.</summary>
        public const string ClusterId = "kaonomi";

        /// <summary>
        /// Time a grain call may take before Orleans fails it (30 s by default). An agent waiting on an LLM generation
        /// (a long answer, a tutorial…) easily exceeds that; aligned with the Ollama client timeout (HelperOllamaClient).
        /// </summary>
        public static readonly TimeSpan GrainCallTimeout = TimeSpan.FromMinutes(10);

        /// <summary>Applies Kaonomi silo name, optional dashboard, grain call timeout and local sequence executor placement.</summary>
        /// <param name="dashboardPort">Port of the dashboard web UI; when null the silo only reports its statistics.</param>
        public static ISiloBuilder UseKaonomiNode(this ISiloBuilder silo, string siloName, int? dashboardPort = null)
        {
            silo.Configure<SiloOptions>(options => options.SiloName = siloName);
            silo.Configure<SiloMessagingOptions>(options => options.ResponseTimeout = GrainCallTimeout);
            silo.UseDashboard(options =>
            {
                options.HostSelf = dashboardPort.HasValue;
                if (dashboardPort.HasValue)
                {
                    options.Port = dashboardPort.Value;
                    // This machine only: by default the dashboard listens on every interface (the network), without
                    // a password, and shows (and lets act on) the grains of the cluster.
                    options.Host = "localhost";
                }
            });
            silo.ConfigureServices(services =>
                services.AddSingleton<IPlacementStrategyResolver, LocalSequenceExecutorPlacementResolver>());
            return silo;
        }

        /// <summary>
        /// Sequence definitions are registered in memory on the node that owns them.
        /// Keep the executor on that node so another silo does not report the definition as missing.
        /// </summary>
        private sealed class LocalSequenceExecutorPlacementResolver : IPlacementStrategyResolver
        {
            /// <summary>Assigns prefer-local placement to sequence executor grains.</summary>
            public bool TryResolvePlacementStrategy(GrainType grainType, GrainProperties properties, out PlacementStrategy? strategy)
            {
                var name = grainType.ToString() ?? string.Empty;
                if (name.Contains("sequenceexecutor", StringComparison.OrdinalIgnoreCase))
                {
                    strategy = new PreferLocalPlacement();
                    return true;
                }

                strategy = null;
                return false;
            }
        }
    }
}
