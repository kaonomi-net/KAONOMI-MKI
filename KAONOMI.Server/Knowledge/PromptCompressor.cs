using System.Text;
using System.Text.RegularExpressions;
using KAONOMI.Core.Models;

namespace KAONOMI.Server.Knowledge
{
    /// <summary>What goes to the model: the system prompt (with the knowledge kept), the user prompt, and what was done.</summary>
    public sealed record CompressedPrompt(string User, string? System, PromptCompressionStats Stats);

    /// <summary>
    /// Writes the knowledge of a dialog in its prompt and makes the whole fit a budget of characters, by rules (no
    /// model is asked, it takes well under a millisecond):
    /// <list type="bullet">
    /// <item>the texts are squeezed: the runs of spaces and of empty lines become one;</item>
    /// <item>the knowledge (entities of the ontology, then intels by score, then the related entities) is written at the end of the system
    /// prompt: a sentence already said by another item is left out, each item is cut at the end of a sentence to its
    /// share of the budget of the knowledge, and the items that do not fit are dropped, the last ones first;</item>
    /// <item>the previous exchanges the chat writes before the question (« Discussions précédentes … Nouvelle question
    /// de l'utilisateur : ») are shortened (a long message keeps its start), then the oldest ones are dropped until the
    /// prompt fits;</item>
    /// <item>the question itself is never changed: the semantic cache of the LLM gateway compares it as it is, and it
    /// is what the model must answer.</item>
    /// </list>
    /// The knowledge goes in the system prompt, not in the question, so that the gateway counts it as context.
    /// </summary>
    public static class PromptCompressor
    {
        // The layout the chat gives to a prompt with its previous exchanges (MainViewModel.WithPreviousDiscussions);
        // the LLM gateway reads the same one (CacheQuestion).
        private const string Discussions = "Discussions précédentes";
        private const string NewQuestion = "Nouvelle question de l'utilisateur :";
        private const string UserLabel = "Utilisateur : ";
        private const string AssistantLabel = "Kaonomi : ";

        private const string KnowledgeHeader =
            "What Kaonomi knows about what the user talks about (its ontology and its intels). Use it when it helps to answer, "
            + "follow the instructions it gives, do not mention that it was given to you, and ignore what is not related to the question:";

        /// <summary>A previous message keeps this many characters at most (the question of the user, the answer of the assistant).</summary>
        private const int MaxPreviousQuestion = 300;
        private const int MaxPreviousAnswer = 500;
        private const int MinKnowledgeItem = 160;

        /// <summary>
        /// <paramref name="context"/>: the previous exchanges as the server keeps them for the conversation (see
        /// <see cref="ContextManager"/>), already compressed by their age; they replace the ones the chat wrote in the
        /// prompt and are not shortened again (the oldest still go when the whole does not fit).
        /// </summary>
        public static CompressedPrompt Compress(string? user, string? system, IReadOnlyList<DialogKnowledge>? knowledge, int maxChars, int knowledgeMaxChars,
            RenderedContext? context = null)
        {
            user ??= string.Empty;
            knowledge ??= [];
            maxChars = Math.Max(1000, maxChars);
            knowledgeMaxChars = Math.Clamp(knowledgeMaxChars, 0, maxChars / 2);
            var stats = new PromptCompressionStats
            {
                originalChars = user.Length + (system?.Length ?? 0) + knowledge.Sum(k => k.text.Length)
            };

            // The system prompt and the knowledge.
            var sentSystem = Squeeze(system);
            var block = KnowledgeBlock(knowledge, knowledgeMaxChars, stats);
            if (block.Length > 0)
                sentSystem = sentSystem.Length == 0 ? block : sentSystem + "\n\n" + block;

            // The user prompt: its previous exchanges within what is left, its question as it is.
            var sentUser = User(user, Math.Max(0, maxChars - sentSystem.Length), stats, context);
            if (context is not null)
            {
                // Before the compression: the conversation as it was said, in the place of what the chat wrote in the prompt.
                stats.originalChars = ContextManager.Question(user, bare: false).Length + (system?.Length ?? 0) + knowledge.Sum(k => k.text.Length) + context.OriginalChars;
                stats.steps.Add(context.Note);
            }

            stats.sentChars = sentUser.Length + sentSystem.Length;
            stats.ratio = stats.originalChars == 0 ? 100 : Math.Round(100.0 * stats.sentChars / stats.originalChars, 1);
            return new CompressedPrompt(sentUser, sentSystem.Length == 0 ? null : sentSystem, stats);
        }

