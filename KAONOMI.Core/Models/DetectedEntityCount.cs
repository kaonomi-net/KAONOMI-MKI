using System;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// An entity found by the NER and how many times (<c>GET /detectedentity/top</c>): the same entity is the same entity
    /// of the ontology, or else the same normalized text of the same type. Its text is the title of the ontology when it is
    /// known there, or else the form written most often.
    /// </summary>
    public class DetectedEntityCount
    {
        /// <summary>The text shown: the title of the ontology, or the form written most often.</summary>
        public string text { get; set; } = string.Empty;

        /// <summary>The type (PERSON, ORG, LOC, URL, DATE…, or a type of the ontology).</summary>
        public string type { get; set; } = string.Empty;

        /// <summary>How many times it was found (one per place in a prompt).</summary>
        public int count { get; set; }

        /// <summary>In how many dialogs.</summary>
        public int dialogs { get; set; }

        /// <summary>The id of the entity of the ontology it is, or null.</summary>
        public string? ontologyId { get; set; }

        /// <summary>When it was found last.</summary>
        public DateTime lastSeen { get; set; }
    }
}
