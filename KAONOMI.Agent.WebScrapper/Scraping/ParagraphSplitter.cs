using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace KAONOMI.Agent.WebScrapper.Scraping
{
    /// <summary>A block of text of a page: its text and the HTML element it comes from.</summary>
    public sealed record Paragraph(string Element, string Text);

    /// <summary>
    /// Cuts the HTML of a page into paragraphs: the text blocks (p, li, blockquote, headings, dd, figcaption, pre) out of
    /// the parts that are not content (scripts, styles, menus, headers and footers, forms). A block that holds another
    /// block keeps only the inner ones; blocks shorter than the minimum length are left out (headings of ten characters
    /// or more are kept), and a text met twice is kept once.
    /// </summary>
    public static class ParagraphSplitter
    {
        private static readonly string[] NotContent =
            { "script", "style", "noscript", "nav", "footer", "header", "aside", "form", "svg", "iframe", "button", "select", "template" };

        private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
            { "p", "li", "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "dd", "figcaption", "pre" };

        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

        public static List<Paragraph> Split(string html, int minLength)
        {
            var document = new HtmlDocument();
            document.LoadHtml(html ?? string.Empty);
            var root = document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;

            foreach (var name in NotContent)
                foreach (var node in root.SelectNodes($".//{name}")?.ToList() ?? new List<HtmlNode>())
                    node.Remove();
            // Hidden parts (cookie banners, menus closed) are not content either.
            foreach (var node in root.SelectNodes(".//*[@aria-hidden='true' or @hidden]")?.ToList() ?? new List<HtmlNode>())
                node.Remove();

            var result = new List<Paragraph>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in root.Descendants().Where(n => n.NodeType == HtmlNodeType.Element && Blocks.Contains(n.Name)))
            {
                // The innermost blocks only: a li holding p keeps the p.
                if (node.Descendants().Any(d => d.NodeType == HtmlNodeType.Element && Blocks.Contains(d.Name)))
                    continue;

                var text = Spaces.Replace(HtmlEntity.DeEntitize(node.InnerText) ?? string.Empty, " ").Trim();
                var isHeading = node.Name.Length == 2 && node.Name[0] == 'h' && char.IsDigit(node.Name[1]);
                if (text.Length < (isHeading ? 10 : minLength))
                    continue;
                if (seen.Add(text))
                    result.Add(new Paragraph(node.Name.ToLowerInvariant(), text));
            }
            return result;
        }
    }
}
