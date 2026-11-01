using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// Chat-completion style response (identifiers, choices, token usage).
    /// </summary>
    public class DialogGenericResponse
    {
        /// <summary>Completion identifier.</summary>
        public string? id { get; set; }
        /// <summary>Object type returned by the API.</summary>
        public string? @object { get; set; } = "chat.completion";
        /// <summary>Creation timestamp (epoch or provider-specific).</summary>
        public int? created { get; set; }
        /// <summary>Model that produced the response.</summary>
        public string? model { get; set; } = string.Empty;
        /// <summary>Optional system fingerprint.</summary>
        public string? system_fingerprint { get; set; }
        /// <summary>Response alternatives (often a single entry).</summary>
        public List<Choice>? choices { get; set; } = new List<Choice>();
        /// <summary>Consumed token counts.</summary>
        public Usage? usage { get; set; } = new Usage();
    }
}
