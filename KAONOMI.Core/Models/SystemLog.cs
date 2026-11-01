using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models
{
    public class SystemLog : EntityBase
    {
        
        public string systemComponent { get; set; } // Model of the processor, Model of the GPU,
        public string category { get; set; } // CPU or GPU or RAM or VRAM or Harddisk or OS 
        public int valueCurrentInPourcent { get; set; } // Current value of the component (in MB for RAM, VRAM, Harddisk, in °C for CPU and GPU)
        public int? memoryMaxCapacityInMB { get; set; } // Max capacity of the component (in MB for RAM, VRAM, Harddisk)

    }
}
