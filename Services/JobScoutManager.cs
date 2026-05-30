using JobAutomation.Data;
using JobAutomation.Models;
using Microsoft.EntityFrameworkCore;

namespace JobAutomation.Services;

public class JobScoutManager
{
    private readonly LinkedInScraperService _scraper;
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
        LinkedInScraperService scraper,
        GeminiService gemini,
        AppDbContext dbContext,
        TelegramService telegram,
        ILogger<JobScoutManager> logger)
    {
        _scraper = scraper;
        _gemini = gemini;
        _dbContext = dbContext;
        _telegram = telegram;
        _logger = logger;
    }

    public async Task RunScoutCycleAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting LinkedIn scrape...");
        
        // Find the user's ChatId from previous jobs so we know where to send the alert
        var chatId = await _dbContext.JobPosts
            .Where(j => j.TelegramChatId != null && j.TelegramChatId != 0)
            .Select(j => j.TelegramChatId)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;

        // 1. Scrape new posts
        var scrapedJobs = await _scraper.ScrapePostsAsync(_searchKeywords);
        
        // Deduplicate locally-scraped posts by URL to avoid multi-keyword overlaps in the same search session
        var newJobs = scrapedJobs
            .GroupBy(j => j.LinkedInUrl)
            .Select(g => g.First())
            .ToList();
            
        _logger.LogInformation("Found {ScrapedCount} raw posts, trimmed to {Count} unique posts across all keywords.", scrapedJobs.Count, newJobs.Count);

        foreach (var job in newJobs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // 2. Check if we already processed this exact post URL or identical post content
            var exists = await _dbContext.ScoutedJobs
                .AnyAsync(j => j.LinkedInUrl == job.LinkedInUrl || 
                               (job.RawText != null && j.RawText == job.RawText), cancellationToken);
            
            if (exists)
            {
                _logger.LogInformation("Skipping duplicate post: {Url}", job.LinkedInUrl);
                continue; // Skip duplicates
            }

            // 3. Ask Gemini if it requires 3+ years experience
            _logger.LogInformation("Analyzing post with Gemini: {Url}", job.LinkedInUrl);
            var isRelevant = await _gemini.IsPostRelevantAsync(job.RawText ?? "");

            if (isRelevant)
            {
                _logger.LogInformation("✅ PERFECT MATCH: Requires 3+ years experience! Saving to DB.");
                job.Status = ScoutedJobStatus.SentToTelegram;
                _dbContext.ScoutedJobs.Add(job);
                await _dbContext.SaveChangesAsync(cancellationToken);

                // Send Telegram Push Notification
                if (chatId != 0)
                {
                    await _telegram.SendJobAlertAsync(chatId, job);
                }
            }
            else
            {
                _logger.LogInformation("❌ IGNORED: Does not meet 3+ years experience requirement.");
                job.Status = ScoutedJobStatus.IgnoredByGemini;
                _dbContext.ScoutedJobs.Add(job);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            
            // Random delay to avoid hitting Gemini too fast
            await Task.Delay(2000, cancellationToken);
        }
        
        _logger.LogInformation("Scout cycle complete.");
    }
}
