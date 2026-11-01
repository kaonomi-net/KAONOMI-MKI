namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Connection settings for an Elasticsearch cluster (URL, index, authentication).
    /// </summary>
    public class ElasticServerSettings
    {
        /// <summary>Node or cluster URL.</summary>
        public string? url { get; set; } = "http://localhost:9200";
        /// <summary>Logical layer or environment.</summary>
        public string? layer { get; set; } = "Default";
        /// <summary>Index name prefix.</summary>
        public string? prefix { get; set; } = "";
        /// <summary>Password for basic authentication (with user).</summary>
        public string? password { get; set; }
        /// <summary>Username for basic authentication.</summary>
        public string? user { get; set; }
    }
}
