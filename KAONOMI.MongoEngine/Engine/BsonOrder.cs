using System.Text;
using MongoDB.Bson;

namespace KAONOMI.MongoEngine.Engine;

/// <summary>
/// The order of MongoDB between BSON values of any type: MinKey &lt; null &lt; numbers &lt; strings &lt; objects &lt;
/// arrays &lt; binary &lt; ObjectId &lt; booleans &lt; dates &lt; timestamps &lt; regular expressions &lt; MaxKey; the
/// numbers by value whatever their type (1 == 1L == 1.0), the strings by their UTF-8 bytes.
/// </summary>
public sealed class BsonOrder : IComparer<BsonValue>, IEqualityComparer<BsonValue>
{
    public static readonly BsonOrder Instance = new();

    /// <summary>The rank of the type in the order (equal ranks are compared by value).</summary>
    public static int Rank(BsonValue value) => value.BsonType switch
    {
        BsonType.MinKey => 1,
        BsonType.Undefined or BsonType.Null => 2,
        BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128 => 3,
        BsonType.String or BsonType.Symbol => 4,
        BsonType.Document => 5,
        BsonType.Array => 6,
        BsonType.Binary => 7,
        BsonType.ObjectId => 8,
        BsonType.Boolean => 9,
        BsonType.DateTime => 10,
        BsonType.Timestamp => 11,
        BsonType.RegularExpression => 12,
        BsonType.JavaScript or BsonType.JavaScriptWithScope => 13,
        BsonType.MaxKey => 14,
        _ => 0
    };

    public int Compare(BsonValue? x, BsonValue? y)
    {
        x ??= BsonNull.Value;
        y ??= BsonNull.Value;
        var rank = Rank(x).CompareTo(Rank(y));
        if (rank != 0)
            return rank;
        switch (Rank(x))
        {
            case 1 or 2 or 14:
                return 0;
            case 3:
                return CompareNumbers(x, y);
            case 4:
                return CompareBytes(Encoding.UTF8.GetBytes(x.ToString()!), Encoding.UTF8.GetBytes(y.ToString()!));
            case 5:
                return CompareDocuments(x.AsBsonDocument, y.AsBsonDocument);
            case 6:
            {
                var a = x.AsBsonArray;
                var b = y.AsBsonArray;
                for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
                {
                    var c = Compare(a[i], b[i]);
                    if (c != 0)
                        return c;
                }
                return a.Count.CompareTo(b.Count);
            }
            case 7:
            {
                var a = x.AsBsonBinaryData;
                var b = y.AsBsonBinaryData;
                var length = a.Bytes.Length.CompareTo(b.Bytes.Length);
                if (length != 0)
                    return length;
                var subType = ((int)a.SubType).CompareTo((int)b.SubType);
                return subType != 0 ? subType : CompareBytes(a.Bytes, b.Bytes);
            }
            case 8:
                return x.AsObjectId.CompareTo(y.AsObjectId);
            case 9:
                return x.AsBoolean.CompareTo(y.AsBoolean);
            case 10:
                return x.AsBsonDateTime.MillisecondsSinceEpoch.CompareTo(y.AsBsonDateTime.MillisecondsSinceEpoch);
            case 11:
                return x.AsBsonTimestamp.Value.CompareTo(y.AsBsonTimestamp.Value);
            case 12:
            {
                var a = x.AsBsonRegularExpression;
                var b = y.AsBsonRegularExpression;
                var pattern = string.CompareOrdinal(a.Pattern, b.Pattern);
                return pattern != 0 ? pattern : string.CompareOrdinal(a.Options, b.Options);
            }
            default:
                return string.CompareOrdinal(x.ToString(), y.ToString());
        }
    }

    private int CompareDocuments(BsonDocument a, BsonDocument b)
    {
        for (var i = 0; i < Math.Min(a.ElementCount, b.ElementCount); i++)
        {
            var ea = a.GetElement(i);
            var eb = b.GetElement(i);
            var type = Rank(ea.Value).CompareTo(Rank(eb.Value));
            if (type != 0)
                return type;
            var name = CompareBytes(Encoding.UTF8.GetBytes(ea.Name), Encoding.UTF8.GetBytes(eb.Name));
            if (name != 0)
                return name;
            var value = Compare(ea.Value, eb.Value);
            if (value != 0)
                return value;
        }
        return a.ElementCount.CompareTo(b.ElementCount);
    }

    private static int CompareNumbers(BsonValue x, BsonValue y)
    {
        if (x.BsonType is BsonType.Int32 or BsonType.Int64 && y.BsonType is BsonType.Int32 or BsonType.Int64)
            return x.ToInt64().CompareTo(y.ToInt64());
        if (x.BsonType == BsonType.Decimal128 || y.BsonType == BsonType.Decimal128)
        {
            try
            {
                return x.ToDecimal().CompareTo(y.ToDecimal());
            }
            catch (OverflowException)
            {
            }
        }
        var a = x.ToDouble();
        var b = y.ToDouble();
        if (double.IsNaN(a) || double.IsNaN(b))
            return double.IsNaN(a) ? double.IsNaN(b) ? 0 : -1 : 1; // NaN is the smallest number
        return a.CompareTo(b);
    }

    private static int CompareBytes(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    public bool Equals(BsonValue? x, BsonValue? y) => Compare(x, y) == 0;

    public int GetHashCode(BsonValue value) => Key(value).GetHashCode();

    /// <summary>
    /// A string equal for the values MongoDB holds equal (the key of the <c>_id</c> index and of the unique indexes):
    /// the numbers by value, the documents and arrays by their content.
    /// </summary>
    public static string Key(BsonValue value)
    {
        switch (Rank(value))
        {
            case 2:
                return "null";
            case 3:
            {
                if (value.BsonType is BsonType.Int32 or BsonType.Int64)
                    return "n:" + value.ToInt64();
                var d = value.ToDouble();
                return d == Math.Floor(d) && Math.Abs(d) < 9e15 ? "n:" + (long)d : "n:" + d.ToString("R");
            }
            case 4:
                return "s:" + value;
            case 5:
                return "{" + string.Join(",", value.AsBsonDocument.Elements.Select(e => e.Name + ":" + Key(e.Value))) + "}";
            case 6:
                return "[" + string.Join(",", value.AsBsonArray.Select(Key)) + "]";
            default:
                return value.BsonType + ":" + value.ToJson();
        }
    }
}
