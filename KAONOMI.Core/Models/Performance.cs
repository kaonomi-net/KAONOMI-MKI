namespace KAONOMI.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Performance measurement for an operation (durations, operation title, metadata).
    /// </summary>
    public class Performance : EntityBase
    {
        /// <summary>Logical title of the measured operation.</summary>
        public string operationName { get; set; } = string.Empty;
        /// <summary>Start time.</summary>
        public DateTime startTime { get; set; }
        /// <summary>End time.</summary>
        public DateTime endTime { get; set; }
        /// <summary>Total duration in milliseconds.</summary>
        public int durationMilliseconds { get; set; }
        /// <summary>Contextual details (sizes, model, etc.).</summary>
        public List<MetaData> metadatas { get; set; } = new List<MetaData>();

    }
}
