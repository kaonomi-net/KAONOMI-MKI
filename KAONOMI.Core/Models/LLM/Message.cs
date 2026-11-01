using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// Conversation message (role + content).
    /// </summary>
    public class Message
    {
        /// <summary>Role: system, user, assistant, etc.</summary>
        public string role { get; set; }
        /// <summary>Message text.</summary>
        public string content { get; set; } = string.Empty;
    }
}
