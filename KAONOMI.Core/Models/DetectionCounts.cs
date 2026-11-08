using System;
using System.Collections.Generic;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// A symbol found by the symbolic patterns and how many times (<c>GET /detectedsymbol/top</c>): the same symbol with
    /// the same signals is one symbol; its count is the number of places it matched.
    /// </summary>
    public class DetectedSymbolCount
    {
        public string symbol { get; set; } = string.Empty;

        /// <summary>The signals of the pattern (!SAD, !HAPPY, !FEAR, !DISGUST…).</summary>
        public List<string> signals { get; set; } = new List<string>();

        /// <summary>How many times it matched.</summary>
        public int count { get; set; }

        /// <summary>In how many dialogs.</summary>
        public int dialogs { get; set; }

        public DateTime lastSeen { get; set; }
    }

    /// <summary>A tag found through the detections and how many times (<c>GET /detectedtag/top</c>).</summary>
    public class DetectedTagCount
    {
        public string tag { get; set; } = string.Empty;

        /// <summary>How many detections gave it (patterns, entities of the ontology, tags written).</summary>
        public int count { get; set; }

        /// <summary>In how many dialogs.</summary>
        public int dialogs { get; set; }

        /// <summary>What gave it: symbolicpattern, entity, tag.</summary>
        public List<string> origins { get; set; } = new List<string>();

        public DateTime lastSeen { get; set; }
    }
}
