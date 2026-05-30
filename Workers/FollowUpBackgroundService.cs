using Microsoft.EntityFrameworkCore;
using JobAutomation.Data;
using JobAutomation.Models;
using JobAutomation.Services;

namespace JobAutomation.Workers;

public class FollowUpBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<FollowUpBackgroundService> _logger;

    public FollowUpBackgroundService(IServiceProvider serviceProvider, ILogger<FollowUpBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FollowUpBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckForFollowUpsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing FollowUpBackgroundService.");
            }

            // Run check every 1 hour
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task CheckForFollowUpsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var telegramService = scope.ServiceProvider.GetRequiredService<TelegramService>();

        var cutoffTime = DateTime.UtcNow.AddDays(-3);

        // Find jobs that were sent at least 3 days ago and haven't had a follow-up or status update
        var jobsNeedingFollowUp = await dbContext.JobPosts
            .Include(j => j.GeneratedEmail)
            .Where(j => j.Status == JobStatus.Sent 
                        && j.GeneratedEmail != null 
                        && j.GeneratedEmail.SentAt <= cutoffTime)
            .ToListAsync(stoppingToken);

        foreach (var job in jobsNeedingFollowUp)
        {
            if (job.TelegramChatId.HasValue)
            {
                _logger.LogInformation("Prompting follow-up for Job {JobId} to Chat {ChatId}", job.Id, job.TelegramChatId);
                
                var inlineKeyboard = new
                {
                    inline_keyboard = new[]
                    {
                        new[] { new { text = "📨 Auto-Generate & Send Follow-up", callback_data = $"followup_send:{job.Id}" } },
                        new[] { new { text = "✅ I got a response!", callback_data = $"followup_gotresponse:{job.Id}" } },
                        new[] { new { text = "❌ Dismiss", callback_data = $"followup_dismiss:{job.Id}" } }
                    }
                };

                var message = $"⏰ <b>Follow-Up Reminder!</b>\n\nIt's been 3 days since you applied to <b>{job.Role}</b> at <b>{job.CompanyName}</b>.\n\nHave you heard back? I can write a polite follow-up email for you.";
                
                await telegramService.SendKeyboardReplyAsync(job.TelegramChatId.Value, message, inlineKeyboard);
            }
        }
    }
}
