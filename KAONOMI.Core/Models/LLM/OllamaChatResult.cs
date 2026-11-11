namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// Normalized result of an Ollama chat call: text, model, and performance metrics.
    /// </summary>
    public class OllamaChatResult
    {
        /// <summary>Displayable response content; when <see cref="Error"/> is set, the message of the failure.</summary>
        public string Content { get; set; }
        /// <summary>
        /// Why the model gave no answer (it was not reached, it refused the request, its answer could not be read);
        /// null when it answered. <see cref="Content"/> then holds a message to show, never an answer of the model.
        /// </summary>
        public string? Error { get; set; }
        /// <summary>Model used.</summary>
        public string Model { get; set; }
        /// <summary>Total tokens (prompt + response).</summary>
        public int TotalTokens { get; set; }
        /// <summary>Prompt-side tokens.</summary>
        public int PromptTokens { get; set; }
        /// <summary>Response-side tokens.</summary>
        public int ResponseTokens { get; set; }
        /// <summary>Approximate throughput in tokens per second.</summary>
        public double TokensPerSecond { get; set; }
        /// <summary>Total processing duration in seconds.</summary>
        public double TotalDurationSec { get; set; }
    }
}
