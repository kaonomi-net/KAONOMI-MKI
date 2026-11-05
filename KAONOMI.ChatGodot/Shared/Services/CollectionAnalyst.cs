using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The analysis of a collection plan: the pages the web scraper read are given to the LLM with the instructions of the
/// plan, which it carries out on them alone (citing its sources); the answer becomes a document of /work: the title
/// (the plan), a line of what was done, the instructions, the analysis, and the list of the pages read.
/// </summary>
public static class CollectionAnalyst
{
    /// <summary>The most characters of collected text given to the LLM (the pages share them).</summary>
    public const int Budget = 30_000;

    public static readonly TimeSpan AnalysisTimeout = TimeSpan.FromMinutes(12);

    /// <summary>What the LLM is told to be and to do.</summary>
    public static string SystemPrompt(string language) =>
        "You are an intelligence analyst. A web scraper has collected web pages for a collection plan. "
        + "Carry out the instructions of the plan, using only the material collected below: do not invent facts, and say plainly what the material does not tell. "
        + "Cite your sources after the facts they support as Markdown links whose text is the headline of the article or the title of the page, "
        + "like [Budget 2027 : les députés remanient la TVA](https://…), "
        + "using only the addresses written in the material (the pages, and the links of their headlines for the articles they list): never make up an address. "
        + $"Write your answer in {language}, in Markdown: ## headings, short paragraphs, - lists, **bold** for the key facts. "
        + "Do not repeat the instructions; start directly with the result.";

    /// <summary>
    /// The plan and the material, cut to <see cref="Budget"/>: for each page, the pieces of information the vision model
    /// read on its pictures and its text, or (not read so) the passages of its HTML holding the keywords.
    /// </summary>
    public static string UserPrompt(CollectionPlanEntry plan, IReadOnlyList<ScrapedPage> pages, IReadOnlyDictionary<string, PageReading>? readings = null)
    {
        var prompt = new StringBuilder();
        prompt.Append("# Collection plan: ").Append(plan.Name).Append('\n');
        if (!string.IsNullOrWhiteSpace(plan.Project))
            prompt.Append("Project: ").Append(plan.Project).Append('\n');
        if (!string.IsNullOrWhiteSpace(plan.Description))
            prompt.Append("Description: ").Append(plan.Description).Append('\n');
        prompt.Append("\n# Instructions to carry out\n")
            .Append(string.IsNullOrWhiteSpace(plan.Instructions) ? "Summarise what the pages say, the key facts first." : plan.Instructions)
            .Append("\n\n# Material collected\n");
        PageReading? ReadingOf(ScrapedPage page) => readings is not null && readings.TryGetValue(page.Url, out var reading) ? reading : null;
        var read = pages.Where(p => p.Ok && (ReadingOf(p) is not null || p.Excerpts.Count > 0 || p.Description.Length > 0)).ToList();
        if (read.Count == 0)
        {
            prompt.Append("Nothing could be read.\n");
            return prompt.ToString();
        }
        // The pages with the most keyword hits first; each gets its share of the budget.
        var share = Math.Max(1200, Budget / read.Count);
        var n = 0;
        foreach (var page in read.OrderByDescending(p => p.KeywordHits))
        {
            n++;
            prompt.Append($"\n## [{n}] {(page.Title.Length > 0 ? page.Title : page.Url)}\n");
            prompt.Append("Address: ").Append(page.Url).Append('\n');
            prompt.Append("Website: ").Append(page.Website);
            if (page.Keywords.Count > 0)
                prompt.Append(" · keywords: ").Append(string.Join(", ", page.Keywords)).Append(" (").Append(page.KeywordHits).Append(" hits)");
            prompt.Append('\n');
            if (page.Description.Length > 0)
                prompt.Append("Description: ").Append(page.Description).Append('\n');
            var used = 0;
            if (page.Headlines.Count > 0)
            {
                prompt.Append("Links of the headlines of the page (the only addresses to cite for them):\n");
                foreach (var (headline, url) in page.Headlines)
                {
                    var line = $"- {headline}: {url}\n";
                    if (used + line.Length > share / 3)
                        break;
                    prompt.Append(line);
                    used += line.Length;
                }
            }
            if (ReadingOf(page) is { } reading)
            {
                prompt.Append("Information read on the page:\n");
                foreach (var information in reading.Informations)
                {
                    var line = $"- {information.Title} ({information.Kind}): {information.Summary}\n";
                    if (used + line.Length > share)
                        break;
                    prompt.Append(line);
                    used += line.Length;
                }
                if (used < share && reading.Text.Length > 0)
                {
                    var text = reading.Text.Length > share - used ? reading.Text[..Math.Max(0, share - used)] + "…" : reading.Text;
                    prompt.Append("Text read on the page:\n").Append(text).Append('\n');
                }
            }
            else
            {
                foreach (var excerpt in page.Excerpts)
                {
                    if (used + excerpt.Length > share)
                        break;
                    prompt.Append("- ").Append(excerpt).Append('\n');
                    used += excerpt.Length;
                }
            }
            if (prompt.Length > Budget + 6000)
                break;
        }
        return prompt.ToString();
    }

