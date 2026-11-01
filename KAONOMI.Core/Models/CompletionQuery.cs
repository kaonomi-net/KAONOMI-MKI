using System;
using System.Collections.Generic;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// The advanced search of the completions (<c>POST /completion/query</c>, the window /completion of the chat): a text
    /// (and, or, ( ), "exact words"; empty: all) in the prompt, the completion or both, a period, exact values of the
    /// model, the language, the topic and the collection (several values of a field: any of them), the order, the size.
    /// </summary>
    public class CompletionQuery
    {
        public string? term { get; set; } = string.Empty;
        /// <summary>Where the text is searched: "all" (prompt and completion), "prompt" or "completion".</summary>
        public string? searchIn { get; set; } = "all";
        /// <summary>The first date (included), null: no start.</summary>
        public DateTime? from { get; set; }
        /// <summary>The last date (included), null: no end.</summary>
        public DateTime? to { get; set; }
        public List<string> models { get; set; } = new List<string>();
        public List<string> languages { get; set; } = new List<string>();
        public List<string> topics { get; set; } = new List<string>();
        public List<string> collections { get; set; } = new List<string>();
        /// <summary>The order: "newest" (the default), "oldest", or "relevance" (with a text).</summary>
        public string? sort { get; set; } = "newest";
        public int size { get; set; } = 200;
    }
}
