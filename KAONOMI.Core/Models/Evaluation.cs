using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models
{
    public class Evaluation : EntityBase
    {
        public string prompt { get; set; } = string.Empty;
        public string completion { get; set; } = string.Empty;
        public string? explaination { get; set; }
        public bool isNegativeFeedback { get; set; } = false;
    }
}
