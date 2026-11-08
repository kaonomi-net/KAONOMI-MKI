namespace KAONOMI.Server
{
    using MARI.Framework.Core;
    using MARI.Framework.Core.Abstractions;
    using KAONOMI.Core;
    using KAONOMI.Core.Helpers;
    using KAONOMI.Core.Models;
    using KAONOMI.Core.Models.LLM;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Selects the appropriate LLM for the current dialog based on request and policy.
    /// </summary>
    internal class DialogModelSelectionVAgent : VGrainBase<IDialogModelSelectionVAgent>, IDialogModelSelectionVAgent
    {
        /// <summary>Creates the agent with MARI logging support.</summary>
        /// <param title="logger">Logger for this grain type.</param>
        public DialogModelSelectionVAgent(ILogger<IDialogModelSelectionVAgent> logger) : base(logger)
        {
        }

        /// <inheritdoc />
        public async Task<string> DoProcess(string idDialog, IExecutionContext Context)
        {
            Config config = HelperConfigManager.GetFromFile<Config>("config.json");
            string appName = config.appName + "_ModelSelectionVAgent";

            var session = new SessionManager(config);
            session.LogSave("Agent Called", appName, "INFO");

            Performance perf = new Performance();
            perf.date = DateTime.Now;
            perf.comment = "Model selection DoProcess execution time.";
            perf.machineName = Environment.MachineName;
            perf.userName = Environment.UserName;
            perf.appName = appName;
            perf.operationName = "DialogModelSelectionVAgent.DoProcess";
            perf.startTime = perf.date;
            perf.metadatas = new List<MetaData>();
            MetaData dialogIdMeta = new MetaData();
            dialogIdMeta.Key = "DialogId";
            dialogIdMeta.Value_String = idDialog;
            perf.metadatas.Add(dialogIdMeta);

            try
            {
                Dialog dialog;
                try
                {
                    dialog = session.DialogGet(idDialog);
                    session.LogSave("Dialog Loaded", appName, "INFO");
                }
                catch (Exception ex)
                {
                    session.LogSave($"Error loading dialog '{idDialog}': {ex.Message}", appName, "ERROR");
                    throw; // Stop execution to avoid selecting a model on a null object
                }

                // Run model selection
                await RunModelSelectionAsync(dialog, session, appName, config);

                try
                {
                    idDialog = session.DialogSave(dialog);
                    session.LogSave("Dialog Updated", appName, "INFO");
                }
                catch (Exception ex)
                {
                    session.LogSave($"Error saving dialog '{idDialog}': {ex.Message}", appName, "ERROR");
                    throw;
                }

                return idDialog;
            }
            finally
            {
                perf.endTime = DateTime.Now;
                perf.durationMilliseconds = (int)(perf.endTime - perf.startTime).TotalMilliseconds;
                session.PerformanceSave(perf);
            }
        }

        private async Task RunModelSelectionAsync(Dialog dialog, SessionManager session, string appName, Config config)
        {
            session.LogSave("Running Model Selection", appName, "INFO");

            // TODO: Implement model selection logic (routing, cost/latency, capability, fallback, etc.)
            await Task.CompletedTask;
        }
    }
}
