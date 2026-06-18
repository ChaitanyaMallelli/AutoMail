using System.Text;
using System.Text.RegularExpressions;
using JobAutomation.Data;
using JobAutomation.Extensions;
using JobAutomation.Models;
using Microsoft.EntityFrameworkCore;

namespace JobAutomation.Services;

public class AutoApplyService
{
    private readonly AppDbContext _dbContext;
    private readonly GeminiService _geminiService;
    private readonly ResumeMatchingService _resumeMatchingService;
    private readonly CompanyEmailFinderService _emailFinder;
    private readonly EmailService _emailService;
    private readonly TelegramService _telegramService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AutoApplyService> _logger;

    public AutoApplyService(
        AppDbContext dbContext,
        GeminiService geminiService,
        ResumeMatchingService resumeMatchingService,
        CompanyEmailFinderService emailFinder,
        EmailService emailService,
        TelegramService telegramService,
        IConfiguration configuration,
        ILogger<AutoApplyService> logger)
    {
        _dbContext = dbContext;
        _geminiService = geminiService;
        _resumeMatchingService = resumeMatchingService;
        _emailFinder = emailFinder;
        _emailService = emailService;
        _telegramService = telegramService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AutoApplyLog> ProcessScoutedJobAsync(ScoutedJob scoutedJob, long chatId, CancellationToken ct = default)
    {
        var log = new AutoApplyLog
        {
            JobUrl  = scoutedJob.LinkedInUrl,
            JobId   = ExtractJobId(scoutedJob.LinkedInUrl),
            Platform = scoutedJob.Board ?? "LinkedIn",
            AppliedAt = DateTime.UtcNow
        };

        try
        {
            // ── 1. Load preferences ──────────────────────────────────────────
            var prefs = await _dbContext.UserJobPreferences.FirstOrDefaultAsync(ct)
                        ?? new UserJobPreferences();

            if (!prefs.AutoApplyEnabled)
            {
                log.CompanyName = "Unknown";
                log.JobTitle    = "Unknown";
                return await SkipAsync(log, "Auto-apply is disabled", chatId, notify: false, ct);
            }

            // ── 2. Fast-path duplicate check (before any HTTP/AI call) ───────
            var fastDup = await FastDuplicateCheckAsync(scoutedJob.LinkedInUrl, log.JobId, ct);
            if (fastDup != null)
            {
                log.CompanyName = "Unknown";
                log.JobTitle    = "Unknown";
                return await SkipAsync(log, fastDup, chatId, notify: false, ct);
            }

            // ── 3. Fetch URL + Gemini extraction ─────────────────────────────
            string htmlContent;
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; AutoMail/1.0)");
                http.Timeout = TimeSpan.FromSeconds(20);
                htmlContent = await http.GetStringAsync(scoutedJob.LinkedInUrl, ct);
            }
            catch (Exception ex)
            {
                log.CompanyName = "Unknown";
                log.JobTitle    = "Unknown";
                _logger.LogWarning("AutoApply: failed to fetch URL {Url}: {Err}", scoutedJob.LinkedInUrl, ex.Message);
                return await FailAsync(log, $"URL fetch failed: {ex.Message}", chatId, ct);
            }

            var extraction = await _geminiService.ExtractJobDetailsFromUrlContentAsync(htmlContent, scoutedJob.LinkedInUrl);
            if (!extraction.IsSuccessful)
            {
                log.CompanyName = "Unknown";
                log.JobTitle    = "Unknown";
                return await FailAsync(log, $"Extraction failed: {extraction.ErrorMessage}", chatId, ct);
            }

            log.CompanyName       = extraction.CompanyName ?? "Unknown";
            log.JobTitle          = extraction.Role ?? "Unknown";
            log.Location          = extraction.Location;
            log.ExperienceRequired = extraction.ExperienceRequired;

            // Detect work mode from raw post text
            log.WorkMode = DetectWorkMode(scoutedJob.RawText ?? htmlContent);

            // ── 4. Full duplicate check (now we have company + role) ─────────
            var fullDup = await FullDuplicateCheckAsync(log.CompanyName, log.JobTitle, ct);
            if (fullDup != null)
                return await SkipAsync(log, fullDup, chatId, notify: false, ct);

            // ── 5. Experience filter ─────────────────────────────────────────
            var (expMin, expMax) = ParseExperienceRange(extraction.ExperienceRequired);
            if (!expMin.HasValue)
            {
                return await SkipAsync(log, "Experience requirement unknown — cannot verify fit", chatId, notify: true, ct);
            }

            // Range overlap check: job range must overlap with preferred range
            if (expMax < prefs.MinExperienceYears || expMin > prefs.MaxExperienceYears)
            {
                return await SkipAsync(log,
                    $"Experience {extraction.ExperienceRequired} outside preferred {prefs.MinExperienceYears}–{prefs.MaxExperienceYears} yrs",
                    chatId, notify: true, ct);
            }

            // ── 6. Work mode filter ──────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(prefs.PreferredWorkModes) && !string.IsNullOrWhiteSpace(log.WorkMode))
            {
                var preferred = prefs.PreferredWorkModes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!preferred.Any(m => log.WorkMode.Contains(m, StringComparison.OrdinalIgnoreCase)))
                {
                    return await SkipAsync(log,
                        $"Work mode '{log.WorkMode}' not in preferred list ({prefs.PreferredWorkModes})",
                        chatId, notify: true, ct);
                }
            }

