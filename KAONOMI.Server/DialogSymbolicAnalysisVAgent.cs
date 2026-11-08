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
    /// Implements symbolic pattern and rule analysis for dialogs.
    /// </summary>
    internal class DialogSymbolicAnalysisVAgent : VGrainBase<IDialogSymbolicAnalysisVAgent>, IDialogSymbolicAnalysisVAgent
    {
        /// <summary>Creates the agent with MARI logging support.</summary>
        /// <param title="logger">Logger for this grain type.</param>
        public DialogSymbolicAnalysisVAgent(ILogger<IDialogSymbolicAnalysisVAgent> logger) : base(logger)
        {
        }

        /// <inheritdoc />
        public async Task<string> DoProcess(string idDialog, IExecutionContext Context)
        {
            Config config = HelperConfigManager.GetFromFile<Config>("config.json");
            string appName = config.appName + "_SymbolicAnalysisVAgent";

            var session = new SessionManager(config);
            session.LogSave("Agent Called", appName, "INFO");

            Performance perf = new Performance();
            perf.date = DateTime.Now;
            perf.comment = "Symbolic analysis DoProcess execution time.";
            perf.machineName = Environment.MachineName;
            perf.userName = Environment.UserName;
            perf.appName = appName;
            perf.operationName = "DialogSymbolicAnalysisVAgent.DoProcess";
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
                    throw; // Stop execution to avoid symbolic analysis on a null object
                }

                // Run symbolic analysis
                await RunSymbolicAnalysisAsync(dialog, session, appName, config);

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

        private async Task RunSymbolicAnalysisAsync(Dialog dialog, SessionManager session, string appName, Config config)
        {
            session.LogSave("Running Symbolic Analysis", appName, "INFO");

            // The prompt of the user, read from the request as the entity agent does (PromptUserText is only filled by
            // the processor, later in the sequence).
            var prompt = string.IsNullOrWhiteSpace(dialog.PromptUserText) && dialog.request is not null
                ? HelperLlmParsersAndTools.GetUserPromptFromRequest(dialog.request)
                : dialog.PromptUserText;
            var result = await Task.Run(() => Symbolic.SymbolicPatternMatcher.Get(config).Match(prompt));
            foreach (var warning in result.warnings.Take(20))
                session.LogSave(warning, appName, "WARNING");
            session.LogSave($"{result.detections.Count} symbolic pattern(s) of {result.patterns} matched in {result.durationMs} ms: " +
                string.Join(", ", result.detections.Take(20).Select(d => $"{d.Pattern.Symbol} {string.Join(' ', d.Pattern.Signals ?? [])}")), appName, "INFO");
            // The tags declared in the indices symbolicpattern and ontology, found written in the prompt (#IA, « guerre »),
            // whether a pattern matched or not.
            await StoreRecognizedTagsAsync(dialog, prompt, session, appName, config);

            if (result.detections.Count == 0)
                return;

            // The signals found (!SAD, !HAPPY, !FEAR…) are kept with the dialog for the agents that follow.
            dialog.Signals = TagRules.NormalizeSignals((dialog.Signals ?? []).Concat(result.detections.SelectMany(d => d.Pattern.Signals ?? [])));

            await StoreDetectionsAsync(dialog, result, session, appName);
            await StoreTagsAsync(dialog, result, session, appName);
        }

        /// <summary>
        /// Looks in the prompt for the tags declared in the indices symbolicpattern and ontology (as a hashtag or as words,
        /// see <see cref="Symbolic.TagGazetteer"/>) and keeps those found in the search engine (index <c>detectedtag</c>,
        /// origin <c>tag</c>), with what was written. A failure is logged and does not stop the sequence.
        /// </summary>
        private static async Task StoreRecognizedTagsAsync(Dialog dialog, string? prompt, SessionManager session, string appName, Config config)
        {
            try
            {
                var (found, warnings, declared) = await Task.Run(() => Symbolic.TagGazetteer.Get(config).Find(prompt));
                foreach (var warning in warnings.Take(10))
                    session.LogSave(warning, appName, "WARNING");
                session.LogSave($"{found.Count} declared tag(s) of {declared} written in the prompt: {string.Join(", ", found.Take(20).Select(t => t.Tag))}", appName, "INFO");
                if (found.Count == 0)
                    return;
                var tags = found
                    .Select(t => new DetectedTag(t.Tag, dialog.id, dialog.SessionId, DetectedTag.OriginTag, t.Source, t.Tag)
                    {
                        appName = appName,
                        matchedTexts = t.MatchedTexts
                    })
                    .ToList();
                var ret = await Task.Run(() => session.DetectedTagsSave(tags));
                session.LogSave(ret.Message, appName, ret.ReturnedId == tags.Count.ToString() ? "INFO" : "WARNING");
            }
            catch (Exception ex)
            {
                session.LogSave($"Error looking for the declared tags in dialog '{dialog.id}': {ex.Message}", appName, "ERROR");
            }
        }

        /// <summary>
        /// Keeps the tags of the patterns found (#MORT, #DEUIL…) in the search engine (index <c>detectedtag</c>), one
        /// document per tag and pattern, with the words that matched. A failure is logged and does not stop the sequence.
        /// </summary>
        private static async Task StoreTagsAsync(Dialog dialog, Symbolic.SymbolicMatchResult result, SessionManager session, string appName)
        {
            try
            {
                var tags = result.detections
                    .SelectMany(d => TagRules.NormalizeTags(d.Pattern.Tags).Select(tag =>
                        new DetectedTag(tag, dialog.id, dialog.SessionId, DetectedTag.OriginSymbolicPattern, d.Pattern.id, d.Pattern.Symbol ?? string.Empty)
                        {
                            appName = appName,
                            Signals = new List<string>(d.Pattern.Signals ?? []),
                            matchedTexts = d.Matches.Select(m => m.text).Distinct().ToList()
                        }))
                    .ToList();
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
        /// Keeps the detections in the search engine (index <c>detectedsymbol</c>), one document per pattern found, linked
        /// to the dialog and its session. A failure is logged and does not stop the sequence.
        /// </summary>
        private static async Task StoreDetectionsAsync(Dialog dialog, Symbolic.SymbolicMatchResult result, SessionManager session, string appName)
        {
            try
            {
                var detections = result.detections
                    .Select(d => new DetectedSymbol(d.Pattern, dialog.id, dialog.SessionId, d.Matches) { appName = appName })
                    .ToList();
                var ret = await Task.Run(() => session.DetectedSymbolsSave(detections));
                var level = ret.ReturnedId == detections.Count.ToString() ? "INFO" : "WARNING";
                session.LogSave(ret.Message, appName, level);
            }
            catch (Exception ex)
            {
                session.LogSave($"Error saving the detected symbols of dialog '{dialog.id}': {ex.Message}", appName, "ERROR");
            }
        }
    }
}
