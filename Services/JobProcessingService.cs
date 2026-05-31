using JobAutomation.Data;
using JobAutomation.DTOs;
using JobAutomation.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace JobAutomation.Services;

public class JobProcessingService
{
    private readonly AppDbContext _dbContext;
    private readonly GeminiService _geminiService;
    private readonly ResumeMatchingService _resumeMatchingService;
    private readonly ILogger<JobProcessingService> _logger;

    public JobProcessingService(
        AppDbContext dbContext,
        GeminiService geminiService,
        ResumeMatchingService resumeMatchingService,
        ILogger<JobProcessingService> logger)
    {
        _dbContext = dbContext;
        _geminiService = geminiService;
        _resumeMatchingService = resumeMatchingService;
        _logger = logger;
    }

    public async Task<int> ProcessTextAsync(string text, JobSource source, long? chatId = null, string tone = "professional")
    {
        _logger.LogInformation("Processing text job post from {Source}", source);

        if (chatId.HasValue)
        {
            TelegramProgressTracker.ResetProgress(chatId.Value);
            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 1: Received job post from Telegram ✅");
            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 2: AI extracting details (Company, Role, Skills, Recruiter Email)... ⏳");
        }

        var extraction = await _geminiService.ExtractJobDetailsFromTextAsync(text);
        if (!extraction.IsSuccessful)
        {
            _logger.LogWarning("Job extraction failed: {Error}", extraction.ErrorMessage);
            if (chatId.HasValue)
            {
                TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 2: AI extraction failed ❌ ({extraction.ErrorMessage})");
            }
            // Still save it so user can see the failure
            var savedId = await SaveJobPostAsync(extraction, source, SourceType.Text, text, null, chatId);
            if (chatId.HasValue) TelegramProgressTracker.SetLatestJobId(chatId.Value, savedId);
            return savedId;
        }

        if (chatId.HasValue)
        {
            TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 2: AI extracted details successfully! (Company: {extraction.CompanyName}, Role: {extraction.Role}) ✅");
        }

        var jobId = await ProcessExtractionAsync(extraction, source, SourceType.Text, text, null, chatId, tone);
        if (chatId.HasValue) TelegramProgressTracker.SetLatestJobId(chatId.Value, jobId);
        return jobId;
    }

    public async Task<int> ProcessImageAsync(byte[] imageBytes, string mimeType, JobSource source, long? chatId = null, string tone = "professional")
    {
        _logger.LogInformation("Processing image job post from {Source}", source);

        if (chatId.HasValue)
        {
            TelegramProgressTracker.ResetProgress(chatId.Value);
            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 1: Received screenshot from Telegram ✅");
            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 2: AI reading image text and extracting job details... ⏳");
        }

        // Save image to disk
        var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{Guid.NewGuid()}{GetExtension(mimeType)}";
        var filePath = Path.Combine(uploadsDir, fileName);
        await File.WriteAllBytesAsync(filePath, imageBytes);
        var relativePath = $"/uploads/{fileName}";

        var extraction = await _geminiService.ExtractJobDetailsFromImageAsync(imageBytes, mimeType);
        if (!extraction.IsSuccessful)
        {
            if (chatId.HasValue)
            {
                TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 2: AI extraction failed ❌ ({extraction.ErrorMessage})");
            }
            var savedId = await SaveJobPostAsync(extraction, source, SourceType.Image, null, relativePath, chatId);
            if (chatId.HasValue) TelegramProgressTracker.SetLatestJobId(chatId.Value, savedId);
            return savedId;
        }

        if (chatId.HasValue)
        {
            TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 2: AI extracted details successfully! (Company: {extraction.CompanyName}, Role: {extraction.Role}) ✅");
        }

        var jobId = await ProcessExtractionAsync(extraction, source, SourceType.Image, null, relativePath, chatId, tone);
        if (chatId.HasValue) TelegramProgressTracker.SetLatestJobId(chatId.Value, jobId);
        return jobId;
    }

