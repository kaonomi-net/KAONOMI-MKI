namespace KAONOMI.Core.Helpers
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using KAONOMI.Core.Models.LLM;

    /// <summary>
    /// Minimal HTTP client for the Ollama chat API (non-streamed POST, metrics aggregation).
    /// </summary>
    public static class HelperOllamaClient
    {
        /// <summary>Client reused for all calls (connection pool).</summary>
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10) // <-- Sets the global timeout for the instance
        };

        /// <summary>
        /// Sends a request to Ollama and returns the text along with usage statistics.
        /// </summary>
        public static Task<OllamaChatResult> SendChatMessageAsync(string baseUrl, string modelName, string userPrompt) =>
            SendChatMessageAsync(baseUrl, modelName, userPrompt, systemPrompt: null);

        /// <summary>
        /// Sends a request to Ollama, preceded by <paramref name="systemPrompt"/> as the system message when it is not
        /// empty, and returns the text along with usage statistics.
        /// </summary>
        public static async Task<OllamaChatResult> SendChatMessageAsync(string baseUrl, string modelName, string userPrompt, string? systemPrompt)
        {
            var endpoint = $"{baseUrl.TrimEnd('/')}/api/chat";

            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
            messages.Add(new { role = "user", content = userPrompt });

            var requestBody = new
            {
                model = modelName,
                messages,
                stream = false
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync(endpoint, requestBody);
                response.EnsureSuccessStatusCode();

                var rawResult = await response.Content.ReadFromJsonAsync<OllamaInternalResponse>();

                if (rawResult == null) return new OllamaChatResult { Content = "Error: Empty response." };

                // Compute throughput (tokens per second)
                double tokensPerSecond = rawResult.EvalDuration > 0
                    ? (double)rawResult.EvalCount / (rawResult.EvalDuration / 1_000_000_000.0)
                    : 0;

                return new OllamaChatResult
                {
                    Content = rawResult.Message?.Content ?? "",
                    Model = rawResult.Model,
                    TotalTokens = rawResult.PromptEvalCount + rawResult.EvalCount,
                    PromptTokens = rawResult.PromptEvalCount,
                    ResponseTokens = rawResult.EvalCount,
                    TokensPerSecond = Math.Round(tokensPerSecond, 2),
                    TotalDurationSec = Math.Round(rawResult.TotalDuration / 1_000_000_000.0, 2)
                };
            }
            catch (Exception ex)
            {
                return new OllamaChatResult { Content = $"[Kaonomi Error] : {ex.Message}" };
            }
        }
    }

 



 
}