using Microsoft.Playwright;
using JobAutomation.Models;

namespace JobAutomation.Services;

public class LinkedInScraperService : IJobBoardScraper
{
    public string BoardName => "LinkedIn";

    private readonly string _email;
    private readonly string _password;
    private readonly ILogger<LinkedInScraperService> _logger;

    public LinkedInScraperService(IConfiguration configuration, ILogger<LinkedInScraperService> logger)
    {
        _email = configuration["LinkedIn:Email"] ?? "";
        _password = configuration["LinkedIn:Password"] ?? "";
        _logger = logger;
    }

    public async Task<List<ScoutedJob>> ScrapePostsAsync(List<string> keywords, CancellationToken cancellationToken = default)
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
            
            // Launch browser visibly so we can see what LinkedIn is complaining about!
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false });
            
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
            });
            var page = await context.NewPageAsync();

            // Bypass basic WebDriver detection
            await page.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

            try
            {
                _logger.LogInformation("Logging into LinkedIn...");
                await page.GotoAsync("https://www.linkedin.com/login");
                
                // Wait for the page DOM to be ready
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                
                // Give it a moment to process potential automatic redirects (if a session exists)
                await Task.Delay(3000);
                
                if (!page.Url.Contains("feed") && !page.Url.Contains("checkpoint"))
                {
                    // LinkedIn frequently changes input IDs. We will try all common variations, ensuring we only target visible elements.
                    var emailSelector = "input#username:visible, input#session_key:visible, input[name='session_key']:visible, input[type='email']:visible, input[autocomplete='username']:visible";
                    var passSelector = "input#password:visible, input#session_password:visible, input[name='session_password']:visible, input[type='password']:visible, input[autocomplete='current-password']:visible";
                    
                    // Wait for at least one of these to appear
                    await page.WaitForSelectorAsync(emailSelector, new PageWaitForSelectorOptions { Timeout = 10000 });
                    
                    await page.FillAsync(emailSelector, _email);
                    await page.FillAsync(passSelector, _password);
                    
                    // Press Enter on the password field instead of looking for brittle submit buttons
                    await page.PressAsync(passSelector, "Enter");
                }
                
                // Wait for feed to load. Checks for redirect to feed or a checkpoint.
                _logger.LogInformation("Waiting for home feed to load...");
                var feedLoaded = false;
                for (int i = 0; i < 60; i++)
                {
                    if (page.Url.Contains("feed") || page.Url.Contains("checkpoint"))
                    {
                        feedLoaded = true;
                        break;
                    }
                    await Task.Delay(1000);
                }

                if (!feedLoaded)
                {
                    throw new TimeoutException("Failed to redirect to LinkedIn feed page or checkpoint after login.");
                }

                // If LinkedIn redirects us to a security checkpoint, pause and notify the user to solve it in the visible window
                if (page.Url.Contains("checkpoint"))
                {
                    _logger.LogWarning("LinkedIn security checkpoint detected. Please complete any verification/captcha in the opened browser window...");
                    for (int i = 0; i < 90; i++) // up to 90 seconds for checkpoint solving
                    {
                        if (page.Url.Contains("feed"))
                        {
                            _logger.LogInformation("Security checkpoint successfully completed!");
                            break;
                        }
                        await Task.Delay(1000);
                    }
                }

                // Settle delay to let the feed fully render
                await Task.Delay(5000);
                _logger.LogInformation("Login successful and feed loaded.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to login. Current URL: {Url}. Taking screenshot...", page.Url);
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
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                
                // Wait for SPA to render posts
                await Task.Delay(6000);

                // Scroll down to load more posts until we have nearly 50 posts (up to a max of 10 scrolls to avoid hanging)
                int maxScrolls = 10;
                int scrollCount = 0;
                
                // Broadened selectors for LinkedIn's changing DOM
                var postSelector = ".feed-shared-update-v2, .reusable-search__result-container, li.reusable-search__result-container, div.search-hit--post, .entity-result, div[data-urn*='activity']";
                var postElements = await page.QuerySelectorAllAsync(postSelector);
                _logger.LogInformation("Initially found {Count} posts for keyword {Keyword}.", postElements.Count, keyword);

                if (postElements.Count == 0)
                {
                    _logger.LogWarning("No search results found for {Keyword} using current selectors.", keyword);
                    continue;
                }

                while (postElements.Count < 50 && scrollCount < maxScrolls)
                {
                    _logger.LogInformation("Scrolling down to load more posts... (Current count: {Count}, Scroll: {Scroll}/{MaxScrolls})", postElements.Count, scrollCount + 1, maxScrolls);
                    
                    // Scroll to the bottom of the page
                    await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
                    
                    // Wait for new posts to load and render
                    await Task.Delay(2500);

                    var currentElements = await page.QuerySelectorAllAsync(postSelector);
                    
                    // If no new elements were loaded after a scroll, try a small jog/nudge to trigger lazy loading
                    if (currentElements.Count == postElements.Count)
                    {
                        await page.EvaluateAsync("window.scrollBy(0, -300)");
                        await Task.Delay(500);
                        await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
                        await Task.Delay(2000);
                        
                        currentElements = await page.QuerySelectorAllAsync(postSelector);
                        if (currentElements.Count == postElements.Count)
                        {
                            _logger.LogInformation("No more posts available to load for this search.");
                            break;
                        }
                    }

                    postElements = currentElements;
                    scrollCount++;
                }

                _logger.LogInformation("Finished loading posts for keyword {Keyword}. Total found: {Count}", keyword, postElements.Count);
                
                foreach (var element in postElements)
                {
                    // Extract post URN (unique ID) to build the URL
                    var dataUrn = await element.GetAttributeAsync("data-urn");
                    if (string.IsNullOrEmpty(dataUrn))
                    {
                        // Sometimes the urn is nested deeper inside the container
                        var childWithUrn = await element.QuerySelectorAsync("[data-urn]");
                        if (childWithUrn != null)
                        {
                            dataUrn = await childWithUrn.GetAttributeAsync("data-urn");
                        }
                    }
                    if (string.IsNullOrEmpty(dataUrn)) continue;

                    var url = $"https://www.linkedin.com/feed/update/{dataUrn}";

                    // Extract text (usually inside span with dir="ltr")
                    var textElement = await element.QuerySelectorAsync(".update-components-text span[dir='ltr'], .feed-shared-update-v2__description, .break-words, .entity-result__summary, .feed-shared-text");
                    var rawText = textElement != null ? await textElement.InnerTextAsync() : "";

                    if (!string.IsNullOrWhiteSpace(rawText))
                    {
                        foundJobs.Add(new ScoutedJob
                        {
                            LinkedInUrl = url,
                            RawText = rawText,
                            KeywordMatched = keyword,
                            Board = BoardName
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
