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
    private readonly ILogger<EmailController> _logger;

    public EmailController(AppDbContext dbContext, EmailService emailService, ILogger<EmailController> logger)
    {
        _dbContext = dbContext;
        _emailService = emailService;
        _logger = logger;
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

        // Prioritize user's requested specific local resume, fallback to active database resume
        string? attachmentPath = null;
        var folderResumePath = Path.Combine(Directory.GetCurrentDirectory(), "Resume", "Chaitanya_Mallelli_Resume.pdf");
        var specificPath = @"C:\Users\Susmita sahoo\Downloads\Chaitanya_Mallelli_Resume.pdf";

        if (System.IO.File.Exists(folderResumePath))
        {
            attachmentPath = folderResumePath;
            _logger.LogInformation("Using project-folder relative resume: {Path}", folderResumePath);
        }
        else if (System.IO.File.Exists(specificPath))
        {
            attachmentPath = specificPath;
            _logger.LogInformation("Using absolute local resume: {Path}", specificPath);
        }
        else
        {
            var activeResume = await _dbContext.Resumes.FirstOrDefaultAsync(r => r.IsActive);
            if (activeResume != null && !string.IsNullOrEmpty(activeResume.FilePath))
            {
                if (activeResume.FilePath.StartsWith("Resume", StringComparison.OrdinalIgnoreCase))
                {
                    attachmentPath = Path.Combine(Directory.GetCurrentDirectory(), activeResume.FilePath.Replace("/", "\\"));
                }
                else
                {
                    attachmentPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", activeResume.FilePath.TrimStart('/'));
                }
                _logger.LogInformation("Using active database resume: {Path}", attachmentPath);
            }
        }

        var (success, errorMessage) = await _emailService.SendEmailAsync(email, profile, attachmentPath);

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
            query = query.Where(e =>
                e.RecipientEmail.Contains(search) ||
                e.Subject.Contains(search) ||
                (e.JobPost != null && (e.JobPost.CompanyName.Contains(search) || e.JobPost.Role.Contains(search))));
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