    /// <summary>Asks <paramref name="model"/> for the analysis (Markdown).</summary>
    public static async Task<string> AnalyseAsync(OllamaModelService ollama, string model, string language, CollectionPlanEntry plan,
        IReadOnlyList<ScrapedPage> pages, IReadOnlyDictionary<string, PageReading>? readings, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AnalysisTimeout);
        var answer = await ollama.ChatAsync(model, SystemPrompt(language), UserPrompt(plan, pages, readings), timeout.Token);
        return StripThinking(answer).Trim();
    }

    /// <summary>A reasoning model may write its thoughts between &lt;think&gt; tags: only the answer is kept.</summary>
    private static string StripThinking(string text)
    {
        var end = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return end >= 0 ? text[(end + "</think>".Length)..] : text;
    }

    /// <summary>The title of the document: the plan and the time.</summary>
    public static string DocumentTitle(CollectionPlanEntry plan, DateTime when) =>
        $"{(string.IsNullOrWhiteSpace(plan.Name) ? "Collection plan" : plan.Name!.Trim())} · {when:yyyy-MM-dd HH:mm}";

    /// <summary>
    /// The text of the document of /work (the format of the tape: Markdown, title and subtitle as &lt;p class&gt;): the
    /// plan, the instructions, the analysis, then each page: its address, its pictures, each piece of information read
    /// on it, and its text; last, the list of the pages, read or not.
    /// </summary>
    public static string Tape(CollectionPlanEntry plan, IReadOnlyList<ScrapedPage> pages, string analysis, string model, string vision, DateTime when,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? pictures = null, IReadOnlyDictionary<string, PageReading>? readings = null)
    {
        var ok = pages.Count(p => p.Ok);
        var text = new StringBuilder();
        text.Append("<p class=\"title\">").Append(Escape(string.IsNullOrWhiteSpace(plan.Name) ? "Collection plan" : plan.Name!)).Append("</p>\n");
        text.Append("<p class=\"subtitle\">").Append(Escape(string.Create(CultureInfo.InvariantCulture,
            $"Web scraper · {when:yyyy-MM-dd HH:mm} · {ok} page(s) read of {pages.Count} · pages read by {vision} · analysis by {model}"))).Append("</p>\n");
        if (!string.IsNullOrWhiteSpace(plan.Description))
            text.Append('*').Append(Escape(plan.Description!.Replace('\n', ' ').Trim())).Append("*\n");
        text.Append("## Instructions\n");
        foreach (var line in (plan.Instructions ?? "(none: a summary of the pages)").Replace("\r\n", "\n").Split('\n'))
            text.Append(Escape(line)).Append('\n');
        // A heading of its own when the model wrote none first.
        if (!analysis.TrimStart().StartsWith('#'))
            text.Append("## Analysis\n");
        foreach (var raw in analysis.Replace("\r\n", "\n").Split('\n'))
        {
            // An address with parentheses (Wikipedia) written between < >, so that the link holds.
            var line = ParenthesisLink.Replace(raw, "](<$1>)");
            // The tape knows three levels of heading: deeper ones become the third.
            var trimmed = line.TrimStart();
            text.Append(trimmed.StartsWith("####", StringComparison.Ordinal) ? "### " + trimmed.TrimStart('#').Trim() : line).Append('\n');
        }

        // Each page: its pictures, the information read on it, its text.
        var number = 0;
        foreach (var page in pages.Where(p => p.Ok))
        {
            if (number == 0)
                text.Append("## Pages\n");
            number++;
            var title = page.Title.Length > 0 ? page.Title : page.Url;
            text.Append("### ").Append(number).Append(". ").Append(Escape(title)).Append('\n');
            text.Append(Link(page.Url, page.Url)).Append(" · ").Append(Escape(page.Website)).Append(" · ").Append(page.Mode);
            if (page.Keywords.Count > 0)
                text.Append(" · ").Append(page.KeywordHits).Append(" keyword hit(s)");
            text.Append('\n');
            if (pictures is not null && pictures.TryGetValue(page.Url, out var screens))
                for (var k = 0; k < screens.Count; k++)
                    text.Append(Picture(screens[k], screens.Count > 1 ? $"{title} ({k + 1}/{screens.Count})" : title, PictureWidth)).Append('\n');
            if (readings is not null && readings.TryGetValue(page.Url, out var reading))
            {
                text.Append("**").Append(reading.Informations.Count).Append(" piece(s) of information** · ").Append(Escape(reading.Language)).Append('\n');
                foreach (var information in reading.Informations)
                    text.Append("- **").Append(Escape(Markup(information.Title))).Append("** (").Append(information.Kind).Append(") — ").Append(Escape(Markup(information.Summary))).Append('\n');
                if (reading.Text.Length > 0)
                {
                    text.Append("**Text read on the page**\n");
                    foreach (var paragraph in Paragraphs(reading.Text))
                        text.Append(Escape(Markup(paragraph))).Append('\n');
                }
            }
            else
                text.Append("*Not read from its pictures: the analysis used the text of its HTML.*\n");
            // Every headline the page links to (all of the page, not only its first screens), with its real address.
            if (page.Headlines.Count > 0)
            {
                text.Append("**").Append(page.Headlines.Count).Append(" headline(s) linked by the page**\n");
                foreach (var (headline, url) in page.Headlines)
                    text.Append("- ").Append(Link(headline, url)).Append('\n');
            }
        }

        text.Append("## Sources\n");
        foreach (var page in pages)
        {
            var title = page.Title.Length > 0 ? page.Title : page.Url;
            text.Append("- ").Append(Link(title, page.Url)).Append(" · ").Append(Escape(page.Website)).Append(" · ").Append(page.Mode);
            if (page.Keywords.Count > 0)
                text.Append(" · ").Append(page.KeywordHits).Append(" keyword hit(s)");
            if (!page.Ok)
                text.Append(" · **not read**: ").Append(Escape(page.Error!));
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>
    /// The paragraphs of a text read on a page: its lines, a line too long (the model wrote it all on one line) cut
    /// at the ends of its sentences into paragraphs of about 600 characters.
    /// </summary>
    public static IEnumerable<string> Paragraphs(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.Length <= 900)
            {
                yield return line;
                continue;
            }
            var paragraph = new StringBuilder();
            foreach (System.Text.RegularExpressions.Match sentence in Sentence.Matches(line))
            {
                paragraph.Append(sentence.Value);
                if (paragraph.Length >= 600)
                {
                    yield return paragraph.ToString().Trim();
                    paragraph.Clear();
                }
            }
            if (paragraph.ToString().Trim() is { Length: > 0 } rest)
                yield return rest;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex Sentence = new(@"[^.!?…]+(?:[.!?…]+[""»”)]*\s*|$)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A Markdown link, its address between &lt; &gt; when it holds spaces or parentheses.</summary>
    private static string Link(string title, string url) =>
        $"[{Escape(Markup(title))}]({(url.IndexOfAny([' ', '(', ')']) >= 0 ? $"<{url}>" : url)})";

    /// <summary>The characters the tape reads as formatting (* ` [ ]) escaped in a text that is only text.</summary>
    private static string Markup(string text) =>
        text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("`", "\\`").Replace("[", "\\[").Replace("]", "\\]");

    private static readonly System.Text.RegularExpressions.Regex ParenthesisLink =
        new(@"\]\((https?://[^\s<>()]*\([^\s<>()]*\)[^\s<>()]*)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The width of the pictures of the pages in the document.</summary>
    public const int PictureWidth = 560;

    /// <summary>A picture of the tape: <c>![alt|width](path)</c>, the path between &lt; &gt; when it holds spaces or parentheses.</summary>
    public static string Picture(string path, string alt, int width)
    {
        var text = alt.Replace("]", string.Empty).Replace("[", string.Empty).Replace("|", " ").Trim();
        var link = path.IndexOfAny([' ', '(', ')']) >= 0 ? $"<{path}>" : path;
        return $"![{(text.Length > 80 ? text[..80] : text)}|{width}]({link})";
    }

    /// <summary>A short description of the document, for the list of the documents.</summary>
    public static string Description(CollectionPlanEntry plan, IReadOnlyList<ScrapedPage> pages) =>
        $"Analysis of the collection plan \"{plan.Name}\" by the web scraper: {pages.Count(p => p.Ok)} page(s) of {string.Join(", ", pages.Select(p => p.Website).Distinct())}, read from their pictures."
        + (string.IsNullOrWhiteSpace(plan.Instructions) ? string.Empty : $" Instructions: {Short(plan.Instructions!, 220)}");

    private static string Short(string text, int length)
    {
        var line = text.Replace('\n', ' ').Trim();
        return line.Length <= length ? line : line[..length] + "…";
    }

    /// <summary>Text that would be read as markup by the tape (&lt; &gt;) written as entities.</summary>
    private static string Escape(string text) => text.Replace("<", "&lt;").Replace(">", "&gt;");
}
