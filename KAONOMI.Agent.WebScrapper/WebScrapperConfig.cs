using KAONOMI.Core.Models;

namespace KAONOMI.Agent.WebScrapper
{
    /// <summary>
    /// config.json of the web scraper: the settings of every Kaonomi host (<see cref="Config"/>) and how the pages are
    /// visited.
    /// </summary>
    public sealed class WebScrapperConfig : Config
    {
        /// <summary>Minutes between two runs over the collection plans.</summary>
        public int scrapeIntervalMinutes { get; set; } = 60;

        /// <summary>A page visited less than this many hours ago is not visited again.</summary>
        public double revisitHours { get; set; } = 24;

        /// <summary>Most pages visited from one URL of a collection plan (explore mode).</summary>
        public int maxPagesPerUrl { get; set; } = 20;

        /// <summary>How many links away from the start URL the explore mode goes.</summary>
        public int maxDepth { get; set; } = 2;

        /// <summary>How many links of the start page the random mode visits.</summary>
        public int randomPages { get; set; } = 5;

        /// <summary>Most pictures downloaded for one page.</summary>
        public int maxImagesPerPage { get; set; } = 40;

        /// <summary>Smaller pictures (icons, tracking pixels) are not kept.</summary>
        public int minImageBytes { get; set; } = 2048;

        /// <summary>Shorter blocks of text are not paragraphs (headings excepted).</summary>
        public int minParagraphLength { get; set; } = 40;

        public int pageTimeoutSeconds { get; set; } = 45;

        /// <summary>
        /// Minutes the visits of one run may take (a grain call fails after 10 minutes): the URLs not reached are left
        /// for the next run.
        /// </summary>
        public double runBudgetMinutes { get; set; } = 8;

        /// <summary>Pause between two pages of the same run, to be polite with the sites.</summary>
        public int delayBetweenPagesMs { get; set; } = 1500;

        /// <summary>Leaves out the pages that the robots.txt of the site disallows.</summary>
        public bool respectRobotsTxt { get; set; } = true;

        /// <summary>The installed browser Playwright drives (msedge, chrome), or empty for the browser of Playwright.</summary>
        public string browserChannel { get; set; } = "msedge";

        public string userAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 KaonomiWebScrapper/1.0";

        /// <summary>Root of the screenshots and pictures; empty: Pictures/kaonomi/webscrapper.</summary>
        public string outputFolder { get; set; } = string.Empty;

        /// <summary>The root folder of the screenshots and pictures.</summary>
        public string ResolvedOutputFolder => string.IsNullOrWhiteSpace(outputFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "kaonomi", "webscrapper")
            : outputFolder;

        /// <summary>The URL of the Kaonomi server (for <c>POST /ner</c>).</summary>
        public string ServerUrl => string.IsNullOrWhiteSpace(dialogServerUrl) ? "http://localhost:49166" : dialogServerUrl.TrimEnd('/');
    }
}
