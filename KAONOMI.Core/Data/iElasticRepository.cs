using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KAONOMI.Core.Data
{
    using Models;
    /// <summary>
    /// Elasticsearch repository marker for entities derived from <see cref="EntityBase"/>.
    /// </summary>
    public interface iElasticRepository<TEntity>
        where TEntity : EntityBase
    {
        /// <summary>
        /// The <paramref name="size"/> most recent documents (sorted by <c>date</c>, newest first), at most 10,000
        /// (the default Elasticsearch result window).
        /// </summary>
        IReadOnlyCollection<TEntity> Last(int size);
    }
    /// <summary>
    /// Specialization for persisting <see cref="Log"/> entries.
    /// </summary>
    public interface IElasticLogRepository : iElasticRepository<Log>
    {
    }
}