            // ── 7. Location filter ───────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(prefs.PreferredLocations) && !string.IsNullOrWhiteSpace(extraction.Location))
            {
                var preferredLocs = prefs.PreferredLocations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var jobLoc = extraction.Location.ToLower();
                if (!preferredLocs.Any(l => jobLoc.Contains(l.ToLower())))
                {
                    return await SkipAsync(log,
                        $"Location '{extraction.Location}' not in preferred list ({prefs.PreferredLocations})",
                        chatId, notify: true, ct);
                }
            }

            // ── 8. Excluded companies ────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(prefs.ExcludedCompanies))
            {
                var excluded = prefs.ExcludedCompanies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (excluded.Any(e => log.CompanyName.Contains(e, StringComparison.OrdinalIgnoreCase)))
                {
                    return await SkipAsync(log, $"Company '{log.CompanyName}' is in excluded list", chatId, notify: false, ct);
                }
            }

            // ── 9. Resume + ATS filter ───────────────────────────────────────
            var resume  = await _dbContext.Resumes.FirstOrDefaultAsync(r => r.IsActive, ct);
            var profile = await _dbContext.UserProfiles.FirstOrDefaultAsync(ct);

            if (resume == null || profile == null)
                return await FailAsync(log, "No active resume or user profile configured", chatId, ct);

            var matchResult = _resumeMatchingService.Match(extraction, resume);
            if (matchResult.AtsScore < prefs.MinAtsScore)
            {
                return await SkipAsync(log,
                    $"ATS score {matchResult.AtsScore}% below minimum {prefs.MinAtsScore}%",
                    chatId, notify: true, ct);
            }

            // ── 10. Recruiter email discovery ────────────────────────────────
            if (string.IsNullOrWhiteSpace(extraction.RecruiterEmail))
            {
                var found = await _emailFinder.FindRecruiterEmailAsync(extraction.CompanyName, extraction.Role);
                if (!string.IsNullOrWhiteSpace(found))
                    extraction.RecruiterEmail = found;
            }

            // ── 11. Save JobPost ─────────────────────────────────────────────
            var jobPost = new JobPost
            {
                CompanyName        = extraction.CompanyName ?? log.CompanyName,
                Role               = extraction.Role ?? log.JobTitle,
                RequiredSkills     = extraction.RequiredSkills,
                RecruiterEmail     = extraction.RecruiterEmail,
                ExperienceRequired = extraction.ExperienceRequired,
                Location           = extraction.Location,
                Source             = JobSource.Upload,
                SourceType         = SourceType.Url,
                RawContent         = scoutedJob.LinkedInUrl,
                AtsScore           = matchResult.AtsScore,
                SkillMatchPercentage = matchResult.MatchPercentage,
                Status             = JobStatus.Pending,
                CreatedAt          = DateTime.UtcNow
            };
            _dbContext.JobPosts.Add(jobPost);
            await _dbContext.SaveChangesAsync(ct);

