using Microsoft.Playwright;
using JobAutomation.Models;

namespace JobAutomation.Services;

public class LinkedInScraperService
{
    private readonly string _email;
    private readonly string _password;
    private readonly ILogger<LinkedInScraperService> _logger;

    public LinkedInScraperService(IConfiguration configuration, ILogger<LinkedInScraperService> logger)
    {
        _email = configuration["LinkedIn:Email"] ?? "";
        _password = configuration["LinkedIn:Password"] ?? "";
        _logger = logger;
    }

    public async Task<List<ScoutedJob>> ScrapePostsAsync(List<string> keywords)
    {
        var foundJobs = new List<ScoutedJob>();

        if (string.IsNullOrEmpty(_email) || string.IsNullOrEmpty(_password))
        {
            _logger.LogError("LinkedIn credentials not found in appsettings.");
            return foundJobs;
        }

        try
        {
            using var playwright = await Playwright.CreateAsync();
            
            // Launch headless browser (use headless: false if debugging)
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
            });
            var page = await context.NewPageAsync();

            try
            {
                _logger.LogInformation("Logging into LinkedIn...");
                await page.GotoAsync("https://www.linkedin.com/login");
                
                // Sometimes it's #username, sometimes it's #session_key
                var hasUsername = await page.QuerySelectorAsync("#username") != null;
                if (hasUsername)
                {
                    await page.FillAsync("#username", _email);
                    await page.FillAsync("#password", _password);
                }
                else
                {
                    await page.FillAsync("#session_key", _email);
                    await page.FillAsync("#session_password", _password);
                }
                
                await page.ClickAsync("button[type='submit']");
                
                // Wait for feed to load
                await page.WaitForSelectorAsync(".global-nav__me", new PageWaitForSelectorOptions { Timeout = 15000 });
                _logger.LogInformation("Login successful.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to login. Taking screenshot...");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = "login_error.png" });
                throw;
            }

            foreach (var keyword in keywords)
            {
                _logger.LogInformation("Searching LinkedIn Posts for: {Keyword}", keyword);
                var encodedKeyword = Uri.EscapeDataString(keyword);
                
                // Navigate to Content (Posts) Search, filtered to past 24 hours
                var searchUrl = $"https://www.linkedin.com/search/results/content/?datePosted=%22past-24h%22&keywords={encodedKeyword}";
                await page.GotoAsync(searchUrl);

                // Wait for the search results to populate
                try
                {
                    await page.WaitForSelectorAsync(".search-results-container", new PageWaitForSelectorOptions { Timeout = 10000 });
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("No search results found for {Keyword}", keyword);
                    continue;
                }

                // Give it a moment to render posts
                await Task.Delay(3000);

                // Extract post items. The structure is usually .feed-shared-update-v2
                var postElements = await page.QuerySelectorAllAsync(".feed-shared-update-v2");
                
                foreach (var element in postElements)
                {
                    // Extract post URN (unique ID) to build the URL
                    var dataUrn = await element.GetAttributeAsync("data-urn");
                    if (string.IsNullOrEmpty(dataUrn)) continue;

                    var url = $"https://www.linkedin.com/feed/update/{dataUrn}";

                    // Extract text (usually inside span with dir="ltr")
                    var textElement = await element.QuerySelectorAsync(".update-components-text span[dir='ltr']");
                    var rawText = textElement != null ? await textElement.InnerTextAsync() : "";

                    if (!string.IsNullOrWhiteSpace(rawText))
                    {
                        foundJobs.Add(new ScoutedJob
                        {
                            LinkedInUrl = url,
                            RawText = rawText,
                            KeywordMatched = keyword
                        });
                    }
                }
                
                // Random human-like delay between searches
                await Task.Delay(new Random().Next(3000, 7000));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scraping LinkedIn posts");
            try 
            {
                // Take a screenshot of whatever page LinkedIn is currently serving to debug why it can't find #username
                if (System.IO.File.Exists("playwright-error.png")) System.IO.File.Delete("playwright-error.png");
                // We don't have the 'page' variable in this scope, so we can't take a screenshot here easily.
                // Let me move this logic inside the try block.
            } catch {}
        }

        return foundJobs;
    }
}
