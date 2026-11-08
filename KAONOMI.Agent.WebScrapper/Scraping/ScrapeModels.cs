using System.Text.Json;
using KAONOMI.Core.Models;

namespace KAONOMI.Agent.WebScrapper.Scraping
{
    /// <summary>
    /// What the sequence receives: URLs given on the command line, or a collection plan (its id), or nothing (the active
    /// collection plans).
    /// </summary>
    public sealed class ScrapeRequest
    {
        public List<string> urls { get; set; } = new();

        /// <summary>The id of one collection plan to visit now, active or not (--plan, the Launch agent button of the chat).</summary>
        public string planId { get; set; } = string.Empty;

        /// <summary>fixed, explore or random, for the URLs of the command line.</summary>
        public string mode { get; set; } = "fixed";

        public List<string> keywords { get; set; } = new();
    }

    /// <summary>A URL of a collection plan to visit (the Collection agent gives them to the Analysis agent).</summary>
    public sealed class ScrapeJob
    {
        public string url { get; set; } = string.Empty;

        /// <summary>fixed: the page only; explore: the page and the links of the same site; random: the page and some of its links.</summary>
        public string mode { get; set; } = "fixed";

        public List<string> keywords { get; set; } = new();

        public string collectionPlanId { get; set; } = string.Empty;

        public string collectionPlanName { get; set; } = string.Empty;

        public string websiteName { get; set; } = string.Empty;
    }

    /// <summary>A visited page and its paragraphs (the Analysis agent gives them to the Actions agent, which saves them).</summary>
    public sealed class PageResult
    {
        public WebPage page { get; set; } = new();

        public List<WebParagraph> paragraphs { get; set; } = new();
    }

    /// <summary>The JSON passed from one agent of the sequence to the next.</summary>
    public static class SequenceJson
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

        public static T Read<T>(string? json) where T : new()
        {
            if (string.IsNullOrWhiteSpace(json))
                return new T();
            try
            {
                return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
            }
            catch (JsonException)
            {
                return new T();
            }
        }
    }
}
