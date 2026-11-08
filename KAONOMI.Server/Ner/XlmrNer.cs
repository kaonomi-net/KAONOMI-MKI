using System.Diagnostics;
using System.Text.Json;
using KAONOMI.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace KAONOMI.Server.Ner
{
    /// <summary>
    /// The named entities of a text by an XLM-RoBERTa token classification model exported to ONNX (quantized in int8, on
    /// the CPU; see Ner/export/ner_export.py): by default <c>FacebookAI/xlm-roberta-large-finetuned-conll03-english</c>
    /// (CoNLL-2003: persons, organizations, places and miscellaneous names; trained on English, it reads the other
    /// languages of XLM-R too, French among them), before it <c>Davlan/xlm-roberta-base-ner-hrl</c>. The labels are those
    /// of <c>config.json</c> (O, B-/I-PER, ORG, LOC, MISC…, tagged IOB1: I- is the usual one, B- only starts a title right
    /// after another of the same type). The pieces are grouped as the pipeline of Hugging Face does
    /// with aggregation "simple": the pieces in a row of the same type make one entity unless a piece is B-, its score
    /// the mean of theirs. A text longer than the model reads at once (512 pieces) is read in windows that overlap.
    /// </summary>
    public sealed class XlmrNer : IDisposable
    {
        /// <summary>The pieces read at once, &lt;s&gt; and &lt;/s&gt; aside.</summary>
        private const int Window = 510, Overlap = 128;

        private readonly InferenceSession _session;
        private readonly XlmrTokenizer _tokenizer;
        private readonly string[] _labels;

        public XlmrNer(string folder)
        {
            Folder = folder;
            var model = Path.Combine(folder, "model_quantized.onnx");
            if (!File.Exists(model))
                model = Path.Combine(folder, "model.onnx");
            var options = new Microsoft.ML.OnnxRuntime.SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Min(8, Environment.ProcessorCount) };
            _session = new InferenceSession(model, options);
            _tokenizer = new XlmrTokenizer(Path.Combine(folder, "tokenizer.json"));
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "config.json")));
            var id2label = config.RootElement.GetProperty("id2label");
            _labels = new string[id2label.EnumerateObject().Count()];
            foreach (var p in id2label.EnumerateObject())
                _labels[int.Parse(p.Name)] = p.Value.GetString()!;
            ModelFile = Path.GetFileName(model);
            ModelId = ReadModelId(folder, config.RootElement);
        }

        /// <summary>
        /// The Hugging Face name of the model: the one of reference.json (written by the export), or "_name_or_path" of
        /// config.json when it is such a name (owner/model, not a path), or else the name of the folder.
        /// </summary>
        private static string ReadModelId(string folder, JsonElement config)
        {
            static bool IsHubName(string? name) =>
                !string.IsNullOrWhiteSpace(name) && name.Count(c => c == '/') == 1 && !name.Contains('\\') && !name.Contains(':') && !name.StartsWith('/') && !name.StartsWith('.');
            try
            {
                var reference = Path.Combine(folder, "reference.json");
                if (File.Exists(reference))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(reference));
                    if (doc.RootElement.TryGetProperty("model", out var model) && IsHubName(model.GetString()))
                        return model.GetString()!;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
            }
            if (config.TryGetProperty("_name_or_path", out var path) && IsHubName(path.GetString()))
                return path.GetString()!;
            return Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        /// <summary>The name of the model (its Hugging Face id, e.g. FacebookAI/xlm-roberta-large-finetuned-conll03-english).</summary>
        public string ModelId { get; }

        public string Folder { get; }

        public string ModelFile { get; }

        /// <summary>The types the model finds, as the server names them, from its labels (PERSON, ORG, LOC, MISC for CoNLL-2003).</summary>
        public IReadOnlyList<string> Types => _labels.Where(l => l != "O").Select(l => TypeOf(l.Length > 2 && l[1] == '-' ? l[2..] : l)).Distinct().ToList();

        public XlmrTokenizer Tokenizer => _tokenizer;

        /// <summary>PER → PERSON; ORG, LOC, MISC (and DATE of the former model) stay.</summary>
        public static string TypeOf(string label) => label switch { "PER" => "PERSON", _ => label };

        /// <summary>
        /// The persons, organizations, places and dates of <paramref title="text"/>, in the order of the text. With
        /// <paramref title="whole"/> (the default), each entity is made whole after the model: its punctuation at both ends
        /// taken off (one of punctuation only dropped: "'" of "l'ESA"), a word cut in its middle completed ("L" of
        /// "Louvre" → "Louvre", never across an apostrophe or a hyphen), and two entities of the same type that then
        /// touch made one. Without it, the entities are those of the pipeline of Hugging Face (to compare with it).
        /// </summary>
        public List<NerEntity> Extract(string text, bool whole = true)
        {
            var raw = ExtractRaw(text);
            return whole ? Whole(text, raw) : raw;
        }

        private static List<NerEntity> Whole(string text, List<NerEntity> entities)
        {
            static bool Letter(char c) => char.IsLetterOrDigit(c);
            var made = new List<NerEntity>();
            foreach (var e in entities)
            {
                int start = e.start, end = e.end;
                while (start < end && !Letter(text[start]))
                    start++;
                while (end > start && !Letter(text[end - 1]))
                    end--;
                if (end <= start)
                    continue;
                while (start > 0 && Letter(text[start - 1]))
                    start--;
                while (end < text.Length && Letter(text[end]))
                    end++;
                // Two entities of the same type that touch, or two of any type that overlap once completed (the model gave
                // the pieces of one word two types), are one: of the type of the surer part.
                if (made.Count > 0 && (made[^1].end > start || (made[^1].end == start && made[^1].type == e.type)))
                {
                    var last = made[^1];
                    if (e.score > last.score)
                        last.type = e.type;
                    last.score = Math.Round((last.score + e.score) / 2, 4);
                    last.end = Math.Max(last.end, end);
                    last.text = text[last.start..last.end];
                    continue;
                }
                made.Add(new NerEntity { text = text[start..end], type = e.type, start = start, end = end, score = e.score, source = e.source });
            }
            return made;
        }

        private List<NerEntity> ExtractRaw(string text)
        {
            var tokens = _tokenizer.Encode(text);
            var found = new List<NerEntity>();
            if (tokens.Count == 0)
                return found;
            for (var first = 0; ; first += Window - Overlap)
            {
                var count = Math.Min(Window, tokens.Count - first);
                foreach (var e in Read(text, tokens, first, count))
                {
                    // In the overlap of two windows, the same entity once (the surer reading kept).
                    var same = found.FindIndex(f => f.start < e.end && e.start < f.end);
                    if (same < 0)
                        found.Add(e);
                    else if (e.score > found[same].score)
                        found[same] = e;
                }
                if (first + count >= tokens.Count)
                    break;
            }
            return found.OrderBy(e => e.start).ToList();
        }

        /// <summary>The entities of the pieces <paramref title="first"/> to <paramref title="first"/> + <paramref title="count"/>.</summary>
        private IEnumerable<NerEntity> Read(string text, List<XlmrToken> tokens, int first, int count)
        {
            var n = count + 2;
            var ids = new DenseTensor<long>([1, n]);
            var mask = new DenseTensor<long>([1, n]);
            ids[0, 0] = XlmrTokenizer.BosId;
            for (var i = 0; i < count; i++)
                ids[0, i + 1] = tokens[first + i].Id;
            ids[0, n - 1] = XlmrTokenizer.EosId;
            for (var i = 0; i < n; i++)
                mask[0, i] = 1;
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask)]);
            var logits = results.First(r => r.Name == "logits").AsTensor<float>();

            // Each piece: its label and the probability of it (softmax of its logits).
            var labelled = new List<(string Bi, string Type, double Score, int Start, int End)>();
            for (var i = 0; i < count; i++)
            {
                var best = 0;
                var max = float.NegativeInfinity;
                for (var k = 0; k < _labels.Length; k++)
                    if (logits[0, i + 1, k] > max)
                    {
                        max = logits[0, i + 1, k];
                        best = k;
                    }
                var sum = 0.0;
                for (var k = 0; k < _labels.Length; k++)
                    sum += Math.Exp(logits[0, i + 1, k] - max);
                var label = _labels[best];
                var (bi, type) = label.Length > 2 && label[1] == '-' ? (label[..1], label[2..]) : ("I", label);
                var t = tokens[first + i];
                labelled.Add((bi, type, 1 / sum, t.Start, t.End));
            }

            // The pieces in a row of the same type, unless a piece is B- (a second title right after the first).
            var group = new List<(string Bi, string Type, double Score, int Start, int End)>();
            foreach (var piece in labelled.Append(("B", "O", 0, 0, 0)))
            {
                if (group.Count > 0 && (piece.Type != group[^1].Type || piece.Bi == "B"))
                {
                    if (group[0].Type != "O")
                    {
                        var start = group[0].Start;
                        var end = group[^1].End;
                        // The positions of a piece may hold the space before it: the entity starts at its first letter.
                        while (start < end && char.IsWhiteSpace(text[start]))
                            start++;
                        if (end > start)
                            yield return new NerEntity
                            {
                                text = text[start..end],
                                type = TypeOf(group[0].Type),
                                start = start,
                                end = end,
                                score = Math.Round(group.Average(g => g.Score), 4),
                                source = "xlmr"
                            };
                    }
                    group.Clear();
                }
                group.Add(piece);
            }
        }

        /// <summary>The time of one reading of a short sentence, in milliseconds (the first one warms the model up).</summary>
        public double Warm()
        {
            Extract("Kaonomi warms up.");
            var watch = Stopwatch.StartNew();
            Extract("Jérôme Fortias works at Nexai in Paris.");
            return watch.Elapsed.TotalMilliseconds;
        }

        public void Dispose() => _session.Dispose();
    }
}
