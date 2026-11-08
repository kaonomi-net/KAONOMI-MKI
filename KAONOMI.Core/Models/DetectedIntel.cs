using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// An intel that matched a dialog, kept in the search engine (index <c>detectedintel</c>): its score (the weights of
    /// the included entities found in the prompt over the weights of all its included entities, in percent), the level
    /// it had to reach, the entities found and those missing. Its title, tags, events and signals are those of the
    /// intel. One document per intel and dialog; the id is made from them, so the same dialog analysed again writes it over.
    /// </summary>
    public class DetectedIntel : EntityBase, ISoftDeletable
    {
        /// <summary>Builds an empty detection (for the deserialization).</summary>
        public DetectedIntel()
        {
        }

        /// <summary>Builds the match of <paramref name="intel"/> in the dialog <paramref name="dialogId"/>.</summary>
        public DetectedIntel(Intel intel, string dialogId, string? sessionId, double score, List<IntelEntityMatch> found, List<IntelEntityMatch> missing)
            : base(DetectionIds.Make(dialogId, "intel", intel.id))
        {
            intelId = intel.id;
            title = string.IsNullOrWhiteSpace(intel.title) ? intel.id : intel.title;
            Tags = new List<string>(intel.Tags ?? new List<string>());
            Events = new List<string>(intel.Events ?? new List<string>());
            Signals = new List<string>(intel.Signals ?? new List<string>());
            this.score = score;
            triggerLevel = intel.TriggerLevel;
            isFuzzy = intel.isFuzzy;
            this.found = found;
            this.missing = missing;
            this.dialogId = dialogId;
            this.sessionId = sessionId;
            date = DateTime.UtcNow;
            machineName = Environment.MachineName;
            userName = Environment.UserName;
        }

        /// <summary>The intel that matched.</summary>
        public string intelId { get; set; } = string.Empty;

        /// <summary>The score, 0 to 100 %.</summary>
        public double score { get; set; }

        /// <summary>The level the score had to reach (the trigger level of the intel).</summary>
        public int triggerLevel { get; set; }

        /// <summary>True when the entities were matched loosely.</summary>
        public bool isFuzzy { get; set; }

        /// <summary>The included entities found in the prompt.</summary>
        public List<IntelEntityMatch> found { get; set; } = new List<IntelEntityMatch>();

        /// <summary>The included entities not found.</summary>
        public List<IntelEntityMatch> missing { get; set; } = new List<IntelEntityMatch>();

        public string dialogId { get; set; } = string.Empty;

        public string? sessionId { get; set; }

        /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
        public bool isDeleted { get; set; }
    }

    /// <summary>An included entity of an intel, its weight, and what was written in the prompt when it was found.</summary>
    public class IntelEntityMatch
    {
        public string entityName { get; set; } = string.Empty;

        /// <summary>The weight used in the score (1 when the intel gives none or one that is not a number).</summary>
        public double weight { get; set; }

        /// <summary>What was written in the prompt (null when not found).</summary>
        public string? text { get; set; }
    }

    /// <summary>
    /// An event found in a dialog through a detection (an intel that matched…), kept in the search engine (index
    /// <c>detectedevent</c>), one document per event, dialog and origin.
    /// </summary>
    public class DetectedEvent : EntityBase, ISoftDeletable
    {
        public DetectedEvent()
        {
        }

        public DetectedEvent(string eventName, string dialogId, string? sessionId, string origin, string originId, string originText)
            : base(DetectionIds.Make(dialogId, eventName, origin, originId))
        {
            this.eventName = eventName;
            Events = new List<string> { eventName };
            title = eventName;
            this.dialogId = dialogId;
            this.sessionId = sessionId;
            this.origin = origin;
            this.originId = originId;
            this.originText = originText;
            date = DateTime.UtcNow;
            machineName = Environment.MachineName;
            userName = Environment.UserName;
        }

        /// <summary>The event (@ALERTE…).</summary>
        public string eventName { get; set; } = string.Empty;

        public string dialogId { get; set; } = string.Empty;

        public string? sessionId { get; set; }

        /// <summary>What gave the event (intel…).</summary>
        public string origin { get; set; } = string.Empty;

        /// <summary>The id of what gave it.</summary>
        public string originId { get; set; } = string.Empty;

        /// <summary>The title of what gave it.</summary>
        public string originText { get; set; } = string.Empty;

        /// <summary>What was matched in the prompt.</summary>
        public List<string> matchedTexts { get; set; } = new List<string>();

        public bool isDeleted { get; set; }
    }

    /// <summary>
    /// A signal found in a dialog through a detection (an intel that matched…), kept in the search engine (index
    /// <c>detectedsignal</c>), one document per signal, dialog and origin.
    /// </summary>
    public class DetectedSignal : EntityBase, ISoftDeletable
    {
        public DetectedSignal()
        {
        }

        public DetectedSignal(string signal, string dialogId, string? sessionId, string origin, string originId, string originText)
            : base(DetectionIds.Make(dialogId, signal, origin, originId))
        {
            this.signal = signal;
            Signals = new List<string> { signal };
            title = signal;
            this.dialogId = dialogId;
            this.sessionId = sessionId;
            this.origin = origin;
            this.originId = originId;
            this.originText = originText;
            date = DateTime.UtcNow;
            machineName = Environment.MachineName;
            userName = Environment.UserName;
        }

        /// <summary>The signal (!VIGILANCE…).</summary>
        public string signal { get; set; } = string.Empty;

        public string dialogId { get; set; } = string.Empty;

        public string? sessionId { get; set; }

        /// <summary>What gave the signal (intel…).</summary>
        public string origin { get; set; } = string.Empty;

        public string originId { get; set; } = string.Empty;

        public string originText { get; set; } = string.Empty;

        /// <summary>What was matched in the prompt.</summary>
        public List<string> matchedTexts { get; set; } = new List<string>();

        public bool isDeleted { get; set; }
    }

    /// <summary>The stable ids of the detections: the same parts give the same id.</summary>
    public static class DetectionIds
    {
        public static string Make(params string[] parts)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
            return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        }
    }
}
