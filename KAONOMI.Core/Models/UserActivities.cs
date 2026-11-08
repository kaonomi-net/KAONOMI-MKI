namespace KAONOMI.Core.Models
{
    /// <summary>
    /// An activity of the journal of the chat: a SAVE done in a window, with the title of the window, the link that opens
    /// it again and a description. Its index is <c>useractivities</c> (the title of the class). Never erased
    /// (<see cref="ISoftDeletable"/>).
    /// </summary>
    public class UserActivities : EntityBase, ISoftDeletable
    {
        /// <summary>What was done: "SAVE".</summary>
        public string action { get; set; } = "SAVE";
        /// <summary>The title (title) of the window.</summary>
        public string windowName { get; set; } = string.Empty;
        /// <summary>The link of the window: its command (/intel, /mindmap…) or its key.</summary>
        public string link { get; set; } = string.Empty;
        /// <summary>What was saved.</summary>
        public string description { get; set; } = string.Empty;
        public bool isDeleted { get; set; } = false;
    }
}
