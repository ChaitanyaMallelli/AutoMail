using System.Text.RegularExpressions;
using Microsoft.Playwright;
using JobAutomation.Models;

namespace JobAutomation.Services;

public class LinkedInScraperService : IJobBoardScraper
{
    public string BoardName => "LinkedIn";

    private readonly string _email;
    private readonly string _password;
    private readonly int _maxPostsPerKeyword;
    private readonly ILogger<LinkedInScraperService> _logger;

    /// <summary>
    /// Persistent profile directory so LinkedIn session cookies survive across runs.
    /// </summary>
    private static readonly string SessionDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoMail", "linkedin-session");

    public LinkedInScraperService(IConfiguration configuration, ILogger<LinkedInScraperService> logger)
    {
        _email = configuration["LinkedIn:Email"] ?? "";
        _password = configuration["LinkedIn:Password"] ?? "";
        _maxPostsPerKeyword = configuration.GetValue<int?>("LinkedIn:MaxPostsPerKeyword") ?? 30;
        _logger = logger;
    }

    public async Task<List<ScoutedJob>> ScrapePostsAsync(List<string> keywords, CancellationToken cancellationToken = default)
    {
        var foundJobs = new List<ScoutedJob>();

        if (string.IsNullOrEmpty(_email) || string.IsNullOrEmpty(_password))
        {
            _logger.LogError("LinkedIn credentials not found in configuration.");
            return foundJobs;
        }

        IPlaywright? playwright = null;
        IBrowserContext? context = null;

        try
        {
            playwright = await Playwright.CreateAsync();
            context = await CreatePersistentContextAsync(playwright);
            var page = await GetOrCreatePageAsync(context);

            await EnsureLoggedInAsync(page);

            foreach (var keyword in keywords)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    // Ensure page is still alive before each keyword
                    page = await GetOrCreatePageAsync(context);

                    _logger.LogInformation("Searching LinkedIn Posts for: {Keyword}", keyword);
                    var encodedKeyword = Uri.EscapeDataString(keyword);
                    var targetPostsForKeyword = Math.Max(1, _maxPostsPerKeyword);

                    var searchUrl = $"https://www.linkedin.com/search/results/content/?datePosted=%22past-24h%22&sortBy=%22date_posted%22&keywords={encodedKeyword}";
                    await page.GotoAsync(searchUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 45000 });
                    await Task.Delay(4000);

                    // Re-auth guard if redirected to login
                    if (!IsLoggedIn(page.Url))
                    {
                        _logger.LogWarning("LinkedIn session expired during search for '{Keyword}' — re-logging in...", keyword);
                        await EnsureLoggedInAsync(page);
                        await page.GotoAsync(searchUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 45000 });
                        await Task.Delay(4000);

                        if (!IsLoggedIn(page.Url))
                        {
                            _logger.LogError("Could not re-establish LinkedIn session. Skipping keyword '{Keyword}'.", keyword);
                            await CaptureDebugStateAsync(page, keyword);
                            continue;
                        }
                    }

                    var candidateSelectors = new[]
                    {
                        "div[role='listitem']:has([data-testid='expandable-text-box'])",
                        "div[role='listitem']:has(.feed-shared-update-v2)",
                        "div[role='listitem']",
                        "li[role='listitem']",
                        "div.feed-shared-update-v2",
                        "article"
                    };

