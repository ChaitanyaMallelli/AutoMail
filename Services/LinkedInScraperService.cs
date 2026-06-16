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
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
                // Needed so we can read each post's permalink from the clipboard after "Copy link to post".
                Permissions = new[] { "clipboard-read", "clipboard-write" }
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

                // LinkedIn's content search is now server-driven UI with randomized class names and NO
                // inline post URNs/permalinks. Each post is a role="listitem" containing an
                // [data-testid="expandable-text-box"]; these structural/testid hooks survive redesigns,
                // unlike the old obfuscated CSS classes. The permalink is fetched per-post via its
                // control menu ("Copy link to post") since it isn't present in the DOM.
                var postSelector = "div[role='listitem']:has([data-testid='expandable-text-box'])";

                // Actually WAIT for at least one result to render instead of a blind fixed delay.
                try
                {
                    await page.WaitForSelectorAsync(postSelector, new PageWaitForSelectorOptions { Timeout = 15000 });
                }
                catch (TimeoutException)
                {
                    // Nothing rendered within the window — fall through to the diagnostic capture below.
                }

                // Scroll down to load more posts until we have nearly 50 posts (up to a max of 10 scrolls to avoid hanging)
                int maxScrolls = 10;
                int scrollCount = 0;

                var postElements = await page.QuerySelectorAllAsync(postSelector);
                _logger.LogInformation("Initially found {Count} posts for keyword {Keyword}.", postElements.Count, keyword);

                if (postElements.Count == 0)
                {
                    // Capture exactly what LinkedIn served so we can see the real DOM / detect a wall or empty state,
                    // instead of guessing at class names. Files are overwritten each run, keyed by keyword.
                    await CaptureDebugStateAsync(page, keyword);
                    _logger.LogWarning(
                        "No search results found for {Keyword}. Saved screenshot + HTML to scrape-debug/ for inspection. " +
                        "Common causes: LinkedIn changed result class names, a 'restricted activity' wall, or a genuine empty result set.",
                        keyword);
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
                    try
                    {
                        // Post text — the stable testid hook.
                        var textElement = await element.QuerySelectorAsync("[data-testid='expandable-text-box']");
                        var rawText = textElement != null ? await textElement.InnerTextAsync() : "";
                        if (string.IsNullOrWhiteSpace(rawText)) continue;

                        // Permalink isn't in the DOM — get it via the post's control menu -> "Copy link to post".
                        var url = await GetPostUrlViaMenuAsync(page, element);
                        if (string.IsNullOrEmpty(url))
                        {
                            _logger.LogDebug("Could not resolve a permalink for a '{Keyword}' post; skipping it.", keyword);
                            continue;
                        }

                        foundJobs.Add(new ScoutedJob
                        {
                            LinkedInUrl = url,
                            RawText = rawText,
                            KeywordMatched = keyword,
                            Board = BoardName
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract a post for keyword {Keyword}; continuing.", keyword);
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

    /// <summary>
    /// Resolves a post's permalink the only way LinkedIn's new SDUI search exposes it: open the post's
    /// control menu, click "Copy link to post", and read the URL back from the clipboard.
    /// Returns null if the menu/link couldn't be found, leaving the page menu closed.
    /// </summary>
    private async Task<string?> GetPostUrlViaMenuAsync(IPage page, IElementHandle postElement)
    {
        try
        {
            // The "..." overflow button. aria-label is the stable hook: "Open control menu for post by {Author}".
            var menuButton = await postElement.QuerySelectorAsync("button[aria-label*='Open control menu']");
            if (menuButton == null) return null;

            await menuButton.ScrollIntoViewIfNeededAsync();
            await menuButton.ClickAsync();

            // The menu renders in a portal at page level (not inside the post element), so query the page.
            // Match the menu item by its visible text rather than a randomized class.
            var copyItem = page.GetByText("Copy link to post", new PageGetByTextOptions { Exact = false });
            await copyItem.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 4000 });
            await copyItem.First.ClickAsync();

            // LinkedIn shows a "Link copied" toast; the permalink is now on the clipboard.
            await Task.Delay(400);
            var url = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");

            if (!string.IsNullOrWhiteSpace(url) && url.Contains("linkedin.com"))
                return url.Trim();

            return null;
        }
        catch (Exception)
        {
            // Menu didn't open or item not found — make sure any open menu is dismissed before the next post.
            try { await page.Keyboard.PressAsync("Escape"); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Dumps the current page screenshot and HTML to a scrape-debug/ folder so the real, current
    /// LinkedIn DOM can be inspected when 0 results are found. This is the ground truth for updating
    /// selectors (which LinkedIn rotates frequently) or spotting a login/activity wall vs an empty result set.
    /// </summary>
    private async Task CaptureDebugStateAsync(IPage page, string keyword)
    {
        try
        {
            var dir = Path.Combine(Directory.GetCurrentDirectory(), "scrape-debug");
            Directory.CreateDirectory(dir);

            // Safe file name from the keyword
            var safe = string.Concat(keyword.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(dir, $"{safe}.png"),
                FullPage = true
            });

            var html = await page.ContentAsync();
            await File.WriteAllTextAsync(Path.Combine(dir, $"{safe}.html"), html);

            _logger.LogInformation("Saved debug capture for '{Keyword}' (url: {Url}) to {Dir}", keyword, page.Url, dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture debug state for keyword {Keyword}", keyword);
        }
    }
}
