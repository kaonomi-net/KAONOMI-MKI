using Lucene.Net.Util;

namespace KAONOMI.SearchEngine.Engine;

/// <summary>The values Lucene.Net uses in the sorts (<c>FieldDoc.Fields</c>, missing values): boxed J2N numbers.</summary>
public static class LuceneValues
{
    public static object Long(long value) => J2N.Numerics.Int64.GetInstance(value);
    public static object Double(double value) => J2N.Numerics.Double.GetInstance(value);
    public static object Single(float value) => J2N.Numerics.Single.GetInstance(value);
    public static object Int(int value) => J2N.Numerics.Int32.GetInstance(value);

    /// <summary>A sort value as a plain .NET value (<c>long</c>, <c>double</c>, <c>float</c>, <c>int</c>, <c>string</c>, null).</summary>
    public static object? ToPlain(object? value) => value switch
    {
        null => null,
        J2N.Numerics.Int64 l => l.ToInt64(),
        J2N.Numerics.Int32 i => i.ToInt32(),
        J2N.Numerics.Double d => d.ToDouble(),
        J2N.Numerics.Single f => f.ToSingle(),
        BytesRef b => b.Utf8ToString(),
        _ => value
    };
}
