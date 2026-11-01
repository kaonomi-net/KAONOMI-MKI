using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// A completion choice: index, assistant message, finish reason.
    /// </summary>
    public class Choice
    {
        /// <summary>Choice index in the list.</summary>
        public int index { get; set; }
        /// <summary>Generated message (often assistant role).</summary>
        public Message message { get; set; }
        /// <summary>Stop reason (stop, length, etc.).</summary>
        public string finish_reason { get; set; } = string.Empty;
    }
}
