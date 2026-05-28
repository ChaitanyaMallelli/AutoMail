using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using JobAutomation.Services;

namespace JobAutomation.Controllers;

[Route("api/telegram")]
[ApiController]
public class TelegramWebhookController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelegramWebhookController> _logger;

    public TelegramWebhookController(IServiceScopeFactory scopeFactory, ILogger<TelegramWebhookController> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook()
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();

            _logger.LogInformation("Received Telegram webhook update. Raw Body: {Body}", body);

            if (string.IsNullOrWhiteSpace(body))
            {
                _logger.LogWarning("Telegram webhook update received an empty body");
                return Ok();
            }

            JObject update;
            try
            {
                update = JObject.Parse(body);
            }
            catch (Exception jsonEx)
            {
                _logger.LogError(jsonEx, "Failed to parse Telegram webhook update JSON. Raw body was: {Body}", body);
                return Ok();
            }

            // Process in background so Telegram doesn't timeout
            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                try
                {
                    var telegramService = scope.ServiceProvider.GetRequiredService<TelegramService>();
                    await telegramService.ProcessUpdateAsync(update);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing Telegram update in background");
                }
            });

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error receiving Telegram webhook");
            return Ok(); // Always return 200 to Telegram
        }
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }
}
