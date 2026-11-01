namespace KAONOMI.Server.Models
{
    /// <summary>
    /// Represents a cached prompt/completion pair and associated entity metadata for dialog reuse.
    /// </summary>
    public class dialogCache
    {
        /// <summary>User or system prompt text stored in the cache entry.</summary>
        public string prompt { get; set; } = string.Empty;

        /// <summary>Model completion text paired with the prompt.</summary>
        public string completion { get; set; } = string.Empty;

        /// <summary>LLM or source identifier for this cache entry.</summary>
        public string model { get; set; } = "user";

        /// <summary>Entity names linked to this cached interaction.</summary>
        public List<string>? relatedEntities { get; set; } = new List<string>();

        /// <summary>Entity names explicitly excluded from cache matching.</summary>
        public List<string>? ExcludedEntities { get; set; } = new List<string>();

        /// <summary>When true, the entry is treated as removed without physical deletion.</summary>
        public bool isDeleted { get; set; } = false;
    }
}