    public async Task<int> ProcessPdfAsync(byte[] pdfBytes, JobSource source, long? chatId = null, string tone = "professional")
    {
        _logger.LogInformation("Processing PDF job post from {Source}", source);

        if (chatId.HasValue)
        {
            TelegramProgressTracker.ResetProgress(chatId.Value);
            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 1: Received PDF file from Telegram ✅");
            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 2: AI extracting details from PDF description... ⏳");
        }

        var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
        Directory.CreateDirectory(uploadsDir);
        var fileName = $"{Guid.NewGuid()}.pdf";
        var filePath = Path.Combine(uploadsDir, fileName);
        await File.WriteAllBytesAsync(filePath, pdfBytes);
        var relativePath = $"/uploads/{fileName}";

        var extraction = await _geminiService.ExtractJobDetailsFromPdfAsync(pdfBytes);
        if (!extraction.IsSuccessful)
        {
            if (chatId.HasValue)
            {
                TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 2: AI extraction failed ❌ ({extraction.ErrorMessage})");
            }
            var savedId = await SaveJobPostAsync(extraction, source, SourceType.Pdf, null, relativePath, chatId);
            if (chatId.HasValue) TelegramProgressTracker.SetLatestJobId(chatId.Value, savedId);
            return savedId;
        }

        if (chatId.HasValue)
        {
            TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 2: AI extracted details successfully! (Company: {extraction.CompanyName}, Role: {extraction.Role}) ✅");
        }

        var jobId = await ProcessExtractionAsync(extraction, source, SourceType.Pdf, null, relativePath, chatId, tone);
        if (chatId.HasValue) TelegramProgressTracker.SetLatestJobId(chatId.Value, jobId);
        return jobId;
    }

