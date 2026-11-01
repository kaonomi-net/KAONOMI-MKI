using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;

namespace KAONOMI.SearchEngine.Engine;

/// <summary>
/// BM25 as Lucene 8 (the Lucene of Elasticsearch 7.14) computes it: k1 = 1.2, b = 0.75, the idf and the average length
/// over the documents that have the field (not every document), so that the scores are those of Elasticsearch (the norms
/// are encoded a little differently by Lucene 4.8: the scores of long texts can differ slightly).
/// </summary>
public sealed class EsSimilarity : BM25Similarity
{
    private const float BaseK1 = 1.2f;

    public EsSimilarity() : base(BaseK1, 0.75f)
    {
    }

    protected override float Idf(long docFreq, long numDocs) =>
        (float)Math.Log(1 + (numDocs - docFreq + 0.5D) / (docFreq + 0.5D));

    /// <summary>The length of the field encoded on one byte as Lucene 8 does (exact up to 40 terms, then 4 bits of precision).</summary>
    protected override byte EncodeNormValue(float boost, int fieldLength) => IntToByte4(fieldLength);

    protected override float DecodeNormValue(byte b) => Byte4ToInt(b);

    // SmallFloat of Lucene 8.
    private static readonly int NumFreeValues = 255 - LongToInt4(int.MaxValue);

    private static int LongToInt4(long i)
    {
        var bits = 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)i);
        if (bits < 4)
            return (int)i;
        var shift = bits - 4;
        var encoded = (int)((ulong)i >> shift) & 0x07;
        return encoded | ((shift + 1) << 3);
    }

    private static long Int4ToLong(int i)
    {
        long bits = i & 0x07;
        var shift = (i >> 3) - 1;
        return shift == -1 ? bits : (bits | 0x08) << shift;
    }

    private static byte IntToByte4(int i) =>
        i < NumFreeValues ? (byte)i : (byte)(NumFreeValues + LongToInt4(i - NumFreeValues));

    private static int Byte4ToInt(byte b) =>
        b < NumFreeValues ? b : (int)(NumFreeValues + Int4ToLong(b - NumFreeValues));

    protected override float AvgFieldLength(CollectionStatistics collectionStats)
    {
        var sum = collectionStats.SumTotalTermFreq;
        if (sum <= 0)
            return 1f;
        var docs = collectionStats.DocCount > 0 ? collectionStats.DocCount : collectionStats.MaxDoc;
        return (float)(sum / (double)docs);
    }

    public override Explanation IdfExplain(CollectionStatistics collectionStats, TermStatistics termStats)
    {
        var df = termStats.DocFreq;
        var docs = collectionStats.DocCount > 0 ? collectionStats.DocCount : collectionStats.MaxDoc;
        var idf = Idf(df, docs);
        return new Explanation(idf, $"idf(docFreq={df}, docCount={docs})");
    }

    public override Explanation IdfExplain(CollectionStatistics collectionStats, TermStatistics[] termStats)
    {
        var docs = collectionStats.DocCount > 0 ? collectionStats.DocCount : collectionStats.MaxDoc;
        var explanation = new Explanation { Description = "idf(), sum of:" };
        var idf = 0f;
        foreach (var stat in termStats)
        {
            var termIdf = Idf(stat.DocFreq, docs);
            explanation.AddDetail(new Explanation(termIdf, $"idf(docFreq={stat.DocFreq}, docCount={docs})"));
            idf += termIdf;
        }
        explanation.Value = idf;
        return explanation;
    }
}

/// <summary>
/// The rewrite of the fuzzy queries of Elasticsearch (<c>top_terms_blended_freqs_N</c>, Lucene 8): the N best terms,
/// each scored with the largest document frequency of them all, so that a rare misspelling does not outscore the
/// right word.
/// </summary>
public sealed class BlendedFreqRewrite : MultiTermQuery.RewriteMethod
{
    private readonly int _size;

    public BlendedFreqRewrite(int size) => _size = size;

    public override Query Rewrite(IndexReader reader, MultiTermQuery query)
    {
        var collector = new MultiTermQuery.TopTermsScoringBooleanQueryRewrite(_size);
        var rewritten = (BooleanQuery)collector.Rewrite(reader, query);
        var clauses = rewritten.GetClauses();
        if (clauses.Length <= 1)
            return rewritten;

        // The terms and their statistics, read again in the index.
        var top = reader.Context;
        var terms = new List<(Term Term, float Boost, TermContext Context)>();
        foreach (var clause in clauses)
        {
            if (clause.Query is not TermQuery termQuery)
                return rewritten;
            terms.Add((termQuery.Term, termQuery.Boost, TermContext.Build(top, termQuery.Term)));
        }
        var maxDf = terms.Max(t => t.Context.DocFreq);
        var maxTtf = terms.Any(t => t.Context.TotalTermFreq < 0) ? -1 : terms.Max(t => t.Context.TotalTermFreq);
        var df = Math.Min(reader.MaxDoc, maxDf);

        var result = new BooleanQuery(true) { Boost = rewritten.Boost };
        foreach (var (term, boost, context) in terms)
        {
            var blended = new TermContext(top);
            var first = true;
            for (var ord = 0; ord < top.Leaves.Count; ord++)
            {
                var state = context.Get(ord);
                if (state is null)
                    continue;
                blended.Register(state, ord, first ? df : 0, first ? maxTtf : 0);
                first = false;
            }
            result.Add(new TermQuery(term, blended) { Boost = boost }, Occur.SHOULD);
        }
        return result;
    }
}
