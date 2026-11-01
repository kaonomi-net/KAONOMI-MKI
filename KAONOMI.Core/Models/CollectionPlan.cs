using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models
{
    public class CollectionPlan : EntityBase, ISoftDeletable
    {
        public string name { get; set; } = "";
        public string description { get; set; } = "";
        public string project { get; set; } = "";
        public string instructions { get; set; } = "";
        public bool isActive { get; set; } = false;
        public bool isDeleted { get; set; } = false;
        public List<Website> websites { get; set; } = new List<Website>();
    }

    public class Website
    {
        public string name { get; set; } = "";
        public List<UrlProcess> urlProcesses { get; set; } = new List<UrlProcess>();

    }

    public class  UrlProcess
    {
        public string url { get; set; }
        public string mode { get; set; } // fixed, explore, random
        public List<string> keywords { get; set; } = new List<string>();
    }


}
