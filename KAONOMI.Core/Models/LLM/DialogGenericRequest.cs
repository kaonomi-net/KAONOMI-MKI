using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// Generic chat request body (model, messages, stream mode).
    /// </summary>
    public class DialogGenericRequest
    {
        /// <summary>Target model name.</summary>
        public string? model { get; set; }
        /// <summary>History or user message.</summary>
        public List<Message>? messages { get; set; }
        /// <summary>If true, streaming response (depending on the called API).</summary>
        public bool stream { get; set; } = false;
    }
}
