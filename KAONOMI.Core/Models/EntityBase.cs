namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Indexable base entity: identifier, machine/user context, comment, and the metadata every document shares: its
    /// title, its tags (<c>#TOPIC</c>), its events (<c>@EVENT</c>) and its signals (<c>!SIGNAL</c>), in upper case with
    /// <c>-</c> for the spaces (<see cref="TagRules"/> makes them so when the document is saved or read).
    /// </summary>
    public class EntityBase : ITagged
    {
        /// <summary>Builds an entity with a new GUID identifier.</summary>
        public EntityBase()
        {
            this.id = Guid.NewGuid().ToString();
        }
        /// <summary>Builds an entity with the provided identifier, or a GUID if null.</summary>
        /// <param title="id">Existing identifier, or null to generate one.</param>
        public EntityBase(string? id)
        {
            if (id == null)
            {
                this.id = Guid.NewGuid().ToString();
            }
            else
            {
                this.id = id;
            }
            Init();
        }
        /// <summary>Name of the application that owns the record.</summary>
        public string appName { get; set; }
        /// <summary>Unique document identifier (Elasticsearch / storage).</summary>
        public string id { get; set; }
        /// <summary>Timestamp associated with the event or creation.</summary>
        public DateTime date { get; set; }
        /// <summary>Origin machine title.</summary>
        public string machineName { get; set; }
        /// <summary>Origin user title.</summary>
        public string userName { get; set; }
        /// <summary>Free-text or descriptive label.</summary>
        public string comment { get; set; }

        /// <summary>
        /// The project the document is attached to: the id of a <see cref="Project"/> (JSON <c>projectId</c>), or null
        /// when it belongs to none. Every document has it; the chat chooses it by the name of the project.
        /// </summary>
        public string? projectId { get; set; }

        /// <summary>The title of the document (JSON <c>title</c>).</summary>
        public string title { get; set; } = string.Empty;

        /// <summary>The tags: <c>#TOPIC</c>, in upper case, <c>-</c> for the spaces (<see cref="TagRules.NormalizeTags"/>).</summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>The events: <c>@EVENT</c>, in upper case, <c>-</c> for the spaces (<see cref="TagRules.NormalizeEvents"/>).</summary>
        public List<string> Events { get; set; } = new List<string>();

        /// <summary>The signals: <c>!SIGNAL</c>, in upper case, <c>-</c> for the spaces (<see cref="TagRules.NormalizeSignals"/>).</summary>
        public List<string> Signals { get; set; } = new List<string>();

        /// <summary>Extension point for derived initialization.</summary>
        private void Init()
        {

        }
    }
}
