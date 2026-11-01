using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KAONOMI.Core.Models;

namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// Persisted token counts for a single LLM call (prompt, completion, model).
    /// </summary>
    public class TokensUsage : EntityBase
    {
        /// <summary>Tokens consumed by the prompt.</summary>
        public int prompt_tokens { get; set; }
        
        /// <summary>Tokens generated in the completion.</summary>
        public int completion_tokens { get; set; }
        /// <summary>Model that reported the usage.</summary>
        public string model { get; set; }
    }
}
