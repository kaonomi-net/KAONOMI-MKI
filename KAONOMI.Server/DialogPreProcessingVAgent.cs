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
    /// Implements pre-processing steps for inbound dialog requests.
    /// </summary>
    internal class DialogPreProcessingVAgent : VGrainBase<IDialogPreProcessingVAgent>, IDialogPreProcessingVAgent
    {
        /// <summary>Creates the agent with MARI logging support.</summary>
        /// <param title="logger">Logger for this grain type.</param>
        public DialogPreProcessingVAgent(ILogger<IDialogPreProcessingVAgent> logger) : base(logger)
        {
        }

        /// <inheritdoc />
        public async Task<string> DoProcess(string idDialog, IExecutionContext Context)
        {
            Config config = HelperConfigManager.GetFromFile<Config>("config.json");
            string appName = config.appName + "_PreProcessingVAgent";

            var session = new SessionManager(config);
            session.LogSave("Agent Called", appName, "INFO");

            Performance perf = new Performance();
            perf.date = DateTime.Now;
            perf.comment = "Pre-processing DoProcess execution time.";
            perf.machineName = Environment.MachineName;
            perf.userName = Environment.UserName;
            perf.appName = appName;
            perf.operationName = "DialogPreProcessingVAgent.DoProcess";
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
                    throw; // Stop execution to avoid pre-processing on a null object
                }

                // Run pre-processing
                await RunPreProcessingAsync(dialog, session, appName, config);

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

        private async Task RunPreProcessingAsync(Dialog dialog, SessionManager session, string appName, Config config)
        {
            session.LogSave("Running Pre-Processing", appName, "INFO");

            // The intels applied to the prompt of the user (read from the request: PromptUserText is only filled by the
            // processor, later in the sequence).
            var prompt = string.IsNullOrWhiteSpace(dialog.PromptUserText) && dialog.request is not null
                ? HelperLlmParsersAndTools.GetUserPromptFromRequest(dialog.request)
                : dialog.PromptUserText;
            var result = await Task.Run(() => Intels.IntelMatcher.Get(config).Match(prompt));
            foreach (var warning in result.warnings)
                session.LogSave(warning, appName, "WARNING");
            session.LogSave($"{result.matches.Count} intel(s) of {result.intels} matched: " +
                string.Join(", ", result.matches.Select(m => $"{m.Intel.title} {m.Score} % (≥ {m.Intel.TriggerLevel} %)")) +
                (result.notMatched.Count > 0 ? " · not matched: " + string.Join("; ", result.notMatched.Take(20)) : string.Empty), appName, "INFO");
            if (result.matches.Count == 0)
                return;

            // The signals and events of the intels that matched are kept with the dialog for the agents that follow.
            dialog.Signals = TagRules.NormalizeSignals((dialog.Signals ?? []).Concat(result.matches.SelectMany(m => m.Intel.Signals ?? [])));
            dialog.Events = TagRules.NormalizeEvents((dialog.Events ?? []).Concat(result.matches.SelectMany(m => m.Intel.Events ?? [])));

            await StoreMatchesAsync(dialog, result, session, appName);
        }

        /// <summary>
        /// Keeps each intel that matched (index <c>detectedintel</c>: its score, the entities found and missing) and its
        /// tags, events and signals (indices <c>detectedtag</c>, <c>detectedevent</c>, <c>detectedsignal</c>, origin
        /// <c>intel</c>), each with the words of the prompt that made it match. A failure is logged and does not stop
        /// the sequence.
        /// </summary>
        private static async Task StoreMatchesAsync(Dialog dialog, Intels.IntelMatchResult result, SessionManager session, string appName)
        {
            try
            {
                var intels = new List<DetectedIntel>();
                var tags = new List<DetectedTag>();
                var events = new List<DetectedEvent>();
                var signals = new List<DetectedSignal>();
                foreach (var match in result.matches)
                {
                    var intel = match.Intel;
                    var title = string.IsNullOrWhiteSpace(intel.title) ? intel.id : intel.title;
                    var texts = match.Found.Select(f => f.text!).Distinct().ToList();
                    intels.Add(new DetectedIntel(intel, dialog.id, dialog.SessionId, match.Score, match.Found, match.Missing) { appName = appName });
                    tags.AddRange(TagRules.NormalizeTags(intel.Tags).Select(tag =>
                        new DetectedTag(tag, dialog.id, dialog.SessionId, DetectedTag.OriginIntel, intel.id, title) { appName = appName, matchedTexts = texts }));
                    events.AddRange(TagRules.NormalizeEvents(intel.Events).Select(e =>
                        new DetectedEvent(e, dialog.id, dialog.SessionId, DetectedTag.OriginIntel, intel.id, title) { appName = appName, matchedTexts = texts }));
                    signals.AddRange(TagRules.NormalizeSignals(intel.Signals).Select(s =>
                        new DetectedSignal(s, dialog.id, dialog.SessionId, DetectedTag.OriginIntel, intel.id, title) { appName = appName, matchedTexts = texts }));
                }
                foreach (var ret in new[]
                {
                    await Task.Run(() => session.DetectionsSave(intels)),
                    await Task.Run(() => session.DetectionsSave(tags)),
                    await Task.Run(() => session.DetectionsSave(events)),
                    await Task.Run(() => session.DetectionsSave(signals))
                })
                    session.LogSave(ret.Message, appName, ret.Message.Contains("failed") ? "WARNING" : "INFO");
            }
            catch (Exception ex)
            {
                session.LogSave($"Error saving the intels matched in dialog '{dialog.id}': {ex.Message}", appName, "ERROR");
            }
        }
    }
}
