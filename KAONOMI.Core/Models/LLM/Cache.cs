using System.Collections.Generic;
﻿namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// Cached LLM prompt/completion pair for reuse and related-entity tracking.
    /// </summary>
    public class Cache : EntityBase, ISoftDeletable, ITagged
    {
        /// <summary>The tags: #TOPIC, !ALERT, ?QUESTION, &amp;LINK, in upper case, no space (<see cref="TagRules"/>).</summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>Original prompt text used as the cache key.</summary>
        public string prompt { get; set; } = string.Empty;
        /// <summary>Stored model response.</summary>
        public string completion { get; set; } = string.Empty;
        /// <summary>Model name that produced the completion.</summary>
        public string model { get; set; } = "user";
        /// <summary>Entity ids linked to this cache entry.</summary>
        public List<string>? relatedEntities { get; set; } = new List<string>();
        /// <summary>Entity ids explicitly excluded from this cache scope.</summary>
        public List<string>? ExcludedEntities { get; set; } = new List<string>();
        /// <summary>Soft-delete flag.</summary>
        public bool isDeleted { get; set; } = false;
    }
}
