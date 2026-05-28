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
        var totalJobs = await _dbContext.JobPosts.CountAsync();
        var pendingJobs = await _dbContext.JobPosts.CountAsync(j => j.Status == JobStatus.Pending || j.Status == JobStatus.EmailGenerated);
        var sentJobs = await _dbContext.JobPosts.CountAsync(j => j.Status == JobStatus.Sent);
        var failedJobs = await _dbContext.JobPosts.CountAsync(j => j.Status == JobStatus.Failed);
        var avgMatch = totalJobs > 0
            ? (int)await _dbContext.JobPosts.AverageAsync(j => (double)j.SkillMatchPercentage)
            : 0;

        ViewBag.TotalJobs = totalJobs;
        ViewBag.PendingJobs = pendingJobs;
        ViewBag.SentJobs = sentJobs;
        ViewBag.FailedJobs = failedJobs;
        ViewBag.AvgMatch = avgMatch;
        ViewBag.Search = search;
        ViewBag.Status = status;
        ViewBag.Sort = sort;

        return View(jobPosts);
    }
}
