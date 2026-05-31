using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using JobAutomation.Data;
using JobAutomation.Services;

namespace JobAutomation.Controllers;

[ApiController]
[Route("track")]
public class TrackingController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TrackingController> _logger;

    public TrackingController(AppDbContext dbContext, IServiceScopeFactory scopeFactory, ILogger<TrackingController> logger)
    {
        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [HttpGet("open/{token:guid}")]
    public async Task<IActionResult> Open(Guid token)
    {
        var email = await _dbContext.GeneratedEmails
            .Include(e => e.JobPost)
            .FirstOrDefaultAsync(e => e.TrackingToken == token);

        if (email != null && email.OpenedAt == null)
        {
            email.OpenedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Email opened: JobPost {JobPostId} - {Company} ({Role})",
                email.JobPostId, email.JobPost?.CompanyName, email.JobPost?.Role);

            var chatId = email.JobPost?.TelegramChatId;
            if (chatId.HasValue && chatId.Value != 999)
            {
                var company = email.JobPost?.CompanyName ?? "Unknown";
                var role = email.JobPost?.Role ?? "Unknown";
                _ = Task.Run(async () =>
                {
                    using var scope = _scopeFactory.CreateScope();
                    var telegram = scope.ServiceProvider.GetRequiredService<TelegramService>();
                    var msg = $"📬 <b>Email Opened!</b>\n\nThe recruiter at <b>{company}</b> just opened your application email for <b>{role}</b>! 🔥\n\nThis is a great sign — be ready to respond.";
                    await telegram.SendReplyAsync(chatId.Value, msg);
                });
            }
        }

        // Return 1x1 transparent GIF
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");
        return File(gif, "image/gif");
    }
}
