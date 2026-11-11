using System.Collections.Generic;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Something Kaonomi knows about the prompt of a dialog, found by the agent of the entities and given to the model
    /// by the processor: an entity of the ontology the prompt names, or an intel that matches its entities.
    /// </summary>
    public class DialogKnowledge
    {
        public const string KindOntology = "ontology";
        public const string KindIntel = "intel";

        /// <summary>An entity of the ontology the prompt does not name, but that an entity it names lists as related: its summary only.</summary>
        public const string KindRelated = "related";

        /// <summary><see cref="KindOntology"/>, <see cref="KindIntel"/> or <see cref="KindRelated"/>.</summary>
        public string kind { get; set; } = string.Empty;

        /// <summary>The id of the entity of the ontology, or of the intel.</summary>
        public string sourceId { get; set; } = string.Empty;

        /// <summary>The name of the entity, or the title of the intel.</summary>
        public string title { get; set; } = string.Empty;

        /// <summary>What is known, as it is written in the prompt (before the compression).</summary>
        public string text { get; set; } = string.Empty;

        /// <summary>What was written in the prompt of the user that led here (the entity, the words of the intel).</summary>
        public List<string> matchedTexts { get; set; } = new List<string>();

        /// <summary>For an intel: its score in percent; 100 for an entity of the ontology.</summary>
        public double score { get; set; } = 100;
    }

    /// <summary>What the compression of the prompt of a dialog did, in characters.</summary>
    public class PromptCompressionStats
    {
        /// <summary>The system prompt and the user prompt of the request, with all the knowledge found, before the compression.</summary>
        public int originalChars { get; set; }

        /// <summary>What was sent to the model: the system prompt with the knowledge kept, and the user prompt.</summary>
        public int sentChars { get; set; }

        /// <summary>The knowledge written in the system prompt.</summary>
        public int knowledgeChars { get; set; }

        public int knowledgeItems { get; set; }

        /// <summary>The knowledge found that did not fit.</summary>
        public int knowledgeDropped { get; set; }

        /// <summary>The previous exchanges written in the prompt, and how many were kept.</summary>
        public int historyMessages { get; set; }

        public int historyKept { get; set; }

        /// <summary>sentChars over originalChars, in percent (100: nothing was taken out).</summary>
        public double ratio { get; set; } = 100;

        /// <summary>What was done, one line per step, for the logs.</summary>
        public List<string> steps { get; set; } = new List<string>();
    }
}
