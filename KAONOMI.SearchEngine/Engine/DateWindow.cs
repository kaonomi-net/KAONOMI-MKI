using System.Runtime.CompilerServices;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace KAONOMI.SearchEngine.Engine;

/// <summary>
/// The fast path of the searches sorted on a date (or a number), the most recent first: <c>Last</c>, <c>Since</c> and
/// <c>SinceAll</c> of KAONOMI on millions of logs. Instead of sorting every document, the query is run on the most
/// recent values only (a range on the field, as wide as needed to fill the page), which gives the same page: every
/// document left out sorts after the ones found. The total is counted up to <c>track_total_hits</c> (10,000), as
/// Elasticsearch does.
/// </summary>
public static class DateWindow
{
    /// <summary>The smallest and largest value and the number of documents with a value, by segment and field.</summary>
    private static readonly ConditionalWeakTable<object, Dictionary<string, (long Min, long Max, int Count)>> Stats = new();

    private const int Attempts = 4;

    /// <summary>The page of <paramref name="query"/> sorted by <paramref name="sort"/>, or false (the usual search is needed).</summary>
    public static bool TrySearch(IndexSearcher searcher, EsIndex index, Query query, SortSpec primary, FieldDoc? after, int n, Sort sort,
        out TopDocs top)
    {
        top = null!;
        if (primary.Missing is not null || !primary.MissingLast || primary.Mode is not null || primary.Field.StartsWith('_'))
            return false;
        if (index.Mapping.Get(primary.Field) is not { Type: EsType.Date or EsType.Long, Index: true } field)
            return false;
        var desc = primary.Descending;
        long? anchor = null;
        if (after is not null)
        {
            if (LuceneValues.ToPlain(after.Fields[0]) is not long value)
                return false;
            anchor = value;
        }

        var (min, max, count) = Range(searcher.IndexReader, desc ? field.MaxField : field.MinField);
        if (count == 0 || count < n * 4)
            return false; // few documents: the usual search is as fast
        var start = anchor ?? (desc ? max : min);
        var density = count / Math.Max(1.0, (double)max - min);
        var span = (long)Math.Clamp(n * 2 / density, 1, long.MaxValue / 16);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            long? lower = desc ? Subtract(start, span) : null;
            long? upper = desc ? null : Add(start, span);
            var bounded = new BooleanQuery(disableCoord: true)
            {
                { query, Occur.MUST },
                { new ConstantScoreQuery(NumericRangeQuery.NewInt64Range(field.Path, lower, upper, true, true)) { Boost = 0f }, Occur.MUST }
            };
            top = after is null
                ? searcher.Search(bounded, null, n, sort, false, false)
                : searcher.SearchAfter(after, bounded, null, n, sort, false, false);
            if (top.ScoreDocs.Length >= n)
                return true;
            if (desc ? lower <= min : upper >= max)
                return false; // every value is in the window: the documents without a value may be needed
            span = span > long.MaxValue / 16 ? long.MaxValue / 2 : span * 16;
        }
        return false;
    }

    /// <summary>The documents matching <paramref name="query"/>, counted up to <paramref name="limit"/> + 1.</summary>
    public static long Count(IndexSearcher searcher, Query query, int limit)
    {
        var collector = new CappedCounter(limit == int.MaxValue ? int.MaxValue : limit + 1);
        try
        {
            searcher.Search(query, collector);
        }
        catch (CappedCounter.Enough)
        {
        }
        return collector.Count;
    }

    private static (long Min, long Max, long Count) Range(IndexReader reader, string docValues)
    {
        long min = long.MaxValue, max = long.MinValue, count = 0;
        foreach (var leaf in reader.Leaves)
        {
            var atomic = leaf.AtomicReader;
            var stats = Stats.GetValue(atomic.CoreCacheKey, _ => new Dictionary<string, (long, long, int)>());
            (long Min, long Max, int Count) segment;
            lock (stats)
            {
                if (!stats.TryGetValue(docValues, out segment))
                    stats[docValues] = segment = Scan(atomic, docValues);
            }
            if (segment.Count == 0)
                continue;
            min = Math.Min(min, segment.Min);
            max = Math.Max(max, segment.Max);
            count += segment.Count;
        }
        return (min, max, count);
    }

    private static (long, long, int) Scan(AtomicReader reader, string field)
    {
        var values = reader.GetNumericDocValues(field);
        var exists = reader.GetDocsWithField(field);
        if (values is null || exists is null)
            return (0, 0, 0);
        long min = long.MaxValue, max = long.MinValue;
        var count = 0;
        for (var doc = 0; doc < reader.MaxDoc; doc++)
        {
            if (!exists.Get(doc))
                continue;
            var value = values.Get(doc);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            count++;
        }
        return (min, max, count);
    }

    private static long Subtract(long a, long b) => a < long.MinValue + b ? long.MinValue : a - b;
    private static long Add(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    /// <summary>Counts the hits and stops the search once there are enough.</summary>
    private sealed class CappedCounter(int limit) : ICollector
    {
        public sealed class Enough : Exception;

        public long Count { get; private set; }

        public void SetScorer(Scorer scorer)
        {
        }

        public void Collect(int doc)
        {
            if (++Count >= limit)
                throw new Enough();
        }

        public void SetNextReader(AtomicReaderContext context)
        {
        }

        public bool AcceptsDocsOutOfOrder => true;
    }
}
