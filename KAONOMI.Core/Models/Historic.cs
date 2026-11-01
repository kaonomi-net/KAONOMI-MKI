using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// A single prior user prompt and model completion within a dialog session.
    /// </summary>
    public class PreviousInteraction
    {
        /// <summary>When the interaction occurred.</summary>
        public DateTime datetime { get; set; }
        /// <summary>User prompt text.</summary>
        public string prompt { get; set; }
        /// <summary>Model completion text.</summary>
        public string completion { get; set; }
    }
 }
