using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// Token usage statistics for a request/response.
    /// </summary>
    public class Usage
    {
        /// <summary>Prompt tokens.</summary>
        public int prompt_tokens { get; set; }
        /// <summary>Response tokens.</summary>
        public int completion_tokens { get; set; }
        /// <summary>Total prompt + completion.</summary>
        public int total_tokens { get; set; } = 0;
    }
}
