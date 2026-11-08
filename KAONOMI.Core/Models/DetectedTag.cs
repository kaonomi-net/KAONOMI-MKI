using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// A tag found in a dialog through a detection, kept in the search engine (index <c>detectedtag</c>): the tags of a
    /// symbolic pattern that matched (<see cref="OriginSymbolicPattern"/>), of the entity of the ontology an entity
    /// found by the NER is (<see cref="OriginEntity"/>), or a tag declared in those indices found written in the prompt
    /// (<see cref="OriginTag"/>). One document per tag, dialog and origin; the id is made from
    /// them, so the same dialog analysed again writes the same documents over, and the two agents never overwrite each
    /// other's tags.
    /// </summary>
    public class DetectedTag : EntityBase, ISoftDeletable
    {
        public const string OriginSymbolicPattern = "symbolicpattern";
        public const string OriginEntity = "entity";
        /// <summary>A tag declared in the indices symbolicpattern or ontology, found written in the prompt (#IA, « IA »).</summary>
        public const string OriginTag = "tag";
        /// <summary>A tag of an intel that matched the prompt (see <see cref="DetectedIntel"/>).</summary>
        public const string OriginIntel = "intel";

        /// <summary>Builds an empty detection (for the deserialization).</summary>
        public DetectedTag()
        {
        }

        /// <summary>Builds the tag <paramref title="tag"/> found in the dialog <paramref title="dialogId"/> through <paramref title="originId"/>.</summary>
        public DetectedTag(string tag, string dialogId, string? sessionId, string origin, string originId, string originText)
            : base(MakeId(dialogId, tag, origin, originId))
        {
            this.tag = tag;
            Tags = new List<string> { tag };
            title = tag;
            this.dialogId = dialogId;
            this.sessionId = sessionId;
            this.origin = origin;
            this.originId = originId;
            this.originText = originText;
            date = DateTime.UtcNow;
            machineName = Environment.MachineName;
            userName = Environment.UserName;
        }

        /// <summary>The tag (#MORT, #IA…).</summary>
        public string tag { get; set; } = string.Empty;

        /// <summary>The dialog the tag was found in.</summary>
        public string dialogId { get; set; } = string.Empty;

        /// <summary>The session of that dialog.</summary>
        public string? sessionId { get; set; }

        /// <summary>What gave the tag: <see cref="OriginSymbolicPattern"/>, <see cref="OriginEntity"/> or <see cref="OriginTag"/>.</summary>
        public string origin { get; set; } = string.Empty;

        /// <summary>The id of the symbolic pattern or of the entity of the ontology; for a tag found written, the indices it is declared in (symbolicpattern, ontology, symbolicpattern+ontology).</summary>
        public string originId { get; set; } = string.Empty;

        /// <summary>The symbol of the pattern, the title of the entity, or the tag itself.</summary>
        public string originText { get; set; } = string.Empty;

        /// <summary>What was matched in the prompt (the words of the pattern, the entity as written).</summary>
        public List<string> matchedTexts { get; set; } = new List<string>();

        /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
        public bool isDeleted { get; set; }

        /// <summary>The id of a detected tag: the same dialog, tag and origin give the same id.</summary>
        private static string MakeId(string dialogId, string tag, string origin, string originId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{dialogId}|{tag}|{origin}|{originId}"));
            return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        }
    }
}
