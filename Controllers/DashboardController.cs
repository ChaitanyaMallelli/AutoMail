using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using JobAutomation.Data;
using JobAutomation.Models;

namespace JobAutomation.Controllers;

public class DashboardController : Controller
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(AppDbContext dbContext, ILogger<DashboardController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
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
            query = query.Where(j =>
                j.CompanyName.Contains(search) ||
                j.Role.Contains(search) ||
                (j.Location != null && j.Location.Contains(search)) ||
                (j.RequiredSkills != null && j.RequiredSkills.Contains(search)));
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

        return View(jobPosts);
    }
}
