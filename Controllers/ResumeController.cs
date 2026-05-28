using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using JobAutomation.Data;
using JobAutomation.Models;
using System.IO;

namespace JobAutomation.Controllers;

public class ResumeController : Controller
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<ResumeController> _logger;

    public ResumeController(AppDbContext dbContext, ILogger<ResumeController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var activeResume = await _dbContext.Resumes.FirstOrDefaultAsync(r => r.IsActive);
        var allResumes = await _dbContext.Resumes.OrderByDescending(r => r.UploadedAt).ToListAsync();
        var profile = await _dbContext.UserProfiles.FirstOrDefaultAsync();

        ViewBag.Profile = profile;
        ViewBag.Resumes = allResumes;
        return View(activeResume);
    }

    [HttpPost]
    public async Task<IActionResult> Upload(string fullText, string skills, string experience, string education, IFormFile? resumeFile)
    {
        try
        {
            // By default, set new resume as active and deactivate existing ones
            var existingResumes = await _dbContext.Resumes.ToListAsync();
            foreach (var r in existingResumes)
                r.IsActive = false;

            string? filePath = null;
            if (resumeFile != null && resumeFile.Length > 0)
            {
                var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "Resume");
                Directory.CreateDirectory(uploadsDir);
                var fileName = $"resume_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(resumeFile.FileName)}";
                var absolutePath = Path.Combine(uploadsDir, fileName);
                using var stream = new FileStream(absolutePath, FileMode.Create);
                await resumeFile.CopyToAsync(stream);
                filePath = $"Resume/{fileName}";
                _logger.LogInformation("Saved uploaded resume file directly under Resume folder: {Path}", absolutePath);
            }

            // Parse skills into JSON array
            var skillsList = skills?.Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList() ?? new List<string>();

            var resume = new Resume
            {
                FullText = fullText ?? "",
                Skills = JsonConvert.SerializeObject(skillsList),
                Experience = experience,
                Education = education,
                FilePath = filePath,
                IsActive = true,
                UploadedAt = DateTime.UtcNow
            };

            _dbContext.Resumes.Add(resume);
            await _dbContext.SaveChangesAsync();

            TempData["Success"] = "Resume uploaded successfully and set as default!";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading resume");
            TempData["Error"] = $"Failed to upload resume: {ex.Message}";
        }

        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> SetDefault(int id)
    {
        try
        {
            var resumes = await _dbContext.Resumes.ToListAsync();
            var target = resumes.FirstOrDefault(r => r.Id == id);
            if (target == null)
            {
                TempData["Error"] = "Resume not found.";
                return RedirectToAction("Index");
            }

            foreach (var r in resumes)
            {
                r.IsActive = (r.Id == id);
            }

            await _dbContext.SaveChangesAsync();
            TempData["Success"] = "Default resume updated successfully!";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting default resume");
            TempData["Error"] = $"Failed to set default: {ex.Message}";
        }

        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var resume = await _dbContext.Resumes.FirstOrDefaultAsync(r => r.Id == id);
            if (resume == null)
            {
                TempData["Error"] = "Resume not found.";
                return RedirectToAction("Index");
            }

            // Delete local file if it exists
            if (!string.IsNullOrEmpty(resume.FilePath))
            {
                var fullPath = Path.Combine(Directory.GetCurrentDirectory(), resume.FilePath.TrimStart('/'));
                if (System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                    _logger.LogInformation("Deleted local resume file: {Path}", fullPath);
                }
            }

            _dbContext.Resumes.Remove(resume);
            await _dbContext.SaveChangesAsync();

            // If the deleted resume was active, set the next available one as active
            if (resume.IsActive)
            {
                var nextResume = await _dbContext.Resumes.FirstOrDefaultAsync();
                if (nextResume != null)
                {
                    nextResume.IsActive = true;
                    await _dbContext.SaveChangesAsync();
                }
            }

            TempData["Success"] = "Resume deleted successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting resume");
            TempData["Error"] = $"Failed to delete resume: {ex.Message}";
        }

        return RedirectToAction("Index");
    }

    [HttpGet]
    public async Task<IActionResult> ViewFile(int id)
    {
        var resume = await _dbContext.Resumes.FirstOrDefaultAsync(r => r.Id == id);
        if (resume == null || string.IsNullOrEmpty(resume.FilePath))
            return NotFound("Resume file path is empty.");

        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), resume.FilePath.TrimStart('/'));
        if (!System.IO.File.Exists(fullPath))
            return NotFound($"Resume file not found at path: {fullPath}");

        var mimeType = "application/pdf";
        if (fullPath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            mimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        else if (fullPath.EndsWith(".doc", StringComparison.OrdinalIgnoreCase))
            mimeType = "application/msword";

        return PhysicalFile(fullPath, mimeType, Path.GetFileName(fullPath));
    }

    [HttpPost]
    public async Task<IActionResult> UpdateProfile(string fullName, string email, string phone, string linkedInUrl)
    {
        try
        {
            var profile = await _dbContext.UserProfiles.FirstOrDefaultAsync();
            if (profile == null)
            {
                profile = new UserProfile();
                _dbContext.UserProfiles.Add(profile);
            }

            profile.FullName = fullName;
            profile.Email = email;
            profile.Phone = phone;
            profile.LinkedInUrl = linkedInUrl;

            await _dbContext.SaveChangesAsync();
            TempData["Success"] = "Profile updated successfully!";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating profile");
            TempData["Error"] = $"Failed to update profile: {ex.Message}";
        }

        return RedirectToAction("Index");
    }
}