                    string? postSelector = null;
                    foreach (var selector in candidateSelectors)
                    {
                        var matches = await page.QuerySelectorAllAsync(selector);
                        if (matches.Count > 0)
                        {
                            postSelector = selector;
                            _logger.LogInformation("Using selector '{Selector}' for keyword '{Keyword}' ({Count} initial candidates).", selector, keyword, matches.Count);
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(postSelector))
                    {
                        await CaptureDebugStateAsync(page, keyword);
                        _logger.LogWarning("No result nodes matched for '{Keyword}'. Saved debug capture.", keyword);
                        continue;
                    }

                    var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int keptBefore = foundJobs.Count;
                    int emptyRounds = 0;
                    int maxScrolls = 60;

                    for (int scroll = 0; scroll <= maxScrolls; scroll++)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        var elements = await page.QuerySelectorAllAsync(postSelector);
                        int newThisRound = 0;

                        foreach (var element in elements)
                        {
                            try
                            {
                                var rawText = await TryReadPostTextAsync(element);
                                if (string.IsNullOrWhiteSpace(rawText) || rawText.Length < 20) continue;

                                var key = rawText.Trim();
                                if (key.Length > 200) key = key.Substring(0, 200);
                                if (!seenKeys.Add(key)) continue;

                                var url = await ResolvePostUrlAsync(page, element);
                                if (string.IsNullOrEmpty(url))
                                {
                                    _logger.LogDebug("Could not resolve valid post permalink for post '{Text}...'; skipping.", key.Substring(0, Math.Min(40, key.Length)));
                                    continue;
                                }

                                if (!seenUrls.Add(url)) continue;

                                foundJobs.Add(new ScoutedJob
                                {
                                    LinkedInUrl = url,
                                    RawText = rawText,
                                    KeywordMatched = keyword,
                                    Board = BoardName
                                });
                                newThisRound++;

                                if (foundJobs.Count - keptBefore >= targetPostsForKeyword)
                                {
                                    _logger.LogInformation("Reached target count of {Target} posts for keyword '{Keyword}'.", targetPostsForKeyword, keyword);
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to extract a post for '{Keyword}'.", keyword);
                            }
                        }

                        int kept = foundJobs.Count - keptBefore;
                        _logger.LogInformation("Keyword '{Keyword}' - scroll {Scroll}/{Max}: +{New} new, {Kept} total collected.", keyword, scroll, maxScrolls, newThisRound, kept);

                        if (kept >= targetPostsForKeyword) break;

                        if (newThisRound == 0)
                        {
                            emptyRounds++;
                            if (emptyRounds >= 5)
                            {
                                _logger.LogInformation("No new posts after {Rounds} consecutive scrolls - stopping for '{Keyword}'.", emptyRounds, keyword);
                                break;
                            }
                        }
                        else
                        {
                            emptyRounds = 0;
                        }

                        // ── Trigger infinite scroll reliably ──
                        try
                        {
                            if (elements.Count > 0)
                            {
                                await elements.Last().ScrollIntoViewIfNeededAsync();
                            }
                        }
                        catch { }

                        await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
                        await page.Keyboard.PressAsync("PageDown");
                        await Task.Delay(1000);
                        await page.Keyboard.PressAsync("PageDown");
                        await Task.Delay(2000);

                        // Check for "Show more results" button
                        try
                        {
                            var showMoreBtn = await page.QuerySelectorAsync("button:has-text('Show more results'), button:has-text('See more posts'), button.artdeco-button--muted");
                            if (showMoreBtn != null && await showMoreBtn.IsVisibleAsync())
                            {
                                await showMoreBtn.ClickAsync();
                                await Task.Delay(2000);
                            }
                        }
                        catch { }
                    }

                    _logger.LogInformation("Finished keyword '{Keyword}'. Total kept: {Count}", keyword, foundJobs.Count - keptBefore);
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
                {
                    _logger.LogWarning("Playwright error during keyword '{Keyword}': {Message}. Attempting to recover page...", keyword, ex.Message);
                    try
                    {
                        page = await GetOrCreatePageAsync(context);
                    }
                    catch (Exception recoveryEx)
                    {
                        _logger.LogError(recoveryEx, "Failed to recover page/context. Aborting remaining keywords.");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scraping keyword '{Keyword}'.", keyword);
                }

                // Random delay between searches
                try { await Task.Delay(new Random().Next(4000, 8000), cancellationToken); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scraping LinkedIn posts");
        }
        finally
        {
            if (context != null)
            {
                try { await context.CloseAsync(); } catch { }
            }
            if (playwright != null)
            {
                try { playwright.Dispose(); } catch { }
            }
        }

        return foundJobs;
    }

    // ─── Browser / Context Helpers ───────────────────────────────────────────────

    private static async Task<IBrowserContext> CreatePersistentContextAsync(IPlaywright playwright)
    {
        Directory.CreateDirectory(SessionDir);
        return await playwright.Chromium.LaunchPersistentContextAsync(SessionDir, new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = false,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
            Permissions = new[] { "clipboard-read", "clipboard-write" },
            Args = new[] { "--disable-blink-features=AutomationControlled" }
        });
    }

