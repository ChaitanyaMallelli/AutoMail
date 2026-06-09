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
        // Get chatId — prefer UserProfile (survives job data clears), fall back to JobPosts
        var chatId = (await _dbContext.UserProfiles
            .Where(p => p.TelegramChatId != null && p.TelegramChatId != 0)
            .Select(p => p.TelegramChatId)
            .FirstOrDefaultAsync(cancellationToken))
            ?? (await _dbContext.JobPosts
                .Where(j => j.TelegramChatId != null && j.TelegramChatId != 0)
                .Select(j => j.TelegramChatId)
                .FirstOrDefaultAsync(cancellationToken))
            ?? 0;

        // Run each board scraper in sequence
        foreach (var scraper in _scrapers)
        {
            if (cancellationToken.IsCancellationRequested) break;

            _logger.LogInformation("Starting {Board} scrape...", scraper.BoardName);

            List<ScoutedJob> scrapedJobs;
            try
            {
                scrapedJobs = await scraper.ScrapePostsAsync(_searchKeywords, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Board} scraper threw an exception. Continuing with next board.", scraper.BoardName);
                continue;
            }

            // Deduplicate within this batch by URL
            var newJobs = scrapedJobs
                .GroupBy(j => j.LinkedInUrl)
                .Select(g => g.First())
                .ToList();

            _logger.LogInformation("{Board}: {Raw} raw → {Unique} unique jobs.", scraper.BoardName, scrapedJobs.Count, newJobs.Count);

            await ProcessJobsAsync(newJobs, chatId, cancellationToken);
        }

        _logger.LogInformation("All board scout cycles complete.");
    }

    private async Task ProcessJobsAsync(List<ScoutedJob> jobs, long chatId, CancellationToken cancellationToken)
    {
        foreach (var job in jobs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Skip if already seen (same URL or identical raw text)
            var exists = await _dbContext.ScoutedJobs
                .AnyAsync(j => j.LinkedInUrl == job.LinkedInUrl ||
                               (job.RawText != null && j.RawText == job.RawText), cancellationToken);

            if (exists)
            {
                _logger.LogInformation("Skipping duplicate: {Url}", job.LinkedInUrl);
                continue;
            }

            // Gemini relevance filter — 3+ years experience
            _logger.LogInformation("[{Board}] Analyzing with Gemini: {Url}", job.Board, job.LinkedInUrl);
            var isRelevant = await _gemini.IsPostRelevantAsync(job.RawText ?? "");

            if (!isRelevant)
            {
                _logger.LogInformation("❌ [{Board}] IGNORED: Does not meet 3+ years requirement.", job.Board);
                job.Status = ScoutedJobStatus.IgnoredByGemini;
                _dbContext.ScoutedJobs.Add(job);
                await _dbContext.SaveChangesAsync(cancellationToken);
                continue;
            }

            _logger.LogInformation("✅ [{Board}] MATCH: Saving and alerting.", job.Board);
            job.Status = ScoutedJobStatus.SentToTelegram;
            _dbContext.ScoutedJobs.Add(job);
            await _dbContext.SaveChangesAsync(cancellationToken);

            if (chatId != 0)
                await _telegram.SendJobAlertAsync(chatId, job);

            await Task.Delay(2000, cancellationToken);
        }
    }
}
