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
    /// Minimal HTTP client for the chat API of Ollama, or of LM Studio (OpenAI-compatible) when the provider says so
    /// (non-streamed POST, metrics aggregation).
    /// </summary>
    public static class HelperOllamaClient
    {
        /// <summary>Client reused for all calls (connection pool).</summary>
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10) // <-- Sets the global timeout for the instance
        };

        /// <summary>
        /// True when <paramref name="provider"/> (llmProvider of the configuration) names a server that speaks the
        /// OpenAI-compatible API instead of the one of Ollama: "lmstudio" (LM Studio) or "openai".
        /// </summary>
        public static bool IsOpenAiCompatible(string? provider)
        {
            var name = (provider ?? "").Trim().Replace("-", "").Replace("_", "").Replace(" ", "").ToLowerInvariant();
            return name is "lmstudio" or "openai" or "openaicompatible";
        }

        /// <summary>
        /// Sends a request to Ollama and returns the text along with usage statistics.
        /// </summary>
        public static Task<OllamaChatResult> SendChatMessageAsync(string baseUrl, string modelName, string userPrompt) =>
            SendChatMessageAsync(baseUrl, modelName, userPrompt, systemPrompt: null);

        /// <summary>
        /// Sends a request to Ollama, preceded by <paramref title="systemPrompt"/> as the system message when it is not
        /// empty, and returns the text along with usage statistics.
        /// </summary>
        public static Task<OllamaChatResult> SendChatMessageAsync(string baseUrl, string modelName, string userPrompt, string? systemPrompt) =>
            SendChatMessageAsync(baseUrl, modelName, userPrompt, systemPrompt, provider: null);

        /// <summary>
        /// Sends a request to the LLM server of <paramref name="provider"/> ("ollama", the default, or "lmstudio"),
        /// preceded by <paramref title="systemPrompt"/> as the system message when it is not empty, and returns the
        /// text along with usage statistics.
        /// </summary>
        public static async Task<OllamaChatResult> SendChatMessageAsync(string baseUrl, string modelName, string userPrompt, string? systemPrompt, string? provider)
        {
            if (IsOpenAiCompatible(provider))
                return await SendOpenAiChatMessageAsync(baseUrl, modelName, userPrompt, systemPrompt);

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

        /// <summary>
        /// The same request through the OpenAI-compatible API (<c>POST /v1/chat/completions</c>) of LM Studio: the
        /// tokens come from <c>usage</c>, the durations are measured here (that API does not give them).
        /// </summary>
        private static async Task<OllamaChatResult> SendOpenAiChatMessageAsync(string baseUrl, string modelName, string userPrompt, string? systemPrompt)
        {
            var endpoint = $"{OpenAiRoot(baseUrl)}/chat/completions";

            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
            messages.Add(new { role = "user", content = userPrompt });

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var response = await _httpClient.PostAsJsonAsync(endpoint, new { model = modelName, messages, stream = false });
                var text = await response.Content.ReadAsStringAsync();
                stopwatch.Stop();
                if (!response.IsSuccessStatusCode)
                    return new OllamaChatResult { Content = $"[Kaonomi Error] : LM Studio responded {(int)response.StatusCode} ({response.ReasonPhrase}): {text}" };

                using var document = System.Text.Json.JsonDocument.Parse(text);
                var root = document.RootElement;
                string content = "";
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == System.Text.Json.JsonValueKind.Array && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    content = value.GetString() ?? "";

                int Count(string name) =>
                    root.TryGetProperty("usage", out var usage) && usage.ValueKind == System.Text.Json.JsonValueKind.Object
                    && usage.TryGetProperty(name, out var count) && count.TryGetInt32(out var n) ? n : 0;
                int promptTokens = Count("prompt_tokens");
                int responseTokens = Count("completion_tokens");
                double seconds = stopwatch.Elapsed.TotalSeconds;

                return new OllamaChatResult
                {
                    Content = content,
                    Model = root.TryGetProperty("model", out var model) && model.ValueKind == System.Text.Json.JsonValueKind.String ? model.GetString() ?? modelName : modelName,
                    TotalTokens = promptTokens + responseTokens,
                    PromptTokens = promptTokens,
                    ResponseTokens = responseTokens,
                    // The whole call (prompt read and answer written): a little under the pace of the generation alone.
                    TokensPerSecond = seconds > 0 ? Math.Round(responseTokens / seconds, 2) : 0,
                    TotalDurationSec = Math.Round(seconds, 2)
                };
            }
            catch (Exception ex)
            {
                return new OllamaChatResult { Content = $"[Kaonomi Error] : {ex.Message}" };
            }
        }

        /// <summary>The root of the OpenAI-compatible API: <paramref name="baseUrl"/> with <c>/v1</c>, given with it or not.</summary>
        private static string OpenAiRoot(string baseUrl)
        {
            var url = baseUrl.TrimEnd('/');
            return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url : url + "/v1";
        }
    }

 



 
}