            log.JobPostId = jobPost.Id;

            // ── 12. Generate email ───────────────────────────────────────────
            var emailDraft = await _geminiService.GenerateEmailAsync(extraction, resume, profile, matchResult, "professional");
            if (!emailDraft.IsSuccessful)
                return await FailAsync(log, $"Email generation failed: {emailDraft.ErrorMessage}", chatId, ct);

            var generatedEmail = new GeneratedEmail
            {
                JobPostId      = jobPost.Id,
                Subject        = emailDraft.Subject,
                Body           = emailDraft.Body,
                RecipientEmail = extraction.RecruiterEmail ?? emailDraft.RecipientEmail ?? "",
                Tone           = "professional",
                CreatedAt      = DateTime.UtcNow
            };
            _dbContext.GeneratedEmails.Add(generatedEmail);
            jobPost.Status    = JobStatus.EmailGenerated;
            jobPost.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(ct);

            // ── 13. Auto-send (only if we have a recipient) ──────────────────
            if (!string.IsNullOrWhiteSpace(generatedEmail.RecipientEmail))
            {
                // Resolve resume file path
                string? resumePath = null;
                if (resume.FilePath != null)
                {
                    var normalized = resume.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                    var full = Path.Combine(Directory.GetCurrentDirectory(), normalized);
                    if (File.Exists(full)) resumePath = full;
                }

                var appBaseUrl = _configuration["AppBaseUrl"];
                var (sent, sendError) = await _emailService.SendEmailAsync(generatedEmail, profile, resumePath, appBaseUrl);

                if (sent)
                {
                    jobPost.Status    = JobStatus.Sent;
                    jobPost.UpdatedAt = DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync(ct);

                    log.EmailSent = true;
                    log.Status    = AutoApplyStatus.Applied;

                    _logger.LogInformation("AutoApply: sent email for {Company} – {Role}", log.CompanyName, log.JobTitle);
                }
                else
                {
                    return await FailAsync(log, $"SMTP send failed: {sendError}", chatId, ct);
                }
            }
            else
            {
                // Draft saved but no email to send — still counts as partial apply
                log.Status    = AutoApplyStatus.Applied;
                log.SkipReason = "No recruiter email found — draft saved for manual review";
                _logger.LogInformation("AutoApply: draft saved (no recruiter email) for {Company} – {Role}", log.CompanyName, log.JobTitle);
            }

            // ── 14. Save log ─────────────────────────────────────────────────
            await SaveLogAsync(log, ct);

            // ── 15. Telegram notification ────────────────────────────────────
            if (chatId != 0)
            {
                var tgMsg = BuildApplyNotification(log, scoutedJob.LinkedInUrl);
                await SendTelegramSafeAsync(chatId, tgMsg);
                log.TelegramNotified = true;
                await _dbContext.SaveChangesAsync(ct);
            }

            // ── 16. Self-notification email ──────────────────────────────────
            if (log.EmailSent)
            {
                var notifyEmail = (await _dbContext.UserJobPreferences.Select(p => p.NotificationEmail).FirstOrDefaultAsync(ct))
                                  ?? profile.Email;
                await SendSelfNotificationAsync(log, scoutedJob.LinkedInUrl, notifyEmail, profile);
            }