        // ── The knowledge ──

        private static string KnowledgeBlock(IReadOnlyList<DialogKnowledge> knowledge, int budget, PromptCompressionStats stats)
        {
            if (knowledge.Count == 0 || budget <= 0)
            {
                stats.knowledgeDropped = knowledge.Count;
                if (knowledge.Count > 0)
                    stats.steps.Add($"knowledge: no room, {knowledge.Count} item(s) dropped");
                return string.Empty;
            }

            // The entities of the prompt first, then the intels (the best score first), then the summaries of the related
            // entities: when the room is short, these go first. The same source once.
            var ordered = knowledge.Where(k => !string.IsNullOrWhiteSpace(k.text))
                .GroupBy(k => k.kind + "/" + k.sourceId).Select(g => g.First())
                .OrderBy(k => k.kind == DialogKnowledge.KindOntology ? 0 : k.kind == DialogKnowledge.KindIntel ? 1 : 2).ThenByDescending(k => k.score).ToList();
            var share = Math.Max(MinKnowledgeItem, (budget - KnowledgeHeader.Length) / Math.Max(1, ordered.Count));
            var said = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            var size = KnowledgeHeader.Length;
            int cut = 0, repeated = 0;
            foreach (var item in ordered)
            {
                // A sentence another item already said is not said twice.
                var sentences = Sentences(Squeeze(item.text).Replace('\n', ' '));
                var fresh = sentences.Where(s => said.Add(Key(s))).ToList();
                repeated += sentences.Count - fresh.Count;
                var text = string.Join(" ", fresh).Trim();
                if (text.Length == 0)
                    continue;
                if (text.Length > share)
                {
                    text = Cut(text, share);
                    cut++;
                }
                if (size + text.Length + 3 > budget)
                    break;
                lines.Add("- " + text);
                size += text.Length + 3;
            }

            stats.knowledgeItems = lines.Count;
            stats.knowledgeDropped = ordered.Count - lines.Count;
            if (lines.Count == 0)
            {
                stats.steps.Add($"knowledge: nothing fits in {budget} characters");
                return string.Empty;
            }
            var block = KnowledgeHeader + "\n" + string.Join("\n", lines);
            stats.knowledgeChars = block.Length;
            stats.steps.Add($"knowledge: {lines.Count} item(s) written in the system prompt ({block.Length} characters)"
                            + (cut > 0 ? $", {cut} shortened" : string.Empty)
                            + (repeated > 0 ? $", {repeated} repeated sentence(s) left out" : string.Empty)
                            + (stats.knowledgeDropped > 0 ? $", {stats.knowledgeDropped} dropped (no room)" : string.Empty));
            return block;
        }

        // ── The user prompt ──

