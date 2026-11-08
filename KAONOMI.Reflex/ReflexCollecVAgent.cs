namespace KAONOMI.Reflex
{
    using MARI.Framework.Core;
    using MARI.Framework.Core.Abstractions;
    using KAONOMI.Core;
    using KAONOMI.Core.Helpers;
    using KAONOMI.Core.Models;
    using Microsoft.Extensions.Logging;

    /// <summary>MARI grain implementation for the Reflex collection stage.</summary>
    internal class ReflexCollecVAgent : VGrainBase<iReflexCollecVAgent>, iReflexCollecVAgent
    {
        /// <summary>Creates the agent with the given logger.</summary>
        /// <param title="logger">Logger for collection operations.</param>
        public ReflexCollecVAgent(ILogger<iReflexCollecVAgent> logger) : base(logger)
        {
        }

        /// <inheritdoc />
        public async Task<string> DoProcess(string input, IExecutionContext context)
        {
            Config config = HelperConfigManager.GetFromFile<Config>("config.json")
                ?? throw new InvalidOperationException("config.json could not be loaded.");
            string appName = config.appName;

            var session = new SessionManager(config);
            session.LogSave("Agent Called", appName, "INFO");

            Performance perf = new Performance();
            perf.date = DateTime.Now;
            perf.comment = "Collection DoProcess execution time.";
            perf.machineName = Environment.MachineName;
            perf.userName = Environment.UserName;
            perf.appName = appName;
            perf.operationName = "ReflexCollecVAgent.DoProcess";
            perf.startTime = perf.date;
            perf.metadatas = new List<MetaData>();

            try
            {
                await RunCollectionAsync(session, appName, config);
                return input;
            }
            finally
            {
                perf.endTime = DateTime.Now;
                perf.durationMilliseconds = (int)(perf.endTime - perf.startTime).TotalMilliseconds;
                session.PerformanceSave(perf);
            }
        }

        private async Task RunCollectionAsync(SessionManager session, string appName, Config config)
        {
            session.LogSave("Running Collection", appName, "INFO");

            // TODO: Implement collection logic (recent dialogs, context, signals, etc.)
            await Task.CompletedTask;
        }
    }
}
