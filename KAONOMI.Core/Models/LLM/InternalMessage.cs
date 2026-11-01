using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// Message fragment as returned by the Ollama API (JSON deserialization).
    /// </summary>
    public class InternalMessage
    {
        /// <summary>Textual message content.</summary>
        [JsonPropertyName("content")] public string Content { get; set; }
    }
}
