using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using JobAutomation.Data;
using JobAutomation.Models;
using JobAutomation.Services;

namespace JobAutomation.Workers;

public class GmailReplyMonitorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GmailReplyMonitorService> _logger;

    public GmailReplyMonitorService(IServiceProvider serviceProvider, IConfiguration configuration, ILogger<GmailReplyMonitorService> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GmailReplyMonitorService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckForRepliesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GmailReplyMonitorService.");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    private async Task CheckForRepliesAsync(CancellationToken stoppingToken)
    {
        var imapServer = _configuration["Gmail:ImapServer"] ?? "imap.gmail.com";
        var imapPort = int.Parse(_configuration["Gmail:ImapPort"] ?? "993");
        var senderEmail = _configuration["Gmail:SenderEmail"];
        var appPassword = _configuration["Gmail:AppPassword"];

        if (string.IsNullOrEmpty(senderEmail) || string.IsNullOrEmpty(appPassword))
        {
            _logger.LogWarning("Gmail credentials not configured. Skipping reply check.");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var geminiService = scope.ServiceProvider.GetRequiredService<GeminiService>();
        var telegramService = scope.ServiceProvider.GetRequiredService<TelegramService>();

        // Get all sent emails that haven't had a reply detected yet
        var sentEmails = await dbContext.GeneratedEmails
            .Include(e => e.JobPost)
            .Where(e => e.IsSent && e.RepliedAt == null && e.RecipientEmail != null)
            .ToListAsync(stoppingToken);

        if (!sentEmails.Any()) return;

        var recruiterEmails = sentEmails
            .Select(e => e.RecipientEmail.ToLower())
            .ToHashSet();

        _logger.LogInformation("Checking Gmail INBOX for replies from {Count} recruiters.", recruiterEmails.Count);

        try
        {
            using var client = new ImapClient();
            await client.ConnectAsync(imapServer, imapPort, MailKit.Security.SecureSocketOptions.SslOnConnect, stoppingToken);
            await client.AuthenticateAsync(senderEmail, appPassword, stoppingToken);

            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, stoppingToken);

            // Search for messages received in the last 60 days
            var since = DateTime.UtcNow.AddDays(-60);
            var query = SearchQuery.DeliveredAfter(since);
            var uids = await inbox.SearchAsync(query, stoppingToken);

            foreach (var uid in uids)
            {
                if (stoppingToken.IsCancellationRequested) break;

                var message = await inbox.GetMessageAsync(uid, stoppingToken);
                var fromAddress = message.From.Mailboxes.FirstOrDefault()?.Address?.ToLower();

                if (fromAddress == null || !recruiterEmails.Contains(fromAddress)) continue;

                // Find matching sent email by recruiter address
                var matchedEmail = sentEmails.FirstOrDefault(e =>
                    e.RecipientEmail.ToLower() == fromAddress && e.RepliedAt == null);

                if (matchedEmail == null) continue;

                // Confirm it's a reply (not the original - skip if sent before this message arrived)
                if (matchedEmail.SentAt.HasValue && message.Date.UtcDateTime < matchedEmail.SentAt.Value) continue;

                _logger.LogInformation("Reply detected from {From} for job {JobId}", fromAddress, matchedEmail.JobPostId);

                var replyText = message.TextBody ?? message.HtmlBody ?? "";
                if (replyText.Length > 3000) replyText = replyText.Substring(0, 3000);

                var company = matchedEmail.JobPost?.CompanyName ?? "Unknown";
                var role = matchedEmail.JobPost?.Role ?? "Unknown";

                // Classify the reply with Gemini
                string classification = "other";
                try
                {
                    classification = await geminiService.ClassifyReplyAsync(replyText, company, role);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gemini classification failed, defaulting to 'other'.");
                }

                // Update DB
                matchedEmail.RepliedAt = DateTime.UtcNow;
                matchedEmail.ReplySubject = message.Subject?.Length > 200 ? message.Subject.Substring(0, 200) : message.Subject;
                matchedEmail.ReplySnippet = replyText.Length > 1000 ? replyText.Substring(0, 1000) : replyText;
                matchedEmail.ReplyClassification = classification;

                if (matchedEmail.JobPost != null)
                {
                    matchedEmail.JobPost.Status = classification switch
                    {
                        "interview" => JobStatus.InterviewScheduled,
                        "rejected" => JobStatus.Rejected,
                        _ => matchedEmail.JobPost.Status
                    };
                    matchedEmail.JobPost.UpdatedAt = DateTime.UtcNow;
                }

                await dbContext.SaveChangesAsync(stoppingToken);

                // Send Telegram notification
                var chatId = matchedEmail.JobPost?.TelegramChatId;
                if (chatId.HasValue && chatId.Value != 999)
                {
                    var emoji = classification switch
                    {
                        "interview" => "🎉",
                        "rejected" => "😔",
                        "interested" => "👀",
                        _ => "📩"
                    };
                    var statusText = classification switch
                    {
                        "interview" => "Interview request received!",
                        "rejected" => "Application rejected.",
                        "interested" => "Recruiter expressed interest.",
                        _ => "General reply received."
                    };
                    var tgMsg = $"{emoji} <b>Reply Detected!</b>\n\n<b>{company}</b> replied to your <b>{role}</b> application.\n<i>{statusText}</i>\n\nSnippet: {replyText.Substring(0, Math.Min(200, replyText.Length))}...";
                    try { await telegramService.SendReplyAsync(chatId.Value, tgMsg); } catch { }
                }
            }

            await client.DisconnectAsync(true, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IMAP connection or search failed.");
        }
    }
}
