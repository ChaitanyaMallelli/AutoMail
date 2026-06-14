using System.Text;
using JobAutomation.Data;
using JobAutomation.Extensions;
using JobAutomation.Models;
using JobAutomation.Services;
using Microsoft.EntityFrameworkCore;

namespace JobAutomation.Workers;

public class DailyReportBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyReportBackgroundService> _logger;

    public DailyReportBackgroundService(IServiceProvider serviceProvider, ILogger<DailyReportBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DailyReportBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reportHour = await GetReportHourAsync();
                var delay = CalculateDelay(reportHour);
                _logger.LogInformation("Daily report scheduled in {Delay:hh\\:mm}", delay);
                await Task.Delay(delay, stoppingToken);
                await SendDailyReportAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DailyReportBackgroundService error.");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }

    private async Task<int> GetReportHourAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var prefs = await db.UserJobPreferences.FirstOrDefaultAsync();
        return prefs?.DailyReportUtcHour ?? 15;
    }

    private static TimeSpan CalculateDelay(int utcHour)
    {
        var now     = DateTime.UtcNow;
        var today   = now.Date.AddHours(utcHour);
        var target  = now < today ? today : today.AddDays(1);
        return target - now;
    }

    private async Task SendDailyReportAsync(CancellationToken ct)
    {
        using var scope    = _serviceProvider.CreateScope();
        var db             = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var emailService   = scope.ServiceProvider.GetRequiredService<EmailService>();
        var telegramService = scope.ServiceProvider.GetRequiredService<TelegramService>();

        // IST "today" window: 00:00 IST = UTC minus 5h30m, so today IST start = UTC 18:30 yesterday
        var istNow       = DateTime.UtcNow.ToIst();
        var todayIstStart = istNow.Date;                                // 00:00 IST today
        var todayUtcStart = todayIstStart.AddHours(-5).AddMinutes(-30); // convert back to UTC
        var todayUtcEnd   = todayUtcStart.AddDays(1);

        var logs = await db.AutoApplyLogs
            .Where(l => l.AppliedAt >= todayUtcStart && l.AppliedAt < todayUtcEnd)
            .OrderByDescending(l => l.AppliedAt)
            .ToListAsync(ct);

        var applied = logs.Where(l => l.Status == AutoApplyStatus.Applied).ToList();
        var skipped = logs.Where(l => l.Status == AutoApplyStatus.Skipped).ToList();
        var failed  = logs.Where(l => l.Status == AutoApplyStatus.Failed).ToList();

        _logger.LogInformation("Daily report: {A} applied, {S} skipped, {F} failed", applied.Count, skipped.Count, failed.Count);

        var profile  = await db.UserProfiles.FirstOrDefaultAsync(ct);
        var prefs    = await db.UserJobPreferences.FirstOrDefaultAsync(ct);
        var toEmail  = prefs?.NotificationEmail ?? profile?.Email;

        if (string.IsNullOrWhiteSpace(toEmail) || profile == null)
        {
            _logger.LogWarning("Daily report: no notification email configured.");
            return;
        }

        var reportDate = todayIstStart.ToString("dd MMM yyyy");
        var subject    = $"📋 Daily Job Report — {reportDate} ({applied.Count} applied, {skipped.Count} skipped)";
        var body       = BuildHtmlReport(logs, applied, skipped, failed, reportDate);

        var tempEmail = new GeneratedEmail
        {
            Subject        = subject,
            Body           = body,
            RecipientEmail = toEmail,
            CreatedAt      = DateTime.UtcNow
        };

        var (sent, err) = await emailService.SendEmailAsync(tempEmail, profile);
        if (sent)
            _logger.LogInformation("Daily report sent to {Email}", toEmail);
        else
            _logger.LogWarning("Daily report email failed: {Err}", err);

        // Telegram summary
        var chatId = profile.TelegramChatId ?? 0;
        if (chatId != 0)
        {
            var tgSummary = $"📋 *Daily Report — {reportDate}*\n\n" +
                            $"✅ Applied: {applied.Count}\n" +
                            $"⏭️ Skipped: {skipped.Count}\n" +
                            $"❌ Failed:  {failed.Count}\n\n" +
                            $"Full report sent to {toEmail}";
            try { await telegramService.SendReplyAsync(chatId, tgSummary); }
            catch (Exception ex) { _logger.LogWarning("Daily report Telegram failed: {Err}", ex.Message); }
        }
    }

    private static string BuildHtmlReport(
        List<AutoApplyLog> all,
        List<AutoApplyLog> applied,
        List<AutoApplyLog> skipped,
        List<AutoApplyLog> failed,
        string reportDate)
    {
        var sb = new StringBuilder();

        sb.Append($"""
            <html><body style="font-family:sans-serif;color:#1a1a1a;max-width:800px;margin:auto;padding:20px;">
            <h2 style="color:#7c3aed;">📋 Daily Job Application Report — {reportDate}</h2>
            <p style="font-size:18px;">
              <span style="color:#22c55e;">✅ Applied: <b>{applied.Count}</b></span> &nbsp;|&nbsp;
              <span style="color:#f59e0b;">⏭️ Skipped: <b>{skipped.Count}</b></span> &nbsp;|&nbsp;
              <span style="color:#ef4444;">❌ Failed: <b>{failed.Count}</b></span>
            </p>
            """);

        if (applied.Count > 0)
        {
            sb.Append("""
                <h3 style="color:#22c55e;border-bottom:2px solid #22c55e;padding-bottom:6px;">✅ Applied Jobs</h3>
                <table style="width:100%;border-collapse:collapse;font-size:14px;">
                <tr style="background:#f0fdf4;">
                  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Company</th>
                  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Position</th>
                  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Work Mode</th>
                  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Location</th>
                  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Applied At (IST)</th>
                </tr>
                """);
            foreach (var log in applied)
            {
                var timeIst = log.AppliedAt.ToIst().ToString("HH:mm");
                var jobLink = !string.IsNullOrEmpty(log.JobUrl)
                    ? $"<a href=\"{log.JobUrl}\" style=\"color:#7c3aed;\">🔗 View</a>"
                    : "—";
                sb.Append($"""
                    <tr>
                      <td style="padding:8px;border:1px solid #ddd;">{Esc(log.CompanyName)}</td>
                      <td style="padding:8px;border:1px solid #ddd;">{Esc(log.JobTitle)} {jobLink}</td>
                      <td style="padding:8px;border:1px solid #ddd;">{Esc(log.WorkMode ?? "—")}</td>
                      <td style="padding:8px;border:1px solid #ddd;">{Esc(log.Location ?? "—")}</td>
                      <td style="padding:8px;border:1px solid #ddd;">{timeIst}</td>
                    </tr>
                    """);
            }
            sb.Append("</table><br/>");
        }

        if (skipped.Count > 0)
        {
            sb.Append("""
                <h3 style="color:#f59e0b;border-bottom:2px solid #f59e0b;padding-bottom:6px;">⏭️ Skipped Jobs</h3>
                <table style="width:100%;border-collapse:collapse;font-size:14px;">
                <tr style="background:#fffbeb;">
                  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Company</th>
                  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Position</th>
                  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Reason</th>
                </tr>
                """);
            foreach (var log in skipped)
            {
                sb.Append($"""
                    <tr>
                      <td style="padding:8px;border:1px solid #ddd;">{Esc(log.CompanyName)}</td>
                      <td style="padding:8px;border:1px solid #ddd;">{Esc(log.JobTitle)}</td>
                      <td style="padding:8px;border:1px solid #ddd;color:#78716c;">{Esc(log.SkipReason ?? "—")}</td>
                    </tr>
                    """);
            }
            sb.Append("</table><br/>");
        }

        if (failed.Count > 0)
        {
            sb.Append("""
                <h3 style="color:#ef4444;border-bottom:2px solid #ef4444;padding-bottom:6px;">❌ Failed</h3>
                <table style="width:100%;border-collapse:collapse;font-size:14px;">
                <tr style="background:#fef2f2;">
                  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Company</th>
                  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Position</th>
                  <th style="padding:8px;border:1px solid #ddd;text-align:left;">Error</th>
                </tr>
                """);
            foreach (var log in failed)
            {
                sb.Append($"""
                    <tr>
                      <td style="padding:8px;border:1px solid #ddd;">{Esc(log.CompanyName)}</td>
                      <td style="padding:8px;border:1px solid #ddd;">{Esc(log.JobTitle)}</td>
                      <td style="padding:8px;border:1px solid #ddd;color:#ef4444;">{Esc(log.SkipReason ?? "—")}</td>
                    </tr>
                    """);
            }
            sb.Append("</table><br/>");
        }

        if (all.Count == 0)
            sb.Append("<p style=\"color:#71717a;\">No auto-apply activity today.</p>");

        sb.Append("""
            <hr style="margin-top:24px;border:none;border-top:1px solid #e5e7eb;"/>
            <p style="color:#9ca3af;font-size:12px;">JobFlow AI — Automated Job Application System</p>
            </body></html>
            """);

        return sb.ToString();
    }

    private static string Esc(string? s) =>
        System.Net.WebUtility.HtmlEncode(s ?? "");
}
