using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// An entity found by the NER of the server in a dialog, kept in the search engine (index <c>detectedentity</c>), one
    /// document per entity: the text as written and normalized, its type (any type: PERSON, ORG, LOC of the model; URL,
    /// EMAIL, DATE, MONEY, CODE… of the rules; any type of the ontology), where it is in the text, how sure, what found it,
    /// the entity of the ontology it is, and the dialog and session it comes from. The id is made from the dialog, the
    /// place and the type: the same dialog processed again writes the same documents over, it never doubles them.
    /// </summary>
    public class DetectedEntity : EntityBase, ISoftDeletable
    {
        /// <summary>Builds an empty entity (for the deserialization).</summary>
        public DetectedEntity()
        {
        }

        /// <summary>Builds the entity of <paramref title="entity"/> found in the dialog <paramref title="dialogId"/>.</summary>
        public DetectedEntity(NerEntity entity, string dialogId, string? sessionId)
            : base(MakeId(dialogId, entity))
        {
            text = entity.text;
            normalizedText = Normalize(entity.text);
            type = entity.type;
            start = entity.start;
            end = entity.end;
            score = entity.score;
            source = entity.source;
            ontologyId = entity.ontologyId;
            ontologyName = entity.ontologyName;
            this.dialogId = dialogId;
            this.sessionId = sessionId;
            date = DateTime.Now;
            machineName = Environment.MachineName;
            userName = Environment.UserName;
        }

        /// <summary>The text as written in the prompt.</summary>
        public string text { get; set; } = string.Empty;

        /// <summary>The text trimmed, in lower case, spaces made single: to group the same entity written differently.</summary>
        public string normalizedText { get; set; } = string.Empty;

        /// <summary>The type, in upper case (PERSON, ORG, LOC, URL, EMAIL, DATE, MONEY, CODE…, or a type of the ontology).</summary>
        public string type { get; set; } = string.Empty;

        /// <summary>First character of the entity in the prompt.</summary>
        public int start { get; set; }

        /// <summary>Character after the last one of the entity (excluded).</summary>
        public int end { get; set; }

        /// <summary>0 to 1: the mean probability of the model, 1 for a rule or a title of the ontology.</summary>
        public double score { get; set; }

        /// <summary>"xlmr", "rule", "ontology" or "xlmr+ontology".</summary>
        public string source { get; set; } = string.Empty;

        /// <summary>The id of the entity of the ontology it is, or null.</summary>
        public string? ontologyId { get; set; }

        /// <summary>The title of that entity of the ontology, or null.</summary>
        public string? ontologyName { get; set; }

        /// <summary>The dialog the entity was found in.</summary>
        public string dialogId { get; set; } = string.Empty;

        /// <summary>The session of that dialog.</summary>
        public string? sessionId { get; set; }

        /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
        public bool isDeleted { get; set; }

        /// <summary>The text trimmed, in lower case, spaces made single.</summary>
        public static string Normalize(string? value) =>
            string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

        /// <summary>The id of an entity: the same dialog, place and type give the same id.</summary>
        private static string MakeId(string dialogId, NerEntity entity)
        {
            var key = $"{dialogId}|{entity.start}|{entity.end}|{entity.type}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        }
    }
}
