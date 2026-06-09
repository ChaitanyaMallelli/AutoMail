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
        ILogger<JobScoutManager> logger)
    {
        _scrapers = scrapers;
        _gemini = gemini;
        _dbContext = dbContext;
        _telegram = telegram;
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

        // ── Assign status and collect all at once ───────────────────────
        var toSave = new List<ScoutedJob>();
        var toAlert = new List<ScoutedJob>();

        for (int i = 0; i < fresh.Count; i++)
        {
            var job = fresh[i];
            var isRelevant = i < relevanceResults.Count && relevanceResults[i];

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

        // ── Send Telegram alerts for matches ─────────────────────────────
        if (chatId != 0)
        {
            foreach (var job in toAlert)
            {
                try { await _telegram.SendJobAlertAsync(chatId, job); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to send Telegram alert for {Url}", job.LinkedInUrl); }
            }
        }

        _logger.LogInformation("Scout cycle complete.");
    }
}
