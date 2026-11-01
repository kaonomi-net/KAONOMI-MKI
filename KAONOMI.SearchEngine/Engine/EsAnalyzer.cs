using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.TokenAttributes;
using Lucene.Net.Util;

namespace KAONOMI.SearchEngine.Engine;

/// <summary>
/// The <c>standard</c> analyzer of Elasticsearch 7.14 (Lucene 8.9): the standard tokenizer, then lower case, no stop
/// words.
/// </summary>
public sealed class EsStandardAnalyzer : Analyzer
{
    protected override TokenStreamComponents CreateComponents(string fieldName, TextReader reader)
    {
        var tokenizer = new EsStandardTokenizer(reader);
        return new TokenStreamComponents(tokenizer, new LowerCaseFilter(LuceneVersion.LUCENE_48, tokenizer));
    }
}

/// <summary>
/// The word segmentation of Lucene 8.9 (UAX #29 of a newer Unicode) on top of the standard tokenizer of Lucene 4.8,
/// for what the two differ on in real texts:
/// <list type="bullet">
/// <item>U+202F (narrow no-break space, the French typography: <c>200 g</c>, <c>appétit !</c>) joins words as
/// <c>_</c> does (Word_Break = ExtendNumLet since Unicode 11);</item>
/// <item>the emoji are tokens (<c>&lt;EMOJI&gt;</c>), with their modifiers, variation selectors, ZWJ sequences and
/// flags;</item>
/// <item>a token longer than 255 characters is cut in pieces of 255 (Lucene 4.8 dropped it).</item>
/// </list>
/// </summary>
public sealed class EsStandardTokenizer : Tokenizer
{
    private const int MaxTokenLength = 255;

    private readonly ICharTermAttribute _term;
    private readonly IOffsetAttribute _offset;
    private readonly ITypeAttribute _type;
    private readonly IPositionIncrementAttribute _increment;
    private readonly List<(int Start, int End, string Type)> _tokens = new();
    private StandardTokenizer? _inner;
    private string _text = string.Empty;
    private int _next;

    public EsStandardTokenizer(TextReader input) : base(input)
    {
        _term = AddAttribute<ICharTermAttribute>();
        _offset = AddAttribute<IOffsetAttribute>();
        _type = AddAttribute<ITypeAttribute>();
        _increment = AddAttribute<IPositionIncrementAttribute>();
    }

    public override void Reset()
    {
        base.Reset();
        _text = m_input.ReadToEnd();
        _tokens.Clear();
        _next = 0;

        // The words of Lucene 4.8, U+202F read as "_" (same length: the offsets are those of the text).
        var mapped = _text.Contains('\u202F') ? _text.Replace('\u202F', '_') : _text;
        var reader = new StringReader(mapped);
        if (_inner is null)
            _inner = new StandardTokenizer(LuceneVersion.LUCENE_48, reader) { MaxTokenLength = 1 << 16 };
        else
            _inner.SetReader(reader);
        var offset = _inner.GetAttribute<IOffsetAttribute>();
        var type = _inner.GetAttribute<ITypeAttribute>();
        _inner.Reset();
        var words = new List<(int, int, string)>();
        while (_inner.IncrementToken())
            words.Add((offset.StartOffset, offset.EndOffset, type.Type));
        _inner.End();
        _inner.Dispose();

        // The emoji, between the words.
        var gapStart = 0;
        foreach (var (start, end, wordType) in words)
        {
            AddEmoji(gapStart, start);
            for (var piece = start; piece < end; piece += MaxTokenLength)
                _tokens.Add((piece, Math.Min(end, piece + MaxTokenLength), wordType));
            gapStart = end;
        }
        AddEmoji(gapStart, _text.Length);
    }

    public override bool IncrementToken()
    {
        ClearAttributes();
        if (_next >= _tokens.Count)
            return false;
        var (start, end, type) = _tokens[_next++];
        _term.SetEmpty().Append(_text, start, end - start);
        _offset.SetOffset(CorrectOffset(start), CorrectOffset(end));
        _type.Type = type;
        _increment.PositionIncrement = 1;
        return true;
    }

    public override void End()
    {
        base.End();
        var final = CorrectOffset(_text.Length);
        _offset.SetOffset(final, final);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner?.Dispose();
        base.Dispose(disposing);
    }

    // ── Emoji (UTS #51) ──

    private void AddEmoji(int from, int to)
    {
        var i = from;
        while (i < to)
        {
            var length = EmojiLength(_text, i, to);
            if (length > 0)
            {
                _tokens.Add((i, i + length, "<EMOJI>"));
                i += length;
            }
            else
                i += char.IsSurrogatePair(_text, i) && i + 1 < to ? 2 : 1;
        }
    }

