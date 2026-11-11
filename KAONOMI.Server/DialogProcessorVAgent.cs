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
    /// Produces the answer of a dialog turn: writes in the prompt what the agent of the entities found (the entities of
    /// the ontology and the intels, <see cref="Dialog.Knowledge"/>), compresses the prompt to the budget of config.json
    /// (<c>promptMaxChars</c>, <c>promptKnowledgeMaxChars</c>; see <see cref="Knowledge.PromptCompressor"/>), writes
    /// before the question the context the server keeps for the conversation (<see cref="Knowledge.ContextManager"/>), then asks
    /// the model through <c>ollamaUrl</c>: the LLM gateway (semantic cache, router of the models), or Ollama / LM Studio.
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

            // The knowledge of the entities goes in the system prompt, and the whole is made to fit; the question of the
            // user is sent as it is (the semantic cache of the gateway compares it).
            // The context of the conversation, when the request names one and the server keeps it: its exchanges, the
            // older the more compressed, except what they say about the entities that come back (see ContextManager).
            Knowledge.RenderedContext? context = null;
            try
            {
                if (session.DialogContextGet(dialog.SessionId) is { turns.Count: > 0 } kept)
                {
                    var question = Knowledge.ContextManager.Question(dialog.PromptUserText, bare: true);
                    var entities = await Task.Run(() => Ner.NerService.Get(config).Extract(question, types: null).entities);
                    context = Knowledge.ContextManager.Render(kept, Knowledge.ContextManager.EntityNames(entities), config, question);
                }
            }
            catch (Exception ex)
            {
                session.LogSave("Context of the conversation not read: " + ex.Message, appName, "ERROR");
            }

            var prompt = Knowledge.PromptCompressor.Compress(dialog.PromptUserText, dialog.PromptSystemText, dialog.Knowledge,
                config.promptMaxChars, config.promptKnowledgeMaxChars, context);
            dialog.PromptSentUserText = prompt.User;
            dialog.PromptSentSystemText = prompt.System;
            dialog.PromptCompression = prompt.Stats;
            session.LogSave($"Prompt: {prompt.Stats.originalChars} → {prompt.Stats.sentChars} characters ({prompt.Stats.ratio} %)"
                + (prompt.Stats.steps.Count > 0 ? " · " + string.Join(" · ", prompt.Stats.steps) : " · nothing to compress"), appName, "INFO");

            string selectedModel = req.model;
            // Through ollamaUrl: the LLM gateway when it points to it (cache, router), else the model server itself.
            OllamaChatResult response = await HelperOllamaClient.SendChatMessageAsync(
                config.ollamaUrl, selectedModel, prompt.User, prompt.System, config.llmProvider);
            session.LogSave($"Model asked at {config.ollamaUrl} ({config.llmProvider}): {selectedModel} asked, {response.Model} answered", appName, "INFO");

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

            // The model that answered (the gateway may have routed the request to another one than the model asked).
            dialog.response.model = string.IsNullOrWhiteSpace(response.Model) ? dialog.request.model : response.Model;
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