            return log;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoApply: unhandled error for {Url}", scoutedJob.LinkedInUrl);
            log.CompanyName = log.CompanyName.Length > 0 ? log.CompanyName : "Unknown";
            log.JobTitle    = log.JobTitle.Length > 0 ? log.JobTitle : "Unknown";
            return await FailAsync(log, ex.Message, chatId, ct);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<string?> FastDuplicateCheckAsync(string url, string? jobId, CancellationToken ct)
    {
        if (await _dbContext.AutoApplyLogs.AnyAsync(l => l.JobUrl == url, ct))
            return "Duplicate URL already in AutoApplyLogs";

        if (!string.IsNullOrWhiteSpace(jobId) &&
            await _dbContext.AutoApplyLogs.AnyAsync(l => l.JobId == jobId, ct))
            return $"Duplicate Job ID '{jobId}' already in AutoApplyLogs";

        if (await _dbContext.JobPosts.AnyAsync(j => j.RawContent == url && j.Status != JobStatus.Skipped, ct))
            return "URL already manually applied via JobPosts";

        return null;
    }

    private async Task<string?> FullDuplicateCheckAsync(string company, string role, CancellationToken ct)
    {
        var companyLower = company.ToLower();
        var roleLower    = role.ToLower();

        if (await _dbContext.AutoApplyLogs.AnyAsync(
                l => l.CompanyName.ToLower() == companyLower && l.JobTitle.ToLower() == roleLower, ct))
            return $"Already auto-applied to {company} – {role}";

        if (await _dbContext.JobPosts.AnyAsync(
                j => j.CompanyName.ToLower() == companyLower &&
                     j.Role.ToLower() == roleLower &&
                     j.Status != JobStatus.Skipped, ct))
            return $"Already applied to {company} – {role} via main pipeline";

        return null;
    }

    private async Task<AutoApplyLog> SkipAsync(AutoApplyLog log, string reason, long chatId, bool notify, CancellationToken ct)
    {
        log.Status     = AutoApplyStatus.Skipped;
        log.SkipReason = reason;
        await SaveLogAsync(log, ct);
        _logger.LogInformation("AutoApply SKIP [{Company} – {Role}]: {Reason}", log.CompanyName, log.JobTitle, reason);

        if (notify && chatId != 0)
        {
            await SendTelegramSafeAsync(chatId,
                $"⏭️ *Auto-Skip*: {EscapeMd(log.CompanyName)} — {EscapeMd(log.JobTitle)}\n_{EscapeMd(reason)}_");
            log.TelegramNotified = true;
            await _dbContext.SaveChangesAsync(ct);
        }
        return log;
    }

    private async Task<AutoApplyLog> FailAsync(AutoApplyLog log, string reason, long chatId, CancellationToken ct)
    {
        log.Status     = AutoApplyStatus.Failed;
        log.SkipReason = reason;
        await SaveLogAsync(log, ct);
        _logger.LogWarning("AutoApply FAIL [{Company} – {Role}]: {Reason}", log.CompanyName, log.JobTitle, reason);

        if (chatId != 0)
        {
            await SendTelegramSafeAsync(chatId,
                $"❌ *Auto-Apply Failed*: {EscapeMd(log.CompanyName)} — {EscapeMd(log.JobTitle)}\n_{EscapeMd(reason)}_");
            log.TelegramNotified = true;
            await _dbContext.SaveChangesAsync(ct);
        }
        return log;
    }

    private async Task SaveLogAsync(AutoApplyLog log, CancellationToken ct)
    {
        if (log.Id == 0)
            _dbContext.AutoApplyLogs.Add(log);
        await _dbContext.SaveChangesAsync(ct);
    }

    private string BuildApplyNotification(AutoApplyLog log, string url)
    {
        var sb = new StringBuilder();
        sb.AppendLine(log.EmailSent
            ? $"✅ *Auto-Applied*: {EscapeMd(log.JobTitle)} at *{EscapeMd(log.CompanyName)}*"
            : $"📋 *Draft Ready* \\(no email found\\): {EscapeMd(log.JobTitle)} at *{EscapeMd(log.CompanyName)}*");

        if (!string.IsNullOrWhiteSpace(log.Location))   sb.AppendLine($"📍 {EscapeMd(log.Location)}");
        if (!string.IsNullOrWhiteSpace(log.WorkMode))   sb.AppendLine($"💼 {EscapeMd(log.WorkMode)}");
        if (!string.IsNullOrWhiteSpace(log.ExperienceRequired)) sb.AppendLine($"🎯 {EscapeMd(log.ExperienceRequired)}");
        sb.AppendLine($"🕐 {DateTime.UtcNow.ToIst():MMM dd, HH:mm} IST");
        sb.AppendLine($"🔗 [View Job]({url})");
        return sb.ToString();
    }

