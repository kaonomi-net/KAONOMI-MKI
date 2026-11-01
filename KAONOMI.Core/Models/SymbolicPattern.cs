
namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Regex-based symbolic trigger: pattern, symbol, tags, and optional event name.
    /// </summary>
    public class SymbolicPattern : EntityBase
    {
        /// <summary>Regular expression matched against user input.</summary>
        public List<string> RegexPatterns { get; set; } = new List<string>();
        /// <summary>Symbol or token emitted when the pattern matches.</summary>
        public string Symbol { get; set; }
        /// <summary>Optional classification tags.</summary>
        public List<string> Tags { get; set; }
        /// <summary>Optional event name raised on match.</summary>
        public string? Event { get; set; }
        /// <summary>Soft-delete flag; deleted patterns are ignored by consumers.</summary>
        public bool isDeleted { get; set; } = false;
    }
}
