using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models
{
    public class WhiteNote : EntityBase
    {
        private static readonly Random _random = new();

        public string text { get; set; } = string.Empty;
        public string context { get; set; } = string.Empty;


        public bool isDeleted { get; set; } = false;

        public WhiteNote()
        {
            
            date = DateTime.UtcNow;
        }
    }
}
