using MARI.Framework.Core;
using MARI.Framework.Core.Abstractions;
using KAONOMI.Core;
using KAONOMI.Core.Helpers;
using KAONOMI.Core.Models;
using KAONOMI.Core.Models.LLM;
using Microsoft.Extensions.Logging;

namespace KAONOMI.Server
{
    /// <summary>
    /// Applies outbound moderation and safety checks on assistant responses.
    /// </summary>
    internal class DialogOutboundGuardrailVAgent : VGrainBase<IDialogOutboundGuardrailVAgent>, IDialogOutboundGuardrailVAgent
    {
        /// <summary>Creates the agent with MARI logging support.</summary>
        /// <param name="logger">Logger for this grain type.</param>
        public DialogOutboundGuardrailVAgent(ILogger<IDialogOutboundGuardrailVAgent> logger) : base(logger)
        {
        }

        /// <inheritdoc />
        public async Task<string> DoProcess(string idDialog, IExecutionContext Context)
        {
            Config config = HelperConfigManager.GetFromFile<Config>("config.json");
            string appName = config.appName + "_OutboundGuardrailVAgent";

            var session = new SessionManager(config);
            session.LogSave("Agent Called", appName, "INFO");

            Performance perf = new Performance();
            perf.date = DateTime.Now;
            perf.comment = "Outbound guardrail DoProcess execution time.";
            perf.machineName = Environment.MachineName;
            perf.userName = Environment.UserName;
            perf.appName = appName;
            perf.operationName = "DialogOutboundGuardrailVAgent.DoProcess";
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
                    throw; // Prevent processing on an unloaded object
                }

                // Run outbound guardrail rules
                await RunGuardrailAsync(dialog, session, appName, config);

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

        private async Task RunGuardrailAsync(Dialog dialog, SessionManager session, string appName, Config config)
        {
            session.LogSave("Running Outbound Guardrail Logic", appName, "INFO");

            // TODO: Implement outbound filtering (data/PII leakage, response moderation, JSON formatting, etc.)
            await Task.CompletedTask;
        }
    }
}