    /// <summary>The length of the emoji sequence at <paramref name="i"/>, or 0.</summary>
    private static int EmojiLength(string s, int i, int to)
    {
        var cp = CodePoint(s, i, to, out var size);
        var j = i + size;
        if (IsRegionalIndicator(cp))
        {
            if (j >= to)
                return 0;
            var next = CodePoint(s, j, to, out var nextSize);
            return IsRegionalIndicator(next) ? size + nextSize : 0;
        }
        // A keycap: 0-9, # or * then U+FE0F U+20E3 (the digits are already words: only # and * are left here).
        if (cp is '#' or '*')
        {
            var k = j < to && s[j] == '\uFE0F' ? j + 1 : j;
            return k < to && s[k] == '\u20E3' ? k + 1 - i : 0;
        }
        var selected = j < to && s[j] == '\uFE0F' && !char.IsLetterOrDigit(s, i);
        if (!IsPictographic(cp) && !selected)
            return 0;
        while (j < to)
        {
            var next = CodePoint(s, j, to, out var nextSize);
            if (next is 0xFE0F or 0x20E3 || next is >= 0x1F3FB and <= 0x1F3FF || next is >= 0xE0020 and <= 0xE007F)
                j += nextSize;
            else if (next == 0x200D && j + 1 < to)
            {
                var joined = CodePoint(s, j + 1, to, out var joinedSize);
                if (!IsPictographic(joined))
                    break;
                j += 1 + joinedSize;
            }
            else
                break;
        }
        return j - i;
    }

    private static int CodePoint(string s, int i, int to, out int size)
    {
        if (char.IsHighSurrogate(s[i]) && i + 1 < to && char.IsLowSurrogate(s[i + 1]))
        {
            size = 2;
            return char.ConvertToUtf32(s[i], s[i + 1]);
        }
        size = 1;
        return s[i];
    }

    private static bool IsRegionalIndicator(int cp) => cp is >= 0x1F1E6 and <= 0x1F1FF;

    /// <summary>The bases of the emoji: Extended_Pictographic (UTS #51), and the skin tones alone.</summary>
    private static bool IsPictographic(int cp) => cp switch
    {
        0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or >= 0x2194 and <= 0x2199 or 0x21A9 or 0x21AA
            or 0x231A or 0x231B or 0x2328 or 0x2388 or 0x23CF or >= 0x23E9 and <= 0x23F3 or >= 0x23F8 and <= 0x23FA
            or 0x24C2 or 0x25AA or 0x25AB or 0x25B6 or 0x25C0 or >= 0x25FB and <= 0x25FE or >= 0x2600 and <= 0x2605
            or >= 0x2607 and <= 0x2612 or >= 0x2614 and <= 0x2685 or >= 0x2690 and <= 0x2705 or >= 0x2708 and <= 0x2712
            or 0x2714 or 0x2716 or 0x271D or 0x2721 or 0x2728 or 0x2733 or 0x2734 or 0x2744 or 0x2747 or 0x274C or 0x274E
            or >= 0x2753 and <= 0x2755 or 0x2757 or >= 0x2763 and <= 0x2767 or >= 0x2795 and <= 0x2797 or 0x27A1
            or 0x27B0 or 0x27BF or 0x2934 or 0x2935 or >= 0x2B05 and <= 0x2B07 or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55
            or 0x3030 or 0x303D or 0x3297 or 0x3299
            or >= 0x1F000 and <= 0x1F0FF or >= 0x1F10D and <= 0x1F10F or 0x1F12F or >= 0x1F16C and <= 0x1F171
            or 0x1F17E or 0x1F17F or 0x1F18E or >= 0x1F191 and <= 0x1F19A or >= 0x1F1AD and <= 0x1F1E5
            or >= 0x1F201 and <= 0x1F20F or 0x1F21A or 0x1F22F or >= 0x1F232 and <= 0x1F23A or >= 0x1F23C and <= 0x1F23F
            or >= 0x1F249 and <= 0x1F3FA or >= 0x1F3FB and <= 0x1F3FF or >= 0x1F400 and <= 0x1F53D
            or >= 0x1F546 and <= 0x1F64F or >= 0x1F680 and <= 0x1F6FF or >= 0x1F774 and <= 0x1F77F
            or >= 0x1F7D5 and <= 0x1F7FF or >= 0x1F80C and <= 0x1F80F or >= 0x1F848 and <= 0x1F84F
            or >= 0x1F85A and <= 0x1F85F or >= 0x1F888 and <= 0x1F88F or >= 0x1F8AE and <= 0x1F8FF
            or >= 0x1F90C and <= 0x1F93A or >= 0x1F93C and <= 0x1F945 or >= 0x1F947 and <= 0x1FAFF
            or >= 0x1FC00 and <= 0x1FFFD => true,
        _ => false
    };
}
