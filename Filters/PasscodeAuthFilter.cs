using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JobAutomation.Filters;

public class PasscodeAuthFilter : IAsyncActionFilter
{
    private readonly string _configuredPasscode;

    public PasscodeAuthFilter(IConfiguration configuration)
    {
        _configuredPasscode = configuration["AppPasscode"] ?? "password123";
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? "";

        // Bypass webhook, login endpoints, health checks, or executing checks
        if (path.StartsWith("/api/telegram", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Login", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/track/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Telegram/IsExecuting", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Telegram/GetLiveStatus", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Telegram/Progress", StringComparison.OrdinalIgnoreCase) || // Let tracker load without login if background checks run
            path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase) || 
            path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase) || 
            path.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        // Check passcode cookie
        if (context.HttpContext.Request.Cookies.TryGetValue("AuthPasscode", out var passcode) && passcode == _configuredPasscode)
        {
            await next();
            return;
        }

        // Redirect to Login
        context.Result = new RedirectToActionResult("Index", "Login", null);
    }
}
