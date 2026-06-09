using Microsoft.Playwright;
using Newtonsoft.Json;
using JobAutomation.Models;

namespace JobAutomation.Services;

public class NaukriScraperService : IJobBoardScraper
{
    public string BoardName => "Naukri";

    private readonly IConfiguration _configuration;
    private readonly ILogger<NaukriScraperService> _logger;

    private static readonly string SessionDir = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Sessions");
    private static readonly string SessionFile = Path.Combine(SessionDir, "naukri_session.json");

    public NaukriScraperService(IConfiguration configuration, ILogger<NaukriScraperService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<ScoutedJob>> ScrapePostsAsync(List<string> keywords, CancellationToken cancellationToken = default)
    {
        var foundJobs = new List<ScoutedJob>();

        try
        {
            Directory.CreateDirectory(SessionDir);
            using var playwright = await Playwright.CreateAsync();

            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                Args = new[] { "--disable-blink-features=AutomationControlled" }
            });

            BrowserNewContextOptions contextOptions = new()
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
            };

            // Load saved session if it exists
            if (File.Exists(SessionFile))
            {
                var cookieJson = await File.ReadAllTextAsync(SessionFile, cancellationToken);
                var cookies = JsonConvert.DeserializeObject<List<Cookie>>(cookieJson);
                if (cookies != null)
                    contextOptions.StorageStatePath = null; // cookies injected after context creation
            }

            var context = await browser.NewContextAsync(contextOptions);

            // Inject saved cookies if available
            if (File.Exists(SessionFile))
            {
                try
                {
                    var cookieJson = await File.ReadAllTextAsync(SessionFile, cancellationToken);
                    var rawCookies = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(cookieJson);
                    if (rawCookies != null)
                    {
                        var cookies = rawCookies.Select(c => new Cookie
                        {
                            Name = c.GetValueOrDefault("name")?.ToString() ?? "",
                            Value = c.GetValueOrDefault("value")?.ToString() ?? "",
                            Domain = c.GetValueOrDefault("domain")?.ToString() ?? ".naukri.com",
                            Path = c.GetValueOrDefault("path")?.ToString() ?? "/",
                            HttpOnly = c.ContainsKey("httpOnly") && bool.TryParse(c["httpOnly"]?.ToString(), out var h) && h,
                            Secure = c.ContainsKey("secure") && bool.TryParse(c["secure"]?.ToString(), out var s) && s,
                        }).ToList();
                        await context.AddCookiesAsync(cookies);
                        _logger.LogInformation("Loaded {Count} Naukri session cookies.", cookies.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load Naukri session cookies. Will require fresh login.");
                }
            }

            var page = await context.NewPageAsync();
            await page.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

            // Check if session is still valid
            await page.GotoAsync("https://www.naukri.com/");
            await Task.Delay(3000, cancellationToken);

            var isLoggedIn = await IsLoggedInAsync(page);

            if (!isLoggedIn)
            {
                _logger.LogWarning("Naukri session expired or not found. Opening browser for manual login...");
                _logger.LogInformation("Please log in to Naukri in the browser window. The app will continue automatically once you are logged in.");

                await page.GotoAsync("https://www.naukri.com/nlogin/login");
                await Task.Delay(3000, cancellationToken);

                // Wait up to 3 minutes for user to log in
                for (int i = 0; i < 180; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (await IsLoggedInAsync(page))
                    {
                        _logger.LogInformation("Naukri login detected. Saving session...");
                        await SaveSessionAsync(context);
                        break;
                    }
                    await Task.Delay(1000, cancellationToken);
                }

                if (!await IsLoggedInAsync(page))
                {
                    _logger.LogError("Naukri login timed out. Skipping Naukri scraping.");
                    return foundJobs;
                }
            }

            _logger.LogInformation("Naukri session active. Starting job scraping...");

            foreach (var keyword in keywords)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var encodedKeyword = keyword.Replace(" ", "-").ToLower();
                var searchUrl = $"https://www.naukri.com/{encodedKeyword}-jobs-in-bangalore?experience=3";

                _logger.LogInformation("Scraping Naukri for: {Keyword}", keyword);
                await page.GotoAsync(searchUrl);

                try
                {
                    await page.WaitForSelectorAsync(".srp-jobtuple-wrapper, .jobTuple", new PageWaitForSelectorOptions { Timeout = 10000 });
                }
                catch
                {
                    _logger.LogWarning("No job results found on Naukri for: {Keyword}", keyword);
                    continue;
                }

                await Task.Delay(2000, cancellationToken);

                // Scroll to load more
                for (int s = 0; s < 3; s++)
                {
                    await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
                    await Task.Delay(1500, cancellationToken);
                }

                var jobCards = await page.QuerySelectorAllAsync(".srp-jobtuple-wrapper, .jobTupleHeader");

                foreach (var card in jobCards.Take(30))
                {
                    try
                    {
                        var title = await card.QuerySelectorAsync("a.title, a.jobTitle");
                        var company = await card.QuerySelectorAsync(".comp-name, .companyInfo");
                        var desc = await card.QuerySelectorAsync(".job-desc, .jobDesc");
                        var experience = await card.QuerySelectorAsync(".expwdth, li.experience");

                        var titleText = title != null ? await title.InnerTextAsync() : "";
                        var companyText = company != null ? await company.InnerTextAsync() : "";
                        var descText = desc != null ? await desc.InnerTextAsync() : "";
                        var expText = experience != null ? await experience.InnerTextAsync() : "";
                        var jobUrl = title != null ? await title.GetAttributeAsync("href") : "";

                        if (string.IsNullOrWhiteSpace(titleText) || string.IsNullOrWhiteSpace(jobUrl))
                            continue;

                        var rawText = $"{titleText} at {companyText}. {descText}. Experience: {expText}";

                        foundJobs.Add(new ScoutedJob
                        {
                            LinkedInUrl = jobUrl.StartsWith("http") ? jobUrl : "https://www.naukri.com" + jobUrl,
                            RawText = rawText.Trim(),
                            KeywordMatched = keyword,
                            Board = BoardName
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to parse Naukri job card.");
                    }
                }

                _logger.LogInformation("Found {Count} jobs on Naukri for '{Keyword}'.", jobCards.Count, keyword);
                await Task.Delay(3000, cancellationToken);
            }

            // Refresh session after successful scrape
            await SaveSessionAsync(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Naukri scraping failed.");
        }

        return foundJobs;
    }

    private static async Task<bool> IsLoggedInAsync(IPage page)
    {
        try
        {
            // Naukri shows user avatar or "My Naukri" link when logged in
            var loggedInEl = await page.QuerySelectorAsync(".nI-gNb-user-details, .user-name, [class*='userName'], .view-profile-wrapper");
            return loggedInEl != null;
        }
        catch { return false; }
    }

    private async Task SaveSessionAsync(IBrowserContext context)
    {
        try
        {
            var cookies = await context.CookiesAsync();
            var json = JsonConvert.SerializeObject(cookies.Select(c => new
            {
                name = c.Name,
                value = c.Value,
                domain = c.Domain,
                path = c.Path,
                httpOnly = c.HttpOnly,
                secure = c.Secure
            }), Formatting.Indented);

            Directory.CreateDirectory(SessionDir);
            await File.WriteAllTextAsync(SessionFile, json);
            _logger.LogInformation("Naukri session saved to {Path}", SessionFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save Naukri session.");
        }
    }
}
