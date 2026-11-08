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
        /// <param title="logger">Logger for this grain type.</param>
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
            // Every type: the model (PERSON, ORG, LOC), the rules (URL, EMAIL, DATE, MONEY, CODE…) and the ontology.
            var result = await Task.Run(() => Ner.NerService.Get(config).Extract(prompt, types: null));
            dialog.Entities = result.entities;
            foreach (var warning in result.warnings)
                session.LogSave(warning, appName, "WARNING");
            session.LogSave($"{result.entities.Count} entities in {result.durationMs} ms: " +
                string.Join(", ", result.entities.Take(20).Select(e => $"{e.type}:{e.text}")), appName, "INFO");

            await StoreEntitiesAsync(dialog, result.entities, session, appName);
            await StoreTagsAsync(dialog, result.entities, session, appName);
        }

        /// <summary>
        /// Keeps the tags of the entities found that are entities of the ontology (their tags in the ontology: #IA,
        /// #PERSON…) in the search engine (index <c>detectedtag</c>), one document per tag and entity of the ontology,
        /// with the forms written in the prompt. The entities the ontology does not know have no tags. A failure is
        /// logged and does not stop the sequence.
        /// </summary>
        private static async Task StoreTagsAsync(Dialog dialog, IReadOnlyCollection<NerEntity> entities, SessionManager session, string appName)
        {
            var known = entities.Where(e => !string.IsNullOrWhiteSpace(e.ontologyId)).GroupBy(e => e.ontologyId!).ToList();
            if (known.Count == 0)
                return;
            try
            {
                var tags = new List<DetectedTag>();
                foreach (var group in known)
                {
                    var node = await Task.Run(() => session.OntologyGet(group.Key));
                    if (node is null || node.isDeleted)
                        continue;
                    var name = node.name ?? group.First().ontologyName ?? group.First().text;
                    tags.AddRange(TagRules.NormalizeTags(node.Tags).Select(tag =>
                        new DetectedTag(tag, dialog.id, dialog.SessionId, DetectedTag.OriginEntity, group.Key, name)
                        {
                            appName = appName,
                            matchedTexts = group.Select(e => e.text).Distinct().ToList()
                        }));
                }
                if (tags.Count == 0)
                    return;
                var ret = await Task.Run(() => session.DetectedTagsSave(tags));
                session.LogSave($"{ret.Message} ({string.Join(", ", tags.Select(t => t.tag).Distinct().Take(20))})", appName,
                    ret.ReturnedId == tags.Count.ToString() ? "INFO" : "WARNING");
            }
            catch (Exception ex)
            {
                session.LogSave($"Error saving the detected tags of dialog '{dialog.id}': {ex.Message}", appName, "ERROR");
            }
        }

        /// <summary>
        /// Keeps the entities found in the search engine (index <c>detectedentity</c>), one document each, linked to the
        /// dialog and its session. A failure is logged and does not stop the sequence: the entities stay in the dialog.
        /// </summary>
        private static async Task StoreEntitiesAsync(Dialog dialog, IReadOnlyCollection<NerEntity> entities, SessionManager session, string appName)
        {
            if (entities.Count == 0)
                return;
            try
            {
                var detected = entities.Select(e => new DetectedEntity(e, dialog.id, dialog.SessionId) { appName = appName }).ToList();
                var ret = await Task.Run(() => session.DetectedEntitiesSave(detected));
                var level = ret.ReturnedId == detected.Count.ToString() ? "INFO" : "WARNING";
                var types = string.Join(", ", detected.GroupBy(d => d.type).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"));
                session.LogSave($"{ret.Message} ({types})", appName, level);
            }
            catch (Exception ex)
            {
                session.LogSave($"Error saving the detected entities of dialog '{dialog.id}': {ex.Message}", appName, "ERROR");
            }
        }
    }
}
