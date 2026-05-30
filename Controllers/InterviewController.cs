using Microsoft.AspNetCore.Mvc;
using JobAutomation.Data;

namespace JobAutomation.Controllers;

public class InterviewController : Controller
{
    private readonly AppDbContext _dbContext;

    public InterviewController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IActionResult> CoPilot(int id)
    {
        var job = await _dbContext.JobPosts.FindAsync(id);
        if (job == null)
        {
            return NotFound();
        }

        return View(job);
    }
}
