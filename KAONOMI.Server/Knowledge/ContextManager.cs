using System.Text;
using System.Text.RegularExpressions;
using KAONOMI.Core.Models;

namespace KAONOMI.Server.Knowledge
{
    /// <summary>What the context of a conversation gives to a prompt: its messages, the oldest first, and what was done.</summary>
    public sealed record RenderedContext(List<(bool IsUser, string Text)> Messages, int OriginalChars, int RenderedChars, string Note);

    /// <summary>
    /// The context of a conversation, more and more compressed as its exchanges get older, except what they say about
    /// the entities that come back. By rules and the NER (no model is asked: it takes about a millisecond, and nothing
    /// is invented):
    /// <list type="bullet">
    /// <item><b>recent</b> (the last <c>contextRecentTurns</c> exchanges, 2): written as they were said;</item>
    /// <item><b>short</b> (the <c>contextShortTurns</c> before, 4): the question and the answer keep their start
    /// (300 and 600 characters, whole sentences);</item>
    /// <item><b>brief</b> (the <c>contextBriefTurns</c> before, 8): 160 and 220 characters;</item>
    /// <item><b>digest</b> (older): 100 and 120 characters, cut for good when the exchange gets there (the original
    /// texts are then dropped); at most <see cref="MaxDigests"/> digests are kept.</item>
    /// </list>
    /// An entity comes back when it is in two exchanges or more (the question being asked counts as one). The
    /// sentences that name such an entity are kept beyond these limits, up to as many characters again, at every
    /// level: what the conversation keeps talking about stays, the rest fades. When the whole is over
    /// <c>contextMaxChars</c>, the oldest exchanges are left out.
    /// </summary>
    public static class ContextManager
    {
        public const int MaxDigests = 60;

        // The limits of a level: the question, the answer; 0: no limit.
        private static readonly (int Question, int Answer)[] Limits = [(0, 0), (300, 600), (160, 220), (100, 120)];
        private static readonly string[] LevelNames = ["recent", "short", "brief", "digest"];

