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
    /// Calls Ollama (or configured backend) to produce the assistant reply for a dialog turn.
    /// </summary>
    internal class DialogProcessorVAgent : VGrainBase<IDialogProcessorVAgent>, IDialogProcessorVAgent
    {
        /// <summary>Creates the agent with MARI logging support.</summary>
        /// <param title="logger">Logger for this grain type.</param>
        public DialogProcessorVAgent(ILogger<IDialogProcessorVAgent> logger) : base(logger)
        {
        }

        /// <inheritdoc />
        public async Task<string> DoProcess(string idDialog, IExecutionContext Context)
        {
            // Load configuration (locally or injected)
            Config config = HelperConfigManager.GetFromFile<Config>("config.json");
            string appName = config.appName + "_ProcssorVAgent";
            var session = new SessionManager(config);

            session.LogSave("Agent Called", appName, "INFO");

            Performance perf = new Performance();
            perf.date = DateTime.Now;
            perf.comment = "Dialog processor DoProcess execution time.";
            perf.machineName = Environment.MachineName;
            perf.userName = Environment.UserName;
            perf.appName = appName;
            perf.operationName = "DialogProcessorVAgent.DoProcess";
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
                    session.LogSave("Error loading dialog: " + ex.Message, appName, "ERROR");
                    throw; // Stop execution if the dialog cannot be loaded
                }

                // Main dialog processing (with await)
                await RunAsync(dialog, config, session, appName);

                try
                {
                    idDialog = session.DialogSave(dialog);
                    session.LogSave("Dialog Updated", appName, "INFO");
                }
                catch (Exception ex)
                {
                    session.LogSave("Error saving dialog: " + ex.Message, appName, "ERROR");
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

        private async Task RunAsync(Dialog dialog, Config config, SessionManager session, string appName)
        {
            DialogGenericRequest req = dialog.request;
            dialog.PromptUserText = HelperLlmParsersAndTools.GetUserPromptFromRequest(req);
            dialog.PromptSystemText = HelperLlmParsersAndTools.GetSystemPromptFromRequest(req);

            string selectedModel = req.model;
            // The system prompt of the chat (first "system" message of the request) is given to the model too.
            OllamaChatResult response = await HelperOllamaClient.SendChatMessageAsync(
                config.ollamaUrl, selectedModel, dialog.PromptUserText, dialog.PromptSystemText);

            dialog.response.id = Guid.NewGuid().ToString();
            dialog.response.choices = new List<Choice>
            {
                new Choice
                {
                    message = new Message
                    {
                        content = response.Content,
                        role = "assistant"
                    },
                    index = 0,
                    finish_reason = "stop"
                }
            };

            dialog.response.model = dialog.request.model;
            dialog.response.usage = new Usage
            {
                prompt_tokens = response.PromptTokens,
                completion_tokens = response.ResponseTokens,
                total_tokens = response.TotalTokens
            };

            try
            {
                var tokensUsage = new TokensUsage
                {
                    prompt_tokens = response.PromptTokens,
                    completion_tokens = response.ResponseTokens,
                    date = DateTime.UtcNow,
                    model = dialog.response.model,
                    appName = appName,
                    comment = "",
                    machineName = Environment.MachineName,
                    userName = Environment.UserName
                };

                session.TokensUsageSave(tokensUsage);
            }
            catch (Exception ex)
            {
                session.LogSave("Error saving tokens usage: " + ex.Message, appName, "ERROR");
            }
        }
    }
}