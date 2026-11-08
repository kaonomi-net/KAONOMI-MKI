using System.Diagnostics;
using KAONOMI.Agent.WebScrapper.Scraping;
using KAONOMI.Core;
using KAONOMI.Core.Helpers;
using KAONOMI.Core.Models;
using MARI.Framework.Core;
using MARI.Framework.Core.Abstractions;
using MARI.Framework.Core.Abstractions.Attributes;
using Microsoft.Extensions.Logging;

namespace KAONOMI.Agent.WebScrapper.Agents
{
    /// <summary>Collection stage: the URLs to visit (of the active collection plans, or of the command line).</summary>
    [VGrainCategory("Collection")]
    public interface iWebScrapperCollecVAgent : IVGrain
    {
        /// <summary>Input: a <see cref="ScrapeRequest"/> (JSON); output: the <see cref="ScrapeJob"/> list (JSON).</summary>
        Task<string> DoProcess(string input, IExecutionContext context);
    }

    /// <summary>Analysis stage: visits the pages, screenshots, pictures, paragraphs and entities.</summary>
    [VGrainCategory("Analysis")]
    public interface iWebScrapperAnalysisVAgent : IVGrain
    {
        /// <summary>Input: the <see cref="ScrapeJob"/> list (JSON); output: the <see cref="PageResult"/> list (JSON).</summary>
        Task<string> DoProcess(string input, IExecutionContext context);
    }

    /// <summary>Actions stage: saves the pages and their paragraphs in the search engine.</summary>
    [VGrainCategory("Actions")]
    public interface iWebScrapperActionsVAgent : IVGrain
    {
        /// <summary>Input: the <see cref="PageResult"/> list (JSON); output: a summary of what was saved.</summary>
        Task<string> DoProcess(string input, IExecutionContext context);
    }

    /// <summary>What the three agents share: the configuration, the session and the measure of their time.</summary>
    internal static class AgentRun
    {
        public static WebScrapperConfig LoadConfig() =>
            HelperConfigManager.GetFromFile<WebScrapperConfig>("config.json")
            ?? throw new InvalidOperationException("config.json could not be loaded.");

        public static async Task<string> MeasureAsync(string operation, Func<WebScrapperConfig, SessionManager, Task<string>> work)
        {
            var config = LoadConfig();
            var session = new SessionManager(config);
            var perf = new Performance
            {
                date = DateTime.Now,
                comment = $"{operation} execution time.",
                machineName = Environment.MachineName,
                userName = Environment.UserName,
                appName = config.appName,
                operationName = operation,
                metadatas = new List<MetaData>()
            };
            perf.startTime = perf.date;
            try
            {
                return await work(config, session);
            }
            finally
            {
                perf.endTime = DateTime.Now;
                perf.durationMilliseconds = (int)(perf.endTime - perf.startTime).TotalMilliseconds;
                session.PerformanceSave(perf);
            }
        }

        public static void Log(SessionManager session, WebScrapperConfig config, string message, string level = "INFO")
        {
            session.LogSave(message, config.appName, level);
            if (level == "ERROR")
                HelperConsoleTheme.Error(message);
            else
                HelperConsoleTheme.Info(message);
        }
    }

    /// <summary>
    /// The URLs to visit: those of the command line (in the mode asked), otherwise every URL of the websites of the
    /// active collection plans (not deleted, <c>isActive</c>), except the ones visited less than
    /// <see cref="WebScrapperConfig.revisitHours"/> ago.
    /// </summary>
    internal sealed class WebScrapperCollecVAgent : VGrainBase<iWebScrapperCollecVAgent>, iWebScrapperCollecVAgent
    {
        public WebScrapperCollecVAgent(ILogger<iWebScrapperCollecVAgent> logger) : base(logger)
        {
        }

        public Task<string> DoProcess(string input, IExecutionContext context) =>
            AgentRun.MeasureAsync("WebScrapperCollecVAgent.DoProcess", (config, session) =>
            {
                var request = SequenceJson.Read<ScrapeRequest>(input);
                var jobs = new List<ScrapeJob>();
                if (request.urls.Count > 0)
                {
                    jobs.AddRange(request.urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => new ScrapeJob
                    {
                        url = u.Trim(),
                        mode = request.mode,
                        keywords = request.keywords,
                        collectionPlanName = "(command line)"
                    }));
                    AgentRun.Log(session, config, $"{jobs.Count} URL(s) from the command line");
                    return Task.FromResult(SequenceJson.Write(jobs));
                }

                // One plan asked (--plan): visited now, active or not, its pages visited recently included.
                var asked = request.planId.Length > 0;
                List<CollectionPlan> plans;
                if (asked)
                {
                    var plan = session.CollectionPlanGet(request.planId);
                    if (plan is null || plan.isDeleted)
                    {
                        AgentRun.Log(session, config, $"Collection plan {request.planId} not found (or deleted)", "ERROR");
                        return Task.FromResult(SequenceJson.Write(jobs));
                    }
                    plans = new List<CollectionPlan> { plan };
                }
                else
                {
                    plans = session.CollectionPlansLast(500).Where(p => p.isActive && !p.isDeleted).ToList();
                }
                var skipped = 0;
                foreach (var plan in plans)
                    foreach (var website in plan.websites ?? new List<Website>())
                        foreach (var process in website.urlProcesses ?? new List<UrlProcess>())
                        {
                            if (string.IsNullOrWhiteSpace(process.url))
                                continue;
                            var last = asked ? null : SafeLastVisit(session, process.url.Trim());
                            if (last is not null && DateTime.Now - last.scrapedAt < TimeSpan.FromHours(config.revisitHours))
                            {
                                skipped++;
                                continue;
                            }
                            jobs.Add(new ScrapeJob
                            {
                                url = process.url.Trim(),
                                mode = string.IsNullOrWhiteSpace(process.mode) ? "fixed" : process.mode,
                                keywords = process.keywords ?? new List<string>(),
                                collectionPlanId = plan.id,
                                collectionPlanName = plan.name,
                                websiteName = website.name
                            });
                        }
                AgentRun.Log(session, config, asked
                    ? $"Collection plan \"{plans[0].name}\": {jobs.Count} URL(s) to visit"
                    : $"{plans.Count} active collection plan(s): {jobs.Count} URL(s) to visit, {skipped} visited less than {config.revisitHours} h ago");
                return Task.FromResult(SequenceJson.Write(jobs));
            });

