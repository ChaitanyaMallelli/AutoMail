using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using JobAutomation.Data;
using JobAutomation.Models;
using JobAutomation.Services;
using System.Text;

namespace JobAutomation.Controllers;

public class EmailController : Controller
{
    private readonly AppDbContext _dbContext;
    private readonly EmailService _emailService;
    private readonly JobProcessingService _jobProcessingService;
    private readonly ILogger<EmailController> _logger;
    private readonly IConfiguration _configuration;

    public EmailController(
        AppDbContext dbContext,
        EmailService emailService,
        JobProcessingService jobProcessingService,
        ILogger<EmailController> logger,
        IConfiguration configuration)
    {
        _dbContext = dbContext;
        _emailService = emailService;
        _jobProcessingService = jobProcessingService;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<IActionResult> Preview(int id)
    {
        var jobPost = await _dbContext.JobPosts
            .Include(j => j.GeneratedEmail)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (jobPost == null)
            return NotFound();

        return View(jobPost);
    }

    [HttpPost]
    public async Task<IActionResult> Update(int id, string subject, string body, string recipientEmail)
    {
        var email = await _dbContext.GeneratedEmails.FirstOrDefaultAsync(e => e.JobPostId == id);
        if (email == null)
            return NotFound();

        email.Subject = subject;
        email.Body = body;
        email.RecipientEmail = recipientEmail;

        await _dbContext.SaveChangesAsync();

        TempData["Success"] = "Email draft updated successfully!";
        return RedirectToAction("Preview", new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Regenerate(int id, string tone)
    {
        var job = await _dbContext.JobPosts
            .Include(j => j.GeneratedEmail)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job == null) return NotFound();

        if (job.GeneratedEmail != null)
        {
            _dbContext.GeneratedEmails.Remove(job.GeneratedEmail);
            await _dbContext.SaveChangesAsync();
        }

        await _jobProcessingService.ContinueProcessingAsync(id, tone);
        
        TempData["Success"] = $"Email regenerated with {tone} tone!";
        return RedirectToAction("Preview", new { id });
    }

    [HttpPost]
    public async Task<IActionResult> UpdateStatus(int id, string status, string? notes)
    {
        var job = await _dbContext.JobPosts.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound();

        if (Enum.TryParse<JobStatus>(status, out var parsedStatus))
        {
            job.Status = parsedStatus;
        }

        if (!string.IsNullOrEmpty(notes))
        {
            job.ResponseNotes = notes;
        }

        await _dbContext.SaveChangesAsync();

        TempData["Success"] = "Application status updated successfully!";
        return RedirectToAction("Preview", new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Approve(int id)
    {
        var email = await _dbContext.GeneratedEmails
            .Include(e => e.JobPost)
            .FirstOrDefaultAsync(e => e.JobPostId == id);

        if (email == null)
            return NotFound();

        email.IsApproved = true;
        if (email.JobPost != null)
        {
            email.JobPost.Status = JobStatus.Approved;
            email.JobPost.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();

        TempData["Success"] = "Email approved! Ready to send.";
        return RedirectToAction("Preview", new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Send(int id)
    {
        var email = await _dbContext.GeneratedEmails
            .Include(e => e.JobPost)
            .FirstOrDefaultAsync(e => e.JobPostId == id);

        if (email == null)
            return NotFound();

        var profile = await _dbContext.UserProfiles.FirstOrDefaultAsync();
        if (profile == null)
        {
            TempData["Error"] = "User profile not found. Please configure your profile first.";
            return RedirectToAction("Preview", new { id });
        }

        string? attachmentPath = null;
        var activeResume = await _dbContext.Resumes.FirstOrDefaultAsync(r => r.IsActive);

        if (activeResume != null && !string.IsNullOrEmpty(activeResume.FilePath))
        {
            // Normalize: convert forward slashes to OS separator and resolve from app root
            var normalizedRelative = activeResume.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            attachmentPath = Path.Combine(Directory.GetCurrentDirectory(), normalizedRelative);
            _logger.LogInformation("Resume attachment path resolved: {Path}", attachmentPath);

            if (!System.IO.File.Exists(attachmentPath))
            {
                _logger.LogWarning("Resume file not found at resolved path: {Path} — email will be sent without attachment.", attachmentPath);
                attachmentPath = null;
            }
        }
        else if (activeResume != null)
        {
            _logger.LogWarning("Active resume has no file path (text-only resume) — email will be sent without attachment.");
        }

        var baseUrl = _configuration?["AppBaseUrl"];
        var (success, errorMessage) = await _emailService.SendEmailAsync(email, profile, attachmentPath, baseUrl);

        if (success)
        {
            email.IsSent = true;
            email.SentAt = DateTime.UtcNow;
            email.ErrorMessage = null;
            if (email.JobPost != null)
            {
                email.JobPost.Status = JobStatus.Sent;
                email.JobPost.UpdatedAt = DateTime.UtcNow;
            }
            TempData["Success"] = "Email sent successfully! 🎉";
        }
        else
        {
            email.RetryCount++;
            email.ErrorMessage = errorMessage;
            if (email.JobPost != null)
            {
                email.JobPost.Status = JobStatus.Failed;
                email.JobPost.UpdatedAt = DateTime.UtcNow;
            }
            TempData["Error"] = $"Failed to send email: {errorMessage}";
        }

        await _dbContext.SaveChangesAsync();
        return RedirectToAction("Preview", new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Retry(int id)
    {
        return await Send(id);
    }

    public async Task<IActionResult> History(string? search, string? status)
    {
        var query = _dbContext.GeneratedEmails
            .Include(e => e.JobPost)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(e =>
                e.RecipientEmail.ToLower().Contains(searchLower) ||
                e.Subject.ToLower().Contains(searchLower) ||
                (e.JobPost != null && (e.JobPost.CompanyName.ToLower().Contains(searchLower) || e.JobPost.Role.ToLower().Contains(searchLower))));
        }

        if (status == "sent")
            query = query.Where(e => e.IsSent);
        else if (status == "pending")
            query = query.Where(e => !e.IsSent && !e.IsApproved);
        else if (status == "approved")
            query = query.Where(e => e.IsApproved && !e.IsSent);
        else if (status == "failed")
            query = query.Where(e => e.ErrorMessage != null && !e.IsSent);

        var emails = await query.OrderByDescending(e => e.CreatedAt).ToListAsync();

        ViewBag.Search = search;
        ViewBag.Status = status;

        return View(emails);
    }

    public async Task<IActionResult> Export()
    {
        var emails = await _dbContext.GeneratedEmails
            .Include(e => e.JobPost)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        var csv = new StringBuilder();
        csv.AppendLine("Company,Role,Recipient Email,Subject,Status,Sent Date,Created Date");

        foreach (var email in emails)
        {
            var company = email.JobPost?.CompanyName?.Replace(",", ";") ?? "N/A";
            var role = email.JobPost?.Role?.Replace(",", ";") ?? "N/A";
            var recipientEmail = email.RecipientEmail?.Replace(",", ";") ?? "";
            var subject = email.Subject?.Replace(",", ";") ?? "";
            var sentStatus = email.IsSent ? "Sent" : (email.ErrorMessage != null ? "Failed" : (email.IsApproved ? "Approved" : "Pending"));
            var sentDate = email.SentAt?.ToString("yyyy-MM-dd HH:mm") ?? "N/A";
            var createdDate = email.CreatedAt.ToString("yyyy-MM-dd HH:mm");

            csv.AppendLine($"{company},{role},{recipientEmail},{subject},{sentStatus},{sentDate},{createdDate}");
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"applications_export_{DateTime.Now:yyyyMMdd}.csv");
    }
}
