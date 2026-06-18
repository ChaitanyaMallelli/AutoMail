using JobAutomation.Data;
using JobAutomation.Models;
using Microsoft.EntityFrameworkCore;

namespace JobAutomation.Services;

public class JobScoutManager
{
    private readonly IEnumerable<IJobBoardScraper> _scrapers;
    private readonly GeminiService _gemini;
    private readonly AppDbContext _dbContext;
    private readonly TelegramService _telegram;
    private readonly AutoApplyService _autoApply;
    private readonly ILogger<JobScoutManager> _logger;

    private readonly List<string> _searchKeywords = new()
    {
        "hiring for dotnet developer",
        "dotnet developer",
        ".net developer",
        ".net developer banglore",
        ".net developer in dubai"
    };

    public JobScoutManager(
        IEnumerable<IJobBoardScraper> scrapers,
        GeminiService gemini,
        AppDbContext dbContext,
        TelegramService telegram,
        AutoApplyService autoApply,
        ILogger<JobScoutManager> logger)
    {
        _scrapers = scrapers;
        _gemini = gemini;
        _dbContext = dbContext;
        _telegram = telegram;
        _autoApply = autoApply;
        _logger = logger;
    }

    public async Task RunScoutCycleAsync(CancellationToken cancellationToken = default)
    {
        var chatId = (await _dbContext.UserProfiles
            .Where(p => p.TelegramChatId != null && p.TelegramChatId != 0)
            .Select(p => p.TelegramChatId)
            .FirstOrDefaultAsync(cancellationToken))
            ?? (await _dbContext.JobPosts
                .Where(j => j.TelegramChatId != null && j.TelegramChatId != 0)
                .Select(j => j.TelegramChatId)
                .FirstOrDefaultAsync(cancellationToken))
            ?? 0;

        // Collect all scraped jobs across all boards
        var allScraped = new List<ScoutedJob>();
        foreach (var scraper in _scrapers)
        {
            if (cancellationToken.IsCancellationRequested) break;
            _logger.LogInformation("Starting {Board} scrape...", scraper.BoardName);
            try
            {
                var jobs = await scraper.ScrapePostsAsync(_searchKeywords, cancellationToken);
                allScraped.AddRange(jobs);
                _logger.LogInformation("{Board}: {Count} raw posts scraped.", scraper.BoardName, jobs.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Board} scraper failed.", scraper.BoardName);
            }
        }

        if (allScraped.Count == 0)
        {
            _logger.LogInformation("No posts found across all boards.");
            return;
        }

        // Deduplicate by URL within this batch
        var newJobs = allScraped
            .GroupBy(j => j.LinkedInUrl)
            .Select(g => g.First())
            .ToList();

        _logger.LogInformation("Total: {Raw} raw → {Unique} unique.", allScraped.Count, newJobs.Count);

        // ── Bulk dedup against DB in ONE query ──────────────────────────
        var candidateUrls = newJobs.Select(j => j.LinkedInUrl).ToHashSet();
        var alreadySeenUrls = (await _dbContext.ScoutedJobs
            .Where(j => candidateUrls.Contains(j.LinkedInUrl))
            .Select(j => j.LinkedInUrl)
            .ToListAsync(cancellationToken)).ToHashSet();

        var candidateRawTexts = newJobs
            .Where(j => j.RawText != null)
            .Select(j => j.RawText!)
            .ToHashSet();
        var alreadySeenTexts = candidateRawTexts.Count > 0
            ? (await _dbContext.ScoutedJobs
                .Where(j => j.RawText != null && candidateRawTexts.Contains(j.RawText!))
                .Select(j => j.RawText!)
                .ToListAsync(cancellationToken)).ToHashSet()
            : new HashSet<string>();

        var fresh = newJobs
            .Where(j => !alreadySeenUrls.Contains(j.LinkedInUrl) &&
                        (j.RawText == null || !alreadySeenTexts.Contains(j.RawText)))
            .ToList();

        _logger.LogInformation("{Fresh} new (unseen) posts to filter.", fresh.Count);

        if (fresh.Count == 0) return;

        // ── Batch Gemini relevance filter — one API call for all posts ──
        var texts = fresh.Select(j => j.RawText ?? "").ToList();
        List<bool> relevanceResults;
        try
        {
            relevanceResults = await _gemini.BatchIsPostRelevantAsync(texts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch relevance check failed entirely — skipping cycle.");
            return;
        }

        // ── Check if auto-apply is enabled ──────────────────────────────
        var autoApplyEnabled = (await _dbContext.UserJobPreferences.Select(p => (bool?)p.AutoApplyEnabled).FirstOrDefaultAsync(cancellationToken)) ?? true;

        // ── Assign status and collect all at once ───────────────────────
        var toSave = new List<ScoutedJob>();
        var toAlert = new List<ScoutedJob>();

        for (int i = 0; i < fresh.Count; i++)
        {
            var job = fresh[i];
            var isRelevant = i < relevanceResults.Count && relevanceResults[i];

            // Stamp the board-specific job ID extracted from URL
            job.JobId = AutoApplyService.ExtractJobId(job.LinkedInUrl);

            if (isRelevant)
            {
                _logger.LogInformation("✅ MATCH: {Url}", job.LinkedInUrl);
                job.Status = ScoutedJobStatus.SentToTelegram;
                toAlert.Add(job);
            }
            else
            {
                _logger.LogInformation("❌ IGNORED: {Url}", job.LinkedInUrl);
                job.Status = ScoutedJobStatus.IgnoredByGemini;
            }
            toSave.Add(job);
        }

        // ── Single bulk DB write ─────────────────────────────────────────
        _dbContext.ScoutedJobs.AddRange(toSave);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Saved {Count} jobs. {Matches} match(es).", toSave.Count, toAlert.Count);

        // ── Auto-apply or send Telegram alert (conditional) ──────────────
        foreach (var job in toAlert)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (autoApplyEnabled)
            {
                // Auto-apply handles Telegram notification internally
                try { await _autoApply.ProcessScoutedJobAsync(job, chatId, cancellationToken); }
                catch (Exception ex) { _logger.LogError(ex, "AutoApply failed for {Url}", job.LinkedInUrl); }
            }
            else
            {
                // Manual mode: send Telegram alert with Apply button
                if (chatId != 0)
                {
                    try { await _telegram.SendJobAlertAsync(chatId, job); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to send Telegram alert for {Url}", job.LinkedInUrl); }
                }
            }
        }

        _logger.LogInformation("Scout cycle complete.");
    }

    /// <summary>
    /// On-demand flow: read curated LinkedIn post links from <paramref name="filePath"/> and run the
    /// next <paramref name="maxApplies"/> unprocessed links through the auto-apply pipeline.
    /// Links already present in AutoApplyLogs are skipped, so every run advances through the list.
    /// Reports per-post status to <see cref="FileApplyProgressTracker"/> and stops when cancelled.
    /// </summary>
    public async Task RunFileApplyCycleAsync(string filePath, int maxApplies)
    {
        var chatId = (await _dbContext.UserProfiles
            .Where(p => p.TelegramChatId != null && p.TelegramChatId != 0)
            .Select(p => p.TelegramChatId)
            .FirstOrDefaultAsync()) ?? 0;

        var links = LinkedInFileScraperService.ParseLinks(filePath);
        _logger.LogInformation("File apply: {Count} links parsed from {Path}", links.Count, filePath);

        if (links.Count == 0)
        {
            FileApplyProgressTracker.Begin(0);
            FileApplyProgressTracker.Complete();
            _logger.LogWarning("File apply: no links found in {Path}", filePath);
            return;
        }

        // Skip links we've already processed (applied/skipped/failed are all logged in AutoApplyLogs).
        var doneUrls = (await _dbContext.AutoApplyLogs
            .Where(l => l.JobUrl != null)
            .Select(l => l.JobUrl!)
            .ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pending = links.Where(l => !doneUrls.Contains(l.LinkedInUrl)).Take(maxApplies).ToList();
        _logger.LogInformation("File apply: {Pending} pending (cap {Cap}); {Done} already processed.",
            pending.Count, maxApplies, doneUrls.Count);

        var ct = FileApplyProgressTracker.Begin(pending.Count);
        try
        {
            foreach (var job in pending)
            {
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("File apply: stop requested — halting.");
                    break;
                }

                if (GeminiService.DailyLimitReached)
                {
                    _logger.LogWarning("File apply: Gemini daily limit reached — stopping run.");
                    FileApplyProgressTracker.Record("Skipped", "—", "Daily limit",
                        $"Gemini {GeminiService.MaxPerDay}/day quota reached — stopping. Resumes after UTC midnight.");
                    break;
                }

                try
                {
                    var log = await _autoApply.ProcessScoutedJobAsync(job, chatId, ct);
                    var detail = log.Status == Models.AutoApplyStatus.Applied
                        ? (log.EmailSent ? "Email sent" : (log.SkipReason ?? "Draft saved"))
                        : (log.SkipReason ?? "");
                    FileApplyProgressTracker.Record(log.Status.ToString(), log.CompanyName, log.JobTitle, detail);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "File apply: error processing {Url}", job.LinkedInUrl);
                    FileApplyProgressTracker.Record("Failed", "Unknown", "Unknown", ex.Message);
                }
            }
        }
        finally
        {
            FileApplyProgressTracker.Complete();
            _logger.LogInformation("File apply: run finished.");
        }
    }
}
