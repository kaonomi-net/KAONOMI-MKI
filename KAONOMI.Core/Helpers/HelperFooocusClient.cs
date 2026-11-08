namespace KAONOMI.Core.Helpers
{
    using System;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Text.Json;
    using System.Threading.Tasks;
    using KAONOMI.Core.Models;

    /// <summary>
    /// HTTP client of the Fooocus API (<c>KAONOMI.Server/Fooocus/fooocus_api.py</c>): its state, its options (styles,
    /// performances, aspect ratios, models) and the generation of images.
    /// </summary>
    public static class HelperFooocusClient
    {
        /// <summary>Client reused for all calls; a generation of several images in Quality can take minutes.</summary>
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        /// <summary>GET /health of the Fooocus API (version, default model, busy); throws when it does not answer.</summary>
        public static async Task<JsonElement> GetHealthAsync(string baseUrl)
        {
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _httpClient.GetFromJsonAsync<JsonElement>($"{baseUrl.TrimEnd('/')}/health", timeout.Token);
        }

        /// <summary>GET /options of the Fooocus API: what a generation accepts, and the defaults.</summary>
        public static async Task<JsonElement> GetOptionsAsync(string baseUrl) =>
            await _httpClient.GetFromJsonAsync<JsonElement>($"{baseUrl.TrimEnd('/')}/options");

        /// <summary>
        /// POST /generate of the Fooocus API: generates the images of <paramref title="request"/> and returns them; on
        /// failure, a result with <see cref="ImageGenerationResult.error"/> set and no image.
        /// </summary>
        public static async Task<ImageGenerationResult> GenerateAsync(string baseUrl, ImageGenerationRequest request)
        {
            try
            {
                // The Fooocus API names its fields in snake_case; the empty ones keep its defaults.
                var body = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["prompt"] = request.prompt,
                    ["image_number"] = request.imageNumber,
                    ["seed"] = request.seed,
                    ["output_format"] = request.outputFormat,
                    ["include_base64"] = request.includeBase64
                };
                if (request.negativePrompt is not null) body["negative_prompt"] = request.negativePrompt;
                if (request.styles is not null) body["styles"] = request.styles;
                if (request.performance is not null) body["performance"] = request.performance;
                if (request.aspectRatio is not null) body["aspect_ratio"] = request.aspectRatio;
                if (request.baseModel is not null) body["base_model"] = request.baseModel;
                if (request.guidanceScale is not null) body["guidance_scale"] = request.guidanceScale;
                var response = await _httpClient.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/generate", body);
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await response.Content.ReadAsStringAsync();
                    return new ImageGenerationResult { error = $"Fooocus API: {(int)response.StatusCode} {detail}" };
                }
                return await response.Content.ReadFromJsonAsync<ImageGenerationResult>()
                       ?? new ImageGenerationResult { error = "Fooocus API: empty answer" };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return new ImageGenerationResult { error = $"Fooocus API ({baseUrl}) not reachable: {ex.Message}" };
            }
        }
    }
}