    private async Task<IPage> GetOrCreatePageAsync(IBrowserContext context)
    {
        foreach (var p in context.Pages)
        {
            if (!p.IsClosed)
            {
                return p;
            }
        }

        var newPage = await context.NewPageAsync();
        await newPage.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");
        return newPage;
    }

    // ─── Login Flow ──────────────────────────────────────────────────────────────

    private async Task EnsureLoggedInAsync(IPage page)
    {
        _logger.LogInformation("Checking LinkedIn login state...");

        await page.GotoAsync("https://www.linkedin.com/feed/", new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30000 });
        await Task.Delay(3000);

        if (IsLoggedIn(page.Url))
        {
            try
            {
                var hasFeedDom = await page.QuerySelectorAsync(".global-nav, .scaffold-layout, [data-testid='home-feed'], div.feed-shared-update-v2");
                if (hasFeedDom != null)
                {
                    _logger.LogInformation("✅ Already logged in from persistent session. URL: {Url}", page.Url);
                    return;
                }
            }
            catch (PlaywrightException) { }
        }

        _logger.LogInformation("Logging into LinkedIn...");
        await page.GotoAsync("https://www.linkedin.com/login", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Task.Delay(3000);

        if (IsLoggedIn(page.Url))
        {
            _logger.LogInformation("✅ Auto-redirected to logged-in state. URL: {Url}", page.Url);
            return;
        }

        if (!page.Url.Contains("checkpoint", StringComparison.OrdinalIgnoreCase))
        {
            var emailSelector = "input#username:visible, input#session_key:visible, input[name='session_key']:visible, input[type='email']:visible, input[autocomplete='username']:visible";
            var passSelector = "input#password:visible, input#session_password:visible, input[name='session_password']:visible, input[type='password']:visible, input[autocomplete='current-password']:visible";

            try
            {
                await page.WaitForSelectorAsync(emailSelector, new PageWaitForSelectorOptions { Timeout = 10000 });
                await page.FillAsync(emailSelector, _email);
                await page.FillAsync(passSelector, _password);

                var signInBtn = await page.QuerySelectorAsync("button[type='submit']:visible, button[data-litms-control-urn*='login-submit']:visible");
                if (signInBtn != null)
                {
                    await signInBtn.ClickAsync();
                }
                else
                {
                    await page.PressAsync(passSelector, "Enter");
                }

                try
                {
                    await page.WaitForURLAsync(url => !url.Contains("/login") || url.Contains("feed") || url.Contains("checkpoint"), new PageWaitForURLOptions { Timeout = 30000 });
                }
                catch (TimeoutException) { }
            }
            catch (TimeoutException)
            {
                _logger.LogError("Could not find login form fields.");
                try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = "login_error.png" }); } catch { }
                throw;
            }
        }

        // Wait for feed or checkpoint
        var feedLoaded = false;
        var hitCheckpoint = false;
        for (int i = 0; i < 60; i++)
        {
            var currentUrl = page.Url;

            if (currentUrl.Contains("checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                hitCheckpoint = true;
                feedLoaded = true;
                _logger.LogInformation("Detected checkpoint at URL: {Url} (after {Seconds}s)", currentUrl, i);
                break;
            }

            if (IsLoggedIn(currentUrl))
            {
                feedLoaded = true;
                _logger.LogInformation("Detected logged-in state at URL: {Url} (after {Seconds}s)", currentUrl, i);
                break;
            }

            if (i > 5)
            {
                try
                {
                    var hasFeed = await page.QuerySelectorAsync("div.feed-shared-update-v2, [data-testid='home-feed'], .scaffold-layout, .global-nav");
                    if (hasFeed != null)
                    {
                        feedLoaded = true;
                        break;
                    }
                }
                catch (PlaywrightException) { }
            }

            await Task.Delay(1000);
        }

        if (!feedLoaded)
        {
            _logger.LogError("Failed to reach feed/checkpoint after 60s. URL: {Url}", page.Url);
            try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = "login_error.png" }); } catch { }
            throw new TimeoutException($"Failed to reach LinkedIn feed after login. URL: {page.Url}");
        }

        if (hitCheckpoint || (page.Url.Contains("checkpoint", StringComparison.OrdinalIgnoreCase) && !IsLoggedIn(page.Url)))
        {
            _logger.LogWarning("⚠️ Security checkpoint detected! Please complete verification in the browser window...");
            for (int i = 0; i < 120; i++)
            {
                var checkUrl = page.Url;
                if (IsLoggedIn(checkUrl) && !checkUrl.Contains("checkpoint", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("✅ Checkpoint cleared! URL: {Url}", checkUrl);
                    break;
                }
                await Task.Delay(1000);
            }

            if (!IsLoggedIn(page.Url) || page.Url.Contains("checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Checkpoint not cleared within 120s. URL: {Url}", page.Url);
                throw new TimeoutException("LinkedIn checkpoint not cleared in time.");
            }
        }

        await Task.Delay(4000);
        _logger.LogInformation("✅ Login successful — feed loaded. URL: {Url}", page.Url);
    }

    // ─── URL / Logged In Check ───────────────────────────────────────────────────

    private static bool IsLoggedIn(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        var loginPaths = new[] { "/login", "/uas/login", "/checkpoint/challenge", "/checkpoint/lg/login-submit" };
        var uri = new Uri(url, UriKind.RelativeOrAbsolute);
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath.TrimEnd('/') : url;

        foreach (var lp in loginPaths)
        {
            if (path.Equals(lp, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var loggedInIndicators = new[] { "feed", "home", "mynetwork", "jobs", "messaging", "notifications", "search" };
        foreach (var ind in loggedInIndicators)
        {
            if (url.Contains(ind, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return url.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("/login", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("/checkpoint", StringComparison.OrdinalIgnoreCase);
    }

    // ─── Post Text Extraction ────────────────────────────────────────────────────

    private async Task<string> TryReadPostTextAsync(IElementHandle element)
    {
        var candidates = new[]
        {
            "[data-testid='expandable-text-box']",
            "[data-testid='feed-activity-card']",
            ".feed-shared-update-v2__description-wrapper",
            ".feed-shared-text",
            ".feed-shared-inline-show-more-text",
            "div[class*='update-components-text']",
            "div[class*='text-body']",
            "span[class*='text-body']",
            "p"
        };

        foreach (var selector in candidates)
        {
            try
            {
                var target = await element.QuerySelectorAsync(selector);
                if (target != null)
                {
                    var text = await target.InnerTextAsync();
                    if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length >= 20)
                        return text.Trim();
                }
            }
            catch (PlaywrightException) { }
        }

        return string.Empty;
    }

    // ─── Post Permalink Extraction ───────────────────────────────────────────────

    private async Task<string?> ResolvePostUrlAsync(IPage page, IElementHandle postElement)
    {
        // 1. Check direct post permalink anchors (IGNORE profile / company / group links)
        var postLinkSelectors = new[]
        {
            "a[href*='/feed/update/urn:li:activity:']",
            "a[href*='/feed/update/urn:li:share:']",
            "a[href*='/feed/update/urn:li:ugcPost:']",
            "a[href*='/feed/update/']",
            "a[href*='/posts/']",
            "a.update-components-actor__sub-description-link",
            "a.feed-shared-actor__sub-description-link",
            "a[href*='activity-']",
            "a[href*='urn:li:activity']"
        };

        foreach (var selector in postLinkSelectors)
        {
            try
            {
                var anchor = await postElement.QuerySelectorAsync(selector);
                if (anchor != null)
                {
                    var href = await anchor.GetAttributeAsync("href");
                    if (!string.IsNullOrWhiteSpace(href) && IsValidPostUrl(href))
                    {
                        return CleanPostUrl(href);
                    }
                }
            }
            catch (PlaywrightException) { }
        }

        // 2. Check for URN attributes on the container or children
        try
        {
            var urn = await postElement.GetAttributeAsync("data-urn")
                   ?? await postElement.GetAttributeAsync("data-id")
                   ?? await postElement.GetAttributeAsync("data-chameleon-result-urn")
                   ?? await postElement.GetAttributeAsync("data-activity-urn");

            if (!string.IsNullOrEmpty(urn))
            {
                var match = Regex.Match(urn, @"urn:li:(activity|share|ugcPost):(\d+)");
                if (match.Success)
                {
                    return $"https://www.linkedin.com/feed/update/{match.Value}/";
                }
            }
        }
        catch (PlaywrightException) { }

        // 3. Fallback: Click "..." Menu -> "Copy link to post" -> Read clipboard
        return await GetPostUrlViaMenuAsync(page, postElement);
    }

    private async Task<string?> GetPostUrlViaMenuAsync(IPage page, IElementHandle postElement)
    {
        var menuButtonSelectors = new[]
        {
            "button[aria-label*='Open control menu']",
            "button[aria-label*='control menu']",
            "button[aria-label*='More actions']",
            "button[aria-label*='More options']",
            "button[aria-label*='options for this update']",
            "button.feed-shared-control-menu__trigger",
            "button.artdeco-dropdown__trigger",
            "div.feed-shared-control-menu button"
        };

        try
        {
            IElementHandle? menuBtn = null;
            foreach (var sel in menuButtonSelectors)
            {
                menuBtn = await postElement.QuerySelectorAsync(sel);
                if (menuBtn != null) break;
            }

            if (menuBtn == null) return null;

            await menuBtn.ScrollIntoViewIfNeededAsync();
            await menuBtn.ClickAsync();
            await Task.Delay(400);

            // Click copy item
            var copyItem = page.Locator("text='Copy link to post', text='Copy link'").First;
            await copyItem.WaitForAsync(new LocatorWaitForOptions { Timeout = 3000 });
            await copyItem.ClickAsync();

            await Task.Delay(500);
            var url = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");

            // Dismiss menu
            try { await page.Keyboard.PressAsync("Escape"); } catch { }

            if (!string.IsNullOrWhiteSpace(url) && IsValidPostUrl(url))
            {
                return CleanPostUrl(url);
            }

            return null;
        }
        catch (Exception)
        {
            try { await page.Keyboard.PressAsync("Escape"); } catch { }
            return null;
        }
    }

    private static bool IsValidPostUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Reject profile, company, school, and generic group links
        if (url.Contains("/in/", StringComparison.OrdinalIgnoreCase) && !url.Contains("activity", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Contains("/company/", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Contains("/school/", StringComparison.OrdinalIgnoreCase)) return false;

        return url.Contains("/feed/update/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/posts/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("urn:li:activity", StringComparison.OrdinalIgnoreCase)
            || url.Contains("urn:li:share", StringComparison.OrdinalIgnoreCase)
            || url.Contains("urn:li:ugcPost", StringComparison.OrdinalIgnoreCase)
            || url.Contains("highlightedUpdateUrn", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanPostUrl(string href)
    {
        if (href.StartsWith("/"))
        {
            href = "https://www.linkedin.com" + href;
        }

        // Clean tracking params unless it contains highlightedUpdateUrn
        var qIdx = href.IndexOf('?');
        if (qIdx > 0 && !href.Contains("highlightedUpdateUrn", StringComparison.OrdinalIgnoreCase))
        {
            href = href.Substring(0, qIdx);
        }

        return href.Trim();
    }

    // ─── Diagnostic Capture ──────────────────────────────────────────────────────

    private async Task CaptureDebugStateAsync(IPage page, string keyword)
    {
        try
        {
            var dir = Path.Combine(Directory.GetCurrentDirectory(), "scrape-debug");
            Directory.CreateDirectory(dir);

            var safe = string.Concat(keyword.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(dir, $"{safe}.png"),
                FullPage = true
            });

            var html = await page.ContentAsync();
            await File.WriteAllTextAsync(Path.Combine(dir, $"{safe}.html"), html);

            _logger.LogInformation("Saved debug capture for '{Keyword}' to {Dir}", keyword, dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture debug state for keyword {Keyword}", keyword);
        }
    }
}
