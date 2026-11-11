using System;
using System.Collections.Generic;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// The context of a conversation (a session of the chat), kept by the server: its exchanges and the entities they
    /// talk about. The agent of the context update adds each exchange once it is answered; the processor writes the
    /// context before the next question, the older an exchange the more compressed, except what it says about the
    /// entities that come back in the conversation. Its id is the id of the session.
    /// </summary>
    public class DialogContext : EntityBase
    {
        /// <summary>The exchanges, the oldest first.</summary>
        public List<DialogContextTurn> turns { get; set; } = new List<DialogContextTurn>();

        /// <summary>The entities of the conversation and in how many exchanges each one is.</summary>
        public List<DialogContextEntity> entities { get; set; } = new List<DialogContextEntity>();

        /// <summary>How many exchanges the conversation had (the oldest may have left <see cref="turns"/>).</summary>
        public int totalTurns { get; set; }
    }

    /// <summary>An exchange of a conversation: a question and its answer.</summary>
    public class DialogContextTurn
    {
        /// <summary>Its number in the conversation, from 1.</summary>
        public int index { get; set; }

        public string dialogId { get; set; } = string.Empty;

        public DateTime date { get; set; }

        /// <summary>The question; once <see cref="folded"/>, what is left of it.</summary>
        public string prompt { get; set; } = string.Empty;

        /// <summary>The answer; once <see cref="folded"/>, what is left of it.</summary>
        public string completion { get; set; } = string.Empty;

        /// <summary>The entities of the question and of the answer, by name.</summary>
        public List<string> entities { get; set; } = new List<string>();

        /// <summary>
        /// True once the exchange is old enough to be kept only as its digest: its texts were cut for good (their start,
        /// and what they said about the entities that came back), the originals are gone.
        /// </summary>
        public bool folded { get; set; }

        /// <summary>The size of the question and of the answer as they were said, in characters.</summary>
        public int originalChars { get; set; }
    }

    /// <summary>An entity of a conversation.</summary>
    public class DialogContextEntity
    {
        public string name { get; set; } = string.Empty;

        /// <summary>In how many exchanges it is; 2 or more: it comes back, what is said about it is kept.</summary>
        public int turns { get; set; }

        /// <summary>The number of the last exchange that names it.</summary>
        public int lastTurn { get; set; }
    }
}
