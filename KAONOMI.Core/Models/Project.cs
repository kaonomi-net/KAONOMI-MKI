using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// A project (/project in the chat; index "project"): a name, a description and goals. Its tags are the ones of
    /// <see cref="EntityBase"/> (<c>Tags</c>, "tags" in JSON): a second "tags" property here has the same JSON name and
    /// makes the serializer refuse the class, and with it every route of the server.
    /// </summary>
    public class Project : EntityBase, ISoftDeletable
    {
        public string name { get; set; } = string.Empty;
        public string? description { get; set; }
        public bool isDeleted { get; set; } = false;
        public List<string> goals { get; set; } = new List<string>();

        /// <summary>The day the project starts: today by default.</summary>
        public DateTime startDate { get; set; } = DateTime.Today;

        /// <summary>The day the project ends; null while it has no end.</summary>
        public DateTime? endDate { get; set; }
    
    }
}