    public async Task<int> ProcessUrlAsync(string url, JobSource source, long? chatId = null, string tone = "professional")
    {
        _logger.LogInformation("Processing URL job post from {Source}: {Url}", source, url);

        if (chatId.HasValue)
        {
            TelegramProgressTracker.ResetProgress(chatId.Value);
            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 1: Received job URL from Telegram ✅");
            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 2: Fetching page content and extracting details... ⏳");
        }

        try
        {
            // Fetch URL content
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; AutoMail/1.0)");
            httpClient.Timeout = TimeSpan.FromSeconds(15);
            var htmlContent = await httpClient.GetStringAsync(url);

            var extraction = await _geminiService.ExtractJobDetailsFromUrlContentAsync(htmlContent, url);
            if (!extraction.IsSuccessful)
            {
                if (chatId.HasValue)
                {
                    TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 2: AI extraction from URL failed ❌ ({extraction.ErrorMessage})");
                }
                var savedId = await SaveJobPostAsync(extraction, source, SourceType.Url, url, null, chatId);
                if (chatId.HasValue) TelegramProgressTracker.SetLatestJobId(chatId.Value, savedId);
                return savedId;
            }

            if (chatId.HasValue)
            {
                TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 2: AI extracted details from URL! (Company: {extraction.CompanyName}, Role: {extraction.Role}) ✅");
            }

            var jobId = await ProcessExtractionAsync(extraction, source, SourceType.Url, url, null, chatId, tone);
            if (chatId.HasValue) TelegramProgressTracker.SetLatestJobId(chatId.Value, jobId);
            return jobId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch or process URL: {Url}", url);
            if (chatId.HasValue)
            {
                TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 2: Failed to fetch URL ❌ ({ex.Message})");
            }
            throw;
        }
    }

    /// <summary>
    /// Check if a similar job (same company + role) already exists in the database
    /// </summary>
    public async Task<JobPost?> FindDuplicateAsync(string companyName, string role)
    {
        return await _dbContext.JobPosts
            .Where(j => j.CompanyName.ToLower() == companyName.ToLower()
                     && j.Role.ToLower() == role.ToLower()
                     && j.Status != JobStatus.Skipped)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Detect if a text string contains a URL
    /// </summary>
    public static bool ContainsUrl(string text)
    {
        return Regex.IsMatch(text.Trim(), @"^https?://\S+$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Continue processing a job post after duplicate/filter confirmation (used by Telegram callbacks)
    /// </summary>
    public async Task<int> ContinueProcessingAsync(int jobId, string tone = "professional")
    {
        var job = await _dbContext.JobPosts.FirstOrDefaultAsync(j => j.Id == jobId);
        if (job == null) return 0;

        var resume = await _dbContext.Resumes.FirstOrDefaultAsync(r => r.IsActive);
        var profile = await _dbContext.UserProfiles.FirstOrDefaultAsync();

        if (resume == null || profile == null) return jobId;

        var extraction = new JobExtractionResult
        {
            CompanyName = job.CompanyName,
            Role = job.Role,
            RequiredSkills = job.RequiredSkills ?? "",
            RecruiterEmail = job.RecruiterEmail,
            ExperienceRequired = job.ExperienceRequired,
            Location = job.Location,
            RawContent = job.RawContent,
            IsSuccessful = true
        };

        var matchResult = _resumeMatchingService.Match(extraction, resume);

        try
        {
            var emailDraft = await _geminiService.GenerateEmailAsync(extraction, resume, profile, matchResult, tone);
            if (emailDraft.IsSuccessful)
            {
                var generatedEmail = new GeneratedEmail
                {
                    JobPostId = job.Id,
                    Subject = emailDraft.Subject,
                    Body = emailDraft.Body,
                    RecipientEmail = extraction.RecruiterEmail ?? emailDraft.RecipientEmail,
                    Tone = tone,
                    CreatedAt = DateTime.UtcNow
                };

                _dbContext.GeneratedEmails.Add(generatedEmail);
                job.Status = JobStatus.EmailGenerated;
                job.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate email for job post {JobPostId}", job.Id);
        }

        return jobId;
    }

    private async Task<int> ProcessExtractionAsync(
        JobExtractionResult extraction, JobSource source, SourceType sourceType,
        string? rawContent, string? imagePath, long? chatId = null, string tone = "professional")
    {
        // Step 2.5: Duplicate detection
        var duplicate = await FindDuplicateAsync(extraction.CompanyName, extraction.Role);
        if (duplicate != null && chatId.HasValue)
        {
            TelegramProgressTracker.UpdateProgress(chatId.Value,
                $"Step 2.5: ⚠️ Duplicate found! Already applied to {extraction.CompanyName} - {extraction.Role} on {duplicate.CreatedAt:MMM dd} (Status: {duplicate.Status})");
            // Store info for Telegram callback handling — we continue but flag it
            _logger.LogInformation("Duplicate job detected: {Company} - {Role} (existing JobId: {ExistingId})", extraction.CompanyName, extraction.Role, duplicate.Id);
        }

        if (chatId.HasValue)
        {
            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 3: Comparing job requirements with your active Resume... ⏳");
        }

        // Get active resume
        var resume = await _dbContext.Resumes.FirstOrDefaultAsync(r => r.IsActive);
        var profile = await _dbContext.UserProfiles.FirstOrDefaultAsync();

        ResumeMatchResult? matchResult = null;
        if (resume != null)
        {
            matchResult = _resumeMatchingService.Match(extraction, resume);
        }

        if (chatId.HasValue)
        {
            if (resume != null && matchResult != null)
            {
                TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 3: Resume match completed: ATS Score: {matchResult.AtsScore}%, Skills Match: {matchResult.MatchPercentage}% ✅");

                // Step 3.5: Smart filter — warn on low match
                if (matchResult.MatchPercentage < 30)
                {
                    var missingSkillsText = matchResult.MissingSkills.Any()
                        ? string.Join(", ", matchResult.MissingSkills.Take(5))
                        : "various required skills";
                    TelegramProgressTracker.UpdateProgress(chatId.Value,
                        $"Step 3.5: ⚠️ Low match ({matchResult.MatchPercentage}%). Missing: {missingSkillsText}");
                }
            }
            else
            {
                TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 3: Skipped resume matching (No active resume found) ⚠️");
            }
        }

        // Save job post
        var jobPost = new JobPost
        {
            CompanyName = extraction.CompanyName,
            Role = extraction.Role,
            RequiredSkills = extraction.RequiredSkills,
            RecruiterEmail = extraction.RecruiterEmail,
            ExperienceRequired = extraction.ExperienceRequired,
            Location = extraction.Location,
            Source = source,
            SourceType = sourceType,
            RawContent = rawContent ?? extraction.RawContent,
            ImagePath = imagePath,
            AtsScore = matchResult?.AtsScore ?? 0,
            SkillMatchPercentage = matchResult?.MatchPercentage ?? 0,
            Status = JobStatus.Pending,
            TelegramChatId = chatId,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.JobPosts.Add(jobPost);
        await _dbContext.SaveChangesAsync();

        // Generate email if we have resume and profile
        if (resume != null && profile != null && matchResult != null)
        {
            if (chatId.HasValue)
            {
                TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 4: AI generating a professional email draft... ⏳");
            }

            try
            {
                var emailDraft = await _geminiService.GenerateEmailAsync(extraction, resume, profile, matchResult, tone);
                if (emailDraft.IsSuccessful)
                {
                    var generatedEmail = new GeneratedEmail
                    {
                        JobPostId = jobPost.Id,
                        Subject = emailDraft.Subject,
                        Body = emailDraft.Body,
                        RecipientEmail = extraction.RecruiterEmail ?? emailDraft.RecipientEmail,
                        Tone = tone,
                        CreatedAt = DateTime.UtcNow
                    };

                    _dbContext.GeneratedEmails.Add(generatedEmail);
                    jobPost.Status = JobStatus.EmailGenerated;
                    jobPost.UpdatedAt = DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync();

                    if (chatId.HasValue)
                    {
                        TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 4: Professional email draft generated successfully! ✅");
                        if (chatId.Value == 999)
                            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 5: Email draft ready! Review and approve on dashboard ✅");
                        else
                            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 5: Sending approval request to your Telegram... ⏳");
                    }
                }
                else if (chatId.HasValue)
                {
                    TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 4: Email generation failed ❌ ({emailDraft.ErrorMessage})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate email for job post {JobPostId}", jobPost.Id);
                if (chatId.HasValue)
                {
                    TelegramProgressTracker.UpdateProgress(chatId.Value, $"Step 4: Email generation crashed ❌ ({ex.Message})");
                }
            }
        }
        else if (chatId.HasValue)
        {
            TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 4: Skipped email generation (No resume or profile configured) ⚠️");
        }

        _logger.LogInformation("Job post {Id} created for {Company} - {Role}", jobPost.Id, jobPost.CompanyName, jobPost.Role);
        return jobPost.Id;
    }

    private async Task<int> SaveJobPostAsync(
        JobExtractionResult extraction, JobSource source, SourceType sourceType,
        string? rawContent, string? imagePath, long? chatId = null)
    {
        var jobPost = new JobPost
        {
            CompanyName = extraction.CompanyName ?? "Unknown",
            Role = extraction.Role ?? "Unknown",
            RequiredSkills = extraction.RequiredSkills,
            RecruiterEmail = extraction.RecruiterEmail,
            ExperienceRequired = extraction.ExperienceRequired,
            Location = extraction.Location,
            Source = source,
            SourceType = sourceType,
            RawContent = rawContent ?? extraction.RawContent,
            ImagePath = imagePath,
            Status = JobStatus.Pending,
            TelegramChatId = chatId,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.JobPosts.Add(jobPost);
        await _dbContext.SaveChangesAsync();
        return jobPost.Id;
    }

    private string GetExtension(string mimeType) => mimeType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "application/pdf" => ".pdf",
        _ => ".bin"
    };
}
