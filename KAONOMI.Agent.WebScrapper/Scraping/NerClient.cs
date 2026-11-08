using System.Net.Http.Json;
using KAONOMI.Core.Models;

namespace KAONOMI.Agent.WebScrapper.Scraping
{
    /// <summary>The NER of the Kaonomi server (<c>POST /ner</c>): the model, the rules and the names of the ontology.</summary>
    public sealed class NerClient
    {
        private readonly HttpClient _http;
        private readonly string _serverUrl;

        public NerClient(HttpClient http, string serverUrl)
        {
            _http = http;
            _serverUrl = serverUrl.TrimEnd('/');
        }

        private sealed class NerAnswer
        {
            public List<NerEntity> entities { get; set; } = new();
            public List<string> warnings { get; set; } = new();
        }

        /// <summary>The entities of <paramref name="text"/>, with the warnings of the server.</summary>
        public async Task<(List<NerEntity> Entities, List<string> Warnings)> ExtractAsync(string text, CancellationToken cancellationToken)
        {
            using var response = await _http.PostAsJsonAsync(_serverUrl + "/ner", new { text, types = (string[]?)null }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var answer = await response.Content.ReadFromJsonAsync<NerAnswer>(cancellationToken: cancellationToken) ?? new NerAnswer();
            return (answer.entities, answer.warnings);
        }
    }
}
