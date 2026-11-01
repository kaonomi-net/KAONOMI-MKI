using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Simple search parameters: maximum hits and free-text term.
    /// </summary>
    public class SearchCriterias
    {
        /// <summary>Maximum number of documents to return.</summary>
        public int size { get; set; } = 10;
        /// <summary>Search term or wildcard; empty matches all when supported.</summary>
        public string? term { get; set; } = string.Empty;
        /// <summary>
        /// Loose search (<c>/cache/search</c>): a document matches when it holds at least one word of the term with a
        /// typo or as the start of a word, accents ignored. The clients ask for it when the plain search found nothing.
        /// </summary>
        public bool fuzzy { get; set; } = false;
    }
}
