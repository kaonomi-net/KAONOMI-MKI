namespace KAONOMI.Reflex
{
    using MARI.Framework.Core;
    using MARI.Framework.Core.Abstractions;
    using KAONOMI.Core;
    using KAONOMI.Core.Helpers;
    using KAONOMI.Core.Models;
    using Microsoft.Extensions.Logging;

    /// <summary>MARI grain implementation for the Reflex analysis stage.</summary>
    internal class ReflexAnalysisVAgent : VGrainBase<iReflexAnalysisVAgent>, iReflexAnalysisVAgent
    {
        /// <summary>Creates the agent with the given logger.</summary>
        /// <param title="logger">Logger for analysis operations.</param>
        public ReflexAnalysisVAgent(ILogger<iReflexAnalysisVAgent> logger) : base(logger)
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
            perf.comment = "Analysis DoProcess execution time.";
            perf.machineName = Environment.MachineName;
            perf.userName = Environment.UserName;
            perf.appName = appName;
            perf.operationName = "DoProcess";
            perf.startTime = perf.date;
            perf.metadatas = new List<MetaData>();
            MetaData dialogIdMeta = new MetaData();


            try
            {
                await RunAnalysisAsync(session, appName, config);
                return input;
            }
            finally
            {
                perf.endTime = DateTime.Now;
                perf.durationMilliseconds = (int)(perf.endTime - perf.startTime).TotalMilliseconds;
                session.PerformanceSave(perf);
            }
        }

        private async Task RunAnalysisAsync(SessionManager session, string appName, Config config)
        {
            session.LogSave("Running Analysis", appName, "INFO");

            // TODO: Implement analysis logic (patterns, gaps, next actions, etc.)
            await Task.CompletedTask;
        }
    }
}
