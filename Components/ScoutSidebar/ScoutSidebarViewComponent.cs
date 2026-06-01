using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using JobAutomation.Data;

namespace JobAutomation.Components.ScoutSidebar;

public class ScoutSidebarViewComponent : ViewComponent
{
    private readonly AppDbContext _dbContext;

    public ScoutSidebarViewComponent(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var jobs = await _dbContext.ScoutedJobs
            .OrderByDescending(s => s.CreatedAt)
            .Take(8)
            .ToListAsync();

        return View(jobs);
    }
}
