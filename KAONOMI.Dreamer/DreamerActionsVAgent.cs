namespace KAONOMI.Dreamer
{
    using MARI.Framework.Core;
    using MARI.Framework.Core.Abstractions;
    using KAONOMI.Core;
    using KAONOMI.Core.Helpers;
    using KAONOMI.Core.Models;
    using Microsoft.Extensions.Logging;

    /// <summary>MARI grain implementation for the Dreamer actions stage.</summary>
    internal class DreamerActionsVAgent : VGrainBase<iDreamerActionsVAgent>, iDreamerActionsVAgent
    {
        /// <summary>Creates the agent with the given logger.</summary>
        /// <param name="logger">Logger for action operations.</param>
        public DreamerActionsVAgent(ILogger<iDreamerActionsVAgent> logger) : base(logger)
        {
        }

        /// <inheritdoc />
        public async Task<string> DoProcess(string input, IExecutionContext context)
        {
            Config config = HelperConfigManager.GetFromFile<Config>("config.json")
                ?? throw new InvalidOperationException("config.json could not be loaded.");
            string appName = config.appName ;

            var session = new SessionManager(config);
            session.LogSave("Agent Called", appName, "INFO");

            Performance perf = new Performance();
            perf.date = DateTime.Now;
            perf.comment = "Actions DoProcess execution time.";
            perf.machineName = Environment.MachineName;
            perf.userName = Environment.UserName;
            perf.appName = appName;
            perf.operationName = "DoProcess";
            perf.startTime = perf.date;
            perf.metadatas = new List<MetaData>();
            MetaData dialogIdMeta = new MetaData();


            try
            {
                await RunActionsAsync(session, appName, config);
                return input;
            }
            finally
            {
                perf.endTime = DateTime.Now;
                perf.durationMilliseconds = (int)(perf.endTime - perf.startTime).TotalMilliseconds;
                ServiceReturn res = session.PerformanceSave(perf);
                Console.WriteLine($"Performance saved: {res.Message} id {res.ReturnedId}");
                Console.WriteLine($"Duration: {perf.durationMilliseconds} ms");
            }
        }

        private async Task RunActionsAsync(SessionManager session, string appName, Config config)
        {
            session.LogSave("Running Actions", appName, "INFO");

            // TODO: Implement actions logic (enrichment, synchronization, session variables, etc.)
            await Task.CompletedTask;
        }
    }
}
