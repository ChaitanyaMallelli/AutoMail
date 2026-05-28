using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using JobAutomation.Services;

namespace JobAutomation.Controllers;

public class TelegramController : Controller
{
    private readonly TelegramService _telegramService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramController> _logger;

    public TelegramController(
        TelegramService telegramService,
        IConfiguration configuration,
        ILogger<TelegramController> logger)
    {
        _telegramService = telegramService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var botToken = _configuration["Telegram:BotToken"] ?? "";
        var webhookUrl = _configuration["Telegram:WebhookUrl"] ?? "";

        ViewBag.BotToken = botToken;
        ViewBag.ConfiguredWebhookUrl = webhookUrl;

        JObject? botInfo = null;
        JObject? webhookInfo = null;

        if (!string.IsNullOrEmpty(botToken))
        {
            botInfo = await _telegramService.GetBotInfoAsync();
            webhookInfo = await _telegramService.GetWebhookInfoAsync();
        }

        ViewBag.BotInfo = botInfo;
        ViewBag.WebhookInfo = webhookInfo;

        return View();
    }

    [HttpGet]
    public IActionResult Progress()
    {
        ViewData["Title"] = "Live Telegram Processing";
        ViewData["Subtitle"] = "Real-time step-by-step automation status";
        return View();
    }

    [HttpGet]
    public IActionResult GetLiveStatus()
    {
        var all = TelegramProgressTracker.AllProgress.Select(p => new
        {
            chatId = p.Key,
            steps = p.Value,
            jobId = TelegramProgressTracker.GetLatestJobId(p.Key)
        }).ToList();

        return Json(all);
    }

    [HttpGet]
    public IActionResult IsExecuting()
    {
        return Json(new { active = TelegramProgressTracker.IsRecentExecutionActive() });
    }

    [HttpPost]
    public async Task<IActionResult> Register(string webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            TempData["Error"] = "Webhook URL cannot be empty.";
            return RedirectToAction("Index");
        }

        webhookUrl = webhookUrl.Trim();
        // Ensure webhookUrl ends with /api/telegram/webhook if it doesn't already
        if (!webhookUrl.EndsWith("/api/telegram/webhook", StringComparison.OrdinalIgnoreCase))
        {
            if (webhookUrl.EndsWith("/"))
            {
                webhookUrl += "api/telegram/webhook";
            }
            else
            {
                webhookUrl += "/api/telegram/webhook";
            }
        }

        if (!webhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Telegram webhooks require a secure HTTPS URL (e.g. https://xxxx.ngrok-free.app/api/telegram/webhook).";
            return RedirectToAction("Index");
        }

        try
        {
            // Register with Telegram
            var success = await _telegramService.SetWebhookAsync(webhookUrl);
            if (success)
            {
                // Persist to appsettings.json
                var path = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
                if (System.IO.File.Exists(path))
                {
                    var json = await System.IO.File.ReadAllTextAsync(path);
                    var config = JObject.Parse(json);
                    if (config["Telegram"] == null)
                    {
                        config["Telegram"] = new JObject();
                    }
                    config["Telegram"]!["WebhookUrl"] = webhookUrl;
                    await System.IO.File.WriteAllTextAsync(path, config.ToString(Newtonsoft.Json.Formatting.Indented));
                }

                TempData["Success"] = "Telegram webhook registered successfully!";
            }
            else
            {
                TempData["Error"] = "Failed to register webhook with Telegram. Please check your Bot Token.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering webhook");
            TempData["Error"] = $"Error registering webhook: {ex.Message}";
        }

        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> Delete()
    {
        try
        {
            var success = await _telegramService.DeleteWebhookAsync();
            if (success)
            {
                TempData["Success"] = "Telegram webhook unregistered successfully.";
            }
            else
            {
                TempData["Error"] = "Failed to unregister webhook with Telegram.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting webhook");
            TempData["Error"] = $"Error deleting webhook: {ex.Message}";
        }

        return RedirectToAction("Index");
    }
}
