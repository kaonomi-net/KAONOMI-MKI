using System.Collections.Generic;
﻿namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// Cached LLM prompt/completion pair for reuse and related-entity tracking.
    /// </summary>
    public class CacheBuilder : EntityBase, ISoftDeletable, ITagged
    {

        /// <summary>Original prompt text used as the cache key.</summary>
        public List<string> prompts { get; set; } = new List<string>();
        /// <summary>Stored model response.</summary>
        public List<string> completions { get; set; } = new List<string>();
        /// <summary>Model title that produced the completion.</summary>
        public string model { get; set; } = "user";
        /// <summary>The language of the prompts and of the completions: FR, EN, DE, NL, ES, IT, PT, RU, ZH, JA, KO… (null: not said).</summary>
        public string? language { get; set; }
        /// <summary>Entity ids linked to this cache entry.</summary>
        public List<string>? relatedEntities { get; set; } = new List<string>();
        /// <summary>Entity ids explicitly excluded from this cache scope.</summary>
        public List<string>? ExcludedEntities { get; set; } = new List<string>();
        /// <summary>
        /// The ids of the documents of the index <c>cache</c> written by the last publication (one per prompt and
        /// completion pair); kept by the server, a client that does not send them does not erase them.
        /// </summary>
        public List<string>? publishedCacheIds { get; set; }
        /// <summary>Soft-delete flag.</summary>
        public bool isDeleted { get; set; } = false;
    }
}