        private static WebPage? SafeLastVisit(SessionManager session, string url)
        {
            try
            {
                return session.WebPageLastVisit(url);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Visits the URLs with the shared browser (<see cref="WebCrawler"/>), within the time budget of a run
    /// (<see cref="WebScrapperConfig.runBudgetMinutes"/>): the URLs not reached are left for the next run.
    /// </summary>
    internal sealed class WebScrapperAnalysisVAgent : VGrainBase<iWebScrapperAnalysisVAgent>, iWebScrapperAnalysisVAgent
    {
        public WebScrapperAnalysisVAgent(ILogger<iWebScrapperAnalysisVAgent> logger) : base(logger)
        {
        }

        public Task<string> DoProcess(string input, IExecutionContext context) =>
            AgentRun.MeasureAsync("WebScrapperAnalysisVAgent.DoProcess", async (config, session) =>
            {
                var jobs = SequenceJson.Read<List<ScrapeJob>>(input);
                var results = new List<PageResult>();
                if (jobs.Count == 0)
                    return SequenceJson.Write(results);

                var crawler = await WebCrawler.GetAsync(config);
                using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, config.runBudgetMinutes)));
                var watch = Stopwatch.StartNew();
                var done = 0;
                foreach (var job in jobs)
                {
                    if (budget.IsCancellationRequested)
                        break;
                    try
                    {
                        AgentRun.Log(session, config, $"Visiting {job.url} ({job.mode}{(job.collectionPlanName.Length > 0 ? ", " + job.collectionPlanName : "")})");
                        results.AddRange(await crawler.CrawlAsync(job, session, message => AgentRun.Log(session, config, message), budget.Token));
                        done++;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AgentRun.Log(session, config, $"Visit of {job.url} failed: {ex.Message}", "ERROR");
                    }
                }
                AgentRun.Log(session, config, $"{results.Count} page(s) visited from {done}/{jobs.Count} URL(s) in {watch.Elapsed.TotalSeconds:0} s"
                                              + (done < jobs.Count ? $" (time budget of {config.runBudgetMinutes} min reached, the rest next run)" : string.Empty));
                return SequenceJson.Write(results);
            });
    }

    /// <summary>Saves each page (index <c>webpage</c>) and its paragraphs with their entities (index <c>webparagraph</c>).</summary>
    internal sealed class WebScrapperActionsVAgent : VGrainBase<iWebScrapperActionsVAgent>, iWebScrapperActionsVAgent
    {
        public WebScrapperActionsVAgent(ILogger<iWebScrapperActionsVAgent> logger) : base(logger)
        {
        }

        public Task<string> DoProcess(string input, IExecutionContext context) =>
            AgentRun.MeasureAsync("WebScrapperActionsVAgent.DoProcess", (config, session) =>
            {
                var results = SequenceJson.Read<List<PageResult>>(input);
                int pages = 0, paragraphs = 0, failed = 0;
                foreach (var result in results)
                {
                    var saved = session.WebPageSave(result.page);
                    if (!saved.Success || saved.ReturnedId == "-1")
                    {
                        failed++;
                        AgentRun.Log(session, config, $"Page {result.page.url} not saved: {saved.Message}", "ERROR");
                        continue;
                    }
                    pages++;
                    foreach (var paragraph in result.paragraphs)
                    {
                        var p = session.WebParagraphSave(paragraph);
                        if (p.Success && p.ReturnedId != "-1")
                            paragraphs++;
                        else
                            failed++;
                    }
                }
                var summary = $"Saved {pages} page(s) and {paragraphs} paragraph(s)" + (failed > 0 ? $", {failed} failed" : string.Empty);
                if (results.Count > 0)
                    AgentRun.Log(session, config, summary);
                return Task.FromResult(summary);
            });
    }
}
