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
    /// After the answer: keeps the exchange (the question, the answer, their entities) in the context of its
    /// conversation (<see cref="DialogContext"/>), where the older exchanges become digests; the processor writes this
    /// context before the next question of the conversation (see <see cref="Knowledge.ContextManager"/>).
    /// </summary>
    internal class DialogContextUpdateVAgent : VGrainBase<IDialogContextUpdateVAgent>, IDialogContextUpdateVAgent
    {
        /// <summary>Creates the agent with MARI logging support.</summary>
        /// <param title="logger">Logger for this grain type.</param>
        public DialogContextUpdateVAgent(ILogger<IDialogContextUpdateVAgent> logger) : base(logger)
        {
        }

        /// <inheritdoc />
        public async Task<string> DoProcess(string idDialog, IExecutionContext Context)
        {
            Config config = HelperConfigManager.GetFromFile<Config>("config.json");
            string appName = config.appName + "_ContextUpdateVAgent";

            var session = new SessionManager(config);
            session.LogSave("Agent Called", appName, "INFO");

            Performance perf = new Performance();
            perf.date = DateTime.Now;
            perf.comment = "Context update DoProcess execution time.";
            perf.machineName = Environment.MachineName;
            perf.userName = Environment.UserName;
            perf.appName = appName;
            perf.operationName = "DialogContextUpdateVAgent.DoProcess";
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
                    throw; // Stop execution to avoid updating context on a null object
                }

                // Run context update
                await RunContextUpdateAsync(dialog, session, appName, config);

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

        private async Task RunContextUpdateAsync(Dialog dialog, SessionManager session, string appName, Config config)
        {
            session.LogSave("Running Context Update", appName, "INFO");

            // The exchange that was just answered joins the context of its conversation (DialogContext, its id the id
            // of the session): the processor will write it before the next question. A request without a session has
            // no context to keep.
            if (string.IsNullOrWhiteSpace(dialog.SessionId))
            {
                session.LogSave("No session in the request: no context kept", appName, "INFO");
                return;
            }
            var completion = dialog.response?.choices?.FirstOrDefault()?.message?.content;
            if (string.IsNullOrWhiteSpace(completion) || dialog.request is null)
            {
                session.LogSave("No answer: the context is left as it is", appName, "INFO");
                return;
            }
            try
            {
                // The question as the user asked it: without the exchanges the chat may have written before it, nor the
                // name it is addressed to.
                var prompt = string.IsNullOrWhiteSpace(dialog.PromptUserText) ? HelperLlmParsersAndTools.GetUserPromptFromRequest(dialog.request) : dialog.PromptUserText;
                var question = Knowledge.ContextManager.Question(prompt, bare: true);

                // The entities of the question and of the answer: those that come back are what the context keeps.
                var ner = Ner.NerService.Get(config);
                var entities = await Task.Run(() => ner.Extract(question, types: null).entities.Concat(ner.Extract(completion, types: null).entities).ToList());
                var names = Knowledge.ContextManager.EntityNames(entities);

                var context = session.DialogContextGet(dialog.SessionId) ?? new DialogContext { id = dialog.SessionId.Trim(), appName = appName };
                context.userName = dialog.userName;
                context.machineName = dialog.machineName;
                var note = Knowledge.ContextManager.AddTurn(context, dialog.id, question, completion, names, config);
                session.DialogContextSave(context);
                session.LogSave($"Context of {dialog.SessionId}: {note}", appName, "INFO");
            }
            catch (Exception ex)
            {
                session.LogSave($"Error updating the context of '{dialog.SessionId}': {ex.Message}", appName, "ERROR");
            }
        }
    }
}
