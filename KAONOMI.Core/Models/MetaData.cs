using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Typed key/value pair to enrich logs, performances, or completions.
    /// </summary>
    public class MetaData
    {
        /// <summary>Metadata title.</summary>
        public string Key { get; set; } = string.Empty;
        /// <summary>text value.</summary>
        public string Value_String { get; set; } = string.Empty;
        /// <summary>Integer value.</summary>
        public int Value_Int { get; set; }
        /// <summary>Floating-point value.</summary>
        public double Value_Double { get; set; }

    }
}
