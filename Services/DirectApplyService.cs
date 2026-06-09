using Microsoft.Playwright;
using Newtonsoft.Json;
using JobAutomation.Models;

namespace JobAutomation.Services;

public class DirectApplyService
{
    private readonly ILogger<DirectApplyService> _logger;

    private static readonly string SessionDir = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Sessions");
    private static readonly string NaukriSessionFile = Path.Combine(SessionDir, "naukri_session.json");

    public DirectApplyService(ILogger<DirectApplyService> logger)
    {
        _logger = logger;
    }

    public async Task<(bool Success, string Message)> ApplyAsync(
        ScoutedJob job, UserProfile profile, string? resumeFilePath,
        CancellationToken cancellationToken = default)
    {
        return job.Board switch
        {
            "Naukri" => await ApplyOnNaukriAsync(job, profile, resumeFilePath, cancellationToken),
            "Indeed" => await ApplyOnIndeedAsync(job, profile, resumeFilePath, cancellationToken),
            _ => (false, $"Direct apply not supported for board: {job.Board}")
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Naukri
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<(bool, string)> ApplyOnNaukriAsync(
        ScoutedJob job, UserProfile profile, string? resumeFilePath,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Direct Apply → Naukri: {Url}", job.LinkedInUrl);

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                Args = new[] { "--disable-blink-features=AutomationControlled" }
            });

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
            });

            // Load Naukri session cookies
            if (!await LoadNaukriCookiesAsync(context))
            {
                return (false, "Naukri session not found. Please run the Scout first to log in.");
            }

            var page = await context.NewPageAsync();
            await page.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

            await page.GotoAsync(job.LinkedInUrl, new PageGotoOptions { Timeout = 20000 });
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await Task.Delay(2500, cancellationToken);

            // Check if already applied
            var alreadyApplied = await page.QuerySelectorAsync("button[disabled][class*='applied'], .already-applied, [data-qa='already-applied']");
            if (alreadyApplied != null)
            {
                _logger.LogInformation("Already applied on Naukri for: {Url}", job.LinkedInUrl);
                return (true, "Already applied on Naukri previously.");
            }

            // Find Apply button
            var applyBtn = await FindApplyButtonAsync(page);
            if (applyBtn == null)
            {
                return (false, "Could not find Apply button on Naukri job page.");
            }

            await applyBtn.ClickAsync();
            await Task.Delay(2000, cancellationToken);

            // Check if redirected to external site
            if (!page.Url.Contains("naukri.com"))
            {
                return (false, $"Job redirects to external site: {page.Url} — cannot auto-apply.");
            }

            // Handle apply modal / multi-step form
            var result = await FillNaukriApplyFormAsync(page, profile, resumeFilePath, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Naukri direct apply failed for {Url}", job.LinkedInUrl);
            return (false, ex.Message);
        }
    }

    private async Task<IElementHandle?> FindApplyButtonAsync(IPage page)
    {
        var selectors = new[]
        {
            "button.apply-button",
            "button[data-qa='apply-button']",
            "a.apply-button",
            "button:has-text('Apply')",
            "button:has-text('Apply Now')",
            "[class*='applyBtn']",
            ".nI-gNb-heroSection__applyBtn",
            ".styles_apply-button__N0iSF"
        };

        foreach (var selector in selectors)
        {
            try
            {
                var el = await page.QuerySelectorAsync(selector);
                if (el != null && await el.IsVisibleAsync())
                    return el;
            }
            catch { }
        }

        return null;
    }

    private async Task<(bool, string)> FillNaukriApplyFormAsync(
        IPage page, UserProfile profile, string? resumeFilePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(1500, cancellationToken);

            // ── Upload resume if prompted ──────────────────────────────────
            if (!string.IsNullOrEmpty(resumeFilePath) && File.Exists(resumeFilePath))
            {
                var fileInput = await page.QuerySelectorAsync("input[type='file']");
                if (fileInput != null)
                {
                    await fileInput.SetInputFilesAsync(resumeFilePath);
                    _logger.LogInformation("Resume uploaded on Naukri apply form.");
                    await Task.Delay(1000, cancellationToken);
                }
            }

            // ── Fill text fields if present ────────────────────────────────
            await TryFillFieldAsync(page, "input[name='name'], input[placeholder*='Name'], input[id*='name']", profile.FullName);
            await TryFillFieldAsync(page, "input[name='email'], input[type='email'], input[placeholder*='Email']", profile.Email);
            await TryFillFieldAsync(page, "input[name='mobile'], input[name='phone'], input[type='tel'], input[placeholder*='Mobile'], input[placeholder*='Phone']", profile.Phone ?? "");

            // ── Look for and click Submit / Apply ──────────────────────────
            var submitSelectors = new[]
            {
                "button[type='submit']",
                "button:has-text('Submit')",
                "button:has-text('Apply')",
                "button:has-text('Send Application')",
                "[data-qa='submit-btn']"
            };

            foreach (var sel in submitSelectors)
            {
                try
                {
                    var btn = await page.QuerySelectorAsync(sel);
                    if (btn != null && await btn.IsVisibleAsync() && await btn.IsEnabledAsync())
                    {
                        await btn.ClickAsync();
                        await Task.Delay(2000, cancellationToken);
                        _logger.LogInformation("Naukri application submitted.");
                        return (true, "Application submitted on Naukri.");
                    }
                }
                catch { }
            }

            return (false, "Could not find submit button on Naukri apply form.");
        }
        catch (Exception ex)
        {
            return (false, $"Form fill failed: {ex.Message}");
        }
    }

    private static async Task TryFillFieldAsync(IPage page, string selector, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            var el = await page.QuerySelectorAsync(selector);
            if (el != null && await el.IsVisibleAsync())
            {
                var existing = await el.InputValueAsync();
                if (string.IsNullOrWhiteSpace(existing))
                    await el.FillAsync(value);
            }
        }
        catch { }
    }

    private async Task<bool> LoadNaukriCookiesAsync(IBrowserContext context)
    {
        if (!File.Exists(NaukriSessionFile)) return false;
        try
        {
            var json = await File.ReadAllTextAsync(NaukriSessionFile);
            var raw = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(json);
            if (raw == null) return false;

            var cookies = raw.Select(c => new Cookie
            {
                Name = c.GetValueOrDefault("name")?.ToString() ?? "",
                Value = c.GetValueOrDefault("value")?.ToString() ?? "",
                Domain = c.GetValueOrDefault("domain")?.ToString() ?? ".naukri.com",
                Path = c.GetValueOrDefault("path")?.ToString() ?? "/",
                HttpOnly = c.ContainsKey("httpOnly") && bool.TryParse(c["httpOnly"]?.ToString(), out var h) && h,
                Secure = c.ContainsKey("secure") && bool.TryParse(c["secure"]?.ToString(), out var s) && s,
            }).ToList();

            await context.AddCookiesAsync(cookies);
            return true;
        }
        catch { return false; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Indeed Easy Apply
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<(bool, string)> ApplyOnIndeedAsync(
        ScoutedJob job, UserProfile profile, string? resumeFilePath,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Direct Apply → Indeed: {Url}", job.LinkedInUrl);

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                Args = new[] { "--disable-blink-features=AutomationControlled" }
            });

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
            });

            var page = await context.NewPageAsync();
            await page.AddInitScriptAsync("Object.defineProperty(navigator, 'webdriver', {get: () => undefined})");

            await page.GotoAsync(job.LinkedInUrl, new PageGotoOptions { Timeout = 20000 });
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await Task.Delay(2500, cancellationToken);

            // Check for Easy Apply button
            var easyApplyBtn = await page.QuerySelectorAsync(
                "button[data-testid='indeedApplyButton'], button:has-text('Apply now'), .ia-IndeedApply-modal-trigger");

            if (easyApplyBtn == null)
            {
                // Check if it's an external apply
                var externalBtn = await page.QuerySelectorAsync("button:has-text('Apply on company site'), a[target='_blank']");
                if (externalBtn != null)
                    return (false, "Job requires applying on company website — cannot auto-apply.");

                return (false, "No Easy Apply button found on Indeed job page.");
            }

            await easyApplyBtn.ClickAsync();
            await Task.Delay(2000, cancellationToken);

            // Fill Indeed Easy Apply form (multi-step)
            return await FillIndeedApplyFormAsync(page, profile, resumeFilePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indeed direct apply failed for {Url}", job.LinkedInUrl);
            return (false, ex.Message);
        }
    }

    private async Task<(bool, string)> FillIndeedApplyFormAsync(
        IPage page, UserProfile profile, string? resumeFilePath,
        CancellationToken cancellationToken)
    {
        try
        {
            // Indeed uses a multi-page modal — iterate up to 5 steps
            for (int step = 0; step < 5; step++)
            {
                await Task.Delay(1500, cancellationToken);

                // Upload resume if prompted
                if (!string.IsNullOrEmpty(resumeFilePath) && File.Exists(resumeFilePath))
                {
                    var fileInput = await page.QuerySelectorAsync("input[type='file'][name*='resume'], input[type='file'][accept*='pdf']");
                    if (fileInput != null)
                    {
                        await fileInput.SetInputFilesAsync(resumeFilePath);
                        await Task.Delay(1000, cancellationToken);
                    }
                }

                // Fill common fields
                await TryFillFieldAsync(page, "input[name='applicant.name'], input[id*='applicant-name']", profile.FullName);
                await TryFillFieldAsync(page, "input[name='applicant.phoneNumber'], input[id*='phone']", profile.Phone ?? "");

                // Look for Continue / Next / Submit button
                var nextBtn = await page.QuerySelectorAsync(
                    "button[data-testid='continue-button'], button:has-text('Continue'), button:has-text('Next'), button:has-text('Submit your application')");

                if (nextBtn == null) break;

                var btnText = (await nextBtn.InnerTextAsync()).Trim();
                await nextBtn.ClickAsync();
                await Task.Delay(2000, cancellationToken);

                if (btnText.Contains("Submit", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Indeed Easy Apply submitted.");
                    return (true, "Application submitted on Indeed Easy Apply.");
                }
            }

            return (false, "Could not complete Indeed Easy Apply form — may require manual input.");
        }
        catch (Exception ex)
        {
            return (false, $"Indeed form fill failed: {ex.Message}");
        }
    }
}
