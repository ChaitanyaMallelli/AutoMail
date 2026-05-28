using Microsoft.AspNetCore.Mvc;

namespace JobAutomation.Controllers;

public class LoginController : Controller
{
    [HttpGet]
    public IActionResult Index()
    {
        // If already authenticated, go directly to Dashboard
        if (Request.Cookies.TryGetValue("AuthPasscode", out var passcode) && passcode == "9390981596")
        {
            return RedirectToAction("Index", "Dashboard");
        }
        return View();
    }

    [HttpPost]
    public IActionResult Submit(string passcode)
    {
        if (passcode == "9390981596")
        {
            var options = new CookieOptions
            {
                Expires = DateTime.UtcNow.AddDays(7),
                HttpOnly = true,
                Secure = false, // Set to true if running over HTTPS in production
                SameSite = SameSiteMode.Lax,
                Path = "/"
            };
            Response.Cookies.Append("AuthPasscode", "9390981596", options);
            return RedirectToAction("Index", "Dashboard");
        }

        ViewBag.Error = "Incorrect passcode. Please try again.";
        return View("Index");
    }

    [HttpGet]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("AuthPasscode");
        return RedirectToAction("Index");
    }
}
