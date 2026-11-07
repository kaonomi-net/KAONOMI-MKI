using System.Text;
using System.Text.Json;

namespace KAONOMI.Server.Ner
{
    /// <summary>A piece of the text for the model: its id in the vocabulary, and the characters of the text it covers (end excluded).</summary>
    public readonly record struct XlmrToken(int Id, int Start, int End);

    /// <summary>
    /// The tokenizer of XLM-RoBERTa, read from its <c>tokenizer.json</c> (the one of Hugging Face): a SentencePiece
    /// Unigram model of 250,002 pieces. The text is normalized (NFKC, as SentencePiece's nmt_nfkc), cut at the spaces,
    /// each word starting with "▁", and each word cut into the pieces of the vocabulary whose sum of scores is the
    /// highest (Viterbi); a character no piece holds is &lt;unk&gt; (several in a row are one). The ids are those of
    /// Hugging Face (&lt;s&gt; 0, &lt;pad&gt; 1, &lt;/s&gt; 2, &lt;unk&gt; 3), with the position of each piece in the original text.
    /// </summary>
    public sealed class XlmrTokenizer
    {
        public const int BosId = 0, PadId = 1, EosId = 2;

        private readonly Dictionary<string, (int Id, double Score)> _pieces = new(StringComparer.Ordinal);
        private readonly int _unkId;
        private readonly int _maxPieceLength;
        private readonly double _unkScore;

        public XlmrTokenizer(string tokenizerJsonPath)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(tokenizerJsonPath));
            var model = document.RootElement.GetProperty("model");
            if (model.GetProperty("type").GetString() != "Unigram")
                throw new InvalidDataException($"{tokenizerJsonPath}: a Unigram model is expected");
            _unkId = model.TryGetProperty("unk_id", out var unk) && unk.ValueKind == JsonValueKind.Number ? unk.GetInt32() : 3;
            var id = 0;
            var lowest = 0.0;
            foreach (var entry in model.GetProperty("vocab").EnumerateArray())
            {
                var piece = entry[0].GetString()!;
                var score = entry[1].GetDouble();
                // The special pieces (<s>, </s>…) are never found in a text.
                if (id > 3)
                {
                    _pieces.TryAdd(piece, (id, score));
                    _maxPieceLength = Math.Max(_maxPieceLength, piece.Length);
                    lowest = Math.Min(lowest, score);
                }
                id++;
            }
            // An unknown character costs more than any piece, as in SentencePiece.
            _unkScore = lowest - 10;
        }

        /// <summary>The pieces of <paramref name="text"/>, without &lt;s&gt; and &lt;/s&gt;.</summary>
        public List<XlmrToken> Encode(string text)
        {
            var tokens = new List<XlmrToken>();
            var i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    i++;
                    continue;
                }
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                    i++;
                EncodeWord(text, start, i, tokens);
            }
            return tokens;
        }

        /// <summary>A word (characters <paramref name="start"/> to <paramref name="end"/>) cut into the pieces of best score.</summary>
        private void EncodeWord(string text, int start, int end, List<XlmrToken> tokens)
        {
            var word = text[start..end];
            var normalized = word.Normalize(NormalizationForm.FormKC);
            // The positions in the original text: one for one when NFKC kept the length, else the whole word.
            var exact = normalized.Length == word.Length;
            var s = "▁" + normalized;
            var n = s.Length;
            var best = new double[n + 1];
            var from = new int[n + 1];
            var pieceId = new int[n + 1];
            Array.Fill(best, double.NegativeInfinity);
            best[0] = 0;
            for (var j = 1; j <= n; j++)
            {
                // Never cut a pair of surrogates (an emoji, a rare character).
                if (j < n && char.IsLowSurrogate(s[j]))
                    continue;
                for (var k = Math.Max(0, j - _maxPieceLength); k < j; k++)
                {
                    if (double.IsNegativeInfinity(best[k]) || (k > 0 && char.IsLowSurrogate(s[k])))
                        continue;
                    if (_pieces.TryGetValue(s[k..j], out var piece) && best[k] + piece.Score > best[j])
                    {
                        best[j] = best[k] + piece.Score;
                        from[j] = k;
                        pieceId[j] = piece.Id;
                    }
                }
                // No piece ends here: the last character alone is unknown.
                if (double.IsNegativeInfinity(best[j]))
                {
                    var k = j - (j >= 2 && char.IsLowSurrogate(s[j - 1]) ? 2 : 1);
                    if (!double.IsNegativeInfinity(best[k]))
                    {
                        best[j] = best[k] + _unkScore;
                        from[j] = k;
                        pieceId[j] = _unkId;
                    }
                }
            }
            var pieces = new List<(int Id, int From, int To)>();
            for (var j = n; j > 0; j = from[j])
                pieces.Add((pieceId[j], from[j], j));
            pieces.Reverse();
            foreach (var (id, a, b) in pieces)
            {
                // Unknown pieces in a row make one, as Hugging Face does (fuse_unk).
                if (id == _unkId && tokens.Count > 0 && tokens[^1].Id == _unkId && tokens[^1].End == Map(a))
                {
                    tokens[^1] = tokens[^1] with { End = Map(b) };
                    continue;
                }
                tokens.Add(new XlmrToken(id, Map(a), Map(b)));
            }

            // The index k of s ("▁" first) in the original text.
            int Map(int k) => exact ? start + Math.Max(0, k - 1) : (k <= 1 ? start : end);
        }
    }
}
