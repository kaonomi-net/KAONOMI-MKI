using System;
using System.Collections.Generic;

namespace KAONOMI.Core.Models
{
    /// <summary>The search of <c>POST /search</c>: a term looked for in several indices at once (fuzzy).</summary>
    public class MultiSearchCriterias
    {
        /// <summary>The words looked for (a typo or the start of a word is enough).</summary>
        public string? term { get; set; } = string.Empty;
        /// <summary>The indices searched (cache, completion, dialog…); every searchable index when empty.</summary>
        public List<string> indices { get; set; } = new List<string>();
        /// <summary>Hits per index at most.</summary>
        public int size { get; set; } = 10;
        /// <summary><c>POST /recent</c>: the documents dated within these minutes.</summary>
        public int minutes { get; set; } = 60;
    }

    /// <summary>A document found by <c>POST /search</c>: where it is, what it is, and its source.</summary>
    public class SearchHit
    {
        public string index { get; set; } = string.Empty;
        public string id { get; set; } = string.Empty;
        public double score { get; set; }
        public DateTime? date { get; set; }
        public string appName { get; set; } = string.Empty;
        /// <summary>The title, name, prompt or question of the document, whichever it has.</summary>
        public string title { get; set; } = string.Empty;
        /// <summary>A piece of its text around the first word found.</summary>
        public string snippet { get; set; } = string.Empty;
        /// <summary>The text of the document (its text fields), 4,000 characters at most.</summary>
        public string text { get; set; } = string.Empty;
        /// <summary>The document itself, as JSON.</summary>
        public string source { get; set; } = "{}";
    }
}
