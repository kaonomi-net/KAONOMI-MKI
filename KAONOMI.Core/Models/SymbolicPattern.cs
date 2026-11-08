
namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Regex-based symbolic trigger: pattern, symbol, tags, and optional event title.
    /// </summary>
    public class SymbolicPattern : EntityBase
    {
        /// <summary>Regular expression matched against user input.</summary>
        public List<string> RegexPatterns { get; set; } = new List<string>();
        /// <summary>Symbol or token emitted when the pattern matches.</summary>
        public string Symbol { get; set; }
        /// <summary>Optional event title raised on match.</summary>
        public string? Event { get; set; }
        /// <summary>Soft-delete flag; deleted patterns are ignored by consumers.</summary>
        /// 
        public string? Language { get; set; } //FR, DE,NL, ES, IT, PT, RU, ZH, JA, KO

        public bool isDeleted { get; set; } = false;
    }
}
