using Microsoft.Playwright;
using JobAutomation.Models;

namespace JobAutomation.Services;

public class IndeedScraperService : IJobBoardScraper
{
    public string BoardName => "Indeed";

    private readonly ILogger<IndeedScraperService> _logger;

    public IndeedScraperService(ILogger<IndeedScraperService> logger)
    {
        _logger = logger;
    }

    public async Task<List<ScoutedJob>> ScrapePostsAsync(List<string> keywords, CancellationToken cancellationToken = default)
    {
        var foundJobs = new List<ScoutedJob>();

        try
        {
            using var playwright = await Playwright.CreateAsync();

            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[] { "--disable-blink-features=AutomationControlled", "--no-sandbox" }
            });

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1366, Height = 768 }
            });

            var page = await context.NewPageAsync();
            await page.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

            // Indeed India searches — deduplicate by URL across keywords
            var seenUrls = new HashSet<string>();

            foreach (var keyword in keywords)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var encodedKeyword = Uri.EscapeDataString(keyword);
                // fromage=1 = posted in last 1 day; explvl=SENIOR_LEVEL targets experienced roles
                var searchUrl = $"https://in.indeed.com/jobs?q={encodedKeyword}&l=Bengaluru%2C+Karnataka&fromage=1&sort=date";

                _logger.LogInformation("Scraping Indeed India for: {Keyword}", keyword);

                try
                {
                    await page.GotoAsync(searchUrl, new PageGotoOptions { Timeout = 15000 });
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                    await Task.Delay(2000, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load Indeed search page for {Keyword}", keyword);
                    continue;
                }

                // Scroll a couple of times to load lazy content
                for (int s = 0; s < 2; s++)
                {
                    await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
                    await Task.Delay(1200, cancellationToken);
                }

                // Indeed job cards
                var jobCards = await page.QuerySelectorAllAsync(".job_seen_beacon, .tapItem, [data-testid='job-card']");
                _logger.LogInformation("Found {Count} Indeed job cards for '{Keyword}'.", jobCards.Count, keyword);

                foreach (var card in jobCards.Take(25))
                {
                    try
                    {
                        var titleEl = await card.QuerySelectorAsync("h2.jobTitle a, [data-testid='job-title'] a, .jcs-JobTitle");
                        var companyEl = await card.QuerySelectorAsync("[data-testid='company-name'], .companyName, .company");
                        var locationEl = await card.QuerySelectorAsync("[data-testid='text-location'], .companyLocation");
                        var descEl = await card.QuerySelectorAsync(".job-snippet, [data-testid='job-snippet']");

                        var titleText = titleEl != null ? (await titleEl.InnerTextAsync()).Trim() : "";
                        var companyText = companyEl != null ? (await companyEl.InnerTextAsync()).Trim() : "";
                        var locationText = locationEl != null ? (await locationEl.InnerTextAsync()).Trim() : "";
                        var descText = descEl != null ? (await descEl.InnerTextAsync()).Trim() : "";

                        if (string.IsNullOrWhiteSpace(titleText)) continue;

                        // Build job URL from data-jk attribute or href
                        var jk = await card.GetAttributeAsync("data-jk");
                        string jobUrl;
                        if (!string.IsNullOrEmpty(jk))
                        {
                            jobUrl = $"https://in.indeed.com/viewjob?jk={jk}";
                        }
                        else
                        {
                            var href = titleEl != null ? await titleEl.GetAttributeAsync("href") : null;
                            if (string.IsNullOrEmpty(href)) continue;
                            jobUrl = href.StartsWith("http") ? href : "https://in.indeed.com" + href;
                        }

                        if (!seenUrls.Add(jobUrl)) continue;

                        var rawText = $"{titleText} at {companyText} in {locationText}. {descText}";

                        foundJobs.Add(new ScoutedJob
                        {
                            LinkedInUrl = jobUrl,
                            RawText = rawText.Trim(),
                            KeywordMatched = keyword,
                            Board = BoardName
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to parse Indeed job card.");
                    }
                }

                await Task.Delay(3000, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indeed scraping failed.");
        }

        return foundJobs;
    }
}
