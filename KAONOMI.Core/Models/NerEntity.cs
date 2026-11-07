namespace KAONOMI.Core.Models
{
    /// <summary>
    /// An entity found in a text by the NER of the server (<c>POST /ner</c>, and the dialogs): the text as written, its
    /// type (PERSON, ORG, LOC from the XLM-RoBERTa model; URL, EMAIL, DATE, MONEY, CODE… from the rules; any type of the
    /// ontology), where it is (characters <see cref="start"/> to <see cref="end"/>, end excluded), how sure, what found
    /// it, and the entity of the ontology it is when it is known there.
    /// </summary>
    public class NerEntity
    {
        public string text { get; set; } = string.Empty;

        public string type { get; set; } = string.Empty;

        public int start { get; set; }

        public int end { get; set; }

        /// <summary>0 to 1: the mean probability of the model, 1 for a rule or a name of the ontology.</summary>
        public double score { get; set; }

        /// <summary>"xlmr" (the model), "rule", "ontology", or "xlmr+ontology" (found by the model and known in the ontology).</summary>
        public string source { get; set; } = string.Empty;

        /// <summary>The id of the entity of the ontology it is, or null.</summary>
        public string? ontologyId { get; set; }

        /// <summary>The name of that entity of the ontology (the one written may be a synonym), or null.</summary>
        public string? ontologyName { get; set; }
    }
}
