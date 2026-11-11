using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using KAONOMI.Core.Models.LLM;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Persisted dialog turn: session id, request/response payloads, prompts, and prior interactions.
    /// </summary>
    public  class Dialog : EntityBase, ISoftDeletable, ITagged
    {
        /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
        public bool isDeleted { get; set; } = false;


        /// <summary>Client or server session identifier grouping related turns.</summary>
        public string SessionId { get; set; }
        /// <summary>Outbound chat request sent to the LLM API.</summary>
        public DialogGenericRequest? request { get; set; }
        /// <summary>Inbound chat completion returned by the LLM API.</summary>
        public DialogGenericResponse? response { get; set; }
        /// <summary>Extracted user prompt text for indexing and search.</summary>
        public string PromptUserText { get; set; }
        /// <summary>Extracted system prompt text for indexing and search.</summary>
        public string PromptSystemText { get; set; }

        /// <summary>Earlier prompts and completions in the same session.</summary>
        public List<PreviousInteraction> PreviousInteractions { get; set; }

        /// <summary>The entities of the user prompt, found by the NER of the server (the agent of the entities of the sequence).</summary>
        public List<NerEntity> Entities { get; set; } = new List<NerEntity>();

        /// <summary>
        /// What Kaonomi knows about these entities (the agent of the entities): the entities of the ontology the prompt
        /// names and the intels that match them. The processor writes it in the prompt of the model.
        /// </summary>
        public List<DialogKnowledge> Knowledge { get; set; } = new List<DialogKnowledge>();

        /// <summary>The user prompt as it was sent to the model (its previous exchanges compressed); null before the processor.</summary>
        public string? PromptSentUserText { get; set; }

        /// <summary>The system prompt as it was sent to the model (with the knowledge kept); null before the processor.</summary>
        public string? PromptSentSystemText { get; set; }

        /// <summary>What the compression of the prompt did; null before the processor.</summary>
        public PromptCompressionStats? PromptCompression { get; set; }

        /// <summary>Initializes empty request, response, and interaction collections.</summary>
        public Dialog()
        { 
            PreviousInteractions = new List<PreviousInteraction>();
            request = new DialogGenericRequest();
            response = new DialogGenericResponse();
            

        }
    }
}
