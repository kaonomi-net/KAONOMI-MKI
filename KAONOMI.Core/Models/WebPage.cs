using System;
using System.Collections.Generic;

namespace KAONOMI.Core.Models
{
    /// <summary>
    /// A web page saved by the web scraper (KAONOMI.Agent.WebScrapper), index <c>webpage</c>: one document per visit, so
    /// the same URL visited again gives a new document. Its id names the screenshot of the page
    /// (<c>Pictures/kaonomi/webscrapper/&lt;yyyy-MM&gt;/&lt;id&gt;.png</c>); the pictures of the page are downloaded in
    /// <see cref="imagesFolder"/> (<c>…/&lt;yyyy-MM&gt;/&lt;full date&gt;/</c>). Its paragraphs, with their entities, are
    /// <see cref="WebParagraph"/> documents.
    /// </summary>
    public class WebPage : EntityBase, ISoftDeletable
    {
        /// <summary>The URL asked.</summary>
        public string url { get; set; } = string.Empty;

        /// <summary>The URL reached after the redirections.</summary>
        public string finalUrl { get; set; } = string.Empty;

        /// <summary>The URL of the collection plan this page comes from (the same as <see cref="url"/> for a start page).</summary>
        public string startUrl { get; set; } = string.Empty;

        /// <summary>How far from the start URL (0: the start URL itself).</summary>
        public int depth { get; set; }

        /// <summary>fixed, explore or random: the mode of the URL of the collection plan.</summary>
        public string mode { get; set; } = string.Empty;

        /// <summary>The keywords of the URL of the collection plan.</summary>
        public List<string> keywords { get; set; } = new List<string>();

        /// <summary>The keywords found in the text of the page.</summary>
        public List<string> keywordsFound { get; set; } = new List<string>();

        public string collectionPlanId { get; set; } = string.Empty;

        public string collectionPlanName { get; set; } = string.Empty;

        public string websiteName { get; set; } = string.Empty;

        /// <summary>The HTTP status of the page (0 when unknown).</summary>
        public int httpStatus { get; set; }

        /// <summary>The title of the HTML page.</summary>
        public string pageTitle { get; set; } = string.Empty;

        /// <summary>The language declared by the page (<c>html lang</c>), or empty.</summary>
        public string pageLanguage { get; set; } = string.Empty;

        /// <summary>The text of the page: its paragraphs one after the other.</summary>
        public string text { get; set; } = string.Empty;

        /// <summary>SHA-256 of <see cref="text"/>: two visits with the same hash saw the same content.</summary>
        public string contentHash { get; set; } = string.Empty;

        /// <summary>The full path of the screenshot of the page (PNG), or empty when it failed.</summary>
        public string screenshotPath { get; set; } = string.Empty;

        /// <summary>The folder where the pictures of the page are downloaded.</summary>
        public string imagesFolder { get; set; } = string.Empty;

        /// <summary>The pictures of the page and where they were downloaded.</summary>
        public List<WebPageImage> images { get; set; } = new List<WebPageImage>();

        /// <summary>The links of the page (absolute URLs), as found.</summary>
        public List<string> links { get; set; } = new List<string>();

        public int paragraphCount { get; set; }

        /// <summary>How many entities the NER found in all the paragraphs.</summary>
        public int entityCount { get; set; }

        /// <summary>When the page was visited.</summary>
        public DateTime scrapedAt { get; set; }

        /// <summary>How long the visit took, in milliseconds.</summary>
        public int durationMs { get; set; }

        /// <summary>What went wrong (screenshot, pictures, NER…), the page being saved anyway.</summary>
        public List<string> warnings { get; set; } = new List<string>();

        public bool isDeleted { get; set; }
    }

    /// <summary>A picture of a web page: where it comes from and where it was downloaded.</summary>
    public class WebPageImage
    {
        /// <summary>The absolute URL of the picture.</summary>
        public string url { get; set; } = string.Empty;

        /// <summary>The text of its <c>alt</c>, or empty.</summary>
        public string alt { get; set; } = string.Empty;

        /// <summary>The full path of the downloaded file, or empty when the download failed.</summary>
        public string path { get; set; } = string.Empty;

        public string contentType { get; set; } = string.Empty;

        public long bytes { get; set; }

        /// <summary>Why the download failed, or empty.</summary>
        public string error { get; set; } = string.Empty;
    }

    /// <summary>
    /// A paragraph of a web page saved by the web scraper, index <c>webparagraph</c>, with the entities the NER of the
    /// server (<c>POST /ner</c>) found in it.
    /// </summary>
    public class WebParagraph : EntityBase, ISoftDeletable
    {
        /// <summary>The id of its <see cref="WebPage"/>.</summary>
        public string pageId { get; set; } = string.Empty;

        public string url { get; set; } = string.Empty;

        /// <summary>Its rank in the page (0 for the first).</summary>
        public int index { get; set; }

        /// <summary>The HTML element it comes from (p, li, h2, blockquote…).</summary>
        public string element { get; set; } = string.Empty;

        public string text { get; set; } = string.Empty;

        /// <summary>The entities found in <see cref="text"/> (positions in the paragraph).</summary>
        public List<NerEntity> entities { get; set; } = new List<NerEntity>();

        /// <summary>The keywords of the collection plan found in the paragraph.</summary>
        public List<string> keywordsFound { get; set; } = new List<string>();

        public string collectionPlanId { get; set; } = string.Empty;

        public DateTime scrapedAt { get; set; }

        public bool isDeleted { get; set; }
    }
}
