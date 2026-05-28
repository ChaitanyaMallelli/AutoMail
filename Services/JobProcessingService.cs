using JobAutomation.Data;
using JobAutomation.DTOs;
using JobAutomation.Models;
using Microsoft.EntityFrameworkCore;

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

    public async Task<int> ProcessTextAsync(string text, JobSource source, long? chatId = null)
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

        var jobId = await ProcessExtractionAsync(extraction, source, SourceType.Text, text, null, chatId);
        if (chatId.HasValue) TelegramProgressTracker.SetLatestJobId(chatId.Value, jobId);
        return jobId;
    }

    public async Task<int> ProcessImageAsync(byte[] imageBytes, string mimeType, JobSource source, long? chatId = null)
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

        var jobId = await ProcessExtractionAsync(extraction, source, SourceType.Image, null, relativePath, chatId);
        if (chatId.HasValue) TelegramProgressTracker.SetLatestJobId(chatId.Value, jobId);
        return jobId;
    }

    public async Task<int> ProcessPdfAsync(byte[] pdfBytes, JobSource source, long? chatId = null)
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

        var jobId = await ProcessExtractionAsync(extraction, source, SourceType.Pdf, null, relativePath, chatId);
        if (chatId.HasValue) TelegramProgressTracker.SetLatestJobId(chatId.Value, jobId);
        return jobId;
    }

    private async Task<int> ProcessExtractionAsync(
        JobExtractionResult extraction, JobSource source, SourceType sourceType,
        string? rawContent, string? imagePath, long? chatId = null)
    {
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
                var emailDraft = await _geminiService.GenerateEmailAsync(extraction, resume, profile, matchResult);
                if (emailDraft.IsSuccessful)
                {
                    var generatedEmail = new GeneratedEmail
                    {
                        JobPostId = jobPost.Id,
                        Subject = emailDraft.Subject,
                        Body = emailDraft.Body,
                        RecipientEmail = extraction.RecruiterEmail ?? emailDraft.RecipientEmail,
                        CreatedAt = DateTime.UtcNow
                    };

                    _dbContext.GeneratedEmails.Add(generatedEmail);
                    jobPost.Status = JobStatus.EmailGenerated;
                    jobPost.UpdatedAt = DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync();

                    if (chatId.HasValue)
                    {
                        TelegramProgressTracker.UpdateProgress(chatId.Value, "Step 4: Professional email draft generated successfully! ✅");
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
