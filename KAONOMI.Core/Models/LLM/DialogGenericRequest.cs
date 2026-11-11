using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Models.LLM
{
    /// <summary>
    /// Generic chat request body (model, messages, stream mode).
    /// </summary>
    public class DialogGenericRequest
    {
        /// <summary>Target model title.</summary>
        public string? model { get; set; }
        /// <summary>History or user message.</summary>
        public List<Message>? messages { get; set; }
        /// <summary>If true, streaming response (depending on the called API).</summary>
        public bool stream { get; set; } = false;
        /// <summary>
        /// The id of the conversation the request belongs to (the chat gives one per conversation). With it, the server
        /// keeps the context of the conversation and writes it before the question itself (see <c>DialogContext</c>);
        /// without it, the request is answered alone, with what its messages hold.
        /// </summary>
        public string? sessionId { get; set; }
        /// <summary>
        /// True when the request asks again the last question of the conversation (its answer was disliked): the new
        /// answer replaces that exchange in the context. Without it, the same question asked twice (« continue »,
        /// « oui ») is one more exchange.
        /// </summary>
        public bool regenerate { get; set; } = false;
    }
}
