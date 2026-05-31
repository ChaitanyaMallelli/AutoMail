using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using JobAutomation.Data;
using JobAutomation.Models;
using JobAutomation.Services;

namespace JobAutomation.Controllers;

public class DashboardController : Controller
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<DashboardController> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public DashboardController(AppDbContext dbContext, ILogger<DashboardController> logger, IServiceScopeFactory scopeFactory)
    {
        _dbContext = dbContext;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<IActionResult> Index(string? search, string? status, string? sort)
    {
        var query = _dbContext.JobPosts
            .Include(j => j.GeneratedEmail)
            .AsQueryable();

        // Search filter
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();
            var searchLower = search.ToLower();
            query = query.Where(j =>
                j.CompanyName.ToLower().Contains(searchLower) ||
                j.Role.ToLower().Contains(searchLower) ||
                (j.Location != null && j.Location.ToLower().Contains(searchLower)) ||
                (j.RequiredSkills != null && j.RequiredSkills.ToLower().Contains(searchLower)));
        }

        // Status filter
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<JobStatus>(status, out var jobStatus))
        {
            query = query.Where(j => j.Status == jobStatus);
        }

        // Sorting
        query = sort switch
        {
            "company" => query.OrderBy(j => j.CompanyName),
            "role" => query.OrderBy(j => j.Role),
            "ats" => query.OrderByDescending(j => j.AtsScore),
            "match" => query.OrderByDescending(j => j.SkillMatchPercentage),
            _ => query.OrderByDescending(j => j.CreatedAt)
        };

        var jobPosts = await query.ToListAsync();

        // Dashboard stats
        var totalJobs = await _dbContext.JobPosts.CountAsync(j => j.Status != JobStatus.Skipped);
        var pendingJobs = await _dbContext.JobPosts.CountAsync(j => j.Status == JobStatus.Pending || j.Status == JobStatus.EmailGenerated);
        var sentJobs = await _dbContext.JobPosts.CountAsync(j => 
            j.Status == JobStatus.Sent || 
            j.Status == JobStatus.FollowUpSent || 
            j.Status == JobStatus.InterviewScheduled || 
            j.Status == JobStatus.Rejected || 
            j.Status == JobStatus.Offered || 
            j.Status == JobStatus.Ghosted);
            
        var failedJobs = await _dbContext.JobPosts.CountAsync(j => j.Status == JobStatus.Failed);
        
        var interviews = await _dbContext.JobPosts.CountAsync(j => j.Status == JobStatus.InterviewScheduled);
        var offers = await _dbContext.JobPosts.CountAsync(j => j.Status == JobStatus.Offered);
        
        var avgMatch = totalJobs > 0
            ? (int)await _dbContext.JobPosts.Where(j => j.Status != JobStatus.Skipped).AverageAsync(j => (double)j.SkillMatchPercentage)
            : 0;

        var responseRate = sentJobs > 0 
            ? (int)Math.Round((double)(interviews + offers + await _dbContext.JobPosts.CountAsync(j => j.Status == JobStatus.Rejected)) / sentJobs * 100)
            : 0;

        var scoutedJobs = await _dbContext.ScoutedJobs
            .OrderByDescending(s => s.CreatedAt)
            .Take(100)
            .ToListAsync();

        ViewBag.TotalJobs = totalJobs;
        ViewBag.PendingJobs = pendingJobs;
        ViewBag.SentJobs = sentJobs;
        ViewBag.FailedJobs = failedJobs;
        ViewBag.Interviews = interviews;
        ViewBag.Offers = offers;
        ViewBag.ResponseRate = responseRate;
        ViewBag.AvgMatch = avgMatch;
        ViewBag.Search = search;
        ViewBag.Status = status;
        ViewBag.Sort = sort;
        ViewBag.ScoutedJobs = scoutedJobs;

        return View(jobPosts);
    }

    [HttpPost]
    public async Task<IActionResult> RunScoutNow([FromServices] JobScoutManager scoutManager)
    {
        try
        {
            await scoutManager.RunScoutCycleAsync();
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run manual scout cycle.");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> ApplyScoutedJob(int id)
    {
        var scoutedJob = await _dbContext.ScoutedJobs.FirstOrDefaultAsync(s => s.Id == id);
        if (scoutedJob == null)
            return NotFound(new { success = false, message = "Scouted job not found." });

        if (string.IsNullOrWhiteSpace(scoutedJob.LinkedInUrl))
            return BadRequest(new { success = false, message = "No URL available for this scouted job." });

        scoutedJob.Status = ScoutedJobStatus.Applied;
        await _dbContext.SaveChangesAsync();

        TelegramProgressTracker.ResetProgress(999);
        var url = scoutedJob.LinkedInUrl;
        var scopeFactory = _scopeFactory;
        var logger = _logger;

        // Create a new DI scope so services aren't disposed when the request ends
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var jobProcessingService = scope.ServiceProvider.GetRequiredService<JobProcessingService>();
            try
            {
                await jobProcessingService.ProcessUrlAsync(url, JobSource.Upload, 999);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Web-triggered pipeline failed for scouted job {Id}", id);
                TelegramProgressTracker.UpdateProgress(999, $"Step 2: Pipeline crashed ❌ ({ex.Message})");
            }
        });

        return Ok(new { success = true, redirectUrl = "/Telegram/Progress" });
    }
}