        private static string User(string user, int budget, PromptCompressionStats stats, RenderedContext? context)
        {
            var at = user.TrimStart().StartsWith(Discussions, StringComparison.Ordinal) ? user.LastIndexOf(NewQuestion, StringComparison.Ordinal) : -1;
            if (at < 0 && context is null)
                return user; // A question alone: sent as it is.

            var question = at < 0 ? user.Trim('\r', '\n', ' ') : user[(at + NewQuestion.Length)..].Trim('\r', '\n', ' ');
            // The context the server keeps for the conversation replaces the exchanges the chat wrote.
            var messages = context is not null ? context.Messages.ToList() : PreviousMessages(user[..at]);
            stats.historyMessages = messages.Count;

            // Each message squeezed and, when long, cut to its start (not the context: it is already cut by its age).
            var shortened = 0;
            for (var i = 0; context is null && i < messages.Count; i++)
            {
                var (isUser, text) = messages[i];
                var squeezed = Squeeze(text);
                var limit = isUser ? MaxPreviousQuestion : MaxPreviousAnswer;
                if (squeezed.Length > limit)
                {
                    squeezed = Cut(squeezed, limit);
                    shortened++;
                }
                messages[i] = (isUser, squeezed);
            }

            // The oldest exchanges go until the prompt fits (an exchange starts at a message of the user).
            var dropped = 0;
            while (messages.Count > 0 && Write(messages, question).Length > budget)
            {
                messages.RemoveAt(0);
                dropped++;
                while (messages.Count > 0 && !messages[0].IsUser)
                {
                    messages.RemoveAt(0);
                    dropped++;
                }
            }

            stats.historyKept = messages.Count;
            if (shortened > 0 || dropped > 0)
                stats.steps.Add($"previous exchanges: {stats.historyMessages} message(s), {shortened} shortened, {dropped} dropped (the oldest)");
            return Write(messages, question);
        }

        /// <summary>The messages written under « Discussions précédentes », each one starting at its label.</summary>
        private static List<(bool IsUser, string Text)> PreviousMessages(string history)
        {
            var messages = new List<(bool, string)>();
            StringBuilder? current = null;
            var isUser = false;
            void Close()
            {
                if (current is not null && current.ToString().Trim().Length > 0)
                    messages.Add((isUser, current.ToString().Trim()));
            }
            foreach (var line in history.Replace("\r\n", "\n").Split('\n'))
            {
                var user = line.StartsWith(UserLabel, StringComparison.Ordinal);
                if (user || line.StartsWith(AssistantLabel, StringComparison.Ordinal))
                {
                    Close();
                    isUser = user;
                    current = new StringBuilder(line[(user ? UserLabel : AssistantLabel).Length..]);
                }
                else
                    current?.Append('\n').Append(line);
            }
            Close();
            return messages;
        }

        /// <summary>The prompt as the chat writes it; the question alone when no previous message is left.</summary>
        private static string Write(List<(bool IsUser, string Text)> messages, string question)
        {
            if (messages.Count == 0)
                return question;
            var text = new StringBuilder(Discussions + " :\n\n");
            foreach (var (isUser, message) in messages)
                text.Append(isUser ? UserLabel : AssistantLabel).Append(message).Append("\n\n");
            return text.Append(NewQuestion).Append('\n').Append(question).ToString();
        }

        // ── The texts ──

        private static readonly Regex Spaces = new(@"[ \t ]+", RegexOptions.Compiled);
        private static readonly Regex EmptyLines = new(@"\n{3,}", RegexOptions.Compiled);
        private static readonly Regex SentenceEnd = new(@"(?<=[.!?…])\s+", RegexOptions.Compiled);

        /// <summary>The text without the spaces and the empty lines that say nothing.</summary>
        public static string Squeeze(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => Spaces.Replace(l, " ").TrimEnd());
            return EmptyLines.Replace(string.Join("\n", lines), "\n\n").Trim();
        }

        private static List<string> Sentences(string text) =>
            SentenceEnd.Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        private static string Key(string sentence) =>
            new string(sentence.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        /// <summary>The start of <paramref name="text"/>, at most <paramref name="limit"/> characters: whole sentences when there are, else whole words, then « … ».</summary>
        private static string Cut(string text, int limit)
        {
            if (text.Length <= limit)
                return text;
            var kept = new StringBuilder();
            foreach (var sentence in Sentences(text))
            {
                if (kept.Length + sentence.Length + 1 > limit - 2)
                    break;
                kept.Append(kept.Length > 0 ? " " : string.Empty).Append(sentence);
            }
            if (kept.Length >= limit / 3)
                return kept + " …";
            var head = text[..Math.Max(1, limit - 2)];
            var space = head.LastIndexOf(' ');
            return (space > limit / 2 ? head[..space] : head).TrimEnd() + " …";
        }
    }
}
