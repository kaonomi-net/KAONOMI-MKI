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
    /// Implements named-entity and related extraction for dialogs.
    /// </summary>
    internal class DialogEntityVAgent : VGrainBase<IDialogEntityVAgent>, IDialogEntityVAgent
    {
        /// <summary>Creates the agent with MARI logging support.</summary>
        /// <param name="logger">Logger for this grain type.</param>
        public DialogEntityVAgent(ILogger<IDialogEntityVAgent> logger) : base(logger)
        {
        }

        /// <inheritdoc />
        public async Task<string> DoProcess(string idDialog, IExecutionContext Context)
        {
            Config config = HelperConfigManager.GetFromFile<Config>("config.json");
            string appName = config.appName + "_EntityVAgent";

            var session = new SessionManager(config);
            session.LogSave("Agent Called", appName, "INFO");

            Performance perf = new Performance();
            perf.date = DateTime.Now;
            perf.comment = "Entity processing DoProcess execution time.";
            perf.machineName = Environment.MachineName;
            perf.userName = Environment.UserName;
            perf.appName = appName;
            perf.operationName = "DialogEntityVAgent.DoProcess";
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
                    throw; // Stop execution to avoid entity extraction on a null object
                }

                // Run entity processing
                await RunEntityAsync(dialog, session, appName, config);

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

        private async Task RunEntityAsync(Dialog dialog, SessionManager session, string appName, Config config)
        {
            session.LogSave("Running Entity Processing", appName, "INFO");

            // The NER of the server (POST /ner): the model, the rules and the names of the ontology, kept with the dialog.
            // The prompt of the user is read from the request: PromptUserText is only filled by the processor, which comes
            // after this agent in the sequence.
            var prompt = string.IsNullOrWhiteSpace(dialog.PromptUserText) && dialog.request is not null
                ? HelperLlmParsersAndTools.GetUserPromptFromRequest(dialog.request)
                : dialog.PromptUserText;
            var result = await Task.Run(() => Ner.NerService.Get(config).Extract(prompt));
            dialog.Entities = result.entities;
            foreach (var warning in result.warnings)
                session.LogSave(warning, appName, "WARNING");
            session.LogSave($"{result.entities.Count} entities in {result.durationMs} ms: " +
                string.Join(", ", result.entities.Take(20).Select(e => $"{e.type}:{e.text}")), appName, "INFO");
        }
    }
}
