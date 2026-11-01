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

        /// <summary>The tags: #TOPIC, !ALERT, ?QUESTION, &amp;LINK, in upper case, no space (<see cref="TagRules"/>).</summary>
        public List<string> Tags { get; set; } = new List<string>();

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

        /// <summary>Initializes empty request, response, and interaction collections.</summary>
        public Dialog()
        { 
            PreviousInteractions = new List<PreviousInteraction>();
            request = new DialogGenericRequest();
            response = new DialogGenericResponse();
            

        }
    }
}