    private async Task SendSelfNotificationAsync(AutoApplyLog log, string url, string toEmail, UserProfile profile)
    {
        try
        {
            var istTime = DateTime.UtcNow.ToIst();
            var subject = $"✅ Auto-Applied: {log.JobTitle} at {log.CompanyName}";
            var body    = $"""
                Auto-Apply Notification — JobFlow AI

                Position   : {log.JobTitle}
                Company    : {log.CompanyName}
                Work Mode  : {log.WorkMode ?? "N/A"}
                Location   : {log.Location ?? "N/A"}
                Experience : {log.ExperienceRequired ?? "N/A"}
                Platform   : {log.Platform}
                Job Link   : {url}
                Applied At : {istTime:dd MMM yyyy, hh:mm tt} IST

                This is an automated notification from JobFlow AI.
                """;

            var tempEmail = new GeneratedEmail
            {
                Subject        = subject,
                Body           = body,
                RecipientEmail = toEmail,
                CreatedAt      = DateTime.UtcNow
            };
            // await _emailService.SendEmailAsync(tempEmail, profile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("AutoApply: self-notification email failed: {Err}", ex.Message);
        }
    }

    private async Task SendTelegramSafeAsync(long chatId, string message)
    {
        try { await _telegramService.SendReplyAsync(chatId, message); }
        catch (Exception ex) { _logger.LogWarning("AutoApply: Telegram notify failed: {Err}", ex.Message); }
    }

    // ── Static helpers ───────────────────────────────────────────────────────

    public static string? ExtractJobId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = Regex.Match(url, @"/jobs/view/(\d+)");
        if (m.Success) return m.Groups[1].Value;
        // LinkedIn post permalinks: ".../posts/...-share-<activityId>-<code>/"
        m = Regex.Match(url, @"-share-(\d+)");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(url, @"JID([A-Za-z0-9]+)");
        if (m.Success) return m.Groups[1].Value;
        return null;
    }

    public static (int? min, int? max) ParseExperienceRange(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var t = text.ToLower();

        // "3-5 years" / "3 to 5 years" / "3–5 yrs"
        var m = Regex.Match(t, @"(\d+)\s*[-–]|\bto\b\s*(\d+)\s*y");
        var range = Regex.Match(t, @"(\d+)\s*[-–to]+\s*(\d+)\s*y");
        if (range.Success)
            return (int.Parse(range.Groups[1].Value), int.Parse(range.Groups[2].Value));

        // "3+ years" / "minimum 3 years"
        var plus = Regex.Match(t, @"(\d+)\s*\+");
        if (plus.Success) return (int.Parse(plus.Groups[1].Value), 99);

        var minWord = Regex.Match(t, @"minimum\s+(\d+)");
        if (minWord.Success) return (int.Parse(minWord.Groups[1].Value), 99);

        // "3 years"
        var single = Regex.Match(t, @"(\d+)\s*y");
        if (single.Success)
        {
            var v = int.Parse(single.Groups[1].Value);
            return (v, v);
        }

        return (null, null);
    }

    private static string DetectWorkMode(string text)
    {
        var t = text.ToLower();
        var modes = new List<string>();
        if (t.Contains("remote")) modes.Add("Remote");
        if (t.Contains("hybrid")) modes.Add("Hybrid");
        if (t.Contains("onsite") || t.Contains("on-site") || t.Contains("in office") || t.Contains("in-office"))
            modes.Add("Onsite");
        return modes.Count > 0 ? string.Join("/", modes) : "Not specified";
    }

    private static string EscapeMd(string? s) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Replace("_", "\\_").Replace("*", "\\*").Replace("[", "\\[").Replace("`", "\\`");
}
