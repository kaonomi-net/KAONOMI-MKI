namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Indexable base entity: identifier, machine/user context, and comment.
    /// </summary>
    public class EntityBase
    {
        /// <summary>Builds an entity with a new GUID identifier.</summary>
        public EntityBase()
        {
            this.id = Guid.NewGuid().ToString();
        }
        /// <summary>Builds an entity with the provided identifier, or a GUID if null.</summary>
        /// <param name="id">Existing identifier, or null to generate one.</param>
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
        /// <summary>Origin machine name.</summary>
        public string machineName { get; set; }
        /// <summary>Origin user name.</summary>
        public string userName { get; set; }
        /// <summary>Free-text or descriptive label.</summary>
        public string comment { get; set; }
        /// <summary>Extension point for derived initialization.</summary>
        private void Init()
        {

        }
    }
}
