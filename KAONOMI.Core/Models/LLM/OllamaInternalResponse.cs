using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// Raw response from Ollama's <c>/api/chat</c> endpoint (durations and evaluation counters).
    /// </summary>
    public class OllamaInternalResponse
    {
        /// <summary>Model that processed the request.</summary>
        [JsonPropertyName("model")] public string Model { get; set; }
        /// <summary>Parsed assistant message.</summary>
        [JsonPropertyName("message")] public InternalMessage Message { get; set; }
        /// <summary>Total duration in nanoseconds.</summary>
        [JsonPropertyName("total_duration")] public long TotalDuration { get; set; }
        /// <summary>Number of tokens evaluated for the prompt.</summary>
        [JsonPropertyName("prompt_eval_count")] public int PromptEvalCount { get; set; }
        /// <summary>Number of tokens generated in the output.</summary>
        [JsonPropertyName("eval_count")] public int EvalCount { get; set; }
        /// <summary>Output evaluation duration in nanoseconds.</summary>
        [JsonPropertyName("eval_duration")] public long EvalDuration { get; set; }
    }
}
