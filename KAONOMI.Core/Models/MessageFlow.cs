using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models
{
    public class MessageFlow : EntityBase
    {
        public string text { get; set; }
        public int priority { get; set; } = 0; // 0 = low, 1 = medium, 2 = high
        public string color { get; set; } = "white"; // default color }
        public string url { get; set; } = ""; // optional URL for more information}

    }
}
