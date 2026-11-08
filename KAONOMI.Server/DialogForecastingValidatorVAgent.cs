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
    /// Validates forecasting results attached to a dialog before later pipeline steps.
    /// </summary>
    internal class DialogForecastingValidatorVAgent : VGrainBase<IDialogForecastingValidatorVAgent>, IDialogForecastingValidatorVAgent
    {
        /// <summary>Creates the agent with MARI logging support.</summary>
        /// <param title="logger">Logger for this grain type.</param>
        public DialogForecastingValidatorVAgent(ILogger<IDialogForecastingValidatorVAgent> logger) : base(logger)
        {
        }

        /// <inheritdoc />
        public async Task<string> DoProcess(string idDialog, IExecutionContext Context)
        {
            Config config = HelperConfigManager.GetFromFile<Config>("config.json");
            string appName = config.appName + "_ForecastingValidatorVAgent";

            var session = new SessionManager(config);
            session.LogSave("Agent Called", appName, "INFO");

            Performance perf = new Performance();
            perf.date = DateTime.Now;
            perf.comment = "Forecasting validator DoProcess execution time.";
            perf.machineName = Environment.MachineName;
            perf.userName = Environment.UserName;
            perf.appName = appName;
            perf.operationName = "DialogForecastingValidatorVAgent.DoProcess";
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
                    throw; // Stop execution to avoid validating forecasting on a null object
                }

                // Run forecasting validation
                await RunForecastingValidatorAsync(dialog, session, appName, config);

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

        private async Task RunForecastingValidatorAsync(Dialog dialog, SessionManager session, string appName, Config config)
        {
            session.LogSave("Running Forecasting Validator", appName, "INFO");

            // TODO: Implement forecasting validation logic (consistency, confidence thresholds, business rules, etc.)
            await Task.CompletedTask;
        }
    }
}