        /// <summary>A name in upper case then "please": the preset the chat addresses the question to.</summary>
        private static readonly Regex Addressed = new(@"^\s*[\p{Lu}\p{N}][\p{Lu}\p{N} _\-'.]{1,40}\s+please\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SentenceEnd = new(@"(?<=[.!?…])\s+|\n+", RegexOptions.Compiled);

        // The layout the chat gives to a prompt with its previous exchanges (see PromptCompressor).
        private const string Discussions = "Discussions précédentes";
        private const string NewQuestion = "Nouvelle question de l'utilisateur :";

        /// <summary>
        /// The question of a user prompt: what follows « Nouvelle question de l'utilisateur : » when the chat wrote the
        /// previous exchanges before it, else the prompt. <paramref name="bare"/>: also without the name it is addressed to.
        /// </summary>
        public static string Question(string? prompt, bool bare)
        {
            var text = prompt ?? string.Empty;
            if (text.TrimStart().StartsWith(Discussions, StringComparison.Ordinal))
            {
                var at = text.LastIndexOf(NewQuestion, StringComparison.Ordinal);
                if (at >= 0)
                    text = text[(at + NewQuestion.Length)..];
            }
            text = text.Trim();
            if (!bare)
                return text;
            var stripped = Addressed.Replace(text, string.Empty, 1).Trim();
            return stripped.Length == 0 ? text : stripped;
        }

        /// <summary>
        /// The names of the entities that count for the context: the named entities the model of the NER found (people,
        /// organisations, places…), by their name in the ontology when they have one. The numbers and dates of the rules
        /// are left out, and so are the common words the ontology alone knows (« eau », « énergie »): they are in every
        /// other exchange and would keep everything.
        /// </summary>
        public static List<string> EntityNames(IEnumerable<NerEntity>? entities) =>
            (entities ?? [])
            .Where(e => (e.source ?? string.Empty).Contains("xlmr", StringComparison.OrdinalIgnoreCase))
            .Select(e => Plain(string.IsNullOrWhiteSpace(e.ontologyName) ? e.text : e.ontologyName))
            .Where(n => n.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        /// <summary>A text with plain spaces: a model often writes a name with a no-break space (« Marie Curie »).</summary>
        private static string Plain(string? text) =>
            string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>The level of an exchange by its age (0: the last one).</summary>
        private static int Level(int age, Config config)
        {
            var recent = Math.Max(1, config.contextRecentTurns);
            var shortTurns = Math.Max(0, config.contextShortTurns);
            var brief = Math.Max(0, config.contextBriefTurns);
            return age < recent ? 0 : age < recent + shortTurns ? 1 : age < recent + shortTurns + brief ? 2 : 3;
        }

        /// <summary>
        /// Adds an answered exchange to the context: the entities are counted again, and the exchanges that are now old
        /// enough become digests (cut for good, with what they say about the entities that come back). The last question
        /// asked again replaces its exchange.
        /// </summary>
        public static string AddTurn(DialogContext context, string dialogId, string question, string completion, IEnumerable<string> entityNames, Config config, bool regenerate = false)
        {
            // The last question asked again because its answer was disliked (the request says so): the new answer takes
            // the place of the one before. The same words typed twice (« continue ») are one more exchange.
            if (regenerate && context.turns.Count > 0 && !context.turns[^1].folded
                && string.Equals(context.turns[^1].prompt, PromptCompressor.Squeeze(question), StringComparison.OrdinalIgnoreCase))
            {
                context.turns.RemoveAt(context.turns.Count - 1);
                context.totalTurns = Math.Max(0, context.totalTurns - 1);
            }
            context.totalTurns++;
            context.turns.Add(new DialogContextTurn
            {
                index = context.totalTurns,
                dialogId = dialogId,
                date = DateTime.UtcNow,
                prompt = PromptCompressor.Squeeze(question),
                completion = PromptCompressor.Squeeze(completion),
                entities = entityNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                originalChars = (question?.Length ?? 0) + (completion?.Length ?? 0)
            });

            // The entities of the conversation, counted on the exchanges that are still there.
            context.entities = context.turns
                .SelectMany(t => t.entities.Distinct(StringComparer.OrdinalIgnoreCase).Select(name => (name, t.index)))
                .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DialogContextEntity { name = g.First().name, turns = g.Count(), lastTurn = g.Max(x => x.index) })
                .OrderByDescending(e => e.turns).ThenByDescending(e => e.lastTurn)
                .ToList();
            var repeated = Repeated(context, []);

            // The exchanges that reach the last level are cut for good.
            var folded = 0;
            for (var i = 0; i < context.turns.Count; i++)
            {
                var turn = context.turns[i];
                if (turn.folded || Level(context.turns.Count - 1 - i, config) < 3)
                    continue;
                turn.prompt = Compress(turn.prompt, Limits[3].Question, repeated);
                turn.completion = Compress(turn.completion, Limits[3].Answer, repeated);
                turn.folded = true;
                folded++;
            }

            // Past the most digests kept, the oldest go.
            var dropped = Math.Max(0, context.turns.Count(t => t.folded) - MaxDigests);
            if (dropped > 0)
                context.turns.RemoveRange(0, dropped);

            return $"exchange {context.totalTurns} added ({context.turns[^1].entities.Count} entity(ies)); "
                   + $"{context.turns.Count} exchange(s) kept" + (folded > 0 ? $", {folded} became digest(s)" : string.Empty)
                   + (dropped > 0 ? $", {dropped} dropped (the oldest)" : string.Empty)
                   + (repeated.Count > 0 ? "; entities that come back: " + string.Join(", ", repeated.Take(20)) : string.Empty);
        }

        /// <summary>The entities in two exchanges or more, the entities of the question being asked counting as one more.</summary>
        private static List<string> Repeated(DialogContext context, IEnumerable<string> currentEntities)
        {
            var current = currentEntities.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return context.entities
                .Where(e => e.turns + (current.Contains(e.name) ? 1 : 0) >= 2)
                .Select(e => e.name)
                .ToList();
        }

        /// <summary>
        /// The context as the messages to write before the question: each exchange at the level of its age, what it
        /// says about the entities that come back kept, the oldest left out when the whole is over the budget.
        /// </summary>
        public static RenderedContext Render(DialogContext context, IEnumerable<string> currentEntities, Config config, string? currentQuestion = null)
        {
            var repeated = Repeated(context, currentEntities);
            // The question asked again right away (its answer was disliked): the exchange it replaces is not written.
            var turns = context.turns.Count > 0 && currentQuestion is not null && !context.turns[^1].folded
                        && string.Equals(context.turns[^1].prompt, PromptCompressor.Squeeze(currentQuestion), StringComparison.OrdinalIgnoreCase)
                ? context.turns.Take(context.turns.Count - 1).ToList()
                : context.turns;
            var rendered = new List<(int Level, string Question, string Answer, bool About)>();
            for (var i = 0; i < turns.Count; i++)
            {
                var turn = turns[i];
                var level = turn.folded ? 3 : Level(turns.Count - 1 - i, config);
                // A digest is already cut; the others are cut here, from their original texts.
                var question = turn.folded ? turn.prompt : Compress(turn.prompt, Limits[level].Question, repeated);
                var answer = turn.folded ? turn.completion : Compress(turn.completion, Limits[level].Answer, repeated);
                // An exchange is about what comes back when it names one of these entities.
                var about = repeated.Count > 0 && (turn.entities.Any(e => repeated.Contains(e, StringComparer.OrdinalIgnoreCase))
                                                   || repeated.Any(name => Names(question, name) || Names(answer, name)));
                rendered.Add((level, question, answer, about));
            }

            // Over the budget: exchanges are left out, never the last one. First the oldest that say nothing about the
            // entities that come back; only then the oldest of those that do.
            var budget = Math.Max(500, config.contextMaxChars);
            var kept = rendered.ToList();
            var left = 0;
            while (kept.Count > 1 && kept.Sum(r => r.Question.Length + r.Answer.Length + 30) > budget)
            {
                var at = kept.FindIndex(0, kept.Count - 1, r => !r.About);
                kept.RemoveAt(at < 0 ? 0 : at);
                left++;
            }

            var messages = new List<(bool, string)>();
            foreach (var (_, question, answer, _) in kept.ToList())
            {
                if (question.Length > 0)
                    messages.Add((true, question));
                if (answer.Length > 0)
                    messages.Add((false, answer));
            }

            var original = turns.Sum(t => t.originalChars);
            var size = kept.Sum(r => r.Question.Length + r.Answer.Length);
            var levels = string.Join(", ", kept.GroupBy(r => r.Level).OrderBy(g => g.Key).Select(g => $"{g.Count()} {LevelNames[g.Key]}"));
            var note = $"context of the conversation: {kept.Count} exchange(s) of {context.totalTurns} ({levels}), {original} → {size} characters"
                       + (left > 0 ? $", {left} left out (over {budget} characters)" : string.Empty)
                       + (repeated.Count > 0 ? "; kept about: " + string.Join(", ", repeated.Take(12)) : string.Empty);
            return new RenderedContext(messages, original, size, note);
        }

        // ── The compression of a text ──

        /// <summary>
        /// <paramref name="text"/> within <paramref name="limit"/> characters (0: as it is): its first sentences up
        /// to the limit, then the sentences that name one of <paramref name="repeated"/>, up to as many characters
        /// again; « … » stands for what was left out.
        /// </summary>
        public static string Compress(string? text, int limit, IReadOnlyCollection<string> repeated)
        {
            text = (text ?? string.Empty).Trim();
            if (limit <= 0 || text.Length <= limit)
                return text;

            var sentences = SentenceEnd.Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var keep = new bool[sentences.Count];

            // The start of the text.
            var size = 0;
            for (var i = 0; i < sentences.Count; i++)
            {
                if (size + sentences[i].Length > limit)
                    break;
                keep[i] = true;
                size += sentences[i].Length + 1;
            }
            // A first sentence longer than the limit is cut at a word.
            if (!keep[0])
            {
                var head = sentences[0][..Math.Min(sentences[0].Length, Math.Max(1, limit - 2))];
                var space = head.LastIndexOf(' ');
                sentences[0] = (space > limit / 2 ? head[..space] : head).TrimEnd() + " …";
                keep[0] = true;
            }

            // What is said about the entities that come back.
            var extra = 0;
            if (repeated.Count > 0)
            {
                for (var i = 0; i < sentences.Count; i++)
                {
                    if (keep[i] || !repeated.Any(name => Names(sentences[i], name)))
                        continue;
                    if (extra + sentences[i].Length > limit)
                        continue; // Too long for what is left: a shorter one may still fit.
                    keep[i] = true;
                    extra += sentences[i].Length + 1;
                }
            }

            var result = new StringBuilder();
            var gap = false;
            for (var i = 0; i < sentences.Count; i++)
            {
                if (!keep[i])
                {
                    gap = true;
                    continue;
                }
                if (result.Length > 0)
                    result.Append(gap ? " … " : " ");
                result.Append(sentences[i]);
                gap = false;
            }
            if (gap && !result.ToString().EndsWith('…'))
                result.Append(" …");
            return result.ToString();
        }

        /// <summary>True when <paramref name="sentence"/> names <paramref name="name"/> as whole words, case ignored.</summary>
        private static bool Names(string sentence, string name)
        {
            sentence = sentence.Replace(' ', ' ').Replace(' ', ' ');
            var at = 0;
            while ((at = sentence.IndexOf(name, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var before = at == 0 || !char.IsLetterOrDigit(sentence[at - 1]);
                var end = at + name.Length;
                var after = end >= sentence.Length || !char.IsLetterOrDigit(sentence[end]);
                if (before && after)
                    return true;
                at = end;
            }
            return false;
        }
    }
}
